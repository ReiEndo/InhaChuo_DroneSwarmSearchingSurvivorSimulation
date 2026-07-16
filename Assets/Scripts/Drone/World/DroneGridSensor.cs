using System;
using UnityEngine;

[RequireComponent(typeof(DroneSwarmAgentState))]
public sealed class DroneGridSensor : MonoBehaviour
{
    // Bound direct and serialized input so one sensing tick cannot scan unbounded cells.
    public const int MaximumSensorRadius = 8;

    [SerializeField] private DroneDemoGridWorld world;
    [SerializeField] private int sensorRadius = 2;
    [SerializeField] private bool useCameraFootprint = true;
    [SerializeField] private Camera sensingCamera;
    [SerializeField] private float cameraSampleHeight = 0.35f;
    [SerializeField] private float senseIntervalSeconds = 0.25f;
    [SerializeField] private bool senseOnStart = true;

    private readonly Vector3[] frustumCorners = new Vector3[4];
    private DroneSwarmAgentState agentState;
    private float nextSenseAt;

    public event Action<DroneGridSensor> ObservationsChanged;
    public event Action<DroneGridSensor, DroneNative.DroneVec3i> TargetSensed;

    public DroneDemoGridWorld World => world;
    public int SensorRadius
    {
        get => sensorRadius;
        set => sensorRadius = Mathf.Clamp(value, 0, MaximumSensorRadius);
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
        SensorRadius = sensorRadius;
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
        sensorRadius = Mathf.Clamp(sensorRadius, 0, MaximumSensorRadius);
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
        GetCandidateRange(center.x, sensorRadius, world.Width, out int minX, out int maxXExclusive);
        GetCandidateRange(center.y, sensorRadius, world.Height, out int minY, out int maxYExclusive);
        GetCandidateRange(center.z, sensorRadius, world.Depth, out int minZ, out int maxZExclusive);

        // Long endpoints prevent overflow before intersecting with map bounds.
        for (int z = minZ; z < maxZExclusive; z++)
        {
            for (int y = minY; y < maxYExclusive; y++)
            {
                for (int x = minX; x < maxXExclusive; x++)
                {
                    long dx = (long)x - center.x;
                    long dy = (long)y - center.y;
                    long dz = (long)z - center.z;
                    if (!IsWithinRadius(dx, dy, dz, sensorRadius))
                    {
                        continue;
                    }

                    var cell = new DroneNative.DroneVec3i(x, y, z);
                    DroneCellState state = world.SenseCell(cell);
                    ObserveCell(cell, state, timestamp, ref changed);
                }
            }
        }
    }

    private static void GetCandidateRange(
        int center,
        int radius,
        int dimension,
        out int minimum,
        out int maximumExclusive
    )
    {
        long safeDimension = Math.Max(0L, dimension);
        minimum = (int)Math.Max(0L, Math.Min(safeDimension, (long)center - radius));
        maximumExclusive = (int)Math.Max(
            0L,
            Math.Min(safeDimension, (long)center + radius + 1L)
        );
    }

    private static bool IsWithinRadius(long dx, long dy, long dz, int radius)
    {
        if (radius < 0
            || dx < -((long)radius) || dx > radius
            || dy < -((long)radius) || dy > radius
            || dz < -((long)radius) || dz > radius)
        {
            return false;
        }

        // Subtraction avoids overflow when checking an Int32-sized radius.
        long remaining = (long)radius * radius;
        long squared = dx * dx;
        if (squared > remaining)
        {
            return false;
        }

        remaining -= squared;
        squared = dy * dy;
        if (squared > remaining)
        {
            return false;
        }

        remaining -= squared;
        return dz * dz <= remaining;
    }

    private bool TrySenseCameraFootprint(float timestamp, ref bool changed)
    {
        Camera camera = ResolveSensingCamera();
        if (camera == null)
        {
            return false;
        }

        int minX = 0;
        int maxX = world.Width - 1;
        int minZ = 0;
        int maxZ = world.Depth - 1;

        // Bound candidates by the frustum corners; malformed projections fall back to a full scan.
        TryGetCameraCandidateBounds(camera, out minX, out maxX, out minZ, out maxZ);

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = minX; x <= maxX; x++)
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

    private bool TryGetCameraCandidateBounds(
        Camera camera,
        out int minX,
        out int maxX,
        out int minZ,
        out int maxZ
    )
    {
        minX = 0;
        maxX = world.Width - 1;
        minZ = 0;
        maxZ = world.Depth - 1;

        float near = camera.nearClipPlane;
        float far = camera.farClipPlane;
        float cellSize = world.CellSize;
        Vector3 origin = world.GridOrigin;
        if (!IsFinite(near) || !IsFinite(far) || near < 0f || far < near
            || !IsFinite(cellSize) || cellSize <= 0f || !IsFinite(origin))
        {
            return false;
        }

        float worldMinX = float.PositiveInfinity;
        float worldMaxX = float.NegativeInfinity;
        float worldMinZ = float.PositiveInfinity;
        float worldMaxZ = float.NegativeInfinity;

        for (int plane = 0; plane < 2; plane++)
        {
            float distance = plane == 0 ? near : far;
            camera.CalculateFrustumCorners(
                new Rect(0f, 0f, 1f, 1f),
                distance,
                Camera.MonoOrStereoscopicEye.Mono,
                frustumCorners
            );

            for (int i = 0; i < frustumCorners.Length; i++)
            {
                Vector3 corner = camera.transform.TransformPoint(frustumCorners[i]);
                if (!IsFinite(corner))
                {
                    return false;
                }

                worldMinX = Mathf.Min(worldMinX, corner.x);
                worldMaxX = Mathf.Max(worldMaxX, corner.x);
                worldMinZ = Mathf.Min(worldMinZ, corner.z);
                worldMaxZ = Mathf.Max(worldMaxZ, corner.z);
            }
        }

        // Include edge points and round candidate bounds outward.
        float padding = cellSize * 0.5f + Mathf.Max(0.0001f, cellSize * 0.0001f);
        worldMinX -= padding;
        worldMaxX += padding;
        worldMinZ -= padding;
        worldMaxZ += padding;

        float gridMaxX = origin.x + (world.Width - 1) * cellSize;
        float gridMaxZ = origin.z + (world.Depth - 1) * cellSize;
        if (!IsFinite(gridMaxX) || !IsFinite(gridMaxZ))
        {
            return false;
        }

        if (worldMaxX < origin.x || worldMinX > gridMaxX
            || worldMaxZ < origin.z || worldMinZ > gridMaxZ)
        {
            minX = 0;
            maxX = -1;
            minZ = 0;
            maxZ = -1;
            return true;
        }

        float clippedMinX = Mathf.Max(worldMinX, origin.x);
        float clippedMaxX = Mathf.Min(worldMaxX, gridMaxX);
        float clippedMinZ = Mathf.Max(worldMinZ, origin.z);
        float clippedMaxZ = Mathf.Min(worldMaxZ, gridMaxZ);

        minX = Mathf.Clamp(Mathf.FloorToInt((clippedMinX - origin.x) / cellSize), 0, world.Width - 1);
        maxX = Mathf.Clamp(Mathf.CeilToInt((clippedMaxX - origin.x) / cellSize), 0, world.Width - 1);
        minZ = Mathf.Clamp(Mathf.FloorToInt((clippedMinZ - origin.z) / cellSize), 0, world.Depth - 1);
        maxZ = Mathf.Clamp(Mathf.CeilToInt((clippedMaxZ - origin.z) / cellSize), 0, world.Depth - 1);
        return true;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
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
