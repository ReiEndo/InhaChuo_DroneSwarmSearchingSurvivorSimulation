using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
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
    [SerializeField] private GameObject gameRoot;

    [Header("Time Limits")]
    [SerializeField] private bool useSearchTimeLimit = true;
    [SerializeField] private float searchTimeLimitSeconds = 300f;

    [SerializeField] private bool useDroneReturnTimeLimit = true;
    [SerializeField] private float droneReturnTimeLimitSeconds = 180f;

    [Header("Result Texts")]
    [SerializeField] private TextMeshProUGUI foundTimeText;
    [SerializeField] private TextMeshProUGUI droneReturnTimeText;
    [SerializeField] private TextMeshProUGUI totalTimeText;
    [SerializeField] private TextMeshProUGUI resultTitleText;

    [Header("Future Result Preview")]
    [SerializeField] private RawImage resultPreviewImage;
    [SerializeField] private Camera resultOverviewCamera;
    [SerializeField] private RenderTexture resultOverviewTexture;

    private float gameStartTime;
    private float foundTime = -1f;
    private float droneReturnTime = -1f;
    private float gameEndTime = -1f;

    private bool gameRunning;
    private bool resultShown;

    private GameEndReason endReason = GameEndReason.None;

    private void Start()
    {
        if (scriptsControl != null && scriptsControl.autoStart)
        {
            StartGame();
        }
        else
        {
            ShowStartScreen();
        }
    }

    public void OnClickStart()
    {
        StartGame();

        if (scriptsControl != null)
        {
            scriptsControl.StartSimulation();
        }
    }

    private void StartGame()
    {
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

        float elapsed = Time.time - gameStartTime;

        // まだ発見していないまま探索制限時間を超えた場合
        if (useSearchTimeLimit && foundTime < 0f && elapsed >= searchTimeLimitSeconds)
        {
            ForceEndSearchFailed();
            return;
        }

        // 発見後、回収制限時間を超えた場合
        if (useDroneReturnTimeLimit && foundTime >= 0f)
        {
            float elapsedAfterFound = elapsed - foundTime;

            if (elapsedAfterFound >= droneReturnTimeLimitSeconds)
            {
                ForceEndDroneReturnFailed();
                return;
            }
        }
    }

    public void NotifyExplorerFound()//遭難者発見時、このメソッドを実行する
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        if (foundTime < 0f)
        {
            foundTime = Time.time - gameStartTime;
        }
    }

    public void NotifyAllDronesReturned()//ドローン全機集合時、このメソッドを実行する
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        if (foundTime < 0f)
        {
            // 念のため。通常は発見済みのはず。
            foundTime = Time.time - gameStartTime;
        }

        droneReturnTime = Time.time - gameStartTime;
        EndGame(GameEndReason.Success);
    }

    public void ForceEndSearchFailed()
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        // 見つけられなかったので foundTime は -1 のまま
        droneReturnTime = -1f;
        EndGame(GameEndReason.SearchTimeout);
    }

    public void ForceEndDroneReturnFailed()
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        // foundTime は残す。回収だけFAILにする。
        droneReturnTime = -1f;
        EndGame(GameEndReason.DroneReturnTimeout);
    }

    public void ForceEnd()
    {
        if (!gameRunning || resultShown)
        {
            return;
        }

        if (foundTime < 0f)
        {
            // まだ発見していないなら、探索失敗扱い
            droneReturnTime = -1f;
            EndGame(GameEndReason.SearchTimeout);
        }
        else
        {
            // 発見済みなら、回収失敗扱い
            droneReturnTime = -1f;
            EndGame(GameEndReason.DroneReturnTimeout);
        }
    }

    private void EndGame(GameEndReason reason)
    {
        gameRunning = false;
        resultShown = true;
        endReason = reason;
        gameEndTime = Time.time - gameStartTime;

        ShowResultScreen();
    }

    public void OnClickBackToTitle()
    {
        // 完全に初期状態へ戻したいならScene再読み込みが一番安全
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    private void ShowStartScreen()
    {
        if (gameRoot != null)
        {
            gameRoot.SetActive(false);
        }

        if (startScreen != null)
        {
            startScreen.SetActive(true);
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
        if (gameRoot != null)
        {
            gameRoot.SetActive(true);
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

        SetupFutureResultPreview();
        UpdateResultText();
    }

    private void UpdateResultText()
    {
        if (resultTitleText != null)
        {
            resultTitleText.text = GetResultTitle();
        }

        if (foundTimeText != null)
        {
            foundTimeText.text = "FoundTime : " + FormatTimeOrFail(foundTime);
        }

        if (droneReturnTimeText != null)
        {
            droneReturnTimeText.text = "DroneReturnTime : " + FormatTimeOrFail(droneReturnTime);
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

    private void SetupFutureResultPreview()
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
    }
}