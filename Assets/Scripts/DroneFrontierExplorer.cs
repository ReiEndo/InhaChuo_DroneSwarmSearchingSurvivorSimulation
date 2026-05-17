using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(DroneSwarmAgentState))]
[RequireComponent(typeof(DroneGridSensor))]
[RequireComponent(typeof(DroneLocalAvoidanceMotor))]
public sealed class DroneFrontierExplorer : MonoBehaviour
{
    [Header("Planning")]
    [SerializeField] private DroneNative.PlannerType plannerType = DroneNative.PlannerType.AStar;
    [SerializeField] private int maxPathLength = 512;
    [SerializeField] private float replanIntervalSeconds = 0.5f;
    [SerializeField] private float goalCommitmentSeconds = 2f;
    [SerializeField] private float goalSwitchImprovementRatio = 0.6f;
    [SerializeField] private float recentGoalPenaltySeconds = 8f;
    [SerializeField] private float recentGoalPenaltyDistance = 16f;

    [Header("Movement")]
    [SerializeField] private bool followPath = true;
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float arriveDistance = 0.2f;

    private static readonly DroneNative.DroneVec3i[] NeighborOffsets =
    {
        new DroneNative.DroneVec3i(1, 0, 0),
        new DroneNative.DroneVec3i(-1, 0, 0),
        new DroneNative.DroneVec3i(0, 1, 0),
        new DroneNative.DroneVec3i(0, -1, 0),
        new DroneNative.DroneVec3i(0, 0, 1),
        new DroneNative.DroneVec3i(0, 0, -1),
    };

    private DroneSwarmAgentState agentState;
    private DroneGridSensor sensor;
    private DroneLocalAvoidanceMotor avoidanceMotor;
    private DroneDemoGridWorld world;
    private DroneNative.DroneVec3i[] path = Array.Empty<DroneNative.DroneVec3i>();
    private int pathCount;
    private int usablePathCount;
    private int pathIndex;
    private float nextReplanAt;
    private bool replanRequested = true;
    private bool immediateReplanRequested;
    private DroneNative.DroneVec3i currentGoal;
    private float currentGoalChosenAt;
    private float currentGoalDistanceSquared;
    private bool hasGoal;
    private readonly List<FrontierCandidate> frontierCandidates = new();
    private readonly Dictionary<int, float> recentGoalTimes = new();
    private readonly List<int> expiredRecentGoalKeys = new();

    public bool HasGoal => hasGoal;
    public DroneNative.DroneVec3i CurrentGoal => currentGoal;
    public DroneNative.DroneVec3i[] Path => path;
    public int UsablePathCount => usablePathCount;
    public int PathIndex => pathIndex;

    public DroneNative.PlannerType PlannerType
    {
        get => plannerType;
        set
        {
            plannerType = value;
            RequestReplan();
        }
    }

    public float MoveSpeed
    {
        get => moveSpeed;
        set => moveSpeed = Mathf.Max(0f, value);
    }

    private void Awake()
    {
        agentState = GetComponent<DroneSwarmAgentState>();
        sensor = GetComponent<DroneGridSensor>();
        avoidanceMotor = GetComponent<DroneLocalAvoidanceMotor>();
        world = sensor.World;
    }

    private void OnEnable()
    {
        if (sensor != null)
        {
            sensor.ObservationsChanged += HandleObservationsChanged;
        }

        if (agentState != null)
        {
            agentState.LocalMapChanged += HandleLocalMapChanged;
        }
    }

    private void OnDisable()
    {
        if (sensor != null)
        {
            sensor.ObservationsChanged -= HandleObservationsChanged;
        }

        if (agentState != null)
        {
            agentState.LocalMapChanged -= HandleLocalMapChanged;
        }
    }

    private void Start()
    {
        world = sensor.World;
        RequestReplan();
    }

