using UnityEngine;

public static class DroneDemoVisualUtility
{
    public static void SetRendererColor(GameObject instance, Color color)
    {
        if (instance.TryGetComponent<Renderer>(out var renderer))
        {
            renderer.material.color = color;
        }
    }
}
