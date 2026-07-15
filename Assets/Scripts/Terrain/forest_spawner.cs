using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class ForestSpawner : MonoBehaviour
{
    [Header("Terrain")]
    public Terrain terrain;

    [Header("Prefab Settings")]
    public GameObject treePrefab;
    public GameObject rockPrefab;

    [Header("Density Settings")]
    [Tooltip("The number of trees & rocks per 100m²")]
    [Min(0f)]
    public float objectsPer100SquareMeters = 2.5f;

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

    public IReadOnlyList<Vector2> SpawnTreePositions //外部参照用(forest_spawner.cs外からの中身の変更は不可)
    {
        get { return spawnedTreePositions; }
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

    /*
    ScriptsControl.csにて制御
    IEnumerator Start()
    {
        yield return null;
        SpawnTrees();
    }
    */

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
        ClearSpawnedTrees();
        SpawnTrees();
    }

    public void SpawnTrees()
    {
        TerrainData terrainData = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;

        int targetTotalCount = CalculateTargetTotalCount(terrainData);
        CalculateTargetCounts(
            targetTotalCount,
            out int targetTreeCount,
            out int targetRockCount
        );

        int spawnedCount = 0;
        int spawnedTreeCount = 0;
        int spawnedRockCount = 0;

        int attempts = 0;
        int maxAttempts = Mathf.Max(1, targetTotalCount * maxSpawnAttemptCounts);

        while (spawnedCount < targetTotalCount && attempts < maxAttempts)
        {
            attempts++;

            // ランダムXZ座標
            float randomX = Random.Range(0, terrainData.size.x);
            float randomZ = Random.Range(0, terrainData.size.z);

            // yの取得
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

            // 高さ制限
            if (worldPos.y < minHeight || worldPos.y > maxHeight)
                continue;

            // 傾斜取得
            Vector3 normal = terrainData.GetInterpolatedNormal(
                randomX / terrainData.size.x,
                randomZ / terrainData.size.z
            );

            float slope = Vector3.Angle(normal, Vector3.up);

            // 急斜面回避
            if (slope > maxSlope) continue;

            //木同士の最低距離制限
            Vector2 candidateXZ = new Vector2(worldX, worldZ);
            if (!IsFarEnoughFromOtherTrees(candidateXZ, spawnedTreePositions)) continue;

            // Prefab選択
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

            GameObject spawnedObject = Instantiate(
                prefab,
                worldPos,
                Quaternion.identity,
                transform
            );

            spawnedObject.name = spawnTree ? "Spawned Tree" : "Spawned Rock";

            // ランダム回転
            spawnedObject.transform.Rotate(0, Random.Range(0, 360), 0);

            // ランダムスケール
            float scale = Random.Range(scaleRange.x, scaleRange.y);
            spawnedObject.transform.localScale *= scale;

            spawnedTreePositions.Add(candidateXZ);
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
    }

    private int CalculateTargetTotalCount(TerrainData terrainData)
    {
        float terrainArea = terrainData.size.x * terrainData.size.z;
        float areaUnitCount = terrainArea / 100f;

        return Mathf.RoundToInt(areaUnitCount * Mathf.Max(0f, objectsPer100SquareMeters));
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
        // 木が必要ない場合は岩だけ
        if (targetTreeCount <= 0)
        {
            spawnTree = false;
            return rockPrefab;
        }

        // 岩が必要ない場合は木だけ
        if (targetRockCount <= 0)
        {
            spawnTree = true;
            return treePrefab;
        }

        // 木が目標数に達していたら岩
        if (spawnedTreeCount >= targetTreeCount)
        {
            spawnTree = false;
            return rockPrefab;
        }

        // 岩が目標数に達していたら木
        if (spawnedRockCount >= targetRockCount)
        {
            spawnTree = true;
            return treePrefab;
        }

        // まだ両方生成可能なら割合に従って選ぶ
        float treeRate = Mathf.Clamp01(treePercent / 100f);
        spawnTree = Random.value < treeRate;

        return spawnTree ? treePrefab : rockPrefab;
    }

    bool IsFarEnoughFromOtherTrees(Vector2 candidatePosition, List<Vector2> spawnedTreePositions)
    {
        if (minDistance <= 0f) return true;
        float minDistanceSqr = minDistance * minDistance;
        foreach (Vector2 existingPosition in spawnedTreePositions)
        {
            float distanceSqr = (candidatePosition - existingPosition).sqrMagnitude;
            if (distanceSqr < minDistanceSqr) return false;
        }
        return true;
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
