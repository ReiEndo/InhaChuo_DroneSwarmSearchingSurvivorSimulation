using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class DroneGridSensorBoundsTests
{
    private Type sensorType;
    private GameObject sensorObject;
    private Component sensor;

    [SetUp]
    public void SetUp()
    {
        sensorType = RequireType("DroneGridSensor, Assembly-CSharp");
        sensorObject = new GameObject("drone-grid-sensor-bounds-test");
        sensor = sensorObject.AddComponent(sensorType);
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(sensorObject);
    }

    [Test]
    public void SensorRadiusClampsExtremePublicAssignmentsToDesignRange()
    {
        PropertyInfo property = sensorType.GetProperty("SensorRadius");
        int maximum = (int)sensorType.GetField("MaximumSensorRadius").GetRawConstantValue();

        property.SetValue(sensor, int.MaxValue);
        Assert.That(property.GetValue(sensor), Is.EqualTo(maximum));
        Assert.That(maximum, Is.EqualTo(8), "Must match the experiment/UI-supported radius limit.");

        property.SetValue(sensor, int.MinValue);
        Assert.That(property.GetValue(sensor), Is.Zero);
    }

    [TestCase(int.MinValue, 8, 256, 0, 0)]
    [TestCase(int.MaxValue, 8, 256, 256, 256)]
    [TestCase(10, 8, 20, 2, 19)]
    public void CandidateRangeIsClampedBeforeIteration(
        int center,
        int radius,
        int dimension,
        int expectedMinimum,
        int expectedMaximumExclusive)
    {
        MethodInfo method = sensorType.GetMethod(
            "GetCandidateRange",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        object[] arguments = { center, radius, dimension, 0, 0 };

        method.Invoke(null, arguments);

        Assert.That(arguments[3], Is.EqualTo(expectedMinimum));
        Assert.That(arguments[4], Is.EqualTo(expectedMaximumExclusive));
    }

    [Test]
    public void SquaredDistanceMathHandlesExtremeValuesWithoutOverflow()
    {
        Assert.That(IsWithinRadius(8L, 0L, 0L, 8), Is.True);
        Assert.That(IsWithinRadius(8L, 1L, 0L, 8), Is.False);
        Assert.That(IsWithinRadius(long.MaxValue, 0L, 0L, 8), Is.False);

        Assert.That(IsWithinRadius(int.MaxValue, 0L, 0L, int.MaxValue), Is.True);
        Assert.That(IsWithinRadius(int.MaxValue, 1L, 0L, int.MaxValue), Is.False);
    }

    private bool IsWithinRadius(long dx, long dy, long dz, int radius)
    {
        MethodInfo method = sensorType.GetMethod(
            "IsWithinRadius",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        return (bool)method.Invoke(null, new object[] { dx, dy, dz, radius });
    }

    private static Type RequireType(string assemblyQualifiedName)
    {
        Type type = Type.GetType(assemblyQualifiedName);
        Assert.That(type, Is.Not.Null, $"Could not load {assemblyQualifiedName}.");
        return type;
    }
}

public sealed class DroneAltitudeTerrainBindingTests
{
    private readonly System.Collections.Generic.List<GameObject> spawnedObjects = new();
    private readonly System.Collections.Generic.List<TerrainData> terrainDataObjects = new();

    [TearDown]
    public void TearDown()
    {
        for (int i = spawnedObjects.Count - 1; i >= 0; i--)
        {
            if (spawnedObjects[i] != null)
            {
                UnityEngine.Object.DestroyImmediate(spawnedObjects[i]);
            }
        }

        foreach (TerrainData terrainData in terrainDataObjects)
        {
            if (terrainData != null)
            {
                UnityEngine.Object.DestroyImmediate(terrainData);
            }
        }

        spawnedObjects.Clear();
        terrainDataObjects.Clear();
    }

    [Test]
    public void SwarmDronesUseTheWorldsSelectedTerrainForAltitude()
    {
        Terrain firstTerrain = CreateTerrain("altitude-binding-terrain-a");
        Terrain secondTerrain = CreateTerrain("altitude-binding-terrain-b");
        Terrain activeTerrain = Terrain.activeTerrain;
        Assert.That(activeTerrain, Is.Not.Null, "The test needs an active fallback terrain.");

        Terrain selectedTerrain = activeTerrain == firstTerrain ? secondTerrain : firstTerrain;
        Assert.That(selectedTerrain, Is.Not.SameAs(activeTerrain));

        Type worldType = RequireType("DroneDemoGridWorld, Assembly-CSharp");
        var worldObject = new GameObject("altitude-binding-world");
        spawnedObjects.Add(worldObject);
        Component world = worldObject.AddComponent(worldType);
        worldType.GetMethod("Configure").Invoke(
            world,
            new object[] { Vector3.zero, 1f, 4, 1, 4, (LayerMask)0, (LayerMask)0 }
        );
        worldType.GetMethod("SetTerrainTreeAvoidance").Invoke(
            world,
            new object[] { selectedTerrain, 0.75f }
        );

        Type spawnerType = RequireType("DroneDemoSwarmSpawner, Assembly-CSharp");
        var registerSpawned = new Action<GameObject>(instance => spawnedObjects.Add(instance));
        object spawner = Activator.CreateInstance(spawnerType, registerSpawned);
        MethodInfo buildDrones = spawnerType.GetMethod(
            "BuildDrones",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        ParameterInfo[] parameters = buildDrones.GetParameters();
        object planner = Enum.Parse(parameters[7].ParameterType, "AStar");
        object explorers = Activator.CreateInstance(parameters[10].ParameterType);
        object communicationNodes = Activator.CreateInstance(parameters[11].ParameterType);
        var dronePrefab = new GameObject("altitude-binding-drone-prefab");
        spawnedObjects.Add(dronePrefab);

        buildDrones.Invoke(
            spawner,
            new object[]
            {
                world,
                4,
                4,
                1,
                dronePrefab,
                1,
                10f,
                planner,
                0,
                null,
                explorers,
                communicationNodes
            }
        );

        GameObject drone = spawnedObjects.Find(instance => instance != null && instance.name == "Drone 01");
        Assert.That(drone, Is.Not.Null);

        Type keeperType = RequireType("DroneAltitudeKeeper, Assembly-CSharp");
        Component keeper = drone.GetComponent(keeperType);
        FieldInfo terrainField = keeperType.GetField("terrain", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(terrainField.GetValue(keeper), Is.SameAs(selectedTerrain));
    }

    private Terrain CreateTerrain(string name)
    {
        var terrainData = new TerrainData
        {
            heightmapResolution = 33,
            size = new Vector3(4f, 10f, 4f)
        };
        terrainDataObjects.Add(terrainData);

        GameObject terrainObject = Terrain.CreateTerrainGameObject(terrainData);
        terrainObject.name = name;
        spawnedObjects.Add(terrainObject);
        return terrainObject.GetComponent<Terrain>();
    }

    private static Type RequireType(string assemblyQualifiedName)
    {
        Type type = Type.GetType(assemblyQualifiedName);
        Assert.That(type, Is.Not.Null, $"Could not load {assemblyQualifiedName}.");
        return type;
    }
}
