using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(DroneGridSensor))]
[RequireComponent(typeof(DroneSwarmAgentState))]
[RequireComponent(typeof(DroneFrontierExplorer))]
public sealed class DroneMissionEndReporter : MonoBehaviour
{
    private static bool s_ExplorerFoundNotified;
    private static bool s_AllDronesReturnedNotified;
    private static bool s_TargetSmokeSpawned;

    [Header("Game Flow")]
    [SerializeField] private GameFlowController gameFlowController;
    [SerializeField] private float completionCheckInterval = 0.2f;

    [Header("Target Smoke")]
    [SerializeField] private string targetSmokeResourcePath = "Smoke/TargetSmoke";
    [SerializeField] private float targetSmokeY = 0.05f;
    [SerializeField] private float targetSmokeStopAfterSeconds = -999f;
    [SerializeField] private float targetSmokeDestroyAfterStop = 999f;

    private ParticleSystem targetSmokePrefab;

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
        s_TargetSmokeSpawned = false;
    }

    public static bool EnsureTargetSmokeForResult()
    {
        if (s_TargetSmokeSpawned)
        {
            return true;
        }

        DroneMissionEndReporter reporter = FindAnyObjectByType<DroneMissionEndReporter>(
            FindObjectsInactive.Include
        );
        Explorer explorer = FindAnyObjectByType<Explorer>(FindObjectsInactive.Include);
        DroneDemoGridWorld world = reporter != null && reporter.sensor != null
            ? reporter.sensor.World
            : FindAnyObjectByType<DroneDemoGridWorld>(FindObjectsInactive.Include);

        if (reporter == null || explorer == null || world == null)
        {
            return false;
        }

        if (reporter.targetSmokePrefab == null)
        {
            reporter.targetSmokePrefab = Resources.Load<ParticleSystem>(
                reporter.targetSmokeResourcePath
            );
        }

        DroneNative.DroneVec3i targetCell = world.WorldToGrid(explorer.transform.position);
        reporter.SpawnTargetSmokeOnce(
            new DroneTargetReport(targetCell, Time.time, -1),
            world
        );
        return s_TargetSmokeSpawned;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        ResetMissionState();
    }

    private void Awake()
    {
        sensor = GetComponent<DroneGridSensor>();

        targetSmokePrefab = Resources.Load<ParticleSystem>(targetSmokeResourcePath);

        if (targetSmokePrefab == null)
        {
            Debug.LogError($"Smoke Prefabが見つかりません: Resources/{targetSmokeResourcePath}.prefab", this);
        }
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
            SpawnTargetSmokeOnce(firstReport, world);
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

    private void SpawnTargetSmokeOnce(DroneTargetReport report, DroneDemoGridWorld world)
    {
        if (s_TargetSmokeSpawned || targetSmokePrefab == null || world == null)
        {
            return;
        }

        s_TargetSmokeSpawned = true;

        Vector3 smokePosition = world.GridToWorld(report.Cell, targetSmokeY);

        ParticleSystem smoke = Instantiate(
            targetSmokePrefab,
            smokePosition,
            Quaternion.identity
        );

        // Parenting prevents interrupted replay teardown from leaking smoke objects.
        smoke.transform.SetParent(transform, true);
        smoke.Play();

        StartCoroutine(StopAndDestroySmoke(smoke));
    }

    private IEnumerator StopAndDestroySmoke(ParticleSystem smoke)
    {
        if (targetSmokeStopAfterSeconds <= 0f)
        {
            yield break;
        }

        yield return new WaitForSeconds(targetSmokeStopAfterSeconds);

        if (smoke != null)
        {
            smoke.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        yield return new WaitForSeconds(Mathf.Max(0f, targetSmokeDestroyAfterStop));

        if (smoke != null)
        {
            Destroy(smoke.gameObject);
        }
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
