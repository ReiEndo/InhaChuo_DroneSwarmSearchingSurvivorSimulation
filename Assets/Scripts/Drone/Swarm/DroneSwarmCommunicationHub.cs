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

    [Header("Node Discovery")]
    [Tooltip("When auto-find is on, re-scan the scene for communication nodes at this interval instead of every tick. FindObjectsByType is expensive, so nodes are cached between scans and refreshed immediately if a cached node is destroyed.")]
    [SerializeField] private float autoFindRefreshIntervalSeconds = 2f;

    [Header("Debug")]
    [SerializeField] private bool drawActiveLinks = true;
    [SerializeField] private Color activeLinkColor = Color.green;

    private readonly DroneSwarmTransportSession transportSession = new();
    private float nextExchangeAt;
    private float nextAutoFindRefreshAt = -1f;

    public IReadOnlyList<DroneSwarmNodePairKey> ActiveLinks => transportSession.ActiveLinks;

    public void ResetCommunicationMemory()
    {
        transportSession.Reset();
    }

    private void Awake()
    {
        RefreshNodes();
    }

    private void OnValidate()
    {
        exchangeIntervalSeconds = Mathf.Max(0.01f, exchangeIntervalSeconds);
        autoFindRefreshIntervalSeconds = Mathf.Max(0.1f, autoFindRefreshIntervalSeconds);
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
        nextAutoFindRefreshAt = Time.time + autoFindRefreshIntervalSeconds;
    }

    private bool ShouldRefreshAutoFoundNodes()
    {
        // Reuse the cache until a destroyed node requires a rescan.
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] == null)
            {
                return true;
            }
        }

        return Time.time >= nextAutoFindRefreshAt;
    }

    private void TickCommunication()
    {
        if (autoFindNodes && ShouldRefreshAutoFoundNodes())
        {
            RefreshNodes();
        }

        transportSession.BeginTick();

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

                transportSession.ExchangeBidirectional(
                    left,
                    right,
                    GetNodeRuntimeId(left),
                    GetNodeRuntimeId(right));
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

    private void OnDrawGizmos()
    {
        if (!drawActiveLinks || nodes == null)
        {
            return;
        }

        Gizmos.color = activeLinkColor;
        foreach (var key in transportSession.ActiveLinks)
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
}
