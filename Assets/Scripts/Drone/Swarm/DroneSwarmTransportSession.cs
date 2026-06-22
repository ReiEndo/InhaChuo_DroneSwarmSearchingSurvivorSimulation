using System.Collections.Generic;

public sealed class DroneSwarmTransportSession
{
    private readonly Dictionary<DroneSwarmDirectedLinkKey, Dictionary<int, float>> sentObservationTimes = new();
    private readonly Dictionary<DroneSwarmDirectedLinkKey, Dictionary<int, float>> sentReportTimes = new();
    private readonly List<DroneSwarmNodePairKey> activeLinks = new();
    private DroneNative.DroneVec3i[] knownCellsBuffer = System.Array.Empty<DroneNative.DroneVec3i>();
    private int[] knownStatesBuffer = System.Array.Empty<int>();
    private float[] knownObservedAtBuffer = System.Array.Empty<float>();

    public IReadOnlyList<DroneSwarmNodePairKey> ActiveLinks => activeLinks;

    public void Reset()
    {
        sentObservationTimes.Clear();
        sentReportTimes.Clear();
        activeLinks.Clear();
    }

    public void BeginTick()
    {
        activeLinks.Clear();
    }

    public void ExchangeBidirectional(
        DroneCommunicationNode left,
        DroneCommunicationNode right,
        int leftNodeId,
        int rightNodeId)
    {
        ExchangeNewerThan(
            left.AgentState,
            right.AgentState,
            new DroneSwarmDirectedLinkKey(leftNodeId, rightNodeId));
        ExchangeNewerThan(
            right.AgentState,
            left.AgentState,
            new DroneSwarmDirectedLinkKey(rightNodeId, leftNodeId));

        activeLinks.Add(DroneSwarmNodePairKey.From(leftNodeId, rightNodeId));
    }

    private void ExchangeNewerThan(
        DroneSwarmAgentState source,
        DroneSwarmAgentState destination,
        DroneSwarmDirectedLinkKey linkKey)
    {
        ExchangeObservationsNewerThan(source, destination, linkKey);
        ExchangeReportsNewerThan(source, destination, linkKey);
        destination.MergeTargetInformedDronesFrom(source);
    }

    private void ExchangeObservationsNewerThan(
        DroneSwarmAgentState source,
        DroneSwarmAgentState destination,
        DroneSwarmDirectedLinkKey linkKey)
    {
        var sentByCell = GetOrCreateSentTimes(sentObservationTimes, linkKey);
        DroneLocalMap sourceMap = source.LocalMap;

        EnsureKnownObservationBuffers(sourceMap.CellCount);
        int knownCount = sourceMap.BuildKnownObservationArrays(knownCellsBuffer, knownStatesBuffer, knownObservedAtBuffer);

        for (int i = 0; i < knownCount; i++)
        {
            var cell = knownCellsBuffer[i];
            int cellIndex = sourceMap.GridIndex(cell);
            float observedAt = knownObservedAtBuffer[i];

            if (sentByCell.TryGetValue(cellIndex, out float sentAt)
                && observedAt <= sentAt)
            {
                continue;
            }

            DroneCellState state = sourceMap.GetState(cell);
            destination.ObserveCell(cell, state, observedAt);
            sentByCell[cellIndex] = observedAt;
        }
    }

    private void EnsureKnownObservationBuffers(int requiredCapacity)
    {
        if (knownCellsBuffer.Length < requiredCapacity)
        {
            knownCellsBuffer = new DroneNative.DroneVec3i[requiredCapacity];
            knownStatesBuffer = new int[requiredCapacity];
            knownObservedAtBuffer = new float[requiredCapacity];
        }
    }

    private void ExchangeReportsNewerThan(
        DroneSwarmAgentState source,
        DroneSwarmAgentState destination,
        DroneSwarmDirectedLinkKey linkKey)
    {
        var sentByReporter = GetOrCreateSentTimes(sentReportTimes, linkKey);

        foreach (var report in source.LocalMap.TargetReports)
        {
            if (sentByReporter.TryGetValue(report.ReporterId, out float sentAt)
                && report.ObservedAt <= sentAt)
            {
                continue;
            }

            destination.ObserveTarget(report.Cell, report.ObservedAt, report.ReporterId);
            sentByReporter[report.ReporterId] = report.ObservedAt;
        }
    }

    private static Dictionary<int, float> GetOrCreateSentTimes(
        Dictionary<DroneSwarmDirectedLinkKey, Dictionary<int, float>> sentTimes,
        DroneSwarmDirectedLinkKey linkKey)
    {
        if (!sentTimes.TryGetValue(linkKey, out var values))
        {
            values = new Dictionary<int, float>();
            sentTimes[linkKey] = values;
        }

        return values;
    }
}
