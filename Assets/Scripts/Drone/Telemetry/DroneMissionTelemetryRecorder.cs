using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public sealed class DroneMissionTelemetryRecorder : MonoBehaviour
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private static readonly Encoding CsvEncoding = new UTF8Encoding(false);

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

    private readonly HashSet<int> targetFinderDroneIds = new();
    private readonly HashSet<int> informedDroneIds = new();

    private DroneMissionSessionConfig config = new();
    private string sessionId = string.Empty;
    private DateTime sessionStartUtc;
    private DateTime sessionEndUtc;
    private float sessionStartTime;
    private bool activeSession;
    private bool summaryWritten;

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
    public string ActiveSessionId => sessionId;
    public float ElapsedSeconds => activeSession ? Mathf.Max(0f, Time.time - sessionStartTime) : 0f;
    public string OutputDirectory => Path.Combine(Application.persistentDataPath, outputDirectoryName);
    public string SummaryFilePath => Path.Combine(OutputDirectory, summaryFileName);

    /// <summary>
    /// Counts summary rows whose <c>end_reason</c> column matches <paramref name="endReason"/>.
    /// Used by batch runners to stop once enough valid missions have been collected.
    /// Returns 0 when the summary CSV does not exist yet.
    /// </summary>
    public int CountSessionsWithEndReason(string endReason)
    {
        if (string.IsNullOrEmpty(endReason))
        {
            return 0;
        }

        string path = SummaryFilePath;
        if (!File.Exists(path))
        {
            return 0;
        }

        int endIndex = Array.IndexOf(SummaryHeader, "end_reason");
        if (endIndex < 0)
        {
            return 0;
        }

        int count = 0;
        try
        {
            using (var reader = new StreamReader(path, CsvEncoding))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    string[] fields = ParseCsvLine(line);
                    if (fields.Length <= endIndex)
                    {
                        continue;
                    }

                    if (fields[endIndex] == endReason)
                    {
                        count++;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[DroneTelemetry] Failed to read {path}: {exception.Message}", this);
        }

        return count;
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result.ToArray();
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

    public void BeginSession(DroneMissionSessionConfig sessionConfig)
    {
        if (activeSession)
        {
            EndSession("superseded", false);
        }

        config = sessionConfig ?? new DroneMissionSessionConfig();
        sessionId = GenerateSessionId();
        sessionStartUtc = DateTime.UtcNow;
        sessionStartTime = Time.time;
        activeSession = true;
        summaryWritten = false;

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

        EnsureOutputFiles();
        AppendEvent("session_start", -1, -1, 0, false, default, config.ToEventValue());

        if (logOutputPathOnSessionStart)
        {
            Debug.Log($"[DroneTelemetry] Session {sessionId} started. CSV output: {OutputDirectory}", this);
        }
    }

    public void RecordTargetFound(int droneId, DroneNative.DroneVec3i cell)
    {
        if (!activeSession)
        {
            return;
        }

        if (droneId > 0)
        {
            if (!targetFinderDroneIds.Add(droneId))
            {
                return;
            }
        }
        else if (hasFirstTargetFound)
        {
            return;
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
        if (!activeSession || hasCommandNotified)
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
        if (!activeSession || droneId <= 0 || !informedDroneIds.Add(droneId))
        {
            return;
        }

        AppendEvent("drone_informed", droneId, -1, Mathf.Max(0, informedCount), true, cell, string.Empty);
    }

    public void RecordAllDronesInformed(int informedCount, DroneNative.DroneVec3i cell)
    {
        if (!activeSession || hasAllDronesInformed)
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
        if (!activeSession || hasMissionComplete)
        {
            return;
        }

        hasMissionComplete = true;
        missionCompleteElapsed = ElapsedSeconds;
        AppendEvent("mission_complete", -1, -1, Mathf.Max(0, informedCount), true, cell, string.Empty);
    }

    public void EndSession(string endReason, bool completed)
    {
        if (!activeSession)
        {
            return;
        }

        sessionEndUtc = DateTime.UtcNow;
        string safeReason = string.IsNullOrWhiteSpace(endReason) ? "ended" : endReason;
        float durationSeconds = ElapsedSeconds;
        AppendEvent("session_end", -1, -1, allDronesInformedCount, false, default, safeReason);

        if (writeSummaryCsv && !summaryWritten)
        {
            AppendCsvRow(SummaryPath, SummaryHeader, BuildSummaryRow(safeReason, completed, durationSeconds));
            summaryWritten = true;
        }

        activeSession = false;
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

    private void EnsureOutputFiles()
    {
        if (writeSummaryCsv)
        {
            EnsureCsvHeader(SummaryPath, SummaryHeader);
        }

        if (writeEventCsv)
        {
            EnsureCsvHeader(EventPath, EventHeader);
        }
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
            return;
        }

        AppendCsvRow(EventPath, EventHeader, new[]
        {
            sessionId,
            FormatFloat(ElapsedSeconds),
            FormatUtc(DateTime.UtcNow),
            eventType,
            IntOrBlank(droneId >= 0, droneId),
            IntOrBlank(otherDroneId >= 0, otherDroneId),
            IntOrBlank(informedCount > 0, informedCount),
            IntOrBlank(hasCell, cell.x),
            IntOrBlank(hasCell, cell.y),
            IntOrBlank(hasCell, cell.z),
            value ?? string.Empty
        });
    }

    private void EnsureCsvHeader(string path, string[] header)
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return;
            }

            using (var writer = new StreamWriter(path, false, CsvEncoding))
            {
                writer.WriteLine(ToCsvLine(header));
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[DroneTelemetry] Failed to create CSV header at {path}: {exception.Message}", this);
        }
    }

    private void AppendCsvRow(string path, string[] header, string[] values)
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            bool needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            using (var writer = new StreamWriter(path, true, CsvEncoding))
            {
                if (needsHeader)
                {
                    writer.WriteLine(ToCsvLine(header));
                }

                writer.WriteLine(ToCsvLine(values));
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[DroneTelemetry] Failed to append CSV row at {path}: {exception.Message}", this);
        }
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
