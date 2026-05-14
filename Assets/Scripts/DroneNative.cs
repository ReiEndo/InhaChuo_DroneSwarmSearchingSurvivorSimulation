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

    public enum PlannerType : int
    {
        AStar = 0
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
}
