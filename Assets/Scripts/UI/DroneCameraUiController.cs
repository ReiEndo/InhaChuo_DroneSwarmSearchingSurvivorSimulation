using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.Text.RegularExpressions;

public class DroneCameraUiController : MonoBehaviour
{
    private enum DisplayMode
    {
        All,
        Single
    }

    [Header("UI References")]
    [SerializeField] private RectTransform droneCameraGrid;

    [Header("Grid Layout")]
    [SerializeField] private bool autoColumnCount = true;
    [SerializeField] private int manualColumnCount = 2;
    [SerializeField] private int maxColumnCount = 4;
    [SerializeField] private Vector2 spacing = new Vector2(10f, 10f);
    [SerializeField] private Vector2 padding = new Vector2(10f, 10f);
    [SerializeField] private bool autoFitCellSize = true;
    [SerializeField] private Vector2 manualCellSize = new Vector2(300f, 300f);

    [Header("Single View")]
    [SerializeField] private bool singleViewKeepsSquare = true;

    [Header("Refresh")]
    [SerializeField] private bool refreshOnStart = true;
    [SerializeField] private float startDelay = 0.2f;

    private readonly List<GameObject> createdViewObjects = new List<GameObject>();
    private readonly List<DroneCameraFeed> feeds = new List<DroneCameraFeed>();

    private DisplayMode displayMode = DisplayMode.All;
    private int selectedDroneIndex = 0;

    public void DroneCameraUI_Start()
    {
        if (refreshOnStart)
        {
            StartCoroutine(RefreshAfterDelay());
        }
    }

    private IEnumerator RefreshAfterDelay()
    {
        yield return new WaitForSeconds(startDelay);
        RefreshDroneCameras();
    }

    [ContextMenu("Refresh Drone Cameras")]
    public void RefreshDroneCameras()
    {
        if (droneCameraGrid == null)
        {
            Debug.LogWarning("DroneCameraGrid が設定されていません。");
            return;
        }

        feeds.Clear();

        DroneFrontierExplorer[] drones =
            FindObjectsByType<DroneFrontierExplorer>();

        if (drones.Length == 0)
        {
            Debug.LogWarning("DroneFrontierExplorer を持つドローンが見つかりません。");
            ClearViews();
            return;
        }

        foreach (DroneFrontierExplorer drone in drones)
        {
            if (drone == null)
            {
                continue;
            }

            DroneCameraFeed feed = drone.GetComponent<DroneCameraFeed>();

            if (feed == null)
            {
                feed = drone.gameObject.AddComponent<DroneCameraFeed>();
            }

            feed.Setup();
            feeds.Add(feed);
        }

        feeds.Sort(CompareDroneFeeds);

        selectedDroneIndex = 0;

        RebuildCurrentView();
    }
    private int CompareDroneFeeds(DroneCameraFeed a, DroneCameraFeed b)
    {
        int aNumber = GetDroneNumber(a);
        int bNumber = GetDroneNumber(b);

        return aNumber.CompareTo(bNumber);
    }

    private int GetDroneNumber(DroneCameraFeed feed)
    {
        if (feed == null)
        {
            return int.MaxValue;
        }

        DroneSwarmAgentState state = feed.GetComponent<DroneSwarmAgentState>();

        if (state != null)
        {
            return state.DroneId;
        }

        Match match = Regex.Match(feed.gameObject.name, @"\d+");

        if (match.Success && int.TryParse(match.Value, out int number))
        {
            return number;
        }

        return int.MaxValue;
    }

    public void ShowAllDrones()
    {
        displayMode = DisplayMode.All;
        RebuildCurrentView();
    }

    public void ShowSelectedDrone()
    {
        displayMode = DisplayMode.Single;
        selectedDroneIndex = Mathf.Clamp(selectedDroneIndex, 0, feeds.Count - 1);
        RebuildCurrentView();
    }

