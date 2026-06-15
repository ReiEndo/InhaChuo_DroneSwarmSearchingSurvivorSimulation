using UnityEngine;

public class TerrainGenerator : MonoBehaviour
{
    public Terrain terrain;

    public int widthx = 128;
    public int widthz = 128;

    public float scale = 250f;
    public float terrainHeight = 50f;

    void Start()
    {
        GenerateTerrain();
    }

    void GenerateTerrain()
    {
        TerrainData data = terrain.terrainData;

        data.heightmapResolution = widthx + 1;
        //widthマス分用意
        data.size = new Vector3(widthx, terrainHeight, widthz);

        float[,] heights = new float[widthx + 1, widthz + 1];

        float offsetX = UnityEngine.Random.Range(0f, 10000f);
        float offsetZ = UnityEngine.Random.Range(0f, 10000f);

        int edge_border = 40;

        for (int x = 0; x < widthx + 1; x++)
        {
            for (int z = 0; z < widthz + 1; z++)
            {
                float nx = x / scale + offsetX;
                float nz = z / scale + offsetZ;

                /*単純な起伏heights[x, z] = Mathf.PerlinNoise(nx, nz);*/
                float height = 0.6f * Mathf.PerlinNoise(nx, nz) + 0.3f * Mathf.PerlinNoise(nx * 2f, nz * 2f) + 0.1f * Mathf.PerlinNoise(nx * 4f, nz * 4f);
                //Terrain周囲の高さを下げる
                float edgeFactor = 1f;
                edgeFactor *= Mathf.Clamp01((float)x / edge_border);
                edgeFactor *= Mathf.Clamp01((float)z / edge_border);
                edgeFactor *= Mathf.Clamp01((float)(widthx - x) / edge_border);
                edgeFactor *= Mathf.Clamp01((float)(widthz - z) / edge_border);
                height *= edgeFactor;

                heights[x, z] = height;
            }
        }


        data.SetHeights(0, 0, heights);
    }
}