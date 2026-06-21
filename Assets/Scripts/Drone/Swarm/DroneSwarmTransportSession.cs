using System.Collections.Generic;

public sealed class DroneSwarmTransportSession
{
    private readonly Dictionary<DroneSwarmDirectedLinkKey, Dictionary<int, float>> sentObservationTimes = new();
    private readonly Dictionary<DroneSwarmDirectedLinkKey, Dictionary<int, float>> sentReportTimes = new();
    private readonly List<DroneSwarmNodePairKey> activeLinks = new();

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

        foreach (var observation in source.LocalMap.KnownObservations)
        {
            int cellIndex = source.LocalMap.GridIndex(observation.Cell);
            if (sentByCell.TryGetValue(cellIndex, out float sentAt)
                && observation.ObservedAt <= sentAt)
            {
                continue;
            }

            destination.ObserveCell(observation.Cell, observation.State, observation.ObservedAt);
            sentByCell[cellIndex] = observation.ObservedAt;
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
