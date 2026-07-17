using TMPro;
using UnityEngine;

public class ElapsedTimeUI : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TMP_Text timeText;

    [Header("Settings")]
    [SerializeField] private bool autoStart = false;
    [SerializeField] private bool countWhenTimeScaleZero = false;

    private float elapsedTime;
    private bool isRunning;

    private void Update()
    {
        if (!isRunning)
        {
            return;
        }

        if (countWhenTimeScaleZero)
        {
            elapsedTime += Time.unscaledDeltaTime;
        }
        else
        {
            elapsedTime += Time.deltaTime;
        }

        UpdateText();
    }

    public void ElapsedTimeUI_Start()
    {
        elapsedTime = 0f;
        isRunning = true;
        UpdateText();
    }

    public void PauseTimer()
    {
        isRunning = false;
    }

    public void ResumeTimer()
    {
        isRunning = true;
    }

    public void ResetTimer()
    {
        elapsedTime = 0f;
        UpdateText();
    }

    private void UpdateText()
    {
        if (timeText == null)
        {
            return;
        }

        int totalSeconds = Mathf.FloorToInt(elapsedTime);
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;

        timeText.text = $"Time {minutes:00}:{seconds:00}";
    }
}