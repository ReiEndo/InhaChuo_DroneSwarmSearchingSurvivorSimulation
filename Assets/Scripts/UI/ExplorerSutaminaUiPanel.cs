using UnityEngine;
using UnityEngine.UI;

public class ExplorerStaminaPanelUI : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Explorer explorer;
    [SerializeField] private Slider staminaSlider;
    [SerializeField] private Image fillImage;
    [SerializeField] private Text staminaText;

    [Header("Settings")]
    [SerializeField] private float maxStamina = 100f;
    [SerializeField] private bool autoFindExplorer = true;
    [SerializeField] private bool autoStart = false;

    private bool initialized;

    public void sutaminaUI_Start()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;

        if (explorer == null && autoFindExplorer)
        {
            explorer = FindAnyObjectByType<Explorer>();
        }

        if (staminaSlider == null)
        {
            staminaSlider = GetComponentInChildren<Slider>(true);
        }

        if (staminaSlider != null)
        {
            staminaSlider.minValue = 0f;
            staminaSlider.maxValue = maxStamina;
        }

        UpdateStaminaUI();
    }

    private void Update()
    {
        if (!initialized)
        {
            return;
        }

        UpdateStaminaUI();
    }

    private void UpdateStaminaUI()
    {
        if (explorer == null || staminaSlider == null)
        {
            return;
        }

        float stamina = Mathf.Clamp(explorer.stamina, 0f, maxStamina);
        float ratio = stamina / maxStamina;

        staminaSlider.value = stamina;

        if (fillImage != null)
        {
            fillImage.color = Color.Lerp(
                Color.red,
                Color.green,
                ratio
            );
        }

        if (staminaText != null)
        {
            staminaText.text = $"Stamina: {stamina:0}%";
        }
    }

    public void SetExplorer(Explorer newExplorer)
    {
        explorer = newExplorer;
        UpdateStaminaUI();
    }
}