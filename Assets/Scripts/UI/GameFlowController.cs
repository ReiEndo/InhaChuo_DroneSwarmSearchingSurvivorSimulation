using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class GameFlowController : MonoBehaviour
{
    private enum GameEndReason
    {
        None,
        Success,
        SearchTimeout,
        DroneReturnTimeout,
        Forced
    }

    [Header("Screens")]
    [SerializeField] private GameObject startScreen;
    [SerializeField] private GameObject gameScreen;
    [SerializeField] private GameObject resultScreen;

    [Header("Main Control")]
    [SerializeField] private ScriptsControl scriptsControl;

    [Header("Roots")]
    [SerializeField] private GameObject startRoot;
    [SerializeField] private GameObject gameRoot;
    [SerializeField] private GameObject resultRoot;

    [Header("Time Limits")]
    [SerializeField] private bool useSearchTimeLimit = true;
    [SerializeField] private float searchTimeLimitSeconds = 180f;

    [SerializeField] private bool useDroneReturnTimeLimit = true;
    [SerializeField] private float droneReturnTimeLimitSeconds = 180f;

    [Header("Result Texts")]
    [SerializeField] private TextMeshProUGUI foundTimeText;
    [SerializeField] private TextMeshProUGUI droneReturnTimeText;
    [SerializeField] private TextMeshProUGUI totalTimeText;
    [SerializeField] private TextMeshProUGUI resultTitleText;

    [Header("Result Preview")]
    [SerializeField] private RawImage resultPreviewImage;
    [SerializeField] private Camera resultOverviewCamera;
    [SerializeField] private RenderTexture resultOverviewTexture;

    [Header("Result Camera Hidden Layers")]
    [SerializeField] private string[] resultHiddenLayerNames = { "GridWorld", "Marker" };

    [Header("Start Settings")]
    [SerializeField] private StartSettingsController startSettingsController;

    private float gameStartTime;
    private float foundTime = -1f;
    private float droneReturnTime = -1f;
    private float gameEndTime = -1f;

    private bool gameRunning;
    private bool resultShown;
    private bool startInProgress;
    private bool pendingStartWasReplay;

    private GameEndReason endReason = GameEndReason.None;

    private void Start()
    {
        if (scriptsControl != null && scriptsControl.autoStart)
        {
            OnClickStart();
        }
        else
        {
            ShowStartScreen();
        }
    }

    public void OnClickStart()
    {
        if (gameRunning || startInProgress)
        {
            return;
        }

        if (startSettingsController != null)
        {
            startSettingsController.ApplySettings();
        }

        pendingStartWasReplay = resultShown;
        startInProgress = true;

        // Activate dependencies before setup; UI and timers still wait for completion.
        if (gameRoot != null)
        {
            gameRoot.SetActive(true);
        }

        bool accepted = scriptsControl != null
            && (pendingStartWasReplay
                ? scriptsControl.TryStartNewSimulationAsync(OnSimulationStartCompleted)
                : scriptsControl.TryStartSimulationAsync(OnSimulationStartCompleted));

        // Handle missing ScriptsControl or a rejection without a callback.
        if (!accepted && startInProgress)
        {
            OnSimulationStartCompleted(false, "Simulation startup request was rejected.");
        }
    }

    private void OnSimulationStartCompleted(bool succeeded, string error)
    {
        if (!startInProgress)
        {
            return;
        }

        bool replay = pendingStartWasReplay;
        startInProgress = false;
        pendingStartWasReplay = false;

        if (!succeeded)
        {
            Debug.LogError($"[GameFlowController] Simulation setup failed; gameplay was not started. {error}", this);
            gameRunning = false;
            // Keep failed replay retries on the explicit new-game path.
            resultShown = replay;
            // Cancellation may invoke this synchronously during OnDisable.
            ShowStartScreen();
            return;
        }

        // Batch runners own their lifecycle and timeout.
        if (scriptsControl.BatchAutoStartEnabled)
        {
            ShowStartScreen();
            return;
        }

        if (!isActiveAndEnabled || !gameObject.activeInHierarchy)
        {
            scriptsControl.CancelPendingStart("GameFlowController became inactive after startup.");
            return;
        }

        StartGame();
    }

    private void OnDisable()
    {
        if (!startInProgress)
        {
            return;
        }

        // Keep the guard set through synchronous cancellation callback.
        scriptsControl?.CancelPendingStart("GameFlowController became inactive before startup completed.");
        startInProgress = false;
        pendingStartWasReplay = false;
    }

    private void StartGame()
    {
        DroneMissionEndReporter.ResetMissionState();
        gameStartTime = Time.time;
        foundTime = -1f;
        droneReturnTime = -1f;
        gameEndTime = -1f;
        endReason = GameEndReason.None;

        gameRunning = true;
        resultShown = false;

        ShowGameScreen();
    }

    private void Update()
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        // Discovery takes precedence over a timeout reported in the same frame.
        TrySyncFoundTimeFromDroneReports();

        float elapsed = Time.time - gameStartTime;

        if (foundTime < 0f)
        {
            if (useSearchTimeLimit && elapsed >= searchTimeLimitSeconds)
            {
                droneReturnTime = -1f;
                EndGame(GameEndReason.SearchTimeout);
            }

            return;
        }

        if (useDroneReturnTimeLimit)
        {
            float elapsedAfterFound = elapsed - foundTime;

            if (elapsedAfterFound >= droneReturnTimeLimitSeconds)
            {
                ForceEndDroneReturnFailed();
            }
        }
    }

    public void NotifyExplorerFound()
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        if (foundTime >= 0f)
        {
            return;
        }

        if (!TrySyncFoundTimeFromDroneReports())
        {
            foundTime = Time.time - gameStartTime;
        }
    }
    public void NotifyAllDronesReturned()
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        float elapsed = Time.time - gameStartTime;

        if (foundTime < 0f)
        {
            TrySyncFoundTimeFromDroneReports();
        }

        if (foundTime < 0f)
        {
            foundTime = elapsed;
        }

        droneReturnTime = Mathf.Max(0f, elapsed - foundTime);

        EndGame(GameEndReason.Success);
    }

    public void ForceEndSearchFailed()
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        TrySyncFoundTimeFromDroneReports();

        if (foundTime >= 0f)
        {
            droneReturnTime = -1f;
            EndGame(GameEndReason.DroneReturnTimeout);
            return;
        }

        droneReturnTime = -1f;
        EndGame(GameEndReason.SearchTimeout);
    }

    public void ForceEndDroneReturnFailed()
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        TrySyncFoundTimeFromDroneReports();

        if (foundTime < 0f)
        {
            droneReturnTime = -1f;
            EndGame(GameEndReason.SearchTimeout);
            return;
        }

        droneReturnTime = -1f;
        EndGame(GameEndReason.DroneReturnTimeout);
    }

    private bool TrySyncFoundTimeFromDroneReports()
    {
        if (foundTime >= 0f)
        {
            return true;
        }

        bool found = false;
        float earliestObservedAt = float.MaxValue;

        foreach (DroneSwarmAgentState state in DroneSwarmAgentState.ActiveAgents)
        {
            if (state == null || state.LocalMap == null)
            {
                continue;
            }

            if (!state.LocalMap.TryGetEarliestTargetReport(out DroneTargetReport report))
            {
                continue;
            }

            if (!found || report.ObservedAt < earliestObservedAt)
            {
                earliestObservedAt = report.ObservedAt;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        foundTime = Mathf.Max(0f, earliestObservedAt - gameStartTime);
        return true;
    }

    public void ForceEnd()
    {
        TryHandleTelemetryTimeout();
    }

    /// <summary>Ends an owned run while preserving GameFlow's timeout classification.</summary>
    public bool TryHandleTelemetryTimeout()
    {
        if (!gameRunning || resultShown)
        {
            return false;
        }

        TrySyncFoundTimeFromDroneReports();
        droneReturnTime = -1f;
        EndGame(foundTime < 0f
            ? GameEndReason.SearchTimeout
            : GameEndReason.DroneReturnTimeout);
        return true;
    }

    private void EndGame(GameEndReason reason)
    {
        // The first same-frame end notification is authoritative.
        if (!gameRunning || resultShown)
        {
            return;
        }

        bool explorerFound = TrySyncFoundTimeFromDroneReports();

        // Correct a same-frame search timeout if discovery already occurred.
        if (reason == GameEndReason.SearchTimeout && explorerFound)
        {
            reason = GameEndReason.DroneReturnTimeout;
        }

        gameRunning = false;
        resultShown = true;
        endReason = reason;
        gameEndTime = Time.time - gameStartTime;

        // Successful result smoke must not depend on which drone observes completion first.
        if (reason == GameEndReason.Success)
        {
            DroneMissionEndReporter.EnsureTargetSmokeForResult();
        }

        StopSimulation(reason);
        ShowResultScreen();
    }

    private void StopSimulation(GameEndReason reason)
    {
        DroneSwarmDemoBootstrap bootstrap = scriptsControl != null
            ? scriptsControl.droneSwarmDemoBootstrap
            : null;

        if (bootstrap == null)
        {
            bootstrap = FindAnyObjectByType<DroneSwarmDemoBootstrap>();
        }

        if (bootstrap != null)
        {
            bootstrap.StopSimulation(GetTelemetryEndReason(reason), reason == GameEndReason.Success);
        }
    }

    private static string GetTelemetryEndReason(GameEndReason reason)
    {
        switch (reason)
        {
            case GameEndReason.Success:
                return "mission_complete";
            case GameEndReason.SearchTimeout:
                return "search_timeout";
            case GameEndReason.DroneReturnTimeout:
                return "drone_return_timeout";
            case GameEndReason.Forced:
                return "forced";
            default:
                return "ended";
        }
    }

    public void OnClickBackToTitle()
    {
        if (gameRunning)
        {
            return;
        }

        // Rebuild the stopped result world only on the next Start.
        ShowStartScreen();
    }

    private void ShowStartScreen()
    {
        if (startRoot != null)
        {
            startRoot.SetActive(true);
        }
        if (gameRoot != null)
        {
            gameRoot.SetActive(false);
        }
        if (resultRoot != null)
        {
            resultRoot.SetActive(false);
        }

        if (startScreen != null)
        {
            startScreen.SetActive(true);
        }

        if (startSettingsController  != null)
        {
            startSettingsController.StartSettings_Start();
        }

        if (gameScreen != null)
        {
            gameScreen.SetActive(false);
        }

        if (resultScreen != null)
        {
            resultScreen.SetActive(false);
        }

        if (resultPreviewImage != null)
        {
            resultPreviewImage.gameObject.SetActive(false);
        }
    }

    private void ShowGameScreen()
    {
        if (startRoot  != null)
        {
            startRoot.SetActive(false);
        }
        if (gameRoot != null)
        {
            gameRoot.SetActive(true);
        }
        if (resultRoot != null)
        {
            resultRoot.SetActive(false);
        }

        if (startScreen != null)
        {
            startScreen.SetActive(false);
        }

        if (gameScreen != null)
        {
            gameScreen.SetActive(true);
        }

        if (resultScreen != null)
        {
            resultScreen.SetActive(false);
        }

        if (resultPreviewImage != null)
        {
            resultPreviewImage.gameObject.SetActive(false);
        }
    }

    private void ShowResultScreen()
    {
        if (startRoot != null)
        {
            startRoot.SetActive(false);
        }
        if (resultRoot != null)
        {
            resultRoot.SetActive(true);
        }

        if (startScreen != null)
        {
            startScreen.SetActive(false);
        }

        if (gameScreen != null)
        {
            gameScreen.SetActive(false);
        }

        if (resultScreen != null)
        {
            resultScreen.SetActive(true);
        }

        SetupResultPreview();
        UpdateResultText();
    }

    private void UpdateResultText()
    {
        bool explorerWasFound = foundTime >= 0f;
        bool droneReturnSucceeded = endReason == GameEndReason.Success;

        if (resultTitleText != null)
        {
            resultTitleText.text = GetResultTitle();
        }

        if (foundTimeText != null)
        {
            foundTimeText.text = "FoundTime : " + (explorerWasFound
                ? FormatTime(foundTime)
                : "FAIL");
        }

        if (droneReturnTimeText != null)
        {
            droneReturnTimeText.text = "DroneReturnTime : " + (droneReturnSucceeded
                ? FormatTime(droneReturnTime)
                : "FAIL");
        }

        if (totalTimeText != null)
        {
            totalTimeText.text = "TotalTime : " + FormatTime(gameEndTime);
        }
    }

    private string GetResultTitle()
    {
        switch (endReason)
        {
            case GameEndReason.Success:
                return "MISSION COMPLETE";

            case GameEndReason.SearchTimeout:
                return "SEARCH FAILED";

            case GameEndReason.DroneReturnTimeout:
                return "RETURN FAILED";

            case GameEndReason.Forced:
                return "MISSION ENDED";

            default:
                return "RESULT";
        }
    }

    private string FormatTimeOrFail(float time)
    {
        if (time < 0f)
        {
            return "FAIL";
        }

        return FormatTime(time);
    }

    private string FormatTime(float time)
    {
        if (time < 0f)
        {
            time = 0f;
        }

        int totalSeconds = Mathf.FloorToInt(time);
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;

        return $"{minutes:00}:{seconds:00}";
    }

    private void SetupResultPreview()
    {
        if (resultPreviewImage == null)
        {
            return;
        }

        if (resultOverviewCamera == null || resultOverviewTexture == null)
        {
            resultPreviewImage.gameObject.SetActive(false);
            return;
        }

        resultOverviewCamera.targetTexture = resultOverviewTexture;
        resultPreviewImage.texture = resultOverviewTexture;
        resultPreviewImage.gameObject.SetActive(true);
        ApplyHiddenLayers(resultOverviewCamera, resultHiddenLayerNames);
    }

    private void ApplyHiddenLayers(Camera targetCamera, string[] hiddenLayerNames)
    {
        if (targetCamera == null || hiddenLayerNames == null)
        {
            return;
        }

        int mask = targetCamera.cullingMask;

        foreach (string layerName in hiddenLayerNames)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                continue;
            }

            int layer = LayerMask.NameToLayer(layerName);

            if (layer < 0)
            {
                Debug.LogWarning($"{layerName} レイヤーが見つかりません。リザルトカメラでは非表示にできません。");
                continue;
            }

            mask &= ~(1 << layer);
        }

        targetCamera.cullingMask = mask;
    }


    public bool UseSearchTimeLimit => useSearchTimeLimit;
    public float SearchTimeLimitSeconds => searchTimeLimitSeconds;
    public bool UseDroneReturnTimeLimit => useDroneReturnTimeLimit;
    public float DroneReturnTimeLimitSeconds => droneReturnTimeLimitSeconds;
    public void ConfigureTimeLimits(
    bool newUseSearchTimeLimit,
    float newSearchTimeLimitSeconds,
    bool newUseDroneReturnTimeLimit,
    float newDroneReturnTimeLimitSeconds
    )
    {
        useSearchTimeLimit = newUseSearchTimeLimit;
        searchTimeLimitSeconds = Mathf.Max(1f, newSearchTimeLimitSeconds);

        useDroneReturnTimeLimit = newUseDroneReturnTimeLimit;
        droneReturnTimeLimitSeconds = Mathf.Max(1f, newDroneReturnTimeLimitSeconds);
    }
}