    public void ShowNextDrone()
    {
        if (feeds.Count == 0)
        {
            return;
        }

        if (displayMode == DisplayMode.All)
        {
            displayMode = DisplayMode.Single;
            selectedDroneIndex = 0;
        }
        else
        {
            selectedDroneIndex++;

            if (selectedDroneIndex >= feeds.Count)
            {
                selectedDroneIndex = 0;
            }
        }

        RebuildCurrentView();
    }

    public void ShowPreviousDrone()
    {
        if (feeds.Count == 0)
        {
            return;
        }

        if (displayMode == DisplayMode.All)
        {
            displayMode = DisplayMode.Single;
            selectedDroneIndex = 0;
        }
        else
        {
            selectedDroneIndex--;

            if (selectedDroneIndex < 0)
            {
                selectedDroneIndex = feeds.Count - 1;
            }
        }

        RebuildCurrentView();
    }

    public void ToggleAllAndSingle()
    {
        if (displayMode == DisplayMode.All)
        {
            ShowSelectedDrone();
        }
        else
        {
            ShowAllDrones();
        }
    }

    private void RebuildCurrentView()
    {
        ClearViews();

        if (feeds.Count == 0)
        {
            return;
        }

        SetupGridLayout();

        if (displayMode == DisplayMode.All)
        {
            ApplyGridCellSize(feeds.Count);

            foreach (DroneCameraFeed feed in feeds)
            {
                CreateCameraView(feed);
            }
        }
        else
        {
            selectedDroneIndex = Mathf.Clamp(selectedDroneIndex, 0, feeds.Count - 1);

            ApplySingleCellSize();

            DroneCameraFeed selectedFeed = feeds[selectedDroneIndex];
            CreateCameraView(selectedFeed);
        }
    }

    private void SetupGridLayout()
    {
        GridLayoutGroup grid = droneCameraGrid.GetComponent<GridLayoutGroup>();

        if (grid == null)
        {
            grid = droneCameraGrid.gameObject.AddComponent<GridLayoutGroup>();
        }

        grid.spacing = spacing;
        grid.padding = new RectOffset(
            Mathf.RoundToInt(padding.x),
            Mathf.RoundToInt(padding.x),
            Mathf.RoundToInt(padding.y),
            Mathf.RoundToInt(padding.y)
        );

        grid.childAlignment = displayMode == DisplayMode.Single
            ? TextAnchor.MiddleCenter
            : TextAnchor.UpperLeft;
    }

    private void ApplyGridCellSize(int cameraCount)
    {
        GridLayoutGroup grid = droneCameraGrid.GetComponent<GridLayoutGroup>();

        if (grid == null)
        {
            return;
        }

        if (cameraCount <= 0)
        {
            return;
        }

        if (!autoFitCellSize)
        {
            int columns = autoColumnCount
                ? CalculateBestColumnCount(cameraCount)
                : Mathf.Max(1, manualColumnCount);

            float manualSquareSize = Mathf.Min(manualCellSize.x, manualCellSize.y);

            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = columns;
            grid.cellSize = new Vector2(manualSquareSize, manualSquareSize);

            return;
        }

        int bestColumns = autoColumnCount
            ? CalculateBestColumnCount(cameraCount)
            : Mathf.Max(1, manualColumnCount);

        int bestRows = Mathf.CeilToInt(cameraCount / (float)bestColumns);

        Rect rect = droneCameraGrid.rect;

        float availableWidth =
            rect.width
            - padding.x * 2f
            - spacing.x * Mathf.Max(0, bestColumns - 1);

        float availableHeight =
            rect.height
            - padding.y * 2f
            - spacing.y * Mathf.Max(0, bestRows - 1);

        float cellWidth = availableWidth / bestColumns;
        float cellHeight = availableHeight / bestRows;

        float squareSize = Mathf.Min(cellWidth, cellHeight);
        squareSize = Mathf.Max(1f, squareSize);

        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = bestColumns;
        grid.cellSize = new Vector2(squareSize, squareSize);
    }

