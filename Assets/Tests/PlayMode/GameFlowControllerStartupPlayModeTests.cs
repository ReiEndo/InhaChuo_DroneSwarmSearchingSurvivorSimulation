using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class GameFlowControllerStartupPlayModeTests
{
    [UnityTest]
    public IEnumerator InactiveGameRootIsActivatedBeforeValidationAndRolledBackOnFailure()
    {
        TerrainData terrainData = new TerrainData();
        GameObject terrainObject = Terrain.CreateTerrainGameObject(terrainData);
        terrainObject.name = "startup-test-terrain";

        GameObject gameRoot = new GameObject("startup-test-game-root");
        gameRoot.SetActive(false);
        Component explorer = AddComponent(gameRoot, "Explorer");
        Component bootstrap = AddComponent(gameRoot, "DroneSwarmDemoBootstrap");

        GameObject setupObject = new GameObject("startup-test-setup");
        Component terrainGenerator = AddComponent(setupObject, "TerrainGenerator");
        Component forestSpawner = AddComponent(setupObject, "ForestSpawner");
        Component scriptsControl = AddComponent(setupObject, "ScriptsControl");
        SetField(terrainGenerator, "terrain", terrainObject.GetComponent<Terrain>());
        SetField(forestSpawner, "terrain", terrainObject.GetComponent<Terrain>());
        SetField(scriptsControl, "terrainGenerator", terrainGenerator);
        SetField(scriptsControl, "forestSpawner", forestSpawner);
        SetField(scriptsControl, "droneSwarmDemoBootstrap", bootstrap);
        SetField(scriptsControl, "explorer", explorer);
        // Leave UiScriptsControl null so setup fails only after both required objects
        // under gameRoot have passed their active-state validation.

        GameObject startRoot = new GameObject("startup-test-start-root");
        GameObject gameScreen = new GameObject("startup-test-game-screen");
        GameObject flowObject = new GameObject("startup-test-flow");
        Component flow = AddComponent(flowObject, "GameFlowController");
        SetField(flow, "scriptsControl", scriptsControl);
        SetField(flow, "startRoot", startRoot);
        SetField(flow, "gameRoot", gameRoot);
        SetField(flow, "gameScreen", gameScreen);

        Assert.That(gameRoot.activeSelf, Is.False, "The authored title state must begin inactive.");

        LogAssert.Expect(LogType.Error, "[ScriptsControl] A UiScriptsControl reference is required.");
        LogAssert.Expect(LogType.Error,
            new System.Text.RegularExpressions.Regex(
                "\\[GameFlowController\\] Simulation setup failed; gameplay was not started\\."));

        flow.GetType().GetMethod("OnClickStart", BindingFlags.Instance | BindingFlags.Public)
            .Invoke(flow, null);

        Assert.That(gameRoot.activeSelf, Is.False,
            "A failed setup must restore the inactive gameplay root.");
        Assert.That(startRoot.activeSelf, Is.True);
        Assert.That(gameScreen.activeSelf, Is.False,
            "Activating dependencies must not expose gameplay UI before setup succeeds.");

        UnityEngine.Object.Destroy(flowObject);
        UnityEngine.Object.Destroy(startRoot);
        UnityEngine.Object.Destroy(gameScreen);
        UnityEngine.Object.Destroy(setupObject);
        UnityEngine.Object.Destroy(gameRoot);
        UnityEngine.Object.Destroy(terrainObject);
        UnityEngine.Object.Destroy(terrainData);
        yield return null;
    }

    private static Component AddComponent(GameObject gameObject, string typeName)
    {
        Type type = Type.GetType($"{typeName}, Assembly-CSharp", throwOnError: false);
        Assert.That(type, Is.Not.Null, $"Required runtime type {typeName} was not found.");
        return gameObject.AddComponent(type);
    }

    private static void SetField(Component target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Field was not found: {fieldName}");
        field.SetValue(target, value);
    }
}