    private void OnValidate()
    {
        maxPathLength = Mathf.Max(2, maxPathLength);
        replanIntervalSeconds = Mathf.Max(0.05f, replanIntervalSeconds);
        goalCommitmentSeconds = Mathf.Max(0f, goalCommitmentSeconds);
        goalSwitchImprovementRatio = Mathf.Clamp01(goalSwitchImprovementRatio);
        recentGoalPenaltySeconds = Mathf.Max(0f, recentGoalPenaltySeconds);
        recentGoalPenaltyDistance = Mathf.Max(0f, recentGoalPenaltyDistance);
        moveSpeed = Mathf.Max(0f, moveSpeed);
        arriveDistance = Mathf.Max(0.001f, arriveDistance);
    }

    private void Update()
    {
        if (world == null)
        {
            world = sensor.World;
            if (world == null)
            {
                return;
            }
        }

        if (replanRequested && (immediateReplanRequested || Time.time >= nextReplanAt))
        {
            PlanNextRoute();
        }

        FollowCurrentPath();
    }

    public void RequestReplan()
    {
        replanRequested = true;
    }

    private void RequestImmediateReplan()
    {
        replanRequested = true;
        immediateReplanRequested = true;
    }

    private void HandleObservationsChanged(DroneGridSensor changedSensor)
    {
        RequestReplan();
    }

    private void HandleLocalMapChanged(DroneSwarmAgentState changedAgent)
    {
        RequestReplan();
    }

    private void FollowCurrentPath()
    {
        if (!followPath || usablePathCount <= 0 || pathIndex >= usablePathCount)
        {
            return;
        }

        AdvanceReachedWaypoints();
        if (pathIndex >= usablePathCount && !TryContinueWithImmediateReplan())
        {
            return;
        }

        Vector3 destination = world.GridToWorld(path[pathIndex], transform.position.y);
        Vector3 toDestination = destination - transform.position;
        Vector3 preferredVelocity = toDestination.sqrMagnitude > Mathf.Epsilon
            ? toDestination.normalized * moveSpeed
            : Vector3.zero;

        if (avoidanceMotor != null)
        {
            avoidanceMotor.Move(preferredVelocity, moveSpeed);
        }
        else
        {
            transform.position = Vector3.MoveTowards(
                transform.position,
                destination,
                moveSpeed * Time.deltaTime
            );
        }

        AdvanceReachedOrPassedWaypoints(destination, toDestination);
        if (pathIndex >= usablePathCount)
        {
            TryContinueWithImmediateReplan();
        }
    }

    private bool TryContinueWithImmediateReplan()
    {
        PlanNextRoute();
        return followPath && usablePathCount > 0 && pathIndex < usablePathCount;
    }

    private void AdvanceReachedWaypoints()
    {
        while (pathIndex < usablePathCount)
        {
            Vector3 destination = world.GridToWorld(path[pathIndex], transform.position.y);
            if (Vector3.Distance(transform.position, destination) > arriveDistance)
            {
                return;
            }

            pathIndex++;
        }
    }

    private void AdvanceReachedOrPassedWaypoints(Vector3 previousDestination, Vector3 previousOffset)
    {
        if (pathIndex >= usablePathCount)
        {
            return;
        }

        Vector3 currentDestination = world.GridToWorld(path[pathIndex], transform.position.y);
        Vector3 currentOffset = currentDestination - transform.position;
        if (Vector3.Distance(transform.position, currentDestination) <= arriveDistance
            || (currentDestination == previousDestination
                && Vector3.Dot(previousOffset, currentOffset) <= 0f))
        {
            pathIndex++;
            AdvanceReachedWaypoints();
        }
    }

