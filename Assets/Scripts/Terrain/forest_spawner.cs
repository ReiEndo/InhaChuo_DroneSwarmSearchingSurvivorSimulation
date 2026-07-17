using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Random = UnityEngine.Random;

public class ForestSpawner : MonoBehaviour
{
    // Hard limits protect the main thread from synchronous generation.
    public const int MaximumSpawnedObjectCount = 5000;
    public const int MaximumAttemptsPerObject = 100;
    public const long MaximumTotalSpawnAttempts = 100000L;

    [Header("Terrain")]
    public Terrain terrain;

    [Header("Prefab Settings")]
    public GameObject treePrefab;
    public GameObject rockPrefab;

    [Header("Density Settings")]
    [Tooltip("The requested number of trees & rocks per 100m². The effective density is limited by Maximum Generated Objects.")]
    [Min(0f)]
    public float objectsPer100SquareMeters = 2.5f;

    [Tooltip("Maximum generated objects retained across additive spawn calls. ClearSpawnedTrees resets this capacity; values above 5,000 are clamped.")]
    [Range(1, MaximumSpawnedObjectCount)]
    public int maximumGeneratedObjects = MaximumSpawnedObjectCount;

    [Tooltip("Trees or Rocks percentage")]
    [Range(0f, 100f)]
    public float treePercent = 66f;

    [Header("Spawn Area")]
    public float minHeight = 0f;
    public float maxHeight = 100f;

    [Header("Slope Limit")]
    [Range(0, 60)]
    public float maxSlope = 30f;

    [Header("Random Scale")]
    public Vector2 scaleRange = new Vector2(0.8f, 1.2f);

    [Header("Tree Distance Limit")]
    public float minDistance = 3f;

    [Tooltip("Attempts per requested object. Clamped to 1-100; total attempts are also capped at 100,000.")]
    [Range(1, MaximumAttemptsPerObject)]
    public int maxSpawnAttemptCounts = 20;

    [Header("Drone Sensing")]
    [Tooltip("Layer used by DroneDemoGridWorld as blocked/obstacle. ProjectSettings currently defines Obstacle as layer 7.")]
    public int obstacleLayer = 7;

    [Tooltip("Apply the obstacle layer to every spawned tree child so child colliders are sensed correctly.")]
    public bool setLayerRecursively = true;

    [Tooltip("Add one simple trigger collider to tree prefabs that do not already have colliders. Keeps sensing cheap compared with mesh colliders.")]
    public bool addMissingCollisionProxy = true;
    public float proxyRadius = 0.35f;
    public float proxyHeight = 2.5f;

    private List<Vector2> spawnedTreePositions = new List<Vector2>();

    public IReadOnlyList<Vector2> SpawnTreePositions
    {
        get { return spawnedTreePositions; }
    }

