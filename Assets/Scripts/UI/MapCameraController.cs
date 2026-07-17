using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class SimpleTerrainMapCamera : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Terrain terrain;
    [SerializeField] private RawImage mapImage;

    [Header("Render Texture")]
    [SerializeField] private int textureSize = 1024;

    [Header("Camera")]
    [SerializeField] private float cameraHeightAboveTerrain = 200f;
    [SerializeField] private float orthographicMargin = 1.05f;
    [SerializeField] private Color backgroundColor = Color.black;

    [Header("Layer")]
    [SerializeField] private string hiddenLayerName = "Obstacle";

    private Camera mapCamera;
    private RenderTexture mapTexture;

    private Vector3 cachedTerrainPosition;
    private Vector3 cachedTerrainSize;

    public void MapCamera_Start()
    {
        ResolveReferences();
        SetupCamera();
        UpdateCameraToTerrain();
        ApplyTextureToUi();
    }

    private void LateUpdate()
    {
        ResolveReferences();

        if (terrain == null || terrain.terrainData == null || mapCamera == null)
        {
            return;
        }

        if (TerrainChanged())
        {
            UpdateCameraToTerrain();
        }
    }

    private void ResolveReferences()
    {
        if (terrain == null)
        {
            terrain = FindAnyObjectByType<Terrain>();
        }
    }

    private void SetupCamera()
    {
        if (mapCamera == null)
        {
            GameObject cameraObject = new GameObject("SimpleTerrainMapCamera");
            cameraObject.transform.SetParent(transform, false);

            mapCamera = cameraObject.AddComponent<Camera>();
            mapCamera.orthographic = true;
            mapCamera.clearFlags = CameraClearFlags.SolidColor;
            mapCamera.backgroundColor = backgroundColor;
            mapCamera.allowHDR = false;
            mapCamera.allowMSAA = false;

            AudioListener listener = cameraObject.GetComponent<AudioListener>();

            if (listener != null)
            {
                Destroy(listener);
            }
        }

        EnsureRenderTexture();

        mapCamera.targetTexture = mapTexture;

        int hiddenLayer = LayerMask.NameToLayer(hiddenLayerName);

        if (hiddenLayer >= 0)
        {
            mapCamera.cullingMask = ~LayerMask.GetMask(hiddenLayerName);
        }
        else
        {
            Debug.LogWarning($"{hiddenLayerName} レイヤーが見つかりません。全レイヤーを表示します。");
            mapCamera.cullingMask = ~0;
        }
    }

    private void EnsureRenderTexture()
    {
        int size = Mathf.Max(64, textureSize);

        if (mapTexture != null
            && mapTexture.width == size
            && mapTexture.height == size)
        {
            return;
        }

        if (mapCamera != null && mapCamera.targetTexture == mapTexture)
        {
            mapCamera.targetTexture = null;
        }

        if (mapTexture != null)
        {
            mapTexture.Release();
            Destroy(mapTexture);
        }

        mapTexture = new RenderTexture(size, size, 16, RenderTextureFormat.ARGB32);
        mapTexture.name = "SimpleTerrainMap_RenderTexture";
        mapTexture.Create();
    }

    private bool TerrainChanged()
    {
        return cachedTerrainPosition != terrain.transform.position
            || cachedTerrainSize != terrain.terrainData.size;
    }

    private void UpdateCameraToTerrain()
    {
        TerrainData data = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;
        Vector3 terrainSize = data.size;

        Vector3 center = terrainPos + new Vector3(
            terrainSize.x * 0.5f,
            0f,
            terrainSize.z * 0.5f
        );

        float cameraY = terrainPos.y + terrainSize.y + cameraHeightAboveTerrain;

        mapCamera.transform.position = new Vector3(
            center.x,
            cameraY,
            center.z
        );

        mapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        mapCamera.orthographic = true;
        mapCamera.aspect = 1f;

        float halfX = terrainSize.x * 0.5f;
        float halfZ = terrainSize.z * 0.5f;

        mapCamera.orthographicSize = Mathf.Max(halfX, halfZ) * orthographicMargin;

        mapCamera.nearClipPlane = 0.1f;
        mapCamera.farClipPlane = cameraHeightAboveTerrain + terrainSize.y + 100f;

        cachedTerrainPosition = terrainPos;
        cachedTerrainSize = terrainSize;
    }

    private void ApplyTextureToUi()
    {
        if (mapImage == null)
        {
            Debug.LogWarning("MapImage が設定されていません。");
            return;
        }

        mapImage.texture = mapTexture;
        mapImage.uvRect = new Rect(0f, 0f, 1f, 1f);
    }

    private void OnDestroy()
    {
        if (mapCamera != null && mapCamera.targetTexture == mapTexture)
        {
            mapCamera.targetTexture = null;
        }

        if (mapTexture != null)
        {
            mapTexture.Release();
            Destroy(mapTexture);
        }
    }
}