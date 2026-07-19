using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Runs timed mission segments with rendering-disabled cooldowns until enough
/// valid missions have been persisted.
/// </summary>
public sealed class DroneMissionTimedBatchRunner : MonoBehaviour
{
    private const float DefaultRunHardTimeoutSeconds = 180f;
    private const float MinimumRunHardTimeoutSeconds = 1f;

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
    [Tooltip("Unconditional realtime hard limit for one run. Unlike the telemetry session timeout, this cannot be disabled and remains effective when a mission may overrun its segment or the global wall-clock cap is unlimited.")]
    [SerializeField] private float runHardTimeoutSeconds = DefaultRunHardTimeoutSeconds;
    [Tooltip("If true, an in-flight mission may overrun the segment, but never its run hard timeout. If false, it is cut off at the segment boundary with end_reason = segment_timeout.")]
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
    private Coroutine telemetryCleanupCoroutine;
    private bool telemetryCleanupPending;
    private bool telemetryBatchContextCleared;
    private bool cancelRequested;
    private bool batchCleanupCompleted;
    private bool worldPreparationSucceeded;
    private bool setupFailed;
    private bool telemetryPersistenceFailed;
    private bool wallClockTimeoutReached;

    private readonly List<Camera> disabledCameras = new();
    private DroneSwarmDebugRenderer disabledDebugRenderer;
    private bool cooldownStateApplied;
    private bool timeScaleChanged;
    private float savedTimeScale = 1f;

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
        runSegmentMinutes = Mathf.Max(0.1f, runSegmentMinutes);
        betweenMissionsDelaySeconds = Mathf.Max(0f, betweenMissionsDelaySeconds);
        sessionStartTimeoutSeconds = Mathf.Max(0.1f, sessionStartTimeoutSeconds);
        perSessionTimeoutSeconds = Mathf.Max(0f, perSessionTimeoutSeconds);
        runHardTimeoutSeconds = GetValidRunHardTimeoutSeconds();
        restMinutes = Mathf.Max(0f, restMinutes);
        targetValidMissions = Mathf.Max(1, targetValidMissions);
        maxWallClockHours = Mathf.Max(0f, maxWallClockHours);

        droneCount = Mathf.Clamp(droneCount, 1, DroneSwarmDemoBootstrap.MaximumDroneCount);
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

        if (batchCoroutine != null || telemetryCleanupPending)
        {
            Debug.LogWarning("[TimedBatch] A batch or telemetry cleanup is already running.", this);
            return;
        }

        ResolveReferences();
        if (bootstrap == null)
        {
            Debug.LogError("[TimedBatch] Cannot run batch without a DroneSwarmDemoBootstrap reference.", this);
            return;
        }

        // Readiness creates the recorder and recovers pending session ends before counting.
        if (!bootstrap.TryPrepareTelemetryForBatch(out telemetryRecorder))
        {
            string detail = telemetryRecorder != null
                ? telemetryRecorder.LastPersistenceError
                : "telemetry recorder/bootstrap is unavailable";
            Debug.LogError($"[TimedBatch] Cannot start because telemetry storage is not ready: {detail}", this);
            return;
        }

