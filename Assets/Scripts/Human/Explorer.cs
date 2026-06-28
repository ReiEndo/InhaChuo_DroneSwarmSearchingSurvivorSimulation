using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(CharacterController))]
public class Explorer : MonoBehaviour
{
    private CharacterController controller;//移動方法にCharacterController.Moveを採用

    [Header("Terrain")]
    public Terrain terrain;

    [Header("探索")]
    public float scanRadius = 20f;  //目標地点最大範囲
    public float minTargetDistance = 10f;   //目標地点最小範囲
    public float moveSpeed = 3f;    //移動速度
    [SerializeField] private float edgeCenterSteerDistance = 20f;
    [Range(0f, 1f)]
    [SerializeField] private float edgeCenterSteerStrength = 0.85f;
    public float obstacleCheckDistance = 1f;    //障害物検知距離
    public float obstacleAvoidDuration = 1.0f;  //回避方向を維持する時間
    public float obstacleAvoidAngle = 55f;      //回避時に左右へ曲がる角度
    private float avoidTimer = 0f;  //0f<=移動中 or 0f>回避中
    public float terrainMargin = 10f;   //terrain境界からどこまでをNGとするか
    //[SerializeField] private int maxTargetSearchAttemptsPerFrame = 32;
    [SerializeField] private int fallbackTargetSearchAttempts = 96;
    [SerializeField] private float fallbackMinTargetDistance = 3f;

    private Vector3 targetPosition;     //目標地点
    private bool hasTarget = false;     //目標地点が定まっているか

    [Header("ドローン発見後")]
    private bool stoppedAfterDroneFound; //ドローンに発見されたら停止

    private float verticalVelocity;     //重力用
    private float nextTargetSearchFailureLogAt;
    
    public LayerMask obstacleMask;      //地面の障害物判定を除外
    private bool isAvoiding = false;    //回避開始時だけ回転するようフラッグ
    private Quaternion avoidRotation;   //瞬時に回転しないよう
    
    [Header("体力")]
    [Range(0, 100)]
    public float stamina = 100f; //体力フル
    public float staminaDecreasePerSecond = 5f;     //体力減少速度
    public float staminaRecoveryPerSecond = 7f;     //体力回復速度
    public float recoveryDecay = 0.2f;              //疲労
    public float restartThreshold = 50f;  //休憩→移動への体力必要値
    private bool isResting = false;  //true:移動 false:休憩
    [SerializeField] private AnimationClip takingRestClip;
    [SerializeField] private string takingRestClipName = "TakingRest";
    private PlayableGraph restAnimationGraph;
    private AnimationClipPlayable restAnimationPlayable;

    [Header("スタック判定")]
    private float lastDistanceToTarget;
    public float stuckCheckInterval = 3f;   //スタック確認時間間隔
    public float stuckDistanceThreshold = 1f;
    private float stuckTimer = 0f;      //スタックタイマー

    [Header("Slope Map")]
    public float maxWalkableSlope = 35f;
    public int slopePathSamples = 30;
    private bool[,] blockedSlopeMap;
    private int slopeResolution;

    [Header("Random Spawn")]
    public ForestSpawner forestSpawner;
    public float initialTreeDistance = 5f;
    public int initialSpawnMaxAttempts = 1000;
    public float initialSpawnYOffset = 0.1f;
    public float initialSpawnCheckRadius = 1.0f;
    public float initialMaxSlope = 35f;

    [Header("Drone Announcement Hearing")]
    public float droneAnnouncementInterval = 10f;
    public float droneHearingDistance = 10f;
    private float droneAnnouncementTimer = 0f;
    private Transform nearestDrone;

    /*
    Scripts\ScriptControl\ScriptsControl.csにて制御
    void Start() //起動時
    {
        animator = GetComponent<Animator>();

        controller = GetComponent<CharacterController>();

        TeleportToRandomInitialPosition();
        BuildSlopeMap();
    }
    */

    [SerializeField] private Animator animator;

    public void ExplorerSpawner() //ScriptsControl,csのvoid Start()にて起動
    {
        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>();
        }
        ResolveTakingRestClip();

        controller = GetComponent<CharacterController>();

        TeleportToRandomInitialPosition();

