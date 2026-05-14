using System;
using UnityEngine;

public sealed class DroneNativePathFollower : MonoBehaviour
{
    [Header("Grid")]
    [SerializeField] private Vector3 gridOrigin = Vector3.zero;
    [SerializeField] private float cellSize = 1f;
    [SerializeField] private int width = 10;
    [SerializeField] private int height = 1;
    [SerializeField] private int depth = 10;

    [Header("Route")]
    [SerializeField] private Transform target;
    [SerializeField] private int maxPathLength = 512;
    [SerializeField] private bool planOnStart = true;

    [Header("Movement")]
    [SerializeField] private bool followPath = true;
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float arriveDistance = 0.05f;

    private DroneNative.DroneVec3i[] path = Array.Empty<DroneNative.DroneVec3i>();
    private int pathCount;
    private int usablePathCount;
    private int pathIndex;

    private void Start()
    {
        if (planOnStart)
        {
            PlanPath();
        }
    }

    private void Update()
    {
        if (!followPath || usablePathCount <= 0 || pathIndex >= usablePathCount)
        {
            return;
        }

        Vector3 destination = GridToWorld(path[pathIndex]);
        transform.position = Vector3.MoveTowards(
            transform.position,
            destination,
            moveSpeed * Time.deltaTime
        );

        if (Vector3.Distance(transform.position, destination) <= arriveDistance)
        {
            pathIndex++;
        }
    }

    [ContextMenu("Plan Path")]
    public void PlanPath()
    {
        if (target == null)
        {
            Debug.LogError("DroneNativePathFollower needs a target Transform.", this);
            return;
        }

        var startCell = WorldToGrid(transform.position);
        var goalCell = WorldToGrid(target.position);

        if (!IsInBounds(startCell) || !IsInBounds(goalCell))
        {
            Debug.LogError($"Start {Format(startCell)} or goal {Format(goalCell)} is outside the grid.", this);
            return;
        }

        var knownCells = BuildAllFreeCells(goalCell);
        var states = new int[knownCells.Length];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = (int)DroneNative.CellState.Free;
        }
        states[GridIndex(goalCell)] = (int)DroneNative.CellState.Target;

        path = new DroneNative.DroneVec3i[maxPathLength];
        pathCount = DroneNative.DronePlanKnownPath(
            (int)DroneNative.PlannerType.AStar,
            width,
            height,
            depth,
            startCell,
            goalCell,
            knownCells,
            states,
            knownCells.Length,
            path,
            path.Length
        );

        if (pathCount < 0)
        {
            usablePathCount = 0;
            pathIndex = 0;
            Debug.LogError($"Drone native planner failed with code {pathCount}.", this);
            return;
        }

        if (pathCount == 0)
        {
            usablePathCount = 0;
            pathIndex = 0;
            Debug.LogWarning($"Drone native found no path from {Format(startCell)} to {Format(goalCell)}. Check grid origin/size and known free cells.", this);
            return;
        }

        usablePathCount = Mathf.Min(pathCount, path.Length);
        pathIndex = usablePathCount > 1 ? 1 : 0; // skip current cell when moving

        if (pathCount > path.Length)
        {
            Debug.LogWarning($"Drone native path has {pathCount} cells, but buffer only holds {path.Length}. Increase Max Path Length.", this);
        }

        Debug.Log($"Drone native planned {pathCount} cells from {Format(startCell)} to {Format(goalCell)}.", this);
    }

    private DroneNative.DroneVec3i[] BuildAllFreeCells(DroneNative.DroneVec3i goalCell)
    {
        var cells = new DroneNative.DroneVec3i[width * height * depth];
        int index = 0;
        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    cells[index++] = new DroneNative.DroneVec3i(x, y, z);
                }
            }
        }
        return cells;
    }

    private DroneNative.DroneVec3i WorldToGrid(Vector3 world)
    {
        Vector3 local = world - gridOrigin;
        return new DroneNative.DroneVec3i(
            Mathf.RoundToInt(local.x / cellSize),
            0,
            Mathf.RoundToInt(local.z / cellSize)
        );
    }

    private Vector3 GridToWorld(DroneNative.DroneVec3i cell)
    {
        return new Vector3(
            gridOrigin.x + cell.x * cellSize,
            transform.position.y,
            gridOrigin.z + cell.z * cellSize
        );
    }

    private bool IsInBounds(DroneNative.DroneVec3i cell)
    {
        return cell.x >= 0 && cell.x < width
            && cell.y >= 0 && cell.y < height
            && cell.z >= 0 && cell.z < depth;
    }

    private int GridIndex(DroneNative.DroneVec3i cell)
    {
        return cell.x + width * (cell.y + height * cell.z);
    }

    private static string Format(DroneNative.DroneVec3i cell)
    {
        return $"({cell.x}, {cell.y}, {cell.z})";
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(
            gridOrigin + new Vector3((width - 1) * cellSize, 0f, (depth - 1) * cellSize) * 0.5f,
            new Vector3(width * cellSize, 0.1f, depth * cellSize)
        );

        if (path == null || usablePathCount <= 0)
        {
            return;
        }

        Gizmos.color = Color.yellow;
        for (int i = 0; i < usablePathCount - 1; i++)
        {
            Gizmos.DrawLine(GridToWorld(path[i]), GridToWorld(path[i + 1]));
        }
    }
}
