using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class ExplorerMissionResetTests
{
    private GameObject explorerObject;
    private Component explorer;
    private Type explorerType;
    private GameObject terrainObject;
    private GameObject forestObject;
    private TerrainData terrainData;

    [SetUp]
    public void SetUp()
    {
        explorerType = RequireType("Explorer, Assembly-CSharp");
        explorerObject = new GameObject("explorer-mission-reset-test");
        explorer = explorerObject.AddComponent(explorerType);
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(forestObject);
        UnityEngine.Object.DestroyImmediate(terrainObject);
        UnityEngine.Object.DestroyImmediate(terrainData);
        UnityEngine.Object.DestroyImmediate(explorerObject);
    }

    [Test]
    public void MissionResetRestoresConfiguredStaminaAndRecoveryRate()
    {
        explorerType.GetMethod("ConfigureMissionStamina").Invoke(
            explorer,
            new object[] { 72f, 9f }
        );
        explorerType.GetField("stamina").SetValue(explorer, 3f);
        explorerType.GetField("staminaRecoveryPerSecond").SetValue(explorer, 1f);

        explorerType.GetMethod(
            "ResetMissionState",
            BindingFlags.Instance | BindingFlags.NonPublic
        ).Invoke(explorer, null);

        Assert.That(explorerType.GetField("stamina").GetValue(explorer), Is.EqualTo(72f));
        Assert.That(
            explorerType.GetField("staminaRecoveryPerSecond").GetValue(explorer),
            Is.EqualTo(9f)
        );
        Assert.That(
            explorerType.GetProperty("MissionStartingStamina").GetValue(explorer),
            Is.EqualTo(72f)
        );
    }

    [Test]
    public void SpawnPreparationRelaxesExtraTreeClearanceOnSmallDenseMaps()
    {
        terrainData = new TerrainData
        {
            heightmapResolution = 33,
            size = new Vector3(30f, 5f, 30f)
        };
        terrainObject = Terrain.CreateTerrainGameObject(terrainData);
        Terrain terrain = terrainObject.GetComponent<Terrain>();

        Type forestType = RequireType("ForestSpawner, Assembly-CSharp");
        forestObject = new GameObject("dense-forest-spawn-test");
        Component forest = forestObject.AddComponent(forestType);
        IList positions = (IList)forestType.GetField(
            "spawnedTreePositions",
            BindingFlags.Instance | BindingFlags.NonPublic
        ).GetValue(forest);
        positions.Add(new Vector2(15f, 15f));

        explorerType.GetField("terrainMargin").SetValue(explorer, 2f);
        explorerType.GetField("initialSpawnMaxAttempts").SetValue(explorer, 1);
        explorerType.GetField("initialTreeDistance").SetValue(explorer, 100f);
        explorerType.GetField("obstacleMask").SetValue(explorer, (LayerMask)0);

        object[] arguments = { terrain, forest, Vector3.zero };
        bool prepared = (bool)explorerType.GetMethod("TryPrepareExplorerSpawn").Invoke(
            explorer,
            arguments
        );

        Assert.That(prepared, Is.True);
        Vector3 spawnPosition = (Vector3)arguments[2];
        Assert.That(spawnPosition.x, Is.InRange(2f, 28f));
        Assert.That(spawnPosition.z, Is.InRange(2f, 28f));
    }

    private static Type RequireType(string assemblyQualifiedName)
    {
        Type type = Type.GetType(assemblyQualifiedName);
        Assert.That(type, Is.Not.Null, $"Required runtime type was not found: {assemblyQualifiedName}");
        return type;
    }
}
