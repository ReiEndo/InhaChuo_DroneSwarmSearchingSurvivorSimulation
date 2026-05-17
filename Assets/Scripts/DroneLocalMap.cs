using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public sealed class DroneLocalMap
{
    [SerializeField] private int width;
    [SerializeField] private int height;
    [SerializeField] private int depth;

    private DroneCellState[] states = Array.Empty<DroneCellState>();
    private float[] observedAt = Array.Empty<float>();
    private readonly Dictionary<int, DroneTargetReport> targetReportsByReporter = new();

    public int Width => width;
    public int Height => height;
    public int Depth => depth;
    public int CellCount => states.Length;

    public DroneLocalMap(int width, int height, int depth)
    {
        Resize(width, height, depth);
    }

    public void Resize(int newWidth, int newHeight, int newDepth)
    {
        if (newWidth <= 0 || newHeight <= 0 || newDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newWidth),
                "Drone local map dimensions must all be greater than zero."
            );
        }

        width = newWidth;
        height = newHeight;
        depth = newDepth;
        states = new DroneCellState[width * height * depth];
        observedAt = new float[states.Length];
        targetReportsByReporter.Clear();
    }

    public void Clear()
    {
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = DroneCellState.Unknown;
        }

        Array.Clear(observedAt, 0, observedAt.Length);
        targetReportsByReporter.Clear();
    }

    public bool IsInBounds(DroneNative.DroneVec3i cell)
    {
        return cell.x >= 0 && cell.x < width
            && cell.y >= 0 && cell.y < height
            && cell.z >= 0 && cell.z < depth;
    }

    public DroneCellState GetState(DroneNative.DroneVec3i cell)
    {
        return IsInBounds(cell) ? states[GridIndex(cell)] : DroneCellState.Unknown;
    }

    public float GetObservedAt(DroneNative.DroneVec3i cell)
    {
        return IsInBounds(cell) ? observedAt[GridIndex(cell)] : 0f;
    }

    public bool TrySetCell(DroneNative.DroneVec3i cell, DroneCellState state, float timestamp)
    {
        if (!IsInBounds(cell))
        {
            return false;
        }

        int index = GridIndex(cell);
        if (timestamp <= observedAt[index])
        {
            return false;
        }

        states[index] = state;
        observedAt[index] = timestamp;
        return true;
    }

    public bool TryRecordTargetReport(
        DroneNative.DroneVec3i cell,
        float timestamp,
        int reporterId
    )
    {
        if (!IsInBounds(cell))
        {
            return false;
        }

        if (targetReportsByReporter.TryGetValue(reporterId, out var existing)
            && timestamp <= existing.ObservedAt)
        {
            return false;
        }

        targetReportsByReporter[reporterId] = new DroneTargetReport(cell, timestamp, reporterId);
        TrySetCell(cell, DroneCellState.Target, timestamp);
        return true;
    }

    public IEnumerable<DroneTargetReport> TargetReports => targetReportsByReporter.Values;

    public IEnumerable<DroneCellObservation> KnownObservations
    {
        get
        {
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = GridIndex(x, y, z);
                        DroneCellState state = states[index];
                        if (state == DroneCellState.Unknown)
                        {
                            continue;
                        }

                        yield return new DroneCellObservation(
                            new DroneNative.DroneVec3i(x, y, z),
                            state,
                            observedAt[index]
                        );
                    }
                }
            }
        }
    }

    public IEnumerable<DroneCellObservation> GetObservationsNewerThan(float timestamp)
    {
        foreach (var observation in KnownObservations)
        {
            if (observation.ObservedAt > timestamp)
            {
                yield return observation;
            }
        }
    }

    public IEnumerable<DroneTargetReport> GetTargetReportsNewerThan(float timestamp)
    {
        foreach (var report in targetReportsByReporter.Values)
        {
            if (report.ObservedAt > timestamp)
            {
                yield return report;
            }
        }
    }

    public bool TryGetLatestTargetReport(out DroneTargetReport report)
    {
        report = default;
        bool found = false;

        foreach (var candidate in targetReportsByReporter.Values)
        {
            if (!found || candidate.ObservedAt > report.ObservedAt)
            {
                report = candidate;
                found = true;
            }
        }

        return found;
    }

    public int BuildKnownCellArrays(
        DroneNative.DroneVec3i[] knownCells,
        int[] knownStates,
        bool includeBlocked = true
    )
    {
        if (knownCells == null)
        {
            throw new ArgumentNullException(nameof(knownCells));
        }

        if (knownStates == null)
        {
            throw new ArgumentNullException(nameof(knownStates));
        }

        int capacity = Mathf.Min(knownCells.Length, knownStates.Length);
        int count = 0;

        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = GridIndex(x, y, z);
                    DroneCellState state = states[index];

                    if (state == DroneCellState.Unknown
                        || (!includeBlocked && state == DroneCellState.Blocked))
                    {
                        continue;
                    }

                    if (count >= capacity)
                    {
                        return count;
                    }

                    knownCells[count] = new DroneNative.DroneVec3i(x, y, z);
                    knownStates[count] = ToNativeState(state);
                    count++;
                }
            }
        }

        return count;
    }

    public DronePlannerInputSnapshot CreatePlannerInputSnapshot(bool includeBlocked = true)
    {
        int count = CountKnownCells(includeBlocked);
        var knownCells = new DroneNative.DroneVec3i[count];
        var knownStates = new int[count];
        int knownCount = BuildKnownCellArrays(knownCells, knownStates, includeBlocked);

        if (knownCount == count)
        {
            return new DronePlannerInputSnapshot(knownCells, knownStates, knownCount);
        }

        var compactCells = new DroneNative.DroneVec3i[knownCount];
        var compactStates = new int[knownCount];
        Array.Copy(knownCells, compactCells, knownCount);
        Array.Copy(knownStates, compactStates, knownCount);
        return new DronePlannerInputSnapshot(compactCells, compactStates, knownCount);
    }

    public int CountKnownCells(bool includeBlocked = true)
    {
        int count = 0;
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i] != DroneCellState.Unknown
                && (includeBlocked || states[i] != DroneCellState.Blocked))
            {
                count++;
            }
        }

        return count;
    }

    public int GridIndex(DroneNative.DroneVec3i cell)
    {
        return GridIndex(cell.x, cell.y, cell.z);
    }

    public int GridIndex(int x, int y, int z)
    {
        return x + width * (y + height * z);
    }

    public static int ToNativeState(DroneCellState state)
    {
        return state switch
        {
            DroneCellState.Free => (int)DroneNative.CellState.Free,
            DroneCellState.Blocked => (int)DroneNative.CellState.Blocked,
            DroneCellState.Target => (int)DroneNative.CellState.Target,
            _ => (int)DroneNative.CellState.Unknown,
        };
    }
}
