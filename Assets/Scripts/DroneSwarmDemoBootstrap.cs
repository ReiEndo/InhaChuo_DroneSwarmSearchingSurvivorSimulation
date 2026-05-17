using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
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

        ClearSpawnedObjects();
        explorers.Clear();
        communicationNodes.Clear();
        directTargetReporterIds.Clear();

        BuildWorld();
        BuildCommand();
        BuildDrones();
        BuildDebugRenderer();
        BuildUi();

        communicationHub.ResetCommunicationMemory();
        communicationHub.RefreshNodes();
        resetQueued = false;
    }

    private void Start()
    {
        ResetDemo();
    }

    private void Update()
    {
        UpdateStatusText();
    }

    private void OnValidate()
    {
        width = Mathf.Max(4, width);
        depth = Mathf.Max(4, depth);
        cellSize = Mathf.Max(0.25f, cellSize);
        droneCount = Mathf.Clamp(droneCount, 1, 12);
        sensorRadius = Mathf.Clamp(sensorRadius, 1, 8);
        communicationRadius = Mathf.Max(0f, communicationRadius);
    }

    private void BuildWorld()
    {
        var worldObject = Spawn("Drone Demo Grid World");
        world = worldObject.AddComponent<DroneDemoGridWorld>();
        world.Configure(
            new Vector3(-width * cellSize * 0.5f, 0f, -depth * cellSize * 0.5f),
            cellSize,
            width,
            1,
            depth,
            1 << c_ObstacleLayer,
            1 << c_TargetLayer
        );

        var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        RegisterSpawned(ground);
        ground.name = "Demo Ground";
        ground.layer = c_NonSensedLayer;
        ground.transform.position = world.GridOrigin + new Vector3((width - 1) * cellSize, -0.06f, (depth - 1) * cellSize) * 0.5f;
        ground.transform.localScale = new Vector3(width * cellSize, 0.08f, depth * cellSize);
        SetRendererColor(ground, new Color(0.09f, 0.1f, 0.105f));

        BuildObstacleLine(5, 2, 8, 2);
        BuildObstacleLine(10, 4, 10, 10);
        BuildObstacleLine(13, 1, 15, 1);
        BuildObstacleLine(3, 8, 8, 8);
        BuildTarget(new DroneNative.DroneVec3i(width - 3, 0, depth - 3));

        var hubObject = Spawn("Drone Communication Hub");
        communicationHub = hubObject.AddComponent<DroneSwarmCommunicationHub>();
    }

    private void BuildCommand()
    {
        var command = Spawn("Command");
        command.layer = c_NonSensedLayer;
        command.transform.position = world.GridToWorld(new DroneNative.DroneVec3i(1, 0, 1), 0.35f);
        commandState = command.AddComponent<DroneSwarmAgentState>();
        commandState.DroneId = 0;
        commandState.ConfigureMap(width, 1, depth);

        var node = command.AddComponent<DroneCommunicationNode>();
        node.Configure(communicationRadius, true);
        communicationNodes.Add(node);

        commandRoutePlanner = command.AddComponent<DroneCommandRoutePlanner>();
        commandRoutePlanner.World = world;
        commandRoutePlanner.PlannerType = plannerType;

        var visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        RegisterSpawned(visual);
        visual.name = "Command Visual";
        visual.layer = c_NonSensedLayer;
        visual.transform.SetParent(command.transform, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localScale = new Vector3(0.55f, 0.16f, 0.55f);
        SetRendererColor(visual, new Color(0.88f, 0.88f, 0.92f));
    }

    private void BuildDrones()
    {
        var starts = new[]
        {
            new DroneNative.DroneVec3i(2, 0, 2),
            new DroneNative.DroneVec3i(2, 0, depth - 3),
            new DroneNative.DroneVec3i(width / 2, 0, 2),
            new DroneNative.DroneVec3i(4, 0, depth / 2),
            new DroneNative.DroneVec3i(width - 5, 0, 2),
            new DroneNative.DroneVec3i(1, 0, depth / 2),
            new DroneNative.DroneVec3i(width / 3, 0, depth - 2),
            new DroneNative.DroneVec3i(width - 7, 0, depth - 2),
        };

        int count = Mathf.Clamp(droneCount, 1, 12);
        for (int i = 0; i < count; i++)
        {
            var drone = Spawn($"Drone {i + 1:00}");
            drone.layer = c_NonSensedLayer;
            var start = starts[i % starts.Length];
            drone.transform.position = world.GridToWorld(start, 0.35f) + new Vector3(0f, 0f, (i / starts.Length) * 0.15f);

            var state = drone.AddComponent<DroneSwarmAgentState>();
            state.DroneId = i + 1;
            state.ConfigureMap(width, 1, depth);

            var sensor = drone.AddComponent<DroneGridSensor>();
            sensor.Configure(world, sensorRadius);
            sensor.TargetSensed += HandleDroneTargetSensed;

            var motor = drone.AddComponent<DroneLocalAvoidanceMotor>();
            var node = drone.AddComponent<DroneCommunicationNode>();
            node.Configure(communicationRadius, false);
            communicationNodes.Add(node);

            var explorer = drone.AddComponent<DroneFrontierExplorer>();
            explorer.PlannerType = plannerType;
            explorers.Add(explorer);

            var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            RegisterSpawned(visual);
            visual.name = "Drone Visual";
            visual.layer = c_NonSensedLayer;
            visual.transform.SetParent(drone.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localScale = Vector3.one * Mathf.Max(0.35f, motor.DroneRadius * 1.8f);
            SetRendererColor(visual, Color.Lerp(new Color(0.1f, 0.5f, 1f), new Color(0.1f, 1f, 0.65f), i / Mathf.Max(1f, count - 1f)));
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
        var eventSystemObject = Spawn("EventSystem");
        eventSystemObject.AddComponent<EventSystem>();
        eventSystemObject.AddComponent<InputSystemUIInputModule>();

        var canvasObject = Spawn("Drone Swarm Demo UI");
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasObject.AddComponent<GraphicRaycaster>();

        var panel = CreateUiPanel(canvasObject.transform, "Controls", new Vector2(300f, 270f), new Vector2(18f, -18f));
        statusText = CreateText(panel.transform, "Status", 14, TextAnchor.UpperLeft);
        statusText.rectTransform.anchorMin = new Vector2(0f, 1f);
        statusText.rectTransform.anchorMax = new Vector2(1f, 1f);
        statusText.rectTransform.offsetMin = new Vector2(12f, -98f);
        statusText.rectTransform.offsetMax = new Vector2(-12f, -10f);

        CreateControlLabel(panel.transform, "Planner", new Vector2(12f, -108f));
        var plannerButton = CreateButton(panel.transform, PlannerLabel(), new Vector2(88f, -108f));
        plannerButton.GetComponent<RectTransform>().sizeDelta = new Vector2(150f, 28f);
        plannerButtonText = plannerButton.GetComponentInChildren<Text>();
        plannerButton.onClick.AddListener(() =>
        {
            plannerType = plannerType == DroneNative.PlannerType.AStar
                ? DroneNative.PlannerType.ThetaStar
                : DroneNative.PlannerType.AStar;
            if (plannerButtonText != null)
            {
                plannerButtonText.text = PlannerLabel();
            }
            ApplyPlannerType();
        });

        sensorSlider = CreateSlider(panel.transform, "Sensor", new Vector2(12f, -148f), 1f, 6f, sensorRadius);
        sensorSlider.wholeNumbers = true;
        sensorSlider.onValueChanged.AddListener(value =>
        {
            sensorRadius = Mathf.RoundToInt(value);
            ApplySensorRadius();
        });

        communicationSlider = CreateSlider(panel.transform, "Comms", new Vector2(12f, -178f), 0.5f, 8f, communicationRadius);
        communicationSlider.onValueChanged.AddListener(value =>
        {
            communicationRadius = value;
            ApplyCommunicationRadius();
        });

        droneCountSlider = CreateSlider(panel.transform, "Drones", new Vector2(12f, -208f), 1f, 10f, droneCount);
        droneCountSlider.wholeNumbers = true;
        droneCountSlider.onValueChanged.AddListener(value =>
        {
            droneCount = Mathf.RoundToInt(value);
        });

        CreateControlLabel(panel.transform, "Map", new Vector2(12f, -238f));
        var mapViewButton = CreateButton(panel.transform, MapViewLabel(), new Vector2(88f, -238f));
        mapViewButton.GetComponent<RectTransform>().sizeDelta = new Vector2(92f, 28f);
        mapViewButtonText = mapViewButton.GetComponentInChildren<Text>();
        mapViewButton.onClick.AddListener(() =>
        {
            if (debugRenderer != null)
            {
                debugRenderer.CycleMapViewMode();
                UpdateMapViewButtons();
            }
        });

        var droneViewButton = CreateButton(panel.transform, "Next", new Vector2(188f, -238f));
        droneViewButton.GetComponent<RectTransform>().sizeDelta = new Vector2(52f, 28f);
        droneViewButtonText = droneViewButton.GetComponentInChildren<Text>();
        droneViewButton.onClick.AddListener(() =>
        {
            if (debugRenderer != null)
            {
                debugRenderer.SelectNextDrone();
                UpdateMapViewButtons();
            }
        });

        var resetButton = CreateButton(panel.transform, "Reset", new Vector2(246f, -238f));
        resetButton.GetComponent<RectTransform>().sizeDelta = new Vector2(42f, 28f);
        resetButton.onClick.AddListener(ResetDemo);
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
        statusText.text = $"Planner: {plannerType}  Map: {mapView}\nSensor: {sensorRadius}  Comms: {communicationRadius:0.0}\nDrones: {droneCount}  Links: {links}\nCommand known: {knownCells}\nTarget found: {swarmTarget}  Command: {commandTarget}\nRoute: {route}";
    }

    private string PlannerLabel()
    {
        return plannerType == DroneNative.PlannerType.ThetaStar ? "Theta*" : "A*";
    }

    private void HandleDroneTargetSensed(DroneGridSensor sensor, DroneNative.DroneVec3i cell)
    {
        if (sensor != null && sensor.TryGetComponent<DroneSwarmAgentState>(out var agentState))
        {
            directTargetReporterIds.Add(agentState.DroneId);
        }
    }

    private string MapViewLabel()
    {
        return debugRenderer != null ? debugRenderer.CurrentMapViewLabel : "Merged";
    }

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

    private void BuildObstacleLine(int x0, int z0, int x1, int z1)
    {
        int dx = x1 == x0 ? 0 : (x1 > x0 ? 1 : -1);
        int dz = z1 == z0 ? 0 : (z1 > z0 ? 1 : -1);
        int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(z1 - z0));
        for (int i = 0; i <= steps; i++)
        {
            BuildObstacle(new DroneNative.DroneVec3i(x0 + dx * i, 0, z0 + dz * i));
        }
    }

    private void BuildObstacle(DroneNative.DroneVec3i cell)
    {
        var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        RegisterSpawned(obstacle);
        obstacle.name = $"Obstacle {cell.x},{cell.z}";
        obstacle.layer = c_ObstacleLayer;
        obstacle.transform.position = world.GridToWorld(cell, 0.45f);
        obstacle.transform.localScale = new Vector3(cellSize * 0.86f, 0.9f, cellSize * 0.86f);
        SetRendererColor(obstacle, new Color(0.72f, 0.16f, 0.12f));
    }

    private void BuildTarget(DroneNative.DroneVec3i cell)
    {
        var target = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        RegisterSpawned(target);
        target.name = "Target";
        target.layer = c_TargetLayer;
        target.transform.position = world.GridToWorld(cell, 0.25f);
        target.transform.localScale = new Vector3(cellSize * 0.55f, 0.25f, cellSize * 0.55f);
        SetRendererColor(target, new Color(1f, 0.8f, 0.05f));
    }

    private GameObject Spawn(string objectName)
    {
        var instance = new GameObject(objectName);
        RegisterSpawned(instance);
        return instance;
    }

    private void RegisterSpawned(GameObject instance)
    {
        spawnedObjects.Add(instance);
    }

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

    private static void SetRendererColor(GameObject instance, Color color)
    {
        if (instance.TryGetComponent<Renderer>(out var renderer))
        {
            renderer.material.color = color;
        }
    }

    private static RectTransform CreateUiPanel(Transform parent, string objectName, Vector2 size, Vector2 anchoredPosition)
    {
        var panel = new GameObject(objectName, typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = size;
        rect.anchoredPosition = anchoredPosition;
        panel.GetComponent<Image>().color = new Color(0.03f, 0.035f, 0.04f, 0.82f);
        return rect;
    }

    private static Text CreateText(Transform parent, string objectName, int fontSize, TextAnchor alignment)
    {
        var textObject = new GameObject(objectName, typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(parent, false);
        var text = textObject.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.color = Color.white;
        text.alignment = alignment;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private static Slider CreateSlider(Transform parent, string label, Vector2 anchoredPosition, float min, float max, float value)
    {
        CreateControlLabel(parent, label, anchoredPosition);
        var sliderObject = new GameObject($"{label} Slider", typeof(RectTransform), typeof(Slider));
        sliderObject.transform.SetParent(parent, false);
        var rect = sliderObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(150f, 24f);
        rect.anchoredPosition = anchoredPosition + new Vector2(76f, -2f);

        var background = CreateSliderImage(sliderObject.transform, "Background", new Color(0.13f, 0.14f, 0.15f));
        background.anchorMin = new Vector2(0f, 0.35f);
        background.anchorMax = new Vector2(1f, 0.65f);

        var fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(sliderObject.transform, false);
        var fillAreaRect = fillArea.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = Vector2.zero;
        fillAreaRect.anchorMax = Vector2.one;
        fillAreaRect.offsetMin = Vector2.zero;
        fillAreaRect.offsetMax = Vector2.zero;

        var fill = CreateSliderImage(fillArea.transform, "Fill", new Color(0.1f, 0.55f, 0.9f));
        fill.anchorMin = new Vector2(0f, 0.35f);
        fill.anchorMax = new Vector2(1f, 0.65f);

        var handle = CreateSliderImage(sliderObject.transform, "Handle", new Color(0.9f, 0.92f, 0.94f));
        handle.sizeDelta = new Vector2(12f, 20f);

        var slider = sliderObject.GetComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = value;
        slider.targetGraphic = handle.GetComponent<Image>();
        slider.fillRect = fill;
        slider.handleRect = handle;
        return slider;
    }

    private static RectTransform CreateSliderImage(Transform parent, string objectName, Color color)
    {
        var imageObject = new GameObject(objectName, typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(parent, false);
        var rect = imageObject.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        imageObject.GetComponent<Image>().color = color;
        return rect;
    }

    private static Button CreateButton(Transform parent, string label, Vector2 anchoredPosition)
    {
        var buttonObject = new GameObject($"{label} Button", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(64f, 28f);
        rect.anchoredPosition = anchoredPosition;
        buttonObject.GetComponent<Image>().color = new Color(0.18f, 0.2f, 0.22f);
        var text = CreateText(buttonObject.transform, "Label", 13, TextAnchor.MiddleCenter);
        text.text = label;
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = Vector2.zero;
        text.rectTransform.offsetMax = Vector2.zero;
        return buttonObject.GetComponent<Button>();
    }

    private static void CreateControlLabel(Transform parent, string label, Vector2 anchoredPosition)
    {
        var text = CreateText(parent, $"{label} Label", 12, TextAnchor.MiddleLeft);
        text.text = label;
        text.rectTransform.anchorMin = new Vector2(0f, 1f);
        text.rectTransform.anchorMax = new Vector2(0f, 1f);
        text.rectTransform.pivot = new Vector2(0f, 1f);
        text.rectTransform.sizeDelta = new Vector2(68f, 24f);
        text.rectTransform.anchoredPosition = anchoredPosition;
    }
}
