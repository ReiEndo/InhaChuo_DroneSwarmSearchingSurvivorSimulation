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
        return new Vector3(
            gridOrigin.x + cell.x * cellSize,
            worldY,
            gridOrigin.z + cell.z * cellSize
        );
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

        Vector3 center = GridToWorld(cell, gridOrigin.y + cell.y * cellSize);
        float probeRadius = Mathf.Max(0.01f, cellSize * cellProbeRadiusScale);

        if (targetLayers.value != 0
            && Physics.CheckSphere(center, probeRadius, targetLayers, QueryTriggerInteraction.Collide))
        {
            return DroneCellState.Target;
        }

        if (blockedLayers.value != 0
            && Physics.CheckSphere(center, probeRadius, blockedLayers, QueryTriggerInteraction.Collide))
        {
            return DroneCellState.Blocked;
        }

        return DroneCellState.Free;
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
