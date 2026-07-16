using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.UI;
using System.Runtime.InteropServices;

public sealed class DroneSwarmDemoBootstrap : MonoBehaviour
{
    private const int c_ObstacleLayer = 7;
    private const int c_TargetLayer = 4;
    private const int c_NonSensedLayer = 2;
    private const string c_GridWorldLayerName = "GridWorld";
    private const float c_DroneCameraFieldOfView = 75f;
    private const int c_FinalTelemetryRetryAttempts = 3;
    private const float c_FinalTelemetryRetryIntervalSeconds = 0.5f;


    [Header("Grid")]
    [SerializeField] private int width = 18;
    [SerializeField] private int depth = 12;
    [SerializeField] private float cellSize = 1f;

    [Header("Swarm")]
    [SerializeField] private GameObject droneModelPrefab;
    [SerializeField] private int droneCount = 5;
    [SerializeField] private int sensorRadius = 2;
    [SerializeField] private float communicationRadius = 3.25f;
    [SerializeField] private float droneSpeed = 3f;
    [SerializeField] private DroneNative.PlannerType plannerType = DroneNative.PlannerType.AStar;

    [Header("Telemetry")]
    [SerializeField] private bool telemetryEnabled = true;
    [SerializeField] private DroneMissionTelemetryRecorder telemetryRecorder;
    [Tooltip("Optional automatic failure cutoff. Set to 0 to disable timeout-based session ending.")]
    [SerializeField] private float telemetryTimeoutSeconds = 0f;

    [Header("Scene Ownership")]
    [Tooltip("The human Explorer controlled by this simulation. Existing scenes may leave this empty when a ScriptsControl explicitly links this bootstrap to its Explorer.")]
    [SerializeField] private Explorer humanExplorer;
    [NonSerialized] private ScriptsControl scriptsControlOwner;

    private string telemetryBatchId = string.Empty;
    private bool telemetryHasBatchRunIndex;
    private int telemetryBatchRunIndex;
    private bool telemetryHasBatchConfigurationIndex;
    private int telemetryBatchConfigurationIndex;
    private bool telemetryHasBatchRepeatIndex;
    private int telemetryBatchRepeatIndex;
    private bool telemetryHasRandomSeed;
    private int telemetryRandomSeed;

    private readonly List<DroneFrontierExplorer> explorers = new();
    private readonly List<DroneCommunicationNode> communicationNodes = new();
    private readonly List<GameObject> spawnedObjects = new();
    private readonly HashSet<int> directTargetReporterIds = new();

    private DroneDemoGridWorld world;
    private DroneSwarmCommunicationHub communicationHub;
    private DroneCommandRoutePlanner commandRoutePlanner;
    private DroneSwarmAgentState commandState;
    private DroneSwarmDebugRenderer debugRenderer;
    private Text statusText;
    private Text plannerButtonText;
    private Text mapViewButtonText;
    private Text droneViewButtonText;
    private RectTransform droneCameraGrid;
    private readonly List<Camera> droneCameras = new();
    private readonly List<RawImage> droneCameraViews = new();
    private readonly List<RenderTexture> droneCameraTextures = new();
    private int appliedDroneCount;
    private int appliedSensorRadius;
    private float appliedCommunicationRadius;
    private float appliedDroneSpeed;
    private DroneNative.PlannerType appliedPlannerType;
    private bool runtimeConfigInitialized;
    private bool resetQueued;
    private Coroutine resetCoroutine;
    [NonSerialized] private Action<string> resetRuntimeSetupExceptionInjection;
    private bool lastResetSucceeded;
    private string lastResetError = string.Empty;
    private bool missionComplete;
    private bool gameplayStopped;
    private bool batchRunShutdownApplied;
    private bool newGamePreflightPassed;

    private sealed class ReplayRuntimeState
    {
        internal bool GameplayStopped;
        internal bool MissionComplete;
        internal bool BatchRunShutdownApplied;
        internal int Width;
        internal int Depth;
        internal float CellSize;
        internal readonly List<GameObject> SpawnedObjects = new();
        internal readonly List<DroneFrontierExplorer> Explorers = new();
        internal readonly List<DroneCommunicationNode> CommunicationNodes = new();
        internal readonly HashSet<int> DirectTargetReporterIds = new();
        internal readonly List<Camera> DroneCameras = new();
        internal readonly List<RawImage> DroneCameraViews = new();
        internal readonly List<RenderTexture> DroneCameraTextures = new();
        internal DroneDemoGridWorld World;
        internal DroneSwarmCommunicationHub CommunicationHub;
        internal DroneCommandRoutePlanner CommandRoutePlanner;
        internal DroneSwarmAgentState CommandState;
        internal DroneSwarmDebugRenderer DebugRenderer;
        internal bool RuntimeCaptured;
    }

    private ReplayRuntimeState replayRuntimeState;
    private bool finalTelemetryRetryExhausted;
    private bool finalTelemetryPersistencePending;
    private int finalTelemetryRetryAttemptsCompleted;
    private Coroutine finalTelemetryRetryCoroutine;
    private float nextStatusTextUpdateAt;

    public bool MissionComplete => missionComplete;

    /// <summary>
    /// Registers the scene controller that explicitly owns this bootstrap. The
    /// controller remains authoritative if replay setup replaces its Explorer.
    /// </summary>
    public void ConfigureExplorerOwnership(ScriptsControl owner)
    {
        if (owner != null && owner.droneSwarmDemoBootstrap == this)
        {
            scriptsControlOwner = owner;
        }
    }
    public bool IsResetQueued => resetQueued;
    /// <summary>Raised exactly once when an accepted queued reset succeeds, fails, or is cancelled.</summary>
    public event Action<bool, string> ResetCompleted;
    public bool LastResetSucceeded => lastResetSucceeded;
    public string LastResetError => lastResetError;
    public bool IsGameplayStopped => gameplayStopped;
    public bool IsTelemetrySessionActive => telemetryRecorder != null && telemetryRecorder.HasActiveSession;
    public bool HasTelemetryPersistenceFailure => telemetryRecorder != null && telemetryRecorder.HasPersistenceFailure;
    public string LastTelemetryPersistenceError => telemetryRecorder != null ? telemetryRecorder.LastPersistenceError : string.Empty;
    public bool IsFinalTelemetryPersistencePending => finalTelemetryPersistencePending
        || (telemetryRecorder != null && telemetryRecorder.HasPendingSessionEnd);
    public bool HasTerminalTelemetryPersistenceFailure => finalTelemetryRetryExhausted
        && telemetryRecorder != null
        && telemetryRecorder.HasPendingSessionEnd;
    public string ActiveTelemetrySessionId => telemetryRecorder != null ? telemetryRecorder.ActiveSessionId : string.Empty;
    private bool IsBatchRun => !string.IsNullOrEmpty(telemetryBatchId) || telemetryHasBatchRunIndex;

    public void ConfigureExperiment(
        int newDroneCount,
        int newSensorRadius,
        float newCommunicationRadius,
        float newDroneSpeed,
        DroneNative.PlannerType newPlannerType,
        float newTelemetryTimeoutSeconds = 0f,
        bool enableTelemetry = true)
    {
        droneCount = Mathf.Clamp(newDroneCount, 1, 12);
        sensorRadius = Mathf.Clamp(newSensorRadius, 1, 8);
        communicationRadius = Mathf.Max(0f, newCommunicationRadius);
        droneSpeed = Mathf.Max(0f, newDroneSpeed);
        plannerType = newPlannerType;
        telemetryTimeoutSeconds = Mathf.Max(0f, newTelemetryTimeoutSeconds);
        telemetryEnabled = enableTelemetry;
    }

    public void SetTelemetryBatchContext(
        string batchId,
        int runIndex,
        int configurationIndex,
        int repeatIndex,
        bool hasRandomSeed,
        int randomSeed)
    {
        telemetryBatchId = batchId ?? string.Empty;
        telemetryHasBatchRunIndex = runIndex >= 0;
        telemetryBatchRunIndex = runIndex;
        telemetryHasBatchConfigurationIndex = configurationIndex >= 0;
        telemetryBatchConfigurationIndex = configurationIndex;
        telemetryHasBatchRepeatIndex = repeatIndex >= 0;
        telemetryBatchRepeatIndex = repeatIndex;
        telemetryHasRandomSeed = hasRandomSeed;
        telemetryRandomSeed = randomSeed;
    }

