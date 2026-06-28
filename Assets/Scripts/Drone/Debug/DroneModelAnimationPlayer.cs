using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
#if UNITY_EDITOR
using UnityEditor;
#endif

public sealed class DroneModelAnimationPlayer : MonoBehaviour
{
    private readonly List<AnimationClip> clips = new();
    private readonly List<AnimationClipPlayable> playables = new();
    private PlayableGraph graph;
    private Animator animator;

    public void Configure(GameObject sourcePrefab)
    {
        clips.Clear();
        CollectPlayableClips(sourcePrefab, clips);
        if (isActiveAndEnabled)
        {
            Play();
        }
    }

    private void OnEnable()
    {
        Play();
    }

    private void OnDisable()
    {
        StopGraph();
    }

    private void OnDestroy()
    {
        StopGraph();
    }

    private void Play()
    {
        if (clips.Count == 0)
        {
            return;
        }

        animator = GetComponentInChildren<Animator>();
        if (animator == null)
        {
            animator = gameObject.AddComponent<Animator>();
        }

        StopGraph();
        playables.Clear();

        graph = PlayableGraph.Create($"{name} Drone Animation");
        graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        var mixer = AnimationMixerPlayable.Create(graph, clips.Count);
        for (int i = 0; i < clips.Count; i++)
        {
            AnimationClip clip = clips[i];

            var clipPlayable = AnimationClipPlayable.Create(graph, clip);
            clipPlayable.SetApplyFootIK(false);
            clipPlayable.SetDuration(clip.length);
            clipPlayable.SetTime(UnityEngine.Random.Range(0f, Mathf.Max(0.01f, clip.length)));
            playables.Add(clipPlayable);

            graph.Connect(clipPlayable, 0, mixer, i);
            mixer.SetInputWeight(i, 1f);
        }

        var output = AnimationPlayableOutput.Create(graph, "Drone Animation", animator);
        output.SetSourcePlayable(mixer);
        graph.Play();
    }

    private void Update()
    {
        if (!graph.IsValid())
        {
            return;
        }

        for (int i = 0; i < playables.Count && i < clips.Count; i++)
        {
            AnimationClip clip = clips[i];
            if (clip == null || clip.length <= 0f)
            {
                continue;
            }

            AnimationClipPlayable playable = playables[i];
            if (!playable.IsValid())
            {
                continue;
            }

            double time = playable.GetTime();
            if (time >= clip.length)
            {
                playable.SetTime(time % clip.length);
                playable.SetDone(false);
            }
        }
    }

    private void StopGraph()
    {
        if (graph.IsValid())
        {
            graph.Destroy();
        }
        playables.Clear();
    }

    private static void CollectPlayableClips(GameObject sourcePrefab, List<AnimationClip> results)
    {
        var seen = new HashSet<AnimationClip>();

#if UNITY_EDITOR
        if (sourcePrefab != null)
        {
            string assetPath = AssetDatabase.GetAssetPath(sourcePrefab);
            if (!string.IsNullOrEmpty(assetPath))
            {
                foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    if (asset is AnimationClip clip)
                    {
                        AddClipIfAllowed(clip, results, seen);
                    }
                }
            }
        }
#endif

        if (sourcePrefab != null)
        {
            foreach (Animator sourceAnimator in sourcePrefab.GetComponentsInChildren<Animator>(true))
            {
                RuntimeAnimatorController controller = sourceAnimator.runtimeAnimatorController;
                if (controller == null)
                {
                    continue;
                }

                foreach (AnimationClip clip in controller.animationClips)
                {
                    AddClipIfAllowed(clip, results, seen);
                }
            }
        }
    }

    private static void AddClipIfAllowed(AnimationClip clip, List<AnimationClip> results, HashSet<AnimationClip> seen)
    {
        if (clip == null || seen.Contains(clip))
        {
            return;
        }

        if (clip.name.IndexOf("cube", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return;
        }

        seen.Add(clip);
        results.Add(clip);
    }
}
