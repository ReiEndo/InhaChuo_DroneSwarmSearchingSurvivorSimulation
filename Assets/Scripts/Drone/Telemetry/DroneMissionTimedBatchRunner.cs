using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Runs drone missions in wall-clock timed segments separated by long cooldown
/// rests, repeating until a target number of <em>valid</em> missions
/// (<see cref="validEndReason"/>) have been written to the telemetry summary CSV.
///
/// Unlike <see cref="DroneMissionBatchRunner"/>, which fires a fixed number of
/// missions back-to-back with short gaps, this runner is designed for sustained
/// data collection on hardware that needs to cool down between running segments:
///
///   run segment (runSegmentMinutes) -> rest (restMinutes) -> run segment -> ...
///   ... until (validMissionsCollectedThisBatch >= targetValidMissions).
///
/// During rest, rendering is disabled (all cameras + the debug renderer are
/// turned off) and the simulation is paused (<see cref="Time.timeScale"/> = 0)
/// so the GPU/CPU can idle, then everything is restored for the next segment.
/// </summary>
public sealed class DroneMissionTimedBatchRunner : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private DroneSwarmDemoBootstrap bootstrap;
    [SerializeField] private TerrainGenerator terrainGenerator;
    [SerializeField] private ForestSpawner forestSpawner;
    [SerializeField] private Explorer explorer;
    [SerializeField] private DroneMissionTelemetryRecorder telemetryRecorder;

    [Header("Auto Start")]
    [SerializeField] private bool autoStartOnPlay = false;
    [SerializeField] private float startDelaySeconds = 0.5f;

    [Header("Run Segment (wall-clock)")]
    [SerializeField] private float runSegmentMinutes = 5f;
    [SerializeField] private float betweenMissionsDelaySeconds = 0.25f;
    [SerializeField] private float sessionStartTimeoutSeconds = 10f;
    [SerializeField] private float perSessionTimeoutSeconds = 120f;
    [Tooltip("If true, an in-flight mission is allowed to finish naturally after the segment budget elapses (its own timeout still applies). If false, it is cut off at the segment boundary with end_reason = segment_timeout.")]
    [SerializeField] private bool allowActiveMissionToFinishAfterSegment = true;

    [Header("Cooldown Rest")]
    [SerializeField] private float restMinutes = 10f;
    [Tooltip("Disable all cameras and the debug renderer during rest to cut GPU load.")]
    [SerializeField] private bool disableRenderingDuringRest = true;
    [Tooltip("Set Time.timeScale = 0 during rest so the simulation idles, restored on the next segment.")]
    [SerializeField] private bool pauseSimulationDuringRest = true;

    [Header("Stop Condition")]
    [Tooltip("Stop once this many valid missions have been collected during this batch.")]
    [SerializeField] private int targetValidMissions = 100;
    [Tooltip("end_reason that counts as a valid mission. Default: mission_complete.")]
    [SerializeField] private string validEndReason = "mission_complete";
    [Tooltip("Hard wall-clock safety cap in hours (0 = unlimited). Stops the whole batch even if the target has not been reached.")]
    [SerializeField] private float maxWallClockHours = 0f;

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

    // Rendering-disable bookkeeping (rest periods).
    private readonly List<Camera> disabledCameras = new();
    private DroneSwarmDebugRenderer disabledDebugRenderer;
    private float savedTimeScale = 1f;

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
        runSegmentMinutes = Mathf.Max(0.1f, runSegmentMinutes);
        betweenMissionsDelaySeconds = Mathf.Max(0f, betweenMissionsDelaySeconds);
        sessionStartTimeoutSeconds = Mathf.Max(0.1f, sessionStartTimeoutSeconds);
        perSessionTimeoutSeconds = Mathf.Max(0f, perSessionTimeoutSeconds);
        restMinutes = Mathf.Max(0f, restMinutes);
        targetValidMissions = Mathf.Max(1, targetValidMissions);
        maxWallClockHours = Mathf.Max(0f, maxWallClockHours);

        droneCount = Mathf.Clamp(droneCount, 1, 12);
        sensorRadius = Mathf.Clamp(sensorRadius, 1, 8);
        communicationRadius = Mathf.Max(0f, communicationRadius);
        droneSpeed = Mathf.Max(0f, droneSpeed);
    }

    [ContextMenu("Run Batch")]
    public void RunBatch()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[TimedBatch] Batch runs can only be started in Play Mode.", this);
            return;
        }

        if (batchCoroutine != null)
        {
            Debug.LogWarning("[TimedBatch] A batch is already running.", this);
            return;
        }

        ResolveReferences();
        if (bootstrap == null)
        {
            Debug.LogError("[TimedBatch] Cannot run batch without a DroneSwarmDemoBootstrap reference.", this);
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
        float batchStartReal = Time.realtimeSinceStartup;
        float segmentBudgetSeconds = runSegmentMinutes * 60f;
        float restSeconds = restMinutes * 60f;
        float maxWallClockSeconds = maxWallClockHours > 0f ? maxWallClockHours * 3600f : 0f;

        int baselineValid = CountValidMissions();
        int runIndex = 0;
        int segmentIndex = 0;

        Debug.Log(
            $"[TimedBatch] Starting batch {batchId}. baseline valid={baselineValid}, " +
            $"target new={targetValidMissions} (stop at {baselineValid + targetValidMissions}), " +
            $"segment={runSegmentMinutes:0.#}m, rest={restMinutes:0.#}m, " +
            $"planner={plannerType}, drones={droneCount}, sensor={sensorRadius}, " +
            $"comms={communicationRadius:0.###}, speed={droneSpeed:0.###}",
            this);

        EnableRendering();

        while (!cancelRequested)
        {
            int currentValid = CountValidMissions();
            int collectedThisBatch = currentValid - baselineValid;
            if (collectedThisBatch >= targetValidMissions)
            {
                Debug.Log($"[TimedBatch] Target reached: {collectedThisBatch}/{targetValidMissions} valid missions collected.", this);
                break;
            }

            if (maxWallClockSeconds > 0f && (Time.realtimeSinceStartup - batchStartReal) >= maxWallClockSeconds)
            {
                Debug.LogWarning($"[TimedBatch] Wall-clock cap of {maxWallClockHours:0.#}h reached. Stopping with {collectedThisBatch}/{targetValidMissions} valid missions.", this);
                break;
            }

            segmentIndex++;
            float segmentStartReal = Time.realtimeSinceStartup;
            Debug.Log($"[TimedBatch] Segment {segmentIndex} start. valid so far: {currentValid} (collected: {collectedThisBatch}/{targetValidMissions}).", this);

            // --- Running segment: launch missions until the wall-clock budget elapses. ---
            while (!cancelRequested)
            {
                if (CountValidMissions() - baselineValid >= targetValidMissions)
                {
                    break;
                }

                float elapsedInSegment = Time.realtimeSinceStartup - segmentStartReal;
                if (elapsedInSegment >= segmentBudgetSeconds)
                {
                    break;
                }

                runIndex++;
                int seed = GetSeedForRun(runIndex);
                Random.InitState(seed);

                Debug.Log(
                    $"[TimedBatch] Run {runIndex}: " +
                    $"planner={plannerType}, drones={droneCount}, sensor={sensorRadius}, " +
                    $"comms={communicationRadius:0.###}, speed={droneSpeed:0.###}, seed={seed}, " +
                    $"segment={segmentIndex}, elapsed={elapsedInSegment:0.#}/{segmentBudgetSeconds:0.#}s",
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
                    configurationIndex: 1,
                    repeatIndex: runIndex,
                    hasRandomSeed: true,
                    randomSeed: seed);
                bootstrap.ResetDemo();

                bool sessionStarted = false;
                float startDeadline = Time.realtimeSinceStartup + sessionStartTimeoutSeconds;
                while (!cancelRequested && Time.realtimeSinceStartup <= startDeadline)
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
                    Debug.LogWarning($"[TimedBatch] Run {runIndex} did not start a telemetry session.", this);
                    continue;
                }

                // Wait for the mission to end. Optionally cut it off at the segment boundary.
                while (!cancelRequested && bootstrap.IsTelemetrySessionActive)
                {
                    if (!allowActiveMissionToFinishAfterSegment
                        && (Time.realtimeSinceStartup - segmentStartReal) >= segmentBudgetSeconds)
                    {
                        bootstrap.EndActiveTelemetrySession("segment_timeout", false);
                        break;
                    }

                    yield return null;
                }

                if (cancelRequested)
                {
                    break;
                }

                if (betweenMissionsDelaySeconds > 0f)
                {
                    yield return new WaitForSecondsRealtime(betweenMissionsDelaySeconds);
                }
            }

            if (cancelRequested)
            {
                break;
            }

            int validNow = CountValidMissions();
            int collectedNow = validNow - baselineValid;
            if (collectedNow >= targetValidMissions)
            {
                Debug.Log($"[TimedBatch] Target reached after segment {segmentIndex}: {collectedNow}/{targetValidMissions}.", this);
                break;
            }

            // --- Cooldown rest. ---
            if (restSeconds > 0f)
            {
                Debug.Log(
                    $"[TimedBatch] Segment {segmentIndex} done. valid={validNow} (collected: {collectedNow}/{targetValidMissions}). " +
                    $"Cooling down for {restMinutes:0.#}m (rendering disabled).",
                    this);
                DisableRendering();
                float restStartReal = Time.realtimeSinceStartup;
                while (!cancelRequested && (Time.realtimeSinceStartup - restStartReal) < restSeconds)
                {
                    yield return null;
                }

                EnableRendering();
                if (cancelRequested)
                {
                    break;
                }

                Debug.Log("[TimedBatch] Cooldown complete. Resuming.", this);
            }
        }

        EnableRendering();
        bootstrap.ClearTelemetryBatchContext();

        int finalValid = CountValidMissions();
        int finalCollected = finalValid - baselineValid;
        if (cancelRequested)
        {
            Debug.LogWarning($"[TimedBatch] Batch {batchId} cancelled. Valid: {finalValid} (collected this batch: {finalCollected}/{targetValidMissions}).", this);
        }
        else
        {
            Debug.Log($"[TimedBatch] Batch {batchId} complete. Valid: {finalValid} (collected this batch: {finalCollected}/{targetValidMissions}). Runs: {runIndex}.", this);
        }

        batchCoroutine = null;
    }

    private int CountValidMissions()
    {
        if (telemetryRecorder == null)
        {
            telemetryRecorder = FindAnyObjectByType<DroneMissionTelemetryRecorder>();
        }

        return telemetryRecorder != null
            ? telemetryRecorder.CountSessionsWithEndReason(validEndReason)
            : 0;
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

    private void DisableRendering()
    {
        if (pauseSimulationDuringRest)
        {
            savedTimeScale = Time.timeScale;
            Time.timeScale = 0f;
        }

        if (!disableRenderingDuringRest)
        {
            return;
        }

        disabledCameras.Clear();
        foreach (var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            if (camera != null && camera.enabled)
            {
                camera.enabled = false;
                disabledCameras.Add(camera);
            }
        }

        disabledDebugRenderer = FindAnyObjectByType<DroneSwarmDebugRenderer>();
        if (disabledDebugRenderer != null && disabledDebugRenderer.enabled)
        {
            disabledDebugRenderer.enabled = false;
        }
        else
        {
            disabledDebugRenderer = null;
        }
    }

    private void EnableRendering()
    {
        if (pauseSimulationDuringRest && Time.timeScale == 0f)
        {
            Time.timeScale = savedTimeScale > 0f ? savedTimeScale : 1f;
        }

        if (!disableRenderingDuringRest)
        {
            return;
        }

        foreach (var camera in disabledCameras)
        {
            if (camera != null)
            {
                camera.enabled = true;
            }
        }

        disabledCameras.Clear();

        if (disabledDebugRenderer != null)
        {
            disabledDebugRenderer.enabled = true;
            disabledDebugRenderer = null;
        }
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

    private int GetSeedForRun(int oneBasedRunIndex)
    {
        return useDeterministicSeeds ? baseSeed + oneBasedRunIndex - 1 : Guid.NewGuid().GetHashCode();
    }

    private static string GenerateBatchId()
    {
        return $"timed-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
    }
}
