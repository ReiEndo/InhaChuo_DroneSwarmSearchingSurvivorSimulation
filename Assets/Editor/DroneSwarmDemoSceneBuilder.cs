using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class DroneSwarmDemoSceneBuilder
{
    private const string c_ScenePath = "Assets/Scenes/DroneSwarmDemo.unity";

    public static void SmokeTestSwarmInSampleScene()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);

        var bootstrap = new GameObject("Drone Swarm Terrain Smoke Test Bootstrap").AddComponent<DroneSwarmDemoBootstrap>();
        InvokePrivate(bootstrap, "ClearRuntimeState");
        InvokePrivate(bootstrap, "BuildWorld");
        InvokePrivate(bootstrap, "BuildSwarm");
        InvokePrivate(bootstrap, "BuildDebugRenderer");
        InvokePrivate(bootstrap, "BuildUi");

        int explorerCount = Object.FindObjectsByType<DroneFrontierExplorer>().Length;
        int sensorCount = Object.FindObjectsByType<DroneGridSensor>().Length;
        var commandPlanner = Object.FindAnyObjectByType<DroneCommandRoutePlanner>();
        var debugRenderer = Object.FindAnyObjectByType<DroneSwarmDebugRenderer>();

        if (explorerCount <= 0 || sensorCount != explorerCount || commandPlanner == null || debugRenderer == null)
        {
            throw new System.InvalidOperationException($"Drone swarm smoke test failed: explorers={explorerCount}, sensors={sensorCount}, commandPlanner={commandPlanner != null}, debugRenderer={debugRenderer != null}");
        }

        Debug.Log($"Drone swarm smoke test passed in SampleScene: explorers={explorerCount}, sensors={sensorCount}.");
    }

    private static void InvokePrivate(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method == null)
        {
            throw new System.MissingMethodException(target.GetType().Name, methodName);
        }

        method.Invoke(target, null);
    }

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
