using System.Globalization;
using Newtonsoft.Json.Bson;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class StartSettingsController : MonoBehaviour
{
    private static readonly CultureInfo NumericCulture = CultureInfo.InvariantCulture;
    private const NumberStyles FloatNumberStyles = NumberStyles.Float;
    private const string FloatFormat = "0.###";

    private CanvasScaler settingsCanvasScaler;
    private CanvasScaler.ScreenMatchMode originalScreenMatchMode;
    private float originalMatchWidthOrHeight;
    private bool settingsCanvasScaleOverridden;

    [Header("Panel")]
    [SerializeField] private GameObject settingsPanel;

    [Header("Targets")]
    [SerializeField] private TerrainGenerator terrainGenerator;
    [SerializeField] private ForestSpawner forestSpawner;
    [SerializeField] private DroneSwarmDemoBootstrap droneSwarmDemoBootstrap;
    [SerializeField] private Explorer explorer;
    [SerializeField] private GameFlowController gameFlowController;

    [Header("TerrainGenerator")]
    [SerializeField] private TMP_InputField terrainWidthInput;
    [SerializeField] private TMP_InputField terrainScaleInput;
    [SerializeField] private TMP_InputField terrainHeightInput;

    [Header("ForestSpawner")]
    [SerializeField] private TMP_InputField CountPer100mmInput;

    [SerializeField] private Slider treePercentSlider;
    [SerializeField] private TextMeshProUGUI treePercentValueText;
    [SerializeField] private TextMeshProUGUI rockPercentValueText;

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
        RestoreCanvasScaleMode();

        if (settingsPanel != null)
        {
            settingsPanel.SetActive(false);
        }

        LoadCurrentValuesToUI();
    }

    public void OpenSettings()
    {
        LoadCurrentValuesToUI();
        UseCanvasExpandScaleMode();

        if (settingsPanel != null)
        {
            settingsPanel.SetActive(true);
        }

        Canvas.ForceUpdateCanvases();
    }

    public void CloseSettings()
    {
        ApplySettings();

        if (settingsPanel != null)
        {
            settingsPanel.SetActive(false);
        }

        RestoreCanvasScaleMode();
    }

    private void OnDisable()
    {
        RestoreCanvasScaleMode();
    }

    private void UseCanvasExpandScaleMode()
    {
        if (settingsCanvasScaleOverridden || settingsPanel == null)
        {
            return;
        }

        Canvas settingsCanvas = settingsPanel.GetComponentInParent<Canvas>();
        settingsCanvasScaler = settingsCanvas != null
            ? settingsCanvas.GetComponent<CanvasScaler>()
            : null;

        if (settingsCanvasScaler == null
            || settingsCanvasScaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
        {
            settingsCanvasScaler = null;
            return;
        }

        originalScreenMatchMode = settingsCanvasScaler.screenMatchMode;
        originalMatchWidthOrHeight = settingsCanvasScaler.matchWidthOrHeight;
        settingsCanvasScaleOverridden = true;

        // Expand keeps the reference layout visible at narrower aspect ratios.
        settingsCanvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
    }

    private void RestoreCanvasScaleMode()
    {
        if (!settingsCanvasScaleOverridden)
        {
            return;
        }

        if (settingsCanvasScaler != null)
        {
            settingsCanvasScaler.screenMatchMode = originalScreenMatchMode;
            settingsCanvasScaler.matchWidthOrHeight = originalMatchWidthOrHeight;
        }

        settingsCanvasScaleOverridden = false;
        settingsCanvasScaler = null;
    }

    public void ApplySettings()
    {
        ApplyExplorerSettings();
        ApplyTerrainSettings();
        ApplyForestSettings();
        ApplyDroneSettings();
        ApplyGameFlowSettings();
    }

    private void LoadCurrentValuesToUI()
    {

        if (terrainGenerator != null)
        {
            SetText(terrainWidthInput, terrainGenerator.widthx);
            SetText(terrainScaleInput, terrainGenerator.scale);
            SetText(terrainHeightInput, terrainGenerator.terrainHeight);
        }

        if (forestSpawner != null)
        {
            SetText(CountPer100mmInput, forestSpawner.objectsPer100SquareMeters);
            SetText(minHeightInput, forestSpawner.minHeight);
            SetText(maxHeightInput, forestSpawner.maxHeight);
            SetText(maxSlopeInput, forestSpawner.maxSlope);
            SetText(treeScaleMinInput, forestSpawner.scaleRange.x);
            SetText(treeScaleMaxInput, forestSpawner.scaleRange.y);
            SetText(minTreeDistanceInput, forestSpawner.minDistance);
            SetText(maxSpawnAttemptInput, forestSpawner.maxSpawnAttemptCounts);

            SetSlider(treePercentSlider, forestSpawner.treePercent);
            UpdateTreePercentText(forestSpawner.treePercent);
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
            SetText(explorerStaminaInput, explorer.MissionStartingStamina);
            SetText(explorerStaminaDecreaseInput, explorer.staminaDecreasePerSecond);
            SetText(explorerStaminaRecoveryInput, explorer.MissionStartingRecoveryPerSecond);
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

        int minimumWidth = TerrainGenerator.MinWorldDimension;
        if (explorer != null)
        {
            minimumWidth = Mathf.Clamp(
                explorer.GetMinimumTerrainDimension(),
                TerrainGenerator.MinWorldDimension,
                TerrainGenerator.MaxWorldDimension
            );
        }

        int worldWidth = GetInt(
            terrainWidthInput,
            terrainGenerator.widthx,
            minimumWidth,
            TerrainGenerator.MaxWorldDimension
        );
        terrainGenerator.widthx = worldWidth;
        terrainGenerator.widthz = worldWidth;
        terrainGenerator.scale = GetFloat(terrainScaleInput, terrainGenerator.scale, 1f, 5000f);
        terrainGenerator.terrainHeight = GetFloat(terrainHeightInput, terrainGenerator.terrainHeight, 1f, 1000f);

        if (explorer != null)
        {
            explorer.ValidateConfigurationForTerrain(worldWidth, worldWidth, false);
            SetText(explorerScanRadiusInput, explorer.scanRadius);
            SetText(explorerMinTargetDistanceInput, explorer.minTargetDistance);
        }
    }

    private void ApplyForestSettings()
    {
        if (forestSpawner == null)
        {
            return;
        }

        forestSpawner.objectsPer100SquareMeters = GetFloat(CountPer100mmInput, forestSpawner.objectsPer100SquareMeters, 0f, 100f);
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
        forestSpawner.maxSpawnAttemptCounts = GetInt(
            maxSpawnAttemptInput,
            forestSpawner.maxSpawnAttemptCounts,
            1,
            ForestSpawner.MaximumAttemptsPerObject
        );

        forestSpawner.treePercent = GetSliderValue(treePercentSlider, forestSpawner.treePercent);

        TerrainData forestTerrainData = forestSpawner.terrain != null
            ? forestSpawner.terrain.terrainData
            : null;
        float forestWidth = terrainGenerator != null
            ? terrainGenerator.widthx
            : forestTerrainData != null ? forestTerrainData.size.x : 0f;
        float forestDepth = terrainGenerator != null
            ? terrainGenerator.widthz
            : forestTerrainData != null ? forestTerrainData.size.z : 0f;
        forestSpawner.ClampDensityForArea(forestWidth, forestDepth, true);
        SetText(CountPer100mmInput, forestSpawner.objectsPer100SquareMeters);
        SetText(maxSpawnAttemptInput, forestSpawner.maxSpawnAttemptCounts);
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

        float scanRadius = GetFloat(explorerScanRadiusInput, explorer.scanRadius, 1f, 10000f);
        float minTargetDistance = GetFloat(
            explorerMinTargetDistanceInput,
            explorer.minTargetDistance,
            0f,
            scanRadius
        );

        explorer.scanRadius = scanRadius;
        explorer.minTargetDistance = minTargetDistance;
        SetText(explorerMinTargetDistanceInput, minTargetDistance);
        explorer.moveSpeed = GetFloat(explorerMoveSpeedInput, explorer.moveSpeed, 0f, 1000f);

        float startingStamina = GetFloat(
            explorerStaminaInput,
            explorer.MissionStartingStamina,
            0f,
            100f
        );
        float startingRecovery = GetFloat(
            explorerStaminaRecoveryInput,
            explorer.MissionStartingRecoveryPerSecond,
            0f,
            1000f
        );
        explorer.ConfigureMissionStamina(startingStamina, startingRecovery);
        explorer.staminaDecreasePerSecond = GetFloat(explorerStaminaDecreaseInput, explorer.staminaDecreasePerSecond, 0f, 1000f);
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
        int value = currentValue;
        if (input != null && !int.TryParse(input.text, out value))
        {
            value = currentValue;
        }

        value = Mathf.Clamp(value, min, max);
        if (input != null)
        {
            input.text = value.ToString();
        }

        return value;
    }

    private float GetFloat(TMP_InputField input, float currentValue, float min, float max)
    {
        float value = currentValue;
        bool rejectedInput = false;
        string rejectionReason = null;

        if (input != null)
        {
            if (!float.TryParse(input.text, FloatNumberStyles, NumericCulture, out value))
            {
                rejectedInput = true;
                rejectionReason = "not a valid number (use '.' as the decimal separator)";
            }
            else if (!IsFinite(value))
            {
                rejectedInput = true;
                rejectionReason = "not a finite number";
            }
        }

        if (rejectedInput || !IsFinite(value))
        {
            value = IsFinite(currentValue) ? currentValue : min;
        }

        value = Mathf.Clamp(value, min, max);
        if (input != null)
        {
            string normalizedValue = FormatFloat(value);
            if (rejectedInput)
            {
                Debug.LogWarning(
                    $"Start setting '{input.name}' rejected '{input.text}': {rejectionReason}. Using '{normalizedValue}'.",
                    input
                );
            }

            input.text = normalizedValue;
        }

        return value;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static string FormatFloat(float value)
    {
        return value.ToString(FloatFormat, NumericCulture);
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
            input.text = FormatFloat(value);
        }
    }
    private void SetToggle(Toggle toggle, bool value)
    {
        if (toggle != null)
        {
            toggle.isOn = value;
        }
    }
    private void SetSlider(Slider slider, float value)
    {
        if(slider == null)
        {
            return;
        }
        slider.minValue = 0f;
        slider.maxValue = 100f;
        slider.wholeNumbers = true;
        slider.value = Mathf.Clamp(value, slider.minValue, slider.maxValue);
    }
    private float GetSliderValue(Slider slider, float currentValue)
    {
        if(slider == null)
        {
            return currentValue;
        }
        return slider.value;
    }
    private void UpdateTreePercentText(float treePercent)
    {
        if(treePercentValueText  == null)
        {
            return;
        }
        float clampedTreePercent = Mathf.Clamp(treePercent, 0f, 100f);
        float rockPercent = 100f - clampedTreePercent;
        treePercentValueText.text = $"Tree {clampedTreePercent.ToString("0", NumericCulture)}%";
        rockPercentValueText.text = $"Rock {rockPercent.ToString("0", NumericCulture)}%";
    }
    public void OnTreePercentSliderChanged()
    {
        if(treePercentSlider == null)
        {
            return;
        }
        UpdateTreePercentText(treePercentSlider.value);
    }
}