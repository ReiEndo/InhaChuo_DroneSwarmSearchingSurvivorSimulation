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

    public enum CellState : int
    {
        Unknown = 0,
        Free = 1,
        Blocked = 2,
        Target = 3
    }

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int DronePlanKnownPath(
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

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int DroneComputeLocalAvoidanceVelocity(
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
