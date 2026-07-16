using System;
using System.Collections;
using UnityEngine;

public class ScriptsControl : MonoBehaviour
{
    [Header("Main Scripts")]
    public TerrainGenerator terrainGenerator;
    public ForestSpawner forestSpawner;
    public DroneSwarmDemoBootstrap droneSwarmDemoBootstrap;
    public Explorer explorer;
    public UiScriptsControl uiScriptsControl;

    [Header("Start Setting")]
    [SerializeField] public bool autoStart = false;

    private enum PendingSetupKind
    {
        None,
        Initial,
        Replay
    }

    private bool started;
    private bool setupInProgress;
    private bool waitingForReset;
    private PendingSetupKind pendingSetupKind;
    private bool pendingInitialRollbackRequired;
    private bool pendingReplayActivated;
    private ReplayWorldPublication pendingReplayPublication;
    [NonSerialized] private Action<string> replayCommitExceptionInjection;
    private Action<bool, string> pendingCompletion;

    private sealed class ReplayWorldPublication
    {
        internal TerrainData StagedData;
        internal TerrainGenerator.TerrainPublication Terrain;
        internal ForestSpawner.ForestPublication Forest;
        internal Explorer.ReplayState Explorer;
    }

    public bool IsStarted => started;
    public bool IsSetupInProgress => setupInProgress;
    public bool BatchAutoStartEnabled => HasBatchAutoStartEnabled();

    private void Awake()
    {
        droneSwarmDemoBootstrap?.ConfigureExplorerOwnership(this);
    }

    private IEnumerator Start()
    {
        // Let GameFlowController own auto-start when one is present so settings and
        // game timers are applied only after asynchronous world startup succeeds.
        yield return null;
        if (autoStart && !started && FindAnyObjectByType<GameFlowController>() == null)
        {
            StartSimulation();
        }
    }

    // Kept as a void wrapper so existing UnityEvent bindings remain valid.
    public void StartSimulation()
    {
        TryStartSimulationAsync((succeeded, error) =>
        {
            if (!succeeded)
            {
                Debug.LogError($"[ScriptsControl] Automatic simulation startup failed: {error}", this);
            }
        });
    }

    /// <summary>
    /// Queues startup. A true return value means the request was accepted, not that
    /// asynchronous reset work has completed. Observe IsStarted or use the Async API.
    /// </summary>
    public bool TryStartSimulation()
    {
        return TryStartSimulationAsync(null);
    }

    public bool TryStartSimulationAsync(Action<bool, string> completion)
    {
        if (started)
        {
            completion?.Invoke(true, string.Empty);
            return true;
        }

        return TrySetupSimulation(false, completion);
    }

    /// <summary>Queues an explicit same-scene new game.</summary>
    public bool TryStartNewSimulation()
    {
        return TryStartNewSimulationAsync(null);
    }

    public bool TryStartNewSimulationAsync(Action<bool, string> completion)
    {
        if (setupInProgress)
        {
            RejectStart("Simulation setup is already in progress.", completion, false);
            return false;
        }
        if (HasBatchAutoStartEnabled())
        {
            RejectStart("Same-scene interactive replay is unavailable while batch auto-start is enabled.", completion, true);
            return false;
        }
        if (droneSwarmDemoBootstrap == null || !droneSwarmDemoBootstrap.IsGameplayStopped)
        {
            RejectStart("A stopped interactive simulation is required before starting a new game.", completion, true);
            return false;
        }

        started = false;
        return TrySetupSimulation(true, completion);
    }

    /// <summary>Cancels an accepted startup request without waiting on the main thread.</summary>
    public void CancelPendingStart(string reason = null)
    {
        if (!setupInProgress)
        {
            return;
        }

        string error = string.IsNullOrWhiteSpace(reason)
            ? "Simulation startup was cancelled."
            : reason;

        if (waitingForReset && droneSwarmDemoBootstrap != null && droneSwarmDemoBootstrap.IsResetQueued)
        {
            // CancelQueuedReset synchronously reports completion to OnResetCompleted.
            droneSwarmDemoBootstrap.CancelQueuedReset(error);
            return;
        }

        FinishSetup(false, error);
    }

