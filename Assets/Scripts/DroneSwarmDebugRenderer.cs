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

    private static readonly DroneNative.DroneVec3i[] NeighborOffsets =
    {
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 0, 1),
        new(0, 0, -1),
    };

    private readonly List<Renderer> cellRenderers = new();
    private readonly List<GameObject> cellTiles = new();
    private MaterialPropertyBlock propertyBlock;

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

    private void OnDestroy()
    {
        ClearRuntimeTiles();
    }

    private void LateUpdate()
    {
        UpdateRuntimeTiles();
    }

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

    private void OnDrawGizmos()
    {
        if (world == null)
        {
            return;
        }

        if (drawCells)
        {
            DrawKnownCells();
        }

        DrawDronePaths();
    }

    private void DrawKnownCells()
    {
        for (int z = 0; z < world.Depth; z++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var cell = new DroneNative.DroneVec3i(x, 0, z);
                DroneCellState state = GetBestKnownState(cell);
                if (state == DroneCellState.Unknown && !drawUnknownCells)
                {
                    continue;
                }

                Vector3 center = world.GridToWorld(cell, world.GridOrigin.y + cellHeightOffset);
                Vector3 size = new(world.CellSize * cellFillScale, 0.02f, world.CellSize * cellFillScale);
                Gizmos.color = GetCellColor(state);
                Gizmos.DrawCube(center, size);

                if (drawFrontiers && IsFrontier(cell, state))
                {
                    Gizmos.color = frontierColor;
                    Gizmos.DrawWireCube(center + Vector3.up * 0.04f, size);
                }
            }
        }
    }

    private void RebuildRuntimeTiles()
    {
        ClearRuntimeTiles();

        if (!buildRuntimeTiles || world == null)
        {
            return;
        }

        propertyBlock ??= new MaterialPropertyBlock();

        for (int z = 0; z < world.Depth; z++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var tile = GameObject.CreatePrimitive(PrimitiveType.Cube);
                tile.name = $"Map Tile {x},{z}";
                tile.transform.SetParent(transform, false);
                tile.transform.position = world.GridToWorld(
                    new DroneNative.DroneVec3i(x, 0, z),
                    world.GridOrigin.y + cellHeightOffset + 0.012f
                );
                tile.transform.localScale = new Vector3(
                    world.CellSize * cellFillScale,
                    0.015f,
                    world.CellSize * cellFillScale
                );

                if (tile.TryGetComponent<Collider>(out var collider))
                {
                    Destroy(collider);
                }

                if (tile.TryGetComponent<Renderer>(out var renderer))
                {
                    cellRenderers.Add(renderer);
                }

                cellTiles.Add(tile);
            }
        }

        UpdateRuntimeTiles();
    }

    private void UpdateRuntimeTiles()
    {
        if (!buildRuntimeTiles || world == null || cellRenderers.Count == 0)
        {
            return;
        }

        propertyBlock ??= new MaterialPropertyBlock();

        int index = 0;
        for (int z = 0; z < world.Depth; z++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                if (index >= cellRenderers.Count)
                {
                    return;
                }

                var cell = new DroneNative.DroneVec3i(x, 0, z);
                DroneCellState state = GetBestKnownState(cell);
                var renderer = cellRenderers[index++];
                renderer.enabled = state != DroneCellState.Unknown || drawUnknownCells;
                propertyBlock.SetColor("_BaseColor", GetCellColor(state));
                propertyBlock.SetColor("_Color", GetCellColor(state));
                renderer.SetPropertyBlock(propertyBlock);
            }
        }
    }

    private void ClearRuntimeTiles()
    {
        for (int i = cellTiles.Count - 1; i >= 0; i--)
        {
            if (cellTiles[i] != null)
            {
                Destroy(cellTiles[i]);
            }
        }

        cellTiles.Clear();
        cellRenderers.Clear();
    }

    private void DrawDronePaths()
    {
        if (explorers == null)
        {
            return;
        }

        Gizmos.color = dronePathColor;
        foreach (var explorer in explorers)
        {
            if (explorer == null || explorer.Path == null || explorer.UsablePathCount <= 1)
            {
                continue;
            }

            float y = explorer.transform.position.y + 0.08f;
            int start = Mathf.Clamp(explorer.PathIndex, 0, explorer.UsablePathCount - 1);
            for (int i = start; i < explorer.UsablePathCount - 1; i++)
            {
                Gizmos.DrawLine(
                    world.GridToWorld(explorer.Path[i], y),
                    world.GridToWorld(explorer.Path[i + 1], y)
                );
            }
        }
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

    private bool IsFrontier(DroneNative.DroneVec3i cell, DroneCellState state)
    {
        if (state != DroneCellState.Free && state != DroneCellState.Target)
        {
            return false;
        }

        foreach (var offset in NeighborOffsets)
        {
            var neighbor = new DroneNative.DroneVec3i(cell.x + offset.x, cell.y, cell.z + offset.z);
            if (world.IsInBounds(neighbor) && GetBestKnownState(neighbor) == DroneCellState.Unknown)
            {
                return true;
            }
        }

        return false;
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
