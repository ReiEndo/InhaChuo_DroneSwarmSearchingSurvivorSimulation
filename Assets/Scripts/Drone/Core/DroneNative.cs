using System;
using System.Runtime.InteropServices;

public static class DroneNative
{
#if UNITY_WEBGL && !UNITY_EDITOR
    private const string LibName = "__Internal";
#else
    private const string LibName = "drone_algo";
#endif

    [StructLayout(LayoutKind.Sequential)]
    public struct DroneVec3i
    {
        public int x;
        public int y;
        public int z;

        public DroneVec3i(int x, int y, int z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DroneVec3f
    {
        public float x;
        public float y;
        public float z;

        public DroneVec3f(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DroneNeighborState
    {
        public DroneVec3f position;
        public DroneVec3f velocity;
        public float radius;

        public DroneNeighborState(DroneVec3f position, DroneVec3f velocity, float radius)
        {
            this.position = position;
            this.velocity = velocity;
            this.radius = radius;
        }
    }

    public enum PlannerType : int
    {
        AStar = 0,
        ThetaStar = 1
    }

    public static int ToNativeCellState(DroneCellState state)
    {
        return state switch
        {
            DroneCellState.Unknown => 0,
            DroneCellState.Free => 1,
            DroneCellState.Blocked => 2,
            DroneCellState.Target => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown drone cell state.")
        };
    }

    public static int DronePlanKnownPath(
        int plannerType,
        int width,
        int height,
        int depth,
        DroneVec3i start,
        DroneVec3i goal,
        DroneVec3i[] knownCells,
        int[] knownStates,
        int knownCount,
        DroneVec3i[] outPath,
        int outCapacity
    )
    {
        ValidatePlannerInput(plannerType, width, height, depth, knownCells, knownStates, knownCount, outPath, outCapacity);

        return DronePlanKnownPathNative(
            plannerType,
            width,
            height,
            depth,
            start,
            goal,
            knownCells,
            knownStates,
            knownCount,
            outPath,
            outCapacity
        );
    }

    public static int DroneComputeLocalAvoidanceVelocity(
        DroneVec3f selfPosition,
        DroneVec3f preferredVelocity,
        float selfRadius,
        float maxSpeed,
        float timeHorizonSeconds,
        DroneNeighborState[] neighbors,
        int neighborCount,
        out DroneVec3f outVelocity
    )
    {
        ValidateAvoidanceInput(selfRadius, maxSpeed, timeHorizonSeconds, neighbors, neighborCount);

        return DroneComputeLocalAvoidanceVelocityNative(
            selfPosition,
            preferredVelocity,
            selfRadius,
            maxSpeed,
            timeHorizonSeconds,
            neighbors,
            neighborCount,
            out outVelocity
        );
    }

    private static void ValidatePlannerInput(
        int plannerType,
        int width,
        int height,
        int depth,
        DroneVec3i[] knownCells,
        int[] knownStates,
        int knownCount,
        DroneVec3i[] outPath,
        int outCapacity
    )
    {
        if (!Enum.IsDefined(typeof(PlannerType), plannerType))
        {
            throw new ArgumentOutOfRangeException(nameof(plannerType), plannerType, "Unknown planner type.");
        }

        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth must be positive.");
        if (knownCells == null) throw new ArgumentNullException(nameof(knownCells));
        if (knownStates == null) throw new ArgumentNullException(nameof(knownStates));
        if (outPath == null) throw new ArgumentNullException(nameof(outPath));
        if (knownCount < 0) throw new ArgumentOutOfRangeException(nameof(knownCount), knownCount, "Known count cannot be negative.");
        if (knownCount > knownCells.Length) throw new ArgumentException("Known count exceeds known cell array length.", nameof(knownCount));
        if (knownCount > knownStates.Length) throw new ArgumentException("Known count exceeds known state array length.", nameof(knownCount));
        if (outCapacity < 0) throw new ArgumentOutOfRangeException(nameof(outCapacity), outCapacity, "Output capacity cannot be negative.");
        if (outCapacity > outPath.Length) throw new ArgumentException("Output capacity exceeds output path array length.", nameof(outCapacity));
    }

    private static void ValidateAvoidanceInput(
        float selfRadius,
        float maxSpeed,
        float timeHorizonSeconds,
        DroneNeighborState[] neighbors,
        int neighborCount
    )
    {
        if (neighbors == null) throw new ArgumentNullException(nameof(neighbors));
        if (neighborCount < 0) throw new ArgumentOutOfRangeException(nameof(neighborCount), neighborCount, "Neighbor count cannot be negative.");
        if (neighborCount > neighbors.Length) throw new ArgumentException("Neighbor count exceeds neighbor array length.", nameof(neighborCount));
        if (!IsFinite(selfRadius) || selfRadius < 0f) throw new ArgumentOutOfRangeException(nameof(selfRadius), selfRadius, "Self radius must be finite and non-negative.");
        if (!IsFinite(maxSpeed) || maxSpeed < 0f) throw new ArgumentOutOfRangeException(nameof(maxSpeed), maxSpeed, "Max speed must be finite and non-negative.");
        if (!IsFinite(timeHorizonSeconds) || timeHorizonSeconds <= 0f) throw new ArgumentOutOfRangeException(nameof(timeHorizonSeconds), timeHorizonSeconds, "Time horizon must be finite and positive.");
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    [DllImport(LibName, EntryPoint = "DronePlanKnownPath", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DronePlanKnownPathNative(
        int plannerType,
        int width,
        int height,
        int depth,
        DroneVec3i start,
        DroneVec3i goal,
        [In] DroneVec3i[] knownCells,
        [In] int[] knownStates,
        int knownCount,
        [Out] DroneVec3i[] outPath,
        int outCapacity
    );

    [DllImport(LibName, EntryPoint = "DroneComputeLocalAvoidanceVelocity", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DroneComputeLocalAvoidanceVelocityNative(
        DroneVec3f selfPosition,
        DroneVec3f preferredVelocity,
        float selfRadius,
        float maxSpeed,
        float timeHorizonSeconds,
        [In] DroneNeighborState[] neighbors,
        int neighborCount,
        out DroneVec3f outVelocity
    );
}
