using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

public sealed class DroneMissionTelemetryRecorder : MonoBehaviour
{
    private const int SummarySchemaVersion = 1;
    private const int EventSchemaVersion = 1;
    private const int MaxPendingOrdinaryEvents = 4096;
    private const int CsvLockTimeoutMilliseconds = 2000;
    private const int CsvLockRetryMilliseconds = 20;
    private const int FingerprintSentinelBytes = 256;
    private const int RecoveryJournalVersion = 4;
    private const int ActiveRecoveryJournalVersion = 3;
    private const int OwnerRecoveryJournalVersion = 2;
    private const int LegacyRecoveryJournalVersion = 1;
    private const float ActiveCheckpointIntervalSeconds = 5f;
    private const double CheckpointHeartbeatFreshSeconds = 30d;
    private const double CheckpointHeartbeatFutureToleranceSeconds = 5d;
    private const int MaxJournalStringBytes = 1024 * 1024;
    private const int MaxJournalRows = MaxPendingOrdinaryEvents + 2;
    private static readonly byte[] RecoveryJournalMagic = Encoding.ASCII.GetBytes("DMTJ");
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private static readonly Encoding CsvEncoding = new UTF8Encoding(false);

    // These exact headers define the on-disk schemas for the versions above.
    private static readonly string[] SummaryHeader =
    {
        "session_id",
        "batch_id",
        "batch_run_index",
        "batch_configuration_index",
        "batch_repeat_index",
        "random_seed",
        "started_utc",
        "ended_utc",
        "end_reason",
        "completed",
        "duration_s",
        "scene_name",
        "planner_type",
        "drone_count",
        "sensor_radius",
        "communication_radius",
        "drone_speed",
        "grid_width",
        "grid_depth",
        "cell_size",
        "target_start_cell_x",
        "target_start_cell_y",
        "target_start_cell_z",
        "target_start_world_x",
        "target_start_world_y",
        "target_start_world_z",
        "nearest_drone_start_distance_cells",
        "nearest_drone_start_distance_world",
        "first_finder_drone_id",
        "first_target_cell_x",
        "first_target_cell_y",
        "first_target_cell_z",
        "time_to_find_s",
        "time_to_command_notified_s",
        "time_to_all_drones_informed_s",
        "time_to_mission_complete_s",
        "command_reporter_id",
        "all_drones_informed_count",
        "target_finder_count",
        "unique_drones_informed_count"
    };

    private static readonly string[] EventHeader =
    {
        "session_id",
        "elapsed_s",
        "utc",
        "event_type",
        "drone_id",
        "other_drone_id",
        "informed_count",
        "cell_x",
        "cell_y",
        "cell_z",
        "value"
    };

    [Header("Output")]
    [SerializeField] private bool writeSummaryCsv = true;
    [SerializeField] private bool writeEventCsv = true;
    [SerializeField] private string outputDirectoryName = "DroneTelemetry";
    [SerializeField] private string summaryFileName = "session_summary.csv";
    [SerializeField] private string eventFileName = "session_events.csv";
    [SerializeField] private bool logOutputPathOnSessionStart = true;

    private sealed class PendingOrdinaryEvent
    {
        public readonly string[] Row;
        public bool AppendAttempted;

        public PendingOrdinaryEvent(string[] row)
        {
            Row = row;
        }
    }

    private readonly HashSet<int> targetFinderDroneIds = new();
    private readonly HashSet<int> informedDroneIds = new();
    private readonly Queue<PendingOrdinaryEvent> pendingOrdinaryEvents = new();

    private DroneMissionSessionConfig config = new();
    private string sessionId = string.Empty;
    private DateTime sessionStartUtc;
    private DateTime sessionEndUtc;
    private float sessionStartTime;
    private float sessionStartRealtime;
    private bool activeSession;
    private bool summaryWritten;
    private bool endRequested;
    private bool endEventWritten;
    private string[] pendingSummaryRow;
    private string[] pendingEndEventRow;
    private bool persistenceFailure;
    private bool ordinaryEventPersistenceFailure;
    private string lastPersistenceError = string.Empty;
    private bool activeCheckpoint;
    private string[] checkpointStartEventRow;
    private float nextActiveCheckpointRealtime;
    // Pair a process nonce with OS identity so PID reuse cannot imply ownership.
    private static string processEpoch = Guid.NewGuid().ToString("N");
    private static readonly ProcessOwnerIdentity CurrentProcessOwner = CreateCurrentProcessOwnerIdentity();
    // Non-null only in deterministic process-boundary tests.
    private static string machineIdentityOverride;
    // Positive=alive start ticks, zero=dead, negative=unsupported.
    private static Func<int, long> processStartUtcTicksProbeOverride;
    private static readonly Dictionary<string, DroneMissionTelemetryRecorder> LiveCheckpointOwners =
        new(StringComparer.Ordinal);
    // Journal ownership is unique even when recorders share output CSVs.
    private string recoveryJournalOwnerId = Guid.NewGuid().ToString("N");

    // Ambiguous writes invalidate the fingerprint and force full validation.
    private sealed class ValidatedCsvCache
    {
        public bool HasValue;
        public string FullPath = string.Empty;
        public long Length;
        public DateTime LastWriteUtc;
        public DateTime CreationUtc;
        public ulong ContentSentinel;
        public string[] LastRecord;
        public readonly Dictionary<string, int> EndReasonCounts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, HashSet<string>>> BatchEndReasonSessionIds =
            new(StringComparer.Ordinal);
    }

    private sealed class DurableFinalRowProof
    {
        public bool HasValue;
        public string FullPath = string.Empty;
        public long Length;
        public DateTime LastWriteUtc;
        public DateTime CreationUtc;
        public ulong ContentSentinel;
    }

    private sealed class CsvFileLock : IDisposable
    {
        private readonly FileStream stream;
        public readonly bool WasContended;

        public CsvFileLock(FileStream stream, bool wasContended)
        {
            this.stream = stream;
            WasContended = wasContended;
        }

        public void Dispose()
        {
            stream.Dispose();
        }
    }

    private enum FinalRowPresence
    {
        Present,
        Absent,
        Failed
    }

    private enum CheckpointOwnerLiveness
    {
        Live,
        Dead,
        Ambiguous
    }

    private enum ProcessProbeResult
    {
        Alive,
        Dead,
        Unsupported
    }

    private sealed class ProcessOwnerIdentity
    {
        public string MachineIdentity = string.Empty;
        public int ProcessId;
        public long ProcessStartUtcTicks;
        public string ProcessNonce = string.Empty;
    }

    private sealed class RecoveryJournal
    {
        public string OwnerId;
        public string EventPath;
        public string SummaryPath;
        public bool EventEnabled;
        public bool SummaryEnabled;
        public readonly List<string[]> OrdinaryRows = new();
        public string[] EndEventRow;
        public string[] SummaryRow;
        public bool ActiveCheckpoint;
        public string ProcessEpoch;
        public string MachineIdentity;
        public int ProcessId;
        public long ProcessStartUtcTicks;
        public string ProcessNonce;
        public long HeartbeatUtcTicks;
        public string[] StartEventRow;
        public string[] InterruptedEndEventRow;
        public string[] InterruptedSummaryRow;
    }

    private sealed class RecoveryJournalArtifactCandidate
    {
        public string Path;
        public DateTime LastWriteUtc;
        public int TieBreakPriority;
    }

    private readonly ValidatedCsvCache summaryFileCache = new();
    private readonly ValidatedCsvCache eventFileCache = new();
    private readonly DurableFinalRowProof summaryRowProof = new();
    private readonly DurableFinalRowProof endEventRowProof = new();
    private int fullCsvValidationCount;

    private bool hasFirstTargetFound;
    private bool hasCommandNotified;
    private bool hasAllDronesInformed;
    private bool hasMissionComplete;

    private float firstTargetFoundElapsed;
    private float commandNotifiedElapsed;
    private float allDronesInformedElapsed;
    private float missionCompleteElapsed;

    private int firstFinderDroneId = -1;
    private int commandReporterId = -1;
    private int allDronesInformedCount;
    private DroneNative.DroneVec3i firstTargetCell;

    public bool HasActiveSession => activeSession;
    public bool IsSummaryCsvEnabled => writeSummaryCsv;
    public bool HasPendingSessionEnd => activeSession && endRequested;
    public bool HasPersistenceFailure => persistenceFailure;
    public string LastPersistenceError => lastPersistenceError;
    public string ActiveSessionId => activeSession ? sessionId : string.Empty;
    public float ActiveSessionStartRealtime => activeSession ? sessionStartRealtime : float.PositiveInfinity;
    public float ElapsedSeconds => activeSession ? Mathf.Max(0f, Time.time - sessionStartTime) : 0f;
    public string OutputDirectory => Path.Combine(Application.persistentDataPath, outputDirectoryName);
    public string SummaryFilePath => Path.Combine(OutputDirectory, summaryFileName);
    private string RecoveryDirectory => Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
    private string LegacyRecoveryJournalPath => Path.Combine(RecoveryDirectory, "pending_rows.journal");
    private string RecoveryJournalPath => Path.Combine(
        RecoveryDirectory, $"pending_rows.{recoveryJournalOwnerId}.journal");

