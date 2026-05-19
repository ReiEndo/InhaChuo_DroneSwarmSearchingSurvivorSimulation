using System;

[Serializable]
public struct DroneCellObservation
{
    public DroneNative.DroneVec3i Cell;
    public DroneCellState State;
    public float ObservedAt;

    public DroneCellObservation(
        DroneNative.DroneVec3i cell,
        DroneCellState state,
        float observedAt
    )
    {
        Cell = cell;
        State = state;
        ObservedAt = observedAt;
    }
}
