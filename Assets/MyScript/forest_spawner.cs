using UnityEngine;

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

    void Start()
    {
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
                y + terrainPos.y -0.3f,
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
        }
    }
}