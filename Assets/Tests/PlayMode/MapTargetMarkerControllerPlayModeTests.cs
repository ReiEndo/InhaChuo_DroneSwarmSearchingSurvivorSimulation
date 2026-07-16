using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class MapTargetMarkerControllerPlayModeTests
{
    [UnityTest]
    public IEnumerator RemovingComponentDestroysOwnedMarkersButPreservesAuthoredChildren()
    {
        GameObject controllerObject = new GameObject("map-marker-controller-removal-test");
        GameObject authoredChild = new GameObject("authored-child");
        authoredChild.transform.SetParent(controllerObject.transform);

        Type controllerType = GetControllerType();
        Component controller = controllerObject.AddComponent(controllerType);
        InvokeStart(controllerType, controller);

        GameObject explorerMarker = CreateOwnedMarker(controllerType, controller, "owned-explorer");
        GameObject droneMarker = CreateOwnedMarker(controllerType, controller, "owned-drone");
        GetField(controllerType, "explorerMarker").SetValue(controller, explorerMarker);

        IList droneMarkers = (IList)GetField(controllerType, "droneMarkers").GetValue(controller);
        droneMarkers.Add(droneMarker);
        droneMarkers.Add(authoredChild);

        Material explorerMaterial = (Material)GetField(controllerType, "explorerMaterial").GetValue(controller);
        Material droneMaterial = (Material)GetField(controllerType, "droneMaterial").GetValue(controller);

        UnityEngine.Object.Destroy(controller);
        yield return null;

        Assert.That(controller == null, Is.True);
        Assert.That(explorerMarker == null, Is.True);
        Assert.That(droneMarker == null, Is.True);
        Assert.That(explorerMaterial == null, Is.True);
        Assert.That(droneMaterial == null, Is.True);
        Assert.That(authoredChild != null, Is.True);
        Assert.That(authoredChild.transform.parent, Is.SameAs(controllerObject.transform));

        UnityEngine.Object.Destroy(controllerObject);
        yield return null;
    }

    [UnityTest]
    public IEnumerator DestroyingHostDestroysOwnedMarkersWithoutDoubleDestroy()
    {
        GameObject controllerObject = new GameObject("map-marker-controller-host-destroy-test");
        Type controllerType = GetControllerType();
        Component controller = controllerObject.AddComponent(controllerType);
        InvokeStart(controllerType, controller);

        GameObject explorerMarker = CreateOwnedMarker(controllerType, controller, "owned-explorer");
        GameObject droneMarker = CreateOwnedMarker(controllerType, controller, "owned-drone");
        GetField(controllerType, "explorerMarker").SetValue(controller, explorerMarker);
        ((IList)GetField(controllerType, "droneMarkers").GetValue(controller)).Add(droneMarker);

        UnityEngine.Object.Destroy(controllerObject);
        yield return null;

        Assert.That(controllerObject == null, Is.True);
        Assert.That(explorerMarker == null, Is.True);
        Assert.That(droneMarker == null, Is.True);
    }

    private static Type GetControllerType()
    {
        Type controllerType = Type.GetType("MapTargetMarkerController, Assembly-CSharp");
        Assert.That(controllerType, Is.Not.Null);
        return controllerType;
    }

    private static void InvokeStart(Type controllerType, Component controller)
    {
        MethodInfo start = controllerType.GetMethod("MapCameraMarker_Start");
        Assert.That(start, Is.Not.Null);
        start.Invoke(controller, null);
    }

    private static GameObject CreateOwnedMarker(
        Type controllerType,
        Component controller,
        string markerName
    )
    {
        MethodInfo createMarker = controllerType.GetMethod(
            "CreateMarker",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.That(createMarker, Is.Not.Null);
        return (GameObject)createMarker.Invoke(controller, new object[] { markerName, null, 1f });
    }

    private static FieldInfo GetField(Type type, string fieldName)
    {
        FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Field was not found: {fieldName}");
        return field;
    }
}
