using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
#if UNITY_EDITOR
using UnityEditor;
#endif

public sealed class DroneEmbeddedAnimationPlayer : MonoBehaviour
{
    [SerializeField] private GameObject animationRoot;
    [SerializeField] private AnimationClip[] animationClips = new AnimationClip[0];
    [SerializeField] private float speed = 1f;

    private PlayableGraph graph;
    private AnimationClipPlayable[] clipPlayables = new AnimationClipPlayable[0];

    public void Configure(GameObject newAnimationRoot, IReadOnlyList<AnimationClip> clips)
    {
        animationRoot = newAnimationRoot;

        if (clips == null || clips.Count == 0)
        {
            animationClips = new AnimationClip[0];
            return;
        }

        var filtered = new List<AnimationClip>(clips.Count);
        for (int i = 0; i < clips.Count; i++)
        {
            if (clips[i] != null)
            {
                filtered.Add(clips[i]);
            }
        }

        animationClips = filtered.ToArray();
    }

    private void OnEnable()
    {
        Play();
    }

    private void Update()
    {
        if (!graph.IsValid())
        {
            return;
        }

        for (int i = 0; i < clipPlayables.Length; i++)
        {
            if (!clipPlayables[i].IsValid())
            {
                continue;
            }

            double length = animationClips[i] != null ? animationClips[i].length : 0.0;
            if (length > 0.0)
            {
                clipPlayables[i].SetTime((Time.time * speed) % length);
            }
        }
    }

    private void OnDisable()
    {
        Stop();
    }

    private void OnDestroy()
    {
        Stop();
    }

    private void Play()
    {
        Stop();

        if (animationRoot == null || animationClips == null || animationClips.Length == 0)
        {
            return;
        }

        var animator = animationRoot.GetComponent<Animator>();
        if (animator == null)
        {
            animator = animationRoot.AddComponent<Animator>();
        }

        animator.applyRootMotion = false;

        graph = PlayableGraph.Create($"{name} Drone Propeller Animation");
        graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        var mixer = AnimationMixerPlayable.Create(graph, animationClips.Length);
        clipPlayables = new AnimationClipPlayable[animationClips.Length];
        for (int i = 0; i < animationClips.Length; i++)
        {
            var playable = AnimationClipPlayable.Create(graph, animationClips[i]);
            playable.SetApplyFootIK(false);
            playable.SetSpeed(0.0);
            clipPlayables[i] = playable;

            graph.Connect(playable, 0, mixer, i);
            mixer.SetInputWeight(i, 1f);
        }

        var output = AnimationPlayableOutput.Create(graph, "Drone Propellers", animator);
        output.SetSourcePlayable(mixer);
        graph.Play();
    }

    private void Stop()
    {
        if (graph.IsValid())
        {
            graph.Destroy();
        }

        clipPlayables = new AnimationClipPlayable[0];
    }

    public static bool TryAttach(GameObject drone, GameObject animationRoot, GameObject clipSourcePrefab)
    {
        if (drone == null || animationRoot == null)
        {
            return false;
        }

        var clips = LoadPropellerClips(clipSourcePrefab);
        if (clips.Count == 0)
        {
            return false;
        }

        var player = drone.GetComponent<DroneEmbeddedAnimationPlayer>();
        if (player == null)
        {
            player = drone.AddComponent<DroneEmbeddedAnimationPlayer>();
        }

        player.Configure(animationRoot, clips);
        player.Play();
        return true;
    }

    private static List<AnimationClip> LoadPropellerClips(GameObject clipSourcePrefab)
    {
        var clips = new List<AnimationClip>();
#if UNITY_EDITOR
        string path = AssetDatabase.GetAssetPath(clipSourcePrefab);
        if (string.IsNullOrEmpty(path))
        {
            return clips;
        }

        foreach (Object asset in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
        {
            if (asset is AnimationClip clip && IsPropellerClip(clip))
            {
                clips.Add(clip);
            }
        }

        clips.Sort((left, right) => string.CompareOrdinal(left.name, right.name));
#endif
        return clips;
    }

    private static bool IsPropellerClip(AnimationClip clip)
    {
        if (clip == null || string.IsNullOrEmpty(clip.name))
        {
            return false;
        }

        string clipName = clip.name.ToLowerInvariant();
        if (clipName.Contains("cube"))
        {
            return false;
        }

        return clipName.Contains("propeller") || clipName.Contains("rotor") || clipName.Contains("blade");
    }
}
