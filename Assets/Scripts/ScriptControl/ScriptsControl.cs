using UnityEngine;

public class ScriptsControl : MonoBehaviour
{
    public TerrainGenerator terrainGenerator;
    public ForestSpawner forestSpawner;
    public DroneSwarmDemoBootstrap droneSwarmDemoBootstrap;
    public Explorer explorer;

    private void Start()
    {
        bool batchAutoStartEnabled = false;
        foreach (var batchRunner in FindObjectsByType<DroneMissionBatchRunner>(FindObjectsSortMode.None))
        {
            if (batchRunner != null && batchRunner.AutoStartOnPlay)
            {
                batchAutoStartEnabled = true;
                break;
            }
        }

        terrainGenerator.GenerateTerrain();
        if (!batchAutoStartEnabled)
        {
            forestSpawner.SpawnTrees();
        }

        explorer.ExplorerSpawner();

        if (batchAutoStartEnabled)
        {
            return;
        }

        droneSwarmDemoBootstrap.ResetDemo();
    }
}
