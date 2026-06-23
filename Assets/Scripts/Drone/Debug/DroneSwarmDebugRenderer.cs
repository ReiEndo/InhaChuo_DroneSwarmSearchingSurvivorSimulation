using System.Collections.Generic;
using UnityEngine;

public sealed class DroneSwarmDebugRenderer : MonoBehaviour
{
    public enum MapViewMode
    {
        Merged,
        Command,
        SelectedDrone,
    }

    [Header("Sources")]
    [SerializeField] private DroneDemoGridWorld world;
    [SerializeField] private DroneSwarmAgentState commandState;
    [SerializeField] private List<DroneFrontierExplorer> explorers = new();
    [SerializeField] private MapViewMode mapViewMode = MapViewMode.Merged;
    [SerializeField] private int selectedDroneIndex;

    [Header("Cells")]
    [SerializeField] private bool drawCells = true;
    [SerializeField] private bool buildRuntimeTiles = true;
    [SerializeField] private bool drawUnknownCells = true;
    [SerializeField] private bool drawFrontiers = true;
    [SerializeField] private float cellHeightOffset = 0.02f;
    [SerializeField] private float cellFillScale = 0.86f;
    [SerializeField] private Color unknownColor = new(0.12f, 0.12f, 0.12f, 0.12f);
    [SerializeField] private Color freeColor = new(0.15f, 0.65f, 0.95f, 0.22f);
    [SerializeField] private Color blockedColor = new(0.95f, 0.18f, 0.12f, 0.42f);
    [SerializeField] private Color targetColor = new(1f, 0.85f, 0.12f, 0.6f);
    [SerializeField] private Color frontierColor = new(0.25f, 1f, 0.25f, 0.75f);
    [SerializeField] private Color dronePathColor = new(1f, 1f, 0.2f, 0.9f);
    [SerializeField] private bool buildRuntimeReturnPath = true;
    [SerializeField] private Color returnPathColor = new(0.1f, 1f, 0.25f, 0.92f);
    [SerializeField] private float returnPathHeightOffset = 0.16f;
    [SerializeField] private float returnPathWidthScale = 0.28f;

    private readonly List<DroneSwarmAgentState> explorerStates = new();
    private DroneCellState[] mapStateCache = System.Array.Empty<DroneCellState>();
    private int cachedWidth;
    private int cachedDepth;
    private bool mapStateCacheDirty = true;
    private bool mapStateCacheValid;
    private bool runtimeTilesDirty = true;
    private bool lastBuildRuntimeTiles;
    private bool lastDrawUnknownCells;
    private DroneSwarmMapTileRenderer tileRenderer;
    private DroneSwarmPathMeshRenderer returnPathRenderer;

    public MapViewMode CurrentMapViewMode => mapViewMode;
    public int SelectedDroneIndex => selectedDroneIndex;

    public string CurrentMapViewLabel
    {
        get
        {
            if (mapViewMode == MapViewMode.SelectedDrone)
            {
                return explorers != null && explorers.Count > 0
                    ? $"Drone {Mathf.Clamp(selectedDroneIndex, 0, explorers.Count - 1) + 1:00}"
                    : "Drone --";
            }

            return mapViewMode.ToString();
        }
    }

    public void Configure(
        DroneDemoGridWorld newWorld,
        DroneSwarmAgentState newCommandState,
        List<DroneFrontierExplorer> newExplorers
    )
    {
        world = newWorld;
        commandState = newCommandState;
        explorers = newExplorers ?? new List<DroneFrontierExplorer>();
        CacheExplorerStates();
        MarkMapStateCacheDirty();
        RebuildRuntimeTiles();
    }

    public void SetMapViewMode(MapViewMode newMapViewMode)
    {
        if (mapViewMode == newMapViewMode)
        {
            return;
        }

        mapViewMode = newMapViewMode;
        MarkMapStateCacheDirty();
    }

