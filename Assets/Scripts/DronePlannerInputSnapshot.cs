using System;

[Serializable]
public struct DronePlannerInputSnapshot
{
    public DroneNative.DroneVec3i[] KnownCells;
    public int[] KnownStates;
    public int KnownCount;

    public DronePlannerInputSnapshot(
        DroneNative.DroneVec3i[] knownCells,
        int[] knownStates,
        int knownCount
    )
    {
        KnownCells = knownCells;
        KnownStates = knownStates;
        KnownCount = knownCount;
    }
}
