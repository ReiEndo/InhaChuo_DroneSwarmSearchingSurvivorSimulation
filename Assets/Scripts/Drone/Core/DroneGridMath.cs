public static class DroneGridMath
{
    public static bool CellsEqual(DroneNative.DroneVec3i a, DroneNative.DroneVec3i b)
    {
        return a.x == b.x && a.y == b.y && a.z == b.z;
    }

    public static float SquaredDistance(DroneNative.DroneVec3i a, DroneNative.DroneVec3i b)
    {
        float dx = (float)a.x - b.x;
        float dy = (float)a.y - b.y;
        float dz = (float)a.z - b.z;
        return dx * dx + dy * dy + dz * dz;
    }
}
