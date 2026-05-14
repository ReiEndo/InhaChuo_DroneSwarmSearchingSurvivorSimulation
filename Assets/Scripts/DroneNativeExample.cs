using UnityEngine;

public sealed class DroneNativeExample : MonoBehaviour
{
    private void Start()
    {
        var start = new DroneNative.DroneVec3i(0, 0, 0);
        var goal = new DroneNative.DroneVec3i(4, 0, 4);

        var knownCells = new DroneNative.DroneVec3i[]
        {
            new(0, 0, 0),
            new(1, 0, 0),
            new(2, 0, 0),
            new(3, 0, 0),
            new(4, 0, 0),
            new(4, 0, 1),
            new(4, 0, 2),
            new(4, 0, 3),
            new(4, 0, 4),
        };

        var states = new int[knownCells.Length];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = (int)DroneNative.CellState.Free;
        }

        states[^1] = (int)DroneNative.CellState.Target;

        var pathBuffer = new DroneNative.DroneVec3i[128];

        int count = DroneNative.DronePlanKnownPath(
            (int)DroneNative.PlannerType.AStar,
            10, 1, 10,
            start,
            goal,
            knownCells,
            states,
            knownCells.Length,
            pathBuffer,
            pathBuffer.Length
        );

        Debug.Log($"Drone native path count: {count}");

        for (int i = 0; i < count; i++)
        {
            Debug.Log($"Drone native path[{i}]: {pathBuffer[i].x}, {pathBuffer[i].y}, {pathBuffer[i].z}");
        }
    }
}