    public void ClearTelemetryBatchContext()
    {
        telemetryBatchId = string.Empty;
        telemetryHasBatchRunIndex = false;
        telemetryBatchRunIndex = 0;
        telemetryHasBatchConfigurationIndex = false;
        telemetryBatchConfigurationIndex = 0;
        telemetryHasBatchRepeatIndex = false;
        telemetryBatchRepeatIndex = 0;
        telemetryHasRandomSeed = false;
        telemetryRandomSeed = 0;
    }

    public bool EndActiveTelemetrySession(string endReason, bool completed)
    {
        bool persisted = EndTelemetrySession(endReason, completed);
        if (!persisted)
        {
            StartFinalTelemetryRetryIfNeeded();
        }

        return persisted;
    }

    /// <summary>
    /// Terminates bootstrap-owned work for a batch run and freezes the current
    /// simulation. Unlike <see cref="StopSimulation"/>, this does not latch the
    /// interactive game in its stopped state, so a later deliberate batch reset
    /// can replace the frozen world. A telemetry end that is already pending keeps
    /// its original frozen rows and reason.
    /// </summary>
    public void ShutdownBatchRun(string endReason, bool completed)
    {
        newGamePreflightPassed = false;

        if (resetCoroutine != null)
        {
            StopCoroutine(resetCoroutine);
            resetCoroutine = null;
        }

        if (resetQueued)
        {
            CompleteReset(
                false,
                string.IsNullOrWhiteSpace(endReason)
                    ? "Batch run shut down before the queued reset completed."
                    : $"Batch run shut down: {endReason}");
        }

        // TryEndSession is itself idempotent and preserves an already-pending end.
        // Keep an existing bounded retry alive; cleanup is not a reason to discard
        // frozen rows that still have recovery attempts remaining.
        bool telemetryPersisted = EndTelemetrySession(endReason, completed);

        missionComplete = true;
        batchRunShutdownApplied = true;
        FreezeSimulationComponents();
        if (!telemetryPersisted)
        {
            StartFinalTelemetryRetryIfNeeded();
        }
    }

