using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class TerrainGenerator : MonoBehaviour
{
    public const int MinWorldDimension = 4;

    // Caps synchronous generation at a 513² heightmap.
    public const int MaxWorldDimension = 512;
    public const int MinHeightmapResolution = 33;
    public const int MaxHeightmapResolution = MaxWorldDimension + 1;

    public Terrain terrain;

    // These are world-space dimensions, not heightmap sample counts.
    public int widthx = 128;
    public int widthz = 128;

    public float scale = 250f;
    public float terrainHeight = 50f;

    // Runtime replay data is owned; the original TerrainData is borrowed.
    private TerrainData originalTerrainData;
    private TerrainData ownedRuntimeTerrainData;
    private bool originalTerrainDataCaptured;
    private readonly Dictionary<TerrainCollider, TerrainData> originalColliderData =
        new Dictionary<TerrainCollider, TerrainData>();

    public int HeightmapResolution => GetHeightmapResolution(widthx, widthz);
    public TerrainData OwnedRuntimeTerrainData => ownedRuntimeTerrainData;

    // Kept as a void wrapper so existing UnityEvent bindings remain valid.
    public void GenerateTerrain()
    {
        TryGenerateTerrain();
    }

    public bool TryGenerateTerrain()
    {
        if (terrain == null || terrain.terrainData == null)
        {
            Debug.LogError("[TerrainGenerator] A Terrain with TerrainData is required.", this);
            return false;
        }

        return TryGenerateTerrainData(terrain.terrainData);
    }

    /// <summary>Generates into caller-owned data without changing the live Terrain.</summary>
    public bool TryGenerateTerrainData(TerrainData data)
    {
        if (data == null)
        {
            Debug.LogError("[TerrainGenerator] TerrainData is required.", this);
            return false;
        }

        if (float.IsNaN(scale) || float.IsInfinity(scale)
            || float.IsNaN(terrainHeight) || float.IsInfinity(terrainHeight))
        {
            Debug.LogError("[TerrainGenerator] Scale and terrain height must be finite.", this);
            return false;
        }

        if (widthx < MinWorldDimension || widthx > MaxWorldDimension
            || widthz < MinWorldDimension || widthz > MaxWorldDimension)
        {
            Debug.LogError(
                $"[TerrainGenerator] World dimensions must be between {MinWorldDimension} and "
                + $"{MaxWorldDimension}; received {widthx} x {widthz}.",
                this
            );
            return false;
        }

        int generatedWidth = widthx;
        int generatedDepth = widthz;
        float generatedScale = Mathf.Max(1f, scale);
        float generatedHeight = Mathf.Max(1f, terrainHeight);
        int resolution = GetHeightmapResolution(generatedWidth, generatedDepth);

        if (resolution < MinHeightmapResolution || resolution > MaxHeightmapResolution)
        {
            Debug.LogError($"[TerrainGenerator] Invalid heightmap resolution {resolution}.", this);
            return false;
        }

        // TerrainData heightmaps are always square and only support 2^n + 1.
        // World dimensions remain independent and may be rectangular.
        float[,] heights = new float[resolution, resolution];

        float offsetX = Random.Range(0f, 10000f);
        float offsetZ = Random.Range(0f, 10000f);
        const float edgeBorderWorldUnits = 40f;

        for (int z = 0; z < resolution; z++)
        {
            float worldZ = z / (float)(resolution - 1) * generatedDepth;

            for (int x = 0; x < resolution; x++)
            {
                float worldX = x / (float)(resolution - 1) * generatedWidth;
                float nx = worldX / generatedScale + offsetX;
                float nz = worldZ / generatedScale + offsetZ;

                float height = 0.6f * Mathf.PerlinNoise(nx, nz)
                    + 0.3f * Mathf.PerlinNoise(nx * 2f, nz * 2f)
                    + 0.1f * Mathf.PerlinNoise(nx * 4f, nz * 4f);

                float edgeFactor = 1f;
                edgeFactor *= Mathf.Clamp01(worldX / edgeBorderWorldUnits);
                edgeFactor *= Mathf.Clamp01(worldZ / edgeBorderWorldUnits);
                edgeFactor *= Mathf.Clamp01((generatedWidth - worldX) / edgeBorderWorldUnits);
                edgeFactor *= Mathf.Clamp01((generatedDepth - worldZ) / edgeBorderWorldUnits);
                heights[z, x] = height * edgeFactor;
            }
        }

        try
        {
            data.heightmapResolution = resolution;
            data.size = new Vector3(generatedWidth, generatedHeight, generatedDepth);
            data.SetHeights(0, 0, heights);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[TerrainGenerator] Terrain generation failed: {exception.Message}", this);
            return false;
        }

        widthx = generatedWidth;
        widthz = generatedDepth;
        scale = generatedScale;
        terrainHeight = generatedHeight;
        return true;
    }

    /// <summary>Generates replay data while preserving layers and splat maps.</summary>
    public bool TryGenerateReplayTerrainData(TerrainData data, TerrainData renderingSource)
    {
        if (!TryGenerateTerrainData(data))
        {
            return false;
        }

        if (renderingSource == null)
        {
            Debug.LogError("[TerrainGenerator] Replay rendering source is required.", this);
            return false;
        }

        try
        {
            TerrainLayer[] layers = renderingSource.terrainLayers;
            data.terrainLayers = layers;
            data.alphamapResolution = renderingSource.alphamapResolution;
            data.baseMapResolution = renderingSource.baseMapResolution;

            if (layers.Length > 0 && renderingSource.alphamapLayers > 0)
            {
                float[,,] alphamaps = renderingSource.GetAlphamaps(
                    0,
                    0,
                    renderingSource.alphamapWidth,
                    renderingSource.alphamapHeight
                );
                data.SetAlphamaps(0, 0, alphamaps);
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[TerrainGenerator] Could not copy replay terrain rendering data: {exception.Message}", this);
            return false;
        }

        return true;
    }

    public sealed class TerrainPublication
    {
        internal TerrainData PublishedData;
        internal TerrainData DisplacedData;
        internal TerrainData DisplacedOwnedData;
        internal Dictionary<TerrainCollider, TerrainData> ColliderData;
        internal bool Active;
    }

    /// <summary>Publishes replay data while retaining displaced data for rollback.</summary>
    public bool TryBeginRuntimeTerrainDataAdoption(
        TerrainData runtimeData,
        out TerrainPublication publication)
    {
        publication = null;
        if (terrain == null || runtimeData == null)
        {
            Debug.LogError("[TerrainGenerator] A Terrain and runtime TerrainData are required.", this);
            return false;
        }

        if (!originalTerrainDataCaptured)
        {
            originalTerrainData = terrain.terrainData;
            originalTerrainDataCaptured = true;
        }

        var candidate = new TerrainPublication
        {
            PublishedData = runtimeData,
            DisplacedData = terrain.terrainData,
            DisplacedOwnedData = ownedRuntimeTerrainData,
            ColliderData = new Dictionary<TerrainCollider, TerrainData>(),
            Active = true
        };
        foreach (TerrainCollider terrainCollider in FindAssociatedTerrainColliders())
        {
            candidate.ColliderData.Add(terrainCollider, terrainCollider.terrainData);
        }

        try
        {
            terrain.terrainData = runtimeData;
            foreach (TerrainCollider terrainCollider in candidate.ColliderData.Keys)
            {
                terrainCollider.terrainData = runtimeData;
            }
        }
        catch (Exception exception)
        {
            RestoreTerrainPublication(candidate);
            Debug.LogError($"[TerrainGenerator] Could not publish runtime TerrainData: {exception.Message}", this);
            return false;
        }

        ownedRuntimeTerrainData = runtimeData == originalTerrainData ? null : runtimeData;
        publication = candidate;
        return true;
    }

    public void CommitRuntimeTerrainDataAdoption(TerrainPublication publication)
    {
        if (publication == null || !publication.Active) return;
        foreach (KeyValuePair<TerrainCollider, TerrainData> assignment in publication.ColliderData)
        {
            if (assignment.Key != null && !originalColliderData.ContainsKey(assignment.Key))
            {
                originalColliderData.Add(assignment.Key, assignment.Value);
            }
        }
        publication.Active = false;
        if (publication.DisplacedOwnedData != null
            && publication.DisplacedOwnedData != publication.PublishedData)
        {
            DestroyOwnedTerrainDataIfUnused(publication.DisplacedOwnedData);
        }
    }

    public void RollbackRuntimeTerrainDataAdoption(TerrainPublication publication)
    {
        if (publication == null || !publication.Active) return;
        RestoreTerrainPublication(publication);
        ownedRuntimeTerrainData = publication.DisplacedOwnedData;
        publication.Active = false;
    }

    private void RestoreTerrainPublication(TerrainPublication publication)
    {
        foreach (KeyValuePair<TerrainCollider, TerrainData> assignment in publication.ColliderData)
        {
            if (assignment.Key != null) assignment.Key.terrainData = assignment.Value;
        }
        if (terrain != null) terrain.terrainData = publication.DisplacedData;
    }

    public bool TryAdoptRuntimeTerrainData(TerrainData runtimeData)
    {
        if (!TryBeginRuntimeTerrainDataAdoption(runtimeData, out TerrainPublication publication))
        {
            return false;
        }
        CommitRuntimeTerrainDataAdoption(publication);
        return true;
    }

    private void OnDestroy()
    {
        TerrainData ownedData = ownedRuntimeTerrainData;
        ownedRuntimeTerrainData = null;

        // Restore every transactionally managed collider, including child colliders.
        foreach (KeyValuePair<TerrainCollider, TerrainData> assignment in originalColliderData)
        {
            if (assignment.Key != null)
            {
                assignment.Key.terrainData = assignment.Value;
            }
        }
        originalColliderData.Clear();

        // Restore borrowed data before destroying owned data.
        if (ownedData != null && terrain != null && terrain.terrainData == ownedData)
        {
            // Clearing first releases Unity's native reference before the ownership scan.
            terrain.terrainData = null;
            if (originalTerrainDataCaptured && originalTerrainData != null)
            {
                terrain.terrainData = originalTerrainData;
            }
        }

        DestroyOwnedTerrainDataIfUnused(ownedData);
    }

    private List<TerrainCollider> FindAssociatedTerrainColliders()
    {
        var associated = new List<TerrainCollider>();
        if (terrain == null)
        {
            return associated;
        }

        // Associate by hierarchy ownership, not shared TerrainData identity.
        TerrainCollider[] childColliders = terrain.GetComponentsInChildren<TerrainCollider>(true);
        foreach (TerrainCollider terrainCollider in childColliders)
        {
            if (terrainCollider != null
                && terrainCollider.GetComponentInParent<Terrain>(true) == terrain)
            {
                associated.Add(terrainCollider);
            }
        }

        return associated;
    }

    private static void DestroyOwnedTerrainDataIfUnused(TerrainData ownedData)
    {
        if (ownedData == null)
        {
            return;
        }

        // Runtime data may be shared; scan globally before destroying it.
        Terrain[] sceneTerrains = FindObjectsByType<Terrain>(
            FindObjectsInactive.Include);
        foreach (Terrain sceneTerrain in sceneTerrains)
        {
            if (sceneTerrain != null && sceneTerrain.terrainData == ownedData)
            {
                return;
            }
        }

        TerrainCollider[] sceneColliders = FindObjectsByType<TerrainCollider>(
            FindObjectsInactive.Include);
        foreach (TerrainCollider terrainCollider in sceneColliders)
        {
            if (terrainCollider != null && terrainCollider.terrainData == ownedData)
            {
                return;
            }
        }

        if (Application.isPlaying) Destroy(ownedData);
        else DestroyImmediate(ownedData);
    }

    public static int GetHeightmapResolution(int worldWidth, int worldDepth)
    {
        int largestDimension = Mathf.Clamp(
            Mathf.Max(worldWidth, worldDepth),
            MinWorldDimension,
            MaxWorldDimension
        );
        int sampleIntervals = Mathf.NextPowerOfTwo(largestDimension);
        return Mathf.Clamp(sampleIntervals + 1, MinHeightmapResolution, MaxHeightmapResolution);
    }
}
