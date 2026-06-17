using UnityEngine;

public class ScriptsControl : MonoBehaviour
{
    public TerrainGenerator terrainGenerator;
    public ForestSpawner forestSpawner;
    public DroneSwarmDemoBootstrap droneSwarmDemoBootstrap;
    public Explorer explorer;

    private void Start()
    {
        terrainGenerator.GenerateTerrain();
        forestSpawner.SpawnTrees();
        explorer.ExplorerSpawner();
        droneSwarmDemoBootstrap.ResetDemo();
    }
}
