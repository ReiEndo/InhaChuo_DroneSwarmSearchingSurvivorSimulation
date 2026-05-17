using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

public sealed class DroneSwarmCommunicationHub : MonoBehaviour
{
    [Header("Participants")]
    [SerializeField] private bool autoFindNodes = true;
    [SerializeField] private List<DroneCommunicationNode> nodes = new();

    [Header("Tick")]
    [SerializeField] private float exchangeIntervalSeconds = 0.1f;

    [Header("Debug")]
    [SerializeField] private bool drawActiveLinks = true;
    [SerializeField] private Color activeLinkColor = Color.green;

    private readonly Dictionary<DirectedLinkKey, Dictionary<int, float>> sentObservationTimes = new();
    private readonly Dictionary<DirectedLinkKey, Dictionary<int, float>> sentReportTimes = new();
    private readonly List<NodePairKey> activeLinks = new();
    private float nextExchangeAt;

    public IReadOnlyList<NodePairKey> ActiveLinks => activeLinks;

    public void ResetCommunicationMemory()
    {
        sentObservationTimes.Clear();
        sentReportTimes.Clear();
        activeLinks.Clear();
    }

    private void Awake()
    {
        RefreshNodes();
    }

    private void OnValidate()
    {
        exchangeIntervalSeconds = Mathf.Max(0.01f, exchangeIntervalSeconds);
    }

    private void Update()
    {
        if (Time.time < nextExchangeAt)
        {
            return;
        }

        nextExchangeAt = Time.time + exchangeIntervalSeconds;
        TickCommunication();
    }

    public void RefreshNodes()
    {
        if (!autoFindNodes)
        {
            nodes.RemoveAll(static node => node == null);
            return;
        }

        nodes.Clear();
        nodes.AddRange(FindObjectsByType<DroneCommunicationNode>(FindObjectsInactive.Exclude));
    }

    private void TickCommunication()
    {
        if (autoFindNodes)
        {
            RefreshNodes();
        }

        activeLinks.Clear();

        for (int i = 0; i < nodes.Count; i++)
        {
            var left = nodes[i];
            if (!IsUsable(left))
            {
                continue;
            }

            for (int j = i + 1; j < nodes.Count; j++)
            {
                var right = nodes[j];
                if (!IsUsable(right) || !AreInContact(left, right))
                {
                    continue;
                }

                int leftNodeId = GetNodeRuntimeId(left);
                int rightNodeId = GetNodeRuntimeId(right);
                var leftToRight = new DirectedLinkKey(leftNodeId, rightNodeId);
                var rightToLeft = new DirectedLinkKey(rightNodeId, leftNodeId);

                ExchangeNewerThan(left.AgentState, right.AgentState, leftToRight);
                ExchangeNewerThan(right.AgentState, left.AgentState, rightToLeft);

                activeLinks.Add(NodePairKey.From(leftNodeId, rightNodeId));
            }
        }
    }

    private static bool IsUsable(DroneCommunicationNode node)
    {
        return node != null && node.AgentState != null && node.AgentState.LocalMap != null;
    }

    private static bool AreInContact(DroneCommunicationNode left, DroneCommunicationNode right)
    {
        float range = Mathf.Min(left.CommunicationRadius, right.CommunicationRadius);
        float distanceSquared = (left.transform.position - right.transform.position).sqrMagnitude;
        return distanceSquared <= range * range;
    }

    private void ExchangeNewerThan(
        DroneSwarmAgentState source,
        DroneSwarmAgentState destination,
        DirectedLinkKey linkKey
    )
    {
        ExchangeObservationsNewerThan(source, destination, linkKey);
        ExchangeReportsNewerThan(source, destination, linkKey);
    }

    private void ExchangeObservationsNewerThan(
        DroneSwarmAgentState source,
        DroneSwarmAgentState destination,
        DirectedLinkKey linkKey
    )
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
        DirectedLinkKey linkKey
    )
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
        Dictionary<DirectedLinkKey, Dictionary<int, float>> sentTimes,
        DirectedLinkKey linkKey
    )
    {
        if (!sentTimes.TryGetValue(linkKey, out var values))
        {
            values = new Dictionary<int, float>();
            sentTimes[linkKey] = values;
        }

        return values;
    }

    private void OnDrawGizmos()
    {
        if (!drawActiveLinks || nodes == null)
        {
            return;
        }

        Gizmos.color = activeLinkColor;
        foreach (var key in activeLinks)
        {
            if (TryFindNode(key.LeftNodeId, out var left) && TryFindNode(key.RightNodeId, out var right))
            {
                Gizmos.DrawLine(left.transform.position, right.transform.position);
            }
        }
    }

    private bool TryFindNode(int nodeInstanceId, out DroneCommunicationNode node)
    {
        foreach (var candidate in nodes)
        {
            if (candidate != null
                && GetNodeRuntimeId(candidate) == nodeInstanceId)
            {
                node = candidate;
                return true;
            }
        }

        node = null;
        return false;
    }

    private static int GetNodeRuntimeId(DroneCommunicationNode node)
    {
        return RuntimeHelpers.GetHashCode(node);
    }

    [Serializable]
    private readonly struct DirectedLinkKey : IEquatable<DirectedLinkKey>
    {
        private readonly int sourceNodeId;
        private readonly int destinationNodeId;

        public DirectedLinkKey(int sourceNodeId, int destinationNodeId)
        {
            this.sourceNodeId = sourceNodeId;
            this.destinationNodeId = destinationNodeId;
        }

        public bool Equals(DirectedLinkKey other)
        {
            return sourceNodeId == other.sourceNodeId
                && destinationNodeId == other.destinationNodeId;
        }

        public override bool Equals(object obj)
        {
            return obj is DirectedLinkKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(sourceNodeId, destinationNodeId);
        }
    }

    [Serializable]
    public readonly struct NodePairKey : IEquatable<NodePairKey>
    {
        public readonly int LeftNodeId;
        public readonly int RightNodeId;

        private NodePairKey(int leftNodeId, int rightNodeId)
        {
            LeftNodeId = leftNodeId;
            RightNodeId = rightNodeId;
        }

        public static NodePairKey From(int firstNodeId, int secondNodeId)
        {
            return firstNodeId <= secondNodeId
                ? new NodePairKey(firstNodeId, secondNodeId)
                : new NodePairKey(secondNodeId, firstNodeId);
        }

        public bool Equals(NodePairKey other)
        {
            return LeftNodeId == other.LeftNodeId && RightNodeId == other.RightNodeId;
        }

        public override bool Equals(object obj)
        {
            return obj is NodePairKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(LeftNodeId, RightNodeId);
        }
    }
}
