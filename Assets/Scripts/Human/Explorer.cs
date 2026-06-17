using UnityEngine;

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
    public float obstacleCheckDistance = 2.0f;    //障害物検知距離
    private float avoidTimer = 0f;  //0f<=移動中 or 0f>回避中
    public float terrainMargin = 10f;   //terrain境界からどこまでをNGとするか

    private Vector3 targetPosition;     //目標地点
    private bool hasTarget = false;     //目標地点が定まっているか

    private float verticalVelocity;     //重力用
    
    public LayerMask obstacleMask;      //地面の障害物判定を除外
    private float avoidDirection;       //回避回転の左右ランダム化
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
    private float lastDistanceToTarget;
    public float stuckCheckInterval = 3f;   //スタック確認時間間隔
    public float stuckDistanceThreshold = 3f;   //スタック判定最低距離
    private Vector3 lastCheckPosition;     //最新現在地点
    private float stuckTimer = 0f;      //スタックタイマー

    [Header("Slope Map")]
    public float maxWalkableSlope = 35f;
    private bool[,] blockedSlopeMap;
    private int slopeResolution;

    [Header("Random Spawn")]
    public ForestSpawner forestSpawner;
    public float initialTreeDistance = 5f;
    public int initialSpawnMaxAttempts = 1000;
    public float initialSpawnYOffset = 0.1f;
    public float initialSpawnCheckRadius = 1.0f;
    public float initialMaxSlope = 35f;

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
        TeleportToRandomInitialPosition();

        BuildSlopeMap();

        lastCheckPosition = transform.position;

        animator = GetComponent<Animator>();

        controller = GetComponent<CharacterController>();
    }

    void Update() //フレームごとの更新
    {
        CheckStuck();
        if (isResting)
        {
            RecoverStamina();
            return;
        }

        while(!hasTarget)
        {
            FindUnknownTarget();
            lastDistanceToTarget = Vector3.Distance(transform.position,targetPosition);
        }

        MoveToTarget();
    }

    void BuildSlopeMap()
    {
        TerrainData data = terrain.terrainData;

        slopeResolution = data.heightmapResolution;

        blockedSlopeMap =
            new bool[slopeResolution, slopeResolution];

        for(int x = 0; x < slopeResolution; x++)
        {
            for(int z = 0; z < slopeResolution; z++)
            {
                float nx =
                    x / (float)(slopeResolution - 1);

                float nz =
                    z / (float)(slopeResolution - 1);

                Vector3 normal =
                    data.GetInterpolatedNormal(
                        nx,
                        nz
                    );

                float slope =
                    Vector3.Angle(
                        normal,
                        Vector3.up
                    );

                blockedSlopeMap[x, z] =
                    slope > maxWalkableSlope;
            }
        }
    }

    bool IsBlockedSlope(Vector3 worldPos)
    {
        Vector3 terrainPos =
            terrain.transform.position;

        TerrainData data =
            terrain.terrainData;

        float nx =
            (worldPos.x - terrainPos.x)
            / data.size.x;

        float nz =
            (worldPos.z - terrainPos.z)
            / data.size.z;

        int x =
            Mathf.RoundToInt(
                nx * (slopeResolution - 1)
            );

        int z =
            Mathf.RoundToInt(
                nz * (slopeResolution - 1)
            );

        if (x < 0 ||
            z < 0 ||
            x >= slopeResolution ||
            z >= slopeResolution)
        {
            return true;
        }

        return blockedSlopeMap[x, z];
    }

    void CheckStuck()
    {
        if (!hasTarget) return;

        stuckTimer += Time.deltaTime;

        if (stuckTimer < stuckCheckInterval)
            return;

        float currentDistance =
            Vector3.Distance(
                transform.position,
                targetPosition
            );

        float progress =
            lastDistanceToTarget -
            currentDistance;

        if (progress < 1.0f)
        {
            Debug.Log("Stuck");

            hasTarget = false;
            isAvoiding = false;
            avoidTimer = 0f;
        }

        lastDistanceToTarget = currentDistance;
        stuckTimer = 0f;
    }

    bool IsObstacleAhead() //障害物検知
    {
        Vector3 origin = transform.position + Vector3.up * 0.8f;

        float radius = controller.radius*0.9f;

        Debug.DrawRay(
            origin,
            transform.forward * obstacleCheckDistance,
            Color.red
        );

        return Physics.SphereCast(
            origin,
            radius,
            transform.forward,
            out _,
            obstacleCheckDistance,
            obstacleMask
        );
    }

    void FindUnknownTarget() //目標地点決定
    {
        float angle = Random.Range(-60f, 60f);
        Vector3 dir = Quaternion.Euler(0, angle, 0) * transform.forward;
        float distance = Random.Range(minTargetDistance, scanRadius);

        Vector3 target = transform.position + dir * distance;

        target.y = Terrain.activeTerrain.SampleHeight(target);

        if(IsInsideTerrain(target) && IsValidPoint(target) && !CrossBlockedSlope(transform.position, target)) 
        {
            hasTarget = true;
            targetPosition = target;

            return;
        }
    }

    bool IsInsideTerrain(Vector3 point) //terrain範囲外への移動防止
    {
        Vector3 terrainPos = terrain.transform.position;

        Vector3 terrainSize = terrain.terrainData.size;

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

    bool CrossBlockedSlope(Vector3 start, Vector3 end)
    {
        int samples = 30;

        for(int i = 0; i <= samples; i++)
        {
            float t =
                i / (float)samples;

            Vector3 p =
                Vector3.Lerp(
                    start,
                    end,
                    t
                );

            if(IsBlockedSlope(p))
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

    //改善の必要あり
    void MoveToTarget()
    {
        //debug log
        Debug.Log("Avoid:" + isAvoiding);
        Debug.Log("HasObstacle:" + IsObstacleAhead());
        Debug.Log("Distance:" +
        Vector3.Distance(transform.position, targetPosition));

        if(CrossBlockedSlope(transform.position, targetPosition))
        {
            hasTarget = false;
            return;
        }

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
            avoidDirection = Random.value < 0.5f ? -1f : 1f;
            avoidRotation = Quaternion.Euler(0, transform.eulerAngles.y + 45f + avoidDirection * 60f, 0);

            avoidTimer = 5.0f;

            isAvoiding = true;
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

        animator.SetFloat("Speed", moveSpeed);
        animator.SetFloat("MotionSpeed", 1f);
        float dist = Vector3.Distance(
            transform.position,
            targetPosition
        );

        if (dist < 4f) hasTarget = false;
    }

    /*Explorer初期位置テレポート*/
    public void TeleportToRandomInitialPosition()
    {
        if(terrain == null)
        {
            Debug.LogError("[Explorer.cs:Set Terrain on Explorers Inspector]");
            return;
        }
        TerrainData terrainData = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;

        CharacterController cc = GetComponent <CharacterController>();

        for (int attempt = 0; attempt < initialSpawnMaxAttempts; attempt++)
        {
            float randomX = Random.Range(terrainMargin, terrainData.size.x - terrainMargin);
            float randomZ = Random.Range(terrainMargin, terrainData.size.z - terrainMargin);

            float worldX = terrainPos.x + randomX;
            float worldZ = terrainPos.z + randomZ;

            float y = terrain.SampleHeight(new Vector3(worldX, 0f, worldZ)) + terrainPos.y;

            Vector3 candidatePosition = new Vector3(worldX, y + initialSpawnYOffset, worldZ);

            if (!IsInsideTerrain(candidatePosition)) continue;

            if (!IsValidInitialSpawnPoint(candidatePosition)) continue;

            if (!IsSlopeValidForInitialSpawn(candidatePosition)) continue;

            if(forestSpawner !=  null && !forestSpawner.IsFarEnoughFromTrees(candidatePosition, initialTreeDistance)) continue;

            if (cc != null) cc.enabled = false;

            transform.position = candidatePosition;

            if (cc != null) cc.enabled = true;

            hasTarget = false;
            isAvoiding = false;
            avoidTimer = 0f;
            verticalVelocity = 0f;
            lastCheckPosition = transform.position;

            Debug.Log("Explorer position set: " + candidatePosition);

            return;
        }
        Debug.LogWarning("[Explorer.cs]: cannot set Explorer on terrain safe position");
    }
    bool IsValidInitialSpawnPoint(Vector3 point)
    {
        Vector3 checkCenter = point + Vector3.up * 1.0f;

        bool blocked = Physics.CheckSphere(
            checkCenter,
            initialSpawnCheckRadius,
            obstacleMask,
            QueryTriggerInteraction.Collide
        );

        return !blocked;
    }
    bool IsSlopeValidForInitialSpawn(Vector3 point)
    {
        Vector3 terrainPos = terrain.transform.position;
        Vector3 terrainSize = terrain.terrainData.size;

        float normalizedX =
            (point.x - terrainPos.x) / terrainSize.x;

        float normalizedZ =
            (point.z - terrainPos.z) / terrainSize.z;

        Vector3 normal =
            terrain.terrainData.GetInterpolatedNormal(
                normalizedX,
                normalizedZ
            );

        float slope = Vector3.Angle(normal, Vector3.up);

        return slope <= initialMaxSlope;
    }

}