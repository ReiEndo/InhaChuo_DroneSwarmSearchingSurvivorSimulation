using System;
using UnityEngine;

/// <summary>Loads the runtime marker shader from Resources to prevent build stripping.</summary>
public static class RuntimeUnlitShader
{
    private const string ResourcePath = "RuntimeUnlit";
    private static Shader cachedShader;

    public static Shader Get()
    {
        if (cachedShader != null)
        {
            return cachedShader;
        }

        cachedShader = Resources.Load<Shader>(ResourcePath)
            ?? Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Sprites/Default")
            ?? Shader.Find("Unlit/Color");

        if (cachedShader == null)
        {
            throw new InvalidOperationException(
                $"Required runtime shader Resources/{ResourcePath}.shader could not be loaded.");
        }

        return cachedShader;
    }
}
