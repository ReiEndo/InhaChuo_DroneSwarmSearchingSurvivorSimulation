using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class StartSettingsController : MonoBehaviour
{
    [Header("Panel")]
    [SerializeField] private GameObject settingsPanel;

    [Header("Targets")]
    [SerializeField] private TerrainGenerator terrainGenerator;
    [SerializeField] private ForestSpawner forestSpawner;
    [SerializeField] private DroneSwarmDemoBootstrap droneSwarmDemoBootstrap;
    [SerializeField] private Explorer explorer;
    [SerializeField] private GameFlowController gameFlowController;

    [Header("TerrainGenerator")]
    [SerializeField] private TMP_InputField terrainWidthXInput;
    [SerializeField] private TMP_InputField terrainWidthZInput;
    [SerializeField] private TMP_InputField terrainScaleInput;
    [SerializeField] private TMP_InputField terrainHeightInput;

    [Header("ForestSpawner")]
    [SerializeField] private TMP_InputField treeCountInput;
    [SerializeField] private TMP_InputField minHeightInput;
    [SerializeField] private TMP_InputField maxHeightInput;
    [SerializeField] private TMP_InputField maxSlopeInput;
    [SerializeField] private TMP_InputField treeScaleMinInput;
    [SerializeField] private TMP_InputField treeScaleMaxInput;
    [SerializeField] private TMP_InputField minTreeDistanceInput;
    [SerializeField] private TMP_InputField maxSpawnAttemptInput;

    [Header("Drone Grid")]
    [SerializeField] private Toggle autoDroneGridFromTerrainToggle;
    [SerializeField] private TMP_InputField droneGridWidthInput;
    [SerializeField] private TMP_InputField droneGridDepthInput;
    [SerializeField] private TMP_InputField droneCellSizeInput;

    [Header("Drone Swarm")]
    [SerializeField] private TMP_InputField droneCountInput;
    [SerializeField] private TMP_InputField droneSensorRadiusInput;
    [SerializeField] private TMP_InputField droneCommunicationRadiusInput;
    [SerializeField] private TMP_InputField droneSpeedInput;

    [Header("Time Limit")]
    [SerializeField] private Toggle useSearchTimeLimitToggle;
    [SerializeField] private TMP_InputField searchTimeLimitInput;
    [SerializeField] private Toggle useDroneReturnTimeLimitToggle;
    [SerializeField] private TMP_InputField droneReturnTimeLimitInput;

    [Header("Explorer")]
    [SerializeField] private TMP_InputField explorerScanRadiusInput;
    [SerializeField] private TMP_InputField explorerMinTargetDistanceInput;
    [SerializeField] private TMP_InputField explorerMoveSpeedInput;
    [SerializeField] private TMP_InputField explorerStaminaInput;
    [SerializeField] private TMP_InputField explorerStaminaDecreaseInput;
    [SerializeField] private TMP_InputField explorerStaminaRecoveryInput;
    [SerializeField] private TMP_InputField explorerRestartThresholdInput;
    [SerializeField] private TMP_InputField explorerMaxWalkableSlopeInput;
    [SerializeField] private TMP_InputField explorerRecoveryDecayInput;

    public void StartSettings_Start()
    {
        if (settingsPanel != null)
        {
            settingsPanel.SetActive(false);
        }

        LoadCurrentValuesToUI();
    }

    public void OpenSettings()
    {
        LoadCurrentValuesToUI();

        if (settingsPanel != null)
        {
            settingsPanel.SetActive(true);
        }
    }

    public void CloseSettings()
    {
        ApplySettings();

        if (settingsPanel != null)
        {
            settingsPanel.SetActive(false);
        }
    }

    public void ApplySettings()
    {
        ApplyTerrainSettings();
        ApplyForestSettings();
        ApplyDroneSettings();
        ApplyExplorerSettings();
        ApplyGameFlowSettings();
    }

    private void LoadCurrentValuesToUI()
    {

        if (terrainGenerator != null)
        {
            SetText(terrainWidthXInput, terrainGenerator.widthx);
            SetText(terrainWidthZInput, terrainGenerator.widthz);
            SetText(terrainScaleInput, terrainGenerator.scale);
            SetText(terrainHeightInput, terrainGenerator.terrainHeight);
        }

        if (forestSpawner != null)
        {
            SetText(treeCountInput, forestSpawner.treeCount);
            SetText(minHeightInput, forestSpawner.minHeight);
            SetText(maxHeightInput, forestSpawner.maxHeight);
            SetText(maxSlopeInput, forestSpawner.maxSlope);
            SetText(treeScaleMinInput, forestSpawner.scaleRange.x);
            SetText(treeScaleMaxInput, forestSpawner.scaleRange.y);
            SetText(minTreeDistanceInput, forestSpawner.minDistance);
            SetText(maxSpawnAttemptInput, forestSpawner.maxSpawnAttemptCounts);
        }

        if (droneSwarmDemoBootstrap != null)
        {
            SetText(droneCountInput, droneSwarmDemoBootstrap.DroneCount);
            SetText(droneSensorRadiusInput, droneSwarmDemoBootstrap.SensorRadius);
            SetText(droneCommunicationRadiusInput, droneSwarmDemoBootstrap.CommunicationRadius);
            SetText(droneSpeedInput, droneSwarmDemoBootstrap.DroneSpeed);
            SetText(droneCellSizeInput, droneSwarmDemoBootstrap.CellSize);
        }

        if (gameFlowController != null)
        {
            SetToggle(useSearchTimeLimitToggle, gameFlowController.UseSearchTimeLimit);
            SetText(searchTimeLimitInput, gameFlowController.SearchTimeLimitSeconds);

            SetToggle(useDroneReturnTimeLimitToggle, gameFlowController.UseDroneReturnTimeLimit);
            SetText(droneReturnTimeLimitInput, gameFlowController.DroneReturnTimeLimitSeconds);
        }

        if (explorer != null)
        {
            SetText(explorerScanRadiusInput, explorer.scanRadius);
            SetText(explorerMinTargetDistanceInput, explorer.minTargetDistance);
            SetText(explorerMoveSpeedInput, explorer.moveSpeed);
            SetText(explorerStaminaInput, explorer.stamina);
            SetText(explorerStaminaDecreaseInput, explorer.staminaDecreasePerSecond);
            SetText(explorerStaminaRecoveryInput, explorer.staminaRecoveryPerSecond);
            SetText(explorerRestartThresholdInput, explorer.restartThreshold);
            SetText(explorerMaxWalkableSlopeInput, explorer.maxWalkableSlope);
            SetText(explorerRecoveryDecayInput, explorer.recoveryDecay);
        }
    }

    private void ApplyTerrainSettings()
    {
        if (terrainGenerator == null)
        {
            return;
        }

        terrainGenerator.widthx = GetInt(terrainWidthXInput, terrainGenerator.widthx, 4, 2048);
        terrainGenerator.widthz = GetInt(terrainWidthZInput, terrainGenerator.widthz, 4, 2048);
        terrainGenerator.scale = GetFloat(terrainScaleInput, terrainGenerator.scale, 1f, 5000f);
        terrainGenerator.terrainHeight = GetFloat(terrainHeightInput, terrainGenerator.terrainHeight, 1f, 1000f);
    }

    private void ApplyForestSettings()
    {
        if (forestSpawner == null)
        {
            return;
        }

        forestSpawner.treeCount = GetInt(treeCountInput, forestSpawner.treeCount, 0, 100000);
        forestSpawner.minHeight = GetFloat(minHeightInput, forestSpawner.minHeight, -1000f, 10000f);
        forestSpawner.maxHeight = GetFloat(maxHeightInput, forestSpawner.maxHeight, -1000f, 10000f);
        forestSpawner.maxSlope = GetFloat(maxSlopeInput, forestSpawner.maxSlope, 0f, 60f);

        float minScale = GetFloat(treeScaleMinInput, forestSpawner.scaleRange.x, 0.01f, 100f);
        float maxScale = GetFloat(treeScaleMaxInput, forestSpawner.scaleRange.y, 0.01f, 100f);

        if (maxScale < minScale)
        {
            maxScale = minScale;
        }

        forestSpawner.scaleRange = new Vector2(minScale, maxScale);

        forestSpawner.minDistance = GetFloat(minTreeDistanceInput, forestSpawner.minDistance, 0f, 1000f);
        forestSpawner.maxSpawnAttemptCounts = GetInt(maxSpawnAttemptInput, forestSpawner.maxSpawnAttemptCounts, 1, 10000);
    }

    private void ApplyDroneSettings()
    {
        if (droneSwarmDemoBootstrap == null)
        {
            return;
        }

        int droneCount = GetInt(
            droneCountInput,
            droneSwarmDemoBootstrap.DroneCount,
            1,
            12
        );

        float communicationRadius = GetFloat(
            droneCommunicationRadiusInput,
            droneSwarmDemoBootstrap.CommunicationRadius,
            0f,
            1000f
        );

        float droneSpeed = GetFloat(
            droneSpeedInput,
            droneSwarmDemoBootstrap.DroneSpeed,
            0f,
            1000f
        );

        int sensorRadius = GetInt(
            droneSensorRadiusInput,
            droneSwarmDemoBootstrap.SensorRadius,
            1,
            8
        );

        float cellSize = GetFloat(
            droneCellSizeInput,
            droneSwarmDemoBootstrap.CellSize,
            1f,
            12f
        );

        droneSwarmDemoBootstrap.ConfigureStartSettings(
            droneCount,
            communicationRadius,
            droneSpeed,
            sensorRadius,
            cellSize
        );
    }

    private void ApplyExplorerSettings()
    {
        if (explorer == null)
        {
            return;
        }

        explorer.scanRadius = GetFloat(explorerScanRadiusInput, explorer.scanRadius, 1f, 10000f);
        explorer.minTargetDistance = GetFloat(explorerMinTargetDistanceInput, explorer.minTargetDistance, 0f, 10000f);
        explorer.moveSpeed = GetFloat(explorerMoveSpeedInput, explorer.moveSpeed, 0f, 1000f);

        explorer.stamina = GetFloat(explorerStaminaInput, explorer.stamina, 0f, 100f);
        explorer.staminaDecreasePerSecond = GetFloat(explorerStaminaDecreaseInput, explorer.staminaDecreasePerSecond, 0f, 1000f);
        explorer.staminaRecoveryPerSecond = GetFloat(explorerStaminaRecoveryInput, explorer.staminaRecoveryPerSecond, 0f, 1000f);
        explorer.restartThreshold = GetFloat(explorerRestartThresholdInput, explorer.restartThreshold, 0f, 100f);
        explorer.recoveryDecay = GetFloat(explorerRecoveryDecayInput, explorer.recoveryDecay, 0f, 100f);

        explorer.maxWalkableSlope = GetFloat(explorerMaxWalkableSlopeInput, explorer.maxWalkableSlope, 0f, 90f);
    }

    private void ApplyGameFlowSettings()
    {
        if (gameFlowController == null)
        {
            return;
        }

        bool useSearchTimeLimit =
            useSearchTimeLimitToggle != null
                ? useSearchTimeLimitToggle.isOn
                : gameFlowController.UseSearchTimeLimit;

        float searchTimeLimitSeconds = GetFloat(
            searchTimeLimitInput,
            gameFlowController.SearchTimeLimitSeconds,
            1f,
            36000f
        );

        bool useDroneReturnTimeLimit =
            useDroneReturnTimeLimitToggle != null
                ? useDroneReturnTimeLimitToggle.isOn
                : gameFlowController.UseDroneReturnTimeLimit;

        float droneReturnTimeLimitSeconds = GetFloat(
            droneReturnTimeLimitInput,
            gameFlowController.DroneReturnTimeLimitSeconds,
            1f,
            36000f
        );

        gameFlowController.ConfigureTimeLimits(
            useSearchTimeLimit,
            searchTimeLimitSeconds,
            useDroneReturnTimeLimit,
            droneReturnTimeLimitSeconds
        );
    }

    private int GetInt(TMP_InputField input, int currentValue, int min, int max)
    {
        if (input == null)
        {
            return currentValue;
        }

        if (!int.TryParse(input.text, out int value))
        {
            input.text = currentValue.ToString();
            return currentValue;
        }

        value = Mathf.Clamp(value, min, max);
        input.text = value.ToString();

        return value;
    }

    private float GetFloat(TMP_InputField input, float currentValue, float min, float max)
    {
        if (input == null)
        {
            return currentValue;
        }

        if (!float.TryParse(input.text, out float value))
        {
            input.text = currentValue.ToString("0.###");
            return currentValue;
        }

        value = Mathf.Clamp(value, min, max);
        input.text = value.ToString("0.###");

        return value;
    }

    private void SetText(TMP_InputField input, int value)
    {
        if (input != null)
        {
            input.text = value.ToString();
        }
    }

    private void SetText(TMP_InputField input, float value)
    {
        if (input != null)
        {
            input.text = value.ToString("0.###");
        }
    }
    private void SetToggle(Toggle toggle, bool value)
    {
        if (toggle != null)
        {
            toggle.isOn = value;
        }
    }
}