using UnityEngine;
using UnityEngine.UI;

public class ExplorerCameraView : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Camera survivorCamera;
    [SerializeField] private RawImage targetImage;

    [Header("Render Texture")]
    [SerializeField] private int textureWidth = 640;
    [SerializeField] private int textureHeight = 360;
    [SerializeField] private int depthBuffer = 16;

    [Header("Camera Settings")]
    [SerializeField] private float fieldOfView = 60f;
    [SerializeField] private float nearClipPlane = 0.1f;
    [SerializeField] private float farClipPlane = 500f;

    [Header("Hidden Layers")]
    [SerializeField] private string[] hiddenLayerNames = { "GridWorld", "Marker" };

    [Header("Fit Mode")]
    [SerializeField] private bool cropToFill = true;

    private RenderTexture renderTexture;
    private Vector2 lastImageSize;

    private void LateUpdate()
    {
        UpdateUvRectIfNeeded();
    }

    [ContextMenu("Setup Survivor Camera View")]
    public void ExplorerCamera_Setup()
    {
        if (survivorCamera == null)
        {
            Debug.LogWarning("Survivor Camera が設定されていません。");
            return;
        }

        if (targetImage == null)
        {
            Debug.LogWarning("Target Image が設定されていません。");
            return;
        }

        survivorCamera.fieldOfView = fieldOfView;
        survivorCamera.nearClipPlane = nearClipPlane;
        survivorCamera.farClipPlane = farClipPlane;
        ApplyHiddenLayers(survivorCamera);

        AudioListener listener = survivorCamera.GetComponent<AudioListener>();

        if (listener != null)
        {
            Destroy(listener);
        }

        CreateRenderTexture();

        survivorCamera.targetTexture = renderTexture;
        targetImage.texture = renderTexture;

        UpdateUvRect();
    }

    private void CreateRenderTexture()
    {
        if (renderTexture != null)
        {
            return;
        }

        renderTexture = new RenderTexture(
            Mathf.Max(1, textureWidth),
            Mathf.Max(1, textureHeight),
            depthBuffer,
            RenderTextureFormat.ARGB32
        );

        renderTexture.name = "SurvivorCamera_RenderTexture";
        renderTexture.Create();
    }

    private void UpdateUvRectIfNeeded()
    {
        if (targetImage == null)
        {
            return;
        }

        Rect rect = targetImage.rectTransform.rect;
        Vector2 currentSize = new Vector2(rect.width, rect.height);

        if (currentSize != lastImageSize)
        {
            UpdateUvRect();
        }
    }

    private void UpdateUvRect()
    {
        if (targetImage == null)
        {
            return;
        }

        if (!cropToFill)
        {
            targetImage.uvRect = new Rect(0f, 0f, 1f, 1f);
            return;
        }

        Rect panelRect = targetImage.rectTransform.rect;

        if (panelRect.width <= 0f || panelRect.height <= 0f)
        {
            return;
        }

        float textureAspect = textureWidth / (float)textureHeight;
        float panelAspect = panelRect.width / panelRect.height;

        if (panelAspect > textureAspect)
        {
            float visibleHeight = textureAspect / panelAspect;
            float y = (1f - visibleHeight) * 0.5f;

            targetImage.uvRect = new Rect(
                0f,
                y,
                1f,
                visibleHeight
            );
        }
        else
        {
            float visibleWidth = panelAspect / textureAspect;
            float x = (1f - visibleWidth) * 0.5f;

            targetImage.uvRect = new Rect(
                x,
                0f,
                visibleWidth,
                1f
            );
        }

        lastImageSize = new Vector2(panelRect.width, panelRect.height);
    }

    private void OnDestroy()
    {
        if (survivorCamera != null && survivorCamera.targetTexture == renderTexture)
        {
            survivorCamera.targetTexture = null;
        }

        if (renderTexture != null)
        {
            renderTexture.Release();
            Destroy(renderTexture);
        }
    }
    private void ApplyHiddenLayers(Camera targetCamera)
    {
        if (targetCamera == null || hiddenLayerNames == null)
        {
            return;
        }

        int mask = targetCamera.cullingMask;

        foreach (string layerName in hiddenLayerNames)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                continue;
            }

            int layer = LayerMask.NameToLayer(layerName);

            if (layer < 0)
            {
                Debug.LogWarning($"{layerName} レイヤーが見つかりません。遭難者カメラでは非表示にできません。");
                continue;
            }

            mask &= ~(1 << layer);
        }

        targetCamera.cullingMask = mask;
    }
}