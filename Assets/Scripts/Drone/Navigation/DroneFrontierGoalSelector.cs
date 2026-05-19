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
        Settings settings
    )
    {
        m_FrontierCandidates.Clear();

        foreach (var observation in agentState.LocalMap.KnownObservations)
        {
            if (DroneGridMath.CellsEqual(observation.Cell, startCell)
                || !IsTraversable(observation.State)
                || !HasUnknownNeighbor(agentState.LocalMap, world, observation.Cell))
            {
                continue;
            }

            float travelCost = DroneGridMath.SquaredDistance(startCell, observation.Cell);
            float informationGain = EstimateInformationGain(agentState.LocalMap, world, observation.Cell, settings.InformationGainRadius);
            float recentPenalty = GetRecentGoalPenalty(agentState.LocalMap, observation.Cell, settings, Time.time);
            float swarmPenalty = EstimateSwarmOverlapPenalty(agentState, observation.Cell, settings);
            float score = settings.TravelCostWeight * travelCost
                - settings.InformationGainWeight * informationGain
                + recentPenalty
                + swarmPenalty;
            m_FrontierCandidates.Add(new FrontierCandidate(observation.Cell, score, travelCost));
        }

        m_FrontierCandidates.Sort(static (left, right) => left.Score.CompareTo(right.Score));
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

    private float EstimateInformationGain(DroneLocalMap localMap, DroneDemoGridWorld world, DroneNative.DroneVec3i center, int radius)
    {
        int gain = 0;
        for (int dz = -radius; dz <= radius; dz++)
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            var cell = new DroneNative.DroneVec3i(center.x + dx, center.y + dy, center.z + dz);
            if (world.IsInBounds(cell) && localMap.GetState(cell) == DroneCellState.Unknown)
            {
                gain++;
            }
        }

        return gain;
    }

    private float EstimateSwarmOverlapPenalty(DroneSwarmAgentState agentState, DroneNative.DroneVec3i candidate, Settings settings)
    {
        if (settings.NearbyDronePenaltyRadius <= 0f || settings.SameGoalPenalty <= 0f)
        {
            return 0f;
        }

        float penalty = 0f;
        float radiusSquared = settings.NearbyDronePenaltyRadius * settings.NearbyDronePenaltyRadius;
        var agents = Object.FindObjectsByType<DroneSwarmAgentState>(FindObjectsSortMode.None);
        foreach (var other in agents)
        {
            if (other == agentState || !other.TryGetComponent<DroneFrontierExplorer>(out var explorer) || !explorer.HasGoal)
            {
                continue;
            }

            if (DroneGridMath.SquaredDistance(candidate, explorer.CurrentGoal) <= radiusSquared)
            {
                penalty += settings.SameGoalPenalty;
            }
        }

        return penalty;
    }

    private float GetRecentGoalPenalty(DroneLocalMap localMap, DroneNative.DroneVec3i cell, Settings settings, float time)
    {
        if (settings.RecentGoalPenaltySeconds <= 0f || settings.RecentGoalPenaltyDistance <= 0f || localMap == null)
        {
            return 0f;
        }

        PruneRecentGoals(settings.RecentGoalPenaltySeconds, time);
        return m_RecentGoalTimes.ContainsKey(localMap.GridIndex(cell)) ? settings.RecentGoalPenaltyDistance : 0f;
    }

    private void PruneRecentGoals(float recentGoalPenaltySeconds, float time)
    {
        if (m_RecentGoalTimes.Count == 0)
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
        public readonly float Score;
        public readonly float RawDistanceSquared;

        public FrontierCandidate(DroneNative.DroneVec3i cell, float score, float rawDistanceSquared)
        {
            Cell = cell;
            Score = score;
            RawDistanceSquared = rawDistanceSquared;
        }
    }
}
