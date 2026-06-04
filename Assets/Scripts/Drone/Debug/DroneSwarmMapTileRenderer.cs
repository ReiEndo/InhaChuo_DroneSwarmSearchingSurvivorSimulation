using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class DroneSwarmMapTileRenderer
{
    private readonly Transform parent;
    private readonly List<Renderer> cellRenderers = new();
    private readonly List<GameObject> cellTiles = new();
    private MaterialPropertyBlock propertyBlock;

    public DroneSwarmMapTileRenderer(Transform parent)
    {
        this.parent = parent;
    }

    public void Rebuild(
        DroneDemoGridWorld world,
        bool enabled,
        float cellHeightOffset,
        float cellFillScale,
        bool drawUnknownCells,
        Func<DroneNative.DroneVec3i, DroneCellState> getCellState,
        Func<DroneCellState, Color> getCellColor)
    {
        Clear();

        if (!enabled || world == null)
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
                tile.transform.SetParent(parent, false);
                tile.transform.position = world.GridToWorld(
                    new DroneNative.DroneVec3i(x, 0, z),
                    cellHeightOffset + 0.012f
                );
                tile.transform.localScale = new Vector3(
                    world.CellSize * cellFillScale,
                    0.015f,
                    world.CellSize * cellFillScale
                );

                if (tile.TryGetComponent<Collider>(out var collider))
                {
                    UnityEngine.Object.Destroy(collider);
                }

                if (tile.TryGetComponent<Renderer>(out var renderer))
                {
                    cellRenderers.Add(renderer);
                }

                cellTiles.Add(tile);
            }
        }

        Update(world, enabled, drawUnknownCells, getCellState, getCellColor);
    }

    public void Update(
        DroneDemoGridWorld world,
        bool enabled,
        bool drawUnknownCells,
        Func<DroneNative.DroneVec3i, DroneCellState> getCellState,
        Func<DroneCellState, Color> getCellColor)
    {
        if (!enabled || world == null || cellRenderers.Count == 0)
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
                DroneCellState state = getCellState(cell);
                var renderer = cellRenderers[index++];
                renderer.enabled = state != DroneCellState.Unknown || drawUnknownCells;
                Color color = getCellColor(state);
                propertyBlock.SetColor("_BaseColor", color);
                propertyBlock.SetColor("_Color", color);
                renderer.SetPropertyBlock(propertyBlock);
            }
        }
    }

    public void Clear()
    {
        for (int i = cellTiles.Count - 1; i >= 0; i--)
        {
            if (cellTiles[i] != null)
            {
                UnityEngine.Object.Destroy(cellTiles[i]);
            }
        }

        cellTiles.Clear();
        cellRenderers.Clear();
    }
}
