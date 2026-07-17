using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

public sealed class StartSettingsResponsiveLayoutPlayModeTests
{
    [UnityTest]
    public IEnumerator SettingsMenuUsesExpandScalingAndRestoresCanvasModeOnClose()
    {
        var canvasObject = new GameObject("settings-layout-test-canvas", typeof(Canvas), typeof(CanvasScaler));
        var settingsPanel = new GameObject("settings-layout-test-panel", typeof(RectTransform));
        settingsPanel.transform.SetParent(canvasObject.transform, false);
        var controllerObject = new GameObject("settings-layout-test-controller");
        Component controller = AddComponent(controllerObject, "StartSettingsController");

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        SetField(controller, "settingsPanel", settingsPanel);

        Invoke(controller, "OpenSettings");

        Assert.That(settingsPanel.activeSelf, Is.True);
        Assert.That(scaler.screenMatchMode, Is.EqualTo(CanvasScaler.ScreenMatchMode.Expand));

        Invoke(controller, "CloseSettings");

        Assert.That(settingsPanel.activeSelf, Is.False);
        Assert.That(scaler.screenMatchMode, Is.EqualTo(CanvasScaler.ScreenMatchMode.MatchWidthOrHeight));
        Assert.That(scaler.matchWidthOrHeight, Is.EqualTo(0.5f));

        UnityEngine.Object.Destroy(controllerObject);
        UnityEngine.Object.Destroy(canvasObject);
        yield return null;
    }

    private static Component AddComponent(GameObject gameObject, string typeName)
    {
        Type type = Type.GetType($"{typeName}, Assembly-CSharp", throwOnError: false);
        Assert.That(type, Is.Not.Null, $"Required runtime type {typeName} was not found.");
        return gameObject.AddComponent(type);
    }

    private static void Invoke(object target, string methodName)
    {
        MethodInfo method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public);
        Assert.That(method, Is.Not.Null, $"Method was not found: {methodName}");
        method.Invoke(target, null);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Field was not found: {fieldName}");
        field.SetValue(target, value);
    }
}
