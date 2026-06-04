using UnityEngine;

public class Explorer : MonoBehaviour
{
    [Header("Terrain")]
    public Terrain terrain;

    private Animator animator;

    [Header("探索")]
    public float scanRadius = 20f;
    public float moveSpeed = 3f;


    //public int mapSizeX = 500;
    //public int mapSizeZ = 500;

    //private bool[,] discovered;

    public float obstacleCheckDistance = 2f;

    private Vector3 targetPosition;

    private bool hasTarget = false;
    private CharacterController controller;//移動方法にCharacterController.Moveを採用
    private float verticalVelocity;//重力用
    public LayerMask obstacleMask;//地面の障害物判定を除外
    private float avoidTimer = 0f;//回避と目標地点に移動、2つの状態に分離
    //private float avoidDirection;//回避回転の左右ランダム化
    private bool isAvoiding = false;//回避開始時だけ回転するようフラッグ
    private Quaternion avoidRotation;//瞬時に回転しないよう


    void Start()
    {
        //discovered = new bool[mapSizeX, mapSizeZ];

        animator = GetComponent<Animator>();

        controller = GetComponent<CharacterController>();
    }

    void Update()
    {
        //ScanAround();

        if(!hasTarget)
        {
            FindUnknownTarget();
        }

        MoveToTarget();
    }

    /*void ScanAround()
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
    }*/

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

        if (Physics.Raycast(origin, forward,obstacleCheckDistance, obstacleMask))
            return true;

        if (Physics.Raycast(origin, leftDir, obstacleCheckDistance, obstacleMask))
            return true;

        if (Physics.Raycast(origin, rightDir, obstacleCheckDistance, obstacleMask))
            return true;

        return false;
    }

    void FindUnknownTarget()
    {
    /*for(int i=0; i<10; i++)
    {*/
        //int x = Random.Range(0, mapSizeX);
        //int z = Random.Range(0, mapSizeZ);
        Vector2 randomCircle = Random.insideUnitCircle * scanRadius;

        Vector3 target = 
            transform.position + 
            new Vector3(randomCircle.x, 0 , randomCircle.y);

        target.y = Terrain.activeTerrain.SampleHeight(target);

        if(IsInsideTerrain(target) && IsValidPoint(target)) 
        {
            hasTarget = true;
            targetPosition = target;

            return;
        }

        /*if (discovered[x, z] == false)
        {
            Vector3 worldPos = new Vector3(x, 0, z);

            float y = terrain.SampleHeight(worldPos);

            y += terrain.transform.position.y;

            targetPosition = new Vector3(x, y, z);

            hasTarget = true;

            return;
        }*/
    /*}*/
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

    void MoveToTarget()
    {
        if (avoidTimer > 0)
        {
            avoidTimer -= Time.deltaTime;

            transform.rotation =
                Quaternion.Slerp(
                    transform.rotation,
                    avoidRotation,
                    3f * Time.deltaTime
                );

            Vector3 avoidMove =
                transform.forward * moveSpeed;

            avoidMove.y = verticalVelocity;

            controller.Move(avoidMove * Time.deltaTime);

            if (avoidTimer <= 0)
            {
                isAvoiding = false;
            }

            return;
        }

        if (IsObstacleAhead() && !isAvoiding)
        {
            /*回避ランダム化avoidDirection =
                Random.value < 0.5f ? -1f : 1f;*/

            avoidRotation = Quaternion.Euler(0, transform.eulerAngles.y + 45f /*+ avoidDirection * 60f*/, 0);

            avoidTimer = 1.0f;

            isAvoiding = true;
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

        if (controller.isGrounded)
        {
            verticalVelocity = -1f;
        }
        else
        {
            verticalVelocity += Physics.gravity.y * Time.deltaTime;
        }

        Vector3 move =
            transform.forward * moveSpeed;

        move.y = verticalVelocity;

        controller.Move(move * Time.deltaTime);

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