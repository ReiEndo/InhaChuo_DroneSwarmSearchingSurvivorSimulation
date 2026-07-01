using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(DroneGridSensor))]
[RequireComponent(typeof(DroneSwarmAgentState))]
[RequireComponent(typeof(DroneFrontierExplorer))]
public sealed class DroneMissionEndReporter : MonoBehaviour
{
    private static bool s_ExplorerFoundNotified;
    private static bool s_AllDronesReturnedNotified;

    [Header("Game Flow")]
    [SerializeField] private GameFlowController gameFlowController;
    [SerializeField] private float completionCheckInterval = 0.2f;

    [Header("Return Check")]
    [SerializeField] private float minimumReturnRadiusInCells = 2f;
    [SerializeField] private float extraReturnRadius = 0f;

    [Tooltip("trueにすると、全DroneがTarget情報を知っていることも成功条件にする。まずはfalse推奨。")]
    [SerializeField] private bool requireAllDronesKnowTarget = false;

    private DroneGridSensor sensor;
    private float nextCompletionCheckAt;

    public static void ResetMissionState()
    {
        s_ExplorerFoundNotified = false;
        s_AllDronesReturnedNotified = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        ResetMissionState();
    }

    private void Awake()
    {
        sensor = GetComponent<DroneGridSensor>();
    }

    private void OnEnable()
    {
        if (sensor != null)
        {
            sensor.TargetSensed += HandleTargetSensed;
        }
    }

    private void OnDisable()
    {
        if (sensor != null)
        {
            sensor.TargetSensed -= HandleTargetSensed;
        }
    }

    private void Update()
    {
        if (Time.time < nextCompletionCheckAt)
        {
            return;
        }

        nextCompletionCheckAt = Time.time + Mathf.Max(0.02f, completionCheckInterval);

        if (!TryGetEarliestTargetReportGlobally(out DroneTargetReport firstReport, out DroneDemoGridWorld world))
        {
            return;
        }

        NotifyExplorerFound();

        if (s_AllDronesReturnedNotified)
        {
            return;
        }

        if (AllActiveDronesReturnedToTarget(firstReport, world))
        {
            NotifyAllDronesReturned();
            StopAllDroneExplorers();
        }
    }

    private void HandleTargetSensed(DroneGridSensor changedSensor, DroneNative.DroneVec3i targetCell)
    {
        NotifyExplorerFound();
    }

    private void NotifyExplorerFound()
    {
        if (s_ExplorerFoundNotified)
        {
            return;
        }

        s_ExplorerFoundNotified = true;
        ResolveGameFlowController()?.NotifyExplorerFound();
    }

    private void NotifyAllDronesReturned()
    {
        if (s_AllDronesReturnedNotified)
        {
            return;
        }

        s_AllDronesReturnedNotified = true;
        ResolveGameFlowController()?.NotifyAllDronesReturned();
    }

    private bool TryGetEarliestTargetReportGlobally(
        out DroneTargetReport earliestReport,
        out DroneDemoGridWorld reportWorld
    )
    {
        earliestReport = default;
        reportWorld = sensor != null ? sensor.World : null;

        bool found = false;

        foreach (DroneSwarmAgentState state in DroneSwarmAgentState.ActiveAgents)
        {
            if (state == null || state.DroneId <= 0 || state.LocalMap == null)
            {
                continue;
            }

            if (!state.LocalMap.TryGetEarliestTargetReport(out DroneTargetReport report))
            {
                continue;
            }

            if (!found
                || report.ObservedAt < earliestReport.ObservedAt
                || (Mathf.Approximately(report.ObservedAt, earliestReport.ObservedAt)
                    && report.ReporterId < earliestReport.ReporterId))
            {
                earliestReport = report;
                found = true;

                if (state.TryGetComponent<DroneGridSensor>(out DroneGridSensor stateSensor))
                {
                    reportWorld = stateSensor.World;
                }
            }
        }

        return found && reportWorld != null;
    }

    private bool AllActiveDronesReturnedToTarget(
        DroneTargetReport firstReport,
        DroneDemoGridWorld world
    )
    {
        if (world == null)
        {
            return false;
        }

        int activeDroneCount = 0;
        float returnRadius = GetMissionReturnRadius(world);

        Vector3 targetPosition = world.GridToWorld(firstReport.Cell, transform.position.y);

        foreach (DroneSwarmAgentState state in DroneSwarmAgentState.ActiveAgents)
        {
            if (state == null || state.DroneId <= 0)
            {
                continue;
            }

            activeDroneCount++;

            if (requireAllDronesKnowTarget && !state.KnowsTargetFound)
            {
                return false;
            }

            if (!IsNearOnXZ(state.transform.position, targetPosition, returnRadius))
            {
                return false;
            }
        }

        return activeDroneCount > 0;
    }

    private float GetMissionReturnRadius(DroneDemoGridWorld world)
    {
        float cellSize = world != null ? Mathf.Max(0.1f, world.CellSize) : 1f;
        float radius = cellSize * Mathf.Max(0.1f, minimumReturnRadiusInCells);

        foreach (DroneSwarmAgentState state in DroneSwarmAgentState.ActiveAgents)
        {
            if (state == null || state.DroneId <= 0)
            {
                continue;
            }

            DroneCommunicationNode communicationNode = state.GetComponent<DroneCommunicationNode>();
            if (communicationNode != null)
            {
                radius = Mathf.Max(radius, communicationNode.CommunicationRadius);
            }
        }

        return radius + Mathf.Max(0f, extraReturnRadius);
    }

    private static bool IsNearOnXZ(Vector3 left, Vector3 right, float radius)
    {
        float clampedRadius = Mathf.Max(0f, radius);
        float dx = left.x - right.x;
        float dz = left.z - right.z;

        return dx * dx + dz * dz <= clampedRadius * clampedRadius;
    }

    private void StopAllDroneExplorers()
    {
        foreach (DroneSwarmAgentState state in DroneSwarmAgentState.ActiveAgents)
        {
            if (state != null && state.TryGetComponent<DroneFrontierExplorer>(out DroneFrontierExplorer explorer))
            {
                explorer.StopAfterMissionComplete();
            }
        }
    }

    private GameFlowController ResolveGameFlowController()
    {
        if (gameFlowController == null)
        {
            gameFlowController = FindAnyObjectByType<GameFlowController>(FindObjectsInactive.Include);
        }

        return gameFlowController;
    }
}