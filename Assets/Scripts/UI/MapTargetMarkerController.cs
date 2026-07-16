using System.Collections.Generic;
using UnityEngine;

public class MapTargetMarkerController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Terrain terrain;

    [Header("Marker Visibility")]
    [SerializeField] private bool showExplorerMarker = true;
    [SerializeField] private bool showDroneMarkers = true;

    [Header("Marker Settings")]
    [SerializeField] private float markerHeightAboveTerrain = 5f;
    [SerializeField] private float explorerMarkerSize = 3f;
    [SerializeField] private float droneMarkerSize = 2f;

    [SerializeField] private Color explorerColor = Color.yellow;
    [SerializeField] private Color droneColor = Color.cyan;

    [Header("Layer")]
    [SerializeField] private string markerLayerName = "Marker";

    [Header("Refresh")]
    [SerializeField] private float refreshInterval = 0.5f;

    private GameObject explorerMarker;
    private readonly List<GameObject> droneMarkers = new List<GameObject>();
    private readonly HashSet<GameObject> ownedMarkers = new HashSet<GameObject>();

    private Material explorerMaterial;
    private Material droneMaterial;

    private float nextRefreshTime;
    private int markerLayer;
    private bool markerLayerResolved;

    private Explorer cachedExplorer;
    private DroneFrontierExplorer[] cachedDrones = System.Array.Empty<DroneFrontierExplorer>();

    private void Awake()
    {
        ResolveMarkerLayer();
    }

    public void MapCameraMarker_Start()
    {
        if (terrain == null)
        {
            terrain = FindAnyObjectByType<Terrain>();
        }

        ResolveMarkerLayer();
        EnsureMaterials();
        RebindMarkerMaterials();
        RebindMarkerLayers();

        RefreshMarkers();
        ApplyMarkerVisibility();
    }

    private void ResolveMarkerLayer()
    {
        if (string.IsNullOrWhiteSpace(markerLayerName) || markerLayerName == "Default")
        {
            markerLayerName = "Marker";
        }

        markerLayer = LayerMask.NameToLayer(markerLayerName);
        markerLayerResolved = true;

        if (markerLayer < 0)
        {
            Debug.LogWarning($"{markerLayerName} レイヤーが見つかりません。Defaultを使用します。");
            markerLayer = 0;
        }
    }

    private void LateUpdate()
    {
        if (terrain == null)
        {
            terrain = FindAnyObjectByType<Terrain>();
        }

        if (Time.time >= nextRefreshTime)
        {
            RefreshMarkers();
            nextRefreshTime = Time.time + refreshInterval;
        }

        UpdateExplorerMarker();
        UpdateDroneMarkers();

        ApplyMarkerVisibility();
    }

    private void RefreshMarkers()
    {
        cachedExplorer = FindAnyObjectByType<Explorer>();
        cachedDrones = FindObjectsByType<DroneFrontierExplorer>();

        RefreshExplorerMarker();
        RefreshDroneMarkers();
    }

    private void RefreshExplorerMarker()
    {
        if (cachedExplorer == null)
        {
            if (explorerMarker != null)
            {
                explorerMarker.SetActive(false);
            }

            return;
        }

        if (explorerMarker == null)
        {
            explorerMarker = CreateMarker(
                "ExplorerMapMarker",
                explorerMaterial,
                explorerMarkerSize
            );
        }
    }

    private void RefreshDroneMarkers()
    {
        while (droneMarkers.Count < cachedDrones.Length)
        {
            GameObject marker = CreateMarker(
                $"DroneMapMarker_{droneMarkers.Count + 1}",
                droneMaterial,
                droneMarkerSize
            );

            droneMarkers.Add(marker);
        }

        for (int i = 0; i < droneMarkers.Count; i++)
        {
            droneMarkers[i].SetActive(
                showDroneMarkers && i < cachedDrones.Length && cachedDrones[i] != null
            );
        }
    }

    /*private void UpdateExplorerMarker()
    {
        if (!showExplorerMarker)
        {
            return;
        }

        if (explorerMarker == null)
        {
            return;
        }

        Explorer explorer = FindAnyObjectByType<Explorer>();

        if (explorer == null)
        {
            explorerMarker.SetActive(false);
            return;
        }

        explorerMarker.transform.position = GetMarkerPosition(explorer.transform.position);
    }*/
    private void UpdateExplorerMarker()
    {
        if (cachedExplorer == null || explorerMarker == null)
        {
            return;
        }

        explorerMarker.transform.position = GetMarkerPosition(cachedExplorer.transform.position);
    }

    /*private void UpdateDroneMarkers()
    {
        if (!showDroneMarkers)
        {
            return;
        }

        DroneFrontierExplorer[] drones = FindObjectsByType<DroneFrontierExplorer>();

        for (int i = 0; i < drones.Length && i < droneMarkers.Count; i++)
        {
            if (drones[i] == null || droneMarkers[i] == null)
            {
                continue;
            }

            droneMarkers[i].transform.position = GetMarkerPosition(drones[i].transform.position);
        }
    }*/
    private void UpdateDroneMarkers()
    {
        for (int i = 0; i < cachedDrones.Length && i < droneMarkers.Count; i++)
        {
            if (cachedDrones[i] == null || droneMarkers[i] == null)
            {
                continue;
            }

            droneMarkers[i].transform.position = GetMarkerPosition(cachedDrones[i].transform.position);
        }
    }

    private Vector3 GetMarkerPosition(Vector3 targetPosition)
    {
        float y = targetPosition.y + markerHeightAboveTerrain;

        if (terrain != null)
        {
            float terrainY = terrain.SampleHeight(targetPosition) + terrain.transform.position.y;
            y = terrainY + markerHeightAboveTerrain;
        }

        return new Vector3(targetPosition.x, y, targetPosition.z);
    }

    private GameObject CreateMarker(string markerName, Material material, float size)
    {
        if (!markerLayerResolved)
        {
            ResolveMarkerLayer();
        }

        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        marker.name = markerName;

        marker.transform.SetParent(transform, false);
        marker.transform.localScale = new Vector3(size, 0.15f, size);
        marker.layer = markerLayer;

        Renderer renderer = marker.GetComponent<Renderer>();
        renderer.sharedMaterial = material;

        Collider collider = marker.GetComponent<Collider>();

        if (collider != null)
        {
            DestroyRuntimeObject(collider);
        }

        ownedMarkers.Add(marker);
        return marker;
    }

    private void EnsureMaterials()
    {
        if (explorerMaterial == null)
        {
            explorerMaterial = CreateMaterial(explorerColor);
        }
        else
        {
            explorerMaterial.color = explorerColor;
        }

        if (droneMaterial == null)
        {
            droneMaterial = CreateMaterial(droneColor);
        }
        else
        {
            droneMaterial.color = droneColor;
        }
    }

    private void RebindMarkerMaterials()
    {
        SetMarkerMaterial(explorerMarker, explorerMaterial);

        for (int i = 0; i < droneMarkers.Count; i++)
        {
            SetMarkerMaterial(droneMarkers[i], droneMaterial);
        }
    }

    private void RebindMarkerLayers()
    {
        SetMarkerLayer(explorerMarker);

        for (int i = 0; i < droneMarkers.Count; i++)
        {
            SetMarkerLayer(droneMarkers[i]);
        }
    }

    private void SetMarkerLayer(GameObject marker)
    {
        if (marker != null)
        {
            marker.layer = markerLayer;
        }
    }

    private static void SetMarkerMaterial(GameObject marker, Material material)
    {
        if (marker == null)
        {
            return;
        }

        Renderer markerRenderer = marker.GetComponent<Renderer>();

        if (markerRenderer != null)
        {
            markerRenderer.sharedMaterial = material;
        }
    }

    private Material CreateMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");

        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        Material material = new Material(shader);
        material.color = color;

        return material;
    }

    private void ApplyMarkerVisibility()
    {
        if (explorerMarker != null)
        {
            explorerMarker.SetActive(showExplorerMarker && cachedExplorer != null);
        }

        for (int i = 0; i < droneMarkers.Count; i++)
        {
            if (droneMarkers[i] != null)
            {
                droneMarkers[i].SetActive(
                    showDroneMarkers && i < cachedDrones.Length && cachedDrones[i] != null
                );
            }
        }
    }

    public void SetExplorerMarkerVisible(bool visible)
    {
        showExplorerMarker = visible;
        ApplyMarkerVisibility();
    }

    public void SetDroneMarkersVisible(bool visible)
    {
        showDroneMarkers = visible;
        ApplyMarkerVisibility();
    }

    public void SetAllMarkersVisible(bool visible)
    {
        showExplorerMarker = visible;
        showDroneMarkers = visible;
        ApplyMarkerVisibility();
    }

    public void ToggleExplorerMarker()
    {
        SetExplorerMarkerVisible(!showExplorerMarker);
    }

    public void ToggleDroneMarkers()
    {
        SetDroneMarkersVisible(!showDroneMarkers);
    }

    public void ToggleAllMarkers()
    {
        bool nextVisible = !(showExplorerMarker || showDroneMarkers);
        SetAllMarkersVisible(nextVisible);
    }

    private static void DestroyRuntimeObject(Object runtimeObject)
    {
        if (runtimeObject == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(runtimeObject);
        }
        else
        {
            DestroyImmediate(runtimeObject);
        }
    }

    private void OnDestroy()
    {
        // Only markers created by this component are owned. References can be
        // populated by tooling or reflection, so do not destroy arbitrary objects
        // merely because they appear in explorerMarker or droneMarkers.
        foreach (GameObject marker in ownedMarkers)
        {
            if (marker != null)
            {
                DestroyRuntimeObject(marker);
            }
        }

        ownedMarkers.Clear();
        explorerMarker = null;
        droneMarkers.Clear();
        cachedExplorer = null;
        cachedDrones = System.Array.Empty<DroneFrontierExplorer>();

        // Markers must be destroyed before the shared runtime materials they use.
        DestroyRuntimeObject(explorerMaterial);
        DestroyRuntimeObject(droneMaterial);
        explorerMaterial = null;
        droneMaterial = null;
    }
}