        BuildSlopeMap();
        lastDistanceToTarget = 0f;
    }

    void Update() //フレームごとの更新
    {
        if (stoppedAfterDroneFound)
        {
            StopRestAnimation();
            SetIdleAnimation();
            return;
        }

        CheckStuck();
        droneAnnouncementTimer += Time.deltaTime;

        if(droneAnnouncementTimer >= droneAnnouncementInterval)
        {
            OnDroneAnnouncement();
            droneAnnouncementTimer = 0f;
        }

        if (isResting)
        {
            PlayRestAnimation();
            RecoverStamina();
            return;
        }

        while (!hasTarget)
        {
            TryFindUnknownTarget();
            return;
        }

        StopRestAnimation();
        MoveToTarget();
    }

    public bool IsStoppedAfterDroneFound => stoppedAfterDroneFound;

    public void StopAfterFoundByDrone()
    {
        stoppedAfterDroneFound = true;
        hasTarget = false;
        isResting = false;
        isAvoiding = false;
        avoidTimer = 0f;
        verticalVelocity = 0f;
        lastDistanceToTarget = 0f;
        StopRestAnimation();
        SetIdleAnimation();
    }

    void BuildSlopeMap()
    {
        Terrain targetTerrain = terrain != null ? terrain : Terrain.activeTerrain;
        if (targetTerrain == null || targetTerrain.terrainData == null)
        {
            blockedSlopeMap = null;
            slopeResolution = 0;
            return;
        }

        terrain = targetTerrain;
        TerrainData data = terrain.terrainData;
        slopeResolution = data.heightmapResolution;
        if (slopeResolution <= 1)
        {
            blockedSlopeMap = null;
            return;
        }

        blockedSlopeMap = new bool[slopeResolution, slopeResolution];

        for (int x = 0; x < slopeResolution; x++)
        {
            float nx = x / (float)(slopeResolution - 1);

            for (int z = 0; z < slopeResolution; z++)
            {
                float nz = z / (float)(slopeResolution - 1);

                Vector3 normal = data.GetInterpolatedNormal(nx, nz);
                float slope = Vector3.Angle(normal, Vector3.up);

                blockedSlopeMap[x, z] = slope > maxWalkableSlope;
            }
        }
    }

    bool IsBlockedSlope(Vector3 worldPos)
    {
        Terrain targetTerrain = terrain != null ? terrain : Terrain.activeTerrain;
        if (targetTerrain == null || targetTerrain.terrainData == null)
        {
            return true;
        }

        terrain = targetTerrain;
        if (blockedSlopeMap == null || slopeResolution <= 1)
        {
            BuildSlopeMap();
        }

        if (blockedSlopeMap == null || slopeResolution <= 1)
        {
            return true;
        }

        TerrainData data = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;

        float nx = (worldPos.x - terrainPos.x) / data.size.x;
        float nz = (worldPos.z - terrainPos.z) / data.size.z;

        if (nx < 0f || nz < 0f || nx > 1f || nz > 1f)
        {
            return true;
        }

        int x = Mathf.RoundToInt(nx * (slopeResolution - 1));
        int z = Mathf.RoundToInt(nz * (slopeResolution - 1));

        return blockedSlopeMap[x, z];
    }

    void OnDroneAnnouncement() //音の届く距離にdroneがいるかどうか
    {
        DroneFrontierExplorer[] drones = FindObjectsByType<DroneFrontierExplorer>();

        float nearestDistance = Mathf.Infinity;

        nearestDrone = null;

        foreach(var drone in drones)
        {
            float dist = Vector3.Distance(transform.position, drone.transform.position);

            if(dist < nearestDistance)
            {
                nearestDistance = dist;
                nearestDrone = drone.transform;
            }
        }

        if(nearestDrone == null)
            return;

        if(nearestDistance > droneHearingDistance)
            return;

        SetTargetTowardDrone();
    }

    void SetTargetTowardDrone() //droneの方向へ向かう
    {
        Vector3 target = nearestDrone.position;

        target.y = terrain.SampleHeight(target);

        if(IsInsideTerrain(target) && IsValidPoint(target) && !CrossBlockedSlope(transform.position, target))
        {
            targetPosition = target;
            hasTarget = true;
        }
    }

    void CheckStuck() //スタック時目的地リセット
    {
        if (!hasTarget)
        {
            stuckTimer = 0f;
            return;
        }

        stuckTimer += Time.deltaTime;

        if (stuckTimer < stuckCheckInterval) return;

        float currentDistance = Vector3.Distance(transform.position, targetPosition);
        float progress = lastDistanceToTarget - currentDistance;

        if (progress < stuckDistanceThreshold)
        {
            Debug.Log("Stuck");
            hasTarget = false;
            isAvoiding = false;
            avoidTimer = 0f;
            lastDistanceToTarget = 0f;
        }
        else
        {
            lastDistanceToTarget = currentDistance;
        }

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

    void TryFindUnknownTarget() //目標地点決定
    {
        Terrain targetTerrain = terrain != null ? terrain : Terrain.activeTerrain;
        if (targetTerrain == null)
        {
            LogTargetSearchFailure(0, 0, 0, 0, "no terrain");
            return;
        }

        terrain = targetTerrain;
        //int attempts = Mathf.Max(1, maxTargetSearchAttemptsPerFrame);
        int outsideTerrainCount = 0;
        int obstacleBlockedCount = 0;
        int slopeBlockedCount = 0;
        float centerSteerWeight = GetCenterSteerWeight(targetTerrain);
        Vector3 centerDirection = GetTerrainCenterDirection(targetTerrain);

    
        float angle = Random.Range(-60f, 60f);
        Vector3 dir = Quaternion.Euler(0, angle, 0) * transform.forward;
        dir.y = 0f;

        if (centerSteerWeight > 0f && centerDirection.sqrMagnitude > 0.0001f)
        {
            dir = Vector3.Slerp(dir.normalized, centerDirection, centerSteerWeight);
        }

        float distance = Random.Range(minTargetDistance, scanRadius);

        Vector3 target = transform.position + dir * distance;

        target.y = targetTerrain.SampleHeight(target) + targetTerrain.transform.position.y;

        if (!IsInsideTerrain(target))
        {
            outsideTerrainCount++;
            return;
        }

        if (!IsValidPoint(target))
        {
            obstacleBlockedCount++;
            return;
        }

        if (CrossBlockedSlope(transform.position, target))
        {
            slopeBlockedCount++;
            return;
        }

        hasTarget = true;
        targetPosition = target;
        lastDistanceToTarget = Vector3.Distance(transform.position, targetPosition);
        stuckTimer = 0f;
        return;
        


        angle = Random.Range(0f, 360f);
        dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;

        if (centerSteerWeight > 0f && centerDirection.sqrMagnitude > 0.0001f)
        {
            dir = Vector3.Slerp(dir.normalized, centerDirection, centerSteerWeight);
        }

        float minDistance = Mathf.Clamp(fallbackMinTargetDistance, 0.5f, scanRadius);
        distance = Random.Range(minDistance, scanRadius);
        target = transform.position + dir * distance;

        target.y = targetTerrain.SampleHeight(target) + targetTerrain.transform.position.y;

        if (!IsInsideTerrain(target))
        {
            outsideTerrainCount++;
            return;
        }

        if (!IsValidPoint(target))
        {
            obstacleBlockedCount++;
            return;
        }

        if (CrossBlockedSlope(transform.position, target))
        {
            slopeBlockedCount++;
            return;
        }

        hasTarget = true;
        targetPosition = target;
        lastDistanceToTarget = Vector3.Distance(transform.position, targetPosition);
        stuckTimer = 0f;
        return;

        //LogTargetSearchFailure(attempts + fallbackAttempts, outsideTerrainCount, obstacleBlockedCount, slopeBlockedCount);
    }

    float GetCenterSteerWeight(Terrain targetTerrain)
    {
        if (targetTerrain == null || targetTerrain.terrainData == null || edgeCenterSteerDistance <= 0f)
        {
            return 0f;
        }

        Vector3 terrainPos = targetTerrain.transform.position;
        Vector3 terrainSize = targetTerrain.terrainData.size;
        float minX = terrainPos.x + terrainMargin;
        float maxX = terrainPos.x + terrainSize.x - terrainMargin;
        float minZ = terrainPos.z + terrainMargin;
        float maxZ = terrainPos.z + terrainSize.z - terrainMargin;

        float distanceToInnerEdge = Mathf.Min(
            transform.position.x - minX,
            maxX - transform.position.x,
            transform.position.z - minZ,
            maxZ - transform.position.z
        );

        float edgeWeight = Mathf.InverseLerp(edgeCenterSteerDistance, 0f, distanceToInnerEdge);
        return Mathf.Clamp01(edgeWeight * edgeCenterSteerStrength);
    }

    Vector3 GetTerrainCenterDirection(Terrain targetTerrain)
    {
        if (targetTerrain == null || targetTerrain.terrainData == null)
        {
            return Vector3.zero;
        }

        Vector3 terrainPos = targetTerrain.transform.position;
        Vector3 terrainSize = targetTerrain.terrainData.size;
        Vector3 center = new Vector3(
            terrainPos.x + terrainSize.x * 0.5f,
            transform.position.y,
            terrainPos.z + terrainSize.z * 0.5f
        );

        Vector3 direction = center - transform.position;
        direction.y = 0f;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.zero;
    }

    void LogTargetSearchFailure(
        int attempts,
        int outsideTerrainCount,
        int obstacleBlockedCount,
        int slopeBlockedCount,
        string reason = null
    )
    {
        if (Time.time < nextTargetSearchFailureLogAt)
        {
            return;
        }

        nextTargetSearchFailureLogAt = Time.time + 1f;
        string suffix = string.IsNullOrEmpty(reason) ? string.Empty : $", reason={reason}";
        Debug.Log(
            $"[Explorer Stop] target search failed: attempts={attempts}, outside={outsideTerrainCount}, obstacle={obstacleBlockedCount}, slope={slopeBlockedCount}{suffix}"
        );
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

    bool CrossBlockedSlope(Vector3 start, Vector3 end) //現在地点→目標地点　急な斜面防止
    {
        if (terrain == null)
        {
            terrain = Terrain.activeTerrain;
        }

        if (terrain == null)
        {
            return true;
        }

        if (blockedSlopeMap == null || slopeResolution <= 1)
        {
            BuildSlopeMap();
        }

        int samples = Mathf.Max(1, slopePathSamples);

        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector3 p = Vector3.Lerp(start, end, t);

            if (IsBlockedSlope(p))
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
            StopRestAnimation();

            staminaRecoveryPerSecond -= recoveryDecay;
            staminaRecoveryPerSecond = Mathf.Max(0f, staminaRecoveryPerSecond);
        }
    }

    //改善の必要あり
    void MoveToTarget(bool consumeStamina = true)
    {
        StopRestAnimation();

        if (!hasTarget)
        {
            return;
        }

        if (CrossBlockedSlope(transform.position, targetPosition))
        {
            Debug.Log("[Explorer Stop] path blocked by slope");
            hasTarget = false;
            isAvoiding = false;
            avoidTimer = 0f;
            SetIdleAnimation();
            return;
        }

        if (consumeStamina)
        {
            stamina -= staminaDecreasePerSecond * Time.deltaTime;
            stamina = Mathf.Clamp(stamina, 0f, 100f);

            if (stamina <= 0f)
            {
                Debug.Log("[Explorer Stop] stamina empty");
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
                Debug.Log("[Explorer Stop] avoidance would leave terrain");
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

        if (dist < 4f)
        {
            hasTarget = false;
            lastDistanceToTarget = 0f;
        }
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

    /*Explorer初期位置テレポート*/
    public void TeleportToRandomInitialPosition()
    {
        Terrain targetTerrain = terrain != null ? terrain : Terrain.activeTerrain;
        if(targetTerrain == null)
        {
            Debug.LogError("[Explorer.cs:Set Terrain on Explorers Inspector]");
            return;
        }

        terrain = targetTerrain;
        TerrainData terrainData = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;

        CharacterController cc = controller != null ? controller : GetComponent<CharacterController>();

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
            stoppedAfterDroneFound = false;
            isResting = false;
            isAvoiding = false;
            avoidTimer = 0f;
            verticalVelocity = 0f;
            lastDistanceToTarget = 0f;
            StopRestAnimation();

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

    void PlayRestAnimation()
    {
        if (animator == null || takingRestClip == null)
        {
            SetIdleAnimation();
            return;
        }

        if (restAnimationGraph.IsValid())
        {
            if (restAnimationPlayable.IsValid() && takingRestClip.length > 0f)
            {
                double time = restAnimationPlayable.GetTime();
                if (time >= takingRestClip.length)
                {
                    restAnimationPlayable.SetTime(time % takingRestClip.length);
                    restAnimationPlayable.SetDone(false);
                }
            }
            return;
        }

        restAnimationGraph = PlayableGraph.Create($"{name} TakingRest");
        restAnimationGraph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
        restAnimationPlayable = AnimationClipPlayable.Create(restAnimationGraph, takingRestClip);
        restAnimationPlayable.SetApplyFootIK(false);

        var output = AnimationPlayableOutput.Create(restAnimationGraph, "TakingRest", animator);
        output.SetSourcePlayable(restAnimationPlayable);
        restAnimationGraph.Play();
    }

    void StopRestAnimation()
    {
        if (restAnimationGraph.IsValid())
        {
            restAnimationGraph.Destroy();
        }
    }

    void OnDisable()
    {
        StopRestAnimation();
    }

    void OnDestroy()
    {
        StopRestAnimation();
    }

    void ResolveTakingRestClip()
    {
        if (takingRestClip != null)
        {
            return;
        }

#if UNITY_EDITOR
        foreach (string guid in AssetDatabase.FindAssets($"{takingRestClipName} t:AnimationClip"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is AnimationClip clip && clip.name == takingRestClipName)
                {
                    takingRestClip = clip;
                    return;
                }
            }
        }
#endif
    }
}
