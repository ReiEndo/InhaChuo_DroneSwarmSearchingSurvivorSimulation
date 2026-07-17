using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class MapTargetMarkerControllerLifecycleTests
{
    [Test]
    public void RepeatedStartReusesMaterialsAndRebindsPooledMarkers()
    {
        GameObject controllerObject = new GameObject("map-marker-controller-test");

        try
        {
            Type controllerType = Type.GetType("MapTargetMarkerController, Assembly-CSharp");
            Assert.That(controllerType, Is.Not.Null);

            Component controller = controllerObject.AddComponent(controllerType);
            MethodInfo start = controllerType.GetMethod("MapCameraMarker_Start");
            FieldInfo explorerMaterialField = GetField(controllerType, "explorerMaterial");
            FieldInfo droneMaterialField = GetField(controllerType, "droneMaterial");
            FieldInfo explorerMarkerField = GetField(controllerType, "explorerMarker");

            start.Invoke(controller, null);
            Material explorerMaterial = (Material)explorerMaterialField.GetValue(controller);
            Material droneMaterial = (Material)droneMaterialField.GetValue(controller);

            GameObject pooledMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pooledMarker.transform.SetParent(controllerObject.transform);
            explorerMarkerField.SetValue(controller, pooledMarker);
            pooledMarker.GetComponent<Renderer>().sharedMaterial = droneMaterial;

            int materialCount = Resources.FindObjectsOfTypeAll<Material>().Length;
            start.Invoke(controller, null);

            Assert.That(explorerMaterialField.GetValue(controller), Is.SameAs(explorerMaterial));
            Assert.That(droneMaterialField.GetValue(controller), Is.SameAs(droneMaterial));
            Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(materialCount));
            Assert.That(pooledMarker.GetComponent<Renderer>().sharedMaterial, Is.SameAs(explorerMaterial));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(controllerObject);
        }
    }

    private static FieldInfo GetField(Type type, string fieldName)
    {
        FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Field was not found: {fieldName}");
        return field;
    }
}