    private void PlanNextRoute()
    {
        replanRequested = false;
        immediateReplanRequested = false;
        nextReplanAt = Time.time + replanIntervalSeconds;

        if (agentState.LocalMap == null)
        {
            ClearPath();
            return;
        }

        var startCell = world.WorldToGrid(transform.position);
        agentState.ObserveCell(startCell, DroneCellState.Free, Mathf.Max(Time.time, 0.0001f));

        var snapshot = agentState.LocalMap.CreatePlannerInputSnapshot();
        if (path == null || path.Length != maxPathLength)
        {
            path = new DroneNative.DroneVec3i[maxPathLength];
        }

        if (!TryPlanReachableGoal(startCell, snapshot, out var goalCell))
        {
            ClearPath();
            return;
        }

        currentGoal = goalCell;
        currentGoalChosenAt = Time.time;
        currentGoalDistanceSquared = SquaredDistance(startCell, goalCell);
        hasGoal = true;
        usablePathCount = Mathf.Min(pathCount, path.Length);
        pathIndex = usablePathCount > 1 ? 1 : 0;
    }

    private bool TryPlanReachableGoal(
        DroneNative.DroneVec3i startCell,
        DronePlannerInputSnapshot snapshot,
        out DroneNative.DroneVec3i goalCell
    )
    {
        if (agentState.LocalMap.TryGetLatestTargetReport(out var targetReport))
        {
            goalCell = targetReport.Cell;
            return TryPlanPath(startCell, goalCell, snapshot);
        }

        if (ShouldKeepCurrentGoal(startCell, snapshot))
        {
            goalCell = currentGoal;
            return true;
        }

        CollectFrontierCandidates(startCell);

        int candidateCount = frontierCandidates.Count;
        int startIndex = candidateCount > 0
            ? Mathf.Abs(agentState.DroneId) % Mathf.Min(candidateCount, 8)
            : 0;

        for (int offset = 0; offset < candidateCount; offset++)
        {
            var candidate = frontierCandidates[(startIndex + offset) % candidateCount];
            if (hasGoal
                && IsCommittedToCurrentGoal()
                && candidate.DistanceSquared >= currentGoalDistanceSquared * goalSwitchImprovementRatio)
            {
                continue;
            }

            if (TryPlanPath(startCell, candidate.Cell, snapshot))
            {
                currentGoalDistanceSquared = candidate.RawDistanceSquared;
                goalCell = candidate.Cell;
                return true;
            }
        }

        goalCell = default;
        return false;
    }

    private void CollectFrontierCandidates(DroneNative.DroneVec3i startCell)
    {
        frontierCandidates.Clear();

        foreach (var observation in agentState.LocalMap.KnownObservations)
        {
            if (CellsEqual(observation.Cell, startCell)
                || !IsTraversable(observation.State)
                || !HasUnknownNeighbor(observation.Cell))
            {
                continue;
            }

            float rawDistance = SquaredDistance(startCell, observation.Cell);
            float rankedDistance = rawDistance + GetRecentGoalPenalty(observation.Cell);
            frontierCandidates.Add(new FrontierCandidate(observation.Cell, rankedDistance, rawDistance));
        }

        frontierCandidates.Sort(static (left, right) =>
            left.DistanceSquared.CompareTo(right.DistanceSquared)
        );
    }

    private bool TryPlanPath(
        DroneNative.DroneVec3i startCell,
        DroneNative.DroneVec3i goalCell,
        DronePlannerInputSnapshot snapshot
    )
    {
        pathCount = DroneNative.DronePlanKnownPath(
            (int)plannerType,
            world.Width,
            world.Height,
            world.Depth,
            startCell,
            goalCell,
            snapshot.KnownCells,
            snapshot.KnownStates,
            snapshot.KnownCount,
            path,
            path.Length
        );

        return pathCount > 0;
    }

    private bool HasUnknownNeighbor(DroneNative.DroneVec3i cell)
    {
        foreach (var offset in NeighborOffsets)
        {
            var neighbor = new DroneNative.DroneVec3i(
                cell.x + offset.x,
                cell.y + offset.y,
                cell.z + offset.z
            );

            if (world.IsInBounds(neighbor)
                && agentState.LocalMap.GetState(neighbor) == DroneCellState.Unknown)
            {
                return true;
            }
        }

        return false;
    }

