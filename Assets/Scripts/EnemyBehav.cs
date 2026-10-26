using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(EnemyDistanceMovement))]
[RequireComponent(typeof(Rigidbody))]
public class EnemyBehav : MonoBehaviour
{
    public enum EnemyState { Idle, Chasing, Windup, Recovery, Dead, Attacking }

    [Header("Target / 目标")]
    public Transform target;
    [Min(0f)] public float detectionDistance = 15f;
    [Header("Movement / 移动")]
    [Min(0f)] public float moveSpeed = 2.5f;
    [Header("Ranged attack / 远程射击")]
    [Min(0.1f), Tooltip("允许开始攻击及射击命中检测的最大距离（米）；不受游走目标距离限制")]
    public float attackRange = 12f;
    [Tooltip("枪口位置；留空时使用敌人中心上方 0.4 米")]
    public Transform muzzle;
    [Min(0f)] public float attackWindup = 0.4f;
    [Min(1), Tooltip("中间帧之后，Attack 动画连续循环的次数；每个循环射击一次")]
    public int attackLoops = 3;
    [Min(0.01f), Tooltip("本轮开火的总持续时间（秒），不含准备和收枪；Attack Loops 次循环在此时间内完成，期间不会因玩家太近而后退")]
    public float attackDuration = 0.5f;
    [Min(0f)] public float attackRecovery = 0.6f;
    [Tooltip("射击和遮挡检测使用的层，需包含玩家和场景障碍")]
    public LayerMask obstructionLayers = ~0;
    public bool logAttacks = true;
    [SerializeField, HideInInspector] private Animator animator;
    [SerializeField, HideInInspector] private string idleState = "Idle";
    [SerializeField, HideInInspector] private string walkState = "Walk";
    [SerializeField, HideInInspector] private string attackState = "Attack";
    [SerializeField, HideInInspector] private string attackReadyState = "AttackReady";
    [SerializeField, HideInInspector] private string deathState = "Death";
    [SerializeField, HideInInspector] private AnimationClip attackClip;
    [SerializeField, HideInInspector] private AnimationClip deathClip;
    [Tooltip("正面、右前、右、右后、背面、左后、左、左前")]
    [SerializeField, HideInInspector] private string[] directionalWalkStates = { "Walk_Front", "Walk_FrontRight", "Walk_Right", "Walk_BackRight",
        "Walk_Back", "Walk_BackLeft", "Walk_Left", "Walk_FrontLeft" };
    [Header("Death / 死亡")]
    [Min(0f), Tooltip("死亡动画播放期间，敌人在世界 Y 轴下降的距离（米）")]
    public float deathSinkDistance = 0.7f;
    [Header("Runtime / 运行状态")]
    [SerializeField] private EnemyState state;
    [SerializeField] private string navigationStatus;
    [Header("Sprite Billboard")]
    [Tooltip("玩家相机；留空时自动使用带 MainCamera 标签的相机")]
    public Transform targetCamera;
    [Tooltip("需要朝向相机的 Sprite 子物体；留空时自动寻找 SpriteRenderer")]
    public Transform spriteVisual;

