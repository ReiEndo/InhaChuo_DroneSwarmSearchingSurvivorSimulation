using System;

[Serializable]
public struct DroneTargetReport
{
    public DroneNative.DroneVec3i Cell;
    public float ObservedAt;
    public int ReporterId;

    public DroneTargetReport(DroneNative.DroneVec3i cell, float observedAt, int reporterId)
    {
        Cell = cell;
        ObservedAt = observedAt;
        ReporterId = reporterId;
    }
}