        cancelRequested = false;
        batchCleanupCompleted = false;
        telemetryCleanupPending = false;
        telemetryBatchContextCleared = false;
        setupFailed = false;
        telemetryPersistenceFailed = false;
        wallClockTimeoutReached = false;
        batchCoroutine = StartCoroutine(RunBatchCoroutine());
    }

    [ContextMenu("Cancel Batch")]
    public void CancelBatch()
    {
        if (batchCoroutine == null && !cooldownStateApplied)
        {
            return;
        }

        StopAndCleanupBatch();
    }

    private IEnumerator RunBatchCoroutine()
    {
        string batchId = GenerateBatchId();
        float batchStartReal = Time.realtimeSinceStartup;
        float segmentBudgetSeconds = runSegmentMinutes * 60f;
        float restSeconds = restMinutes * 60f;
        float maxWallClockSeconds = maxWallClockHours > 0f ? maxWallClockHours * 3600f : 0f;
        float batchDeadlineReal = maxWallClockSeconds > 0f
            ? batchStartReal + maxWallClockSeconds
            : float.PositiveInfinity;
        // Runtime values bypass OnValidate and must be clamped again.
        runHardTimeoutSeconds = GetValidRunHardTimeoutSeconds();

        int runIndex = 0;
        int segmentIndex = 0;

        try
        {
            int initialValid = CountValidMissions(batchId);
            Debug.Log(
                $"[TimedBatch] Starting new batch {batchId}. valid for this batch={initialValid}, " +
                $"target={targetValidMissions}, " +
                $"segment={runSegmentMinutes:0.#}m, rest={restMinutes:0.#}m, " +
                $"planner={plannerType}, drones={droneCount}, sensor={sensorRadius}, " +
                $"comms={communicationRadius:0.###}, speed={droneSpeed:0.###}",
                this);

            EnableRendering();

        while (!cancelRequested && !telemetryPersistenceFailed)
        {
            int collectedThisBatch = CountValidMissions(batchId);
            if (telemetryPersistenceFailed)
            {
                break;
            }
            if (collectedThisBatch >= targetValidMissions)
            {
                Debug.Log($"[TimedBatch] Target reached: {collectedThisBatch}/{targetValidMissions} valid missions collected.", this);
                break;
            }

            if (TryReachWallClockDeadline(batchDeadlineReal, batchId, runIndex))
            {
                Debug.LogWarning($"[TimedBatch] Wall-clock cap of {maxWallClockHours:0.#}h reached. Stopping with {collectedThisBatch}/{targetValidMissions} valid missions.", this);
                break;
            }

            segmentIndex++;
            float segmentDeadlineReal = Time.realtimeSinceStartup + segmentBudgetSeconds;
            Debug.Log($"[TimedBatch] Segment {segmentIndex} start. collected: {collectedThisBatch}/{targetValidMissions}.", this);

            while (!cancelRequested && !telemetryPersistenceFailed)
            {
                int validAtRunStart = CountValidMissions(batchId);
                if (telemetryPersistenceFailed || validAtRunStart >= targetValidMissions)
                {
                    break;
                }

                float remainingInSegment = segmentDeadlineReal - Time.realtimeSinceStartup;
                if (TryReachWallClockDeadline(batchDeadlineReal, batchId, runIndex)
                    || HasReachedDeadline(segmentDeadlineReal))
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
                    $"segment={segmentIndex}, remaining={Mathf.Max(0f, remainingInSegment):0.#}/{segmentBudgetSeconds:0.#}s",
                    this);

                // Synchronous world preparation counts toward the run deadline.
                float runHardDeadlineReal = Time.realtimeSinceStartup + runHardTimeoutSeconds;
                bool runHardTimeoutReached = false;
                yield return PrepareWorldForRun();

                if (cancelRequested)
                {
                    break;
                }
                if (CheckTelemetryPersistenceFailure(batchId, runIndex))
                {
                    break;
                }
                if (TryReachWallClockDeadline(batchDeadlineReal, batchId, runIndex))
                {
                    break;
                }
                if (TryReachRunHardDeadline(runHardDeadlineReal, batchId, runIndex))
                {
                    if (IsFinalPersistenceRetryInProgress())
                    {
                        yield return WaitForFinalPersistenceResolution(batchDeadlineReal, batchId, runIndex);
                    }
                    if (cancelRequested || wallClockTimeoutReached
                        || CheckTelemetryPersistenceFailure(batchId, runIndex))
                    {
                        break;
                    }

                    continue;
                }
                // Recheck the segment deadline after synchronous preparation and final yields.
                if (TryReachSegmentDeadline(segmentDeadlineReal, false))
                {
                    break;
                }
                if (!worldPreparationSucceeded)
                {
                    setupFailed = true;
                    Debug.LogError($"[TimedBatch] Run {runIndex} world setup failed; aborting batch.", this);
                    break;
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
                    configurationIndex: 1,
                    repeatIndex: runIndex,
                    hasRandomSeed: true,
                    randomSeed: seed);
                if (!bootstrap.TryResetDemo())
                {
                    setupFailed = true;
                    Debug.LogError($"[TimedBatch] Run {runIndex} world reset could not be queued.", this);
                    break;
                }

                // Reset owns retries before a write error becomes terminal.
                while (!cancelRequested
                    && bootstrap.IsResetQueued
                    && !HasReachedDeadline(batchDeadlineReal)
                    && !HasReachedDeadline(runHardDeadlineReal)
                    && !HasReachedDeadline(segmentDeadlineReal))
                {
                    yield return null;
                }

                // Apply cancellation, persistence, global, then run deadline precedence.
                if (cancelRequested)
                {
                    break;
                }
                if (CheckTelemetryPersistenceFailure(batchId, runIndex))
                {
                    break;
                }
                if (TryReachWallClockDeadline(batchDeadlineReal, batchId, runIndex))
                {
                    break;
                }
                if (TryReachRunHardDeadline(runHardDeadlineReal, batchId, runIndex))
                {
                    runHardTimeoutReached = true;
                }

                if (runHardTimeoutReached)
                {
                    if (IsFinalPersistenceRetryInProgress())
                    {
                        yield return WaitForFinalPersistenceResolution(batchDeadlineReal, batchId, runIndex);
                    }
                    if (cancelRequested || wallClockTimeoutReached
                        || CheckTelemetryPersistenceFailure(batchId, runIndex))
                    {
                        break;
                    }

                    continue;
                }
                if (!bootstrap.IsResetQueued && !bootstrap.LastResetSucceeded)
                {
                    setupFailed = true;
                    Debug.LogError($"[TimedBatch] Run {runIndex} world reset failed: {bootstrap.LastResetError}", this);
                    break;
                }
                // Cancel or freeze resets that cross the segment deadline.
                bool sessionStarted = HasActiveMissionStartedBeforeDeadline(segmentDeadlineReal);
                if (TryReachSegmentDeadline(segmentDeadlineReal, sessionStarted)
                    && (!sessionStarted
                        || !allowActiveMissionToFinishAfterSegment
                        || !bootstrap.IsTelemetrySessionActive))
                {
                    break;
                }

                float startDeadline = Time.realtimeSinceStartup + sessionStartTimeoutSeconds;
                while (!cancelRequested
                    && Time.realtimeSinceStartup <= startDeadline
                    && !HasReachedDeadline(batchDeadlineReal)
                    && !HasReachedDeadline(runHardDeadlineReal)
                    && !HasReachedDeadline(segmentDeadlineReal))
                {
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

                // Apply deadline precedence before accepting a final-frame session.
                CheckTelemetryPersistenceFailure(batchId, runIndex);
                if (!sessionStarted)
                {
                    sessionStarted = HasActiveMissionStartedBeforeDeadline(segmentDeadlineReal);
                }

                if (telemetryPersistenceFailed || cancelRequested)
                {
                    break;
                }
                if (TryReachWallClockDeadline(batchDeadlineReal, batchId, runIndex))
                {
                    break;
                }
                if (TryReachRunHardDeadline(runHardDeadlineReal, batchId, runIndex))
                {
                    if (IsFinalPersistenceRetryInProgress())
                    {
                        yield return WaitForFinalPersistenceResolution(batchDeadlineReal, batchId, runIndex);
                    }
                    if (cancelRequested || wallClockTimeoutReached
                        || CheckTelemetryPersistenceFailure(batchId, runIndex))
                    {
                        break;
                    }

                    continue;
                }
                if (TryReachSegmentDeadline(segmentDeadlineReal, sessionStarted)
                    && (!sessionStarted
                        || !allowActiveMissionToFinishAfterSegment
                        || !bootstrap.IsTelemetrySessionActive))
                {
                    break;
                }
                if (!sessionStarted && bootstrap.IsTelemetrySessionActive)
                {
                    sessionStarted = true;
                }
                if (!sessionStarted)
                {
                    Debug.LogWarning($"[TimedBatch] Run {runIndex} did not start a telemetry session.", this);
                    continue;
                }

                yield return WaitForActiveSessionToEnd(
                    batchId,
                    runIndex,
                    segmentDeadlineReal,
                    batchDeadlineReal,
                    runHardDeadlineReal);

                CheckTelemetryPersistenceFailure(batchId, runIndex);
                if (cancelRequested || telemetryPersistenceFailed)
                {
                    break;
                }

                // Recount before another launch so the exact target ends the segment.
                int validAfterRun = CountValidMissions(batchId);
                if (telemetryPersistenceFailed || validAfterRun >= targetValidMissions)
                {
                    break;
                }

                if (betweenMissionsDelaySeconds > 0f)
                {
                    float delayDeadline = Time.realtimeSinceStartup + betweenMissionsDelaySeconds;
                    while (!cancelRequested
                        && Time.realtimeSinceStartup < delayDeadline
                        && !HasReachedDeadline(batchDeadlineReal)
                        && !HasReachedDeadline(segmentDeadlineReal))
                    {
                        yield return null;
                    }

                    if (TryReachWallClockDeadline(batchDeadlineReal, batchId, runIndex))
                    {
                        break;
                    }
                    if (TryReachSegmentDeadline(segmentDeadlineReal, false))
                    {
                        break;
                    }
                }
            }

            if (cancelRequested || setupFailed || telemetryPersistenceFailed || wallClockTimeoutReached)
            {
                break;
            }

            int collectedNow = CountValidMissions(batchId);
            if (collectedNow >= targetValidMissions)
            {
                Debug.Log($"[TimedBatch] Target reached after segment {segmentIndex}: {collectedNow}/{targetValidMissions}.", this);
                break;
            }

            if (restSeconds > 0f)
            {
                Debug.Log(
                    $"[TimedBatch] Segment {segmentIndex} done. collected: {collectedNow}/{targetValidMissions}. " +
                    $"Cooling down for {restMinutes:0.#}m (rendering disabled).",
                    this);
                DisableRendering();
                float restStartReal = Time.realtimeSinceStartup;
                while (!cancelRequested
                    && (Time.realtimeSinceStartup - restStartReal) < restSeconds
                    && !HasReachedDeadline(batchDeadlineReal))
                {
                    yield return null;
                }

                EnableRendering();
                if (cancelRequested || TryReachWallClockDeadline(batchDeadlineReal, batchId, runIndex))
                {
                    break;
                }

                Debug.Log("[TimedBatch] Cooldown complete. Resuming.", this);
            }
        }

        int finalCollected = CountValidMissions(batchId);
        if (telemetryPersistenceFailed)
        {
            Debug.LogError($"[TimedBatch] Batch {batchId} stopped after telemetry persistence failure. Collected: {finalCollected}/{targetValidMissions}.", this);
        }
        else if (setupFailed)
        {
            Debug.LogError($"[TimedBatch] Batch {batchId} stopped after world setup failure. Collected: {finalCollected}/{targetValidMissions}.", this);
        }
        else if (cancelRequested)
        {
            Debug.LogWarning($"[TimedBatch] Batch {batchId} cancelled. Collected: {finalCollected}/{targetValidMissions}.", this);
        }
        else if (wallClockTimeoutReached)
        {
            Debug.LogWarning($"[TimedBatch] Batch {batchId} reached its wall-clock cap. Collected: {finalCollected}/{targetValidMissions}. Runs: {runIndex}.", this);
        }
        else
        {
            Debug.Log($"[TimedBatch] Batch {batchId} complete. Collected: {finalCollected}/{targetValidMissions}. Runs: {runIndex}.", this);
        }
        }
        finally
        {
            CleanupBatch(
                telemetryPersistenceFailed ? "telemetry_write_failed" :
                setupFailed ? "setup_failed" :
                cancelRequested ? "batch_cancelled" :
                wallClockTimeoutReached ? "wall_clock_timeout" : "batch_stopped");
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
        // Retire runner context without discarding recorder-owned frozen rows.
        ClearBatchContextOnce();
        telemetryCleanupPending = false;
    }

    private void StopAndCleanupBatch()
    {
        if (batchCoroutine == null && !cooldownStateApplied)
        {
            return;
        }

        cancelRequested = true;
        if (batchCoroutine != null)
        {
            StopCoroutine(batchCoroutine);
            batchCoroutine = null;
        }

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

        EnableRendering();
        if (bootstrap != null)
        {
            bootstrap.ShutdownBatchRun(activeSessionEndReason, false);
        }

        batchCoroutine = null;
        if (bootstrap != null
            && bootstrap.IsFinalTelemetryPersistencePending
            && !bootstrap.HasTerminalTelemetryPersistenceFailure)
        {
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

    private int CountValidMissions(string batchId)
    {
        if (telemetryRecorder == null)
        {
            telemetryRecorder = FindAnyObjectByType<DroneMissionTelemetryRecorder>();
        }

        if (telemetryRecorder == null)
        {
            telemetryPersistenceFailed = true;
            Debug.LogError("[TimedBatch] Telemetry recorder is unavailable; stopping batch.", this);
            return 0;
        }

        // Pending final-row retries are recoverable, not terminal batch failures.
        if (IsTerminalTelemetryPersistenceFailure())
        {
            telemetryPersistenceFailed = true;
            return 0;
        }

        if (!telemetryRecorder.TryCountUniqueSessionsWithEndReasonForBatch(
                validEndReason,
                batchId,
                out int count))
        {
            // A pending final-row retry may also repair the read failure.
            if (IsTerminalTelemetryPersistenceFailure())
            {
                telemetryPersistenceFailed = true;
            }
            return 0;
        }

        return count;
    }

    private IEnumerator WaitForActiveSessionToEnd(
        string batchId,
        int runIndex,
        float segmentDeadlineReal,
        float batchDeadlineReal,
        float runHardDeadlineReal)
    {
        bool segmentCutoffApplied = false;
        while (true)
        {
            // Cancellation and global deadline outrank same-frame persistence failure.
            if (cancelRequested)
            {
                yield break;
            }
            if (TryReachWallClockDeadline(batchDeadlineReal, batchId, runIndex))
            {
                yield break;
            }
            if (TryReachRunHardDeadline(runHardDeadlineReal, batchId, runIndex))
            {
                // Final persistence gets its own bounded cleanup budget.
                if (IsFinalPersistenceRetryInProgress())
                {
                    yield return WaitForFinalPersistenceResolution(batchDeadlineReal, batchId, runIndex);
                }
                yield break;
            }
            if (CheckTelemetryPersistenceFailure(batchId, runIndex))
            {
                yield break;
            }

            if (!bootstrap.IsTelemetrySessionActive)
            {
                // An inactive session may still have frozen rows awaiting persistence.
                if (bootstrap.IsFinalTelemetryPersistencePending
                    && !bootstrap.HasTerminalTelemetryPersistenceFailure)
                {
                    yield return null;
                    continue;
                }

                yield break;
            }

            if (!segmentCutoffApplied
                && !allowActiveMissionToFinishAfterSegment
                && HasReachedDeadline(segmentDeadlineReal))
            {
                // Only the active-mission policy may cut off an accepted session.
                segmentCutoffApplied = true;
                bootstrap.ShutdownBatchRun("segment_timeout", false);
            }

            // Wait for final persistence, terminal failure, cancellation, or deadline.
            yield return null;
        }
    }

    private static bool HasReachedDeadline(float absoluteRealtimeDeadline)
    {
        return Time.realtimeSinceStartup >= absoluteRealtimeDeadline;
    }

    private bool HasActiveMissionStartedBeforeDeadline(float segmentDeadlineReal)
    {
        return bootstrap != null
            && bootstrap.IsTelemetrySessionActive
            && telemetryRecorder != null
            && telemetryRecorder.ActiveSessionStartRealtime < segmentDeadlineReal;
    }

    private bool TryReachSegmentDeadline(
        float segmentDeadlineReal,
        bool activeMissionAcceptedBeforeDeadline)
    {
        if (!HasReachedDeadline(segmentDeadlineReal))
        {
            return false;
        }

        // Cancel queued resets before they can create a session past this boundary.
        if (bootstrap != null && bootstrap.IsResetQueued)
        {
            bootstrap.CancelQueuedReset("Timed batch segment expired before mission launch.");
        }

        // Only sessions persisted before the deadline may finish after the segment.
        if (bootstrap != null
            && bootstrap.IsTelemetrySessionActive
            && (!activeMissionAcceptedBeforeDeadline
                || !allowActiveMissionToFinishAfterSegment))
        {
            bootstrap.ShutdownBatchRun("segment_timeout", false);
        }

        return true;
    }

    private bool TryReachWallClockDeadline(float batchDeadlineReal, string batchId, int runIndex)
    {
        if (!HasReachedDeadline(batchDeadlineReal))
        {
            return false;
        }

        if (!wallClockTimeoutReached)
        {
            wallClockTimeoutReached = true;
            Debug.LogWarning(
                $"[TimedBatch] Batch {batchId}, run {runIndex} reached the absolute wall-clock deadline.",
                this);
        }

        if (bootstrap != null)
        {
            bootstrap.ShutdownBatchRun("wall_clock_timeout", false);
        }

        return true;
    }

    private bool TryReachRunHardDeadline(float runHardDeadlineReal, string batchId, int runIndex)
    {
        if (!HasReachedDeadline(runHardDeadlineReal))
        {
            return false;
        }

        Debug.LogWarning(
            $"[TimedBatch] Batch {batchId}, run {runIndex} reached its {runHardTimeoutSeconds:0.###}s realtime hard deadline.",
            this);
        if (bootstrap != null)
        {
            bootstrap.ShutdownBatchRun("run_hard_timeout", false);
        }

        return true;
    }

    private bool IsFinalPersistenceRetryInProgress()
    {
        return bootstrap != null
            && bootstrap.IsFinalTelemetryPersistencePending
            && !bootstrap.HasTerminalTelemetryPersistenceFailure;
    }

    private IEnumerator WaitForFinalPersistenceResolution(
        float batchDeadlineReal,
        string batchId,
        int runIndex)
    {
        while (bootstrap != null
            && bootstrap.IsFinalTelemetryPersistencePending
            && !bootstrap.HasTerminalTelemetryPersistenceFailure)
        {
            // Cancellation or global deadline cannot launch a replacement during cleanup.
            if (cancelRequested)
            {
                yield break;
            }
            if (TryReachWallClockDeadline(batchDeadlineReal, batchId, runIndex))
            {
                yield break;
            }

            yield return null;
        }

        if (!cancelRequested && !wallClockTimeoutReached)
        {
            CheckTelemetryPersistenceFailure(batchId, runIndex);
        }
    }

    private bool CheckTelemetryPersistenceFailure(string batchId, int runIndex)
    {
        if (telemetryRecorder == null)
        {
            telemetryRecorder = FindAnyObjectByType<DroneMissionTelemetryRecorder>();
        }

        telemetryPersistenceFailed = telemetryRecorder == null
            || IsTerminalTelemetryPersistenceFailure();
        if (telemetryPersistenceFailed)
        {
            string detail = telemetryRecorder != null
                ? telemetryRecorder.LastPersistenceError
                : "telemetry recorder is unavailable";
            Debug.LogError($"[TimedBatch] Batch {batchId}, run {runIndex} telemetry persistence failed: {detail}", this);
        }

        return telemetryPersistenceFailed;
    }

    private bool IsTerminalTelemetryPersistenceFailure()
    {
        if (telemetryRecorder == null)
        {
            return true;
        }

        if (bootstrap != null)
        {
            if (bootstrap.HasTerminalTelemetryPersistenceFailure)
            {
                return true;
            }
            if (bootstrap.IsFinalTelemetryPersistencePending)
            {
                return false;
            }
        }

        return telemetryRecorder.HasPersistenceFailure;
    }

    private IEnumerator PrepareWorldForRun()
    {
        worldPreparationSucceeded = false;
        ResolveReferences();

        if ((clearForestBeforeSpawning || spawnForestEachRun) && forestSpawner == null)
        {
            Debug.LogError("[TimedBatch] ForestSpawner is required by the world reset settings.", this);
            yield break;
        }
        if (regenerateTerrainEachRun && terrainGenerator == null)
        {
            Debug.LogError("[TimedBatch] TerrainGenerator is required by the world reset settings.", this);
            yield break;
        }

        if (clearForestBeforeSpawning)
        {
            forestSpawner.ClearSpawnedTrees();
            yield return null;
        }

        if (regenerateTerrainEachRun && !terrainGenerator.TryGenerateTerrain())
        {
            Debug.LogError("[TimedBatch] Terrain generation failed.", this);
            yield break;
        }

        if (spawnForestEachRun && !forestSpawner.TrySpawnTrees())
        {
            Debug.LogError("[TimedBatch] Forest generation failed.", this);
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
            Debug.LogError("[TimedBatch] Explorer is not ready for the run.", this);
            yield break;
        }

        Physics.SyncTransforms();
        yield return null;
        worldPreparationSucceeded = true;
    }

    private void DisableRendering()
    {
        if (cooldownStateApplied)
        {
            return;
        }

        cooldownStateApplied = true;
        if (pauseSimulationDuringRest && Time.timeScale != 0f)
        {
            savedTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            timeScaleChanged = true;
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
        if (!cooldownStateApplied)
        {
            return;
        }

        cooldownStateApplied = false;
        if (timeScaleChanged)
        {
            // Preserve a time scale changed by another system during rest.
            if (Time.timeScale == 0f)
            {
                Time.timeScale = savedTimeScale;
            }

            timeScaleChanged = false;
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

    private float GetValidRunHardTimeoutSeconds()
    {
        if (float.IsNaN(runHardTimeoutSeconds) || float.IsInfinity(runHardTimeoutSeconds))
        {
            return DefaultRunHardTimeoutSeconds;
        }

        return Mathf.Max(MinimumRunHardTimeoutSeconds, runHardTimeoutSeconds);
    }

    private static string GenerateBatchId()
    {
        return $"timed-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
    }
}
