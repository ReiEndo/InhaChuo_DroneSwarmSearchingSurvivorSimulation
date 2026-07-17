using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class DroneLocalAvoidanceMotor : MonoBehaviour
{
    [Header("Avoidance")]
    [SerializeField] private bool useLocalAvoidance = true;
    [SerializeField] private float droneRadius = 0.35f;
    [SerializeField] private float neighborSearchRadius = 2.5f;
    [SerializeField] private float timeHorizonSeconds = 1.5f;
    [SerializeField] private int maxNeighbors = 16;

    private static readonly DroneNative.DroneNeighborState[] EmptyNeighbors =
        Array.Empty<DroneNative.DroneNeighborState>();
    private static readonly List<DroneLocalAvoidanceMotor> ActiveMotors = new();

    private DroneNative.DroneNeighborState[] neighborBuffer = EmptyNeighbors;
    private Vector3 previousPosition;

    public float DroneRadius => droneRadius;
    public Vector3 CurrentVelocity { get; private set; }

    private void Awake()
    {
        previousPosition = transform.position;
        EnsureNeighborBuffer();
    }

    private void OnEnable()
    {
        if (!ActiveMotors.Contains(this))
        {
            ActiveMotors.Add(this);
        }

        previousPosition = transform.position;
    }

    private void OnDisable()
    {
        ActiveMotors.Remove(this);
    }

    private void OnValidate()
    {
        droneRadius = Mathf.Max(0f, droneRadius);
        neighborSearchRadius = Mathf.Max(0f, neighborSearchRadius);
        timeHorizonSeconds = Mathf.Max(0.01f, timeHorizonSeconds);
        maxNeighbors = Mathf.Max(0, maxNeighbors);
    }

    private void LateUpdate()
    {
        float deltaTime = Time.deltaTime;
        if (deltaTime > Mathf.Epsilon)
        {
            CurrentVelocity = (transform.position - previousPosition) / deltaTime;
        }

        previousPosition = transform.position;
    }

    public Vector3 Move(Vector3 preferredVelocity, float maxSpeed)
    {
        maxSpeed = Mathf.Max(0f, maxSpeed);
        Vector3 velocity = preferredVelocity;

        if (useLocalAvoidance && maxSpeed > 0f)
        {
            velocity = ComputeAvoidedVelocity(preferredVelocity, maxSpeed);
        }
        else if (velocity.sqrMagnitude > maxSpeed * maxSpeed)
        {
            velocity = velocity.normalized * maxSpeed;
        }

        velocity.y = preferredVelocity.y;

        transform.position += velocity * Time.deltaTime;
        CurrentVelocity = velocity;
        return velocity;
    }

    private Vector3 ComputeAvoidedVelocity(Vector3 preferredVelocity, float maxSpeed)
    {
        int neighborCount = CollectNeighbors();
        var result = DroneNative.DroneComputeLocalAvoidanceVelocity(
            ToNative(transform.position),
            ToNative(preferredVelocity),
            droneRadius,
            maxSpeed,
            timeHorizonSeconds,
            neighborBuffer,
            neighborCount,
            out var avoidedVelocity
        );

        if (result == 0)
        {
            return preferredVelocity.sqrMagnitude > maxSpeed * maxSpeed
                ? preferredVelocity.normalized * maxSpeed
                : preferredVelocity;
        }

        return ToUnity(avoidedVelocity);
    }

    private int CollectNeighbors()
    {
        EnsureNeighborBuffer();

        if (maxNeighbors == 0 || neighborSearchRadius <= 0f)
        {
            return 0;
        }

        int count = 0;
        float searchDistanceSquared = neighborSearchRadius * neighborSearchRadius;
        Vector3 selfPosition = transform.position;

        foreach (var motor in ActiveMotors)
        {
            if (motor == null || motor == this)
            {
                continue;
            }

            Vector3 offset = motor.transform.position - selfPosition;
            if (offset.sqrMagnitude > searchDistanceSquared)
            {
                continue;
            }

            neighborBuffer[count++] = new DroneNative.DroneNeighborState(
                ToNative(motor.transform.position),
                ToNative(motor.CurrentVelocity),
                motor.DroneRadius
            );

            if (count >= neighborBuffer.Length)
            {
                break;
            }
        }

        return count;
    }

    private void EnsureNeighborBuffer()
    {
        if (neighborBuffer.Length != maxNeighbors)
        {
            neighborBuffer = maxNeighbors > 0
                ? new DroneNative.DroneNeighborState[maxNeighbors]
                : EmptyNeighbors;
        }
    }

    private static DroneNative.DroneVec3f ToNative(Vector3 value)
    {
        return new DroneNative.DroneVec3f(value.x, value.y, value.z);
    }

    private static Vector3 ToUnity(DroneNative.DroneVec3f value)
    {
        return new Vector3(value.x, value.y, value.z);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position, Mathf.Max(0f, droneRadius));

        Gizmos.color = new Color(1f, 0f, 1f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, Mathf.Max(0f, neighborSearchRadius));
    }
}
