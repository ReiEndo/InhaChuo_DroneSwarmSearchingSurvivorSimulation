using System;
using System.Collections;
using UnityEngine;
using Random = UnityEngine.Random;

public sealed class DroneMissionBatchRunner : MonoBehaviour
{
    private const float DefaultRunHardTimeoutSeconds = 180f;

    [Header("References")]
    [SerializeField] private DroneSwarmDemoBootstrap bootstrap;
    [SerializeField] private TerrainGenerator terrainGenerator;
    [SerializeField] private ForestSpawner forestSpawner;
    [SerializeField] private Explorer explorer;
    [SerializeField] private DroneMissionTelemetryRecorder telemetryRecorder;

    [Header("Run Control")]
    [SerializeField] private bool autoStartOnPlay = false;
    [SerializeField] private float startDelaySeconds = 0.5f;
    [SerializeField] private float betweenRunsDelaySeconds = 0.25f;
    [SerializeField] private float sessionStartTimeoutSeconds = 10f;
    [SerializeField] private float perSessionTimeoutSeconds = 120f;
    [Tooltip("Realtime hard limit for one batch run. This remains effective if the bootstrap's telemetry timeout is disabled or its Update loop stops running.")]
    [SerializeField] private float runHardTimeoutSeconds = DefaultRunHardTimeoutSeconds;
    [SerializeField] private int repetitionsPerConfiguration = 3;
    [SerializeField] private bool stopBatchIfSessionFailsToStart = true;

    [Header("World Reset Per Run")]
    [SerializeField] private bool regenerateTerrainEachRun = true;
    [SerializeField] private bool clearForestBeforeSpawning = true;
    [SerializeField] private bool spawnForestEachRun = true;
    [SerializeField] private bool respawnExplorerEachRun = true;

    [Header("Random Seeds")]
    [SerializeField] private bool useDeterministicSeeds = true;
    [SerializeField] private int baseSeed = 10000;

    [Header("Experiment Parameters")]
    [SerializeField] private int droneCount = 5;
    [SerializeField] private int sensorRadius = 2;
    [SerializeField] private float communicationRadius = 3.25f;
    [SerializeField] private float droneSpeed = 3f;
    [SerializeField] private DroneNative.PlannerType plannerType = DroneNative.PlannerType.AStar;

    private Coroutine batchCoroutine;
    private Coroutine telemetryCleanupCoroutine;
    private bool telemetryCleanupPending;
    private bool telemetryBatchContextCleared;
    private bool cancelRequested;
    private bool batchCleanupCompleted;
    private bool worldPreparationSucceeded;
    private bool setupFailed;
    private bool telemetryPersistenceFailed;
    private bool runHardTimeoutReached;

    public bool AutoStartOnPlay => autoStartOnPlay;
    public bool IsRunning => batchCoroutine != null || telemetryCleanupPending;

    private IEnumerator Start()
    {
        if (!autoStartOnPlay)
        {
            yield break;
        }

        yield return new WaitForSeconds(Mathf.Max(0f, startDelaySeconds));
        RunBatch();
    }

    private void OnValidate()
    {
        startDelaySeconds = Mathf.Max(0f, startDelaySeconds);
        betweenRunsDelaySeconds = Mathf.Max(0f, betweenRunsDelaySeconds);
        sessionStartTimeoutSeconds = Mathf.Max(0.1f, sessionStartTimeoutSeconds);
        perSessionTimeoutSeconds = Mathf.Max(0f, perSessionTimeoutSeconds);
        runHardTimeoutSeconds = GetValidRunHardTimeoutSeconds();
        repetitionsPerConfiguration = Mathf.Max(1, repetitionsPerConfiguration);

        ClampExperimentParameters();
    }