    private bool TrySetupSimulation(bool replay, Action<bool, string> completion)
    {
        if (setupInProgress)
        {
            RejectStart("Simulation setup is already in progress.", completion, false);
            return false;
        }

        setupInProgress = true;
        pendingCompletion = completion;
        pendingSetupKind = replay ? PendingSetupKind.Replay : PendingSetupKind.Initial;
        pendingInitialRollbackRequired = false;
        pendingReplayActivated = false;
        bool remainsAsynchronous = false;
        try
        {
            bool batchAutoStartEnabled = HasBatchAutoStartEnabled();
            if (!ValidateRequiredReferences(batchAutoStartEnabled))
            {
                FinishSetup(false, "Required simulation references are invalid.");
                return false;
            }

            if (replay && !droneSwarmDemoBootstrap.TryPrepareForNewGame())
            {
                Debug.LogError("[ScriptsControl] Replay preflight failed; the retained result world remains unchanged.", this);
                FinishSetup(false, "Replay preflight failed.");
                return false;
            }

            if (replay)
            {
                if (!TryPrepareAndCommitReplayWorld(out string replaySetupError))
                {
                    FinishSetup(false, replaySetupError);
                    return false;
                }
                pendingReplayActivated = true;
            }
            else
            {
                if (!terrainGenerator.TryGenerateTerrain())
                {
                    Debug.LogError("[ScriptsControl] Terrain generation failed; simulation setup aborted.", this);
                    FinishSetup(false, "Terrain generation failed.");
                    return false;
                }

                if (droneSwarmDemoBootstrap != null)
                {
                    droneSwarmDemoBootstrap.ConfigureGridFromTerrain(
                        terrainGenerator.terrain,
                        droneSwarmDemoBootstrap.CellSize);
                }

                if (!batchAutoStartEnabled)
                {
                    // Forest spawning is additive and explorer setup mutates live runtime
                    // state. Any failure from this point must return initial startup to an
                    // empty baseline before Start can be retried.
                    pendingInitialRollbackRequired = true;
                    if (!forestSpawner.TrySpawnTrees())
                    {
                        Debug.LogError("[ScriptsControl] Forest generation failed; simulation setup aborted.", this);
                        FinishSetup(false, "Forest generation failed.");
                        return false;
                    }
                }

                if (!explorer.ExplorerSpawner())
                {
                    Debug.LogError("[ScriptsControl] Explorer spawn failed; simulation setup aborted.", this);
                    FinishSetup(false, "Explorer spawn failed.");
                    return false;
                }
            }

            if (batchAutoStartEnabled)
            {
                FinishSetup(true, string.Empty);
                return true;
            }

            // Subscribe before queueing so every accepted reset has one observable
            // completion path, including cancellation/deactivation.
            droneSwarmDemoBootstrap.ResetCompleted += OnResetCompleted;
            waitingForReset = true;
            replayCommitExceptionInjection?.Invoke("reset_queue");
            if (!droneSwarmDemoBootstrap.TryResetDemo())
            {
                droneSwarmDemoBootstrap.ResetCompleted -= OnResetCompleted;
                waitingForReset = false;
                Debug.LogError("[ScriptsControl] Drone world reset could not be queued.", this);
                FinishSetup(false, "Drone world reset could not be queued.");
                return false;
            }

            remainsAsynchronous = true;
            return true;
        }
        catch (Exception exception)
        {
            if (waitingForReset && droneSwarmDemoBootstrap != null
                && !droneSwarmDemoBootstrap.IsResetQueued)
            {
                droneSwarmDemoBootstrap.ResetCompleted -= OnResetCompleted;
                waitingForReset = false;
            }
            Debug.LogError($"[ScriptsControl] Simulation setup failed: {exception.Message}", this);
            FinishSetup(false, exception.Message);
            return false;
        }
        finally
        {
            if (replay && !pendingReplayActivated && droneSwarmDemoBootstrap != null)
            {
                droneSwarmDemoBootstrap.CancelPreparedNewGame();
            }

            // For an accepted reset, OnResetCompleted owns the setup latch.
            if (!remainsAsynchronous && setupInProgress)
            {
                FinishSetup(false, "Simulation setup did not complete.");
            }
        }
    }

