using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class Explorer : MonoBehaviour
{
    private Animator animator;
    private CharacterController controller;//移動方法にCharacterController.Moveを採用

    [Header("Terrain")]
    public Terrain terrain;

    [Header("探索")]
    public float scanRadius = 20f;  //目標地点最大範囲
    public float minTargetDistance = 10f;   //目標地点最小範囲
    public float moveSpeed = 3f;    //移動速度
    public float obstacleCheckDistance = 1f;    //障害物検知距離
    public float obstacleAvoidDuration = 1.0f;  //回避方向を維持する時間
    public float obstacleAvoidAngle = 55f;      //回避時に左右へ曲がる角度
    private float avoidTimer = 0f;  //0f<=移動中 or 0f>回避中
    public float terrainMargin = 10f;   //terrain境界からどこまでをNGとするか
    [SerializeField] private int maxTargetSearchAttemptsPerFrame = 32;

    private Vector3 targetPosition;     //目標地点
    private bool hasTarget = false;     //目標地点が定まっているか

    [Header("ドローン追従")]
    public float followStopDistance = 3f; //ドローンに近づきすぎない距離
    public float followBehindDistance = 4f; //ドローンの少し後ろを目標にする距離
    private Transform followTarget; //最初に発見したドローン

    private float verticalVelocity;     //重力用
    
    public LayerMask obstacleMask;      //地面の障害物判定を除外
    private bool isAvoiding = false;    //回避開始時だけ回転するようフラッグ
    private Quaternion avoidRotation;   //瞬時に回転しないよう
    
    [Header("体力")]
    [Range(0, 100)]
    public float stamina = 100f; //体力フル
    public float staminaDecreasePerSecond = 5f;     //体力減少速度
    public float staminaRecoveryPerSecond = 7f;     //体力回復速度
    public float restartThreshold = 50f;  //休憩→移動への体力必要値
    private bool isResting = false;  //true:移動 false:休憩

    [Header("スタック判定")]
    public float stuckCheckInterval = 3f;   //スタック確認時間間隔
    public float stuckDistanceThreshold = 1f;   //スタック判定最低距離
    private Vector3 lastCheckPosition;     //最新現在地点
    private float stuckTimer = 0f;      //スタックタイマー

    /*
    Scripts\ScriptControl\ScriptsControl.csにて制御
    void Start() //起動時
    {
        lastCheckPosition = transform.position;

        animator = GetComponent<Animator>();

        controller = GetComponent<CharacterController>();
    }
    */

    public void ExplorerSpawner() //ScriptsControl,csのvoid Start()にて起動
    {
        lastCheckPosition = transform.position;

        animator = GetComponent<Animator>();

        controller = GetComponent<CharacterController>();
    }

    void Update() //フレームごとの更新
    {
        CheckStuck();
        if (followTarget != null)
        {
            FollowDrone();
            return;
        }

        if (isResting)
        {
            RecoverStamina();
            return;
        }

        if (!hasTarget && !TryFindUnknownTarget())
        {
            SetIdleAnimation();
            return;
        }

        MoveToTarget();
    }

    public bool IsFollowingDrone => followTarget != null;

    public void StartFollowing(Transform droneTransform)
    {
        if (followTarget != null || droneTransform == null)
        {
            return;
        }

        followTarget = droneTransform;
        isResting = false;
        hasTarget = true;
        isAvoiding = false;
        avoidTimer = 0f;
    }

    void CheckStuck() //スタック時目的地リセット
    {
        stuckTimer += Time.deltaTime;

        if (stuckTimer < stuckCheckInterval) return;

        float moved = Vector3.Distance(transform.position, lastCheckPosition);

        if (moved < stuckDistanceThreshold)
        {
            if (followTarget == null)
            {
                hasTarget = false;
            }
            isAvoiding = false;
            avoidTimer = 0f;
        }

        lastCheckPosition = transform.position;
        stuckTimer = 0f;
    }

    bool IsObstacleAhead() //障害物検知
    {
        return GetObstacleClearance(transform.forward, obstacleCheckDistance) < obstacleCheckDistance;
    }

    float GetObstacleClearance(Vector3 direction, float maxDistance)
    {
        Vector3 origin = transform.position + Vector3.up * 0.8f;
        float radius = controller.radius * 0.9f;
        direction.y = 0f;
        direction.Normalize();

        Debug.DrawRay(origin, direction * maxDistance, Color.red);

        if (Physics.SphereCast(origin, radius, direction, out RaycastHit hit, maxDistance, obstacleMask))
        {
            return hit.distance;
        }

        return maxDistance;
    }

    void StartAvoidance()
    {
        Vector3 leftDir = Quaternion.Euler(0f, -obstacleAvoidAngle, 0f) * transform.forward;
        Vector3 rightDir = Quaternion.Euler(0f, obstacleAvoidAngle, 0f) * transform.forward;

        float checkDistance = Mathf.Max(obstacleCheckDistance, controller.radius * 2f);
        float leftClearance = GetObstacleClearance(leftDir, checkDistance);
        float rightClearance = GetObstacleClearance(rightDir, checkDistance);
        Vector3 chosenDir = rightClearance >= leftClearance ? rightDir : leftDir;

        avoidRotation = Quaternion.LookRotation(chosenDir);
        avoidTimer = obstacleAvoidDuration;
        isAvoiding = true;
    }

    bool TryFindUnknownTarget() //目標地点決定
    {
        Terrain targetTerrain = terrain != null ? terrain : Terrain.activeTerrain;
        if (targetTerrain == null)
        {
            return false;
        }

        terrain = targetTerrain;
        int attempts = Mathf.Max(1, maxTargetSearchAttemptsPerFrame);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            float angle = Random.Range(-60f, 60f);
            Vector3 dir = Quaternion.Euler(0, angle, 0) * transform.forward;
            float distance = Random.Range(minTargetDistance, scanRadius);

            Vector3 target = transform.position + dir * distance;

            target.y = targetTerrain.SampleHeight(target) + targetTerrain.transform.position.y;

            if(IsInsideTerrain(target) && IsValidPoint(target) && !HasSteepSlopeOnPath(transform.position,target)) 
            {
                hasTarget = true;
                targetPosition = target;
                return true;
            }
        }

        return false;
    }

    bool IsInsideTerrain(Vector3 point) //terrain範囲外への移動防止
    {
        Terrain targetTerrain = terrain != null ? terrain : Terrain.activeTerrain;
        if (targetTerrain == null)
        {
            return false;
        }

        Vector3 terrainPos = targetTerrain.transform.position;

        Vector3 terrainSize = targetTerrain.terrainData.size;

        bool insideX =
            point.x >= terrainPos.x + terrainMargin &&
            point.x <= terrainPos.x + terrainSize.x - terrainMargin;

        bool insideZ =
            point.z >= terrainPos.z + terrainMargin &&
            point.z <= terrainPos.z + terrainSize.z - terrainMargin;

        return insideX && insideZ;
    }

    bool IsValidPoint(Vector3 point) //目標地点が障害物と重なること防止
    {
        float checkRadius = 2.0f;

        bool blocked = Physics.CheckSphere(point, checkRadius, obstacleMask);

        return !blocked;
    }

    //改善の必要あり
    bool HasSteepSlopeOnPath(Vector3 start, Vector3 end) //現在地点→目標地点　急な斜面防止
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

        SetIdleAnimation();

        if (stamina >= restartThreshold)
        {
            isResting = false;
        }
    }

    void FollowDrone()
    {
        if (followTarget == null) return;

        targetPosition = followTarget.position - followTarget.forward * followBehindDistance;
        if (terrain != null)
        {
            targetPosition.y = terrain.SampleHeight(targetPosition) + terrain.transform.position.y;
        }
        hasTarget = true;

        if (Vector3.Distance(transform.position, targetPosition) <= followStopDistance)
        {
            SetIdleAnimation();
            return;
        }

        MoveToTarget(false);
    }

    //改善の必要あり
    void MoveToTarget(bool consumeStamina = true)
    {
        if (consumeStamina)
        {
            stamina -= staminaDecreasePerSecond * Time.deltaTime;
            stamina = Mathf.Clamp(stamina, 0f, 100f);

            if (stamina <= 0f)
            {
                stamina = 0f;
                isResting = true;
                hasTarget = false;

                SetIdleAnimation();

                return;
            }
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

            Vector3 avoidMove = transform.forward * moveSpeed;

            avoidMove.y = verticalVelocity;

            Vector3 nextPos = transform.position + transform.forward * moveSpeed * Time.deltaTime;

            if (!IsInsideTerrain(nextPos))
            {
                hasTarget = false;
                isAvoiding = false;
                avoidTimer = 0f;
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
            StartAvoidance();
        }
        if (!hasTarget) return;
        Vector3 currentPos = transform.position;

        Vector3 targetDir = targetPosition - transform.position;

        targetDir.y = 0;

        if (targetDir != Vector3.zero)
        {
            Quaternion targetRot = Quaternion.LookRotation(targetDir);

            transform.rotation =
                Quaternion.Slerp(
                    transform.rotation,
                    targetRot,
                    2f * Time.deltaTime
                );
        }

        if (controller.isGrounded) verticalVelocity = -1f;
        else verticalVelocity += Physics.gravity.y * Time.deltaTime;

        Vector3 move = transform.forward * moveSpeed;

        move.y = verticalVelocity;

        controller.Move(move * Time.deltaTime);

        if (animator != null)
        {
            animator.SetFloat("Speed", moveSpeed);
            animator.SetFloat("MotionSpeed", 1f);
        }
        float dist = Vector3.Distance(
            transform.position,
            targetPosition
        );

        if (dist < 4f && followTarget == null) hasTarget = false;
    }

    private void SetIdleAnimation()
    {
        if (animator == null)
        {
            return;
        }

        animator.SetFloat("Speed", 0f);
        animator.SetFloat("MotionSpeed", 0f);
    }
}