    [ContextMenu("Run Batch")]
    public void RunBatch()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[DroneBatch] Batch runs can only be started in Play Mode.", this);
            return;
        }

        if (batchCoroutine != null || telemetryCleanupPending)
        {
            Debug.LogWarning("[DroneBatch] A batch or telemetry cleanup is already running.", this);
            return;
        }

        ResolveReferences();
        if (bootstrap == null)
        {
            Debug.LogError("[DroneBatch] Cannot run batch without a DroneSwarmDemoBootstrap reference.", this);
            return;
        }

        if (!bootstrap.TryPrepareTelemetryForBatch(out telemetryRecorder))
        {
            string detail = telemetryRecorder != null
                ? telemetryRecorder.LastPersistenceError
                : "telemetry recorder/bootstrap is unavailable";
            Debug.LogError($"[DroneBatch] Cannot start because telemetry storage is not ready: {detail}", this);
            return;
        }

        cancelRequested = false;
        batchCleanupCompleted = false;
        telemetryCleanupPending = false;
        telemetryBatchContextCleared = false;
        setupFailed = false;
        telemetryPersistenceFailed = false;
        batchCoroutine = StartCoroutine(RunBatchCoroutine());
    }

    [ContextMenu("Cancel Batch")]
    public void CancelBatch()
    {
        if (batchCoroutine == null)
        {
            return;
        }

        StopAndCleanupBatch();
    }

    private IEnumerator RunBatchCoroutine()
    {
        string batchId = GenerateBatchId();
        int totalRuns = CalculateTotalRunCount();
        int configurationIndex = 1;
        int runIndex = 0;

        ClampExperimentParameters();
        try
        {
            Debug.Log(
                $"[DroneBatch] Starting batch {batchId} with {totalRuns} runs. " +
                $"planner={plannerType}, drones={droneCount}, sensor={sensorRadius}, " +
                $"comms={communicationRadius:0.###}, speed={droneSpeed:0.###}",
                this);

            for (int repeatIndex = 1; repeatIndex <= repetitionsPerConfiguration; repeatIndex++)
            {
                if (cancelRequested)
                {
                    yield break;
                }

                runIndex++;
                int seed = GetSeedForRun(runIndex);
                Random.InitState(seed);

                Debug.Log(
                    $"[DroneBatch] Run {runIndex}/{totalRuns}: " +
                    $"planner={plannerType}, drones={droneCount}, sensor={sensorRadius}, " +
                    $"comms={communicationRadius:0.###}, speed={droneSpeed:0.###}, repeat={repeatIndex}, seed={seed}",
                    this);

                // The hard limit includes synchronous world preparation.
                runHardTimeoutReached = false;
                float runHardDeadline = Time.realtimeSinceStartup + GetValidRunHardTimeoutSeconds();
                yield return PrepareWorldForRun();

                if (cancelRequested)
                {
                    yield break;
                }
                if (CheckTelemetryPersistenceFailure(batchId, runIndex))
                {
                    yield break;
                }
                if (TryReachRunHardDeadline(runHardDeadline, batchId, runIndex))
                {
                    yield break;
                }
                if (!worldPreparationSucceeded)
                {
                    setupFailed = true;
                    Debug.LogError($"[DroneBatch] Run {runIndex}/{totalRuns} world setup failed; aborting batch.", this);
                    yield break;
                }

                bootstrap.ConfigureExperiment(
                    droneCount,
                    sensorRadius,
                    communicationRadius,
                    droneSpeed,
                    plannerType,
                    perSessionTimeoutSeconds,
                    true);
                bootstrap.SetTelemetryBatchContext(
                    batchId,
                    runIndex,
                    configurationIndex,
                    repeatIndex,
                    true,
                    seed);
                if (!bootstrap.TryResetDemo())
                {
                    setupFailed = true;
                    Debug.LogError($"[DroneBatch] Run {runIndex}/{totalRuns} world reset could not be queued.", this);
                    yield break;
                }

                // Reset owns bounded persistence retries for the old session.
                while (!cancelRequested
                    && bootstrap.IsResetQueued
                    && Time.realtimeSinceStartup < runHardDeadline)
                {
                    yield return null;
                }

                // Cancellation and reset failure take precedence over same-frame timeout.
                if (cancelRequested)
                {
                    yield break;
                }
                if (!bootstrap.IsResetQueued && !bootstrap.LastResetSucceeded)
                {
                    if (!CheckTelemetryPersistenceFailure(batchId, runIndex))
                    {
                        setupFailed = true;
                    }
                    Debug.LogError(
                        $"[DroneBatch] Run {runIndex}/{totalRuns} world reset failed: {bootstrap.LastResetError}",
                        this);
                    yield break;
                }
                if (CheckTelemetryPersistenceFailure(batchId, runIndex))
                {
                    yield break;
                }
                if (TryReachRunHardDeadline(runHardDeadline, batchId, runIndex))
                {
                    yield break;
                }

                bool sessionStarted = false;
                float startDeadline = Time.realtimeSinceStartup + sessionStartTimeoutSeconds;
                while (!cancelRequested
                    && Time.realtimeSinceStartup <= startDeadline
                    && Time.realtimeSinceStartup < runHardDeadline)
                {
                    // A failed session_start never becomes an active session.
                    if (CheckTelemetryPersistenceFailure(batchId, runIndex))
                    {
                        break;
                    }

                    if (bootstrap.IsTelemetrySessionActive)
                    {
                        sessionStarted = true;
                        break;
                    }

                    yield return null;
                }

                CheckTelemetryPersistenceFailure(batchId, runIndex);
                if (telemetryPersistenceFailed || cancelRequested)
                {
                    yield break;
                }
                if (TryReachRunHardDeadline(runHardDeadline, batchId, runIndex))
                {
                    yield break;
                }

                if (!sessionStarted && bootstrap.IsTelemetrySessionActive)
                {
                    sessionStarted = true;
                }

                if (!sessionStarted)
                {
                    Debug.LogWarning($"[DroneBatch] Run {runIndex}/{totalRuns} did not start a telemetry session.", this);
                    if (stopBatchIfSessionFailsToStart)
                    {
                        setupFailed = true;
                        yield break;
                    }

                    continue;
                }

                yield return WaitForActiveSessionToEnd(batchId, runIndex, runHardDeadline);

                CheckTelemetryPersistenceFailure(batchId, runIndex);
                if (telemetryPersistenceFailed || cancelRequested || runHardTimeoutReached)
                {
                    yield break;
                }

                if (betweenRunsDelaySeconds > 0f && repeatIndex < repetitionsPerConfiguration)
                {
                    yield return new WaitForSeconds(betweenRunsDelaySeconds);
                }
            }

            Debug.Log($"[DroneBatch] Batch {batchId} complete. Runs: {runIndex}/{totalRuns}.", this);
        }
        finally
        {
            CleanupBatch(
                telemetryPersistenceFailed ? "telemetry_write_failed" :
                setupFailed ? "setup_failed" :
                cancelRequested ? "batch_cancelled" :
                runHardTimeoutReached ? "batch_timeout" : "batch_stopped");
        }
    }

    private void OnEnable()
    {
        ResumePendingTelemetryCleanup();
    }

    private void OnDisable()
    {
        StopAndCleanupBatch();
        StopTelemetryCleanupWaiter();
    }

    private void OnDestroy()
    {
        StopAndCleanupBatch();
        StopTelemetryCleanupWaiter();
        // Release batch context without discarding recorder-owned pending rows.
        ClearBatchContextOnce();
        telemetryCleanupPending = false;
    }

    private void StopAndCleanupBatch()
    {
        if (batchCoroutine == null)
        {
            return;
        }

        cancelRequested = true;
        StopCoroutine(batchCoroutine);
        batchCoroutine = null;
        CleanupBatch("batch_cancelled");
    }

    private void CleanupBatch(string activeSessionEndReason)
    {
        if (batchCleanupCompleted)
        {
            return;
        }
        if (telemetryCleanupPending)
        {
            ResumePendingTelemetryCleanup();
            return;
        }

        if (bootstrap != null)
        {
            bootstrap.ShutdownBatchRun(activeSessionEndReason, false);
        }

        batchCoroutine = null;
        if (bootstrap != null
            && bootstrap.IsFinalTelemetryPersistencePending
            && !bootstrap.HasTerminalTelemetryPersistenceFailure)
        {
            // The flag survives deactivation even when the coroutine handle does not.
            telemetryCleanupPending = true;
            ResumePendingTelemetryCleanup();
            return;
        }

        CompleteBatchCleanup();
    }

    private void ResumePendingTelemetryCleanup()
    {
        if (!telemetryCleanupPending || batchCleanupCompleted)
        {
            return;
        }
        if (bootstrap == null
            || !bootstrap.IsFinalTelemetryPersistencePending
            || bootstrap.HasTerminalTelemetryPersistenceFailure)
        {
            CompleteBatchCleanup();
            return;
        }
        if (isActiveAndEnabled && gameObject.activeInHierarchy && telemetryCleanupCoroutine == null)
        {
            telemetryCleanupCoroutine = StartCoroutine(WaitForTelemetryCleanup());
        }
    }

    private void StopTelemetryCleanupWaiter()
    {
        if (telemetryCleanupCoroutine != null)
        {
            StopCoroutine(telemetryCleanupCoroutine);
            telemetryCleanupCoroutine = null;
        }
    }

    private IEnumerator WaitForTelemetryCleanup()
    {
        while (bootstrap != null
            && bootstrap.IsFinalTelemetryPersistencePending
            && !bootstrap.HasTerminalTelemetryPersistenceFailure)
        {
            yield return null;
        }

        telemetryCleanupCoroutine = null;
        CompleteBatchCleanup();
    }

    private void CompleteBatchCleanup()
    {
        if (batchCleanupCompleted)
        {
            return;
        }

        batchCleanupCompleted = true;
        telemetryCleanupPending = false;
        telemetryCleanupCoroutine = null;
        ClearBatchContextOnce();
        batchCoroutine = null;
    }

    private void ClearBatchContextOnce()
    {
        if (telemetryBatchContextCleared)
        {
            return;
        }

        telemetryBatchContextCleared = true;
        if (bootstrap != null)
        {
            bootstrap.ClearTelemetryBatchContext();
        }
    }

    private bool TryReachRunHardDeadline(float runHardDeadline, string batchId, int runIndex)
    {
        if (Time.realtimeSinceStartup < runHardDeadline)
        {
            return false;
        }

        runHardTimeoutReached = true;
        Debug.LogWarning(
            $"[DroneBatch] Batch {batchId}, run {runIndex} reached its {GetValidRunHardTimeoutSeconds():0.###}s realtime hard deadline.",
            this);
        if (bootstrap != null)
        {
            bootstrap.ShutdownBatchRun("batch_timeout", false);
        }

        return true;
    }

    private IEnumerator WaitForActiveSessionToEnd(string batchId, int runIndex, float runHardDeadline)
    {
        while (!cancelRequested
            && bootstrap != null
            && (bootstrap.IsTelemetrySessionActive
                || (bootstrap.IsFinalTelemetryPersistencePending
                    && !bootstrap.HasTerminalTelemetryPersistenceFailure)))
        {
            // Do not replace the world until frozen final rows are durable or terminal.
            if (CheckTelemetryPersistenceFailure(batchId, runIndex))
            {
                yield break;
            }

            if (TryReachRunHardDeadline(runHardDeadline, batchId, runIndex))
            {
                yield break;
            }

            yield return null;
        }
    }

    private bool CheckTelemetryPersistenceFailure(string batchId, int runIndex)
    {
        if (telemetryRecorder == null)
        {
            telemetryRecorder = FindAnyObjectByType<DroneMissionTelemetryRecorder>();
        }

        bool finalRetryInProgress = bootstrap != null
            && bootstrap.IsFinalTelemetryPersistencePending
            && !bootstrap.HasTerminalTelemetryPersistenceFailure;
        telemetryPersistenceFailed = telemetryRecorder == null
            || (telemetryRecorder.HasPersistenceFailure && !finalRetryInProgress);
        if (telemetryPersistenceFailed)
        {
            string detail = telemetryRecorder != null
                ? telemetryRecorder.LastPersistenceError
                : "telemetry recorder is unavailable";
            Debug.LogError(
                $"[DroneBatch] Batch {batchId}, run {runIndex} telemetry persistence failed: {detail}",
                this);
        }

        return telemetryPersistenceFailed;
    }

    private IEnumerator PrepareWorldForRun()
    {
        worldPreparationSucceeded = false;
        ResolveReferences();

        if ((clearForestBeforeSpawning || spawnForestEachRun) && forestSpawner == null)
        {
            Debug.LogError("[DroneBatch] ForestSpawner is required by the world reset settings.", this);
            yield break;
        }
        if (regenerateTerrainEachRun && terrainGenerator == null)
        {
            Debug.LogError("[DroneBatch] TerrainGenerator is required by the world reset settings.", this);
            yield break;
        }

        if (clearForestBeforeSpawning)
        {
            forestSpawner.ClearSpawnedTrees();
            yield return null;
        }

        if (regenerateTerrainEachRun && !terrainGenerator.TryGenerateTerrain())
        {
            Debug.LogError("[DroneBatch] Terrain generation failed.", this);
            yield break;
        }

        if (spawnForestEachRun && !forestSpawner.TrySpawnTrees())
        {
            Debug.LogError("[DroneBatch] Forest generation failed.", this);
            yield break;
        }

        if (respawnExplorerEachRun)
        {
            if (explorer == null || !explorer.ExplorerSpawner())
            {
                yield break;
            }
        }
        else if (explorer == null || !explorer.IsReadyForMission)
        {
            Debug.LogError("[DroneBatch] Explorer is not ready for the run.", this);
            yield break;
        }

        Physics.SyncTransforms();
        yield return null;
        worldPreparationSucceeded = true;
    }

    private void ResolveReferences()
    {
        if (bootstrap == null)
        {
            bootstrap = FindAnyObjectByType<DroneSwarmDemoBootstrap>();
        }

        if (terrainGenerator == null)
        {
            terrainGenerator = FindAnyObjectByType<TerrainGenerator>();
        }

        if (forestSpawner == null)
        {
            forestSpawner = FindAnyObjectByType<ForestSpawner>();
        }

        if (explorer == null)
        {
            explorer = FindAnyObjectByType<Explorer>();
        }

        if (telemetryRecorder == null)
        {
            telemetryRecorder = FindAnyObjectByType<DroneMissionTelemetryRecorder>();
        }
    }

    private int CalculateTotalRunCount()
    {
        return repetitionsPerConfiguration;
    }

    private int GetSeedForRun(int oneBasedRunIndex)
    {
        if (useDeterministicSeeds)
        {
            return baseSeed + oneBasedRunIndex - 1;
        }

        return Guid.NewGuid().GetHashCode();
    }

    private float GetValidRunHardTimeoutSeconds()
    {
        if (float.IsNaN(runHardTimeoutSeconds) || float.IsInfinity(runHardTimeoutSeconds))
        {
            return DefaultRunHardTimeoutSeconds;
        }

        return Mathf.Max(1f, runHardTimeoutSeconds);
    }

    private void ClampExperimentParameters()
    {
        droneCount = Mathf.Clamp(droneCount, 1, DroneSwarmDemoBootstrap.MaximumDroneCount);
        sensorRadius = Mathf.Clamp(sensorRadius, 1, 8);
        communicationRadius = Mathf.Max(0f, communicationRadius);
        droneSpeed = Mathf.Max(0f, droneSpeed);
    }

    private static string GenerateBatchId()
    {
        return $"batch-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
    }
}