    /// <summary>Counts summary rows matching an end reason, or zero if no CSV exists.</summary>
    public int CountSessionsWithEndReason(string endReason)
    {
        TryCountSessionsWithEndReason(endReason, out int count);
        return count;
    }

    public bool TryCountSessionsWithEndReason(string endReason, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(endReason))
        {
            return true;
        }

        return TryReadSummaryCount(
            cache => cache.EndReasonCounts.TryGetValue(endReason, out int value) ? value : 0,
            out count);
    }

    /// <summary>Counts unique sessions matching an end reason and batch ID.</summary>
    public int CountUniqueSessionsWithEndReasonForBatch(string endReason, string batchId)
    {
        TryCountUniqueSessionsWithEndReasonForBatch(endReason, batchId, out int count);
        return count;
    }

    public bool TryCountUniqueSessionsWithEndReasonForBatch(
        string endReason,
        string batchId,
        out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(endReason))
        {
            return true;
        }

        string requestedBatchId = batchId ?? string.Empty;
        return TryReadSummaryCount(cache =>
        {
            if (!cache.BatchEndReasonSessionIds.TryGetValue(requestedBatchId, out var reasonSessions)
                || !reasonSessions.TryGetValue(endReason, out HashSet<string> sessionIds))
            {
                return 0;
            }

            return sessionIds.Count;
        }, out count);
    }

    private bool TryReadSummaryCount(Func<ValidatedCsvCache, int> selectCount, out int count)
    {
        count = 0;
        string path = SummaryFilePath;
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            using (CsvFileLock fileLock = AcquireCsvFileLock(path))
            {
                if (fileLock.WasContended)
                {
                    InvalidateCache(summaryFileCache);
                }

                if (!File.Exists(path))
                {
                    InvalidateCache(summaryFileCache);
                    return true;
                }

                if (!IsCacheCurrent(summaryFileCache, path))
                {
                    ValidateAndCacheCsvFile(path, SummaryHeader, SummarySchemaVersion, summaryFileCache);
                }

                count = selectCount(summaryFileCache);
                return true;
            }
        }
        catch (Exception exception)
        {
            InvalidateCache(summaryFileCache);
            ReportPersistenceFailure($"Failed to validate/read summary CSV at {path}: {exception.Message}");
            count = 0;
            return false;
        }
    }

    private void OnValidate()
    {
        if (string.IsNullOrWhiteSpace(outputDirectoryName))
        {
            outputDirectoryName = "DroneTelemetry";
        }

        if (string.IsNullOrWhiteSpace(summaryFileName))
        {
            summaryFileName = "session_summary.csv";
        }

        if (string.IsNullOrWhiteSpace(eventFileName))
        {
            eventFileName = "session_events.csv";
        }
    }

    /// <summary>Clears persistence failure only after every output passes validation.</summary>
    public bool TryRevalidateStorage()
    {
        if (activeSession)
        {
            Debug.LogError(
                "[DroneTelemetry] Cannot revalidate storage while a telemetry session is active. Persist or retry its end first.",
                this);
            return false;
        }

        // Keep recovery independent of output configuration changes.
        if (!TryReplayRecoveryJournal() || !EnsureOutputFiles())
        {
            return false;
        }

        persistenceFailure = false;
        ordinaryEventPersistenceFailure = false;
        lastPersistenceError = string.Empty;
        return true;
    }

    public bool BeginSession(DroneMissionSessionConfig sessionConfig)
    {
        return TryBeginSession(sessionConfig);
    }

    /// <summary>Activates a session only after its session_start row persists.</summary>
    public bool TryBeginSession(DroneMissionSessionConfig sessionConfig)
    {
        if (activeSession && !TryEndSession("superseded", false))
        {
            Debug.LogError("[DroneTelemetry] Cannot start a new session while the previous session has unpersisted data.", this);
            return false;
        }

        if (!TryRevalidateStorage())
        {
            return false;
        }

        config = sessionConfig ?? new DroneMissionSessionConfig();
        sessionId = GenerateSessionId();
        // Revalidation drained older transactions; this session gets a new owner ID.
        recoveryJournalOwnerId = Guid.NewGuid().ToString("N");
        sessionStartUtc = DateTime.UtcNow;
        sessionStartTime = Time.time;
        activeSession = false;
        summaryWritten = false;
        endRequested = false;
        endEventWritten = false;
        pendingSummaryRow = null;
        pendingEndEventRow = null;
        ClearFinalRowProof(summaryRowProof);
        ClearFinalRowProof(endEventRowProof);
        pendingOrdinaryEvents.Clear();

        targetFinderDroneIds.Clear();
        informedDroneIds.Clear();
        hasFirstTargetFound = false;
        hasCommandNotified = false;
        hasAllDronesInformed = false;
        hasMissionComplete = false;
        firstTargetFoundElapsed = 0f;
        commandNotifiedElapsed = 0f;
        allDronesInformedElapsed = 0f;
        missionCompleteElapsed = 0f;
        firstFinderDroneId = -1;
        commandReporterId = -1;
        allDronesInformedCount = 0;
        firstTargetCell = default;

        checkpointStartEventRow = writeEventCsv
            ? BuildEventRow(
                "session_start", -1, -1, 0, false, default, config.ToEventValue(), 0f, sessionStartUtc)
            : null;
        activeCheckpoint = writeEventCsv || writeSummaryCsv;
        nextActiveCheckpointRealtime = Time.realtimeSinceStartup + ActiveCheckpointIntervalSeconds;
        if (activeCheckpoint)
        {
            LiveCheckpointOwners[recoveryJournalOwnerId] = this;
            // Checkpoint the start identity before appending session_start.
            if (!PersistRecoveryJournalSnapshot())
            {
                LiveCheckpointOwners.Remove(recoveryJournalOwnerId);
                return false;
            }
        }

        if (writeEventCsv && !AppendCsvRow(EventPath, EventHeader, checkpointStartEventRow, true))
        {
            LiveCheckpointOwners.Remove(recoveryJournalOwnerId);
            return false;
        }

        sessionStartRealtime = Time.realtimeSinceStartup;
        activeSession = true;
        if (logOutputPathOnSessionStart)
        {
            Debug.Log($"[DroneTelemetry] Session {sessionId} started. CSV output: {OutputDirectory}", this);
        }

        return true;
    }

    public void RecordTargetFound(int droneId, DroneNative.DroneVec3i cell)
    {
        if (!activeSession || endRequested)
        {
            return;
        }

        if ((droneId > 0 && targetFinderDroneIds.Contains(droneId)) ||
            (droneId <= 0 && hasFirstTargetFound))
        {
            return;
        }

        // Reserve both finder events before mutating deduplication state.
        int requiredSlots = droneId > 0 && !informedDroneIds.Contains(droneId) ? 2 : 1;
        if (!CanAcceptOrdinaryEvents(requiredSlots))
        {
            return;
        }

        if (droneId > 0)
        {
            targetFinderDroneIds.Add(droneId);
        }

        if (!hasFirstTargetFound)
        {
            hasFirstTargetFound = true;
            firstTargetFoundElapsed = ElapsedSeconds;
            firstFinderDroneId = droneId;
            firstTargetCell = cell;
        }

        AppendEvent("target_found", droneId, -1, targetFinderDroneIds.Count, true, cell, string.Empty);

        if (droneId > 0)
        {
            RecordDroneInformed(droneId, cell, Mathf.Max(1, targetFinderDroneIds.Count));
        }
    }

    public void RecordCommandNotified(int reporterId, DroneNative.DroneVec3i cell, int informedCount)
    {
        if (!activeSession || endRequested || hasCommandNotified || !CanAcceptOrdinaryEvents(1))
        {
            return;
        }

        hasCommandNotified = true;
        commandNotifiedElapsed = ElapsedSeconds;
        commandReporterId = reporterId;
        AppendEvent("command_notified", 0, reporterId, Mathf.Max(0, informedCount), true, cell, string.Empty);
    }

    public void RecordDroneInformed(int droneId, DroneNative.DroneVec3i cell, int informedCount)
    {
        if (!activeSession || endRequested || droneId <= 0 || informedDroneIds.Contains(droneId) ||
            !CanAcceptOrdinaryEvents(1))
        {
            return;
        }

        informedDroneIds.Add(droneId);
        AppendEvent("drone_informed", droneId, -1, Mathf.Max(0, informedCount), true, cell, string.Empty);
    }

    public void RecordAllDronesInformed(int informedCount, DroneNative.DroneVec3i cell)
    {
        if (!activeSession || endRequested || hasAllDronesInformed || !CanAcceptOrdinaryEvents(1))
        {
            return;
        }

        hasAllDronesInformed = true;
        allDronesInformedElapsed = ElapsedSeconds;
        allDronesInformedCount = Mathf.Max(0, informedCount);
        AppendEvent("all_drones_informed", -1, -1, allDronesInformedCount, true, cell, string.Empty);
    }

    public void RecordMissionComplete(int informedCount, DroneNative.DroneVec3i cell)
    {
        if (!activeSession || endRequested || hasMissionComplete || !CanAcceptOrdinaryEvents(1))
        {
            return;
        }

        hasMissionComplete = true;
        missionCompleteElapsed = ElapsedSeconds;
        AppendEvent("mission_complete", -1, -1, Mathf.Max(0, informedCount), true, cell, string.Empty);
    }

    public void EndSession(string endReason, bool completed)
    {
        TryEndSession(endReason, completed);
    }

    /// <summary>Keeps the original end data retryable until final rows persist.</summary>
    public bool TryEndSession(string endReason, bool completed)
    {
        if (!activeSession)
        {
            return true;
        }

        if (!endRequested)
        {
            endRequested = true;
            sessionEndUtc = DateTime.UtcNow;
            string safeReason = string.IsNullOrWhiteSpace(endReason) ? "ended" : endReason;
            float durationSeconds = ElapsedSeconds;
            pendingSummaryRow = BuildSummaryRow(safeReason, completed, durationSeconds);
            pendingEndEventRow = BuildEventRow(
                "session_end", -1, -1, allDronesInformedCount, false, default, safeReason,
                durationSeconds, sessionEndUtc);
        }

        // Snapshot exact destinations before offering pending rows to a CSV.
        if (!PersistRecoveryJournalSnapshot())
        {
            return false;
        }

        // Ordinary rows must persist before final boundary rows.
        if (!FlushPendingOrdinaryEvents())
        {
            return false;
        }

        bool finalRowsPresent = true;
        if (writeEventCsv)
        {
            finalRowsPresent &= ReconcileFrozenFinalRow(
                EventPath, EventHeader, pendingEndEventRow, ref endEventWritten, endEventRowProof);
        }

        if (writeSummaryCsv)
        {
            finalRowsPresent &= ReconcileFrozenFinalRow(
                SummaryPath, SummaryHeader, pendingSummaryRow, ref summaryWritten, summaryRowProof);
        }

        if (!finalRowsPresent)
        {
            return false;
        }

        // Prove each frozen row against the current files after partial failures.
        if (!EnsureOutputFiles())
        {
            return false;
        }

        finalRowsPresent = true;
        if (writeEventCsv)
        {
            finalRowsPresent &= ReconcileFrozenFinalRow(
                EventPath, EventHeader, pendingEndEventRow, ref endEventWritten, endEventRowProof);
        }

        if (writeSummaryCsv)
        {
            finalRowsPresent &= ReconcileFrozenFinalRow(
                SummaryPath, SummaryHeader, pendingSummaryRow, ref summaryWritten, summaryRowProof);
        }

        if (!finalRowsPresent)
        {
            return false;
        }

        // Retire only after every pending row is current and durable.
        if (!DeleteRecoveryJournal())
        {
            // Retirement is incomplete until the committed journal is removed.
            return false;
        }
        activeSession = false;
        activeCheckpoint = false;
        checkpointStartEventRow = null;
        LiveCheckpointOwners.Remove(recoveryJournalOwnerId);
        persistenceFailure = false;
        ordinaryEventPersistenceFailure = false;
        lastPersistenceError = string.Empty;
        pendingSummaryRow = null;
        pendingEndEventRow = null;
        ClearFinalRowProof(summaryRowProof);
        ClearFinalRowProof(endEventRowProof);
        return true;
    }

    private bool ReconcileFrozenFinalRow(
        string path,
        string[] header,
        string[] row,
        ref bool written,
        DurableFinalRowProof proof)
    {
        if (written)
        {
            FinalRowPresence presence = VerifyFrozenFinalRow(path, header, row, proof);
            if (presence == FinalRowPresence.Present)
            {
                return true;
            }

            if (presence == FinalRowPresence.Failed)
            {
                return false;
            }

            written = false;
            ClearFinalRowProof(proof);
        }

        if (!AppendCsvRow(path, header, row, true, false, proof))
        {
            return false;
        }

        written = true;
        return true;
    }

    private FinalRowPresence VerifyFrozenFinalRow(
        string path,
        string[] header,
        string[] row,
        DurableFinalRowProof proof)
    {
        ValidatedCsvCache cache = GetCache(header);
        int schemaVersion = ReferenceEquals(header, SummaryHeader) ? SummarySchemaVersion : EventSchemaVersion;
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            using (CsvFileLock fileLock = AcquireCsvFileLock(path))
            {
                if (fileLock.WasContended)
                {
                    InvalidateCache(cache);
                }

                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    InvalidateCache(cache);
                    return FinalRowPresence.Absent;
                }

                if (cache.HasValue && IsCacheCurrent(cache, path) && ProofMatchesCurrentFile(proof, path))
                {
                    return FinalRowPresence.Present;
                }

                bool found = ValidateAndCacheCsvFile(path, header, schemaVersion, cache, row);
                if (!found)
                {
                    return FinalRowPresence.Absent;
                }

                CaptureFinalRowProof(proof, cache);
                return FinalRowPresence.Present;
            }
        }
        catch (Exception exception)
        {
            InvalidateCache(cache);
            ReportPersistenceFailure(
                $"Failed to verify frozen schema v{schemaVersion} CSV row at {path}: {exception.Message}");
            return FinalRowPresence.Failed;
        }
    }

    private static bool ProofMatchesCurrentFile(DurableFinalRowProof proof, string path)
    {
        if (!proof.HasValue ||
            !string.Equals(proof.FullPath, Path.GetFullPath(path), StringComparison.Ordinal))
        {
            return false;
        }

        var info = new FileInfo(path);
        info.Refresh();
        return info.Exists && info.Length == proof.Length &&
               info.LastWriteTimeUtc == proof.LastWriteUtc &&
               info.CreationTimeUtc == proof.CreationUtc &&
               ComputeContentSentinel(path, info.Length) == proof.ContentSentinel;
    }

    private static void CaptureFinalRowProof(DurableFinalRowProof proof, ValidatedCsvCache cache)
    {
        proof.FullPath = cache.FullPath;
        proof.Length = cache.Length;
        proof.LastWriteUtc = cache.LastWriteUtc;
        proof.CreationUtc = cache.CreationUtc;
        proof.ContentSentinel = cache.ContentSentinel;
        proof.HasValue = true;
    }

    private static void ClearFinalRowProof(DurableFinalRowProof proof)
    {
        proof.HasValue = false;
        proof.FullPath = string.Empty;
    }

    private string[] BuildSummaryRow(string endReason, bool completed, float durationSeconds)
    {
        return new[]
        {
            sessionId,
            config.BatchId,
            IntOrBlank(config.HasBatchRunIndex, config.BatchRunIndex),
            IntOrBlank(config.HasBatchConfigurationIndex, config.BatchConfigurationIndex),
            IntOrBlank(config.HasBatchRepeatIndex, config.BatchRepeatIndex),
            IntOrBlank(config.HasRandomSeed, config.RandomSeed),
            FormatUtc(sessionStartUtc),
            FormatUtc(sessionEndUtc),
            endReason,
            completed ? "true" : "false",
            FormatFloat(durationSeconds),
            config.SceneName,
            config.PlannerType,
            FormatInt(config.DroneCount),
            FormatInt(config.SensorRadius),
            FormatFloat(config.CommunicationRadius),
            FormatFloat(config.DroneSpeed),
            FormatInt(config.GridWidth),
            FormatInt(config.GridDepth),
            FormatFloat(config.CellSize),
            IntOrBlank(config.HasTargetStartCell, config.TargetStartCell.x),
            IntOrBlank(config.HasTargetStartCell, config.TargetStartCell.y),
            IntOrBlank(config.HasTargetStartCell, config.TargetStartCell.z),
            FloatOrBlank(config.HasTargetStartWorldPosition, config.TargetStartWorldPosition.x),
            FloatOrBlank(config.HasTargetStartWorldPosition, config.TargetStartWorldPosition.y),
            FloatOrBlank(config.HasTargetStartWorldPosition, config.TargetStartWorldPosition.z),
            FloatOrBlank(config.HasNearestDroneStartDistance, config.NearestDroneStartDistanceCells),
            FloatOrBlank(config.HasNearestDroneStartDistance, config.NearestDroneStartDistanceWorld),
            IntOrBlank(hasFirstTargetFound, firstFinderDroneId),
            IntOrBlank(hasFirstTargetFound, firstTargetCell.x),
            IntOrBlank(hasFirstTargetFound, firstTargetCell.y),
            IntOrBlank(hasFirstTargetFound, firstTargetCell.z),
            FloatOrBlank(hasFirstTargetFound, firstTargetFoundElapsed),
            FloatOrBlank(hasCommandNotified, commandNotifiedElapsed),
            FloatOrBlank(hasAllDronesInformed, allDronesInformedElapsed),
            FloatOrBlank(hasMissionComplete, missionCompleteElapsed),
            IntOrBlank(hasCommandNotified, commandReporterId),
            IntOrBlank(hasAllDronesInformed, allDronesInformedCount),
            FormatInt(targetFinderDroneIds.Count),
            FormatInt(informedDroneIds.Count)
        };
    }

    private void Update()
    {
        if (!activeSession || endRequested || !activeCheckpoint ||
            Time.realtimeSinceStartup < nextActiveCheckpointRealtime)
        {
            return;
        }

        // Interval checkpoints bound elapsed-time loss without writing every frame.
        nextActiveCheckpointRealtime = Time.realtimeSinceStartup + ActiveCheckpointIntervalSeconds;
        PersistRecoveryJournalSnapshot();
    }

    private bool EnsureOutputFiles()
    {
        bool succeeded = true;
        if (writeSummaryCsv)
        {
            succeeded &= EnsureCsvHeader(SummaryPath, SummaryHeader, SummarySchemaVersion);
        }

        if (writeEventCsv)
        {
            succeeded &= EnsureCsvHeader(EventPath, EventHeader, EventSchemaVersion);
        }

        return succeeded;
    }

    private void AppendEvent(
        string eventType,
        int droneId,
        int otherDroneId,
        int informedCount,
        bool hasCell,
        DroneNative.DroneVec3i cell,
        string value)
    {
        if (!writeEventCsv)
        {
            // Summary-only sessions checkpoint each aggregate transition.
            PersistRecoveryJournalSnapshot();
            return;
        }

        pendingOrdinaryEvents.Enqueue(new PendingOrdinaryEvent(
            BuildEventRow(eventType, droneId, otherDroneId, informedCount, hasCell, cell, value, ElapsedSeconds, DateTime.UtcNow)));
        FlushPendingOrdinaryEvents();
    }

    private bool CanAcceptOrdinaryEvents(int count)
    {
        if (!writeEventCsv)
        {
            return true;
        }

        FlushPendingOrdinaryEvents();
        if (pendingOrdinaryEvents.Count <= MaxPendingOrdinaryEvents - count)
        {
            return true;
        }

        ReportPersistenceFailure(
            $"Pending ordinary-event queue reached its {MaxPendingOrdinaryEvents}-row limit; event state was not mutated.",
            true);
        return false;
    }

    private bool FlushPendingOrdinaryEvents()
    {
        if (!writeEventCsv)
        {
            pendingOrdinaryEvents.Clear();
            return true;
        }

        while (pendingOrdinaryEvents.Count > 0)
        {
            if (!PersistRecoveryJournalSnapshot())
            {
                return false;
            }

            PendingOrdinaryEvent pending = pendingOrdinaryEvents.Peek();
            // Another process may have replayed this journal row; dedupe by row identity.
            pending.AppendAttempted = true;
            if (!AppendCsvRow(EventPath, EventHeader, pending.Row, true, true))
            {
                return false;
            }

            pendingOrdinaryEvents.Dequeue();
            if (!PersistRecoveryJournalSnapshot())
            {
                return false;
            }
        }

        if (ordinaryEventPersistenceFailure)
        {
            ordinaryEventPersistenceFailure = false;
            persistenceFailure = false;
            lastPersistenceError = string.Empty;
        }

        return true;
    }

    private string[] BuildEventRow(
        string eventType,
        int droneId,
        int otherDroneId,
        int informedCount,
        bool hasCell,
        DroneNative.DroneVec3i cell,
        string value,
        float elapsedSeconds,
        DateTime utc)
    {
        return new[]
        {
            sessionId,
            FormatFloat(elapsedSeconds),
            FormatUtc(utc),
            eventType,
            IntOrBlank(droneId >= 0, droneId),
            IntOrBlank(otherDroneId >= 0, otherDroneId),
            IntOrBlank(informedCount > 0, informedCount),
            IntOrBlank(hasCell, cell.x),
            IntOrBlank(hasCell, cell.y),
            IntOrBlank(hasCell, cell.z),
            value ?? string.Empty
        };
    }

    private bool EnsureCsvHeader(string path, string[] header, int schemaVersion)
    {
        ValidatedCsvCache cache = GetCache(header);
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            using (CsvFileLock fileLock = AcquireCsvFileLock(path))
            {
                if (fileLock.WasContended)
                {
                    InvalidateCache(cache);
                }

                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    if (!IsCacheCurrent(cache, path))
                    {
                        ValidateAndCacheCsvFile(path, header, schemaVersion, cache);
                    }

                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        stream.Flush();
                    }

                    return true;
                }

                InvalidateCache(cache);
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, CsvEncoding))
                {
                    writer.WriteLine(ToCsvLine(header));
                    writer.Flush();
                    stream.Flush(true);
                }

                CaptureFingerprint(cache, path, null);
                return true;
            }
        }
        catch (Exception exception)
        {
            InvalidateCache(cache);
            // Never rotate an unknown user file.
            ReportPersistenceFailure($"Failed to validate/create schema v{schemaVersion} CSV at {path}: {exception.Message}");
            return false;
        }
    }

    private bool AppendCsvRow(
        string path,
        string[] header,
        string[] values,
        bool avoidDuplicate,
        bool ordinaryEvent = false,
        DurableFinalRowProof durableProof = null)
    {
        ValidateRecord(path, values, header.Length);
        string row = ToCsvLine(values);
        ValidatedCsvCache cache = GetCache(header);
        int schemaVersion = ReferenceEquals(header, SummaryHeader) ? SummarySchemaVersion : EventSchemaVersion;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (CsvFileLock fileLock = AcquireCsvFileLock(path))
            {
                bool cacheWasCurrent = !fileLock.WasContended && IsCacheCurrent(cache, path);
                if (!cacheWasCurrent)
                {
                    InvalidateCache(cache);
                }

                bool needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
                bool matchingIdentityFound = false;
                if (!needsHeader && !cacheWasCurrent)
                {
                    // Ambiguous retries require a full locked scan, not a tail check.
                    matchingIdentityFound = ValidateAndCacheCsvFile(
                        path, header, schemaVersion, cache, avoidDuplicate ? values : null);
                }

                if (avoidDuplicate && !needsHeader &&
                    (matchingIdentityFound || RowsHaveSameIdentity(header, cache.LastRecord, values)))
                {
                    if (durableProof != null)
                    {
                        CaptureFinalRowProof(durableProof, cache);
                    }
                    return true;
                }

                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, CsvEncoding))
                {
                    if (needsHeader)
                    {
                        writer.WriteLine(ToCsvLine(header));
                    }

                    writer.WriteLine(row);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (ReferenceEquals(header, SummaryHeader))
                {
                    IndexSummaryRecord(cache.EndReasonCounts, cache.BatchEndReasonSessionIds, values);
                }
                CaptureFingerprint(cache, path, values);
                if (durableProof != null)
                {
                    CaptureFinalRowProof(durableProof, cache);
                }
                return true;
            }
        }
        catch (Exception exception)
        {
            // Ambiguous close or lock failures force full validation on retry.
            InvalidateCache(cache);
            ReportPersistenceFailure(
                $"Failed to append schema v{schemaVersion} CSV row at {path}: {exception.Message}",
                ordinaryEvent);
            return false;
        }
    }

    private ValidatedCsvCache GetCache(string[] header)
    {
        return ReferenceEquals(header, SummaryHeader) ? summaryFileCache : eventFileCache;
    }

    private bool PersistRecoveryJournalSnapshot()
    {
        EnsureCurrentMachineIdentity();
        string[] interruptedEnd = null;
        string[] interruptedSummary = null;
        if (activeCheckpoint && !endRequested)
        {
            DateTime checkpointUtc = DateTime.UtcNow;
            float checkpointDuration = activeSession ? ElapsedSeconds : 0f;
            sessionEndUtc = checkpointUtc;
            if (writeEventCsv)
            {
                interruptedEnd = BuildEventRow(
                    "session_end", -1, -1, allDronesInformedCount, false, default,
                    "process_interrupted", checkpointDuration, checkpointUtc);
            }
            if (writeSummaryCsv)
            {
                interruptedSummary = BuildSummaryRow("process_interrupted", false, checkpointDuration);
            }
        }

        var journal = new RecoveryJournal
        {
            OwnerId = recoveryJournalOwnerId,
            EventPath = Path.GetFullPath(EventPath),
            SummaryPath = Path.GetFullPath(SummaryPath),
            EventEnabled = writeEventCsv,
            SummaryEnabled = writeSummaryCsv,
            EndEventRow = writeEventCsv && endRequested ? pendingEndEventRow : null,
            SummaryRow = writeSummaryCsv && endRequested ? pendingSummaryRow : null,
            ActiveCheckpoint = activeCheckpoint && !endRequested,
            ProcessEpoch = processEpoch,
            MachineIdentity = CurrentProcessOwner.MachineIdentity,
            ProcessId = CurrentProcessOwner.ProcessId,
            ProcessStartUtcTicks = CurrentProcessOwner.ProcessStartUtcTicks,
            ProcessNonce = CurrentProcessOwner.ProcessNonce,
            HeartbeatUtcTicks = DateTime.UtcNow.Ticks,
            StartEventRow = checkpointStartEventRow,
            InterruptedEndEventRow = interruptedEnd,
            InterruptedSummaryRow = interruptedSummary
        };
        foreach (PendingOrdinaryEvent pending in pendingOrdinaryEvents)
        {
            journal.OrdinaryRows.Add(pending.Row);
        }

        if (journal.OrdinaryRows.Count == 0 && journal.EndEventRow == null &&
            journal.SummaryRow == null && !journal.ActiveCheckpoint)
        {
            return DeleteRecoveryJournal();
        }

        string journalPath = RecoveryJournalPath;
        try
        {
            Directory.CreateDirectory(RecoveryDirectory);
            using (AcquireCsvFileLock(journalPath))
            {
                foreach (string artifact in JournalArtifacts(journalPath))
                {
                    if (!File.Exists(artifact)) continue;
                    RecoveryJournal existing = ReadRecoveryJournal(artifact);
                    ValidateJournalOwner(journalPath, existing);
                    if (!JournalDestinationsEqual(existing, journal))
                    {
                        throw new InvalidOperationException("Existing owner journal names different destinations.");
                    }
                }
                WriteRecoveryJournalAtomically(journalPath, journal);
            }
            return true;
        }
        catch (Exception exception)
        {
            ReportPersistenceFailure($"Failed to durably write telemetry recovery journal at {journalPath}: {exception.Message}");
            return false;
        }
    }

    private bool TryReplayRecoveryJournal()
    {
        List<string> journalPaths;
        try
        {
            Directory.CreateDirectory(RecoveryDirectory);
            journalPaths = new List<string>(EnumerateRecoveryJournalPaths());
        }
        catch (Exception exception)
        {
            ReportPersistenceFailure($"Failed to enumerate telemetry recovery journals: {exception.Message}");
            return false;
        }

        bool succeeded = true;
        foreach (string journalPath in journalPaths)
        {
            try
            {
                using (AcquireCsvFileLock(journalPath))
                {
                    RecoveryJournal journal = LoadNewestJournalArtifact(journalPath);
                    if (journal == null) continue; // Another replayer already retired it.
                    ValidateJournalOwner(journalPath, journal);

                    // Replay only to the destinations captured by the journal.
                    ValidateRecoveryDestination(journal.EventPath, "event");
                    ValidateRecoveryDestination(journal.SummaryPath, "summary");

                    if (journal.ActiveCheckpoint)
                    {
                        CheckpointOwnerLiveness liveness = ClassifyCheckpointOwner(journal, DateTime.UtcNow);
                        if (liveness == CheckpointOwnerLiveness.Live)
                        {
                            // The owner lock prevents heartbeat replacement during this decision.
                            continue;
                        }
                        if (liveness == CheckpointOwnerLiveness.Ambiguous)
                        {
                            // Ambiguous liveness must preserve all recovery artifacts.
                            succeeded = false;
                            Debug.LogWarning(
                                $"[DroneTelemetry] Deferring ambiguous active checkpoint owner at {journalPath}; " +
                                "recovery evidence was preserved.",
                                this);
                            continue;
                        }
                    }

                    if (!ReplayRecoveryJournal(journal))
                    {
                        succeeded = false;
                        continue;
                    }

                    // Delete only while this owner's journal and CSV locks remain held.
                    DeleteRecoveryJournalArtifacts(journalPath);
                }
            }
            catch (Exception exception)
            {
                // Unsafe durable evidence blocks readiness but not checks of other owners.
                succeeded = false;
                Debug.LogWarning(
                    $"[DroneTelemetry] Preserving unreadable recovery journal at {journalPath}: {exception.Message}",
                    this);
            }
        }
        return succeeded;
    }

    private static CheckpointOwnerLiveness ClassifyCheckpointOwner(RecoveryJournal journal, DateTime nowUtc)
    {
        EnsureCurrentMachineIdentity();
        // A same-process nonce is live only while its recorder is registered.
        if (string.Equals(journal.ProcessNonce, CurrentProcessOwner.ProcessNonce, StringComparison.Ordinal))
        {
            return LiveCheckpointOwners.TryGetValue(
                       journal.OwnerId, out DroneMissionTelemetryRecorder liveOwner) &&
                   liveOwner != null && liveOwner.activeSession
                ? CheckpointOwnerLiveness.Live
                : CheckpointOwnerLiveness.Dead;
        }

        // Legacy journals lack enough evidence to prove their owner dead.
        if (string.IsNullOrEmpty(journal.MachineIdentity) ||
            string.IsNullOrEmpty(CurrentProcessOwner.MachineIdentity) ||
            !string.Equals(journal.MachineIdentity, CurrentProcessOwner.MachineIdentity, StringComparison.Ordinal))
        {
            return CheckpointOwnerLiveness.Ambiguous;
        }

        if (journal.ProcessId <= 0 || journal.ProcessStartUtcTicks <= 0)
        {
            return CheckpointOwnerLiveness.Ambiguous;
        }

        ProcessProbeResult probe = ProbeProcessStartUtcTicks(journal.ProcessId, out long observedStartUtcTicks);
        if (probe == ProcessProbeResult.Dead)
        {
            return CheckpointOwnerLiveness.Dead;
        }
        if (probe != ProcessProbeResult.Alive)
        {
            return CheckpointOwnerLiveness.Ambiguous;
        }

        // A mismatched start instant proves PID reuse.
        if (observedStartUtcTicks != journal.ProcessStartUtcTicks)
        {
            return CheckpointOwnerLiveness.Dead;
        }

        if (journal.HeartbeatUtcTicks <= 0)
        {
            return CheckpointOwnerLiveness.Ambiguous;
        }
        double heartbeatAgeSeconds = (nowUtc.Ticks - journal.HeartbeatUtcTicks) / (double)TimeSpan.TicksPerSecond;
        if (heartbeatAgeSeconds < -CheckpointHeartbeatFutureToleranceSeconds ||
            heartbeatAgeSeconds > CheckpointHeartbeatFreshSeconds)
        {
            // A stale matching process is not proven dead.
            return CheckpointOwnerLiveness.Ambiguous;
        }

        return CheckpointOwnerLiveness.Live;
    }

    private static ProcessOwnerIdentity CreateCurrentProcessOwnerIdentity()
    {
        var identity = new ProcessOwnerIdentity { ProcessNonce = Guid.NewGuid().ToString("N") };
        try
        {
            using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
            {
                identity.ProcessId = process.Id;
                identity.ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            }
        }
        catch
        {
            identity.ProcessId = 0;
            identity.ProcessStartUtcTicks = 0;
        }
        return identity;
    }

    private static void EnsureCurrentMachineIdentity()
    {
        string rawIdentity = machineIdentityOverride;
        if (string.IsNullOrEmpty(rawIdentity) && !string.IsNullOrEmpty(CurrentProcessOwner.MachineIdentity)) return;
        if (string.IsNullOrEmpty(rawIdentity))
        {
            try
            {
                string candidate = SystemInfo.deviceUniqueIdentifier;
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    !string.Equals(candidate, "n/a", StringComparison.OrdinalIgnoreCase) &&
                    candidate.IndexOf("unsupported", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    rawIdentity = candidate;
                }
            }
            catch
            {
                // This Unity/platform API is optional. Empty means ambiguous.
            }
        }
        if (string.IsNullOrEmpty(rawIdentity)) return;
        using (SHA256 sha = SHA256.Create())
        {
            CurrentProcessOwner.MachineIdentity = BitConverter.ToString(
                sha.ComputeHash(Encoding.UTF8.GetBytes(rawIdentity))).Replace("-", string.Empty);
        }
    }

    private static ProcessProbeResult ProbeProcessStartUtcTicks(int processId, out long startUtcTicks)
    {
        startUtcTicks = 0;
        if (processStartUtcTicksProbeOverride != null)
        {
            long simulated = processStartUtcTicksProbeOverride(processId);
            if (simulated < 0) return ProcessProbeResult.Unsupported;
            if (simulated == 0) return ProcessProbeResult.Dead;
            startUtcTicks = simulated;
            return ProcessProbeResult.Alive;
        }
        try
        {
            using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId))
            {
                if (process.HasExited) return ProcessProbeResult.Dead;
                startUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                return ProcessProbeResult.Alive;
            }
        }
        catch (ArgumentException)
        {
            return ProcessProbeResult.Dead;
        }
        catch
        {
            // Access-denied and unsupported platform APIs cannot establish death.
            return ProcessProbeResult.Unsupported;
        }
    }

    private bool ReplayRecoveryJournal(RecoveryJournal journal)
    {
        if (journal.StartEventRow != null &&
            !AppendRecoveryCsvRow(journal.EventPath, EventHeader, journal.StartEventRow, false)) return false;
        foreach (string[] row in journal.OrdinaryRows)
        {
            if (!AppendRecoveryCsvRow(journal.EventPath, EventHeader, row, true)) return false;
        }

        // A frozen end supersedes its older active checkpoint.
        string[] endRow = journal.EndEventRow ?? journal.InterruptedEndEventRow;
        string[] summaryRow = journal.SummaryRow ?? journal.InterruptedSummaryRow;
        if (endRow != null &&
            !AppendRecoveryCsvRow(journal.EventPath, EventHeader, endRow, false)) return false;
        if (summaryRow != null &&
            !AppendRecoveryCsvRow(journal.SummaryPath, SummaryHeader, summaryRow, false)) return false;
        return true;
    }

    private bool AppendRecoveryCsvRow(string path, string[] header, string[] row, bool ordinaryEvent)
    {
        // Journal rows require a full scan because a later owner may have appended.
        InvalidateCache(GetCache(header));
        return AppendCsvRow(path, header, row, true, ordinaryEvent);
    }

    private bool DeleteRecoveryJournal()
    {
        string journalPath = RecoveryJournalPath;
        try
        {
            Directory.CreateDirectory(RecoveryDirectory);
            using (AcquireCsvFileLock(journalPath))
            {
                foreach (string artifact in JournalArtifacts(journalPath))
                {
                    if (!File.Exists(artifact)) continue;
                    RecoveryJournal existing = ReadRecoveryJournal(artifact);
                    ValidateJournalOwner(journalPath, existing);
                    if (!JournalTargetsCurrentOutputs(existing))
                    {
                        throw new InvalidOperationException("Owner journal artifact names different outputs.");
                    }
                }
                DeleteRecoveryJournalArtifacts(journalPath);
            }
            return true;
        }
        catch (Exception exception)
        {
            ReportPersistenceFailure($"Failed to remove committed telemetry recovery journal at {journalPath}: {exception.Message}");
            return false;
        }
    }

    private bool JournalTargetsCurrentOutputs(RecoveryJournal journal)
    {
        return journal.EventEnabled == writeEventCsv && journal.SummaryEnabled == writeSummaryCsv &&
               string.Equals(journal.EventPath, Path.GetFullPath(EventPath), StringComparison.Ordinal) &&
               string.Equals(journal.SummaryPath, Path.GetFullPath(SummaryPath), StringComparison.Ordinal);
    }

    private static void ValidateRecoveryDestination(string path, string destinationName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            throw new InvalidDataException(
                $"Unsafe {destinationName} recovery destination: journal path is not absolute.");
        }

        string fullPath = Path.GetFullPath(path);
        StringComparison comparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(path, fullPath, comparison))
        {
            throw new InvalidDataException(
                $"Unsafe {destinationName} recovery destination: journal path is not canonical.");
        }

        string trustedRoot = Path.GetFullPath(Application.persistentDataPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string trustedPrefix = trustedRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(trustedPrefix, comparison))
        {
            throw new InvalidDataException(
                $"Unsafe {destinationName} recovery destination outside persistentDataPath: {path}");
        }

        // Reject reparse points that can escape lexical containment.
        string cursor = fullPath;
        while (!string.Equals(cursor, trustedRoot, comparison))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Unsafe {destinationName} recovery destination traverses a reparse point: {path}");
                }
            }
            catch (FileNotFoundException)
            {
                // Missing paths are checked through their existing ancestors.
            }
            catch (DirectoryNotFoundException)
            {
                // Missing paths are checked through their existing ancestors.
            }

            DirectoryInfo parent = Directory.GetParent(cursor);
            if (parent == null)
            {
                throw new InvalidDataException(
                    $"Unsafe {destinationName} recovery destination has no trusted ancestor: {path}");
            }
            cursor = parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool JournalDestinationsEqual(RecoveryJournal left, RecoveryJournal right)
    {
        return left.EventEnabled == right.EventEnabled && left.SummaryEnabled == right.SummaryEnabled &&
               string.Equals(left.EventPath, right.EventPath, StringComparison.Ordinal) &&
               string.Equals(left.SummaryPath, right.SummaryPath, StringComparison.Ordinal);
    }

    private IEnumerable<string> EnumerateRecoveryJournalPaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(RecoveryDirectory)) return paths;
        foreach (string artifact in Directory.GetFiles(RecoveryDirectory, "pending_rows*"))
        {
            string path = artifact;
            if (path.EndsWith(".tmp", StringComparison.Ordinal) || path.EndsWith(".bak", StringComparison.Ordinal))
            {
                path = path.Substring(0, path.Length - 4);
            }
            if (string.Equals(path, LegacyRecoveryJournalPath, StringComparison.Ordinal) ||
                (Path.GetFileName(path).StartsWith("pending_rows.", StringComparison.Ordinal) &&
                 path.EndsWith(".journal", StringComparison.Ordinal)))
            {
                paths.Add(path);
            }
        }
        var ordered = new List<string>(paths);
        ordered.Sort(StringComparer.Ordinal);
        return ordered;
    }

    private static string[] JournalArtifacts(string journalPath)
    {
        return new[] { journalPath, journalPath + ".tmp", journalPath + ".bak" };
    }

    private RecoveryJournal LoadNewestJournalArtifact(string journalPath)
    {
        // Validate each atomic-replacement artifact independently under the journal lock.
        string[] artifacts = JournalArtifacts(journalPath);
        var candidates = new List<RecoveryJournalArtifactCandidate>();
        for (int i = 0; i < artifacts.Length; i++)
        {
            if (!File.Exists(artifacts[i])) continue;
            candidates.Add(new RecoveryJournalArtifactCandidate
            {
                Path = artifacts[i],
                LastWriteUtc = File.GetLastWriteTimeUtc(artifacts[i]),
                // Equal timestamps prefer temp, then main, then backup.
                TieBreakPriority = i == 1 ? 0 : i == 0 ? 1 : 2
            });
        }
        if (candidates.Count == 0) return null;
        candidates.Sort((left, right) =>
        {
            int newestFirst = right.LastWriteUtc.CompareTo(left.LastWriteUtc);
            return newestFirst != 0
                ? newestFirst
                : left.TieBreakPriority.CompareTo(right.TieBreakPriority);
        });

        RecoveryJournal selected = null;
        var corrupt = new List<KeyValuePair<string, Exception>>();
        foreach (RecoveryJournalArtifactCandidate candidate in candidates)
        {
            try
            {
                RecoveryJournal journal = ReadRecoveryJournal(candidate.Path);
                ValidateJournalOwner(journalPath, journal);
                if (selected == null) selected = journal;
            }
            catch (Exception exception)
            {
                corrupt.Add(new KeyValuePair<string, Exception>(candidate.Path, exception));
            }
        }

        if (selected == null)
        {
            // Keep unresolved artifacts visible to later readiness checks.
            var details = new StringBuilder();
            foreach (KeyValuePair<string, Exception> failure in corrupt)
            {
                if (details.Length != 0) details.Append("; ");
                details.Append(Path.GetFileName(failure.Key)).Append(": ").Append(failure.Value.Message);
            }
            throw new InvalidDataException(
                $"No valid recovery journal artifact exists ({details}). Corrupt evidence was left unchanged.");
        }

        // Quarantine corrupt siblings before replaying the valid snapshot.
        foreach (KeyValuePair<string, Exception> failure in corrupt)
        {
            string quarantinedPath = QuarantineCorruptJournalArtifact(failure.Key);
            Debug.LogWarning(
                $"[DroneTelemetry] Quarantined corrupt recovery artifact {failure.Key} as {quarantinedPath}: " +
                failure.Value.Message,
                this);
        }
        return selected;
    }

    private static string QuarantineCorruptJournalArtifact(string path)
    {
        string destination = path + ".corrupt";
        int suffix = 1;
        while (File.Exists(destination))
        {
            destination = path + ".corrupt." + suffix.ToString(InvariantCulture);
            suffix++;
        }
        File.Move(path, destination);
        return destination;
    }

    private void ValidateJournalOwner(string journalPath, RecoveryJournal journal)
    {
        if (string.Equals(journalPath, LegacyRecoveryJournalPath, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(journal.OwnerId))
                throw new InvalidOperationException("Legacy journal unexpectedly claims an owner.");
            return;
        }
        string name = Path.GetFileName(journalPath);
        string prefix = "pending_rows.";
        string suffix = ".journal";
        string expected = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
        if (string.IsNullOrEmpty(journal.OwnerId) ||
            !string.Equals(expected, journal.OwnerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Recovery journal filename does not match its owner identity.");
        }
    }

    private static void DeleteRecoveryJournalArtifacts(string journalPath)
    {
        foreach (string path in JournalArtifacts(journalPath))
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WriteRecoveryJournalAtomically(string journalPath, RecoveryJournal journal)
    {
        byte[] payload;
        using (var payloadStream = new MemoryStream())
        using (var writer = new BinaryWriter(payloadStream, CsvEncoding, true))
        {
            writer.Write(RecoveryJournalVersion);
            WriteJournalString(writer, journal.OwnerId);
            WriteJournalString(writer, journal.EventPath);
            WriteJournalString(writer, journal.SummaryPath);
            writer.Write(EventSchemaVersion);
            writer.Write(SummarySchemaVersion);
            writer.Write(journal.EventEnabled);
            writer.Write(journal.SummaryEnabled);
            writer.Write(journal.OrdinaryRows.Count);
            foreach (string[] row in journal.OrdinaryRows) WriteJournalRow(writer, row);
            WriteOptionalJournalRow(writer, journal.EndEventRow);
            WriteOptionalJournalRow(writer, journal.SummaryRow);
            writer.Write(journal.ActiveCheckpoint);
            WriteJournalString(writer, journal.ProcessEpoch);
            WriteJournalString(writer, journal.MachineIdentity);
            writer.Write(journal.ProcessId);
            writer.Write(journal.ProcessStartUtcTicks);
            WriteJournalString(writer, journal.ProcessNonce);
            writer.Write(journal.HeartbeatUtcTicks);
            WriteOptionalJournalRow(writer, journal.StartEventRow);
            WriteOptionalJournalRow(writer, journal.InterruptedEndEventRow);
            WriteOptionalJournalRow(writer, journal.InterruptedSummaryRow);
            writer.Flush();
            payload = payloadStream.ToArray();
        }

        byte[] checksum;
        using (SHA256 sha = SHA256.Create()) { checksum = sha.ComputeHash(payload); }

        string tempPath = journalPath + ".tmp";
        string backupPath = journalPath + ".bak";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(RecoveryJournalMagic, 0, RecoveryJournalMagic.Length);
            byte[] length = BitConverter.GetBytes(payload.Length);
            stream.Write(length, 0, length.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Write(checksum, 0, checksum.Length);
            stream.Flush(true);
        }

        if (File.Exists(journalPath))
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Replace(tempPath, journalPath, backupPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
        else
        {
            File.Move(tempPath, journalPath);
        }
    }

    private static RecoveryJournal ReadRecoveryJournal(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 64L * 1024L * 1024L)
        {
            throw new FormatException("Recovery journal exceeds the safe size limit.");
        }
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < RecoveryJournalMagic.Length + sizeof(int) + 32)
        {
            throw new FormatException("Recovery journal is truncated.");
        }
        for (int i = 0; i < RecoveryJournalMagic.Length; i++)
        {
            if (bytes[i] != RecoveryJournalMagic[i])
            {
                throw new FormatException("Recovery journal magic is invalid.");
            }
        }

        int payloadLength = BitConverter.ToInt32(bytes, RecoveryJournalMagic.Length);
        int payloadOffset = RecoveryJournalMagic.Length + sizeof(int);
        if (payloadLength < 0 || payloadLength != bytes.Length - payloadOffset - 32)
        {
            throw new FormatException("Recovery journal length is invalid.");
        }
        using (SHA256 sha = SHA256.Create())
        {
            byte[] expected = sha.ComputeHash(bytes, payloadOffset, payloadLength);
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != bytes[payloadOffset + payloadLength + i])
                {
                    throw new FormatException("Recovery journal checksum is invalid.");
                }
            }
        }

        using (var stream = new MemoryStream(bytes, payloadOffset, payloadLength, false))
        using (var reader = new BinaryReader(stream, CsvEncoding))
        {
            int version = reader.ReadInt32();
            if (version != RecoveryJournalVersion && version != ActiveRecoveryJournalVersion &&
                version != OwnerRecoveryJournalVersion && version != LegacyRecoveryJournalVersion)
            {
                throw new FormatException($"Unsupported recovery journal schema v{version}.");
            }
            var journal = new RecoveryJournal();
            journal.OwnerId = version >= OwnerRecoveryJournalVersion ? ReadJournalString(reader) : string.Empty;
            journal.EventPath = ReadJournalString(reader);
            journal.SummaryPath = ReadJournalString(reader);
            if (reader.ReadInt32() != EventSchemaVersion || reader.ReadInt32() != SummarySchemaVersion)
            {
                throw new FormatException("Recovery journal CSV schema identity is unsupported.");
            }
            journal.EventEnabled = reader.ReadBoolean();
            journal.SummaryEnabled = reader.ReadBoolean();
            int rowCount = reader.ReadInt32();
            if (rowCount < 0 || rowCount > MaxJournalRows)
            {
                throw new FormatException("Recovery journal row count is invalid.");
            }
            for (int i = 0; i < rowCount; i++)
            {
                journal.OrdinaryRows.Add(ReadJournalRow(reader, EventHeader.Length));
            }
            journal.EndEventRow = ReadOptionalJournalRow(reader, EventHeader.Length);
            journal.SummaryRow = ReadOptionalJournalRow(reader, SummaryHeader.Length);
            if (version >= ActiveRecoveryJournalVersion)
            {
                journal.ActiveCheckpoint = reader.ReadBoolean();
                journal.ProcessEpoch = ReadJournalString(reader);
                if (version >= RecoveryJournalVersion)
                {
                    journal.MachineIdentity = ReadJournalString(reader);
                    journal.ProcessId = reader.ReadInt32();
                    journal.ProcessStartUtcTicks = reader.ReadInt64();
                    journal.ProcessNonce = ReadJournalString(reader);
                    journal.HeartbeatUtcTicks = reader.ReadInt64();
                }
                journal.StartEventRow = ReadOptionalJournalRow(reader, EventHeader.Length);
                journal.InterruptedEndEventRow = ReadOptionalJournalRow(reader, EventHeader.Length);
                journal.InterruptedSummaryRow = ReadOptionalJournalRow(reader, SummaryHeader.Length);
            }
            if (stream.Position != stream.Length)
            {
                throw new FormatException("Recovery journal has trailing payload data.");
            }
            if ((!journal.EventEnabled && (journal.OrdinaryRows.Count != 0 || journal.EndEventRow != null ||
                                            journal.StartEventRow != null || journal.InterruptedEndEventRow != null)) ||
                (!journal.SummaryEnabled && (journal.SummaryRow != null || journal.InterruptedSummaryRow != null)) ||
                (journal.ActiveCheckpoint && string.IsNullOrEmpty(journal.ProcessEpoch)) ||
                (version >= RecoveryJournalVersion && journal.ActiveCheckpoint &&
                 (string.IsNullOrEmpty(journal.ProcessNonce) || journal.HeartbeatUtcTicks <= 0)) ||
                (journal.ActiveCheckpoint && journal.EndEventRow != null))
            {
                throw new FormatException("Recovery journal contains inconsistent active-session data.");
            }
            return journal;
        }
    }

    private static void WriteOptionalJournalRow(BinaryWriter writer, string[] row)
    {
        writer.Write(row != null);
        if (row != null) WriteJournalRow(writer, row);
    }

    private static string[] ReadOptionalJournalRow(BinaryReader reader, int expectedFields)
    {
        return reader.ReadBoolean() ? ReadJournalRow(reader, expectedFields) : null;
    }

    private static void WriteJournalRow(BinaryWriter writer, string[] row)
    {
        writer.Write(row.Length);
        foreach (string field in row) WriteJournalString(writer, field ?? string.Empty);
    }

    private static string[] ReadJournalRow(BinaryReader reader, int expectedFields)
    {
        int count = reader.ReadInt32();
        if (count != expectedFields) throw new FormatException("Recovery journal row schema is invalid.");
        var row = new string[count];
        for (int i = 0; i < count; i++) row[i] = ReadJournalString(reader);
        return row;
    }

    private static void WriteJournalString(BinaryWriter writer, string value)
    {
        byte[] bytes = CsvEncoding.GetBytes(value ?? string.Empty);
        if (bytes.Length > MaxJournalStringBytes) throw new InvalidDataException("Recovery journal field is too large.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadJournalString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxJournalStringBytes) throw new FormatException("Recovery journal field length is invalid.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Recovery journal field is truncated.");
        return CsvEncoding.GetString(bytes);
    }

    private void OnDestroy()
    {
        // Teardown may not run another retry, so persist the active session synchronously.
        try
        {
            if (activeSession)
            {
                // Preserve an end already frozen by the bootstrap.
                TryEndSession("recorder_destroyed", false);
            }
            else if (pendingOrdinaryEvents.Count > 0)
            {
                PersistRecoveryJournalSnapshot();
            }
        }
        catch (Exception exception)
        {
            // Unity teardown must not throw through user code.
            try
            {
                if (pendingOrdinaryEvents.Count > 0 || (activeSession && endRequested))
                {
                    PersistRecoveryJournalSnapshot();
                }
            }
            catch
            {
                // Teardown persistence is best effort.
            }

            try
            {
                Debug.LogWarning(
                    $"[DroneTelemetry] Recorder teardown could not finish telemetry persistence: {exception.Message}",
                    this);
            }
            catch
            {
                // Logging during teardown is best effort.
            }
        }
        finally
        {
            LiveCheckpointOwners.Remove(recoveryJournalOwnerId);
        }
    }

    private static CsvFileLock AcquireCsvFileLock(string path)
    {
        string lockPath = Path.GetFullPath(path) + ".lock";
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(CsvLockTimeoutMilliseconds);
        bool contended = false;
        IOException lastError = null;
        while (true)
        {
            try
            {
                return new CsvFileLock(
                    new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
                    contended);
            }
            catch (IOException exception)
            {
                lastError = exception;
                contended = true;
                if (DateTime.UtcNow >= deadline)
                {
                    throw new IOException(
                        $"Timed out acquiring telemetry CSV lock {lockPath} after {CsvLockTimeoutMilliseconds} ms.",
                        lastError);
                }

                Thread.Sleep(CsvLockRetryMilliseconds);
            }
        }
    }

    private static bool IsCacheCurrent(ValidatedCsvCache cache, string path)
    {
        if (!cache.HasValue || !string.Equals(cache.FullPath, Path.GetFullPath(path), StringComparison.Ordinal))
        {
            return false;
        }

        var info = new FileInfo(path);
        info.Refresh();
        return info.Exists && info.Length == cache.Length &&
               info.LastWriteTimeUtc == cache.LastWriteUtc &&
               info.CreationTimeUtc == cache.CreationUtc &&
               ComputeContentSentinel(path, info.Length) == cache.ContentSentinel;
    }

    private static void CaptureFingerprint(ValidatedCsvCache cache, string path, string[] lastRecord)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists)
        {
            throw new FileNotFoundException("The validated CSV no longer exists.", path);
        }

        cache.FullPath = Path.GetFullPath(path);
        cache.Length = info.Length;
        cache.LastWriteUtc = info.LastWriteTimeUtc;
        cache.CreationUtc = info.CreationTimeUtc;
        cache.ContentSentinel = ComputeContentSentinel(path, info.Length);
        cache.LastRecord = lastRecord == null ? null : (string[])lastRecord.Clone();
        cache.HasValue = true;
    }

    private static ulong ComputeContentSentinel(string path, long length)
    {
        // First/tail samples detect edits without a full scan.
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var buffer = new byte[FingerprintSentinelBytes];
            int firstCount = stream.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < firstCount; i++)
            {
                hash = (hash ^ buffer[i]) * prime;
            }

            if (length > FingerprintSentinelBytes)
            {
                stream.Position = Math.Max(FingerprintSentinelBytes, length - FingerprintSentinelBytes);
                int tailCount = stream.Read(buffer, 0, buffer.Length);
                for (int i = 0; i < tailCount; i++)
                {
                    hash = (hash ^ buffer[i]) * prime;
                }
            }
        }

        unchecked
        {
            hash = (hash ^ (ulong)length) * prime;
        }
        return hash;
    }

    private static void InvalidateCache(ValidatedCsvCache cache)
    {
        cache.HasValue = false;
        cache.FullPath = string.Empty;
        cache.LastRecord = null;
        cache.EndReasonCounts.Clear();
        cache.BatchEndReasonSessionIds.Clear();
    }

    private bool ValidateAndCacheCsvFile(
        string path,
        string[] expectedHeader,
        int schemaVersion,
        ValidatedCsvCache cache,
        string[] soughtRecord = null)
    {
        fullCsvValidationCount++;
        bool soughtRecordFound = false;
        string[] lastRecord = null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var batchReasonSessionIds =
            new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
        bool isSummary = ReferenceEquals(expectedHeader, SummaryHeader);
        using (var reader = new StreamReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), CsvEncoding))
        {
            if (!TryReadCsvRecord(reader, out string[] actualHeader))
            {
                throw new FormatException("CSV is empty and has no header.");
            }

            ValidateHeader(path, actualHeader, expectedHeader, schemaVersion);
            while (TryReadCsvRecord(reader, out string[] record))
            {
                ValidateRecord(path, record, expectedHeader.Length);
                lastRecord = record;
                if (soughtRecord != null && RowsHaveSameIdentity(expectedHeader, record, soughtRecord))
                {
                    soughtRecordFound = true;
                }

                if (isSummary)
                {
                    IndexSummaryRecord(counts, batchReasonSessionIds, record);
                }
            }
        }

        cache.EndReasonCounts.Clear();
        foreach (KeyValuePair<string, int> pair in counts)
        {
            cache.EndReasonCounts.Add(pair.Key, pair.Value);
        }
        cache.BatchEndReasonSessionIds.Clear();
        foreach (KeyValuePair<string, Dictionary<string, HashSet<string>>> pair in batchReasonSessionIds)
        {
            cache.BatchEndReasonSessionIds.Add(pair.Key, pair.Value);
        }
        CaptureFingerprint(cache, path, lastRecord);
        return soughtRecordFound;
    }

    private static bool RowsHaveSameIdentity(string[] header, string[] left, string[] right)
    {
        if (left == null || right == null || left.Length != header.Length || right.Length != header.Length)
        {
            return false;
        }

        // Exact fields dedupe retries without collapsing similar legitimate events.
        for (int i = 0; i < header.Length; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static void IndexSummaryRecord(
        IDictionary<string, int> counts,
        IDictionary<string, Dictionary<string, HashSet<string>>> batchReasonSessionIds,
        string[] record)
    {
        const int sessionIdIndex = 0;
        const int batchIdIndex = 1;
        const int endReasonIndex = 8;
        string endReason = record[endReasonIndex];
        counts.TryGetValue(endReason, out int current);
        counts[endReason] = current + 1;

        string sessionId = record[sessionIdIndex];
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        string batchId = record[batchIdIndex];
        if (!batchReasonSessionIds.TryGetValue(batchId, out var reasonSessions))
        {
            reasonSessions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            batchReasonSessionIds.Add(batchId, reasonSessions);
        }

        if (!reasonSessions.TryGetValue(endReason, out HashSet<string> sessionIds))
        {
            sessionIds = new HashSet<string>(StringComparer.Ordinal);
            reasonSessions.Add(endReason, sessionIds);
        }

        sessionIds.Add(sessionId);
    }

    private static void ValidateHeader(string path, string[] actual, string[] expected, int schemaVersion)
    {
        if (!FieldsEqual(actual, expected))
        {
            throw new FormatException(
                $"Header does not match schema v{schemaVersion} ({expected.Length} exact columns required). " +
                $"The existing file was left unchanged: {path}");
        }
    }

    private static void ValidateRecord(string path, string[] record, int expectedFieldCount)
    {
        if (record.Length != expectedFieldCount)
        {
            throw new FormatException(
                $"Malformed record has {record.Length} fields; expected {expectedFieldCount}. File was left unchanged: {path}");
        }
    }

    private static bool FieldsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadCsvRecord(TextReader reader, out string[] fields)
    {
        var result = new List<string>();
        var field = new StringBuilder();
        bool hasInput = false;
        bool inQuotes = false;
        bool closedQuote = false;

        while (true)
        {
            int next = reader.Read();
            if (next < 0)
            {
                if (!hasInput)
                {
                    fields = null;
                    return false;
                }

                if (inQuotes)
                {
                    throw new FormatException("CSV ended inside a quoted field.");
                }

                result.Add(field.ToString());
                fields = result.ToArray();
                return true;
            }

            hasInput = true;
            char c = (char)next;
            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                    continue;
                }

                if (reader.Peek() == '"')
                {
                    reader.Read();
                    field.Append('"');
                }
                else
                {
                    inQuotes = false;
                    closedQuote = true;
                }

                continue;
            }

            if (closedQuote && c != ',' && c != '\r' && c != '\n')
            {
                throw new FormatException("Unexpected character after a closing quote.");
            }

            if (c == '"')
            {
                if (field.Length != 0)
                {
                    throw new FormatException("Quote appeared inside an unquoted field.");
                }

                inQuotes = true;
            }
            else if (c == ',')
            {
                result.Add(field.ToString());
                field.Clear();
                closedQuote = false;
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                result.Add(field.ToString());
                fields = result.ToArray();
                return true;
            }
            else
            {
                field.Append(c);
            }
        }
    }

    private void ReportPersistenceFailure(string message, bool ordinaryEvent = false)
    {
        // Ordinary-event errors cannot clear a broader storage failure.
        bool preserveNonOrdinaryFailure = persistenceFailure
            && !ordinaryEventPersistenceFailure
            && ordinaryEvent;
        persistenceFailure = true;
        if (!preserveNonOrdinaryFailure)
        {
            ordinaryEventPersistenceFailure = ordinaryEvent;
            lastPersistenceError = message;
        }

        Debug.LogError($"[DroneTelemetry] {message}", this);
    }

    private string SummaryPath => Path.Combine(OutputDirectory, summaryFileName);
    private string EventPath => Path.Combine(OutputDirectory, eventFileName);

    private static string ToCsvLine(IReadOnlyList<string> values)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(EscapeCsv(values[i]));
        }

        return builder.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needsQuotes)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string GenerateSessionId()
    {
        return $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    }

    private static string FormatUtc(DateTime utc)
    {
        return utc.ToUniversalTime().ToString("O", InvariantCulture);
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.###", InvariantCulture);
    }

    private static string FormatInt(int value)
    {
        return value.ToString(InvariantCulture);
    }

    private static string FloatOrBlank(bool hasValue, float value)
    {
        return hasValue ? FormatFloat(value) : string.Empty;
    }

    private static string IntOrBlank(bool hasValue, int value)
    {
        return hasValue ? FormatInt(value) : string.Empty;
    }
}

