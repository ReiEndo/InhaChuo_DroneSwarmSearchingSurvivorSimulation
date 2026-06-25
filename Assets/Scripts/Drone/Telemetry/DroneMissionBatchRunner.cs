using System;
using System.Collections;
using UnityEngine;
using Random = UnityEngine.Random;

public sealed class DroneMissionBatchRunner : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private DroneSwarmDemoBootstrap bootstrap;
    [SerializeField] private TerrainGenerator terrainGenerator;
    [SerializeField] private ForestSpawner forestSpawner;
    [SerializeField] private Explorer explorer;

    [Header("Run Control")]
    [SerializeField] private bool autoStartOnPlay = false;
    [SerializeField] private float startDelaySeconds = 0.5f;
    [SerializeField] private float betweenRunsDelaySeconds = 0.25f;
    [SerializeField] private float sessionStartTimeoutSeconds = 10f;
    [SerializeField] private float perSessionTimeoutSeconds = 120f;
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
    private bool cancelRequested;

    public bool AutoStartOnPlay => autoStartOnPlay;
    public bool IsRunning => batchCoroutine != null;

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

        if (batchCoroutine != null)
        {
            Debug.LogWarning("[DroneBatch] A batch is already running.", this);
            return;
        }

        ResolveReferences();
        if (bootstrap == null)
        {
            Debug.LogError("[DroneBatch] Cannot run batch without a DroneSwarmDemoBootstrap reference.", this);
            return;
        }

        cancelRequested = false;
        batchCoroutine = StartCoroutine(RunBatchCoroutine());
    }

    [ContextMenu("Cancel Batch")]
    public void CancelBatch()
    {
        cancelRequested = true;
    }

    private IEnumerator RunBatchCoroutine()
    {
        string batchId = GenerateBatchId();
        int totalRuns = CalculateTotalRunCount();
        int configurationIndex = 1;
        int runIndex = 0;

        ClampExperimentParameters();
        Debug.Log(
            $"[DroneBatch] Starting batch {batchId} with {totalRuns} runs. " +
            $"planner={plannerType}, drones={droneCount}, sensor={sensorRadius}, " +
            $"comms={communicationRadius:0.###}, speed={droneSpeed:0.###}",
            this);

        for (int repeatIndex = 1; repeatIndex <= repetitionsPerConfiguration; repeatIndex++)
        {
            if (cancelRequested)
            {
                yield return FinishCancelledBatch(batchId);
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

            yield return PrepareWorldForRun();

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
            bootstrap.ResetDemo();

            bool sessionStarted = false;
            float startDeadline = Time.time + sessionStartTimeoutSeconds;
            while (!cancelRequested && Time.time <= startDeadline)
            {
                if (bootstrap.IsTelemetrySessionActive)
                {
                    sessionStarted = true;
                    break;
                }

                yield return null;
            }

            if (!sessionStarted)
            {
                Debug.LogWarning($"[DroneBatch] Run {runIndex}/{totalRuns} did not start a telemetry session.", this);
                if (stopBatchIfSessionFailsToStart)
                {
                    yield return FinishCancelledBatch(batchId);
                    yield break;
                }

                continue;
            }

            while (!cancelRequested && bootstrap.IsTelemetrySessionActive)
            {
                yield return null;
            }

            if (cancelRequested)
            {
                yield return FinishCancelledBatch(batchId);
                yield break;
            }

            if (betweenRunsDelaySeconds > 0f && repeatIndex < repetitionsPerConfiguration)
            {
                yield return new WaitForSeconds(betweenRunsDelaySeconds);
            }
        }

        bootstrap.ClearTelemetryBatchContext();
        Debug.Log($"[DroneBatch] Batch {batchId} complete. Runs: {runIndex}/{totalRuns}.", this);
        batchCoroutine = null;
    }

    private IEnumerator FinishCancelledBatch(string batchId)
    {
        if (bootstrap != null)
        {
            bootstrap.EndActiveTelemetrySession("batch_cancelled", false);
            bootstrap.ClearTelemetryBatchContext();
        }

        Debug.LogWarning($"[DroneBatch] Batch {batchId} cancelled.", this);
        batchCoroutine = null;
        yield return null;
    }

    private IEnumerator PrepareWorldForRun()
    {
        ResolveReferences();

        if (clearForestBeforeSpawning && forestSpawner != null)
        {
            forestSpawner.ClearSpawnedTrees();
            yield return null;
        }

        if (regenerateTerrainEachRun && terrainGenerator != null)
        {
            terrainGenerator.GenerateTerrain();
        }

        if (spawnForestEachRun && forestSpawner != null)
        {
            forestSpawner.SpawnTrees();
        }

        if (respawnExplorerEachRun && explorer != null)
        {
            explorer.ExplorerSpawner();
        }

        Physics.SyncTransforms();
        yield return null;
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

    private void ClampExperimentParameters()
    {
        droneCount = Mathf.Clamp(droneCount, 1, 12);
        sensorRadius = Mathf.Clamp(sensorRadius, 1, 8);
        communicationRadius = Mathf.Max(0f, communicationRadius);
        droneSpeed = Mathf.Max(0f, droneSpeed);
    }

    private static string GenerateBatchId()
    {
        return $"batch-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
    }
}
