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
    private const float c_DroneCameraFieldOfView = 75f;

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
    private bool missionComplete;
    private float nextStatusTextUpdateAt;
    private readonly List<Explorer> cachedExplorers = new();
    private float nextExplorerCacheRefreshAt = -1f;

    public bool MissionComplete => missionComplete;
    public bool IsResetQueued => resetQueued;
    public bool IsTelemetrySessionActive => telemetryRecorder != null && telemetryRecorder.HasActiveSession;
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

    public void EndActiveTelemetrySession(string endReason, bool completed)
    {
        EndTelemetrySession(endReason, completed);
    }

    public void ResetDemo()
    {
        if (resetQueued)
        {
            return;
        }

        resetQueued = true;
        StartCoroutine(ResetDemoNextFrame());
    }

    private IEnumerator ResetDemoNextFrame()
    {
        yield return null;

        EndTelemetrySession("reset", false);
        ClearRuntimeState();
        BuildWorld();
        BuildSwarm();
        BuildDebugRenderer();
        //新たにUIを作成するためコメントアウト↓
        //BuildUi();

        communicationHub.ResetCommunicationMemory();
        communicationHub.RefreshNodes();
        CaptureRuntimeConfig();
        BeginTelemetrySession();
        resetQueued = false;
    }

    /*
    Assets\Scripts\ScriptControl\ScriptsControler.csのvoid Start()にて実行
    private void Start() => ResetDemo();
    */

    private void Update()
    {
        ApplyInspectorChanges();
        UpdateTelemetryMilestones();
        CheckMissionComplete();
        UpdateTelemetryTimeout();
        UpdateStatusText();
    }

    private void OnDestroy()
    {
        EndTelemetrySession("destroyed", false);
        ReleaseDroneCameraTextures();
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
        ClearSpawnedObjects();
        explorers.Clear();
        communicationNodes.Clear();
        directTargetReporterIds.Clear();
        missionComplete = false;
        droneCameras.Clear();
        droneCameraViews.Clear();
        cachedExplorers.Clear();
        nextExplorerCacheRefreshAt = -1f;
        ReleaseDroneCameraTextures();
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

        if (!IsBatchRun)
        {
            BuildDroneCameras();
        }
    }

    private void BuildDebugRenderer()
    {
        var rendererObject = Spawn("Drone Swarm Debug Renderer");
        debugRenderer = rendererObject.AddComponent<DroneSwarmDebugRenderer>();
        debugRenderer.Configure(world, commandState, explorers);
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

    private List<Explorer> GetAliveExplorers()
    {
        // only rescan when a cached entry is destroyed or the periodic refresh window elapses.
        bool needsRefresh = Time.time >= nextExplorerCacheRefreshAt;
        for (int i = 0; i < cachedExplorers.Count; i++)
        {
            if (cachedExplorers[i] == null)
            {
                needsRefresh = true;
                break;
            }
        }

        if (!needsRefresh)
        {
            return cachedExplorers;
        }

        cachedExplorers.Clear();
        cachedExplorers.AddRange(FindObjectsByType<Explorer>(FindObjectsSortMode.None));
        nextExplorerCacheRefreshAt = Time.time + 2f;
        return cachedExplorers;
    }

    private void BeginTelemetrySession()
    {
        if (!telemetryEnabled)
        {
            return;
        }

        var recorder = EnsureTelemetryRecorder();
        if (recorder == null)
        {
            return;
        }

        recorder.BeginSession(BuildTelemetryConfig());
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

    private void EndTelemetrySession(string endReason, bool completed)
    {
        if (telemetryRecorder != null && telemetryRecorder.HasActiveSession)
        {
            telemetryRecorder.EndSession(endReason, completed);
        }
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

        telemetryRecorder.EndSession("timeout", false);
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

        missionComplete = true;
        UpdateTelemetryMilestones();
        RecordTelemetryMissionComplete();
        EndTelemetrySession("mission_complete", true);
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