    public void CycleMapViewMode()
    {
        mapViewMode = mapViewMode switch
        {
            MapViewMode.Merged => MapViewMode.Command,
            MapViewMode.Command => MapViewMode.SelectedDrone,
            _ => MapViewMode.Merged,
        };
        MarkMapStateCacheDirty();
    }

    public void SelectNextDrone()
    {
        if (explorers == null || explorers.Count == 0)
        {
            selectedDroneIndex = 0;
            return;
        }

        selectedDroneIndex = (selectedDroneIndex + 1) % explorers.Count;
        mapViewMode = MapViewMode.SelectedDrone;
        MarkMapStateCacheDirty();
    }

    private void Awake()
    {
        tileRenderer = new DroneSwarmMapTileRenderer(transform);
        returnPathRenderer = new DroneSwarmPathMeshRenderer(transform);
    }

    private void OnDestroy()
    {
        UnsubscribeMapEvents();
        tileRenderer?.Clear();
        returnPathRenderer?.Clear();
    }

    private void LateUpdate()
    {
        bool cacheRebuilt = EnsureMapStateCache();
        bool tileSettingsChanged = lastBuildRuntimeTiles != buildRuntimeTiles
            || lastDrawUnknownCells != drawUnknownCells;

        if (cacheRebuilt || runtimeTilesDirty || tileSettingsChanged)
        {
            tileRenderer?.Update(world, buildRuntimeTiles, drawUnknownCells, GetBestKnownState, GetCellColor);
            runtimeTilesDirty = false;
            lastBuildRuntimeTiles = buildRuntimeTiles;
            lastDrawUnknownCells = drawUnknownCells;
        }

        returnPathRenderer?.Update(world, explorers, buildRuntimeReturnPath, returnPathColor, returnPathHeightOffset, returnPathWidthScale);
    }

    private void OnDrawGizmos()
    {
        if (world == null)
        {
            return;
        }

        EnsureMapStateCache();

        if (drawCells)
        {
            DroneSwarmDebugGizmoDrawer.DrawKnownCells(
                world,
                drawUnknownCells,
                drawFrontiers,
                cellHeightOffset,
                cellFillScale,
                frontierColor,
                GetBestKnownState,
                GetCellColor);
        }

        DroneSwarmDebugGizmoDrawer.DrawDronePaths(world, explorers, dronePathColor);
    }

    private void RebuildRuntimeTiles()
    {
        tileRenderer ??= new DroneSwarmMapTileRenderer(transform);
        tileRenderer.Rebuild(
            world,
            buildRuntimeTiles,
            cellHeightOffset,
            cellFillScale,
            drawUnknownCells,
            GetBestKnownState,
            GetCellColor);
        runtimeTilesDirty = false;
        lastBuildRuntimeTiles = buildRuntimeTiles;
        lastDrawUnknownCells = drawUnknownCells;
    }

    private DroneCellState GetBestKnownState(DroneNative.DroneVec3i cell)
    {
        if (mapStateCacheValid
            && cell.x >= 0 && cell.x < cachedWidth
            && cell.z >= 0 && cell.z < cachedDepth)
        {
            return mapStateCache[cell.z * cachedWidth + cell.x];
        }

        return ResolveBestKnownState(cell);
    }

    private DroneCellState ResolveBestKnownState(DroneNative.DroneVec3i cell)
    {
        if (mapViewMode == MapViewMode.Command)
        {
            return commandState != null && commandState.LocalMap != null
                ? commandState.LocalMap.GetState(cell)
                : DroneCellState.Unknown;
        }

        if (mapViewMode == MapViewMode.SelectedDrone)
        {
            return GetSelectedDroneState(cell);
        }

        DroneCellState state = commandState != null && commandState.LocalMap != null
            ? commandState.LocalMap.GetState(cell)
            : DroneCellState.Unknown;

        if (state != DroneCellState.Unknown || explorers == null)
        {
            return state;
        }

        for (int i = 0; i < explorerStates.Count; i++)
        {
            var agentState = explorerStates[i];
            if (agentState == null || agentState.LocalMap == null)
            {
                continue;
            }

            state = agentState.LocalMap.GetState(cell);
            if (state != DroneCellState.Unknown)
            {
                return state;
            }
        }

        return DroneCellState.Unknown;
    }

