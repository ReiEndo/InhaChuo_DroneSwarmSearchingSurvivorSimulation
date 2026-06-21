using System.Collections;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.UI;

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

        ClearRuntimeState();
        BuildWorld();
        BuildSwarm();
        BuildDebugRenderer();
        BuildUi();

        communicationHub.ResetCommunicationMemory();
        communicationHub.RefreshNodes();
        CaptureRuntimeConfig();
        resetQueued = false;
    }

    /*
    Assets\Scripts\ScriptControl\ScriptsControler.csのvoid Start()にて実行
    private void Start() => ResetDemo();
    */

    private void Update()
    {
        ApplyInspectorChanges();
        CheckMissionComplete();
        UpdateStatusText();
    }

    private void OnDestroy() => ReleaseDroneCameraTextures();

    private void OnValidate()
    {
        width = Mathf.Max(4, width);
        depth = Mathf.Max(4, depth);
        cellSize = Mathf.Max(0.25f, cellSize);
        droneCount = Mathf.Clamp(droneCount, 1, 12);
        sensorRadius = Mathf.Clamp(sensorRadius, 1, 8);
        communicationRadius = Mathf.Max(0f, communicationRadius);
        droneSpeed = Mathf.Max(0f, droneSpeed);
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
        BuildDroneCameras();
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

        int knownCells = commandState != null && commandState.LocalMap != null
            ? commandState.LocalMap.CountKnownCells()
            : 0;
        string swarmTarget = directTargetReporterIds.Count > 0 ? "yes" : "no";
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
        if (sensor != null && sensor.TryGetComponent<DroneSwarmAgentState>(out var agentState))
        {
            directTargetReporterIds.Add(agentState.DroneId);
        }

        StopHumanAtTarget(sensor, cell);
    }

    private void StopHumanAtTarget(DroneGridSensor sensor, DroneNative.DroneVec3i cell)
    {
        if (sensor == null || sensor.World == null)
        {
            return;
        }

        foreach (Explorer human in FindObjectsByType<Explorer>(FindObjectsSortMode.None))
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

    private void CheckMissionComplete()
    {
        if (missionComplete || directTargetReporterIds.Count == 0 || explorers.Count == 0)
        {
            return;
        }

        if (!AllExplorersKnowTargetFound())
        {
            return;
        }

        if (!TryGetFirstTargetFinderState(out var firstFinderState)
            || !firstFinderState.AllExpectedDronesTargetInformed)
        {
            return;
        }

        missionComplete = true;
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

    private bool TryGetFirstTargetFinderState(out DroneSwarmAgentState firstFinderState)
    {
        firstFinderState = null;
        DroneTargetReport firstReport = default;
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
}
