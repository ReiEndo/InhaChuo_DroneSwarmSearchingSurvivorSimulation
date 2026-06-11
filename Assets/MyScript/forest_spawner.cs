using UnityEngine;
using System.Collections;

public class ForestSpawner : MonoBehaviour
{
    [Header("Terrain")]
    public Terrain terrain;

    [Header("Tree Settings")]
    public GameObject[] treePrefabs;
    public int treeCount = 1000;

    [Header("Spawn Area")]
    public float minHeight = 0f;
    public float maxHeight = 100f;

    [Header("Slope Limit")]
    [Range(0, 60)]
    public float maxSlope = 30f;

    [Header("Random Scale")]
    public Vector2 scaleRange = new Vector2(0.8f, 1.2f);

    [Header("Drone Sensing")]
    [Tooltip("Layer used by DroneDemoGridWorld as blocked/obstacle. ProjectSettings currently defines Obstacle as layer 7.")]
    public int obstacleLayer = 7;
    [Tooltip("Apply the obstacle layer to every spawned tree child so child colliders are sensed correctly.")]
    public bool setLayerRecursively = true;
    [Tooltip("Add one simple trigger collider to tree prefabs that do not already have colliders. Keeps sensing cheap compared with mesh colliders.")]
    public bool addMissingCollisionProxy = true;
    public float proxyRadius = 0.35f;
    public float proxyHeight = 2.5f;

    IEnumerator Start()
    {
        yield return null;
        SpawnTrees();
    }

    void SpawnTrees()
    {
        TerrainData terrainData = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;

        for (int i = 0; i < treeCount; i++)
        {
            // ランダムXZ座標
            float randomX = Random.Range(0, terrainData.size.x);
            float randomZ = Random.Range(0, terrainData.size.z);

            // yの取得
            float worldX = randomX + terrainPos.x;
            float worldZ = randomZ + terrainPos.z;
            float y = terrain.SampleHeight(
                new Vector3(worldX, 0, worldZ)
            );

            Vector3 worldPos = new Vector3(
                randomX + terrainPos.x,
                y + terrainPos.y -0.5f,
                randomZ + terrainPos.z
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
            if (slope > maxSlope)
                continue;

            // Prefab選択
            GameObject prefab =
                treePrefabs[Random.Range(0, treePrefabs.Length)];

            // 生成
            GameObject tree = Instantiate(
                prefab,
                worldPos,
                Quaternion.identity,
                transform
            );

            // ランダム回転
            tree.transform.Rotate(0, Random.Range(0, 360), 0);

            // ランダムスケール
            float scale = Random.Range(scaleRange.x, scaleRange.y);
            tree.transform.localScale *= scale;

            PrepareTreeForDroneSensing(tree);
        }
    }

    void PrepareTreeForDroneSensing(GameObject tree)
    {
        if (tree == null)
            return;

        if (setLayerRecursively)
            SetLayerRecursively(tree, obstacleLayer);
        else
            tree.layer = obstacleLayer;

        if (!addMissingCollisionProxy || tree.GetComponentInChildren<Collider>() != null)
            return;

        CapsuleCollider proxy = tree.AddComponent<CapsuleCollider>();
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