    private bool TryPrepareAndCommitReplayWorld(out string error)
    {
        error = string.Empty;
        TerrainData stagedData = null;
        GameObject stagedTerrainObject = null;
        GameObject stagedForestObject = null;
        bool terrainDataOwnershipTransferred = false;

        try
        {
            stagedData = new TerrainData();
            if (!terrainGenerator.TryGenerateTerrainData(stagedData))
            {
                Debug.LogError("[ScriptsControl] Terrain generation failed; retained replay world was not changed.", this);
                error = "Terrain generation failed.";
                return false;
            }

            stagedTerrainObject = Terrain.CreateTerrainGameObject(stagedData);
            stagedTerrainObject.name = "Replay Terrain (staging)";
            stagedTerrainObject.hideFlags = HideFlags.HideAndDontSave;
            stagedTerrainObject.transform.SetPositionAndRotation(
                terrainGenerator.terrain.transform.position,
                terrainGenerator.terrain.transform.rotation);
            Terrain stagedTerrain = stagedTerrainObject.GetComponent<Terrain>();

            stagedForestObject = new GameObject("Replay Forest (staging)");
            stagedForestObject.hideFlags = HideFlags.HideAndDontSave;
            ForestSpawner stagedForest = stagedForestObject.AddComponent<ForestSpawner>();
            forestSpawner.CopySpawnSettingsTo(stagedForest, stagedTerrain);
            if (!stagedForest.TrySpawnTrees())
            {
                Debug.LogError("[ScriptsControl] Forest generation failed; retained replay world was not changed.", this);
                error = "Forest generation failed.";
                return false;
            }

            if (!TryPrepareExplorerSpawnAgainstStagedWorld(
                stagedTerrain,
                stagedForest,
                out Vector3 spawnPosition))
            {
                Debug.LogError("[ScriptsControl] Explorer spawn failed; retained replay world was not changed.", this);
                error = "Explorer spawn failed.";
                return false;
            }

            // Re-check every fallible eligibility condition before opening the
            // rollback-capable publication. Activation does not retire result drones.
            if (!droneSwarmDemoBootstrap.CanActivatePreparedNewGame()
                || !droneSwarmDemoBootstrap.TryActivatePreparedNewGame())
            {
                error = "Replay activation failed.";
                return false;
            }

            var publication = new ReplayWorldPublication
            {
                StagedData = stagedData,
                Explorer = explorer.CaptureReplayState()
            };
            pendingReplayPublication = publication;
            pendingReplayActivated = true;
            replayCommitExceptionInjection?.Invoke("activation");

            if (!terrainGenerator.TryBeginRuntimeTerrainDataAdoption(
                stagedData,
                out publication.Terrain))
            {
                error = "Terrain replacement failed.";
                return false;
            }
            terrainDataOwnershipTransferred = true;
            replayCommitExceptionInjection?.Invoke("terrain");

            publication.Forest = forestSpawner.BeginAdoptSpawnedTreesFrom(stagedForest);
            replayCommitExceptionInjection?.Invoke("forest");

            explorer.CommitPreparedExplorerSpawn(
                terrainGenerator.terrain,
                forestSpawner,
                spawnPosition);
            replayCommitExceptionInjection?.Invoke("explorer");

            droneSwarmDemoBootstrap.ConfigureGridFromTerrain(
                terrainGenerator.terrain,
                droneSwarmDemoBootstrap.CellSize);
            replayCommitExceptionInjection?.Invoke("grid");
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[ScriptsControl] Transactional replay preparation failed: {exception.Message}", this);
            error = exception.Message;
            return false;
        }
        finally
        {
            DestroyTransient(stagedForestObject);
            if (terrainDataOwnershipTransferred && stagedTerrainObject != null)
            {
                // The live Terrain now owns this data. Remove staging references before
                // its deferred GameObject destruction so teardown cannot mistake the
                // temporary Terrain/Collider for a real shared owner.
                Terrain stagedTerrain = stagedTerrainObject.GetComponent<Terrain>();
                TerrainCollider stagedCollider = stagedTerrainObject.GetComponent<TerrainCollider>();
                if (stagedTerrain != null) stagedTerrain.terrainData = null;
                if (stagedCollider != null) stagedCollider.terrainData = null;
            }
            DestroyTransient(stagedTerrainObject);
            if (!terrainDataOwnershipTransferred)
            {
                DestroyTransient(stagedData);
            }
        }
    }

