using UnityEngine;

public class ScriptsControl : MonoBehaviour
{
    public TerrainGenerator terrainGenerator;
    public ForestSpawner forestSpawner;
    public DroneSwarmDemoBootstrap droneSwarmDemoBootstrap;
    public Explorer explorer;
    public UiScriptsControl uiScriptsControl;

    private void Start()
    {
        terrainGenerator.GenerateTerrain();
        forestSpawner.SpawnTrees();
        explorer.ExplorerSpawner();
        droneSwarmDemoBootstrap.ResetDemo();
        uiScriptsControl.UI_Start();
    }
}
