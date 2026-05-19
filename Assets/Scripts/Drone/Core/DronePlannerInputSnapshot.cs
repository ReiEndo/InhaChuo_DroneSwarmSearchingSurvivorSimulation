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
        KnownCells = knownCells != null ? (DroneNative.DroneVec3i[])knownCells.Clone() : null;
        KnownStates = knownStates != null ? (int[])knownStates.Clone() : null;
        KnownCount = knownCount;
    }
}
