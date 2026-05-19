using System;

[Serializable]
public readonly struct DroneSwarmNodePairKey : IEquatable<DroneSwarmNodePairKey>
{
    public readonly int LeftNodeId;
    public readonly int RightNodeId;

    private DroneSwarmNodePairKey(int leftNodeId, int rightNodeId)
    {
        LeftNodeId = leftNodeId;
        RightNodeId = rightNodeId;
    }

    public static DroneSwarmNodePairKey From(int firstNodeId, int secondNodeId)
    {
        return firstNodeId <= secondNodeId
            ? new DroneSwarmNodePairKey(firstNodeId, secondNodeId)
            : new DroneSwarmNodePairKey(secondNodeId, firstNodeId);
    }

    public bool Equals(DroneSwarmNodePairKey other)
    {
        return LeftNodeId == other.LeftNodeId && RightNodeId == other.RightNodeId;
    }

    public override bool Equals(object obj)
    {
        return obj is DroneSwarmNodePairKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(LeftNodeId, RightNodeId);
    }
}

[Serializable]
public readonly struct DroneSwarmDirectedLinkKey : IEquatable<DroneSwarmDirectedLinkKey>
{
    private readonly int sourceNodeId;
    private readonly int destinationNodeId;

    public DroneSwarmDirectedLinkKey(int sourceNodeId, int destinationNodeId)
    {
        this.sourceNodeId = sourceNodeId;
        this.destinationNodeId = destinationNodeId;
    }

    public bool Equals(DroneSwarmDirectedLinkKey other)
    {
        return sourceNodeId == other.sourceNodeId
            && destinationNodeId == other.destinationNodeId;
    }

    public override bool Equals(object obj)
    {
        return obj is DroneSwarmDirectedLinkKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(sourceNodeId, destinationNodeId);
    }
}
