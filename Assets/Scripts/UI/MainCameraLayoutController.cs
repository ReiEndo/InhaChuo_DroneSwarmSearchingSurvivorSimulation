using UnityEngine;

public class MainCameraLayoutController : MonoBehaviour
{
    [Header("Main Panels")]
    [SerializeField] private RectTransform droneCameraArea;
    [SerializeField] private RectTransform rightArea;

    [Header("Right Area Panels")]
    [SerializeField] private RectTransform mapPanel;
    [SerializeField] private RectTransform explorerCameraPanel;

    [Header("Layout Settings")]
    [SerializeField] private float rightWidth = 520f;
    [SerializeField] private float gap = 20f;
    [SerializeField] private float padding = 10f;
    [SerializeField] private float mapGap = 10f;

    [Header("Map Settings")]
    [SerializeField] private bool mapSizeFollowsRightWidth = true;
    [SerializeField] private float manualMapSize = 400f;

    private void Start()
    {
        ApplyLayout();
    }

    private void OnValidate()
    {
        if (!HasAllReferences())
        {
            return;
        }

        ApplyLayout();
    }

    private void OnRectTransformDimensionsChange()
    {
        if (!HasAllReferences())
        {
            return;
        }

        ApplyLayout();
    }

    public void ApplyLayout()
    {
        if (!HasAllReferences())
        {
            return;
        }

        rightWidth = Mathf.Max(200f, rightWidth);
        gap = Mathf.Max(0f, gap);
        padding = Mathf.Max(0f, padding);
        mapGap = Mathf.Max(0f, mapGap);

        float innerRightWidth = Mathf.Max(0f, rightWidth - padding * 2f);

        float mapSize = mapSizeFollowsRightWidth
            ? innerRightWidth
            : Mathf.Min(manualMapSize, innerRightWidth);

        mapSize = Mathf.Max(100f, mapSize);

        SetDroneCameraArea();
        SetRightArea();
        SetMapPanel(mapSize);
        SetexplorerCameraPanel(mapSize);
    }

    private void SetDroneCameraArea()
    {
        droneCameraArea.anchorMin = new Vector2(0f, 0f);
        droneCameraArea.anchorMax = new Vector2(1f, 1f);
        droneCameraArea.pivot = new Vector2(0.5f, 0.5f);

        droneCameraArea.offsetMin = new Vector2(0f, 0f);
        droneCameraArea.offsetMax = new Vector2(-(rightWidth + gap), 0f);
    }

    private void SetRightArea()
    {
        rightArea.anchorMin = new Vector2(1f, 0f);
        rightArea.anchorMax = new Vector2(1f, 1f);
        rightArea.pivot = new Vector2(1f, 0.5f);

        rightArea.anchoredPosition = Vector2.zero;
        rightArea.sizeDelta = new Vector2(rightWidth, 0f);
    }

    private void SetMapPanel(float mapSize)
    {
        mapPanel.anchorMin = new Vector2(0.5f, 1f);
        mapPanel.anchorMax = new Vector2(0.5f, 1f);
        mapPanel.pivot = new Vector2(0.5f, 1f);

        mapPanel.anchoredPosition = new Vector2(0f, -padding);
        mapPanel.sizeDelta = new Vector2(mapSize, mapSize);
    }

    private void SetexplorerCameraPanel(float mapSize)
    {
        explorerCameraPanel.anchorMin = new Vector2(0f, 0f);
        explorerCameraPanel.anchorMax = new Vector2(1f, 1f);
        explorerCameraPanel.pivot = new Vector2(0.5f, 0.5f);

        float topOffset = padding + mapSize + mapGap;

        explorerCameraPanel.offsetMin = new Vector2(padding, padding);
        explorerCameraPanel.offsetMax = new Vector2(-padding, -topOffset);
    }

    private bool HasAllReferences()
    {
        return droneCameraArea != null
            && rightArea != null
            && mapPanel != null
            && explorerCameraPanel != null;
    }

    public void SetNormalLayout()
    {
        rightWidth = 520f;
        gap = 20f;
        padding = 10f;
        mapGap = 10f;
        mapSizeFollowsRightWidth = true;

        ApplyLayout();
    }

    public void SetDroneCameraFocusLayout()
    {
        rightWidth = 420f;
        gap = 16f;
        padding = 10f;
        mapGap = 10f;
        mapSizeFollowsRightWidth = true;

        ApplyLayout();
    }

    public void SetRightInfoFocusLayout()
    {
        rightWidth = 700f;
        gap = 20f;
        padding = 12f;
        mapGap = 12f;
        mapSizeFollowsRightWidth = true;

        ApplyLayout();
    }
}