using UnityEngine;
using UnityEngine.UI;

public class StaminaUI : MonoBehaviour
{
    public Explorer explorer;

    private Slider slider;

    public Image fillImage;

    void Start()
    {
        slider = GetComponent<Slider>();
    }

    void Update()
    {
        slider.value = explorer.stamina;

        float t = explorer.stamina / 100f;

        fillImage.color = Color.Lerp(
            Color.red,
            Color.green,
            t
        );
    }
}