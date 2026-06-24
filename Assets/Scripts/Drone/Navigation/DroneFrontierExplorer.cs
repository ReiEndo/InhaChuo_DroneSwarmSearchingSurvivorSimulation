using System;
using System.Collections.Generic;
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
    [SerializeField] private float targetReturnDwellSeconds = 0.5f;

    private enum TargetKnowledgeMode
    {
        SearchingForTarget,
        HoldingTarget,
        SearchingForDrones,
        ReturningToTarget,
    }

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
    private DroneNative.DroneVec3i homeCell;
    private float currentGoalChosenAt;
    private float currentGoalDistanceSquared;
    private bool hasGoal;
    private bool hasHomeCell;
    private bool returningHome;
    private bool reachedHomeAfterTarget;
    private bool searchFromTargetAnchorRequested;
    private TargetKnowledgeMode targetKnowledgeMode = TargetKnowledgeMode.SearchingForTarget;
    private DroneNative.DroneVec3i targetAnchorCell;
    private bool hasTargetAnchorCell;
    private int targetAnchorReporterId = -1;
    private int lastTargetInformedDroneCount;
    private bool dwellingAtTargetAnchor;
    private float targetAnchorDwellUntil;
    private bool missionComplete;
    private readonly List<DroneNative.DroneVec3i> breadcrumbTrail = new();
    private readonly DroneFrontierGoalSelector goalSelector = new();

    public bool HasGoal => hasGoal;
    public DroneNative.DroneVec3i CurrentGoal => currentGoal;
    public bool ReturningHome => returningHome || targetKnowledgeMode == TargetKnowledgeMode.ReturningToTarget;
    public bool HoldingTarget => targetKnowledgeMode == TargetKnowledgeMode.HoldingTarget;
    public bool SearchingForDrones => targetKnowledgeMode == TargetKnowledgeMode.SearchingForDrones;
    public int TargetAnchorReporterId => targetAnchorReporterId;
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
            sensor.TargetSensed += HandleTargetSensed;
        }

        if (agentState != null)
        {
            agentState.LocalMapChanged += HandleLocalMapChanged;
            agentState.TargetInformedDronesChanged += HandleTargetInformedDronesChanged;
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
            sensor.TargetSensed -= HandleTargetSensed;
        }

        if (agentState != null)
        {
            agentState.LocalMapChanged -= HandleLocalMapChanged;
            agentState.TargetInformedDronesChanged -= HandleTargetInformedDronesChanged;
        }

        if (pathFollower != null)
        {
            pathFollower.PathFinished -= HandlePathFinished;
        }
    }

    private void Start()
    {
        world = sensor.World;
        CaptureHomeCellIfNeeded();
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
        targetReturnDwellSeconds = Mathf.Max(0f, targetReturnDwellSeconds);
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

        RecordBreadcrumb(world.WorldToGrid(transform.position));

        if (dwellingAtTargetAnchor)
        {
            if (Time.time < targetAnchorDwellUntil)
            {
                return;
            }

            dwellingAtTargetAnchor = false;
            if (agentState.AllExpectedDronesTargetInformed)
            {
                replanRequested = false;
                immediateReplanRequested = false;
                ClearPath();
                return;
            }

            if (!missionComplete)
            {
                RequestImmediateReplan();
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

    public void ConfigureHomeCell(DroneNative.DroneVec3i newHomeCell)
    {
        homeCell = newHomeCell;
        hasHomeCell = true;
    }

    public void ReturnToHome()
    {
        CaptureHomeCellIfNeeded();
        if (!hasHomeCell || reachedHomeAfterTarget)
        {
            return;
        }

        returningHome = true;
        RequestImmediateReplan();
    }

    public void StopAfterTargetFound()
    {
        if (agentState != null
            && agentState.LocalMap != null
            && agentState.LocalMap.TryGetEarliestTargetReport(out var firstReport))
        {
            if (firstReport.ReporterId == agentState.DroneId)
            {
                BecomeTargetHolder(firstReport);
            }
            else
            {
                BecomeDroneSearcher(firstReport, HasDirectTargetReportFor(firstReport.Cell));
            }
            return;
        }

        targetKnowledgeMode = TargetKnowledgeMode.HoldingTarget;
        StopInPlaceAsTargetHolder();
    }

    public void StopAfterMissionComplete()
    {
        missionComplete = true;
        returningHome = false;
        dwellingAtTargetAnchor = false;
        searchFromTargetAnchorRequested = false;
        ClearPath();
        if (pathFollower != null)
        {
            pathFollower.FollowPath = false;
        }
    }

    private void RequestImmediateReplan()
    {
        replanRequested = true;
        immediateReplanRequested = true;
    }

    private void SynchronizeTargetKnowledgeState()
    {
        if (missionComplete || agentState == null || agentState.LocalMap == null)
        {
            return;
        }

        if (!agentState.LocalMap.TryGetEarliestTargetReport(out var firstReport))
        {
            targetKnowledgeMode = TargetKnowledgeMode.SearchingForTarget;
            hasTargetAnchorCell = false;
            targetAnchorReporterId = -1;
            lastTargetInformedDroneCount = 0;
            dwellingAtTargetAnchor = false;
            reachedHomeAfterTarget = false;
            searchFromTargetAnchorRequested = false;
            if (pathFollower != null)
            {
                pathFollower.FollowPath = true;
            }
            return;
        }

        hasTargetAnchorCell = true;
        targetAnchorCell = firstReport.Cell;
        targetAnchorReporterId = firstReport.ReporterId;

        if (firstReport.ReporterId == agentState.DroneId)
        {
            BecomeTargetHolder(firstReport);
        }
        else
        {
            BecomeDroneSearcher(firstReport, HasDirectTargetReportFor(firstReport.Cell));
        }
    }

    private void BecomeTargetHolder(DroneTargetReport firstReport)
    {
        hasTargetAnchorCell = true;
        targetAnchorCell = firstReport.Cell;
        targetAnchorReporterId = firstReport.ReporterId;
        targetKnowledgeMode = TargetKnowledgeMode.HoldingTarget;
        lastTargetInformedDroneCount = agentState.TargetInformedDroneCount;
        searchFromTargetAnchorRequested = false;
        StopInPlaceAsTargetHolder();
    }

    private void StopInPlaceAsTargetHolder()
    {
        returningHome = false;
        dwellingAtTargetAnchor = false;
        reachedHomeAfterTarget = true;
        searchFromTargetAnchorRequested = false;
        ClearPath();
        if (pathFollower != null)
        {
            pathFollower.FollowPath = false;
        }
    }

    private void BecomeDroneSearcher(DroneTargetReport firstReport, bool forceSearchFromTargetAnchor = false)
    {
        bool wasDroneSearcher = targetKnowledgeMode == TargetKnowledgeMode.SearchingForDrones
            || targetKnowledgeMode == TargetKnowledgeMode.ReturningToTarget;
        int informedDroneCount = agentState.TargetInformedDroneCount;
        bool learnedAboutMoreDrones = wasDroneSearcher
            && informedDroneCount > lastTargetInformedDroneCount;

        hasTargetAnchorCell = true;
        targetAnchorCell = firstReport.Cell;
        targetAnchorReporterId = firstReport.ReporterId;
        lastTargetInformedDroneCount = informedDroneCount;
        returningHome = false;
        reachedHomeAfterTarget = false;
        if (pathFollower != null)
        {
            pathFollower.FollowPath = true;
        }

        if (forceSearchFromTargetAnchor)
        {
            searchFromTargetAnchorRequested = true;
        }

        if (!forceSearchFromTargetAnchor
            && IsAtTargetAnchor()
            && (dwellingAtTargetAnchor || agentState.AllExpectedDronesTargetInformed))
        {
            targetKnowledgeMode = TargetKnowledgeMode.SearchingForDrones;
            ClearPath();
            return;
        }

        bool shouldReturnToTarget = learnedAboutMoreDrones
            || (wasDroneSearcher && agentState.AllExpectedDronesTargetInformed);
        if (shouldReturnToTarget && !IsAtTargetAnchor())
        {
            dwellingAtTargetAnchor = false;
            targetKnowledgeMode = TargetKnowledgeMode.ReturningToTarget;
        }
        else if (targetKnowledgeMode != TargetKnowledgeMode.ReturningToTarget || IsAtTargetAnchor())
        {
            targetKnowledgeMode = TargetKnowledgeMode.SearchingForDrones;
        }

        RequestImmediateReplan();
    }

    private bool IsAtTargetAnchor()
    {
        return hasTargetAnchorCell
            && world != null
            && DroneGridMath.CellsEqual(world.WorldToGrid(transform.position), targetAnchorCell);
    }

    private bool HasDirectTargetReportFor(DroneNative.DroneVec3i targetCell)
    {
        return agentState != null
            && agentState.LocalMap != null
            && agentState.LocalMap.TryGetTargetReport(agentState.DroneId, out var directReport)
            && DroneGridMath.CellsEqual(directReport.Cell, targetCell);
    }

    private bool ShouldWaitAtTargetAnchorWithAllDrones()
    {
        return !searchFromTargetAnchorRequested
            && targetKnowledgeMode == TargetKnowledgeMode.SearchingForDrones
            && agentState != null
            && agentState.AllExpectedDronesTargetInformed
            && IsAtTargetAnchor();
    }

    private void HandleObservationsChanged(DroneGridSensor changedSensor)
    {
        RequestReplan();
    }

    private void HandleLocalMapChanged(DroneSwarmAgentState changedAgent)
    {
        SynchronizeTargetKnowledgeState();
        RequestReplan();
    }

    private void HandleTargetInformedDronesChanged(DroneSwarmAgentState changedAgent)
    {
        SynchronizeTargetKnowledgeState();
        RequestReplan();
    }

    private void HandleTargetSensed(DroneGridSensor changedSensor, DroneNative.DroneVec3i targetCell)
    {
        SynchronizeTargetKnowledgeState();
    }

    private void HandlePathFinished(DronePathFollower follower)
    {
        if (targetKnowledgeMode == TargetKnowledgeMode.ReturningToTarget)
        {
            targetKnowledgeMode = TargetKnowledgeMode.SearchingForDrones;
            dwellingAtTargetAnchor = true;
            targetAnchorDwellUntil = Time.time + targetReturnDwellSeconds;
            searchFromTargetAnchorRequested = false;
            ClearPath();
            return;
        }

        if (returningHome)
        {
            returningHome = false;
            reachedHomeAfterTarget = true;
            ClearPath();
            return;
        }

        RequestImmediateReplan();
    }

    private void PlanNextRoute()
    {
        replanRequested = false;
        immediateReplanRequested = false;
        nextReplanAt = Time.time + replanIntervalSeconds;

        if (agentState.LocalMap == null
            || reachedHomeAfterTarget
            || dwellingAtTargetAnchor
            || ShouldWaitAtTargetAnchorWithAllDrones()
            || missionComplete)
        {
            ClearPath();
            return;
        }

        CaptureHomeCellIfNeeded();

        var startCell = world.WorldToGrid(transform.position);
        // Target sensing can happen earlier in the same frame as this replan. Use a
        // slightly newer timestamp so the drone's current cell is traversable when
        // plotting the route home from the found person.
        float planningTimestamp = Mathf.Max(Time.time, 0.0001f) + 0.0001f;
        agentState.ObserveCell(startCell, DroneCellState.Free, planningTimestamp);
        if (hasHomeCell)
        {
            agentState.ObserveCell(homeCell, DroneCellState.Free, planningTimestamp);
        }

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

        searchFromTargetAnchorRequested = false;
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
        if (returningHome && hasHomeCell)
        {
            goalCell = homeCell;
            return TryPlanPath(startCell, goalCell, snapshot)
                || TryBuildBreadcrumbReturnPath(startCell, goalCell);
        }

        if (targetKnowledgeMode == TargetKnowledgeMode.ReturningToTarget && hasTargetAnchorCell)
        {
            goalCell = targetAnchorCell;
            if (TryPlanPath(startCell, goalCell, snapshot))
            {
                return true;
            }

            targetKnowledgeMode = TargetKnowledgeMode.SearchingForDrones;
        }

        bool searchingForDrones = targetKnowledgeMode == TargetKnowledgeMode.SearchingForDrones;
        if (!searchingForDrones && agentState.LocalMap.TryGetLatestTargetReport(out var targetReport))
        {
            goalCell = targetReport.Cell;
            return TryPlanPath(startCell, goalCell, snapshot);
        }

        if (ShouldKeepCurrentGoal(startCell, snapshot))
        {
            goalCell = currentGoal;
            return true;
        }

        goalSelector.Collect(agentState, world, startCell, snapshot, CreateGoalSelectorSettings());
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

    private void CaptureHomeCellIfNeeded()
    {
        if (hasHomeCell || world == null)
        {
            return;
        }

        homeCell = world.WorldToGrid(transform.position);
        hasHomeCell = true;
        RecordBreadcrumb(homeCell);
    }

    private void RecordBreadcrumb(DroneNative.DroneVec3i cell)
    {
        if (breadcrumbTrail.Count > 0
            && DroneGridMath.CellsEqual(breadcrumbTrail[breadcrumbTrail.Count - 1], cell))
        {
            return;
        }

        breadcrumbTrail.Add(cell);
    }

    private bool TryBuildBreadcrumbReturnPath(
        DroneNative.DroneVec3i startCell,
        DroneNative.DroneVec3i goalCell
    )
    {
        if (path == null || path.Length == 0)
        {
            return false;
        }

        int count = 0;
        path[count++] = startCell;

        int trailIndex = breadcrumbTrail.Count - 1;
        while (trailIndex >= 0 && !DroneGridMath.CellsEqual(breadcrumbTrail[trailIndex], startCell))
        {
            trailIndex--;
        }

        if (trailIndex < 0)
        {
            trailIndex = breadcrumbTrail.Count - 1;
        }

        for (int i = trailIndex - 1; i >= 0 && count < path.Length; i--)
        {
            var breadcrumb = breadcrumbTrail[i];
            if (!DroneGridMath.CellsEqual(path[count - 1], breadcrumb))
            {
                path[count++] = breadcrumb;
            }

            if (DroneGridMath.CellsEqual(breadcrumb, goalCell))
            {
                break;
            }
        }

        if (!DroneGridMath.CellsEqual(path[count - 1], goalCell) && count < path.Length)
        {
            path[count++] = goalCell;
        }

        pathCount = count;
        return count > 1 && DroneGridMath.CellsEqual(path[count - 1], goalCell);
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
