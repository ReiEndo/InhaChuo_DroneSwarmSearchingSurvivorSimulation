using UnityEngine;

public class Explorer : MonoBehaviour
{
    public Terrain terrain;

    private Animator animator;

    public float scanRadius = 10f;

    public int mapSizeX = 500;
    public int mapSizeZ = 500;

    private bool[,] discovered;

    public float moveSpeed = 3f;

    public float obstacleCheckDistance = 2f;

    private Vector3 targetPosition;

    private bool hasTarget = false;

    void Start()
    {
        discovered = new bool[mapSizeX, mapSizeZ];

        animator = GetComponent<Animator>();
    }

    void Update()
    {
        ScanAround();

        if (!hasTarget)
        {
            FindUnknownTarget();
        }

        MoveToTarget();
    }

    void ScanAround()
    {
        Vector3 pos = transform.position;
        int centerX = Mathf.RoundToInt(pos.x);
        int centerZ = Mathf.RoundToInt(pos.z);
        for (int x = centerX - (int)scanRadius; x <= centerX + (int)scanRadius; x++)
        {
            for (int z = centerZ - (int)scanRadius; z <= centerZ + (int)scanRadius; z++)
            {
                if (x < 0 || z < 0 || x >= mapSizeX || z >= mapSizeZ) continue;
                float dist = Vector2.Distance(
                    new Vector2(centerX, centerZ),
                    new Vector2(x, z)
                );
                if (dist <= scanRadius)
                {
                    discovered[x, z] = true;
                }
            }
        }
    }

    bool IsObstacleAhead()
    {
        Vector3 origin =
            transform.position + Vector3.up * 1.5f;

        Vector3 forward = transform.forward;

        Vector3 leftDir =
            Quaternion.Euler(0, -30, 0) * forward;

        Vector3 rightDir =
            Quaternion.Euler(0, 30, 0) * forward;

        Debug.DrawRay(origin, forward * obstacleCheckDistance, Color.red);

        Debug.DrawRay(origin, leftDir * obstacleCheckDistance, Color.yellow);

        Debug.DrawRay(origin, rightDir * obstacleCheckDistance, Color.cyan);

        if (Physics.Raycast(origin, forward, obstacleCheckDistance))
            return true;

        if (Physics.Raycast(origin, leftDir, obstacleCheckDistance))
            return true;

        if (Physics.Raycast(origin, rightDir, obstacleCheckDistance))
            return true;

        return false;
    }

    void FindUnknownTarget()
    {
        for (int i = 0; i < 100; i++)
        {
            int x = Random.Range(0, mapSizeX);
            int z = Random.Range(0, mapSizeZ);

            if (discovered[x, z] == false)
            {
                Vector3 worldPos = new Vector3(x, 0, z);

                float y = terrain.SampleHeight(worldPos);

                y += terrain.transform.position.y;

                targetPosition = new Vector3(x, y, z);

                hasTarget = true;

                return;
            }
        }
    }

    void MoveToTarget()
    {
        if (IsObstacleAhead())
        {
            transform.Rotate(0, 90f * Time.deltaTime, 0);
        }
        if (!hasTarget) return;
        Vector3 currentPos = transform.position;

        Vector3 targetDir =
            targetPosition - transform.position;

        targetDir.y = 0;

        if (targetDir != Vector3.zero)
        {
            Quaternion targetRot =
                Quaternion.LookRotation(targetDir);

            transform.rotation =
                Quaternion.Slerp(
                    transform.rotation,
                    targetRot,
                    2f * Time.deltaTime
                );
        }

        Vector3 nextPos =
            currentPos +
            transform.forward *
            moveSpeed *
            Time.deltaTime;
        float terrainY = terrain.SampleHeight(nextPos);

        terrainY += terrain.transform.position.y;

        nextPos.y = terrainY;
        transform.position = nextPos;
        Vector3 dir = targetPosition - transform.position;

        dir.y = 0;

        if (dir != Vector3.zero)
        {
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(dir),
                5f * Time.deltaTime
            );
        }
        animator.SetFloat("Speed", moveSpeed);
        animator.SetFloat("MotionSpeed", 1f);
        float dist = Vector3.Distance(
            transform.position,
            targetPosition
        );

        if (dist < 1f)
        {
            hasTarget = false;
        }
    }
}