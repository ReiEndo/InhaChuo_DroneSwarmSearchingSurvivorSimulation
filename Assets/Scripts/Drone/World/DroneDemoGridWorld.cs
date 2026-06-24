using System.Collections.Generic;
using UnityEngine;

public sealed class DroneDemoGridWorld : MonoBehaviour
{
    [Header("Grid")]
    [SerializeField] private Vector3 gridOrigin = Vector3.zero;
    [SerializeField] private float cellSize = 1f;
    [SerializeField] private int width = 10;
    [SerializeField] private int height = 1;
    [SerializeField] private int depth = 10;

    [Header("Sensing")]
    [SerializeField] private LayerMask blockedLayers = 0;
    [SerializeField] private LayerMask targetLayers = 0;
    [SerializeField] private float cellProbeRadiusScale = 0.35f;

    private readonly HashSet<int> terrainTreeBlockedCells = new();
    private Collider[] targetOverlapBuffer = new Collider[8];
    private Terrain surfaceTerrain;

    public Vector3 GridOrigin => gridOrigin;
    public float CellSize => cellSize;
    public int Width => width;
    public int Height => height;
    public int Depth => depth;

    public void Configure(
        Vector3 newGridOrigin,
        float newCellSize,
        int newWidth,
        int newHeight,
        int newDepth,
        LayerMask newBlockedLayers,
        LayerMask newTargetLayers
    )
    {
        gridOrigin = newGridOrigin;
        cellSize = Mathf.Max(0.01f, newCellSize);
        width = Mathf.Max(1, newWidth);
        height = Mathf.Max(1, newHeight);
        depth = Mathf.Max(1, newDepth);
        blockedLayers = newBlockedLayers;
        targetLayers = newTargetLayers;
        terrainTreeBlockedCells.Clear();
    }

    public void SetTerrainTreeAvoidance(Terrain terrain, float treeRadiusCells = 1f)
    {
        surfaceTerrain = terrain;
        terrainTreeBlockedCells.Clear();
        if (terrain == null || terrain.terrainData == null)
        {
            return;
        }

        TerrainData terrainData = terrain.terrainData;
        Vector3 terrainOrigin = terrain.transform.position;
        float radius = Mathf.Max(0f, treeRadiusCells) * cellSize;
        int radiusCells = Mathf.CeilToInt(radius / cellSize);

        foreach (TreeInstance tree in terrainData.treeInstances)
        {
            Vector3 treeWorld = terrainOrigin + Vector3.Scale(tree.position, terrainData.size);
            var centerCell = WorldToGrid(treeWorld);
            for (int dz = -radiusCells; dz <= radiusCells; dz++)
            {
                for (int dx = -radiusCells; dx <= radiusCells; dx++)
                {
                    var cell = new DroneNative.DroneVec3i(centerCell.x + dx, 0, centerCell.z + dz);
                    if (!IsInBounds(cell))
                    {
                        continue;
                    }

                    Vector3 cellWorld = GridToWorld(cell, treeWorld.y);
                    var delta = new Vector2(cellWorld.x - treeWorld.x, cellWorld.z - treeWorld.z);
                    if (delta.sqrMagnitude <= radius * radius + 0.0001f)
                    {
                        terrainTreeBlockedCells.Add(CellKey(cell));
                    }
                }
            }
        }
    }

    private void OnValidate()
    {
        cellSize = Mathf.Max(0.01f, cellSize);
        width = Mathf.Max(1, width);
        height = Mathf.Max(1, height);
        depth = Mathf.Max(1, depth);
        cellProbeRadiusScale = Mathf.Clamp01(cellProbeRadiusScale);
    }

    public DroneNative.DroneVec3i WorldToGrid(Vector3 world)
    {
        Vector3 local = world - gridOrigin;
        return new DroneNative.DroneVec3i(
            Mathf.RoundToInt(local.x / cellSize),
            Mathf.Clamp(Mathf.RoundToInt(local.y / cellSize), 0, height - 1),
            Mathf.RoundToInt(local.z / cellSize)
        );
    }

    public Vector3 GridToWorld(DroneNative.DroneVec3i cell, float worldY)
    {
        float x = gridOrigin.x + cell.x * cellSize;
        float z = gridOrigin.z + cell.z * cellSize;
        float y = worldY;
        if (surfaceTerrain != null && surfaceTerrain.terrainData != null)
        {
            y = surfaceTerrain.transform.position.y + surfaceTerrain.SampleHeight(new Vector3(x, 0f, z)) + worldY;
        }

        return new Vector3(x, y, z);
    }

    public bool IsInBounds(DroneNative.DroneVec3i cell)
    {
        return cell.x >= 0 && cell.x < width
            && cell.y >= 0 && cell.y < height
            && cell.z >= 0 && cell.z < depth;
    }

    public DroneCellState SenseCell(DroneNative.DroneVec3i cell)
    {
        if (!IsInBounds(cell))
        {
            return DroneCellState.Unknown;
        }

        Vector3 center = GridToWorld(cell, cell.y * cellSize + 0.9f);
        float probeRadius = Mathf.Max(0.01f, cellSize * cellProbeRadiusScale);
        float targetProbeRadius = Mathf.Max(probeRadius, 1.25f);

        if (targetLayers.value != 0)
        {
            int hitCount = Physics.OverlapSphereNonAlloc(
                center,
                targetProbeRadius,
                targetOverlapBuffer,
                targetLayers,
                QueryTriggerInteraction.Collide
            );

            if (hitCount == targetOverlapBuffer.Length)
            {
                foreach (Collider hit in Physics.OverlapSphere(center, targetProbeRadius, targetLayers, QueryTriggerInteraction.Collide))
                {
                    if (IsTargetInCell(hit, cell))
                    {
                        return DroneCellState.Target;
                    }
                }
            }
            else
            {
                for (int i = 0; i < hitCount; i++)
                {
                    if (IsTargetInCell(targetOverlapBuffer[i], cell))
                    {
                        return DroneCellState.Target;
                    }
                }
            }
        }

        center = GridToWorld(cell, cell.y * cellSize + 0.35f);

        if (targetLayers.value != 0
            && Physics.CheckSphere(center, probeRadius, targetLayers, QueryTriggerInteraction.Collide))
        {
            return DroneCellState.Target;
        }

        if (terrainTreeBlockedCells.Contains(CellKey(cell)))
        {
            return DroneCellState.Blocked;
        }

        if (blockedLayers.value != 0
            && Physics.CheckSphere(center, probeRadius, blockedLayers, QueryTriggerInteraction.Collide))
        {
            return DroneCellState.Blocked;
        }

        return DroneCellState.Free;
    }

    private int CellKey(DroneNative.DroneVec3i cell) => (cell.y * depth + cell.z) * width + cell.x;

    private bool IsTargetInCell(Collider hit, DroneNative.DroneVec3i cell)
    {
        var hitCell = WorldToGrid(hit.transform.position);
        return hitCell.x == cell.x && hitCell.z == cell.z;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(
            gridOrigin + new Vector3((width - 1) * cellSize, (height - 1) * cellSize, (depth - 1) * cellSize) * 0.5f,
            new Vector3(width * cellSize, Mathf.Max(0.1f, height * cellSize), depth * cellSize)
        );
    }
}
