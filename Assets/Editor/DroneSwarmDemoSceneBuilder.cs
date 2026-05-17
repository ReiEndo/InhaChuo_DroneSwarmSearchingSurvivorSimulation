using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class DroneSwarmDemoSceneBuilder
{
    private const string c_ScenePath = "Assets/Scenes/DroneSwarmDemo.unity";

    public static void RebuildSwarmDemoScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var bootstrap = new GameObject("bootstrap");
        bootstrap.AddComponent<DroneSwarmDemoBootstrap>();

        var cameraObject = new GameObject("Main Camera");
        cameraObject.tag = "MainCamera";
        var camera = cameraObject.AddComponent<Camera>();
        camera.transform.position = new Vector3(0f, 13.5f, -9.5f);
        camera.transform.rotation = Quaternion.Euler(58f, 0f, 0f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.025f, 0.028f, 0.032f);
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 100f;

        var lightObject = new GameObject("Directional Light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        EditorSceneManager.SaveScene(scene, c_ScenePath);
    }
}
