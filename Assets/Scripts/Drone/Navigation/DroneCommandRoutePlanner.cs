using System;
using UnityEngine;

[RequireComponent(typeof(DroneSwarmAgentState))]
[RequireComponent(typeof(DroneCommunicationNode))]
public sealed class DroneCommandRoutePlanner : MonoBehaviour
{
    [Header("Planning")]
    [SerializeField] private DroneDemoGridWorld world;
    [SerializeField] private DroneNative.PlannerType plannerType = DroneNative.PlannerType.AStar;
    [SerializeField] private int maxPathLength = 1024;
    [SerializeField] private float replanIntervalSeconds = 0.25f;

    [Header("Debug")]
    [SerializeField] private bool drawRoute = true;
    [SerializeField] private Color routeColor = Color.magenta;
    [SerializeField] private Color targetColor = Color.red;
    [SerializeField] private float routeHeightOffset = 0.15f;

    private DroneSwarmAgentState agentState;
    private DroneNative.DroneVec3i[] route = Array.Empty<DroneNative.DroneVec3i>();
    private DroneNative.DroneVec3i[] knownCells = Array.Empty<DroneNative.DroneVec3i>();
    private int[] knownStates = Array.Empty<int>();
    private int routeCount;
    private float nextReplanAt;
    private bool replanRequested = true;
    private bool hasTargetReport;
    private DroneTargetReport latestTargetReport;

    public bool HasTargetReport => hasTargetReport;
    public bool HasRoute => routeCount > 0;
    public int RouteCount => routeCount;
    public DroneTargetReport LatestTargetReport => latestTargetReport;
    public DroneNative.DroneVec3i[] Route => route;

    public DroneNative.PlannerType PlannerType
    {
        get => plannerType;
        set
        {
            plannerType = value;
            RequestReplan();
        }
    }

    public DroneDemoGridWorld World
    {
        get => world;
        set
        {
            world = value;
            RequestReplan();
        }
    }

    private void Awake()
    {
        agentState = GetComponent<DroneSwarmAgentState>();
        if (world == null)
        {
            world = FindAnyObjectByType<DroneDemoGridWorld>();
        }
    }

    private void OnEnable()
    {
        if (agentState != null)
        {
            agentState.LocalMapChanged += HandleLocalMapChanged;
            agentState.LocalMapReset += HandleLocalMapReset;
        }
    }

    private void OnDisable()
    {
        if (agentState != null)
        {
            agentState.LocalMapChanged -= HandleLocalMapChanged;
            agentState.LocalMapReset -= HandleLocalMapReset;
        }
    }

    private void Start()
    {
        ConfigureCommandMap();
        RequestReplan();
    }

    private void OnValidate()
    {
        maxPathLength = Mathf.Max(2, maxPathLength);
        replanIntervalSeconds = Mathf.Max(0.05f, replanIntervalSeconds);
        routeHeightOffset = Mathf.Max(0f, routeHeightOffset);
    }

    private void Update()
    {
        if (world == null)
        {
            world = FindAnyObjectByType<DroneDemoGridWorld>();
            if (world == null)
            {
                return;
            }
        }

        ConfigureCommandMap();

        if (replanRequested && Time.time >= nextReplanAt)
        {
            PlanCommandRoute();
        }
    }

    public void RequestReplan()
    {
        replanRequested = true;
    }

    [ContextMenu("Plan Command Route Now")]
    public void PlanCommandRoute()
    {
        replanRequested = false;
        nextReplanAt = Time.time + replanIntervalSeconds;

        if (world == null || agentState == null || agentState.LocalMap == null)
        {
            ClearRoute();
            return;
        }

        if (!agentState.LocalMap.TryGetLatestTargetReport(out latestTargetReport))
        {
            hasTargetReport = false;
            ClearRoute();
            return;
        }

        hasTargetReport = true;
        var startCell = world.WorldToGrid(transform.position);
        float timestamp = Mathf.Max(Time.time, 0.0001f);
        agentState.ObserveCell(startCell, DroneCellState.Free, timestamp);

        if (route == null || route.Length != maxPathLength)
        {
            route = new DroneNative.DroneVec3i[maxPathLength];
        }

        var snapshot = CreateReusablePlannerInputSnapshot();
        routeCount = DroneNative.DronePlanKnownPath(
            (int)plannerType,
            world.Width,
            world.Height,
            world.Depth,
            startCell,
            latestTargetReport.Cell,
            snapshot.KnownCells,
            snapshot.KnownStates,
            snapshot.KnownCount,
            route,
            route.Length
        );

        if (routeCount <= 0)
        {
            routeCount = 0;
        }
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

    private void ConfigureCommandMap()
    {
        if (world == null || agentState == null)
        {
            return;
        }

        if (agentState.LocalMap == null
            || agentState.LocalMap.Width != world.Width
            || agentState.LocalMap.Height != world.Height
            || agentState.LocalMap.Depth != world.Depth)
        {
            agentState.ConfigureMap(world.Width, world.Height, world.Depth);
        }
    }

    private void HandleLocalMapChanged(DroneSwarmAgentState changedAgent)
    {
        RequestReplan();
    }

    private void HandleLocalMapReset(DroneSwarmAgentState changedAgent)
    {
        ClearRoute();
        RequestReplan();
    }

    private void ClearRoute()
    {
        routeCount = 0;
    }

    private void OnDrawGizmos()
    {
        if (!drawRoute || world == null)
        {
            return;
        }

        float y = transform.position.y + routeHeightOffset;

        if (hasTargetReport)
        {
            Gizmos.color = targetColor;
            Gizmos.DrawWireSphere(
                world.GridToWorld(latestTargetReport.Cell, y),
                world.CellSize * 0.4f
            );
        }

        if (route == null || routeCount <= 0)
        {
            return;
        }

        Gizmos.color = routeColor;
        int usableCount = Mathf.Min(routeCount, route.Length);
        for (int i = 0; i < usableCount - 1; i++)
        {
            Gizmos.DrawLine(
                world.GridToWorld(route[i], y),
                world.GridToWorld(route[i + 1], y)
            );
        }
    }
}
