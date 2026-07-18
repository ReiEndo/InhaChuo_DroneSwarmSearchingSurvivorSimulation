using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class DroneMissionEndReporterSmokeTests
{
    private GameObject worldObject;
    private GameObject reporterObject;
    private GameObject explorerObject;
    private GameObject gameFlowObject;

    [TearDown]
    public void TearDown()
    {
        Type reporterType = Type.GetType("DroneMissionEndReporter, Assembly-CSharp");
        reporterType?.GetMethod("ResetMissionState", BindingFlags.Public | BindingFlags.Static)
            ?.Invoke(null, null);
        UnityEngine.Object.DestroyImmediate(gameFlowObject);
        UnityEngine.Object.DestroyImmediate(explorerObject);
        UnityEngine.Object.DestroyImmediate(reporterObject);
        UnityEngine.Object.DestroyImmediate(worldObject);
    }

    [Test]
    public void TargetSmokePrefabKeepsOriginalParticleLook()
    {
        ParticleSystem smokePrefab = Resources.Load<ParticleSystem>("Smoke/TargetSmoke");
        Assert.That(smokePrefab, Is.Not.Null);

        ParticleSystem.MainModule main = smokePrefab.main;
        Assert.That(main.prewarm, Is.False);
        Assert.That(main.startColor.mode, Is.EqualTo(ParticleSystemGradientMode.Color));
        Assert.That(main.startColor.color.r, Is.EqualTo(0.84276724f).Within(0.0001f));
        Assert.That(main.startColor.color.g, Is.EqualTo(0.3137255f).Within(0.0001f));
        Assert.That(main.startColor.color.b, Is.EqualTo(0.24313726f).Within(0.0001f));

        ParticleSystemRenderer renderer = smokePrefab.GetComponent<ParticleSystemRenderer>();
        Assert.That(renderer, Is.Not.Null);
        Assert.That(renderer.sharedMaterial, Is.Not.Null);
        Assert.That(renderer.sharedMaterial.name, Is.EqualTo("Default-ParticleSystem"));
    }

    [Test]
    public void ResultSmokeCanSpawnWithoutACompletedDroneReport()
    {
        Type worldType = RequireType("DroneDemoGridWorld, Assembly-CSharp");
        worldObject = new GameObject("result-smoke-world-test");
        Component world = worldObject.AddComponent(worldType);
        worldType.GetMethod("Configure").Invoke(
            world,
            new object[] { Vector3.zero, 1f, 4, 1, 4, (LayerMask)0, (LayerMask)0 }
        );

        Type reporterType = RequireType("DroneMissionEndReporter, Assembly-CSharp");
        reporterType.GetMethod("ResetMissionState", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, null);
        reporterObject = new GameObject("result-smoke-reporter-test");
        Component reporter = reporterObject.AddComponent(reporterType);
        Component sensor = reporterObject.GetComponent(
            RequireType("DroneGridSensor, Assembly-CSharp")
        );
        sensor.GetType().GetMethod("Configure").Invoke(sensor, new object[] { world, 1 });

        explorerObject = new GameObject("result-smoke-explorer-test");
        explorerObject.transform.position = new Vector3(1f, 0f, 1f);
        explorerObject.AddComponent(RequireType("Explorer, Assembly-CSharp"));

        bool spawned = (bool)reporterType.GetMethod(
            "EnsureTargetSmokeForResult",
            BindingFlags.Public | BindingFlags.Static
        ).Invoke(null, null);

        Assert.That(spawned, Is.True);
        Assert.That(
            reporterObject.GetComponentsInChildren<ParticleSystem>(true),
            Has.Length.GreaterThan(0)
        );
    }

    [Test]
    public void FailedResultDoesNotSpawnTargetSmoke()
    {
        Type worldType = RequireType("DroneDemoGridWorld, Assembly-CSharp");
        worldObject = new GameObject("failed-result-smoke-world-test");
        Component world = worldObject.AddComponent(worldType);
        worldType.GetMethod("Configure").Invoke(
            world,
            new object[] { Vector3.zero, 1f, 4, 1, 4, (LayerMask)0, (LayerMask)0 }
        );

        Type reporterType = RequireType("DroneMissionEndReporter, Assembly-CSharp");
        reporterType.GetMethod("ResetMissionState", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, null);
        reporterObject = new GameObject("failed-result-smoke-reporter-test");
        Component reporter = reporterObject.AddComponent(reporterType);
        Component sensor = reporterObject.GetComponent(
            RequireType("DroneGridSensor, Assembly-CSharp")
        );
        sensor.GetType().GetMethod("Configure").Invoke(sensor, new object[] { world, 1 });

        explorerObject = new GameObject("failed-result-smoke-explorer-test");
        explorerObject.transform.position = new Vector3(1f, 0f, 1f);
        explorerObject.AddComponent(RequireType("Explorer, Assembly-CSharp"));

        Type gameFlowType = RequireType("GameFlowController, Assembly-CSharp");
        gameFlowObject = new GameObject("failed-result-smoke-game-flow-test");
        Component gameFlow = gameFlowObject.AddComponent(gameFlowType);
        gameFlowType.GetField("gameRunning", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(gameFlow, true);

        gameFlowType.GetMethod("ForceEndSearchFailed", BindingFlags.Instance | BindingFlags.Public)
            .Invoke(gameFlow, null);

        Assert.That(
            reporterObject.GetComponentsInChildren<ParticleSystem>(true),
            Is.Empty
        );
    }

    [Test]
    public void SpawnedTargetSmokeIsOwnedByReporterForReplayCleanup()
    {
        Type worldType = RequireType("DroneDemoGridWorld, Assembly-CSharp");
        worldObject = new GameObject("target-smoke-world-test");
        Component world = worldObject.AddComponent(worldType);
        worldType.GetMethod("Configure").Invoke(
            world,
            new object[] { Vector3.zero, 1f, 4, 1, 4, (LayerMask)0, (LayerMask)0 }
        );

        Type reporterType = RequireType("DroneMissionEndReporter, Assembly-CSharp");
        reporterType.GetMethod("ResetMissionState", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, null);
        reporterObject = new GameObject("target-smoke-reporter-test");
        Component reporter = reporterObject.AddComponent(reporterType);
        ParticleSystem smokePrefab = Resources.Load<ParticleSystem>("Smoke/TargetSmoke");
        Assert.That(smokePrefab, Is.Not.Null);
        reporterType.GetField(
            "targetSmokePrefab",
            BindingFlags.Instance | BindingFlags.NonPublic
        ).SetValue(reporter, smokePrefab);

        Type reportType = RequireType("DroneTargetReport, Assembly-CSharp");
        Type cellType = RequireType("DroneNative+DroneVec3i, Assembly-CSharp");
        object report = Activator.CreateInstance(reportType);
        reportType.GetField("Cell").SetValue(
            report,
            Activator.CreateInstance(cellType, new object[] { 1, 0, 1 })
        );
        reportType.GetField("ObservedAt").SetValue(report, 1f);
        reportType.GetField("ReporterId").SetValue(report, 1);

        reporterType.GetMethod(
            "SpawnTargetSmokeOnce",
            BindingFlags.Instance | BindingFlags.NonPublic
        ).Invoke(reporter, new object[] { report, world });

        ParticleSystem[] smokeSystems = reporterObject.GetComponentsInChildren<ParticleSystem>(true);
        Assert.That(smokeSystems, Has.Length.GreaterThan(0));
        Assert.That(smokeSystems[0].transform.parent, Is.SameAs(reporterObject.transform));
    }

    private static Type RequireType(string assemblyQualifiedName)
    {
        Type type = Type.GetType(assemblyQualifiedName);
        Assert.That(type, Is.Not.Null, $"Required runtime type was not found: {assemblyQualifiedName}");
        return type;
    }
}