    private bool TryPrepareExplorerSpawnAgainstStagedWorld(
        Terrain stagedTerrain,
        ForestSpawner stagedForest,
        out Vector3 spawnPosition)
    {
        // Keep retained result visuals visible during staging, but prevent only their
        // colliders from contaminating global Physics queries against the replacement.
        // Staged forest colliders and unrelated world obstacles remain active.
        Collider[] retainedColliders = forestSpawner != null && forestSpawner != stagedForest
            ? forestSpawner.GetComponentsInChildren<Collider>(true)
            : Array.Empty<Collider>();
        bool[] retainedEnabledStates = new bool[retainedColliders.Length];
        for (int i = 0; i < retainedColliders.Length; i++)
        {
            retainedEnabledStates[i] = retainedColliders[i] != null && retainedColliders[i].enabled;
        }

        try
        {
            for (int i = 0; i < retainedColliders.Length; i++)
            {
                if (retainedColliders[i] != null)
                {
                    retainedColliders[i].enabled = false;
                }
            }

            return explorer.TryPrepareExplorerSpawn(stagedTerrain, stagedForest, out spawnPosition);
        }
        finally
        {
            for (int i = 0; i < retainedColliders.Length; i++)
            {
                if (retainedColliders[i] != null)
                {
                    retainedColliders[i].enabled = retainedEnabledStates[i];
                }
            }
        }
    }

    private static void DestroyTransient(UnityEngine.Object transientObject)
    {
        if (transientObject == null) return;
        if (transientObject is GameObject transientGameObject)
        {
            // Destroy is deferred in play mode; make staged colliders/terrain
            // unobservable immediately on both success and rollback.
            transientGameObject.SetActive(false);
        }
        if (Application.isPlaying) Destroy(transientObject);
        else DestroyImmediate(transientObject);
    }

    private void OnResetCompleted(bool succeeded, string error)
    {
        if (!waitingForReset)
        {
            return;
        }

        waitingForReset = false;
        if (droneSwarmDemoBootstrap != null)
        {
            droneSwarmDemoBootstrap.ResetCompleted -= OnResetCompleted;
        }

        if (!succeeded)
        {
            Debug.LogError($"[ScriptsControl] Drone world reset failed: {error}", this);
            FinishSetup(false, error);
            return;
        }

        try
        {
            // Runtime UI is initialized only after the world and required telemetry
            // session are both committed.
            uiScriptsControl.UI_Start();
            FinishSetup(true, string.Empty);
        }
        catch (Exception exception)
        {
            droneSwarmDemoBootstrap.StopSimulation("startup_ui_failed", false);
            Debug.LogError($"[ScriptsControl] Runtime UI startup failed: {exception.Message}", this);
            FinishSetup(false, exception.Message);
        }
    }

    private void CommitReplayPublication(ReplayWorldPublication publication)
    {
        // The reset and UI startup have succeeded. Only now may retained result-world
        // resources be retired.
        droneSwarmDemoBootstrap?.CommitPreparedNewGame();
        forestSpawner?.CommitAdoptSpawnedTrees(publication.Forest);
        terrainGenerator?.CommitRuntimeTerrainDataAdoption(publication.Terrain);
        publication.StagedData = null;
    }

    private void RollbackReplayPublication(ReplayWorldPublication publication)
    {
        // Restore consumers before restoring the data/providers they reference.
        droneSwarmDemoBootstrap?.RollbackPreparedNewGame();
        explorer?.RestoreReplayState(publication.Explorer);
        forestSpawner?.RollbackAdoptSpawnedTrees(publication.Forest);
        terrainGenerator?.RollbackRuntimeTerrainDataAdoption(publication.Terrain);
        DestroyTransient(publication.StagedData);
        publication.StagedData = null;
    }

