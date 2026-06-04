using System;
using UnityEngine;

[RequireComponent(typeof(DroneSwarmAgentState))]
public sealed class DroneGridSensor : MonoBehaviour
{
    [SerializeField] private DroneDemoGridWorld world;
    [SerializeField] private int sensorRadius = 2;
    [SerializeField] private bool useCameraFootprint = true;
    [SerializeField] private Camera sensingCamera;
    [SerializeField] private float cameraSampleHeight = 0.35f;
    [SerializeField] private float senseIntervalSeconds = 0.25f;
    [SerializeField] private bool senseOnStart = true;

    private DroneSwarmAgentState agentState;
    private float nextSenseAt;

    public event Action<DroneGridSensor> ObservationsChanged;
    public event Action<DroneGridSensor, DroneNative.DroneVec3i> TargetSensed;

    public DroneDemoGridWorld World => world;
    public int SensorRadius
    {
        get => sensorRadius;
        set => sensorRadius = Mathf.Max(0, value);
    }

    public Camera SensingCamera
    {
        get => sensingCamera;
        set => sensingCamera = value;
    }

    public void Configure(DroneDemoGridWorld newWorld, int newSensorRadius)
    {
        world = newWorld;
        SensorRadius = newSensorRadius;
        ConfigureAgentMap();
    }

    public void ConfigureCamera(Camera newSensingCamera)
    {
        sensingCamera = newSensingCamera;
    }

    private void Awake()
    {
        agentState = GetComponent<DroneSwarmAgentState>();
        if (world == null)
        {
            world = FindAnyObjectByType<DroneDemoGridWorld>();
        }
    }

    private void Start()
    {
        ConfigureAgentMap();

        if (senseOnStart)
        {
            SenseNow();
        }
    }

    private void OnValidate()
    {
        sensorRadius = Mathf.Max(0, sensorRadius);
        cameraSampleHeight = Mathf.Max(0f, cameraSampleHeight);
        senseIntervalSeconds = Mathf.Max(0.02f, senseIntervalSeconds);
    }

    private void Update()
    {
        if (Time.time < nextSenseAt)
        {
            return;
        }

        SenseNow();
    }

    [ContextMenu("Sense Now")]
    public void SenseNow()
    {
        if (world == null || agentState == null)
        {
            return;
        }

        ConfigureAgentMap();

        bool changed = false;
        float timestamp = Mathf.Max(Time.time, 0.0001f);

        if (useCameraFootprint && TrySenseCameraFootprint(timestamp, ref changed))
        {
            FinishSense(changed);
            return;
        }

        SenseRadius(timestamp, ref changed);
        FinishSense(changed);
    }

    private void SenseRadius(float timestamp, ref bool changed)
    {
        var center = world.WorldToGrid(transform.position);
        int radiusSquared = sensorRadius * sensorRadius;

        for (int z = center.z - sensorRadius; z <= center.z + sensorRadius; z++)
        {
            for (int y = center.y - sensorRadius; y <= center.y + sensorRadius; y++)
            {
                for (int x = center.x - sensorRadius; x <= center.x + sensorRadius; x++)
                {
                    int dx = x - center.x;
                    int dy = y - center.y;
                    int dz = z - center.z;
                    if (dx * dx + dy * dy + dz * dz > radiusSquared)
                    {
                        continue;
                    }

                    var cell = new DroneNative.DroneVec3i(x, y, z);
                    if (!world.IsInBounds(cell))
                    {
                        continue;
                    }

                    DroneCellState state = world.SenseCell(cell);
                    ObserveCell(cell, state, timestamp, ref changed);
                }
            }
        }
    }

    private bool TrySenseCameraFootprint(float timestamp, ref bool changed)
    {
        Camera camera = ResolveSensingCamera();
        if (camera == null)
        {
            return false;
        }

        for (int z = 0; z < world.Depth; z++)
        {
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    var cell = new DroneNative.DroneVec3i(x, y, z);
                    if (!CameraTouchesCell(camera, cell))
                    {
                        continue;
                    }

                    DroneCellState state = world.SenseCell(cell);
                    ObserveCell(cell, state, timestamp, ref changed);
                }
            }
        }

        return true;
    }

    private Camera ResolveSensingCamera()
    {
        if (sensingCamera != null)
        {
            return sensingCamera;
        }

        sensingCamera = GetComponentInChildren<Camera>();
        return sensingCamera;
    }

    private bool CameraTouchesCell(Camera camera, DroneNative.DroneVec3i cell)
    {
        float halfCell = world.CellSize * 0.5f;
        Vector3 center = world.GridToWorld(cell, cell.y * world.CellSize + cameraSampleHeight);

        return CameraContainsPoint(camera, center)
            || CameraContainsPoint(camera, center + new Vector3(-halfCell, 0f, -halfCell))
            || CameraContainsPoint(camera, center + new Vector3(-halfCell, 0f, halfCell))
            || CameraContainsPoint(camera, center + new Vector3(halfCell, 0f, -halfCell))
            || CameraContainsPoint(camera, center + new Vector3(halfCell, 0f, halfCell));
    }

    private static bool CameraContainsPoint(Camera camera, Vector3 worldPoint)
    {
        Vector3 viewportPoint = camera.WorldToViewportPoint(worldPoint);
        return viewportPoint.z >= camera.nearClipPlane
            && viewportPoint.z <= camera.farClipPlane
            && viewportPoint.x >= 0f
            && viewportPoint.x <= 1f
            && viewportPoint.y >= 0f
            && viewportPoint.y <= 1f;
    }

    private void ObserveCell(
        DroneNative.DroneVec3i cell,
        DroneCellState state,
        float timestamp,
        ref bool changed
    )
    {
        if (state == DroneCellState.Target)
        {
            changed |= agentState.ObserveTarget(cell, timestamp);
            TargetSensed?.Invoke(this, cell);
        }
        else
        {
            changed |= agentState.ObserveCell(cell, state, timestamp);
        }
    }

    private void FinishSense(bool changed)
    {
        nextSenseAt = Time.time + senseIntervalSeconds;

        if (changed)
        {
            ObservationsChanged?.Invoke(this);
        }
    }

    private void ConfigureAgentMap()
    {
        if (world == null || agentState == null)
        {
            return;
        }

        if (agentState.LocalMap == null
            || agentState.LocalMap.Width != world.Width
            || agentState.LocalMap.Height != world.Height
            || agentState.LocalMap.Depth != world.Depth)
        {
            agentState.ConfigureMap(world.Width, world.Height, world.Depth);
        }
    }
}
