using UnityEngine;

[RequireComponent(typeof(DroneSwarmAgentState))]
public sealed class DroneCommunicationNode : MonoBehaviour
{
    [Header("Communication")]
    [SerializeField] private float communicationRadius = 4f;
    [SerializeField] private bool commandNode;

    public DroneSwarmAgentState AgentState { get; private set; }
    public float CommunicationRadius => communicationRadius;
    public bool IsCommandNode => commandNode;

    public void Configure(float newCommunicationRadius, bool isCommandNode)
    {
        communicationRadius = Mathf.Max(0f, newCommunicationRadius);
        commandNode = isCommandNode;
    }

    public void SetCommunicationRadius(float newCommunicationRadius)
    {
        communicationRadius = Mathf.Max(0f, newCommunicationRadius);
    }

    private void Awake()
    {
        AgentState = GetComponent<DroneSwarmAgentState>();
    }

    private void OnValidate()
    {
        communicationRadius = Mathf.Max(0f, communicationRadius);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = commandNode ? Color.white : Color.cyan;
        Gizmos.DrawWireSphere(transform.position, Mathf.Max(0f, communicationRadius));
    }
}