    private NavMeshAgent agent;
    private Rigidbody body;
    private Vector3 movementVelocity;
    private Transform attackTarget;
    private Vector3 shotDirection;
    private float phaseEndTime, nextPathTime;
    private Vector3 movementFacing = Vector3.forward, deathStartPosition;
    private float deathStartTime, deathDuration;
    private int currentAnimationHash;
    private int activeAttackLoops, completedAttackLoops;
    private float attackLoopStartTime, attackLoopDuration;
    private float animationSpeedBeforeAttack;
    private bool attackAnimationSpeedOverridden;
    private EnemyDistanceMovement distanceMovement;
    private float canAttackAt;
    private Transform detectedTarget;
    private bool approachingRange;
    private float nextApproachPathTime;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        body.constraints = RigidbodyConstraints.FreezeRotation;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        agent = GetComponent<NavMeshAgent>();
        // NavMesh calculates steering; only Rigidbody moves the enemy.
        agent.updatePosition = false;
        agent.updateRotation = false;
        distanceMovement = GetComponent<EnemyDistanceMovement>();
    }

    private void OnEnable()
    {
        body.isKinematic = true;
        body.useGravity = false;
        movementVelocity = Vector3.zero;
        if (spriteVisual == null)
        {
            SpriteRenderer spriteRenderer = GetComponentInChildren<SpriteRenderer>();
            if (spriteRenderer != null)
                spriteVisual = spriteRenderer.transform;
        }
        FindCamera();
        FindTarget();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (agent == null) agent = GetComponent<NavMeshAgent>();
        agent.updateRotation = false;
        state = EnemyState.Idle;
        nextPathTime = 0f;
        detectedTarget = null;
        approachingRange = false;
        nextApproachPathTime = 0f;
        attackTarget = null;
        currentAnimationHash = 0;
        movementFacing = transform.forward;
        if (distanceMovement == null) distanceMovement = GetComponent<EnemyDistanceMovement>();
        distanceMovement.ResetDestination();
        canAttackAt = Time.time + Mathf.Max(0.1f, distanceMovement.moveBeforeAttackTime);
        PlayAnimation(idleState);
    }

    private void Update()
    {
        if (agent != null && agent.enabled && agent.isOnNavMesh)
            agent.nextPosition = body.position;
        if (state == EnemyState.Dead)
        {
            return;
        }
        if (state == EnemyState.Windup)
        {
            if (Time.time < phaseEndTime) return;
            BeginAttackLoops();
            return;
        }
        if (state == EnemyState.Attacking)
        {
            UpdateAttackLoops();
            return;
        }
        if (state == EnemyState.Recovery)
        {
            if (Time.time < phaseEndTime) return;
            SetState(EnemyState.Idle);
            distanceMovement.ResetDestination();
            canAttackAt = Time.time + Mathf.Max(0.1f, distanceMovement.moveBeforeAttackTime);
        }

        FindTarget();
        if (!EnsureNavigation()) return;
        agent.speed = Mathf.Max(0f, moveSpeed);
        float distance = target != null ? Vector3.Distance(transform.position, target.position) : float.PositiveInfinity;
        if (target == null || (detectedTarget != target && distance > detectionDistance))
        {
            agent.isStopped = true;
            SetState(EnemyState.Idle);
            return;
        }
        // Once detected, keep pursuing even if the player leaves detection range.
        detectedTarget = target;
        float approachMargin = Mathf.Min(0.5f, Mathf.Max(0.01f, attackRange * 0.1f));
        if (distance > attackRange || (approachingRange && distance > Mathf.Max(0.01f, attackRange - approachMargin)))
        {
            SetState(EnemyState.Chasing);
            agent.stoppingDistance = 0.1f;
            agent.isStopped = false;
            if (!approachingRange || Time.time >= nextApproachPathTime)
            {
                nextApproachPathTime = Time.time + 0.25f;
                navigationStatus = agent.SetDestination(target.position)
                    ? "追近玩家至攻击范围" : "无法设置追击路径";
            }
            approachingRange = true;
            return;
        }
        if (approachingRange)
        {
            // Discard the pursuit path before resuming combat repositioning.
            agent.ResetPath();
            distanceMovement.ResetDestination();
            approachingRange = false;
        }
        bool clearShot = target != null && HasLineOfSight(target);
        // Preferred distance controls repositioning, not whether a target is in firing range.
        if (distance <= attackRange && clearShot && Time.time >= canAttackAt)
        {
            agent.isStopped = true;
            agent.ResetPath();
            attackTarget = target;
            // Commit the aim before the windup so movement can dodge the shot.
            shotDirection = (GetAimPoint(attackTarget) - GetShotOrigin()).normalized;
            SetState(EnemyState.Windup);
            phaseEndTime = Time.time + Mathf.Max(0f, attackWindup);
            return;
        }

        SetState(EnemyState.Chasing);
        distanceMovement.MoveAround(target);
        navigationStatus = distanceMovement.MovementStatus;
    }

    private void FixedUpdate()
    {
        if (state == EnemyState.Dead)
        {
            float progress = Mathf.Clamp01((Time.time - deathStartTime) / deathDuration);
            body.MovePosition(deathStartPosition + Vector3.down * (deathSinkDistance * progress));
            return;
        }
        Vector3 horizontal = Vector3.zero;
        if (state == EnemyState.Chasing && agent != null && agent.enabled &&
            agent.isOnNavMesh && !agent.isStopped && agent.hasPath && !agent.pathPending &&
            agent.remainingDistance > agent.stoppingDistance + 0.05f)
        {
            Vector3 desired = Vector3.ClampMagnitude(
                Vector3.ProjectOnPlane(agent.desiredVelocity, Vector3.up), Mathf.Max(0f, moveSpeed));
            horizontal = Vector3.MoveTowards(movementVelocity,
                desired, Mathf.Max(0f, agent.acceleration) * Time.fixedDeltaTime);
            horizontal = Vector3.ClampMagnitude(horizontal,
                Mathf.Max(0f, agent.remainingDistance - agent.stoppingDistance) / Time.fixedDeltaTime);
        }
        movementVelocity = Vector3.zero;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
        // Do not continually project a stationary body back onto slightly different
        // NavMesh points during aiming, recovery, or destination arrival.
        if (horizontal.sqrMagnitude < 0.000001f) return;

        // A kinematic body has no gravity: follow the walkable surface height explicitly.
        Vector3 feet = body.position - Vector3.up * agent.baseOffset;
        Vector3 nextFeet = feet + horizontal * Time.fixedDeltaTime;
        if (!NavMesh.SamplePosition(nextFeet, out NavMeshHit ground, 0.5f, agent.areaMask)) return;
        if (NavMesh.Raycast(feet, ground.position, out NavMeshHit edge, agent.areaMask))
            ground.position = edge.position;
        Vector3 nextPosition = ground.position + Vector3.up * agent.baseOffset;
        movementVelocity = Vector3.ProjectOnPlane((nextPosition - body.position) / Time.fixedDeltaTime, Vector3.up);
        body.MovePosition(nextPosition);
    }

    private void BeginAttackLoops()
    {
        activeAttackLoops = Mathf.Max(1, attackLoops);
        completedAttackLoops = 0;
        attackLoopDuration = Mathf.Max(0.01f, attackDuration) / activeAttackLoops;
        attackLoopStartTime = Time.time;
        if (animator != null)
        {
            animationSpeedBeforeAttack = animator.speed;
            attackAnimationSpeedOverridden = true;
            float clipDuration = attackClip != null ? attackClip.length : 1f / 6f;
            animator.speed = Mathf.Max(0.001f, clipDuration) / attackLoopDuration;
        }
        SetState(EnemyState.Attacking);
        UpdateAttackLoops();
    }

    private void UpdateAttackLoops()
    {
        // Shoot once at each loop's start, including when a low FPS frame crosses a boundary.
        while (completedAttackLoops < activeAttackLoops &&
            Time.time >= attackLoopStartTime + completedAttackLoops * attackLoopDuration)
        {
            FireShot();
            completedAttackLoops++;
        }
        // Wait until the final loop has played in full before leaving Attack.
        if (Time.time < attackLoopStartTime + activeAttackLoops * attackLoopDuration) return;
        SetState(EnemyState.Recovery);
        phaseEndTime = Time.time + Mathf.Max(0f, attackRecovery);
    }

    private bool EnsureNavigation()
    {
        if (agent == null || !agent.enabled) { navigationStatus = "NavMeshAgent 未启用"; return false; }
        if (agent.isOnNavMesh) { navigationStatus = "导航就绪"; return true; }
        if (Time.time < nextPathTime) return false;
        nextPathTime = Time.time + 0.5f;
        Vector3 feet = transform.position - Vector3.up * agent.baseOffset;
        if (NavMesh.SamplePosition(feet, out NavMeshHit hit, 2f, agent.areaMask) &&
            agent.Warp(hit.position + Vector3.up * agent.baseOffset))
        {
            // Initial/recovery placement is the only teleport; regular movement uses physics.
            body.position = agent.nextPosition;
            nextPathTime = 0f;
            return agent.isOnNavMesh;
        }
        navigationStatus = "未找到 NavMesh；请检查 Navigation 上的 Surface";
        return false;
    }

    private bool HasLineOfSight(Transform other)
    {
        Vector3 origin = GetShotOrigin();
        Vector3 difference = GetAimPoint(other) - origin;
        float distance = difference.magnitude;
        if (distance < 0.001f) return true;
        foreach (RaycastHit hit in Physics.RaycastAll(origin, difference / distance, distance,
            obstructionLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.transform == transform || hit.transform.IsChildOf(transform)) continue;
            if (hit.transform == other || hit.transform.IsChildOf(other)) continue;
            return false;
        }
        return true;
    }

    private Vector3 GetShotOrigin()
    {
        return muzzle != null ? muzzle.position : transform.position + Vector3.up * 0.4f;
    }

    private Vector3 GetAimPoint(Transform other)
    {
        CharacterController body = other.GetComponentInChildren<CharacterController>();
        return body != null ? body.bounds.center : other.position + Vector3.up * 0.4f;
    }

    private void FireShot()
    {
        Vector3 origin = GetShotOrigin();
        float nearestDistance = Mathf.Max(0.1f, attackRange);
        Transform firstHit = null;
        Vector3 end = origin + shotDirection * nearestDistance;
        foreach (RaycastHit hit in Physics.RaycastAll(origin, shotDirection, nearestDistance,
            obstructionLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.transform == transform || hit.transform.IsChildOf(transform)) continue;
            if (hit.distance > nearestDistance) continue;
            nearestDistance = hit.distance;
            firstHit = hit.transform;
            end = hit.point;
        }
        bool hitTarget = attackTarget != null && firstHit != null &&
            (firstHit == attackTarget || firstHit.IsChildOf(attackTarget));
        if (hitTarget)
        {
            KatanaParry parry = firstHit.GetComponentInParent<KatanaParry>();
            if (parry != null && parry.isActiveAndEnabled)
            {
                parry.ReceiveShot(this, origin, end);
                if (logAttacks) Debug.Log(name + "：射线命中，进入弹反窗口", this);
                return;
            }
        }
        CombatEffects.Shot(origin, end);
        Debug.DrawLine(origin, end, hitTarget ? Color.red : Color.yellow, 0.2f);
        if (logAttacks)
            Debug.Log(name + (hitTarget ? "：射击命中（仅判定，未扣血）" : "：射击未命中"), this);
    }

    public bool ReceiveReflectedShot(Transform player, Vector3 origin, out Vector3 end)
    {
        end = origin;
        if (state == EnemyState.Dead || !isActiveAndEnabled || player == null) return false;
        Collider targetBody = GetComponentInChildren<Collider>();
        if (targetBody == null || !targetBody.enabled) return false;
        Vector3 difference = targetBody.bounds.center - origin;
        float nearestDistance = difference.magnitude + 0.1f;
        Transform firstHit = null;
        end = targetBody.bounds.center;
        foreach (RaycastHit hit in Physics.RaycastAll(origin, difference.normalized, nearestDistance,
            obstructionLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.transform == player || hit.transform.IsChildOf(player)) continue;
            if (hit.distance > nearestDistance) continue;
            nearestDistance = hit.distance;
            firstHit = hit.transform;
            end = hit.point;
        }
        Debug.DrawLine(origin, end, Color.green, 0.5f);
        EnemyBehav victim = firstHit != null ? firstHit.GetComponentInParent<EnemyBehav>() : null;
        if (victim == null || !victim.isActiveAndEnabled) return false;
        victim.Die();
        return true;
    }

    private void SetState(EnemyState next)
    {
        if (state == next) return;
        if (state == EnemyState.Attacking) RestoreAttackAnimationSpeed();
        state = next;
        if (next == EnemyState.Windup) PlayAnimation(attackReadyState, true);
        else if (next == EnemyState.Attacking) PlayAnimation(attackState, true);
        else if (next == EnemyState.Recovery) PlayAnimation(attackReadyState);
        else if (next == EnemyState.Dead) PlayAnimation(deathState, true);
        else if (next == EnemyState.Chasing) UpdateDirectionalWalk();
        else PlayAnimation(idleState);
    }

    private void PlayAnimation(string animationState, bool restart = false, bool preserveWalkPhase = false)
    {
        if (animator == null || animator.runtimeAnimatorController == null || string.IsNullOrEmpty(animationState)) return;
        int hash = Animator.StringToHash(animationState);
        if (!restart && hash == currentAnimationHash) return;
        if (!animator.HasState(0, hash)) return;
        float phase = preserveWalkPhase ? Mathf.Repeat(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, 1f) : 0f;
        animator.Play(hash, 0, phase);
        currentAnimationHash = hash;
    }

    private void UpdateDirectionalWalk()
    {
        if (state != EnemyState.Chasing || agent == null || !agent.enabled || !agent.isOnNavMesh) return;
        Vector3 velocity = movementVelocity;
        if (velocity.sqrMagnitude < 0.01f) { PlayAnimation(idleState); return; }
        movementFacing = velocity.normalized;
        if (targetCamera == null || directionalWalkStates == null || directionalWalkStates.Length < 8)
        {
            PlayAnimation(walkState);
            return;
        }
        Vector3 toCamera = Vector3.ProjectOnPlane(targetCamera.position - transform.position, Vector3.up);
        if (toCamera.sqrMagnitude < 0.0001f) return;
        Vector3 right = Vector3.Cross(Vector3.up, movementFacing);
        float angle = Mathf.Atan2(Vector3.Dot(toCamera, right), Vector3.Dot(toCamera, movementFacing)) * Mathf.Rad2Deg;
        // Mirror the viewing angle to match the sprite sheet's left/right directions.
        int direction = (8 - Mathf.RoundToInt(angle / 45f)) % 8;
        PlayAnimation(directionalWalkStates[direction], false, true);
    }

    private void OnTriggerEnter(Collider other)
    {
        HandleKatanaContact(other);
    }

    private void OnTriggerStay(Collider other)
    {
        HandleKatanaContact(other);
    }

    private void HandleKatanaContact(Collider other)
    {
        if (!isActiveAndEnabled || state == EnemyState.Dead) return;
        FirstPersonKatana katana = other.GetComponentInParent<FirstPersonKatana>();
        if (katana != null && katana.isActiveAndEnabled) Die();
    }

    [ContextMenu("Play Death (Play Mode)")]
    public void Die()
    {
        if (!Application.isPlaying || state == EnemyState.Dead) return;
        movementVelocity = Vector3.zero;
        if (agent != null)
        {
            if (agent.enabled && agent.isOnNavMesh) { agent.isStopped = true; agent.ResetPath(); }
            agent.enabled = false;
        }
        foreach (Collider body in GetComponentsInChildren<Collider>()) body.enabled = false;
        attackTarget = null;
        deathStartPosition = body.position;
        deathStartTime = Time.time;
        deathDuration = Mathf.Max(0.01f, deathClip != null ? deathClip.length : 0.33333334f);
        SetState(EnemyState.Dead);
    }

    private void FindTarget()
    {
        if (target != null) return;
        FindCamera();
        if (targetCamera == null) return;
        CharacterController player = targetCamera.GetComponentInParent<CharacterController>();
        target = player != null ? player.transform : targetCamera.root;
    }

    private void RestoreAttackAnimationSpeed()
    {
        if (!attackAnimationSpeedOverridden) return;
        if (animator != null) animator.speed = animationSpeedBeforeAttack;
        attackAnimationSpeedOverridden = false;
    }

    private void OnDisable()
    {
        movementVelocity = Vector3.zero;
        RestoreAttackAnimationSpeed();
        if (agent != null && agent.enabled && agent.isOnNavMesh) agent.isStopped = true;
        state = EnemyState.Idle;
        attackTarget = null;
    }

    private void LateUpdate()
    {
        if (targetCamera == null)
            FindCamera();
        if (targetCamera == null || spriteVisual == null)
            return;

        // Rotate only the visual around world Y; keep the enemy upright.
        // Sprites face local -Z, so local +Z points away from the camera.
        Vector3 forward = spriteVisual.position - targetCamera.position;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.0001f)
            spriteVisual.rotation = Quaternion.LookRotation(forward, Vector3.up);
        UpdateDirectionalWalk();
    }

    private void FindCamera()
    {
        if (targetCamera == null && Camera.main != null)
            targetCamera = Camera.main.transform;
    }
}
