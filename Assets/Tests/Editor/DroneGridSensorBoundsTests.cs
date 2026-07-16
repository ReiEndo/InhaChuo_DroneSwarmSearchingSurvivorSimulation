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
