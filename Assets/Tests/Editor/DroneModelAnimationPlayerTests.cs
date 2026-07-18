using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public sealed class DroneModelAnimationPlayerTests
{
    private GameObject playerObject;

    [TearDown]
    public void TearDown()
    {
        if (playerObject != null)
        {
            UnityEngine.Object.DestroyImmediate(playerObject);
        }
    }

    [Test]
    public void ExplicitControllerProvidesClipsWithoutEditorAssetDiscovery()
    {
        RuntimeAnimatorController controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
            "Assets/drone_ver2_moving 1.controller"
        );
        Assert.That(controller, Is.Not.Null);

        playerObject = new GameObject("drone-animation-player-test");
        playerObject.SetActive(false);
        Type playerType = Type.GetType("DroneModelAnimationPlayer, Assembly-CSharp");
        Assert.That(playerType, Is.Not.Null);
        Component player = playerObject.AddComponent(playerType);

        MethodInfo configure = playerType.GetMethod("Configure", BindingFlags.Instance | BindingFlags.Public);
        configure.Invoke(player, new object[] { null, controller });

        FieldInfo clipsField = playerType.GetField("clips", BindingFlags.Instance | BindingFlags.NonPublic);
        var configuredClips = (List<AnimationClip>)clipsField.GetValue(player);

        Assert.That(configuredClips, Is.EquivalentTo(controller.animationClips));
        Assert.That(
            configuredClips.ConvertAll(clip => clip.name),
            Is.EquivalentTo(new[]
            {
                "Cylinder.001|Propeller1",
                "Cylinder.002|Propeller2",
                "Cylinder.003|Propeller3",
                "Cylinder.004|Propeller4",
            })
        );

        playerObject.SetActive(true);

        FieldInfo graphField = playerType.GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic);
        var graph = (PlayableGraph)graphField.GetValue(player);
        Playable rootPlayable = graph.GetRootPlayable(0);

        Assert.That(rootPlayable.GetPlayableType(), Is.EqualTo(typeof(AnimationLayerMixerPlayable)));
        Assert.That(rootPlayable.GetInputCount(), Is.EqualTo(4));
        for (int i = 0; i < rootPlayable.GetInputCount(); i++)
        {
            Assert.That(rootPlayable.GetInputWeight(i), Is.EqualTo(1f));
        }
    }
}
