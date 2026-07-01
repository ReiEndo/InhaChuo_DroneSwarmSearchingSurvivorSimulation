using UnityEngine;

public class ScriptsControl : MonoBehaviour
{
    [Header("Main Scripts")]
    public TerrainGenerator terrainGenerator;
    public ForestSpawner forestSpawner;
    public DroneSwarmDemoBootstrap droneSwarmDemoBootstrap;
    public Explorer explorer;
    public UiScriptsControl uiScriptsControl;

    [Header("Start Setting")]
    [SerializeField] public bool autoStart = false;

    private bool started;

    private void Start()
    {
        if (autoStart)
        {
            StartSimulation();
        }
    }
    public void StartSimulation()
    {
        if (started)
        {
            return;
        }
        started = true;

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

        if (droneSwarmDemoBootstrap != null &&
        terrainGenerator != null &&
        terrainGenerator.terrain != null)
        {
            droneSwarmDemoBootstrap.ConfigureGridFromTerrain(
                terrainGenerator.terrain,
                droneSwarmDemoBootstrap.CellSize
            );
        }

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
