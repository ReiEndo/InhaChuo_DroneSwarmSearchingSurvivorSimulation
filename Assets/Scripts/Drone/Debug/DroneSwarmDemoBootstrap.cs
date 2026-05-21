using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public sealed class DroneSwarmDemoBootstrap : MonoBehaviour
{
    private const int c_ObstacleLayer = 0;
    private const int c_TargetLayer = 4;
    private const int c_NonSensedLayer = 2;

    [Header("Grid")]
    [SerializeField] private int width = 18;
    [SerializeField] private int depth = 12;
    [SerializeField] private float cellSize = 1f;

    [Header("Swarm")]
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
    private Slider sensorSlider;
    private Slider communicationSlider;
    private Slider droneCountSlider;
    private Text mapViewButtonText;
    private Text droneViewButtonText;
    private Text cameraViewButtonText;
    private RawImage droneCameraView;
    private RenderTexture droneCameraTexture;
    private readonly List<Camera> droneCameras = new();
    private int selectedCameraIndex = -1;
    private bool resetQueued;

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
        resetQueued = false;
    }

    private void Start() => ResetDemo();

    private void Update() => UpdateStatusText();

    private void OnDestroy() => ReleaseDroneCameraTexture();

    private void OnValidate()
    {
        width = Mathf.Max(4, width);
        depth = Mathf.Max(4, depth);
        cellSize = Mathf.Max(0.25f, cellSize);
        droneCount = Mathf.Clamp(droneCount, 1, 12);
        sensorRadius = Mathf.Clamp(sensorRadius, 1, 8);
        communicationRadius = Mathf.Max(0f, communicationRadius);
        droneSpeed = Mathf.Max(0f, droneSpeed);
    }

    private void ClearRuntimeState()
    {
        ClearSpawnedObjects();
        explorers.Clear();
        communicationNodes.Clear();
        directTargetReporterIds.Clear();
        droneCameras.Clear();
        selectedCameraIndex = -1;
        ReleaseDroneCameraTexture();
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
            SensorRadius = sensorRadius,
            CommunicationRadius = communicationRadius,
            DroneCount = droneCount,
            DroneSpeed = droneSpeed,
            PlannerLabel = PlannerLabel(),
            MapViewLabel = MapViewLabel(),
            PlannerClicked = HandlePlannerClicked,
            SensorRadiusChanged = value => { sensorRadius = value; ApplySensorRadius(); },
            CommunicationRadiusChanged = value => { communicationRadius = value; ApplyCommunicationRadius(); },
            DroneCountChanged = value => droneCount = value,
            DroneSpeedChanged = value => { droneSpeed = value; ApplyDroneSpeed(); },
            MapViewClicked = HandleMapViewClicked,
            DroneViewClicked = HandleDroneViewClicked,
            CameraViewClicked = HandleCameraViewClicked,
            ResetClicked = ResetDemo,
        });

        statusText = ui.StatusText;
        plannerButtonText = ui.PlannerButtonText;
        sensorSlider = ui.SensorSlider;
        communicationSlider = ui.CommunicationSlider;
        droneCountSlider = ui.DroneCountSlider;
        mapViewButtonText = ui.MapViewButtonText;
        droneViewButtonText = ui.DroneViewButtonText;
        cameraViewButtonText = ui.CameraViewButtonText;
        droneCameraView = ui.DroneCameraView;
        ConfigureDroneCameraView();
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
            cameraObject.transform.localPosition = new Vector3(0f, 0.45f, 0.15f);
            cameraObject.transform.localRotation = Quaternion.Euler(18f, 0f, 0f);
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = 75f;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 250f;
            camera.clearFlags = CameraClearFlags.Skybox;
            droneCameras.Add(camera);
        }
    }

    private void HandleCameraViewClicked()
    {
        if (droneCameras.Count == 0)
        {
            selectedCameraIndex = -1;
        }
        else
        {
            selectedCameraIndex = (selectedCameraIndex + 1) % droneCameras.Count;
        }

        ConfigureDroneCameraView();
    }

    private void ConfigureDroneCameraView()
    {
        if (droneCameraView == null)
        {
            return;
        }

        if (droneCameraTexture == null)
        {
            droneCameraTexture = new RenderTexture(480, 270, 16, RenderTextureFormat.ARGB32);
            droneCameraTexture.name = "Drone Camera View Texture";
        }

        for (int i = 0; i < droneCameras.Count; i++)
        {
            if (droneCameras[i] != null)
            {
                droneCameras[i].enabled = i == selectedCameraIndex;
                droneCameras[i].targetTexture = i == selectedCameraIndex ? droneCameraTexture : null;
            }
        }

        bool show = selectedCameraIndex >= 0 && selectedCameraIndex < droneCameras.Count;
        droneCameraView.enabled = show;
        droneCameraView.texture = show ? droneCameraTexture : null;
        if (cameraViewButtonText != null)
        {
            cameraViewButtonText.text = show ? $"Cam D{selectedCameraIndex + 1:00}" : "Cam";
        }
    }

    private void ReleaseDroneCameraTexture()
    {
        if (droneCameraTexture != null)
        {
            droneCameraTexture.Release();
            Destroy(droneCameraTexture);
            droneCameraTexture = null;
        }
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
        string cameraView = selectedCameraIndex >= 0 ? $"D{selectedCameraIndex + 1:00}" : "off";
        statusText.text = $"Planner: {plannerType}  Map: {mapView}\nSensor: {sensorRadius}  Comms: {communicationRadius:0.0}\nDrones: {droneCount}  Speed: {droneSpeed:0.0}  Links: {links}\nCamera: {cameraView}\nCommand known: {knownCells}\nTarget found: {swarmTarget}  Command: {commandTarget}\nRoute: {route}";
    }

    private string PlannerLabel() => plannerType == DroneNative.PlannerType.ThetaStar ? "Theta*" : "A*";

    private void HandleDroneTargetSensed(DroneGridSensor sensor, DroneNative.DroneVec3i cell)
    {
        if (sensor != null && sensor.TryGetComponent<DroneSwarmAgentState>(out var agentState))
        {
            directTargetReporterIds.Add(agentState.DroneId);
        }
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
