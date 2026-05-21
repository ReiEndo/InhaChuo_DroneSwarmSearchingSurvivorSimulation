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

    private DroneSwarmMapTileRenderer tileRenderer;

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
        RebuildRuntimeTiles();
    }

    public void SetMapViewMode(MapViewMode newMapViewMode)
    {
        mapViewMode = newMapViewMode;
    }

    public void CycleMapViewMode()
    {
        mapViewMode = mapViewMode switch
        {
            MapViewMode.Merged => MapViewMode.Command,
            MapViewMode.Command => MapViewMode.SelectedDrone,
            _ => MapViewMode.Merged,
        };
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
    }

    private void Awake()
    {
        tileRenderer = new DroneSwarmMapTileRenderer(transform);
    }

    private void OnDestroy()
    {
        tileRenderer?.Clear();
    }

    private void LateUpdate()
    {
        tileRenderer?.Update(world, buildRuntimeTiles, drawUnknownCells, GetBestKnownState, GetCellColor);
    }

    private void OnDrawGizmos()
    {
        if (world == null)
        {
            return;
        }

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
    }

    private DroneCellState GetBestKnownState(DroneNative.DroneVec3i cell)
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

        foreach (var explorer in explorers)
        {
            if (explorer == null
                || !explorer.TryGetComponent<DroneSwarmAgentState>(out var agentState)
                || agentState.LocalMap == null)
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
        var explorer = explorers[index];
        if (explorer == null
            || !explorer.TryGetComponent<DroneSwarmAgentState>(out var agentState)
            || agentState.LocalMap == null)
        {
            return DroneCellState.Unknown;
        }

        return agentState.LocalMap.GetState(cell);
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
