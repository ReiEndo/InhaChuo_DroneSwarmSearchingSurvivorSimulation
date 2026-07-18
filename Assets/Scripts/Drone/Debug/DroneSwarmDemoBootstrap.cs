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
    [SerializeField] private RuntimeAnimatorController droneAnimationController;
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

    /// <summary>Keeps ScriptsControl authoritative when replay replaces its Explorer.</summary>
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
    /// Freezes a batch run without latching the interactive stop state or replacing
    /// an already-pending telemetry end.
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

        // Preserve the bounded retry for an already-pending end.
        bool telemetryPersisted = EndTelemetrySession(endReason, completed);

        missionComplete = true;
        batchRunShutdownApplied = true;
        FreezeSimulationComponents();
        if (!telemetryPersisted)
        {
            StartFinalTelemetryRetryIfNeeded();
        }
    }

    /// <summary>Creates telemetry and recovers pending ends before batch reads.</summary>
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

            // Synchronous recovery must also clear the bootstrap retry gate.
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

    /// <summary>Ends an interactive mission while preserving its result world.</summary>
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

    /// <summary>Checks replay eligibility without changing the retained result world.</summary>
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

    /// <summary>Activates replay while retaining result drones for rollback.</summary>
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

    public void CancelPreparedNewGame()
    {
        newGamePreflightPassed = false;
    }

    /// <summary>Restores the stopped replay boundary after activation fails.</summary>
    public void RestoreStoppedReplayBoundary()
    {
        newGamePreflightPassed = false;
        gameplayStopped = true;
        missionComplete = true;
        FreezeSimulationComponents();
    }

    /// <summary>Rolls back initial startup without entering the stopped replay state.</summary>
    public void RollbackInitialStartup()
    {
        newGamePreflightPassed = false;
        ClearRuntimeState();
        runtimeConfigInitialized = false;
        gameplayStopped = false;
        missionComplete = false;
        batchRunShutdownApplied = false;
    }

    private void StartFinalTelemetryRetryIfNeeded()
    {
        // Pending state must survive coroutine cancellation on deactivation.
        if (telemetryRecorder != null && telemetryRecorder.HasPendingSessionEnd)
        {
            finalTelemetryPersistencePending = true;
        }
        else if (telemetryRecorder != null && finalTelemetryPersistencePending)
        {
            // Recovery may have completed while this bootstrap was inactive.
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
            // Cancel a sleeping retry when another caller completes recovery.
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

        // Batch shutdown freezes one run without setting the permanent stop latch.
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

        // Replay restores retained runtime; initial startup freezes partial runtime.
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

        // Persist the old session's frozen rows before retiring its world.
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
        // Always clear resetQueued and freeze any partially registered swarm.
        missionComplete = true;
        try
        {
            // Preserve retained result components until replay state is captured.
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
        // Freeze any runtime built before the reset failed.
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
        // Resume final-row retries interrupted by deactivation.
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
        // Detach destroyed bootstrap context without discarding recoverable rows.
        ClearTelemetryBatchContext();
        ReleaseDroneCameraTextures();
    }

    private void HandleLifecycleShutdown(string endReason)
    {
        // Deactivation must synchronously cancel queued world rebuilds.
        StopAllCoroutines();
        finalTelemetryRetryCoroutine = null;
        resetCoroutine = null;

        if (IsBatchRun || batchRunShutdownApplied)
        {
            // Temporary batch deactivation must not set the interactive stop latch.
            ShutdownBatchRun(endReason, false);
            return;
        }

        if (resetQueued)
        {
            // Cancellation restores the correct initial or replay boundary synchronously.
            CancelQueuedReset("Bootstrap was disabled before the queued reset completed.");
            return;
        }

        // Failed initial setup has no committed runtime and must remain retryable.
        if (!runtimeConfigInitialized && replayRuntimeState == null
            && !IsTelemetrySessionActive)
        {
            gameplayStopped = false;
            missionComplete = false;
            return;
        }

        // TryEndSession preserves an earlier explicit game-flow stop.
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
        var spawner = new DroneDemoSwarmSpawner(RegisterSpawned)
        {
            DroneAnimationController = droneAnimationController,
        };
        var result = spawner.Build(
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
        // ScriptsControl remains authoritative when staging replaces its Explorer.
        if (scriptsControlOwner != null
            && scriptsControlOwner.droneSwarmDemoBootstrap == this)
        {
            // Null during replacement must not fall back to a stale Explorer.
            return scriptsControlOwner.explorer;
        }

        if (humanExplorer != null)
        {
            return humanExplorer;
        }

        // Legacy resolution uses explicit owners; a scene-wide search could cross simulations.
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
            // Clear stale retry state after recovery through another entry point.
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
            // Freeze the run before the batch runner observes inactive telemetry.
            ShutdownBatchRun("timeout", false);
            return;
        }

        // Interactive GameFlow chooses the timeout result when it owns the run.
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
            // Natural completion uses the same bounded recovery as an explicit stop.
            StartFinalTelemetryRetryIfNeeded();
        }

        foreach (var explorer in explorers)
        {
            if (explorer != null)
            {
                explorer.StopAfterMissionComplete();
            }
        }

        // Notify directly to avoid an Update-order race with DroneMissionEndReporter.
        GameFlowController gameFlow = FindAnyObjectByType<GameFlowController>(FindObjectsInactive.Include);
        gameFlow?.NotifyAllDronesReturned();
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

    public int GridWidth => width;
    public int GridDepth => depth;
    public float CellSize => cellSize;
    public int DroneCount => droneCount;
    public int SensorRadius => sensorRadius;
    public float CommunicationRadius => communicationRadius;
    public float DroneSpeed => droneSpeed;
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