    private void ApplySingleCellSize()
    {
        GridLayoutGroup grid = droneCameraGrid.GetComponent<GridLayoutGroup>();

        if (grid == null)
        {
            return;
        }

        Rect rect = droneCameraGrid.rect;

        float availableWidth = rect.width - padding.x * 2f;
        float availableHeight = rect.height - padding.y * 2f;

        Vector2 cellSize;

        if (singleViewKeepsSquare)
        {
            float squareSize = Mathf.Min(availableWidth, availableHeight);
            squareSize = Mathf.Max(1f, squareSize);
            cellSize = new Vector2(squareSize, squareSize);
        }
        else
        {
            cellSize = new Vector2(
                Mathf.Max(1f, availableWidth),
                Mathf.Max(1f, availableHeight)
            );
        }

        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 1;
        grid.cellSize = cellSize;
    }

    private int CalculateBestColumnCount(int cameraCount)
    {
        int maxColumns = Mathf.Clamp(maxColumnCount, 1, cameraCount);

        Rect rect = droneCameraGrid.rect;

        int bestColumns = 1;
        float bestSquareSize = 0f;

        for (int columns = 1; columns <= maxColumns; columns++)
        {
            int rows = Mathf.CeilToInt(cameraCount / (float)columns);

            float availableWidth =
                rect.width
                - padding.x * 2f
                - spacing.x * Mathf.Max(0, columns - 1);

            float availableHeight =
                rect.height
                - padding.y * 2f
                - spacing.y * Mathf.Max(0, rows - 1);

            if (availableWidth <= 0f || availableHeight <= 0f)
            {
                continue;
            }

            float cellWidth = availableWidth / columns;
            float cellHeight = availableHeight / rows;

            float squareSize = Mathf.Min(cellWidth, cellHeight);

            if (squareSize > bestSquareSize)
            {
                bestSquareSize = squareSize;
                bestColumns = columns;
            }
        }

        return bestColumns;
    }

    private void CreateCameraView(DroneCameraFeed feed)
    {
        GameObject panel = new GameObject(
            $"{feed.DisplayName}_View",
            typeof(RectTransform),
            typeof(Image)
        );

        panel.transform.SetParent(droneCameraGrid, false);

        Image background = panel.GetComponent<Image>();
        background.color = new Color(0.04f, 0.045f, 0.05f, 1f);

        RawImage cameraImage = CreateRawImage(panel.transform, "CameraImage");
        Text label = CreateLabel(panel.transform, "Label", feed.DisplayName);

        RectTransform imageRect = cameraImage.rectTransform;
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = new Vector2(4f, 4f);
        imageRect.offsetMax = new Vector2(-4f, -4f);

        RectTransform labelRect = label.rectTransform;
        labelRect.anchorMin = new Vector2(0f, 1f);
        labelRect.anchorMax = new Vector2(1f, 1f);
        labelRect.pivot = new Vector2(0.5f, 1f);
        labelRect.offsetMin = new Vector2(6f, -26f);
        labelRect.offsetMax = new Vector2(-6f, -2f);

        cameraImage.texture = feed.Texture;

        createdViewObjects.Add(panel);
    }

    private RawImage CreateRawImage(Transform parent, string objectName)
    {
        GameObject imageObject = new GameObject(
            objectName,
            typeof(RectTransform),
            typeof(RawImage)
        );

        imageObject.transform.SetParent(parent, false);

        RawImage rawImage = imageObject.GetComponent<RawImage>();
        rawImage.color = Color.white;

        return rawImage;
    }

    private Text CreateLabel(Transform parent, string objectName, string text)
    {
        GameObject textObject = new GameObject(
            objectName,
            typeof(RectTransform),
            typeof(Text)
        );

        textObject.transform.SetParent(parent, false);

        Text label = textObject.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = 13;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleLeft;
        label.text = text;

        return label;
    }

    private void ClearViews()
    {
        foreach (GameObject viewObject in createdViewObjects)
        {
            if (viewObject != null)
            {
                Destroy(viewObject);
            }
        }

        createdViewObjects.Clear();

        for (int i = droneCameraGrid.childCount - 1; i >= 0; i--)
        {
            Destroy(droneCameraGrid.GetChild(i).gameObject);
        }
    }
}