    private void ClearPath()
    {
        if (hasGoal)
        {
            recentGoalTimes[agentState.LocalMap.GridIndex(currentGoal)] = Time.time;
        }

        pathCount = 0;
        usablePathCount = 0;
        pathIndex = 0;
        hasGoal = false;
    }

    private bool ShouldKeepCurrentGoal(
        DroneNative.DroneVec3i startCell,
        DronePlannerInputSnapshot snapshot
    )
    {
        if (!hasGoal || CellsEqual(startCell, currentGoal))
        {
            return false;
        }

        DroneCellState goalState = agentState.LocalMap.GetState(currentGoal);
        if (!IsTraversable(goalState) || !HasUnknownNeighbor(currentGoal))
        {
            return false;
        }

        if (!IsCommittedToCurrentGoal())
        {
            return false;
        }

        bool planned = TryPlanPath(startCell, currentGoal, snapshot);
        if (planned)
        {
            currentGoalDistanceSquared = SquaredDistance(startCell, currentGoal);
        }

        return planned;
    }

    private bool IsCommittedToCurrentGoal()
    {
        return Time.time - currentGoalChosenAt < goalCommitmentSeconds;
    }

    private float GetRecentGoalPenalty(DroneNative.DroneVec3i cell)
    {
        if (recentGoalPenaltySeconds <= 0f
            || recentGoalPenaltyDistance <= 0f
            || agentState.LocalMap == null)
        {
            return 0f;
        }

        PruneRecentGoals();

        int index = agentState.LocalMap.GridIndex(cell);
        return recentGoalTimes.ContainsKey(index) ? recentGoalPenaltyDistance : 0f;
    }

    private void PruneRecentGoals()
    {
        if (recentGoalTimes.Count == 0)
        {
            return;
        }

        float expiryTime = Time.time - recentGoalPenaltySeconds;
        expiredRecentGoalKeys.Clear();
        foreach (var pair in recentGoalTimes)
        {
            if (pair.Value <= expiryTime)
            {
                expiredRecentGoalKeys.Add(pair.Key);
            }
        }

        foreach (int key in expiredRecentGoalKeys)
        {
            recentGoalTimes.Remove(key);
        }
    }

    private static bool IsTraversable(DroneCellState state)
    {
        return state == DroneCellState.Free || state == DroneCellState.Target;
    }

    private static float SquaredDistance(
        DroneNative.DroneVec3i a,
        DroneNative.DroneVec3i b
    )
    {
        int dx = a.x - b.x;
        int dy = a.y - b.y;
        int dz = a.z - b.z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static bool CellsEqual(
        DroneNative.DroneVec3i a,
        DroneNative.DroneVec3i b
    )
    {
        return a.x == b.x && a.y == b.y && a.z == b.z;
    }

    private readonly struct FrontierCandidate
    {
        public readonly DroneNative.DroneVec3i Cell;
        public readonly float DistanceSquared;
        public readonly float RawDistanceSquared;

        public FrontierCandidate(
            DroneNative.DroneVec3i cell,
            float distanceSquared,
            float rawDistanceSquared
        )
        {
            Cell = cell;
            DistanceSquared = distanceSquared;
            RawDistanceSquared = rawDistanceSquared;
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (world == null || path == null || usablePathCount <= 0)
        {
            return;
        }

        Gizmos.color = Color.yellow;
        for (int i = 0; i < usablePathCount - 1; i++)
        {
            Gizmos.DrawLine(
                world.GridToWorld(path[i], transform.position.y),
                world.GridToWorld(path[i + 1], transform.position.y)
            );
        }

        if (hasGoal)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(world.GridToWorld(currentGoal, transform.position.y), world.CellSize * 0.35f);
        }
    }
}