    private void FinishSetup(bool succeeded, string error)
    {
        Action<bool, string> completion = pendingCompletion;
        PendingSetupKind completedKind = pendingSetupKind;
        bool rollbackInitial = pendingInitialRollbackRequired;
        bool restoreReplayBoundary = pendingReplayActivated;
        ReplayWorldPublication replayPublication = pendingReplayPublication;

        pendingCompletion = null;
        pendingSetupKind = PendingSetupKind.None;
        pendingInitialRollbackRequired = false;
        pendingReplayActivated = false;
        pendingReplayPublication = null;
        setupInProgress = false;
        started = succeeded;

        if (completedKind == PendingSetupKind.Replay && replayPublication != null)
        {
            if (succeeded) CommitReplayPublication(replayPublication);
            else RollbackReplayPublication(replayPublication);
        }

        if (!succeeded)
        {
            if (completedKind == PendingSetupKind.Replay && restoreReplayBoundary
                && replayPublication == null && droneSwarmDemoBootstrap != null)
            {
                droneSwarmDemoBootstrap.RestoreStoppedReplayBoundary();
            }
            else if (completedKind == PendingSetupKind.Initial && rollbackInitial)
            {
                if (droneSwarmDemoBootstrap != null)
                {
                    droneSwarmDemoBootstrap.RollbackInitialStartup();
                }
                if (forestSpawner != null)
                {
                    forestSpawner.ClearSpawnedTrees();
                }
                if (explorer != null)
                {
                    explorer.RollbackFailedStartup();
                }
            }
        }

        completion?.Invoke(succeeded, error ?? string.Empty);
    }

    private void OnDisable()
    {
        CancelPendingStart("ScriptsControl became inactive before startup completed.");
    }

    private void OnDestroy()
    {
        CancelPendingStart("ScriptsControl was destroyed before startup completed.");
        if (droneSwarmDemoBootstrap != null)
        {
            droneSwarmDemoBootstrap.ResetCompleted -= OnResetCompleted;
        }
    }

    private void RejectStart(string message, Action<bool, string> completion, bool error)
    {
        if (error)
        {
            Debug.LogError($"[ScriptsControl] {message}", this);
        }
        else
        {
            Debug.LogWarning($"[ScriptsControl] {message}", this);
        }
        completion?.Invoke(false, message);
    }

    private bool ValidateRequiredReferences(bool batchAutoStartEnabled)
    {
        if (terrainGenerator == null)
        {
            Debug.LogError("[ScriptsControl] A TerrainGenerator reference is required.", this);
            return false;
        }
        if (terrainGenerator.terrain == null || terrainGenerator.terrain.terrainData == null)
        {
            Debug.LogError("[ScriptsControl] TerrainGenerator requires a Terrain with TerrainData.", this);
            return false;
        }
        if (explorer == null)
        {
            Debug.LogError("[ScriptsControl] An Explorer reference is required.", this);
            return false;
        }

        if (batchAutoStartEnabled)
        {
            return true;
        }

        if (forestSpawner == null)
        {
            Debug.LogError("[ScriptsControl] A ForestSpawner reference is required.", this);
            return false;
        }
        if (forestSpawner.terrain == null || forestSpawner.terrain.terrainData == null)
        {
            Debug.LogError("[ScriptsControl] ForestSpawner requires a Terrain with TerrainData.", this);
            return false;
        }
        if (forestSpawner.terrain != terrainGenerator.terrain)
        {
            Debug.LogError("[ScriptsControl] TerrainGenerator and ForestSpawner must reference the same Terrain.", this);
            return false;
        }
        if (droneSwarmDemoBootstrap == null)
        {
            Debug.LogError("[ScriptsControl] A DroneSwarmDemoBootstrap reference is required.", this);
            return false;
        }
        if (!droneSwarmDemoBootstrap.isActiveAndEnabled)
        {
            Debug.LogError("[ScriptsControl] DroneSwarmDemoBootstrap must be active and enabled.", this);
            return false;
        }
        if (!explorer.gameObject.activeInHierarchy)
        {
            Debug.LogError("[ScriptsControl] Explorer GameObject must be active.", this);
            return false;
        }
        if (uiScriptsControl == null)
        {
            Debug.LogError("[ScriptsControl] A UiScriptsControl reference is required.", this);
            return false;
        }

        return true;
    }

    private static bool HasBatchAutoStartEnabled()
    {
        // Include inactive objects so eligibility is explicit rather than dependent on
        // FindObjectsByType's filtering. Only a runner whose Start can actually run owns
        // startup; disabled components and inactive hierarchy members must not block it.
        foreach (var batchRunner in FindObjectsByType<DroneMissionBatchRunner>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (batchRunner != null && batchRunner.isActiveAndEnabled && batchRunner.AutoStartOnPlay)
            {
                return true;
            }
        }

        foreach (var timedRunner in FindObjectsByType<DroneMissionTimedBatchRunner>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (timedRunner != null && timedRunner.isActiveAndEnabled && timedRunner.AutoStartOnPlay)
            {
                return true;
            }
        }

        return false;
    }
}
