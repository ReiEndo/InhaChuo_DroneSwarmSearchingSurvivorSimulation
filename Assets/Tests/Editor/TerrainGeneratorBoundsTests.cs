using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class TerrainGeneratorBoundsTests
{
    private Type generatorType;
    private GameObject generatorObject;
    private Component generator;
    private TerrainData terrainData;

    [SetUp]
    public void SetUp()
    {
        generatorType = RequireType("TerrainGenerator, Assembly-CSharp");
        generatorObject = new GameObject("terrain-generator-bounds-test");
        generator = generatorObject.AddComponent(generatorType);
        terrainData = new TerrainData
        {
            heightmapResolution = 33,
            size = new Vector3(20f, 10f, 30f)
        };
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(generatorObject);
        UnityEngine.Object.DestroyImmediate(terrainData);
    }

    [Test]
    public void MaximumWorldDimensionUsesBoundedSquareHeightmapAndExactWorldSize()
    {
        int maximum = GetConstant("MaxWorldDimension");
        int maximumResolution = GetConstant("MaxHeightmapResolution");
        Assert.That(maximum, Is.EqualTo(512), "Keep synchronous terrain generation explicitly bounded.");
        Assert.That(maximumResolution, Is.EqualTo(513));

        SetField("widthx", maximum);
        SetField("widthz", 300);

        Assert.That(TryGenerate(), Is.True);
        Assert.That(terrainData.heightmapResolution, Is.EqualTo(maximumResolution));
        Assert.That(terrainData.size, Is.EqualTo(new Vector3(maximum, 50f, 300f)));

        float[,] heights = terrainData.GetHeights(0, 0, maximumResolution, maximumResolution);
        Assert.That(heights.GetLength(0), Is.EqualTo(maximumResolution));
        Assert.That(heights.GetLength(1), Is.EqualTo(maximumResolution));
    }

    [TestCase(4, 4, 33)]
    [TestCase(32, 17, 33)]
    [TestCase(33, 17, 65)]
    [TestCase(255, 300, 513)]
    [TestCase(512, 512, 513)]
    public void ResolutionIsPowerOfTwoPlusOneWithoutChangingWorldDimensions(
        int width,
        int depth,
        int expectedResolution)
    {
        MethodInfo method = generatorType.GetMethod("GetHeightmapResolution", BindingFlags.Public | BindingFlags.Static);
        int resolution = (int)method.Invoke(null, new object[] { width, depth });

        Assert.That(resolution, Is.EqualTo(expectedResolution));
    }

    [Test]
    public void ReplayGenerationPreservesTerrainLayersAndAlphamaps()
    {
        var sourceData = new TerrainData
        {
            heightmapResolution = 33,
            size = new Vector3(20f, 10f, 30f),
            alphamapResolution = 16,
            baseMapResolution = 16
        };
        var firstLayer = new TerrainLayer();
        var secondLayer = new TerrainLayer();

        try
        {
            sourceData.terrainLayers = new[] { firstLayer, secondLayer };
            var sourceAlphamaps = new float[16, 16, 2];
            for (int z = 0; z < 16; z++)
            {
                for (int x = 0; x < 16; x++)
                {
                    sourceAlphamaps[z, x, 0] = 0.25f;
                    sourceAlphamaps[z, x, 1] = 0.75f;
                }
            }
            sourceData.SetAlphamaps(0, 0, sourceAlphamaps);

            MethodInfo method = generatorType.GetMethod(
                "TryGenerateReplayTerrainData",
                BindingFlags.Instance | BindingFlags.Public
            );
            Assert.That(method, Is.Not.Null);
            Assert.That(
                (bool)method.Invoke(generator, new object[] { terrainData, sourceData }),
                Is.True
            );

            CollectionAssert.AreEqual(sourceData.terrainLayers, terrainData.terrainLayers);
            Assert.That(terrainData.alphamapResolution, Is.EqualTo(sourceData.alphamapResolution));
            Assert.That(terrainData.baseMapResolution, Is.EqualTo(sourceData.baseMapResolution));

            float[,,] copiedAlphamaps = terrainData.GetAlphamaps(0, 0, 16, 16);
            Assert.That(copiedAlphamaps[8, 8, 0], Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(copiedAlphamaps[8, 8, 1], Is.EqualTo(0.75f).Within(0.0001f));
        }
        finally
        {
            terrainData.terrainLayers = Array.Empty<TerrainLayer>();
            UnityEngine.Object.DestroyImmediate(sourceData);
            UnityEngine.Object.DestroyImmediate(firstLayer);
            UnityEngine.Object.DestroyImmediate(secondLayer);
        }
    }

    [TestCase(513, 128)]
    [TestCase(128, 513)]
    public void OversizedDirectInputFailsBeforeMutatingTerrainData(int width, int depth)
    {
        Vector3 originalSize = terrainData.size;
        int originalResolution = terrainData.heightmapResolution;
        SetField("widthx", width);
        SetField("widthz", depth);
        LogAssert.Expect(LogType.Error, new Regex("World dimensions must be between 4 and 512"));

        Assert.That(TryGenerate(), Is.False);
        Assert.That(terrainData.heightmapResolution, Is.EqualTo(originalResolution));
        Assert.That(terrainData.size, Is.EqualTo(originalSize));
        Assert.That(GetField("widthx"), Is.EqualTo(width));
        Assert.That(GetField("widthz"), Is.EqualTo(depth));
    }

    [Test]
    public void RepeatedRuntimeTerrainReplacementDestroysOnlyDisplacedOwnedData()
    {
        GameObject terrainObject = Terrain.CreateTerrainGameObject(terrainData);
        Terrain terrain = terrainObject.GetComponent<Terrain>();
        TerrainCollider coLocatedCollider = terrainObject.GetComponent<TerrainCollider>();
        GameObject assignedColliderObject = new GameObject("child-terrain-collider");
        assignedColliderObject.transform.SetParent(terrainObject.transform, false);
        TerrainCollider assignedCollider = assignedColliderObject.AddComponent<TerrainCollider>();
        assignedCollider.terrainData = terrainData;
        SetField("terrain", terrain);
        TerrainData firstRuntimeData = new TerrainData();
        TerrainData secondRuntimeData = new TerrainData();
        TerrainData thirdRuntimeData = new TerrainData();

        try
        {
            Assert.That(InvokeAdopt(firstRuntimeData), Is.True);
            Assert.That(terrain.terrainData, Is.SameAs(firstRuntimeData));
            Assert.That(coLocatedCollider.terrainData, Is.SameAs(firstRuntimeData));
            Assert.That(assignedCollider.terrainData, Is.SameAs(firstRuntimeData));
            Assert.That(terrainData, Is.Not.Null, "The borrowed original must remain alive.");

            Assert.That(InvokeAdopt(secondRuntimeData), Is.True);
            Assert.That(firstRuntimeData == null, Is.True, "The first displaced runtime allocation leaked.");
            Assert.That(terrain.terrainData, Is.SameAs(secondRuntimeData));
            Assert.That(coLocatedCollider.terrainData, Is.SameAs(secondRuntimeData));
            Assert.That(assignedCollider.terrainData, Is.SameAs(secondRuntimeData));

            Assert.That(InvokeAdopt(thirdRuntimeData), Is.True);
            Assert.That(secondRuntimeData == null, Is.True, "The second displaced runtime allocation leaked.");
            Assert.That(terrain.terrainData, Is.SameAs(thirdRuntimeData));
            Assert.That(coLocatedCollider.terrainData, Is.SameAs(thirdRuntimeData));
            Assert.That(assignedCollider.terrainData, Is.SameAs(thirdRuntimeData));
            Assert.That(terrainData, Is.Not.Null, "A replay must never destroy the project/borrowed data.");

            generatorType.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(generator, null);
            UnityEngine.Object.DestroyImmediate(generatorObject);
            generatorObject = null;
            Assert.That(terrain.terrainData, Is.SameAs(terrainData), "A surviving Terrain must be restored to borrowed data.");
            Assert.That(coLocatedCollider.terrainData, Is.SameAs(terrainData));
            Assert.That(assignedCollider.terrainData, Is.SameAs(terrainData));
            Assert.That(thirdRuntimeData == null, Is.True,
                "Teardown may destroy collision data only after every collider has released it.");
            Assert.That(terrainData, Is.Not.Null);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(assignedColliderObject);
            UnityEngine.Object.DestroyImmediate(terrainObject);
            UnityEngine.Object.DestroyImmediate(firstRuntimeData);
            UnityEngine.Object.DestroyImmediate(secondRuntimeData);
            UnityEngine.Object.DestroyImmediate(thirdRuntimeData);
        }
    }

    [Test]
    public void RuntimeTerrainReplacementDoesNotChangeAnotherTerrainSharingOriginalData()
    {
        GameObject targetObject = Terrain.CreateTerrainGameObject(terrainData);
        Terrain targetTerrain = targetObject.GetComponent<Terrain>();
        TerrainCollider targetCollider = targetObject.GetComponent<TerrainCollider>();
        GameObject unrelatedObject = Terrain.CreateTerrainGameObject(terrainData);
        Terrain unrelatedTerrain = unrelatedObject.GetComponent<Terrain>();
        TerrainCollider unrelatedCollider = unrelatedObject.GetComponent<TerrainCollider>();
        // A nested Terrain proves that descendant traversal does not claim a collider
        // structurally owned by another renderer.
        unrelatedObject.transform.SetParent(targetObject.transform, false);
        SetField("terrain", targetTerrain);
        TerrainData runtimeData = new TerrainData();

        try
        {
            Assert.That(InvokeAdopt(runtimeData), Is.True);

            Assert.That(targetTerrain.terrainData, Is.SameAs(runtimeData));
            Assert.That(targetCollider.terrainData, Is.SameAs(runtimeData));
            Assert.That(unrelatedTerrain.terrainData, Is.SameAs(terrainData));
            Assert.That(unrelatedCollider.terrainData, Is.SameAs(terrainData),
                "A collider owned by a different Terrain must remain aligned with its renderer.");

            generatorType.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(generator, null);
            Assert.That(targetTerrain.terrainData, Is.SameAs(terrainData));
            Assert.That(targetCollider.terrainData, Is.SameAs(terrainData));
            Assert.That(unrelatedTerrain.terrainData, Is.SameAs(terrainData));
            Assert.That(unrelatedCollider.terrainData, Is.SameAs(terrainData));
            Assert.That(runtimeData == null, Is.True);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(unrelatedObject);
            UnityEngine.Object.DestroyImmediate(targetObject);
            UnityEngine.Object.DestroyImmediate(runtimeData);
        }
    }

    [Test]
    public void DisplacedRuntimeDataRemainsAliveWhileAnotherTerrainPairReferencesIt()
    {
        GameObject targetObject = Terrain.CreateTerrainGameObject(terrainData);
        Terrain targetTerrain = targetObject.GetComponent<Terrain>();
        GameObject unrelatedObject = Terrain.CreateTerrainGameObject(terrainData);
        Terrain unrelatedTerrain = unrelatedObject.GetComponent<Terrain>();
        TerrainCollider unrelatedCollider = unrelatedObject.GetComponent<TerrainCollider>();
        SetField("terrain", targetTerrain);
        TerrainData sharedRuntimeData = new TerrainData();
        TerrainData replacementData = new TerrainData();

        try
        {
            Assert.That(InvokeAdopt(sharedRuntimeData), Is.True);
            unrelatedTerrain.terrainData = sharedRuntimeData;
            unrelatedCollider.terrainData = sharedRuntimeData;

            Assert.That(InvokeAdopt(replacementData), Is.True);

            Assert.That(targetTerrain.terrainData, Is.SameAs(replacementData));
            Assert.That(targetObject.GetComponent<TerrainCollider>().terrainData, Is.SameAs(replacementData));
            Assert.That(sharedRuntimeData, Is.Not.Null,
                "Lifetime scans must include unrelated Terrain renderers and colliders.");
            Assert.That(unrelatedTerrain.terrainData, Is.SameAs(sharedRuntimeData));
            Assert.That(unrelatedCollider.terrainData, Is.SameAs(sharedRuntimeData));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(generatorObject);
            generatorObject = null;
            UnityEngine.Object.DestroyImmediate(unrelatedObject);
            UnityEngine.Object.DestroyImmediate(targetObject);
            UnityEngine.Object.DestroyImmediate(sharedRuntimeData);
            UnityEngine.Object.DestroyImmediate(replacementData);
        }
    }

    [Test]
    public void RuntimeTerrainReplacementSupportsTerrainWithoutCollider()
    {
        GameObject terrainObject = Terrain.CreateTerrainGameObject(terrainData);
        Terrain terrain = terrainObject.GetComponent<Terrain>();
        UnityEngine.Object.DestroyImmediate(terrainObject.GetComponent<TerrainCollider>());
        SetField("terrain", terrain);
        TerrainData runtimeData = new TerrainData();

        try
        {
            Assert.That(InvokeAdopt(runtimeData), Is.True);
            Assert.That(terrain.terrainData, Is.SameAs(runtimeData));

            generatorType.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(generator, null);
            Assert.That(terrain.terrainData, Is.SameAs(terrainData));
            Assert.That(runtimeData == null, Is.True);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(terrainObject);
            UnityEngine.Object.DestroyImmediate(runtimeData);
        }
    }

    [Test]
    public void OwnedTerrainDataIsNotDestroyedWhileAnyColliderStillReferencesIt()
    {
        GameObject colliderObject = new GameObject("terrain-data-lifetime-collider");
        TerrainCollider terrainCollider = colliderObject.AddComponent<TerrainCollider>();
        TerrainData collisionData = new TerrainData();
        terrainCollider.terrainData = collisionData;

        try
        {
            MethodInfo destroyMethod = generatorType.GetMethod(
                "DestroyOwnedTerrainDataIfUnused",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(destroyMethod, Is.Not.Null);
            destroyMethod.Invoke(null, new object[] { collisionData });

            Assert.That(collisionData, Is.Not.Null,
                "TerrainData referenced only by a TerrainCollider is still live collision data.");
            Assert.That(terrainCollider.terrainData, Is.SameAs(collisionData));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(colliderObject);
            UnityEngine.Object.DestroyImmediate(collisionData);
        }
    }

    [Test]
    public void StartSettingsClampsSerializedTerrainWidthToGeneratorLimit()
    {
        Type controllerType = RequireType("StartSettingsController, Assembly-CSharp");
        GameObject controllerObject = new GameObject("terrain-settings-bounds-test");

        try
        {
            Component controller = controllerObject.AddComponent(controllerType);
            SetField("widthx", int.MaxValue);
            SetField("widthz", int.MaxValue);
            SetPrivateField(controller, "terrainGenerator", generator);

            controllerType.GetMethod("ApplySettings").Invoke(controller, null);

            int maximum = GetConstant("MaxWorldDimension");
            Assert.That(GetField("widthx"), Is.EqualTo(maximum));
            Assert.That(GetField("widthz"), Is.EqualTo(maximum));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(controllerObject);
        }
    }

    private bool TryGenerate()
    {
        return (bool)generatorType.GetMethod("TryGenerateTerrainData").Invoke(generator, new object[] { terrainData });
    }

    private bool InvokeAdopt(TerrainData data)
    {
        MethodInfo method = generatorType.GetMethod("TryAdoptRuntimeTerrainData", BindingFlags.Instance | BindingFlags.Public);
        Assert.That(method, Is.Not.Null);
        return (bool)method.Invoke(generator, new object[] { data });
    }

    private int GetConstant(string name)
    {
        return (int)generatorType.GetField(name).GetRawConstantValue();
    }

    private object GetField(string name)
    {
        return generatorType.GetField(name).GetValue(generator);
    }

    private void SetField(string name, object value)
    {
        generatorType.GetField(name).SetValue(generator, value);
    }

    private static void SetPrivateField(Component target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        field.SetValue(target, value);
    }

    private static Type RequireType(string assemblyQualifiedName)
    {
        Type type = Type.GetType(assemblyQualifiedName);
        Assert.That(type, Is.Not.Null, $"Required runtime type was not found: {assemblyQualifiedName}");
        return type;
    }
}
