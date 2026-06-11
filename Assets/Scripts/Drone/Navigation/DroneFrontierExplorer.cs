using System;
using UnityEngine;

[RequireComponent(typeof(DroneSwarmAgentState))]
[RequireComponent(typeof(DroneGridSensor))]
[RequireComponent(typeof(DronePathFollower))]
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
    [SerializeField] private float travelCostWeight = 1f;
    [SerializeField] private float informationGainWeight = 4f;
    [SerializeField] private int informationGainRadius = 2;
    [SerializeField] private float sameGoalPenalty = 25f;
    [SerializeField] private float nearbyDronePenaltyRadius = 6f;

    private DroneSwarmAgentState agentState;
    private DroneGridSensor sensor;
    private DronePathFollower pathFollower;
    private DroneDemoGridWorld world;
    private DroneNative.DroneVec3i[] path = Array.Empty<DroneNative.DroneVec3i>();
    private DroneNative.DroneVec3i[] knownCells = Array.Empty<DroneNative.DroneVec3i>();
    private int[] knownStates = Array.Empty<int>();
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
    private readonly DroneFrontierGoalSelector goalSelector = new();

    public bool HasGoal => hasGoal;
    public DroneNative.DroneVec3i CurrentGoal => currentGoal;
    public DroneNative.DroneVec3i[] Path => path;
    public int UsablePathCount => usablePathCount;
    public int PathIndex => pathFollower != null ? pathFollower.PathIndex : pathIndex;

    public DroneNative.PlannerType PlannerType
    {
        get => plannerType;
        set
        {
            plannerType = value;
            RequestReplan();
        }
    }

    private void Awake()
    {
        agentState = GetComponent<DroneSwarmAgentState>();
        sensor = GetComponent<DroneGridSensor>();
        pathFollower = GetComponent<DronePathFollower>();
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

        if (pathFollower != null)
        {
            pathFollower.PathFinished += HandlePathFinished;
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

        if (pathFollower != null)
        {
            pathFollower.PathFinished -= HandlePathFinished;
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
        travelCostWeight = Mathf.Max(0f, travelCostWeight);
        informationGainWeight = Mathf.Max(0f, informationGainWeight);
        informationGainRadius = Mathf.Max(1, informationGainRadius);
        sameGoalPenalty = Mathf.Max(0f, sameGoalPenalty);
        nearbyDronePenaltyRadius = Mathf.Max(0f, nearbyDronePenaltyRadius);
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

    private void HandlePathFinished(DronePathFollower follower)
    {
        RequestImmediateReplan();
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

        var snapshot = CreateReusablePlannerInputSnapshot();
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
        currentGoalDistanceSquared = DroneGridMath.SquaredDistance(startCell, goalCell);
        hasGoal = true;
        usablePathCount = Mathf.Min(pathCount, path.Length);
        pathIndex = usablePathCount > 1 ? 1 : 0;
        pathFollower?.SetPath(path, usablePathCount);
    }

    private DronePlannerInputSnapshot CreateReusablePlannerInputSnapshot()
    {
        int requiredCapacity = agentState.LocalMap.CellCount;
        if (knownCells.Length != requiredCapacity)
        {
            knownCells = new DroneNative.DroneVec3i[requiredCapacity];
            knownStates = new int[requiredCapacity];
        }

        int knownCount = agentState.LocalMap.BuildKnownCellArrays(knownCells, knownStates);
        return DronePlannerInputSnapshot.Wrap(knownCells, knownStates, knownCount);
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

        goalSelector.Collect(agentState, world, startCell, CreateGoalSelectorSettings());
        int candidateCount = goalSelector.Count;
        int startIndex = candidateCount > 0
            ? Mathf.Abs(agentState.DroneId) % Mathf.Min(candidateCount, 8)
            : 0;

        for (int offset = 0; offset < candidateCount; offset++)
        {
            goalSelector.TryGetCandidate(
                (startIndex + offset) % candidateCount,
                out var candidateCell,
                out float candidateDistanceSquared
            );

            if (hasGoal
                && IsCommittedToCurrentGoal()
                && candidateDistanceSquared >= currentGoalDistanceSquared * goalSwitchImprovementRatio)
            {
                continue;
            }

            if (TryPlanPath(startCell, candidateCell, snapshot))
            {
                currentGoalDistanceSquared = candidateDistanceSquared;
                goalCell = candidateCell;
                return true;
            }
        }

        goalCell = default;
        return false;
    }

    private DroneFrontierGoalSelector.Settings CreateGoalSelectorSettings()
    {
        return new DroneFrontierGoalSelector.Settings
        {
            RecentGoalPenaltySeconds = recentGoalPenaltySeconds,
            RecentGoalPenaltyDistance = recentGoalPenaltyDistance,
            TravelCostWeight = travelCostWeight,
            InformationGainWeight = informationGainWeight,
            InformationGainRadius = informationGainRadius,
            SameGoalPenalty = sameGoalPenalty,
            NearbyDronePenaltyRadius = nearbyDronePenaltyRadius,
        };
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

    private void ClearPath()
    {
        if (hasGoal)
        {
            goalSelector.RememberGoal(agentState.LocalMap, currentGoal, Time.time);
        }

        pathCount = 0;
        usablePathCount = 0;
        pathIndex = 0;
        hasGoal = false;
        pathFollower?.ClearPath();
    }

    private bool ShouldKeepCurrentGoal(
        DroneNative.DroneVec3i startCell,
        DronePlannerInputSnapshot snapshot
    )
    {
        if (!hasGoal || DroneGridMath.CellsEqual(startCell, currentGoal))
        {
            return false;
        }

        DroneCellState goalState = agentState.LocalMap.GetState(currentGoal);
        if (!DroneFrontierGoalSelector.IsTraversable(goalState)
            || !DroneFrontierGoalSelector.HasUnknownNeighbor(agentState.LocalMap, world, currentGoal))
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
            currentGoalDistanceSquared = DroneGridMath.SquaredDistance(startCell, currentGoal);
        }

        return planned;
    }

    private bool IsCommittedToCurrentGoal()
    {
        return Time.time - currentGoalChosenAt < goalCommitmentSeconds;
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