[Serializable]
public sealed class DroneMissionSessionConfig
{
    public string BatchId = string.Empty;
    public bool HasBatchRunIndex;
    public int BatchRunIndex;
    public bool HasBatchConfigurationIndex;
    public int BatchConfigurationIndex;
    public bool HasBatchRepeatIndex;
    public int BatchRepeatIndex;
    public bool HasRandomSeed;
    public int RandomSeed;
    public string SceneName = string.Empty;
    public string PlannerType = string.Empty;
    public int DroneCount;
    public int SensorRadius;
    public float CommunicationRadius;
    public float DroneSpeed;
    public int GridWidth;
    public int GridDepth;
    public float CellSize;
    public bool HasTargetStartCell;
    public DroneNative.DroneVec3i TargetStartCell;
    public bool HasTargetStartWorldPosition;
    public Vector3 TargetStartWorldPosition;
    public bool HasNearestDroneStartDistance;
    public float NearestDroneStartDistanceCells;
    public float NearestDroneStartDistanceWorld;

    public string ToEventValue()
    {
        string batch = string.IsNullOrEmpty(BatchId) ? string.Empty : $"batch={BatchId};run={BatchRunIndex};config={BatchConfigurationIndex};repeat={BatchRepeatIndex};seed={(HasRandomSeed ? RandomSeed.ToString(CultureInfo.InvariantCulture) : string.Empty)};";
        return $"{batch}scene={SceneName};planner={PlannerType};drones={DroneCount};sensor={SensorRadius};comms={CommunicationRadius.ToString("0.###", CultureInfo.InvariantCulture)};speed={DroneSpeed.ToString("0.###", CultureInfo.InvariantCulture)};grid={GridWidth}x{GridDepth}";
    }
}
