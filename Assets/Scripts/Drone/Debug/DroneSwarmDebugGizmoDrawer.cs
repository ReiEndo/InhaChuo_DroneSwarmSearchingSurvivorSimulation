using System;
using System.Collections.Generic;
using UnityEngine;

public static class DroneSwarmDebugGizmoDrawer
{
    private static readonly DroneNative.DroneVec3i[] NeighborOffsets =
    {
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 0, 1),
        new(0, 0, -1),
    };

    public static void DrawKnownCells(
        DroneDemoGridWorld world,
        bool drawUnknownCells,
        bool drawFrontiers,
        float cellHeightOffset,
        float cellFillScale,
        Color frontierColor,
        Func<DroneNative.DroneVec3i, DroneCellState> getCellState,
        Func<DroneCellState, Color> getCellColor)
    {
        if (world == null)
        {
            return;
        }

        for (int z = 0; z < world.Depth; z++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var cell = new DroneNative.DroneVec3i(x, 0, z);
                DroneCellState state = getCellState(cell);
                if (state == DroneCellState.Unknown && !drawUnknownCells)
                {
                    continue;
                }

                Vector3 center = world.GridToWorld(cell, world.GridOrigin.y + cellHeightOffset);
                Vector3 size = new(world.CellSize * cellFillScale, 0.02f, world.CellSize * cellFillScale);
                Gizmos.color = getCellColor(state);
                Gizmos.DrawCube(center, size);

                if (drawFrontiers && IsFrontier(world, cell, state, getCellState))
                {
                    Gizmos.color = frontierColor;
                    Gizmos.DrawWireCube(center + Vector3.up * 0.04f, size);
                }
            }
        }
    }

    public static void DrawDronePaths(DroneDemoGridWorld world, List<DroneFrontierExplorer> explorers, Color dronePathColor)
    {
        if (world == null || explorers == null)
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

    private static bool IsFrontier(
        DroneDemoGridWorld world,
        DroneNative.DroneVec3i cell,
        DroneCellState state,
        Func<DroneNative.DroneVec3i, DroneCellState> getCellState)
    {
        if (state != DroneCellState.Free && state != DroneCellState.Target)
        {
            return false;
        }

        foreach (var offset in NeighborOffsets)
        {
            var neighbor = new DroneNative.DroneVec3i(cell.x + offset.x, cell.y, cell.z + offset.z);
            if (world.IsInBounds(neighbor) && getCellState(neighbor) == DroneCellState.Unknown)
            {
                return true;
            }
        }

        return false;
    }
}
