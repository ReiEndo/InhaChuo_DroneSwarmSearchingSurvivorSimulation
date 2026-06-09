using UnityEngine;

public class Explorer : MonoBehaviour
{
    private Animator animator;
    private CharacterController controller;//移動方法にCharacterController.Moveを採用

    [Header("Terrain")]
    public Terrain terrain;

    [Header("探索")]
    public float scanRadius = 20f;
    public float moveSpeed = 3f;
    public float obstacleCheckDistance = 0.5f;
    public float minTargetDistance = 10f;
    private float avoidTimer = 0f;//回避と目標地点に移動、2つの状態に分離

    private Vector3 targetPosition;
    private bool hasTarget = false;

    private float verticalVelocity;//重力用
    
    public LayerMask obstacleMask;//地面の障害物判定を除外
    private float avoidDirection;//回避回転の左右ランダム化
    private bool isAvoiding = false;//回避開始時だけ回転するようフラッグ
    private Quaternion avoidRotation;//瞬時に回転しないよう
    
    [Header("体力")]
    [Range(0, 100)]
    public float stamina = 100f;
    public float staminaDecreasePerSecond = 5f;
    public float staminaRecoveryPerSecond = 7f;
    public float restartThreshold = 50f;
    private bool isResting = false;

    //public int mapSizeX = 500;
    //public int mapSizeZ = 500;

    //private bool[,] discovered;

    void Start()
    {
        //discovered = new bool[mapSizeX, mapSizeZ];

        animator = GetComponent<Animator>();

        controller = GetComponent<CharacterController>();
    }

    void Update()
    {
        //ScanAround();
        if (isResting)
        {
            RecoverStamina();
            return;
        }

        while(!hasTarget)
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

        Vector3 bodyOrigin =
            transform.position + Vector3.up * 0.8f;

        Vector3 forward = transform.forward;

        Vector3 leftDir =
            Quaternion.Euler(0, -15, 0) * forward;

        Vector3 rightDir =
            Quaternion.Euler(0, 15, 0) * forward;

        Debug.DrawRay(origin, forward * obstacleCheckDistance, Color.red);

        Debug.DrawRay(origin, leftDir * obstacleCheckDistance, Color.yellow);

        Debug.DrawRay(origin, rightDir * obstacleCheckDistance, Color.cyan);

        Debug.DrawRay(bodyOrigin, forward * obstacleCheckDistance, Color.green);

        if (Physics.Raycast(origin, forward,obstacleCheckDistance, obstacleMask))
            return true;

        if (Physics.Raycast(origin, leftDir, obstacleCheckDistance, obstacleMask))
            return true;

        if (Physics.Raycast(origin, rightDir, obstacleCheckDistance, obstacleMask))
            return true;

        if (Physics.Raycast(bodyOrigin, forward, obstacleCheckDistance, obstacleMask))
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

        float distance =
            Vector3.Distance(
                transform.position,
                target
            );

        if (distance < minTargetDistance) return;

        if(IsInsideTerrain(target) && IsValidPoint(target) && !HasSteepSlopeOnPath(transform.position,target)) 
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
        float checkRadius = 2.0f;

        bool blocked =
            Physics.CheckSphere(
                point,
                checkRadius,
                obstacleMask
            );

        return !blocked;
    }

    bool HasSteepSlopeOnPath(Vector3 start, Vector3 end)
    {
        int samples = 10;

        for(int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;

            Vector3 p =
                Vector3.Lerp(start, end, t);

            Vector3 normal =
                terrain.terrainData.GetInterpolatedNormal(
                    (p.x - terrain.transform.position.x)
                    / terrain.terrainData.size.x,

                    (p.z - terrain.transform.position.z)
                    / terrain.terrainData.size.z
                );

            float slope =
                Vector3.Angle(
                    normal,
                    Vector3.up
                );

            if(slope > 40f)
                return true;
        }

        return false;
    }

    void RecoverStamina()
    {
        stamina += staminaRecoveryPerSecond * Time.deltaTime;

        stamina = Mathf.Clamp(stamina, 0f, 100f);

        animator.SetFloat("Speed", 0f);
        animator.SetFloat("MotionSpeed", 0f);

        if (stamina >= restartThreshold)
        {
            isResting = false;
        }
    }

    void MoveToTarget()
    {
        //debug log
        Debug.Log("Avoid:" + isAvoiding);
        Debug.Log("HasObstacle:" + IsObstacleAhead());
        Debug.Log("Distance:" +
        Vector3.Distance(transform.position, targetPosition));


        stamina -= staminaDecreasePerSecond * Time.deltaTime;
        stamina = Mathf.Clamp(stamina, 0f, 100f);

        if (stamina <= 0f)
        {
            stamina = 0f;

            isResting = true;

            hasTarget = false;

            animator.SetFloat("Speed", 0f);
            animator.SetFloat("MotionSpeed", 0f);

            return;
        }

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

            Vector3 nextPos =
                transform.position +
                transform.forward * moveSpeed * Time.deltaTime;

            if (!IsInsideTerrain(nextPos))
            {
                avoidTimer = 0f;
                isAvoiding = false;
                hasTarget = false;
                return;
            }

            controller.Move(avoidMove * Time.deltaTime);

            if (avoidTimer <= 0)
            {
                isAvoiding = false;
            }

            return;
        }

        if (IsObstacleAhead() && !isAvoiding)
        {
            avoidDirection = Random.value < 0.5f ? -1f : 1f;
            avoidRotation = Quaternion.Euler(0, transform.eulerAngles.y + 45f + avoidDirection * 60f, 0);

            avoidTimer = 4.0f;

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

       /* Vector3 dir = targetPosition - transform.position;

        dir.y = 0;

        if (dir != Vector3.zero)
        {
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(dir),
                5f * Time.deltaTime
            );
        }*/
        animator.SetFloat("Speed", moveSpeed);
        animator.SetFloat("MotionSpeed", 1f);
        float dist = Vector3.Distance(
            transform.position,
            targetPosition
        );

        if (dist < 4f)
        {
            hasTarget = false;
        }

        /*
        Debug.DrawLine(
            transform.position,
            targetPosition,
            Color.green
        );
        Debug.Log(controller.velocity);
        */
    }

    public void OnFootstep()
    {
    }

    public float GetStamina()
    {
        return stamina;
    }
}