using UnityEngine;

public class Explorer : MonoBehaviour
{
    private Animator animator;

    [Header("Terrain")]
    public Terrain terrain;

    [Header("探索")]
    public float searchRadius = 20f;
    public float moveSpeed = 3f;
    public float arriveDistance = 1.5f;

    [Header("体力")]
    [Range(0, 100)]
    public float stamina = 100f;

    public float staminaDecreasePerSecond = 5f;
    public float staminaRecoveryPerSecond = 10f;

    public float restartThreshold = 30f;

    [Header("障害物")]
    public LayerMask obstacleMask;

    public float obstacleDetectDistance = 2f;

    [Header("ランダム")]
    public int randomSeed = 12345;

    private Vector3 targetPosition;

    private AIState currentState;

    private enum AIState
    {
        Moving,
        Resting
    }

    void Start()
    {
        animator = GetComponentInChildren<Animator>();

        Random.InitState(randomSeed);

        currentState = AIState.Moving;

        SetRandomDestination();
    }

    void Update()
    {
        switch (currentState)
        {
            case AIState.Moving:
                UpdateMoving();
                break;

            case AIState.Resting:
                UpdateResting();
                break;
        }
    }

    void UpdateMoving()
    {
        animator.SetFloat("Speed", 2f);
        animator.SetFloat("MotionSpeed", 1f);

        stamina -= staminaDecreasePerSecond * Time.deltaTime;

        stamina = Mathf.Clamp(stamina, 0, 100);

        if (stamina <= 0)
        {
            animator.SetFloat("Speed", 0f);
            animator.SetFloat("MotionSpeed", 0f);

            currentState = AIState.Resting;
            return;
        }

        if (HasObstacleAhead())
        {
            AvoidObstacle();
            return;
        }

        MoveToTarget();

        float distance =
            Vector3.Distance(
                transform.position,
                targetPosition
            );

        if (distance <= arriveDistance)
        {
            SetRandomDestination();
        }
    }

    void UpdateResting()
    {
        animator.SetFloat("Speed", 0f);
        animator.SetFloat("MotionSpeed", 0f);

        stamina += staminaRecoveryPerSecond * Time.deltaTime;

        stamina = Mathf.Clamp(stamina, 0, 100);

        if (stamina >= restartThreshold)
        {
            currentState = AIState.Moving;

            SetRandomDestination();
        }
    }

    void MoveToTarget()
    {
        Vector3 direction =
            (targetPosition - transform.position).normalized;

        direction.y = 0;

        Vector3 nextPosition =
            transform.position +
            direction *
            moveSpeed *
            Time.deltaTime;

        nextPosition = ClampToTerrain(nextPosition);

        transform.position = nextPosition;

        if (direction != Vector3.zero)
        {
            transform.rotation =
                Quaternion.Slerp(
                    transform.rotation,
                    Quaternion.LookRotation(direction),
                    5f * Time.deltaTime
                );
        }

        AdjustHeightToTerrain();
    }

    void SetRandomDestination()
    {
        for (int i = 0; i < 30; i++)
        {
            Vector2 randomCircle =
                Random.insideUnitCircle * searchRadius;

            Vector3 candidate =
                transform.position +
                new Vector3(
                    randomCircle.x,
                    0,
                    randomCircle.y
                );

            candidate.y =
                Terrain.activeTerrain.SampleHeight(candidate);

            if (IsInsideTerrain(candidate) && IsValidPoint(candidate))
            {
                targetPosition = candidate;
                return;
            }
        }

        targetPosition = transform.position;
    }

    bool IsInsideTerrain(Vector3 point)
    {
        Vector3 terrainPos = terrain.transform.position;

        Vector3 terrainSize = terrain.terrainData.size;

        bool insideX =
            point.x >= terrainPos.x &&
            point.x <= terrainPos.x + terrainSize.x;

        bool insideZ =
            point.z >= terrainPos.z &&
            point.z <= terrainPos.z + terrainSize.z;

        return insideX && insideZ;
    }

    Vector3 ClampToTerrain(Vector3 position)
    {
        Vector3 terrainPos = terrain.transform.position;

        Vector3 terrainSize = terrain.terrainData.size;

        position.x = Mathf.Clamp(
            position.x,
            terrainPos.x,
            terrainPos.x + terrainSize.x
        );

        position.z = Mathf.Clamp(
            position.z,
            terrainPos.z,
            terrainPos.z + terrainSize.z
        );

        return position;
    }

    bool IsValidPoint(Vector3 point)
    {
        float checkRadius = 1.2f;

        bool blocked =
            Physics.CheckSphere(
                point,
                checkRadius,
                obstacleMask
            );

        return !blocked;
    }

    bool HasObstacleAhead()
    {
        Vector3 highOrigin =
            transform.position + Vector3.up * 1.5f;

        Vector3 lowOrigin =
            transform.position + Vector3.up * 0.2f;

        bool highHit =
            Physics.Raycast(
                highOrigin,
                transform.forward,
                obstacleDetectDistance,
                obstacleMask
            );

        bool lowHit =
            Physics.Raycast(
                lowOrigin,
                transform.forward,
                obstacleDetectDistance,
                obstacleMask
            );

        return highHit || lowHit;
    }

    void AvoidObstacle()
    {
        Vector3 avoidDirection =
            Quaternion.Euler(
                0,
                Random.Range(-120f, 120f),
                0
            ) * transform.forward;

        targetPosition =
            ClampToTerrain(
                transform.position +
                avoidDirection * 5f
            );
    }

    void AdjustHeightToTerrain()
    {
        Vector3 position = transform.position;

        position.y =
            Terrain.activeTerrain.SampleHeight(position);

        transform.position = position;
    }

    void OnDrawGizmos()
    {
        Gizmos.color = Color.green;

        Gizmos.DrawWireSphere(
            transform.position,
            searchRadius
        );

        Gizmos.color = Color.red;

        Gizmos.DrawSphere(
            targetPosition,
            0.5f
        );
    }

    public void OnFootstep()
    {

    }
}