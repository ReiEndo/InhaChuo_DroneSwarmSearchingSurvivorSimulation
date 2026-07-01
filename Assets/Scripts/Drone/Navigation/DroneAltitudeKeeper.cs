using UnityEngine;

[DisallowMultipleComponent]
public sealed class DroneAltitudeKeeper : MonoBehaviour
{
    [Header("Altitude")]
    [SerializeField] private Terrain terrain;
    [SerializeField] private float flightAltitude = 3f;

    [Header("Correction")]
    [SerializeField] private bool alwaysFollowTerrainHeight = true;
    [SerializeField] private float minimumClearanceMargin = 0.05f;

    private void Awake()
    {
        ResolveTerrain();
    }

    private void LateUpdate()
    {
        ResolveTerrain();

        if (terrain == null || terrain.terrainData == null)
        {
            return;
        }

        Vector3 position = transform.position;

        float surfaceY = terrain.transform.position.y + terrain.SampleHeight(position);
        float desiredY = surfaceY + flightAltitude;

        if (alwaysFollowTerrainHeight || position.y < desiredY - minimumClearanceMargin)
        {
            position.y = desiredY;
            transform.position = position;
        }
    }

    public void Configure(Terrain targetTerrain, float altitude)
    {
        terrain = targetTerrain;
        flightAltitude = Mathf.Max(0f, altitude);
    }

    private void ResolveTerrain()
    {
        if (terrain != null)
        {
            return;
        }

        terrain = Terrain.activeTerrain;

        if (terrain == null)
        {
            terrain = FindAnyObjectByType<Terrain>();
        }
    }
}