    public void CopySpawnSettingsTo(ForestSpawner target, Terrain targetTerrain)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));

        target.terrain = targetTerrain;
        target.treePrefab = treePrefab;
        target.rockPrefab = rockPrefab;
        target.objectsPer100SquareMeters = objectsPer100SquareMeters;
        target.maximumGeneratedObjects = maximumGeneratedObjects;
        target.treePercent = treePercent;
        target.minHeight = minHeight;
        target.maxHeight = maxHeight;
        target.maxSlope = maxSlope;
        target.scaleRange = scaleRange;
        target.minDistance = minDistance;
        target.maxSpawnAttemptCounts = maxSpawnAttemptCounts;
        target.obstacleLayer = obstacleLayer;
        target.setLayerRecursively = setLayerRecursively;
        target.addMissingCollisionProxy = addMissingCollisionProxy;
        target.proxyRadius = proxyRadius;
        target.proxyHeight = proxyHeight;
    }

    public sealed class ForestPublication
    {
        internal GameObject RetainedRoot;
        internal readonly List<Transform> RetainedChildren = new List<Transform>();
        internal readonly List<Transform> PublishedChildren = new List<Transform>();
        internal List<Vector2> RetainedPositions;
        internal bool Active;
    }

    /// <summary>Retains the current forest so the staged forest can be rolled back.</summary>
    public ForestPublication BeginAdoptSpawnedTreesFrom(ForestSpawner stagedForest)
    {
        if (stagedForest == null) throw new ArgumentNullException(nameof(stagedForest));

        var publication = new ForestPublication
        {
            RetainedRoot = new GameObject("Retained Replay Forest"),
            RetainedPositions = new List<Vector2>(spawnedTreePositions),
            Active = true
        };
        for (int i = 0; i < transform.childCount; i++)
        {
            publication.RetainedChildren.Add(transform.GetChild(i));
        }
        publication.RetainedRoot.hideFlags = HideFlags.HideAndDontSave;
        publication.RetainedRoot.transform.SetParent(transform, false);
        publication.RetainedRoot.SetActive(false);

        try
        {
            foreach (Transform child in publication.RetainedChildren)
            {
                child.SetParent(publication.RetainedRoot.transform, true);
            }
            while (stagedForest.transform.childCount > 0)
            {
                Transform child = stagedForest.transform.GetChild(0);
                publication.PublishedChildren.Add(child);
                child.SetParent(transform, true);
            }
            spawnedTreePositions = new List<Vector2>(stagedForest.spawnedTreePositions);
            stagedForest.spawnedTreePositions.Clear();
            return publication;
        }
        catch
        {
            RollbackAdoptSpawnedTrees(publication);
            throw;
        }
    }

    public void CommitAdoptSpawnedTrees(ForestPublication publication)
    {
        if (publication == null || !publication.Active) return;
        publication.Active = false;
        DestroyForestObject(publication.RetainedRoot);
    }

    public void RollbackAdoptSpawnedTrees(ForestPublication publication)
    {
        if (publication == null || !publication.Active) return;
        publication.Active = false;
        foreach (Transform child in publication.PublishedChildren)
        {
            if (child != null) DestroyForestObject(child.gameObject);
        }
        foreach (Transform child in publication.RetainedChildren)
        {
            if (child != null) child.SetParent(transform, true);
        }
        spawnedTreePositions = new List<Vector2>(publication.RetainedPositions);
        DestroyForestObject(publication.RetainedRoot);
    }

    private static void DestroyForestObject(GameObject target)
    {
        if (target == null) return;
        target.SetActive(false);
        if (Application.isPlaying) Destroy(target);
        else DestroyImmediate(target);
    }

    public void AdoptSpawnedTreesFrom(ForestSpawner stagedForest)
    {
        ForestPublication publication = BeginAdoptSpawnedTreesFrom(stagedForest);
        CommitAdoptSpawnedTrees(publication);
    }

    public bool IsFarEnoughFromTrees(Vector3 worldPosition, float distance)
    {
        Vector2 candidateXZ = new Vector2(
            worldPosition.x,
            worldPosition.z
        );

        float distanceSqrLimit = distance * distance;

        foreach (Vector2 treePos in spawnedTreePositions)
        {
            float distanceSqr = (candidateXZ - treePos).sqrMagnitude;

            if (distanceSqr < distanceSqrLimit)
                return false;
        }

        return true;
    }

    public void ClearSpawnedTrees()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child == null)
            {
                continue;
            }

            if (Application.isPlaying)
            {
                // Disable immediately because Destroy is deferred until frame end.
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
            else
            {
                DestroyImmediate(child.gameObject);
            }
        }

        spawnedTreePositions.Clear();
    }

    public void RespawnTrees()
    {
        TryRespawnTrees();
    }

    public bool TryRespawnTrees()
    {
        if (!ValidateSpawnRequirements(out _, out _, out _))
        {
            return false;
        }

        ClearSpawnedTrees();
        return TrySpawnTrees();
    }

    // Kept as a void wrapper so existing UnityEvent bindings remain valid.
    public void SpawnTrees()
    {
        TrySpawnTrees();
    }

    public bool TrySpawnTrees()
    {
        if (!ValidateSpawnRequirements(
            out TerrainData terrainData,
            out int configuredTargetTotalCount,
            out long requestedTotalCount))
        {
            return false;
        }

        Vector3 terrainPos = terrain.transform.position;
        if (requestedTotalCount > configuredTargetTotalCount)
        {
            Debug.LogWarning(
                $"[ForestSpawner] Requested {requestedTotalCount:N0} objects, but synchronous generation is limited " +
                $"to {configuredTargetTotalCount:N0}. Reduce terrain size/density or raise Maximum Generated Objects up to " +
                $"the hard limit ({MaximumSpawnedObjectCount:N0}).",
                this
            );
        }

        // The limit spans all additive calls since the last clear.
        int remainingCapacity = Mathf.Max(0, maximumGeneratedObjects - spawnedTreePositions.Count);
        int targetTotalCount = Math.Min(configuredTargetTotalCount, remainingCapacity);
        if (targetTotalCount == 0)
        {
            if (configuredTargetTotalCount > 0 && remainingCapacity == 0)
            {
                Debug.Log("[ForestSpawner] Maximum Generated Objects reached; no additional objects were generated.", this);
            }
            return true;
        }

        CalculateTargetCounts(
            targetTotalCount,
            out int targetTreeCount,
            out int targetRockCount
        );

        int spawnedCount = 0;
        int spawnedTreeCount = 0;
        int spawnedRockCount = 0;

        long attempts = 0L;
        long requestedAttempts = SaturatingMultiply(targetTotalCount, maxSpawnAttemptCounts);
        long maxAttempts = Math.Min(MaximumTotalSpawnAttempts, Math.Max(1L, requestedAttempts));
        if (requestedAttempts > maxAttempts)
        {
            Debug.LogWarning(
                $"[ForestSpawner] Requested {requestedAttempts:N0} spawn attempts; capped at " +
                $"{MaximumTotalSpawnAttempts:N0} to protect synchronous generation.",
                this
            );
        }

        Dictionary<Vector2Int, List<Vector2>> positionGrid = BuildPositionGrid(minDistance);

        while (spawnedCount < targetTotalCount && attempts < maxAttempts)
        {
            attempts++;

            float randomX = Random.Range(0, terrainData.size.x);
            float randomZ = Random.Range(0, terrainData.size.z);

            float worldX = randomX + terrainPos.x;
            float worldZ = randomZ + terrainPos.z;
            float y = terrain.SampleHeight(
                new Vector3(worldX, 0f, worldZ)
            );

            Vector3 worldPos = new Vector3(
                worldX,
                y + terrainPos.y -0.5f,
                worldZ
            );

            if (worldPos.y < minHeight || worldPos.y > maxHeight)
                continue;

            Vector3 normal = terrainData.GetInterpolatedNormal(
                randomX / terrainData.size.x,
                randomZ / terrainData.size.z
            );

            float slope = Vector3.Angle(normal, Vector3.up);

            if (slope > maxSlope) continue;

            // The grid avoids scanning every prior spawn on each attempt.
            Vector2 candidateXZ = new Vector2(worldX, worldZ);
            if (!IsFarEnoughFromOtherTrees(candidateXZ, positionGrid)) continue;

            bool spawnTree;
            GameObject prefab = ChoosePrefab(
                spawnedTreeCount,
                targetTreeCount,
                spawnedRockCount,
                targetRockCount,
                out spawnTree
            );

            if (prefab == null)
            {
                continue;
            }

            GameObject spawnedObject;
            try
            {
                spawnedObject = Instantiate(
                    prefab,
                    worldPos,
                    Quaternion.identity,
                    transform
                );
            }
            catch (Exception exception)
            {
                Debug.LogError($"[ForestSpawner] Forest generation failed: {exception.Message}", this);
                return false;
            }

            spawnedObject.name = spawnTree ? "Spawned Tree" : "Spawned Rock";

            spawnedObject.transform.Rotate(0, Random.Range(0, 360), 0);

            float scale = Random.Range(scaleRange.x, scaleRange.y);
            spawnedObject.transform.localScale *= scale;

            spawnedTreePositions.Add(candidateXZ);
            AddToPositionGrid(candidateXZ, positionGrid, minDistance);
            spawnedCount++;

            if (spawnTree)
            {
                spawnedTreeCount++;
            }
            else
            {
                spawnedRockCount++;
            }

            PrepareObstacleForDroneSensing(spawnedObject);
        }
        if (spawnedCount * 2 < targetTotalCount)
        {
            Debug.LogWarning(
                $"[ForestSpawner] 予定数 {targetTotalCount} 個に対して {spawnedCount} 個しか生成できませんでした。" +
                $" minDistance が大きすぎる、または高さ・傾斜条件が厳しすぎる可能性があります。"
            );
        }
        Debug.Log(
            $"[ForestSpawner] Total:{spawnedCount}, Trees:{spawnedTreeCount}, Rocks:{spawnedRockCount}, " +
            $"TerrainArea:{terrainData.size.x * terrainData.size.z}m²"
        );
        if (targetTotalCount > 0 && spawnedCount == 0)
        {
            Debug.LogError("[ForestSpawner] No configured forest objects could be generated.", this);
            return false;
        }

        return true;
    }

    private bool ValidateSpawnRequirements(
        out TerrainData terrainData,
        out int targetTotalCount,
        out long requestedTotalCount
    )
    {
        terrainData = terrain != null ? terrain.terrainData : null;
        targetTotalCount = 0;
        requestedTotalCount = 0L;
        if (terrainData == null)
        {
            Debug.LogError("[ForestSpawner] A Terrain with TerrainData is required.", this);
            return false;
        }

        Vector3 size = terrainData.size;
        if (size.x <= 0f || size.z <= 0f
            || float.IsNaN(size.x) || float.IsNaN(size.z)
            || float.IsInfinity(size.x) || float.IsInfinity(size.z))
        {
            Debug.LogError("[ForestSpawner] TerrainData must have a finite, positive size.", this);
            return false;
        }

        ValidateSafetySettings();
        targetTotalCount = CalculateTargetTotalCount(terrainData, out requestedTotalCount);
        CalculateTargetCounts(targetTotalCount, out int targetTreeCount, out int targetRockCount);
        if (targetTreeCount > 0 && treePrefab == null)
        {
            Debug.LogError("[ForestSpawner] A tree prefab is required by the configured density and tree percentage.", this);
            return false;
        }
        if (targetRockCount > 0 && rockPrefab == null)
        {
            Debug.LogError("[ForestSpawner] A rock prefab is required by the configured density and tree percentage.", this);
            return false;
        }

        return true;
    }

    public bool ClampDensityForArea(float width, float depth, bool logWarning)
    {
        ValidateSafetySettings();

        double area = Math.Max(0d, (double)width) * Math.Max(0d, (double)depth);
        if (double.IsNaN(area) || double.IsInfinity(area) || area <= 0d)
        {
            return false;
        }

        float maximumDensity = (float)(maximumGeneratedObjects * 100d / area);
        if (objectsPer100SquareMeters <= maximumDensity)
        {
            return false;
        }

        float requestedDensity = objectsPer100SquareMeters;
        objectsPer100SquareMeters = maximumDensity;
        if (logWarning)
        {
            Debug.LogWarning(
                $"[ForestSpawner] Density {requestedDensity:0.###}/100m² was clamped to " +
                $"{maximumDensity:0.###}/100m² for a {width:0.###}m x {depth:0.###}m terrain " +
                $"({maximumGeneratedObjects:N0} object synchronous limit).",
                this
            );
        }

        return true;
    }

    private int CalculateTargetTotalCount(TerrainData terrainData, out long requestedTotalCount)
    {
        double width = Math.Max(0d, terrainData.size.x);
        double depth = Math.Max(0d, terrainData.size.z);
        double density = Math.Max(0d, objectsPer100SquareMeters);
        double rawCount = width * depth / 100d * density;

        if (double.IsNaN(rawCount) || rawCount <= 0d)
        {
            requestedTotalCount = 0L;
        }
        else if (double.IsInfinity(rawCount) || rawCount >= long.MaxValue)
        {
            requestedTotalCount = long.MaxValue;
        }
        else
        {
            requestedTotalCount = (long)Math.Round(rawCount, MidpointRounding.ToEven);
        }

        return (int)Math.Min(requestedTotalCount, maximumGeneratedObjects);
    }

    private void CalculateTargetCounts(
        int targetTotalCount,
        out int targetTreeCount,
        out int targetRockCount
    )
    {
        float clampedTreePercent = Mathf.Clamp(treePercent, 0f, 100f);

        targetTreeCount = Mathf.RoundToInt(targetTotalCount * clampedTreePercent / 100f);
        targetRockCount = targetTotalCount - targetTreeCount;
    }

    private GameObject ChoosePrefab(
    int spawnedTreeCount,
    int targetTreeCount,
    int spawnedRockCount,
    int targetRockCount,
    out bool spawnTree
)
    {
        if (targetTreeCount <= 0)
        {
            spawnTree = false;
            return rockPrefab;
        }

        if (targetRockCount <= 0)
        {
            spawnTree = true;
            return treePrefab;
        }

        if (spawnedTreeCount >= targetTreeCount)
        {
            spawnTree = false;
            return rockPrefab;
        }

        if (spawnedRockCount >= targetRockCount)
        {
            spawnTree = true;
            return treePrefab;
        }

        float treeRate = Mathf.Clamp01(treePercent / 100f);
        spawnTree = Random.value < treeRate;

        return spawnTree ? treePrefab : rockPrefab;
    }

    private Dictionary<Vector2Int, List<Vector2>> BuildPositionGrid(float cellSize)
    {
        Dictionary<Vector2Int, List<Vector2>> grid = new Dictionary<Vector2Int, List<Vector2>>();
        if (cellSize <= 0f)
        {
            return grid;
        }

        foreach (Vector2 position in spawnedTreePositions)
        {
            AddToPositionGrid(position, grid, cellSize);
        }

        return grid;
    }

    private static void AddToPositionGrid(
        Vector2 position,
        Dictionary<Vector2Int, List<Vector2>> grid,
        float cellSize
    )
    {
        if (cellSize <= 0f)
        {
            return;
        }

        Vector2Int cell = GetGridCell(position, cellSize);
        if (!grid.TryGetValue(cell, out List<Vector2> positions))
        {
            positions = new List<Vector2>();
            grid.Add(cell, positions);
        }
        positions.Add(position);
    }

    private bool IsFarEnoughFromOtherTrees(
        Vector2 candidatePosition,
        Dictionary<Vector2Int, List<Vector2>> grid
    )
    {
        if (minDistance <= 0f)
        {
            return true;
        }

        float minDistanceSqr = minDistance * minDistance;
        Vector2Int candidateCell = GetGridCell(candidatePosition, minDistance);
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                Vector2Int cell = new Vector2Int(candidateCell.x + x, candidateCell.y + y);
                if (!grid.TryGetValue(cell, out List<Vector2> positions))
                {
                    continue;
                }

                foreach (Vector2 existingPosition in positions)
                {
                    if ((candidatePosition - existingPosition).sqrMagnitude < minDistanceSqr)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static Vector2Int GetGridCell(Vector2 position, float cellSize)
    {
        return new Vector2Int(
            Mathf.FloorToInt(position.x / cellSize),
            Mathf.FloorToInt(position.y / cellSize)
        );
    }

    private static long SaturatingMultiply(int left, int right)
    {
        try
        {
            return checked((long)left * right);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private void ValidateSafetySettings()
    {
        maximumGeneratedObjects = Mathf.Clamp(
            maximumGeneratedObjects,
            1,
            MaximumSpawnedObjectCount
        );
        maxSpawnAttemptCounts = Mathf.Clamp(
            maxSpawnAttemptCounts,
            1,
            MaximumAttemptsPerObject
        );

        if (float.IsNaN(objectsPer100SquareMeters) || float.IsInfinity(objectsPer100SquareMeters))
        {
            objectsPer100SquareMeters = 0f;
        }
        else
        {
            objectsPer100SquareMeters = Mathf.Max(0f, objectsPer100SquareMeters);
        }

        if (float.IsNaN(minDistance) || float.IsInfinity(minDistance))
        {
            minDistance = 0f;
        }
        else
        {
            minDistance = Mathf.Max(0f, minDistance);
        }
    }

    private void OnValidate()
    {
        ValidateSafetySettings();
    }

    void PrepareObstacleForDroneSensing(GameObject obstacle)
    {
        if (obstacle == null)
        {
            return;
        }

        if (setLayerRecursively)
        {
            SetLayerRecursively(obstacle, obstacleLayer);
        }
        else
        {
            obstacle.layer = obstacleLayer;
        }

        if (!addMissingCollisionProxy || obstacle.GetComponentInChildren<Collider>() != null)
        {
            return;
        }

        CapsuleCollider proxy = obstacle.AddComponent<CapsuleCollider>();
        proxy.isTrigger = true;
        proxy.radius = Mathf.Max(0.01f, proxyRadius);
        proxy.height = Mathf.Max(proxy.radius * 2f, proxyHeight);
        proxy.center = new Vector3(0f, proxy.height * 0.5f, 0f);
    }

    void SetLayerRecursively(GameObject root, int layer)
    {
        root.layer = layer;
        foreach (Transform child in root.transform)
            SetLayerRecursively(child.gameObject, layer);
    }
}