    private DroneCellState GetSelectedDroneState(DroneNative.DroneVec3i cell)
    {
        if (explorers == null || explorers.Count == 0)
        {
            return DroneCellState.Unknown;
        }

        int index = Mathf.Clamp(selectedDroneIndex, 0, explorers.Count - 1);
        if (index >= explorerStates.Count)
        {
            return DroneCellState.Unknown;
        }

        var agentState = explorerStates[index];
        return agentState != null && agentState.LocalMap != null
            ? agentState.LocalMap.GetState(cell)
            : DroneCellState.Unknown;
    }

    private void CacheExplorerStates()
    {
        UnsubscribeMapEvents();
        explorerStates.Clear();
        if (explorers != null)
        {
            foreach (var explorer in explorers)
            {
                explorerStates.Add(
                    explorer != null && explorer.TryGetComponent<DroneSwarmAgentState>(out var agentState)
                        ? agentState
                        : null);
            }
        }

        SubscribeMapEvents();
    }

    private bool EnsureMapStateCache()
    {
        if (world == null)
        {
            mapStateCacheValid = false;
            return false;
        }

        int requiredCount = world.Width * world.Depth;
        if (mapStateCache.Length != requiredCount || cachedWidth != world.Width || cachedDepth != world.Depth)
        {
            mapStateCache = new DroneCellState[requiredCount];
            cachedWidth = world.Width;
            cachedDepth = world.Depth;
            mapStateCacheDirty = true;
        }

        if (!mapStateCacheDirty && mapStateCacheValid)
        {
            return false;
        }

        for (int z = 0; z < world.Depth; z++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var cell = new DroneNative.DroneVec3i(x, 0, z);
                mapStateCache[z * world.Width + x] = ResolveBestKnownState(cell);
            }
        }

        mapStateCacheDirty = false;
        mapStateCacheValid = true;
        runtimeTilesDirty = true;
        tileRenderer?.MarkDirty();
        return true;
    }

    private void MarkMapStateCacheDirty()
    {
        mapStateCacheDirty = true;
        mapStateCacheValid = false;
        runtimeTilesDirty = true;
        tileRenderer?.MarkDirty();
    }

    private void SubscribeMapEvents()
    {
        if (commandState != null)
        {
            commandState.LocalMapChanged += HandleMapStateChanged;
            commandState.LocalMapReset += HandleMapStateChanged;
        }

        foreach (var agentState in explorerStates)
        {
            if (agentState == null)
            {
                continue;
            }

            agentState.LocalMapChanged += HandleMapStateChanged;
            agentState.LocalMapReset += HandleMapStateChanged;
        }
    }

    private void UnsubscribeMapEvents()
    {
        if (commandState != null)
        {
            commandState.LocalMapChanged -= HandleMapStateChanged;
            commandState.LocalMapReset -= HandleMapStateChanged;
        }

        foreach (var agentState in explorerStates)
        {
            if (agentState == null)
            {
                continue;
            }

            agentState.LocalMapChanged -= HandleMapStateChanged;
            agentState.LocalMapReset -= HandleMapStateChanged;
        }
    }

    private void HandleMapStateChanged(DroneSwarmAgentState changedAgent)
    {
        MarkMapStateCacheDirty();
    }

    private Color GetCellColor(DroneCellState state)
    {
        return state switch
        {
            DroneCellState.Free => freeColor,
            DroneCellState.Blocked => blockedColor,
            DroneCellState.Target => targetColor,
            _ => unknownColor,
        };
    }
}