    /// <summary>
    /// Makes telemetry ready before a batch performs baseline reads or failure
    /// checks. This also creates the lazily configured recorder. A pending end is
    /// retried with its original frozen rows; it is never replaced or discarded.
    /// </summary>
    public bool TryPrepareTelemetryForBatch(out DroneMissionTelemetryRecorder recorder)
    {
        recorder = null;
        if (!telemetryEnabled)
        {
            Debug.LogError("[DroneTelemetry] Batch telemetry is disabled on the bootstrap.", this);
            return false;
        }

        recorder = EnsureTelemetryRecorder();
        if (recorder == null)
        {
            Debug.LogError("[DroneTelemetry] Cannot prepare batch telemetry because no recorder is available.", this);
            return false;
        }

        if (!recorder.IsSummaryCsvEnabled)
        {
            Debug.LogError(
                "[DroneTelemetry] Cannot prepare batch telemetry because summary CSV output is disabled. Batch completion requires persisted summary rows.",
                this);
            return false;
        }

        if (recorder.HasPendingSessionEnd)
        {
            if (!recorder.TryEndSession("telemetry_recovery", false))
            {
                Debug.LogError(
                    $"[DroneTelemetry] Cannot prepare batch telemetry while the prior session end remains pending: {recorder.LastPersistenceError}",
                    this);
                return false;
            }

            // Readiness can recover the recorder synchronously, outside the retry
            // coroutine. Reconcile the bootstrap-owned gate before any runner waits
            // on it or attempts cleanup without starting another session.
            MarkFinalTelemetryPersistenceComplete();
        }

        if (recorder.HasActiveSession)
        {
            Debug.LogError(
                "[DroneTelemetry] Cannot prepare batch telemetry while another session is active. End it explicitly first.",
                this);
            return false;
        }

        if (!recorder.TryRevalidateStorage())
        {
            Debug.LogError($"[DroneTelemetry] Batch storage validation failed: {recorder.LastPersistenceError}", this);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Authoritatively ends the current interactive mission without destroying its
    /// world, so result cameras can continue to render the final state.
    /// </summary>
    public void StopSimulation(string endReason, bool completed)
    {
        if (gameplayStopped)
        {
            StartFinalTelemetryRetryIfNeeded();
            return;
        }

        gameplayStopped = true;
        missionComplete = true;
        bool telemetryPersisted = EndTelemetrySession(endReason, completed);
        FreezeSimulationComponents();

        if (!telemetryPersisted)
        {
            StartFinalTelemetryRetryIfNeeded();
        }
    }

    /// <summary>
    /// Performs replay eligibility and persistence checks without changing the retained
    /// result world or clearing the stopped-game latch. ScriptsControl calls this before
    /// regenerating any part of the interactive world.
    /// </summary>
    public bool TryPrepareForNewGame()
    {
        newGamePreflightPassed = false;

        if (!gameplayStopped)
        {
            Debug.LogError("[DroneSwarmDemoBootstrap] Replay can only begin after the interactive mission has stopped.", this);
            return false;
        }
        if (IsBatchRun)
        {
            Debug.LogError("[DroneSwarmDemoBootstrap] Interactive replay is not available during a batch run.", this);
            return false;
        }
        if (resetQueued)
        {
            Debug.LogError("[DroneSwarmDemoBootstrap] Cannot begin replay while a world reset is already queued.", this);
            return false;
        }

        if (telemetryRecorder != null && telemetryRecorder.HasActiveSession
            && !telemetryRecorder.TryEndSession("replay_recovery", false))
        {
            Debug.LogError(
                $"[DroneTelemetry] Replay blocked because the prior session end is still pending: {telemetryRecorder.LastPersistenceError}",
                this);
            StartFinalTelemetryRetryIfNeeded();
            return false;
        }

        if (telemetryEnabled)
        {
            DroneMissionTelemetryRecorder recorder = EnsureTelemetryRecorder();
            if (recorder == null || !recorder.TryRevalidateStorage())
            {
                string error = recorder != null ? recorder.LastPersistenceError : "no recorder is available";
                Debug.LogError($"[DroneTelemetry] Replay storage validation failed: {error}", this);
                return false;
            }
        }

        newGamePreflightPassed = true;
        return true;
    }

    public bool CanActivatePreparedNewGame()
    {
        return newGamePreflightPassed && gameplayStopped && !IsBatchRun
            && !resetQueued && replayRuntimeState == null;
    }

    /// <summary>
    /// Opens a rollback-capable replay activation. Result drones remain intact until
    /// CommitPreparedNewGame is called after the replacement reset has succeeded.
    /// </summary>
    public bool TryActivatePreparedNewGame()
    {
        if (!CanActivatePreparedNewGame())
        {
            Debug.LogError("[DroneSwarmDemoBootstrap] Replay activation requires a successful, current preflight.", this);
            return false;
        }

        replayRuntimeState = new ReplayRuntimeState
        {
            GameplayStopped = gameplayStopped,
            MissionComplete = missionComplete,
            BatchRunShutdownApplied = batchRunShutdownApplied,
            Width = width,
            Depth = depth,
            CellSize = cellSize
        };
        newGamePreflightPassed = false;
        if (finalTelemetryRetryCoroutine != null)
        {
            StopCoroutine(finalTelemetryRetryCoroutine);
            finalTelemetryRetryCoroutine = null;
        }

        finalTelemetryRetryExhausted = false;
        finalTelemetryPersistencePending = false;
        finalTelemetryRetryAttemptsCompleted = 0;
        gameplayStopped = false;
        missionComplete = false;
        return true;
    }

    public void CommitPreparedNewGame()
    {
        ReplayRuntimeState retained = replayRuntimeState;
        if (retained == null) return;
        replayRuntimeState = null;
        if (!retained.RuntimeCaptured)
        {
            retained.SpawnedObjects.AddRange(spawnedObjects);
            spawnedObjects.Clear();
        }
        foreach (GameObject instance in retained.SpawnedObjects)
        {
            if (instance == null) continue;
            instance.SetActive(false);
            if (Application.isPlaying) Destroy(instance); else DestroyImmediate(instance);
        }
        foreach (RenderTexture texture in retained.DroneCameraTextures)
        {
            if (texture == null) continue;
            texture.Release();
            if (Application.isPlaying) Destroy(texture); else DestroyImmediate(texture);
        }
    }

    public void RollbackPreparedNewGame()
    {
        ReplayRuntimeState retained = replayRuntimeState;
        if (retained == null) return;
        replayRuntimeState = null;

        if (retained.RuntimeCaptured)
        {
            ClearRuntimeState();
            spawnedObjects.AddRange(retained.SpawnedObjects);
            explorers.AddRange(retained.Explorers);
            communicationNodes.AddRange(retained.CommunicationNodes);
            directTargetReporterIds.UnionWith(retained.DirectTargetReporterIds);
            droneCameras.AddRange(retained.DroneCameras);
            droneCameraViews.AddRange(retained.DroneCameraViews);
            droneCameraTextures.AddRange(retained.DroneCameraTextures);
            world = retained.World;
            communicationHub = retained.CommunicationHub;
            commandRoutePlanner = retained.CommandRoutePlanner;
            commandState = retained.CommandState;
            debugRenderer = retained.DebugRenderer;
        }

        width = retained.Width;
        depth = retained.Depth;
        cellSize = retained.CellSize;
        gameplayStopped = retained.GameplayStopped;
        missionComplete = retained.MissionComplete;
        batchRunShutdownApplied = retained.BatchRunShutdownApplied;
        newGamePreflightPassed = false;
    }

    /// <summary>Discards replay eligibility without changing world or telemetry state.</summary>
    public void CancelPreparedNewGame()
    {
        newGamePreflightPassed = false;
    }

    /// <summary>
    /// Restores the interactive replay boundary when startup fails after activation.
    /// The replacement world remains available as the stopped result world and can be
    /// transactionally replaced by a later retry.
    /// </summary>
    public void RestoreStoppedReplayBoundary()
    {
        newGamePreflightPassed = false;
        gameplayStopped = true;
        missionComplete = true;
        FreezeSimulationComponents();
    }

    /// <summary>
    /// Removes bootstrap-owned state from a failed initial startup. Unlike replay
    /// rollback, initial startup must remain unstopped so the ordinary Start action can
    /// retry rather than being routed through the new-game boundary.
    /// </summary>
    public void RollbackInitialStartup()
    {
        newGamePreflightPassed = false;
        ClearRuntimeState();
        gameplayStopped = false;
        missionComplete = false;
        batchRunShutdownApplied = false;
    }

    private void StartFinalTelemetryRetryIfNeeded()
    {
        // A coroutine handle is only the current waiter, not the durable state. Unity
        // stops it on deactivation, so keep the pending flag and completed-attempt count
        // until persistence succeeds or the bounded retry becomes terminal.
        if (telemetryRecorder != null && telemetryRecorder.HasPendingSessionEnd)
        {
            finalTelemetryPersistencePending = true;
        }
        else if (telemetryRecorder != null && finalTelemetryPersistencePending)
        {
            // Persistence may have been recovered through the recorder's public API
            // while this bootstrap was inactive.
            MarkFinalTelemetryPersistenceComplete();
            return;
        }

        if (isActiveAndEnabled
            && gameObject.activeInHierarchy
            && finalTelemetryPersistencePending
            && !finalTelemetryRetryExhausted
            && finalTelemetryRetryCoroutine == null)
        {
            finalTelemetryRetryCoroutine = StartCoroutine(RetryFinalTelemetryPersistence());
        }
    }

    private IEnumerator RetryFinalTelemetryPersistence()
    {
        while (finalTelemetryRetryAttemptsCompleted < c_FinalTelemetryRetryAttempts)
        {
            yield return new WaitForSecondsRealtime(c_FinalTelemetryRetryIntervalSeconds);

            if (telemetryRecorder == null || !telemetryRecorder.HasPendingSessionEnd)
            {
                MarkFinalTelemetryPersistenceComplete(false);
                yield break;
            }

            int attempt = ++finalTelemetryRetryAttemptsCompleted;
            if (telemetryRecorder.TryEndSession("telemetry_retry", false))
            {
                Debug.Log($"[DroneTelemetry] Final persistence recovered on realtime retry {attempt}.", this);
                MarkFinalTelemetryPersistenceComplete(false);
                yield break;
            }
        }

        finalTelemetryRetryCoroutine = null;
        finalTelemetryRetryExhausted = true;
        finalTelemetryPersistencePending = telemetryRecorder != null && telemetryRecorder.HasPendingSessionEnd;
        if (finalTelemetryPersistencePending)
        {
            Debug.LogError(
                $"[DroneTelemetry] Final persistence still pending after {c_FinalTelemetryRetryAttempts} realtime retries: " +
                telemetryRecorder.LastPersistenceError,
                this);
        }
    }

    private void MarkFinalTelemetryPersistenceComplete(bool stopActiveRetryCoroutine = true)
    {
        Coroutine retryCoroutine = finalTelemetryRetryCoroutine;
        finalTelemetryRetryCoroutine = null;
        if (stopActiveRetryCoroutine && retryCoroutine != null)
        {
            // Persistence may be recovered synchronously by readiness or another
            // recorder caller while this coroutine is sleeping. Do not leave that
            // waiter scheduled to retry a session which is already complete.
            StopCoroutine(retryCoroutine);
        }

        finalTelemetryPersistencePending = false;
        finalTelemetryRetryExhausted = false;
        finalTelemetryRetryAttemptsCompleted = 0;
    }

    public void ResetDemo()
    {
        TryResetDemo();
    }

    public bool TryResetDemo()
    {
        if (resetQueued || gameplayStopped)
        {
            return false;
        }

        // Batch shutdown is a per-run freeze, not a permanent stop latch. The old
        // components remain disabled until the queued reset replaces their world.
        batchRunShutdownApplied = false;
        resetQueued = true;
        lastResetSucceeded = false;
        lastResetError = string.Empty;
        resetCoroutine = StartCoroutine(ResetDemoNextFrame());
        return true;
    }

    public void CancelQueuedReset(string reason)
    {
        if (!resetQueued)
        {
            return;
        }

        if (resetCoroutine != null)
        {
            StopCoroutine(resetCoroutine);
            resetCoroutine = null;
        }

        // Replay cancellation restores the retained runtime instead of freezing or
        // retiring it. Initial startup still freezes any partial runtime.
        if (replayRuntimeState != null)
        {
            RollbackPreparedNewGame();
        }
        else
        {
            missionComplete = true;
            FreezeSimulationComponents();
        }
        CompleteReset(
            false,
            string.IsNullOrWhiteSpace(reason) ? "Queued reset was cancelled." : reason);
    }

    private IEnumerator ResetDemoNextFrame()
    {
        yield return null;

        if (gameplayStopped)
        {
            FailReset("Simulation stopped before the queued reset could begin.");
            yield break;
        }

        // Finalizing the old session is the transaction boundary. Do not retire
        // any world objects until its frozen end rows have been persisted. The
        // initial attempt plus these retries matches final-session recovery.
        bool telemetryPersisted;
        try
        {
            telemetryPersisted = EndTelemetrySession("reset", false);
        }
        catch (Exception exception)
        {
            FailResetAfterException("finalizing the prior telemetry session", exception, false);
            yield break;
        }

        for (int attempt = 1;
             !telemetryPersisted && attempt <= c_FinalTelemetryRetryAttempts;
             attempt++)
        {
            yield return new WaitForSecondsRealtime(c_FinalTelemetryRetryIntervalSeconds);
            try
            {
                telemetryPersisted = EndTelemetrySession("reset", false);
            }
            catch (Exception exception)
            {
                FailResetAfterException("retrying prior telemetry persistence", exception, false);
                yield break;
            }
        }

        if (!telemetryPersisted)
        {
            string detail = telemetryRecorder != null
                ? telemetryRecorder.LastPersistenceError
                : "telemetry recorder is unavailable";
            FailReset($"Prior telemetry session could not be finalized: {detail}");
            yield break;
        }

        bool telemetryStarted;
        try
        {
            finalTelemetryRetryExhausted = false;
            ClearRuntimeState();
            resetRuntimeSetupExceptionInjection?.Invoke("world");
            BuildWorld();
            resetRuntimeSetupExceptionInjection?.Invoke("swarm");
            BuildSwarm();
            BuildDebugRenderer();
            //新たにUIを作成するためコメントアウト↓
            //BuildUi();

            communicationHub.ResetCommunicationMemory();
            communicationHub.RefreshNodes();
            CaptureRuntimeConfig();
            resetRuntimeSetupExceptionInjection?.Invoke("telemetry");
            telemetryStarted = TryBeginTelemetrySession();
        }
        catch (Exception exception)
        {
            FailResetAfterException("building the replacement runtime", exception, true);
            yield break;
        }

        if (!telemetryStarted)
        {
            string detail = telemetryRecorder != null
                ? telemetryRecorder.LastPersistenceError
                : "telemetry recorder is unavailable";
            FailReset($"Telemetry session could not be started: {detail}");
            yield break;
        }

        CompleteReset(true, string.Empty);
    }

    private void FailResetAfterException(string operation, Exception exception, bool clearPartialRuntime)
    {
        // Never let an exception strand resetQueued. Build failures may leave a
        // partially registered swarm, so first freeze it and then best-effort clear it.
        missionComplete = true;
        try
        {
            // Before replay runtime capture these are retained result components;
            // touching them would make rollback observably lossy.
            if (replayRuntimeState == null || replayRuntimeState.RuntimeCaptured)
            {
                FreezeSimulationComponents();
            }
        }
        catch (Exception cleanupException)
        {
            Debug.LogException(cleanupException, this);
        }

        if (clearPartialRuntime)
        {
            try
            {
                ClearRuntimeState();
                missionComplete = true;
            }
            catch (Exception cleanupException)
            {
                Debug.LogException(cleanupException, this);
            }
        }

        if (replayRuntimeState != null)
        {
            RollbackPreparedNewGame();
        }
        string error = $"Exception while {operation}: {exception.GetType().Name}: {exception.Message}";
        CompleteReset(false, error);
        Debug.LogException(exception, this);
        Debug.LogError($"[DroneSwarmDemoBootstrap] World reset aborted. {error}", this);
    }

    private void FailReset(string error)
    {
        // A reset can fail after the new world has been built. Freeze both old and
        // new runtime components so a caller that remains on its start screen never
        // leaves an unrecorded simulation running in the background.
        missionComplete = true;
        if (replayRuntimeState == null || replayRuntimeState.RuntimeCaptured)
        {
            FreezeSimulationComponents();
        }
        if (replayRuntimeState != null)
        {
            RollbackPreparedNewGame();
        }
        CompleteReset(false, error);
        Debug.LogError($"[DroneSwarmDemoBootstrap] World reset aborted. {lastResetError}", this);
    }

    private void CompleteReset(bool succeeded, string error)
    {
        lastResetSucceeded = succeeded;
        lastResetError = succeeded ? string.Empty : (error ?? string.Empty);
        resetQueued = false;
        resetCoroutine = null;

        Action<bool, string> handlers = ResetCompleted;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<bool, string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(succeeded, lastResetError);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
    }

    /*
    Assets\Scripts\ScriptControl\ScriptsControler.csのvoid Start()にて実行
    private void Start() => ResetDemo();
    */

    private void Update()
    {
        if (gameplayStopped || batchRunShutdownApplied)
        {
            return;
        }

        ApplyInspectorChanges();
        UpdateTelemetryMilestones();
        CheckMissionComplete();
        UpdateTelemetryTimeout();
        UpdateStatusText();
    }

    private void OnEnable()
    {
        // Batch shutdown does not set gameplayStopped, and a disable may have stopped
        // the retry coroutine while its frozen end rows were still pending.
        if (IsFinalTelemetryPersistencePending)
        {
            StartFinalTelemetryRetryIfNeeded();
        }
    }

    private void OnDisable()
    {
        HandleLifecycleShutdown("bootstrap_disabled");
    }

    private void OnDestroy()
    {
        HandleLifecycleShutdown("bootstrap_destroyed");
        // The recorder retains any frozen pending rows for external recovery, but a
        // destroyed bootstrap must not leave its per-run context attached to them.
        ClearTelemetryBatchContext();
        ReleaseDroneCameraTextures();
    }

    private void HandleLifecycleShutdown(string endReason)
    {
        // Coroutines are not a reliable cleanup mechanism after deactivation. In
        // particular, a queued reset must not rebuild the world after this component
        // is re-enabled, and a final telemetry retry must not be started from here.
        StopAllCoroutines();
        finalTelemetryRetryCoroutine = null;
        resetCoroutine = null;

        if (IsBatchRun || batchRunShutdownApplied)
        {
            // A temporary component/parent disable must not turn a batch freeze
            // into the permanent interactive-game stop latch.
            ShutdownBatchRun(endReason, false);
            return;
        }

        if (resetQueued)
        {
            // Complete the cancellation as a terminal lifecycle path. ScriptsControl's
            // synchronous completion handler restores either the unstopped initial-start
            // boundary or the stopped replay boundary. Continuing into StopSimulation
            // would overwrite the initial boundary and permanently reject its retry.
            CancelQueuedReset("Bootstrap was disabled before the queued reset completed.");
            return;
        }

        // StopSimulation is idempotent. An explicit game-flow stop remains
        // authoritative because TryEndSession preserves the first frozen end rows and
        // a stopped bootstrap does not end them again. Established gameplay still
        // reaches this path and stops normally.
        StopSimulation(endReason, false);
    }

    private void FreezeSimulationComponents()
    {
        foreach (var droneExplorer in explorers)
        {
            if (droneExplorer == null)
            {
                continue;
            }

            droneExplorer.StopAfterMissionComplete();
            droneExplorer.enabled = false;

            if (droneExplorer.TryGetComponent<DronePathFollower>(out var pathFollower))
            {
                pathFollower.enabled = false;
            }
            if (droneExplorer.TryGetComponent<DroneNativePathFollower>(out var nativePathFollower))
            {
                nativePathFollower.enabled = false;
            }
            if (droneExplorer.TryGetComponent<DroneGridSensor>(out var sensor))
            {
                sensor.enabled = false;
            }
            if (droneExplorer.TryGetComponent<DroneMissionEndReporter>(out var endReporter))
            {
                endReporter.enabled = false;
            }
            if (droneExplorer.TryGetComponent<DroneLocalAvoidanceMotor>(out var avoidanceMotor))
            {
                avoidanceMotor.enabled = false;
            }
            if (droneExplorer.TryGetComponent<DroneAltitudeKeeper>(out var altitudeKeeper))
            {
                altitudeKeeper.enabled = false;
            }
        }

        if (communicationHub != null)
        {
            communicationHub.enabled = false;
        }
        if (commandRoutePlanner != null)
        {
            commandRoutePlanner.enabled = false;
        }

        Explorer ownedHumanExplorer = ResolveOwnedExplorer();
        if (ownedHumanExplorer != null)
        {
            ownedHumanExplorer.StopAfterFoundByDrone();
            ownedHumanExplorer.enabled = false;
        }
    }

    private void OnValidate()
    {
        width = Mathf.Max(4, width);
        depth = Mathf.Max(4, depth);
        cellSize = Mathf.Max(0.25f, cellSize);
        droneCount = Mathf.Clamp(droneCount, 1, 12);
        sensorRadius = Mathf.Clamp(sensorRadius, 1, 8);
        communicationRadius = Mathf.Max(0f, communicationRadius);
        droneSpeed = Mathf.Max(0f, droneSpeed);
        telemetryTimeoutSeconds = Mathf.Max(0f, telemetryTimeoutSeconds);
#if UNITY_EDITOR
        droneModelPrefab ??= AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Drone.fbx");
#endif
    }

    private void ClearRuntimeState()
    {
        if (replayRuntimeState != null && !replayRuntimeState.RuntimeCaptured)
        {
            ReplayRuntimeState retained = replayRuntimeState;
            retained.SpawnedObjects.AddRange(spawnedObjects);
            retained.Explorers.AddRange(explorers);
            retained.CommunicationNodes.AddRange(communicationNodes);
            retained.DirectTargetReporterIds.UnionWith(directTargetReporterIds);
            retained.DroneCameras.AddRange(droneCameras);
            retained.DroneCameraViews.AddRange(droneCameraViews);
            retained.DroneCameraTextures.AddRange(droneCameraTextures);
            retained.World = world;
            retained.CommunicationHub = communicationHub;
            retained.CommandRoutePlanner = commandRoutePlanner;
            retained.CommandState = commandState;
            retained.DebugRenderer = debugRenderer;
            retained.RuntimeCaptured = true;

            spawnedObjects.Clear();
            explorers.Clear();
            communicationNodes.Clear();
            directTargetReporterIds.Clear();
            droneCameras.Clear();
            droneCameraViews.Clear();
            droneCameraTextures.Clear();
            world = null;
            communicationHub = null;
            commandRoutePlanner = null;
            commandState = null;
            debugRenderer = null;
        }
        else
        {
            ClearSpawnedObjects();
            explorers.Clear();
            communicationNodes.Clear();
            directTargetReporterIds.Clear();
            droneCameras.Clear();
            droneCameraViews.Clear();
            ReleaseDroneCameraTextures();
        }

        missionComplete = false;
    }

    private void BuildWorld()
    {
        var result = new DroneDemoWorldBuilder(RegisterSpawned).Build(width, depth, cellSize, c_ObstacleLayer, c_TargetLayer, c_NonSensedLayer);
        world = result.World;
        width = world.Width;
        depth = world.Depth;
        communicationHub = result.CommunicationHub;
    }

    private void BuildSwarm()
    {
        var result = new DroneDemoSwarmSpawner(RegisterSpawned).Build(
            world,
            width,
            depth,
            droneCount,
            ResolveDroneModelPrefab(),
            sensorRadius,
            communicationRadius,
            plannerType,
            c_NonSensedLayer,
            HandleDroneTargetSensed);

        explorers.AddRange(result.Explorers);
        communicationNodes.AddRange(result.CommunicationNodes);
        commandState = result.CommandState;
        commandRoutePlanner = result.CommandRoutePlanner;
        ApplyDroneSpeed();

        /*
        DroneCameraFeed.csで生成しているため、一時停止
        if (!IsBatchRun)
        {
            BuildDroneCameras();
        }
        */
    }

    private void BuildDebugRenderer()
    {
        var rendererObject = Spawn("Drone Swarm Debug Renderer");
        ApplyLayerIfExists(rendererObject, c_GridWorldLayerName);

        debugRenderer = rendererObject.AddComponent<DroneSwarmDebugRenderer>();
        debugRenderer.Configure(world, commandState, explorers);
    }

    private void ApplyLayerIfExists(GameObject target, string layerName)
    {
        if (target == null || string.IsNullOrWhiteSpace(layerName))
        {
            return;
        }

        int layer = LayerMask.NameToLayer(layerName);

        if (layer < 0)
        {
            Debug.LogWarning($"{layerName} レイヤーが見つかりません。GridWorld表示物を専用Layerにできません。");
            return;
        }

        target.layer = layer;
    }

    private void BuildUi()
    {
        var ui = new DroneDemoUiBuilder(RegisterSpawned).Build(new DroneDemoUiBuildConfig
        {
            PlannerLabel = PlannerLabel(),
            MapViewLabel = MapViewLabel(),
            PlannerClicked = HandlePlannerClicked,
            MapViewClicked = HandleMapViewClicked,
            DroneViewClicked = HandleDroneViewClicked,
            ResetClicked = ResetDemo,
        });

        statusText = ui.StatusText;
        plannerButtonText = ui.PlannerButtonText;
        mapViewButtonText = ui.MapViewButtonText;
        droneViewButtonText = ui.DroneViewButtonText;
        droneCameraGrid = ui.DroneCameraGrid;
        ConfigureDroneCameraViews();
    }

    private void HandlePlannerClicked(Text clickedButtonText)
    {
        plannerType = plannerType == DroneNative.PlannerType.AStar
            ? DroneNative.PlannerType.ThetaStar
            : DroneNative.PlannerType.AStar;
        if (clickedButtonText != null)
        {
            clickedButtonText.text = PlannerLabel();
        }
        ApplyPlannerType();
        appliedPlannerType = plannerType;
    }

    private void HandleMapViewClicked()
    {
        if (debugRenderer == null)
        {
            return;
        }

        debugRenderer.CycleMapViewMode();
        UpdateMapViewButtons();
    }

    private void HandleDroneViewClicked()
    {
        if (debugRenderer == null)
        {
            return;
        }

        debugRenderer.SelectNextDrone();
        UpdateMapViewButtons();
    }

    private void ApplyPlannerType()
    {
        foreach (var explorer in explorers)
        {
            if (explorer != null)
            {
                explorer.PlannerType = plannerType;
            }
        }

        if (commandRoutePlanner != null)
        {
            commandRoutePlanner.PlannerType = plannerType;
        }
    }

    private void ApplySensorRadius()
    {
        foreach (var camera in droneCameras)
        {
            if (camera != null)
            {
                ApplyDroneCameraRange(camera);
            }
        }

        foreach (var explorer in explorers)
        {
            if (explorer != null && explorer.TryGetComponent<DroneGridSensor>(out var sensor))
            {
                sensor.SensorRadius = sensorRadius;
                sensor.SenseNow();
            }
        }
    }

    private void ApplyCommunicationRadius()
    {
        foreach (var node in communicationNodes)
        {
            if (node != null)
            {
                node.SetCommunicationRadius(communicationRadius);
            }
        }
    }

    private void ApplyDroneSpeed()
    {
        foreach (var explorer in explorers)
        {
            if (explorer != null && explorer.TryGetComponent<DronePathFollower>(out var follower))
            {
                follower.MoveSpeed = droneSpeed;
            }
        }
    }

    private void BuildDroneCameras()
    {
        droneCameras.Clear();
        for (int i = 0; i < explorers.Count; i++)
        {
            var explorer = explorers[i];
            if (explorer == null)
            {
                continue;
            }

            var cameraObject = new GameObject($"Drone {i + 1:00} Camera");
            RegisterSpawned(cameraObject);
            cameraObject.transform.SetParent(explorer.transform, false);
            cameraObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = true;
            camera.clearFlags = CameraClearFlags.Skybox;
            ApplyDroneCameraRange(camera);
            if (explorer.TryGetComponent<DroneGridSensor>(out var sensor))
            {
                sensor.ConfigureCamera(camera);
            }
            droneCameras.Add(camera);
        }
    }

    private void ApplyDroneCameraRange(Camera camera)
    {
        float cameraHeight = DroneCameraHeightForRange();
        camera.transform.localPosition = new Vector3(0f, cameraHeight, 0f);
        camera.fieldOfView = c_DroneCameraFieldOfView;
        camera.nearClipPlane = 0.03f;
        camera.farClipPlane = cameraHeight + Mathf.Max(2f, cellSize * 2f);
    }

    private float DroneCameraHeightForRange()
    {
        float groundHalfRange = Mathf.Max(cellSize, sensorRadius * cellSize);
        float halfFovRadians = c_DroneCameraFieldOfView * 0.5f * Mathf.Deg2Rad;
        return Mathf.Max(1.5f, groundHalfRange / Mathf.Tan(halfFovRadians));
    }

    private void ConfigureDroneCameraViews()
    {
        if (droneCameraGrid == null)
        {
            return;
        }

        ReleaseDroneCameraTextures();
        droneCameraViews.Clear();

        var grid = droneCameraGrid.GetComponent<GridLayoutGroup>();
        int cameraCount = droneCameras.Count;
        if (cameraCount == 0)
        {
            return;
        }

        int columns = Mathf.CeilToInt(Mathf.Sqrt(cameraCount));
        int rows = Mathf.CeilToInt(cameraCount / (float)columns);
        Vector2 spacing = grid != null ? grid.spacing : Vector2.zero;
        Vector2 gridSize = droneCameraGrid.rect.size;
        if (gridSize.x <= 0f || gridSize.y <= 0f)
        {
            gridSize = droneCameraGrid.sizeDelta;
        }

        Vector2 cellSize = new Vector2(
            (gridSize.x - spacing.x * (columns - 1)) / columns,
            (gridSize.y - spacing.y * (rows - 1)) / rows);

        if (grid != null)
        {
            grid.constraintCount = columns;
            grid.cellSize = cellSize;
        }

        int textureWidth = Mathf.Max(1, Mathf.RoundToInt(cellSize.x));
        int textureHeight = Mathf.Max(1, Mathf.RoundToInt(cellSize.y));
        for (int i = 0; i < cameraCount; i++)
        {
            var camera = droneCameras[i];
            if (camera == null)
            {
                continue;
            }

            var texture = new RenderTexture(textureWidth, textureHeight, 16, RenderTextureFormat.ARGB32);
            texture.name = $"Drone {i + 1:00} Camera Texture";
            droneCameraTextures.Add(texture);
            camera.enabled = true;
            camera.targetTexture = texture;

            var viewObject = new GameObject($"Drone {i + 1:00} Camera View", typeof(RectTransform), typeof(RawImage));
            viewObject.transform.SetParent(droneCameraGrid, false);
            var image = viewObject.GetComponent<RawImage>();
            image.color = Color.white;
            image.texture = texture;
            droneCameraViews.Add(image);
        }
    }

    private void ReleaseDroneCameraTextures()
    {
        for (int i = 0; i < droneCameras.Count; i++)
        {
            if (droneCameras[i] != null)
            {
                droneCameras[i].targetTexture = null;
            }
        }

        for (int i = 0; i < droneCameraTextures.Count; i++)
        {
            if (droneCameraTextures[i] != null)
            {
                droneCameraTextures[i].Release();
                Destroy(droneCameraTextures[i]);
            }
        }

        droneCameraTextures.Clear();
    }

    private void ApplyInspectorChanges()
    {
        if (!runtimeConfigInitialized || resetQueued)
        {
            return;
        }

        if (appliedDroneCount != droneCount)
        {
            ResetDemo();
            return;
        }

        if (appliedSensorRadius != sensorRadius)
        {
            ApplySensorRadius();
            appliedSensorRadius = sensorRadius;
        }

        if (!Mathf.Approximately(appliedCommunicationRadius, communicationRadius))
        {
            ApplyCommunicationRadius();
            appliedCommunicationRadius = communicationRadius;
        }

        if (!Mathf.Approximately(appliedDroneSpeed, droneSpeed))
        {
            ApplyDroneSpeed();
            appliedDroneSpeed = droneSpeed;
        }

        if (appliedPlannerType != plannerType)
        {
            ApplyPlannerType();
            if (plannerButtonText != null)
            {
                plannerButtonText.text = PlannerLabel();
            }
            appliedPlannerType = plannerType;
        }
    }

    private void CaptureRuntimeConfig()
    {
        appliedDroneCount = droneCount;
        appliedSensorRadius = sensorRadius;
        appliedCommunicationRadius = communicationRadius;
        appliedDroneSpeed = droneSpeed;
        appliedPlannerType = plannerType;
        runtimeConfigInitialized = true;
    }

    private void UpdateStatusText()
    {
        if (statusText == null)
        {
            return;
        }

        // Throttling to avoid building a new multiline string and the associated GC per frame
        if (Time.time < nextStatusTextUpdateAt)
        {
            return;
        }

        nextStatusTextUpdateAt = Time.time + 0.2f;

        int knownCells = commandState != null && commandState.LocalMap != null
            ? commandState.LocalMap.CountKnownCells()
            : 0;
        int directFinders = directTargetReporterIds.Count;
        string swarmTarget = directFinders > 0 ? $"yes ({directFinders})" : "no";
        string commandTarget = commandRoutePlanner != null && commandRoutePlanner.HasTargetReport ? "yes" : "no";
        string route = commandRoutePlanner != null && commandRoutePlanner.HasRoute ? commandRoutePlanner.RouteCount.ToString() : "none";
        int links = communicationHub != null ? communicationHub.ActiveLinks.Count : 0;
        string mapView = debugRenderer != null ? debugRenderer.CurrentMapViewLabel : "Merged";
        int informedDrones = CountBestKnownTargetInformedDrones();
        string mission = missionComplete ? "complete" : "active";
        statusText.text = $"Planner: {plannerType}  Map: {mapView}\nCam range: {sensorRadius}  Comms: {communicationRadius:0.0}\nDrones: {droneCount}  Speed: {droneSpeed:0.0}  Links: {links}\nCameras: {droneCameras.Count}\nCommand known: {knownCells}\nTarget found: {swarmTarget}  Command: {commandTarget}\nInformed drones: {informedDrones}/{droneCount}  Mission: {mission}\nRoute: {route}";
    }

    private string PlannerLabel() => plannerType == DroneNative.PlannerType.ThetaStar ? "Theta*" : "A*";

    private GameObject ResolveDroneModelPrefab()
    {
#if UNITY_EDITOR
        droneModelPrefab ??= AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Drone.fbx");
#endif
        return droneModelPrefab;
    }

    private void HandleDroneTargetSensed(DroneGridSensor sensor, DroneNative.DroneVec3i cell)
    {
        int reporterId = -1;
        if (sensor != null && sensor.TryGetComponent<DroneSwarmAgentState>(out var agentState))
        {
            reporterId = agentState.DroneId;
            if (directTargetReporterIds.Add(reporterId))
            {
                nextStatusTextUpdateAt = 0f;
            }
        }

        RecordTelemetryTargetFound(reporterId, cell);
        StopHumanAtTarget(sensor, cell);
    }

    private void StopHumanAtTarget(DroneGridSensor sensor, DroneNative.DroneVec3i cell)
    {
        if (sensor == null || sensor.World == null)
        {
            return;
        }

        foreach (Explorer human in GetAliveExplorers())
        {
            if (human == null || human.IsStoppedAfterDroneFound)
            {
                continue;
            }

            var humanCell = sensor.World.WorldToGrid(human.transform.position);
            if (humanCell.x == cell.x && humanCell.z == cell.z)
            {
                human.StopAfterFoundByDrone();
            }
        }
    }

    private IEnumerable<Explorer> GetAliveExplorers()
    {
        Explorer ownedExplorer = ResolveOwnedExplorer();
        if (ownedExplorer != null)
        {
            yield return ownedExplorer;
        }
    }

    private Explorer ResolveOwnedExplorer()
    {
        // A configured ScriptsControl is authoritative so replay/batch staging can
        // replace its Explorer without leaving this bootstrap attached to the old one.
        if (scriptsControlOwner != null
            && scriptsControlOwner.droneSwarmDemoBootstrap == this)
        {
            // Null is meaningful while a staged world is between Explorers; do not
            // fall back to a stale serialized reference during that transition.
            return scriptsControlOwner.explorer;
        }

        if (humanExplorer != null)
        {
            return humanExplorer;
        }

        // Compatibility for scenes serialized before humanExplorer was introduced:
        // inspect only explicit bootstrap -> ScriptsControl links. Never infer an
        // Explorer from a scene-wide Explorer search, because another simulation may
        // own it. Multiple different configured Explorers are intentionally treated
        // as ambiguous and left untouched.
        ScriptsControl resolvedOwner = null;
        Explorer resolvedExplorer = null;
        foreach (ScriptsControl candidate in FindObjectsByType<ScriptsControl>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            if (candidate == null
                || candidate.droneSwarmDemoBootstrap != this
                || candidate.explorer == null)
            {
                continue;
            }

            if (resolvedExplorer != null && candidate.explorer != resolvedExplorer)
            {
                return null;
            }

            resolvedOwner = candidate;
            resolvedExplorer = candidate.explorer;
        }

        if (resolvedOwner != null)
        {
            scriptsControlOwner = resolvedOwner;
        }

        return resolvedExplorer;
    }

    private bool TryBeginTelemetrySession()
    {
        if (!telemetryEnabled)
        {
            return true;
        }

        var recorder = EnsureTelemetryRecorder();
        if (recorder == null)
        {
            Debug.LogError("[DroneTelemetry] Cannot start telemetry because no recorder is available.", this);
            return false;
        }

        if (recorder.TryBeginSession(BuildTelemetryConfig()))
        {
            return true;
        }

        Debug.LogError($"[DroneTelemetry] Session start failed: {recorder.LastPersistenceError}", this);
        return false;
    }

    private DroneMissionTelemetryRecorder EnsureTelemetryRecorder()
    {
        if (telemetryRecorder != null)
        {
            return telemetryRecorder;
        }

        telemetryRecorder = GetComponent<DroneMissionTelemetryRecorder>();
        if (telemetryRecorder == null)
        {
            telemetryRecorder = gameObject.AddComponent<DroneMissionTelemetryRecorder>();
        }

        return telemetryRecorder;
    }

    private bool EndTelemetrySession(string endReason, bool completed)
    {
        if (telemetryRecorder == null
            || (!telemetryRecorder.HasActiveSession && !telemetryRecorder.HasPendingSessionEnd))
        {
            // A recorder end can be recovered through its public API or batch
            // readiness. In either case, no genuine pending transaction remains,
            // so retire any stale bootstrap gate and its sleeping retry waiter.
            MarkFinalTelemetryPersistenceComplete();
            return true;
        }

        bool persisted = telemetryRecorder.TryEndSession(endReason, completed);
        if (!persisted)
        {
            finalTelemetryPersistencePending = telemetryRecorder.HasPendingSessionEnd;
            Debug.LogError($"[DroneTelemetry] Session end remains pending: {telemetryRecorder.LastPersistenceError}", this);
        }
        else
        {
            MarkFinalTelemetryPersistenceComplete();
        }

        return persisted;
    }

    private void RecordTelemetryTargetFound(int reporterId, DroneNative.DroneVec3i cell)
    {
        if (!telemetryEnabled || telemetryRecorder == null || !telemetryRecorder.HasActiveSession)
        {
            return;
        }

        telemetryRecorder.RecordTargetFound(reporterId, cell);
    }

    private void UpdateTelemetryMilestones()
    {
        if (!telemetryEnabled || telemetryRecorder == null || !telemetryRecorder.HasActiveSession)
        {
            return;
        }

        if (commandState != null
            && commandState.LocalMap != null
            && commandState.LocalMap.TryGetEarliestTargetReport(out var commandReport))
        {
            telemetryRecorder.RecordCommandNotified(
                commandReport.ReporterId,
                commandReport.Cell,
                CountBestKnownTargetInformedDrones());
        }

        foreach (var explorer in explorers)
        {
            if (explorer == null
                || !explorer.TryGetComponent<DroneSwarmAgentState>(out var state)
                || state.LocalMap == null
                || !state.LocalMap.TryGetEarliestTargetReport(out var report))
            {
                continue;
            }

            telemetryRecorder.RecordDroneInformed(state.DroneId, report.Cell, state.TargetInformedDroneCount);
        }

        int bestInformedCount = CountBestKnownTargetInformedDrones();
        if (bestInformedCount >= droneCount && TryGetEarliestKnownTargetReport(out var earliestReport))
        {
            telemetryRecorder.RecordAllDronesInformed(
                bestInformedCount,
                earliestReport.Cell);
        }
    }

    private void UpdateTelemetryTimeout()
    {
        if (!telemetryEnabled
            || telemetryTimeoutSeconds <= 0f
            || telemetryRecorder == null
            || !telemetryRecorder.HasActiveSession
            || telemetryRecorder.ElapsedSeconds < telemetryTimeoutSeconds)
        {
            return;
        }

        if (IsBatchRun)
        {
            // A telemetry timeout is a run boundary, not merely a recording
            // boundary. Freeze every runtime component before the runner observes
            // the inactive session and starts its delay or replacement-world work.
            ShutdownBatchRun("timeout", false);
            return;
        }

        // Let an active interactive game flow choose its established search/return
        // timeout reason and result state. If no flow owns this simulation, still
        // stop it coherently rather than leaving an unrecorded world running.
        GameFlowController gameFlow = FindAnyObjectByType<GameFlowController>();
        if (gameFlow == null || !gameFlow.TryHandleTelemetryTimeout() || !gameplayStopped)
        {
            StopSimulation("timeout", false);
        }
    }

    private void RecordTelemetryMissionComplete()
    {
        if (!telemetryEnabled || telemetryRecorder == null || !telemetryRecorder.HasActiveSession)
        {
            return;
        }

        var targetCell = TryGetEarliestKnownTargetReport(out var earliestReport)
            ? earliestReport.Cell
            : default;
        telemetryRecorder.RecordMissionComplete(CountBestKnownTargetInformedDrones(), targetCell);
    }

    private DroneMissionSessionConfig BuildTelemetryConfig()
    {
        var config = new DroneMissionSessionConfig
        {
            BatchId = telemetryBatchId,
            HasBatchRunIndex = telemetryHasBatchRunIndex,
            BatchRunIndex = telemetryBatchRunIndex,
            HasBatchConfigurationIndex = telemetryHasBatchConfigurationIndex,
            BatchConfigurationIndex = telemetryBatchConfigurationIndex,
            HasBatchRepeatIndex = telemetryHasBatchRepeatIndex,
            BatchRepeatIndex = telemetryBatchRepeatIndex,
            HasRandomSeed = telemetryHasRandomSeed,
            RandomSeed = telemetryRandomSeed,
            SceneName = SceneManager.GetActiveScene().name,
            PlannerType = plannerType.ToString(),
            DroneCount = droneCount,
            SensorRadius = sensorRadius,
            CommunicationRadius = communicationRadius,
            DroneSpeed = droneSpeed,
            GridWidth = world != null ? world.Width : width,
            GridDepth = world != null ? world.Depth : depth,
            CellSize = world != null ? world.CellSize : cellSize,
        };

        if (TryGetTargetStartPose(out var targetCell, out var targetPosition))
        {
            config.HasTargetStartCell = true;
            config.TargetStartCell = targetCell;
            config.HasTargetStartWorldPosition = true;
            config.TargetStartWorldPosition = targetPosition;

            if (TryCalculateNearestDroneStartDistance(
                targetCell,
                targetPosition,
                out float nearestCells,
                out float nearestWorld))
            {
                config.HasNearestDroneStartDistance = true;
                config.NearestDroneStartDistanceCells = nearestCells;
                config.NearestDroneStartDistanceWorld = nearestWorld;
            }
        }

        return config;
    }

    private bool TryGetTargetStartPose(out DroneNative.DroneVec3i targetCell, out Vector3 targetPosition)
    {
        targetCell = default;
        targetPosition = default;

        if (world == null)
        {
            return false;
        }

        foreach (Explorer human in GetAliveExplorers())
        {
            if (human == null)
            {
                continue;
            }

            targetPosition = human.transform.position;
            targetCell = world.WorldToGrid(targetPosition);
            return true;
        }

        var playerArmature = GameObject.Find("PlayerArmature");
        if (playerArmature != null)
        {
            targetPosition = playerArmature.transform.position;
            targetCell = world.WorldToGrid(targetPosition);
            return true;
        }

        var target = GameObject.Find("Target");
        if (target != null)
        {
            targetPosition = target.transform.position;
            targetCell = world.WorldToGrid(targetPosition);
            return true;
        }

        targetCell = new DroneNative.DroneVec3i(
            Mathf.Clamp(world.Width - 3, 0, world.Width - 1),
            0,
            Mathf.Clamp(world.Depth - 3, 0, world.Depth - 1));
        targetPosition = world.GridToWorld(targetCell, 0.25f);
        return true;
    }

    private bool TryCalculateNearestDroneStartDistance(
        DroneNative.DroneVec3i targetCell,
        Vector3 targetPosition,
        out float nearestCells,
        out float nearestWorld)
    {
        nearestCells = 0f;
        nearestWorld = 0f;
        if (world == null || explorers.Count == 0)
        {
            return false;
        }

        float bestCellDistanceSquared = float.PositiveInfinity;
        float bestWorldDistance = float.PositiveInfinity;
        foreach (var explorer in explorers)
        {
            if (explorer == null)
            {
                continue;
            }

            var droneCell = world.WorldToGrid(explorer.transform.position);
            bestCellDistanceSquared = Mathf.Min(
                bestCellDistanceSquared,
                DroneGridMath.SquaredDistance(droneCell, targetCell));
            bestWorldDistance = Mathf.Min(
                bestWorldDistance,
                Vector3.Distance(explorer.transform.position, targetPosition));
        }

        if (float.IsPositiveInfinity(bestCellDistanceSquared) || float.IsPositiveInfinity(bestWorldDistance))
        {
            return false;
        }

        nearestCells = Mathf.Sqrt(bestCellDistanceSquared);
        nearestWorld = bestWorldDistance;
        return true;
    }

    private void CheckMissionComplete()
    {
        if (missionComplete || directTargetReporterIds.Count == 0 || explorers.Count == 0)
        {
            return;
        }

        if (!TryGetFirstTargetFinder(out var firstFinderState, out var firstReport)
            || !firstFinderState.AllExpectedDronesTargetInformed)
        {
            return;
        }

        if (!AllExplorersKnowTargetFound()
            || !AllExplorersReturnedNearTarget(firstFinderState, firstReport))
        {
            return;
        }

        CompleteNaturalMission();
    }

    private void CompleteNaturalMission()
    {
        missionComplete = true;
        UpdateTelemetryMilestones();
        RecordTelemetryMissionComplete();
        if (!EndTelemetrySession("mission_complete", true))
        {
            // Natural completion uses the same realtime, bounded recovery path as
            // an explicit stop. The active telemetry session remains visible to
            // batch runners until recovery succeeds or becomes terminal.
            StartFinalTelemetryRetryIfNeeded();
        }

        foreach (var explorer in explorers)
        {
            if (explorer != null)
            {
                explorer.StopAfterMissionComplete();
            }
        }
    }

    private bool AllExplorersKnowTargetFound()
    {
        foreach (var explorer in explorers)
        {
            if (explorer == null
                || !explorer.TryGetComponent<DroneSwarmAgentState>(out var state)
                || !state.KnowsTargetFound)
            {
                return false;
            }
        }

        return explorers.Count > 0;
    }

    private int CountBestKnownTargetInformedDrones()
    {
        int bestCount = 0;
        foreach (var explorer in explorers)
        {
            if (explorer != null && explorer.TryGetComponent<DroneSwarmAgentState>(out var state))
            {
                bestCount = Mathf.Max(bestCount, state.TargetInformedDroneCount);
            }
        }

        if (commandState != null)
        {
            bestCount = Mathf.Max(bestCount, commandState.TargetInformedDroneCount);
        }

        return bestCount;
    }

    private bool TryGetEarliestKnownTargetReport(out DroneTargetReport earliestReport)
    {
        earliestReport = default;
        bool found = false;

        if (commandState != null
            && commandState.LocalMap != null
            && commandState.LocalMap.TryGetEarliestTargetReport(out var commandReport))
        {
            earliestReport = commandReport;
            found = true;
        }

        foreach (var explorer in explorers)
        {
            if (explorer == null
                || !explorer.TryGetComponent<DroneSwarmAgentState>(out var state)
                || state.LocalMap == null
                || !state.LocalMap.TryGetEarliestTargetReport(out var report))
            {
                continue;
            }

            if (!found
                || report.ObservedAt < earliestReport.ObservedAt
                || (Mathf.Approximately(report.ObservedAt, earliestReport.ObservedAt)
                    && report.ReporterId < earliestReport.ReporterId))
            {
                earliestReport = report;
                found = true;
            }
        }

        return found;
    }

    private bool AllExplorersReturnedNearTarget(DroneSwarmAgentState firstFinderState, DroneTargetReport firstReport)
    {
        if (firstFinderState == null || world == null)
        {
            return false;
        }

        float returnRadius = GetMissionReturnRadius();
        Vector3 firstFinderPosition = firstFinderState.transform.position;
        Vector3 targetAnchorPosition = world.GridToWorld(firstReport.Cell, firstFinderPosition.y);

        foreach (var explorer in explorers)
        {
            if (explorer == null
                || !explorer.TryGetComponent<DroneSwarmAgentState>(out var state)
                || !state.KnowsTargetFound)
            {
                return false;
            }

            Vector3 explorerPosition = explorer.transform.position;
            if (IsNearOnXZ(explorerPosition, targetAnchorPosition, returnRadius)
                || IsNearOnXZ(explorerPosition, firstFinderPosition, returnRadius)
                || (explorer.HasTargetAnchorCell && explorer.IsNearTargetAnchor(returnRadius)))
            {
                continue;
            }

            return false;
        }

        return explorers.Count > 0;
    }

    private float GetMissionReturnRadius()
    {
        float minimumCellRadius = world != null
            ? world.CellSize * 1.5f
            : cellSize * 1.5f;
        return Mathf.Max(minimumCellRadius, communicationRadius);
    }

    private static bool IsNearOnXZ(Vector3 left, Vector3 right, float radius)
    {
        float clampedRadius = Mathf.Max(0f, radius);
        float dx = left.x - right.x;
        float dz = left.z - right.z;
        return dx * dx + dz * dz <= clampedRadius * clampedRadius;
    }

    private bool TryGetFirstTargetFinder(out DroneSwarmAgentState firstFinderState, out DroneTargetReport firstReport)
    {
        firstFinderState = null;
        firstReport = default;
        bool found = false;

        foreach (var explorer in explorers)
        {
            if (explorer == null
                || !explorer.TryGetComponent<DroneSwarmAgentState>(out var state)
                || state.LocalMap == null
                || !state.LocalMap.TryGetEarliestTargetReport(out var report))
            {
                continue;
            }

            if (!found
                || report.ObservedAt < firstReport.ObservedAt
                || (Mathf.Approximately(report.ObservedAt, firstReport.ObservedAt)
                    && report.ReporterId < firstReport.ReporterId))
            {
                firstReport = report;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        foreach (var explorer in explorers)
        {
            if (explorer != null
                && explorer.TryGetComponent<DroneSwarmAgentState>(out var state)
                && state.DroneId == firstReport.ReporterId)
            {
                firstFinderState = state;
                return true;
            }
        }

        return false;
    }

    private string MapViewLabel() => debugRenderer != null ? debugRenderer.CurrentMapViewLabel : "Merged";

    private void UpdateMapViewButtons()
    {
        if (mapViewButtonText != null)
        {
            mapViewButtonText.text = MapViewLabel();
        }

        if (droneViewButtonText != null)
        {
            droneViewButtonText.text = "Next";
        }
    }

    private GameObject Spawn(string objectName)
    {
        var instance = new GameObject(objectName);
        RegisterSpawned(instance);
        return instance;
    }

    private void RegisterSpawned(GameObject instance) => spawnedObjects.Add(instance);

    private void ClearSpawnedObjects()
    {
        for (int i = spawnedObjects.Count - 1; i >= 0; i--)
        {
            if (spawnedObjects[i] != null)
            {
                spawnedObjects[i].SetActive(false);
                Destroy(spawnedObjects[i]);
            }
        }

        spawnedObjects.Clear();
    }

    //0630追加分
    /* StartSetting画面の参照用 */
    public int GridWidth => width;
    public int GridDepth => depth;
    public float CellSize => cellSize;
    public int DroneCount => droneCount;
    public int SensorRadius => sensorRadius;
    public float CommunicationRadius => communicationRadius;
    public float DroneSpeed => droneSpeed;
    /* 外部入力用 */
    public void ConfigureStartSettings(
    int newDroneCount,
    float newCommunicationRadius,
    float newDroneSpeed,
    int newSensorRadius,
    float newCellSize
    )
    {
        droneCount = Mathf.Clamp(newDroneCount, 1, 12);
        droneSpeed = Mathf.Max(0f, newDroneSpeed);
        communicationRadius = Mathf.Max(0f, newCommunicationRadius);
        sensorRadius = Mathf.Clamp(newSensorRadius, 1, 8);
        cellSize = Mathf.Max(1f, newCellSize);
    }
    public void ConfigureGridFromTerrain(
    Terrain terrain,
    float newCellSize,
    int maxGridWidth = 256,
    int maxGridDepth = 256
    )
    {
        if (terrain == null || terrain.terrainData == null)
        {
            Debug.LogWarning("Terrain が未設定のため、Drone Grid を自動設定できません。");
            return;
        }

        cellSize = Mathf.Max(0.25f, newCellSize);

        Vector3 size = terrain.terrainData.size;

        width = Mathf.Clamp(
            Mathf.RoundToInt(size.x / cellSize),
            4,
            maxGridWidth
        );

        depth = Mathf.Clamp(
            Mathf.RoundToInt(size.z / cellSize),
            4,
            maxGridDepth
        );
    }
}

