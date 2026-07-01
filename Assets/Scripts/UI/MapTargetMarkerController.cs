using System.Collections.Generic;
using UnityEditor.Timeline;
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

    private Material explorerMaterial;
    private Material droneMaterial;

    private float nextRefreshTime;
    private int markerLayer;

    public void MapCameraMarker_Start()
    {
        if (terrain == null)
        {
            terrain = FindAnyObjectByType<Terrain>();
        }

        if (markerLayerName == "Default")
        {
            markerLayerName = "Marker";
        }

        markerLayer = LayerMask.NameToLayer(markerLayerName);

        if (markerLayer < 0)
        {
            Debug.LogWarning($"{markerLayerName} レイヤーが見つかりません。Defaultを使用します。");
            markerLayer = 0;
        }

        explorerMaterial = CreateMaterial(explorerColor);
        droneMaterial = CreateMaterial(droneColor);

        RefreshMarkers();
        ApplyMarkerVisibility();
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
        RefreshExplorerMarker();
        RefreshDroneMarkers();
    }

    private void RefreshExplorerMarker()
    {
        Explorer explorer = FindAnyObjectByType<Explorer>();

        if (explorer == null)
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
        DroneFrontierExplorer[] drones = FindObjectsByType<DroneFrontierExplorer>();

        while (droneMarkers.Count < drones.Length)
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
            droneMarkers[i].SetActive(showDroneMarkers && i < drones.Length);
        }
    }

    private void UpdateExplorerMarker()
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
    }

    private void UpdateDroneMarkers()
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
            Destroy(collider);
        }

        return marker;
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
            explorerMarker.SetActive(showExplorerMarker);
        }

        for (int i = 0; i < droneMarkers.Count; i++)
        {
            if (droneMarkers[i] != null)
            {
                droneMarkers[i].SetActive(showDroneMarkers);
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

    private void OnDestroy()
    {
        if (explorerMaterial != null)
        {
            Destroy(explorerMaterial);
        }

        if (droneMaterial != null)
        {
            Destroy(droneMaterial);
        }
    }
}