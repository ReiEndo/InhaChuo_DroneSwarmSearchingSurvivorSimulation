using System;
using UnityEngine;

[RequireComponent(typeof(DroneGridSensor))]
[RequireComponent(typeof(DroneLocalAvoidanceMotor))]
public sealed class DronePathFollower : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private bool followPath = true;
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float arriveDistance = 0.2f;
    [Tooltip("Height above the terrain/ground that drones should fly at while following grid paths.")]
    [SerializeField] private float flightAltitude = 3f;

    private DroneGridSensor sensor;
    private DroneLocalAvoidanceMotor avoidanceMotor;
    private DroneDemoGridWorld world;
    private DroneNative.DroneVec3i[] path = Array.Empty<DroneNative.DroneVec3i>();
    private int usablePathCount;
    private int pathIndex;
    private bool pathFinishedNotified;

    public event Action<DronePathFollower> PathFinished;

    public bool FollowPath
    {
        get => followPath;
        set => followPath = value;
    }

    public float MoveSpeed
    {
        get => moveSpeed;
        set => moveSpeed = Mathf.Max(0f, value);
    }

    public int PathIndex => pathIndex;

    private void Awake()
    {
        sensor = GetComponent<DroneGridSensor>();
        avoidanceMotor = GetComponent<DroneLocalAvoidanceMotor>();
        world = sensor.World;
    }

    private void OnValidate()
    {
        moveSpeed = Mathf.Max(0f, moveSpeed);
        arriveDistance = Mathf.Max(0.001f, arriveDistance);
        flightAltitude = Mathf.Max(0f, flightAltitude);
    }

    private void Update()
    {
        if (world == null)
        {
            world = sensor.World;
            if (world == null)
            {
                return;
            }
        }

        FollowCurrentPath();
    }

    public void SetPath(DroneNative.DroneVec3i[] newPath, int newUsablePathCount)
    {
        path = newPath ?? Array.Empty<DroneNative.DroneVec3i>();
        usablePathCount = Mathf.Clamp(newUsablePathCount, 0, path.Length);
        pathIndex = usablePathCount > 1 ? 1 : 0;
        pathFinishedNotified = false;
    }

    public void ClearPath()
    {
        usablePathCount = 0;
        pathIndex = 0;
        pathFinishedNotified = false;
    }

    private void FollowCurrentPath()
    {
        if (!followPath || usablePathCount <= 0 || pathIndex >= usablePathCount)
        {
            return;
        }

        AdvanceReachedWaypoints();
        if (pathIndex >= usablePathCount)
        {
            NotifyPathFinished();
            return;
        }

        Vector3 destination = GetWaypointPosition(path[pathIndex]);
        Vector3 toDestination = destination - transform.position;
        Vector3 preferredVelocity = toDestination.sqrMagnitude > Mathf.Epsilon
            ? toDestination.normalized * moveSpeed
            : Vector3.zero;

        if (avoidanceMotor != null)
        {
            avoidanceMotor.Move(preferredVelocity, moveSpeed);
        }
        else
        {
            transform.position = Vector3.MoveTowards(
                transform.position,
                destination,
                moveSpeed * Time.deltaTime
            );
        }

        AdvanceReachedOrPassedWaypoints(destination, toDestination);
        if (pathIndex >= usablePathCount)
        {
            NotifyPathFinished();
        }
    }

    private void AdvanceReachedWaypoints()
    {
        while (pathIndex < usablePathCount)
        {
            Vector3 destination = GetWaypointPosition(path[pathIndex]);
            if (Vector3.Distance(transform.position, destination) > arriveDistance)
            {
                return;
            }

            pathIndex++;
        }
    }

    private void AdvanceReachedOrPassedWaypoints(Vector3 previousDestination, Vector3 previousOffset)
    {
        if (pathIndex >= usablePathCount)
        {
            return;
        }

        Vector3 currentDestination = GetWaypointPosition(path[pathIndex]);
        Vector3 currentOffset = currentDestination - transform.position;
        if (Vector3.Distance(transform.position, currentDestination) <= arriveDistance
            || (currentDestination == previousDestination
                && Vector3.Dot(previousOffset, currentOffset) <= 0f))
        {
            pathIndex++;
            AdvanceReachedWaypoints();
        }
    }

    private Vector3 GetWaypointPosition(DroneNative.DroneVec3i cell)
    {
        return world.GridToWorld(cell, flightAltitude);
    }

    private void NotifyPathFinished()
    {
        if (pathFinishedNotified)
        {
            return;
        }

        pathFinishedNotified = true;
        PathFinished?.Invoke(this);
    }
}
