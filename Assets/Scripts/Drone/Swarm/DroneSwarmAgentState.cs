using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class DroneSwarmAgentState : MonoBehaviour
{
    private static readonly List<DroneSwarmAgentState> s_ActiveAgents = new();

    [Header("Identity")]
    [SerializeField] private int droneId;
    [SerializeField] private int expectedDroneCount = 1;
    [SerializeField] private int targetInformedDroneCount;

    [Header("Local Map")]
    [SerializeField] private int width = 10;
    [SerializeField] private int height = 1;
    [SerializeField] private int depth = 10;

    public int DroneId
    {
        get => droneId;
        set => droneId = value;
    }

    public int ExpectedDroneCount
    {
        get => expectedDroneCount;
        set => expectedDroneCount = Mathf.Max(0, value);
    }

    public DroneLocalMap LocalMap { get; private set; }
    public int TargetInformedDroneCount => targetInformedDroneCount;
    public IReadOnlyCollection<int> TargetInformedDroneIds => targetInformedDroneIds;
    public bool KnowsTargetFound => LocalMap != null && LocalMap.TryGetLatestTargetReport(out _);
    public bool AllExpectedDronesTargetInformed => expectedDroneCount > 0 && targetInformedDroneCount >= expectedDroneCount;

    public event Action<DroneSwarmAgentState> LocalMapReset;
    public event Action<DroneSwarmAgentState> LocalMapChanged;
    public event Action<DroneSwarmAgentState> TargetInformedDronesChanged;

    public static IReadOnlyList<DroneSwarmAgentState> ActiveAgents => s_ActiveAgents;

    private readonly HashSet<int> targetInformedDroneIds = new();

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
        expectedDroneCount = Mathf.Max(0, expectedDroneCount);
        targetInformedDroneCount = Mathf.Max(0, targetInformedDroneCount);
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

        ClearTargetInformedDrones();
        LocalMapReset?.Invoke(this);
    }

    public void ConfigureSwarmMembership(int expectedDrones)
    {
        ExpectedDroneCount = expectedDrones;
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
        bool mapChanged = LocalMap.TryRecordTargetReport(cell, timestamp, effectiveReporterId);
        bool informedChanged = MarkTargetInformedInternal(effectiveReporterId)
            | MarkTargetInformedInternal(droneId);

        if (mapChanged)
        {
            LocalMapChanged?.Invoke(this);
        }

        if (informedChanged)
        {
            TargetInformedDronesChanged?.Invoke(this);
        }

        return mapChanged || informedChanged;
    }

    public bool MergeTargetInformedDronesFrom(DroneSwarmAgentState source)
    {
        if (source == null)
        {
            return false;
        }

        bool changed = false;
        if (source.KnowsTargetFound)
        {
            changed |= MarkTargetInformedInternal(source.DroneId);
        }

        foreach (int informedDroneId in source.targetInformedDroneIds)
        {
            changed |= MarkTargetInformedInternal(informedDroneId);
        }

        if (changed)
        {
            TargetInformedDronesChanged?.Invoke(this);
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

    private bool MarkTargetInformedInternal(int informedDroneId)
    {
        if (informedDroneId <= 0 || !targetInformedDroneIds.Add(informedDroneId))
        {
            return false;
        }

        targetInformedDroneCount = targetInformedDroneIds.Count;
        return true;
    }

    private void ClearTargetInformedDrones()
    {
        if (targetInformedDroneIds.Count == 0 && targetInformedDroneCount == 0)
        {
            return;
        }

        targetInformedDroneIds.Clear();
        targetInformedDroneCount = 0;
        TargetInformedDronesChanged?.Invoke(this);
    }
}
