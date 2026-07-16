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
        bool accepted = scriptsControl != null
            && (pendingStartWasReplay
                ? scriptsControl.TryStartNewSimulationAsync(OnSimulationStartCompleted)
                : scriptsControl.TryStartSimulationAsync(OnSimulationStartCompleted));

        // Rejections normally invoke the callback synchronously. This fallback also
        // covers a missing ScriptsControl reference.
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
            // Preserve resultShown for a failed replay so another click still takes
            // the explicit new-game path.
            resultShown = replay;
            if (isActiveAndEnabled && gameObject.activeInHierarchy)
            {
                ShowStartScreen();
            }
            return;
        }

        // Auto-start batch runners own their mission lifecycle asynchronously. Do not
        // start a GameFlow timeout for a run that this controller did not launch.
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

        // CancelQueuedReset reports failure synchronously. Leave the guard set until
        // that callback has observed the cancellation.
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

        // 重要：
        // TimeLimit判定より先に、Drone側の発見情報をGameFlowControllerへ同期する
        TrySyncFoundTimeFromDroneReports();

        float elapsed = Time.time - gameStartTime;

        // まだ本当に発見していない場合だけSearchTimeLimitを見る
        if (foundTime < 0f)
        {
            if (useSearchTimeLimit && elapsed >= searchTimeLimitSeconds)
            {
                droneReturnTime = -1f;
                EndGame(GameEndReason.SearchTimeout);
            }

            return;
        }

        // ここに来た時点で遭難者は発見済み
        // SearchTimeLimitはもう見ない
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

        // まずDrone側のTargetReportから正確な発見時刻を取る
        if (!TrySyncFoundTimeFromDroneReports())
        {
            // TargetReportがまだ取れない場合だけ現在時刻を使う
            foundTime = Time.time - gameStartTime;
        }
    }
    //ドローン全機集合時、このメソッドを実行する
    public void NotifyAllDronesReturned()
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        float elapsed = Time.time - gameStartTime;

        // 成功前にも念のため発見時刻を同期する
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

        // 強制的にSearchFailedにする前に、Drone側では発見済みでないか確認する
        TrySyncFoundTimeFromDroneReports();

        if (foundTime >= 0f)
        {
            // 既に見つけているならSearch失敗ではなくReturn失敗
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
            // 本当にまだ見つけていない場合はSearch失敗
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

    /// <summary>
    /// Ends an actively owned interactive run at an external simulation timeout.
    /// GameFlow remains authoritative for the search-versus-return timeout reason.
    /// </summary>
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
        // EndGame can be reached by UI, timeout, and drone callbacks in the same frame.
        // The first result is authoritative; later notifications must not replace it.
        if (!gameRunning || resultShown)
        {
            return;
        }

        // 終了直前にもDrone側の発見情報を確認する
        bool explorerFound = TrySyncFoundTimeFromDroneReports();

        // SearchTimeout扱いで来ても、実はDroneが発見済みならReturn失敗へ変える
        if (reason == GameEndReason.SearchTimeout && explorerFound)
        {
            reason = GameEndReason.DroneReturnTimeout;
        }

        gameRunning = false;
        resultShown = true;
        endReason = reason;
        gameEndTime = Time.time - gameStartTime;

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

        // Keep the stopped result world intact for result rendering. The next Start
        // click is the explicit boundary that safely rebuilds it in this scene.
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