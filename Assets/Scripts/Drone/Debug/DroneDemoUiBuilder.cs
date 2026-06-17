using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public sealed class DroneDemoUiBuilder
{
    private readonly Action<GameObject> registerSpawned;

    public DroneDemoUiBuilder(Action<GameObject> registerSpawned)
    {
        this.registerSpawned = registerSpawned;
    }

    private void EnsureEventSystemExists()
    {
        if (EventSystem.current != null)
        {
            return;
        }

        Spawn("EventSystem")
            .AddComponent<EventSystem>()
            .gameObject
            .AddComponent<InputSystemUIInputModule>();
    }

    public DroneDemoUiBuildResult Build(DroneDemoUiBuildConfig config)
    {
        /*EventSystemが二つ生成されているため修正
        Spawn("EventSystem").AddComponent<EventSystem>().gameObject.AddComponent<InputSystemUIInputModule>();
        ↓EventSystemが存在する場合は何もしない。存在しなければ生成。*/
        EnsureEventSystemExists();

        var canvasObject = Spawn("Drone Swarm Demo UI");
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasObject.AddComponent<GraphicRaycaster>();

        var panel = CreateUiPanel(canvasObject.transform, "Controls", new Vector2(320f, 210f), new Vector2(18f, -18f));
        var statusText = CreateText(panel.transform, "Status", 14, TextAnchor.UpperLeft);
        statusText.rectTransform.anchorMin = new Vector2(0f, 1f);
        statusText.rectTransform.anchorMax = new Vector2(1f, 1f);
        statusText.rectTransform.offsetMin = new Vector2(12f, -116f);
        statusText.rectTransform.offsetMax = new Vector2(-12f, -10f);

        CreateControlLabel(panel.transform, "Planner", new Vector2(12f, -126f));
        var plannerButton = CreateButton(panel.transform, config.PlannerLabel, new Vector2(88f, -126f));
        plannerButton.GetComponent<RectTransform>().sizeDelta = new Vector2(150f, 28f);
        var plannerButtonText = plannerButton.GetComponentInChildren<Text>();
        plannerButton.onClick.AddListener(() => config.PlannerClicked?.Invoke(plannerButtonText));

        CreateControlLabel(panel.transform, "Map", new Vector2(12f, -166f));
        var mapViewButton = CreateButton(panel.transform, config.MapViewLabel, new Vector2(88f, -166f));
        mapViewButton.GetComponent<RectTransform>().sizeDelta = new Vector2(92f, 28f);
        var mapViewButtonText = mapViewButton.GetComponentInChildren<Text>();
        mapViewButton.onClick.AddListener(() => config.MapViewClicked?.Invoke());

        var droneViewButton = CreateButton(panel.transform, "Next", new Vector2(188f, -166f));
        droneViewButton.GetComponent<RectTransform>().sizeDelta = new Vector2(52f, 28f);
        var droneViewButtonText = droneViewButton.GetComponentInChildren<Text>();
        droneViewButton.onClick.AddListener(() => config.DroneViewClicked?.Invoke());

        var resetButton = CreateButton(panel.transform, "Reset", new Vector2(246f, -166f));
        resetButton.GetComponent<RectTransform>().sizeDelta = new Vector2(64f, 28f);
        resetButton.onClick.AddListener(() => config.ResetClicked?.Invoke());

        var cameraGrid = CreateDroneCameraGrid(canvasObject.transform);

        return new DroneDemoUiBuildResult(statusText, plannerButtonText, mapViewButtonText, droneViewButtonText, cameraGrid);
    }

    private GameObject Spawn(string objectName)
    {
        var instance = new GameObject(objectName);
        registerSpawned?.Invoke(instance);
        return instance;
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

    private static RectTransform CreateDroneCameraGrid(Transform parent)
    {
        var gridObject = new GameObject("Drone Camera Grid", typeof(RectTransform), typeof(GridLayoutGroup));
        gridObject.transform.SetParent(parent, false);
        var rect = gridObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.sizeDelta = new Vector2(480f, 270f);
        rect.anchoredPosition = new Vector2(-18f, -18f);
        var grid = gridObject.GetComponent<GridLayoutGroup>();
        grid.spacing = new Vector2(4f, 4f);
        grid.childAlignment = TextAnchor.UpperLeft;
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 1;
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

public sealed class DroneDemoUiBuildConfig
{
    public string PlannerLabel;
    public string MapViewLabel;
    public Action<Text> PlannerClicked;
    public Action MapViewClicked;
    public Action DroneViewClicked;
    public Action ResetClicked;
}

public readonly struct DroneDemoUiBuildResult
{
    public DroneDemoUiBuildResult(Text statusText, Text plannerButtonText, Text mapViewButtonText, Text droneViewButtonText, RectTransform droneCameraGrid)
    {
        StatusText = statusText;
        PlannerButtonText = plannerButtonText;
        MapViewButtonText = mapViewButtonText;
        DroneViewButtonText = droneViewButtonText;
        DroneCameraGrid = droneCameraGrid;
    }

    public Text StatusText { get; }
    public Text PlannerButtonText { get; }
    public Text MapViewButtonText { get; }
    public Text DroneViewButtonText { get; }
    public RectTransform DroneCameraGrid { get; }
}
