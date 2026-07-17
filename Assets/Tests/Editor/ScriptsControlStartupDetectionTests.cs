using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class ScriptsControlStartupDetectionTests
{
    private readonly List<GameObject> createdObjects = new();
    private Component scriptsControl;

    [SetUp]
    public void SetUp()
    {
        scriptsControl = AddComponent("scripts-control-test", "ScriptsControl");
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = createdObjects.Count - 1; i >= 0; i--)
        {
            UnityEngine.Object.DestroyImmediate(createdObjects[i]);
        }

        createdObjects.Clear();
    }

    [Test]
    public void DisabledAutoStartRunnerDoesNotOwnStartup()
    {
        Component runner = AddAutoStartRunner("disabled-fixed-runner", "DroneMissionBatchRunner");
        ((Behaviour)runner).enabled = false;

        Assert.That(BatchAutoStartEnabled, Is.False);
    }

    [Test]
    public void InactiveAutoStartRunnerDoesNotOwnStartup()
    {
        Component runner = AddAutoStartRunner("inactive-timed-runner", "DroneMissionTimedBatchRunner");
        runner.gameObject.SetActive(false);

        Assert.That(BatchAutoStartEnabled, Is.False);
    }

    [Test]
    public void ActiveEnabledAutoStartRunnerOwnsStartup()
    {
        AddAutoStartRunner("enabled-fixed-runner", "DroneMissionBatchRunner");

        Assert.That(BatchAutoStartEnabled, Is.True);
    }

    [Test]
    public void MixedRunnersUseAnyActiveEnabledAutoStartRunner()
    {
        Component disabledFixed = AddAutoStartRunner("mixed-disabled-fixed", "DroneMissionBatchRunner");
        ((Behaviour)disabledFixed).enabled = false;

        Component inactiveTimed = AddAutoStartRunner("mixed-inactive-timed", "DroneMissionTimedBatchRunner");
        inactiveTimed.gameObject.SetActive(false);

        Component optedOutTimed = AddComponent("mixed-opted-out-timed", "DroneMissionTimedBatchRunner");
        Component eligibleFixed = AddAutoStartRunner("mixed-eligible-fixed", "DroneMissionBatchRunner");

        Assert.That(BatchAutoStartEnabled, Is.True);

        ((Behaviour)eligibleFixed).enabled = false;
        Assert.That(BatchAutoStartEnabled, Is.False,
            "Disabled, inactive, and opted-out runners must not own startup even when several runners exist.");
        Assert.That(((Behaviour)optedOutTimed).isActiveAndEnabled, Is.True);
    }

    private bool BatchAutoStartEnabled
    {
        get
        {
            PropertyInfo property = scriptsControl.GetType().GetProperty(
                "BatchAutoStartEnabled", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null);
            return (bool)property.GetValue(scriptsControl);
        }
    }

    private Component AddAutoStartRunner(string objectName, string typeName)
    {
        Component runner = AddComponent(objectName, typeName);
        FieldInfo field = runner.GetType().GetField(
            "autoStartOnPlay", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        field.SetValue(runner, true);
        return runner;
    }

    private Component AddComponent(string objectName, string typeName)
    {
        Type type = Type.GetType($"{typeName}, Assembly-CSharp", throwOnError: false);
        Assert.That(type, Is.Not.Null, $"Required runtime type {typeName} was not found.");

        var gameObject = new GameObject(objectName);
        createdObjects.Add(gameObject);
        return gameObject.AddComponent(type);
    }
}
