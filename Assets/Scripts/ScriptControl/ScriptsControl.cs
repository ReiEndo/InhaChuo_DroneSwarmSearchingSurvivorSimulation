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
        bool batchAutoStartEnabled = false;
        foreach (var batchRunner in FindObjectsByType<DroneMissionBatchRunner>(FindObjectsSortMode.None))
        {
            if (batchRunner != null && batchRunner.AutoStartOnPlay)
            {
                batchAutoStartEnabled = true;
                break;
            }
        }

        if (!batchAutoStartEnabled)
        {
            foreach (var timedRunner in FindObjectsByType<DroneMissionTimedBatchRunner>(FindObjectsSortMode.None))
            {
                if (timedRunner != null && timedRunner.AutoStartOnPlay)
                {
                    batchAutoStartEnabled = true;
                    break;
                }
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
        uiScriptsControl.UI_Start();
    }
}
