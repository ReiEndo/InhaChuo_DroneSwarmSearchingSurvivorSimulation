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

    [Header("Movement")]
    [SerializeField] private bool followPath = true;
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float arriveDistance = 0.05f;

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
    private DroneNative.DroneVec3i currentGoal;
    private bool hasGoal;
    private readonly List<FrontierCandidate> frontierCandidates = new();

    public bool HasGoal => hasGoal;
    public DroneNative.DroneVec3i CurrentGoal => currentGoal;

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

        if (replanRequested && Time.time >= nextReplanAt)
        {
            PlanNextRoute();
        }

        FollowCurrentPath();
    }

    public void RequestReplan()
    {
        replanRequested = true;
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

        Vector3 destination = world.GridToWorld(path[pathIndex], transform.position.y);
        Vector3 toDestination = destination - transform.position;
        float desiredSpeed = Time.deltaTime > Mathf.Epsilon
            ? Mathf.Min(moveSpeed, toDestination.magnitude / Time.deltaTime)
            : moveSpeed;
        Vector3 preferredVelocity = toDestination.sqrMagnitude > Mathf.Epsilon
            ? toDestination.normalized * desiredSpeed
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

        if (Vector3.Distance(transform.position, destination) > arriveDistance)
        {
            return;
        }

        pathIndex++;

        if (pathIndex >= usablePathCount)
        {
            RequestReplan();
        }
    }

    private void PlanNextRoute()
    {
        replanRequested = false;
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

        CollectFrontierCandidates(startCell);

        foreach (var candidate in frontierCandidates)
        {
            if (TryPlanPath(startCell, candidate.Cell, snapshot))
            {
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
            if (!IsTraversable(observation.State)
                || !HasUnknownNeighbor(observation.Cell))
            {
                continue;
            }

            float distance = SquaredDistance(startCell, observation.Cell);
            frontierCandidates.Add(new FrontierCandidate(observation.Cell, distance));
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
        pathCount = 0;
        usablePathCount = 0;
        pathIndex = 0;
        hasGoal = false;
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

    private readonly struct FrontierCandidate
    {
        public readonly DroneNative.DroneVec3i Cell;
        public readonly float DistanceSquared;

        public FrontierCandidate(DroneNative.DroneVec3i cell, float distanceSquared)
        {
            Cell = cell;
            DistanceSquared = distanceSquared;
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
