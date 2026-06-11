using System;
using UnityEngine;

public sealed class DroneSwarmAgentState : MonoBehaviour
{
    private static readonly System.Collections.Generic.List<DroneSwarmAgentState> s_ActiveAgents = new();

    [Header("Identity")]
    [SerializeField] private int droneId;

    [Header("Local Map")]
    [SerializeField] private int width = 10;
    [SerializeField] private int height = 1;
    [SerializeField] private int depth = 10;

    public int DroneId
    {
        get => droneId;
        set => droneId = value;
    }

    public DroneLocalMap LocalMap { get; private set; }

    public event Action<DroneSwarmAgentState> LocalMapReset;
    public event Action<DroneSwarmAgentState> LocalMapChanged;

    public static System.Collections.Generic.IReadOnlyList<DroneSwarmAgentState> ActiveAgents => s_ActiveAgents;

    private void Awake()
    {
        EnsureLocalMap();
    }

    private void OnEnable()
    {
        if (!s_ActiveAgents.Contains(this))
        {
            s_ActiveAgents.Add(this);
        }
    }

    private void OnDisable()
    {
        s_ActiveAgents.Remove(this);
    }

    private void OnValidate()
    {
        width = Mathf.Max(1, width);
        height = Mathf.Max(1, height);
        depth = Mathf.Max(1, depth);
    }

    public void ConfigureMap(int newWidth, int newHeight, int newDepth)
    {
        width = Mathf.Max(1, newWidth);
        height = Mathf.Max(1, newHeight);
        depth = Mathf.Max(1, newDepth);

        if (LocalMap == null)
        {
            LocalMap = new DroneLocalMap(width, height, depth);
        }
        else
        {
            LocalMap.Resize(width, height, depth);
        }

        LocalMapReset?.Invoke(this);
    }

    public bool ObserveCell(DroneNative.DroneVec3i cell, DroneCellState state, float timestamp)
    {
        EnsureLocalMap();
        bool changed = LocalMap.TrySetCell(cell, state, timestamp);
        if (changed)
        {
            LocalMapChanged?.Invoke(this);
        }

        return changed;
    }

    public bool ObserveTarget(DroneNative.DroneVec3i cell, float timestamp, int reporterId = -1)
    {
        EnsureLocalMap();
        int effectiveReporterId = reporterId >= 0 ? reporterId : droneId;
        bool changed = LocalMap.TryRecordTargetReport(cell, timestamp, effectiveReporterId);
        if (changed)
        {
            LocalMapChanged?.Invoke(this);
        }

        return changed;
    }

    public int BuildPlannerInputs(
        DroneNative.DroneVec3i[] knownCells,
        int[] knownStates,
        bool includeBlocked = true
    )
    {
        EnsureLocalMap();
        return LocalMap.BuildKnownCellArrays(knownCells, knownStates, includeBlocked);
    }

    private void EnsureLocalMap()
    {
        LocalMap ??= new DroneLocalMap(width, height, depth);
    }
}
