using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class ForestSpawnerSafetyTests
{
    private GameObject spawnerObject;
    private GameObject terrainObject;
    private GameObject prefabObject;
    private TerrainData terrainData;
    private Component spawner;
    private Type spawnerType;

    [SetUp]
    public void SetUp()
    {
        spawnerType = RequireType("ForestSpawner, Assembly-CSharp");
        terrainData = new TerrainData
        {
            heightmapResolution = 33,
            size = new Vector3(10f, 1f, 10f)
        };
        terrainObject = Terrain.CreateTerrainGameObject(terrainData);

        prefabObject = new GameObject("forest-test-prefab");
        spawnerObject = new GameObject("forest-spawner-test");
        spawner = spawnerObject.AddComponent(spawnerType);
        SetField(spawner, "terrain", terrainObject.GetComponent<Terrain>());
        SetField(spawner, "treePrefab", prefabObject);
        SetField(spawner, "treePercent", 100f);
        SetField(spawner, "objectsPer100SquareMeters", 2f);
        SetField(spawner, "maximumGeneratedObjects", 3);
        SetField(spawner, "minHeight", -1f);
        SetField(spawner, "maxHeight", 1f);
        SetField(spawner, "minDistance", 0f);
        SetField(spawner, "addMissingCollisionProxy", false);
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(spawnerObject);
        UnityEngine.Object.DestroyImmediate(prefabObject);
        UnityEngine.Object.DestroyImmediate(terrainObject);
        UnityEngine.Object.DestroyImmediate(terrainData);
    }

    [Test]
    public void RepeatedSpawnUsesRemainingCapacityAndNeverExceedsLimit()
    {
        Assert.That(TrySpawn(), Is.True);
        Assert.That(SpawnPositionCount, Is.EqualTo(2));

        Assert.That(TrySpawn(), Is.True);
        Assert.That(SpawnPositionCount, Is.EqualTo(3));
        Assert.That(spawner.transform.childCount, Is.EqualTo(3));

        Assert.That(TrySpawn(), Is.True);
        Assert.That(SpawnPositionCount, Is.EqualTo(3));
        Assert.That(spawner.transform.childCount, Is.EqualTo(3));
    }

    [Test]
    public void ClearResetsCapacityAndSpatialState()
    {
        Assert.That(TrySpawn(), Is.True);
        Assert.That(TrySpawn(), Is.True);

        spawnerType.GetMethod("ClearSpawnedTrees").Invoke(spawner, null);

        Assert.That(SpawnPositionCount, Is.Zero);
        Assert.That(spawner.transform.childCount, Is.Zero);
        Assert.That(TrySpawn(), Is.True);
        Assert.That(SpawnPositionCount, Is.EqualTo(2));
    }

    private bool TrySpawn()
    {
        return (bool)spawnerType.GetMethod("TrySpawnTrees").Invoke(spawner, null);
    }

    private int SpawnPositionCount
    {
        get
        {
            object positions = spawnerType.GetProperty("SpawnTreePositions").GetValue(spawner);
            return ((ICollection)positions).Count;
        }
    }

    private static Type RequireType(string assemblyQualifiedName)
    {
        Type type = Type.GetType(assemblyQualifiedName);
        Assert.That(type, Is.Not.Null, $"Required runtime type was not found: {assemblyQualifiedName}");
        return type;
    }

    private static void SetField(Component target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Field was not found: {fieldName}");
        field.SetValue(target, value);
    }
}

public sealed class StartSettingsControllerSafetyTests
{
    [Test]
    public void ApplySettingsAllowsTerrainWithoutTerrainData()
    {
        GameObject controllerObject = new GameObject("start-settings-test");
        GameObject spawnerObject = new GameObject("settings-forest-spawner-test");
        GameObject terrainObject = new GameObject("terrain-without-data-test");

        try
        {
            Type controllerType = RequireType("StartSettingsController, Assembly-CSharp");
            Type spawnerType = RequireType("ForestSpawner, Assembly-CSharp");
            Component controller = controllerObject.AddComponent(controllerType);
            Component spawner = spawnerObject.AddComponent(spawnerType);
            spawnerType.GetField("terrain").SetValue(spawner, terrainObject.AddComponent<Terrain>());

            FieldInfo field = controllerType.GetField(
                "forestSpawner",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(field, Is.Not.Null);
            field.SetValue(controller, spawner);

            Assert.DoesNotThrow(() => controllerType.GetMethod("ApplySettings").Invoke(controller, null));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(controllerObject);
            UnityEngine.Object.DestroyImmediate(spawnerObject);
            UnityEngine.Object.DestroyImmediate(terrainObject);
        }
    }

    private static Type RequireType(string assemblyQualifiedName)
    {
        Type type = Type.GetType(assemblyQualifiedName);
        Assert.That(type, Is.Not.Null, $"Required runtime type was not found: {assemblyQualifiedName}");
        return type;
    }
}
