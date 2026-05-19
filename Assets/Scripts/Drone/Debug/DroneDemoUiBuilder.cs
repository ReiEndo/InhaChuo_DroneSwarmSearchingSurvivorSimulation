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

    public DroneDemoUiBuildResult Build(DroneDemoUiBuildConfig config)
    {
        Spawn("EventSystem").AddComponent<EventSystem>().gameObject.AddComponent<InputSystemUIInputModule>();

        var canvasObject = Spawn("Drone Swarm Demo UI");
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasObject.AddComponent<GraphicRaycaster>();

        var panel = CreateUiPanel(canvasObject.transform, "Controls", new Vector2(300f, 270f), new Vector2(18f, -18f));
        var statusText = CreateText(panel.transform, "Status", 14, TextAnchor.UpperLeft);
        statusText.rectTransform.anchorMin = new Vector2(0f, 1f);
        statusText.rectTransform.anchorMax = new Vector2(1f, 1f);
        statusText.rectTransform.offsetMin = new Vector2(12f, -98f);
        statusText.rectTransform.offsetMax = new Vector2(-12f, -10f);

        CreateControlLabel(panel.transform, "Planner", new Vector2(12f, -108f));
        var plannerButton = CreateButton(panel.transform, config.PlannerLabel, new Vector2(88f, -108f));
        plannerButton.GetComponent<RectTransform>().sizeDelta = new Vector2(150f, 28f);
        var plannerButtonText = plannerButton.GetComponentInChildren<Text>();
        plannerButton.onClick.AddListener(() => config.PlannerClicked?.Invoke(plannerButtonText));

        var sensorSlider = CreateSlider(panel.transform, "Sensor", new Vector2(12f, -148f), 1f, 6f, config.SensorRadius);
        sensorSlider.wholeNumbers = true;
        sensorSlider.onValueChanged.AddListener(value => config.SensorRadiusChanged?.Invoke(Mathf.RoundToInt(value)));

        var communicationSlider = CreateSlider(panel.transform, "Comms", new Vector2(12f, -178f), 0.5f, 8f, config.CommunicationRadius);
        communicationSlider.onValueChanged.AddListener(value => config.CommunicationRadiusChanged?.Invoke(value));

        var droneCountSlider = CreateSlider(panel.transform, "Drones", new Vector2(12f, -208f), 1f, 10f, config.DroneCount);
        droneCountSlider.wholeNumbers = true;
        droneCountSlider.onValueChanged.AddListener(value => config.DroneCountChanged?.Invoke(Mathf.RoundToInt(value)));

        CreateControlLabel(panel.transform, "Map", new Vector2(12f, -238f));
        var mapViewButton = CreateButton(panel.transform, config.MapViewLabel, new Vector2(88f, -238f));
        mapViewButton.GetComponent<RectTransform>().sizeDelta = new Vector2(92f, 28f);
        var mapViewButtonText = mapViewButton.GetComponentInChildren<Text>();
        mapViewButton.onClick.AddListener(() => config.MapViewClicked?.Invoke());

        var droneViewButton = CreateButton(panel.transform, "Next", new Vector2(188f, -238f));
        droneViewButton.GetComponent<RectTransform>().sizeDelta = new Vector2(52f, 28f);
        var droneViewButtonText = droneViewButton.GetComponentInChildren<Text>();
        droneViewButton.onClick.AddListener(() => config.DroneViewClicked?.Invoke());

        var resetButton = CreateButton(panel.transform, "Reset", new Vector2(246f, -238f));
        resetButton.GetComponent<RectTransform>().sizeDelta = new Vector2(42f, 28f);
        resetButton.onClick.AddListener(() => config.ResetClicked?.Invoke());

        return new DroneDemoUiBuildResult(statusText, plannerButtonText, sensorSlider, communicationSlider, droneCountSlider, mapViewButtonText, droneViewButtonText);
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

public sealed class DroneDemoUiBuildConfig
{
    public int SensorRadius;
    public float CommunicationRadius;
    public int DroneCount;
    public string PlannerLabel;
    public string MapViewLabel;
    public Action<Text> PlannerClicked;
    public Action<int> SensorRadiusChanged;
    public Action<float> CommunicationRadiusChanged;
    public Action<int> DroneCountChanged;
    public Action MapViewClicked;
    public Action DroneViewClicked;
    public Action ResetClicked;
}

public readonly struct DroneDemoUiBuildResult
{
    public DroneDemoUiBuildResult(Text statusText, Text plannerButtonText, Slider sensorSlider, Slider communicationSlider, Slider droneCountSlider, Text mapViewButtonText, Text droneViewButtonText)
    {
        StatusText = statusText;
        PlannerButtonText = plannerButtonText;
        SensorSlider = sensorSlider;
        CommunicationSlider = communicationSlider;
        DroneCountSlider = droneCountSlider;
        MapViewButtonText = mapViewButtonText;
        DroneViewButtonText = droneViewButtonText;
    }

    public Text StatusText { get; }
    public Text PlannerButtonText { get; }
    public Slider SensorSlider { get; }
    public Slider CommunicationSlider { get; }
    public Slider DroneCountSlider { get; }
    public Text MapViewButtonText { get; }
    public Text DroneViewButtonText { get; }
}
