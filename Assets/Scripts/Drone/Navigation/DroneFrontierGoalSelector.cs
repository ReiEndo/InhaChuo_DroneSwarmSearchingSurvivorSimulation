using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class DroneFrontierGoalSelector
{
    private static readonly DroneNative.DroneVec3i[] s_NeighborOffsets =
    {
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 1, 0),
        new(0, -1, 0),
        new(0, 0, 1),
        new(0, 0, -1),
    };

    private readonly List<FrontierCandidate> m_FrontierCandidates = new();
    private readonly Dictionary<int, float> m_RecentGoalTimes = new();
    private readonly List<int> m_ExpiredRecentGoalKeys = new();
    private readonly List<DroneNative.DroneVec3i> m_RecentGoalCells = new();
    private readonly List<DroneNative.DroneVec3i> m_OccupiedGoalCells = new();
    private DroneNative.DroneFrontierCandidate[] m_NativeCandidates = Array.Empty<DroneNative.DroneFrontierCandidate>();
    private DroneNative.DroneVec3i[] m_RecentGoalCellsBuffer = Array.Empty<DroneNative.DroneVec3i>();
    private DroneNative.DroneVec3i[] m_OccupiedGoalCellsBuffer = Array.Empty<DroneNative.DroneVec3i>();

    public struct Settings
    {
        public float RecentGoalPenaltySeconds;
        public float RecentGoalPenaltyDistance;
        public float TravelCostWeight;
        public float InformationGainWeight;
        public int InformationGainRadius;
        public float SameGoalPenalty;
        public float NearbyDronePenaltyRadius;
    }

    public void RememberGoal(DroneLocalMap localMap, DroneNative.DroneVec3i goalCell, float time)
    {
        if (localMap != null)
        {
            m_RecentGoalTimes[localMap.GridIndex(goalCell)] = time;
        }
    }

    public void Collect(
        DroneSwarmAgentState agentState,
        DroneDemoGridWorld world,
        DroneNative.DroneVec3i startCell,
        DronePlannerInputSnapshot snapshot,
        Settings settings
    )
    {
        m_FrontierCandidates.Clear();
        if (agentState == null || agentState.LocalMap == null || world == null)
        {
            return;
        }

        PruneRecentGoals(settings.RecentGoalPenaltySeconds, Time.time);
        BuildRecentGoalCells(agentState.LocalMap, settings);
        BuildOccupiedGoalCells(agentState, settings);

        var nativeSettings = new DroneNative.DroneFrontierScoringSettings
        {
            travel_cost_weight = Mathf.Max(0f, settings.TravelCostWeight),
            information_gain_weight = Mathf.Max(0f, settings.InformationGainWeight),
            information_gain_radius = Mathf.Max(0, settings.InformationGainRadius),
            recent_goal_penalty = Mathf.Max(0f, settings.RecentGoalPenaltyDistance),
            same_goal_penalty = Mathf.Max(0f, settings.SameGoalPenalty),
            nearby_drone_penalty_radius = Mathf.Max(0f, settings.NearbyDronePenaltyRadius),
        };

        DroneNative.DroneVec3i[] recentGoalCells = CopyToOptionalBuffer(
            m_RecentGoalCells,
            ref m_RecentGoalCellsBuffer
        );
        DroneNative.DroneVec3i[] occupiedGoalCells = CopyToOptionalBuffer(
            m_OccupiedGoalCells,
            ref m_OccupiedGoalCellsBuffer
        );

        int required = DroneNative.DroneRankFrontierCandidates(
            agentState.LocalMap.Width,
            agentState.LocalMap.Height,
            agentState.LocalMap.Depth,
            startCell,
            snapshot.KnownCells,
            snapshot.KnownStates,
            snapshot.KnownCount,
            recentGoalCells,
            m_RecentGoalCells.Count,
            occupiedGoalCells,
            m_OccupiedGoalCells.Count,
            nativeSettings,
            null,
            0
        );

        if (required <= 0)
        {
            return;
        }

        EnsureNativeCandidateCapacity(required);
        int count = DroneNative.DroneRankFrontierCandidates(
            agentState.LocalMap.Width,
            agentState.LocalMap.Height,
            agentState.LocalMap.Depth,
            startCell,
            snapshot.KnownCells,
            snapshot.KnownStates,
            snapshot.KnownCount,
            recentGoalCells,
            m_RecentGoalCells.Count,
            occupiedGoalCells,
            m_OccupiedGoalCells.Count,
            nativeSettings,
            m_NativeCandidates,
            m_NativeCandidates.Length
        );

        for (int i = 0; i < count; i++)
        {
            var candidate = m_NativeCandidates[i];
            m_FrontierCandidates.Add(new FrontierCandidate(candidate.cell, candidate.raw_distance_squared));
        }
    }

    public bool TryGetCandidate(int index, out DroneNative.DroneVec3i cell, out float rawDistanceSquared)
    {
        if (index < 0 || index >= m_FrontierCandidates.Count)
        {
            cell = default;
            rawDistanceSquared = 0f;
            return false;
        }

        var candidate = m_FrontierCandidates[index];
        cell = candidate.Cell;
        rawDistanceSquared = candidate.RawDistanceSquared;
        return true;
    }

    public int Count => m_FrontierCandidates.Count;

    public static bool IsTraversable(DroneCellState state)
    {
        return state == DroneCellState.Free || state == DroneCellState.Target;
    }

    public static bool HasUnknownNeighbor(DroneLocalMap localMap, DroneDemoGridWorld world, DroneNative.DroneVec3i cell)
    {
        foreach (var offset in s_NeighborOffsets)
        {
            var neighbor = new DroneNative.DroneVec3i(cell.x + offset.x, cell.y + offset.y, cell.z + offset.z);
            if (world.IsInBounds(neighbor) && localMap.GetState(neighbor) == DroneCellState.Unknown)
            {
                return true;
            }
        }

        return false;
    }

    private void BuildRecentGoalCells(DroneLocalMap localMap, Settings settings)
    {
        m_RecentGoalCells.Clear();
        if (settings.RecentGoalPenaltySeconds <= 0f || settings.RecentGoalPenaltyDistance <= 0f)
        {
            return;
        }

        foreach (int index in m_RecentGoalTimes.Keys)
        {
            if (localMap.TryCellFromGridIndex(index, out var cell))
            {
                m_RecentGoalCells.Add(cell);
            }
        }
    }

    private void BuildOccupiedGoalCells(DroneSwarmAgentState agentState, Settings settings)
    {
        m_OccupiedGoalCells.Clear();
        if (settings.NearbyDronePenaltyRadius <= 0f || settings.SameGoalPenalty <= 0f)
        {
            return;
        }

        var agents = DroneSwarmAgentState.ActiveAgents;
        foreach (var other in agents)
        {
            if (other == agentState || !other.TryGetComponent<DroneFrontierExplorer>(out var explorer) || !explorer.HasGoal)
            {
                continue;
            }

            m_OccupiedGoalCells.Add(explorer.CurrentGoal);
        }
    }

    private static DroneNative.DroneVec3i[] CopyToOptionalBuffer(
        List<DroneNative.DroneVec3i> cells,
        ref DroneNative.DroneVec3i[] buffer
    )
    {
        int count = cells.Count;
        if (count == 0)
        {
            return null;
        }

        if (buffer.Length < count)
        {
            buffer = new DroneNative.DroneVec3i[count];
        }

        for (int i = 0; i < count; i++)
        {
            buffer[i] = cells[i];
        }

        return buffer;
    }

    private void EnsureNativeCandidateCapacity(int required)
    {
        if (m_NativeCandidates.Length >= required)
        {
            return;
        }

        m_NativeCandidates = new DroneNative.DroneFrontierCandidate[required];
    }

    private void PruneRecentGoals(float recentGoalPenaltySeconds, float time)
    {
        if (recentGoalPenaltySeconds <= 0f || m_RecentGoalTimes.Count == 0)
        {
            return;
        }

        float expiryTime = time - recentGoalPenaltySeconds;
        m_ExpiredRecentGoalKeys.Clear();
        foreach (var pair in m_RecentGoalTimes)
        {
            if (pair.Value <= expiryTime)
            {
                m_ExpiredRecentGoalKeys.Add(pair.Key);
            }
        }

        foreach (int key in m_ExpiredRecentGoalKeys)
        {
            m_RecentGoalTimes.Remove(key);
        }
    }

    private readonly struct FrontierCandidate
    {
        public readonly DroneNative.DroneVec3i Cell;
        public readonly float RawDistanceSquared;

        public FrontierCandidate(DroneNative.DroneVec3i cell, float rawDistanceSquared)
        {
            Cell = cell;
            RawDistanceSquared = rawDistanceSquared;
        }
    }
}
