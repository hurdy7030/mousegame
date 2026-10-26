using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
public sealed class EnemyDistanceMovement : MonoBehaviour
{
    [Min(0.5f), Tooltip("希望与玩家保持的水平距离（米）；实际游走距离会限制在射程内，并预留距离容差")]
    public float preferredDistance = 4f;

    [Min(0.1f), Tooltip("允许距离在目标值上下浮动的范围（米）")]
    public float distanceTolerance = 0.75f;

    [Min(0.1f), Tooltip("保持距离时，重新选择游走位置的间隔（秒）")]
    public float repositionInterval = 1.2f;

    [Range(10f, 120f), Tooltip("围绕玩家换位时的角度")]
    public float orbitAngle = 45f;

    [Min(0.1f), Tooltip("每轮射击后至少先游走多久，再允许下一轮射击（秒）")]
    public float moveBeforeAttackTime = 1f;

    public string MovementStatus { get; private set; }

    private NavMeshAgent agent;
    private EnemyBehav enemy;
    private NavMeshPath candidatePath;
    private NavMeshPath bestPath;
    private float nextDestinationTime;
    private Vector3 previousTargetPosition;
    private float orbitSide;

    private float GetPreferredRadius(Transform target)
    {
        if (enemy == null) enemy = GetComponent<EnemyBehav>();
        float radius = Mathf.Max(0.5f, preferredDistance);
        if (enemy == null || target == null) return radius;
        // Attack range is 3D, while the orbit radius is horizontal.
        float height = transform.position.y - target.position.y;
        float horizontalRange = Mathf.Sqrt(Mathf.Max(0f, enemy.attackRange * enemy.attackRange - height * height));
        float margin = Mathf.Min(0.5f, Mathf.Max(0.01f, enemy.attackRange * 0.1f));
        return Mathf.Min(radius, Mathf.Max(0.1f, horizontalRange - Mathf.Max(0.1f, distanceTolerance) - margin));
    }

    public bool IsAtPreferredDistance(Transform target)
    {
        if (target == null)
            return false;

        float distance = Vector3.ProjectOnPlane(
            transform.position - target.position,
            Vector3.up
        ).magnitude;

        return Mathf.Abs(distance - GetPreferredRadius(target))
            <= Mathf.Max(0.1f, distanceTolerance);
    }

    public void ResetDestination()
    {
        nextDestinationTime = float.NegativeInfinity;
        orbitSide = Random.value < 0.5f ? -1f : 1f;
    }

    public bool MoveAround(Transform target)
    {
        if (agent == null)
            agent = GetComponent<NavMeshAgent>();

        if (target == null)
        {
            MovementStatus = "没有玩家目标";
            return false;
        }

        if (!agent.enabled || !agent.isOnNavMesh)
        {
            MovementStatus = "NavMeshAgent 未启用或不在 NavMesh 上";
            return false;
        }

        // 必须在运行时创建，不能在字段初始化时创建。
        if (candidatePath == null)
            candidatePath = new NavMeshPath();

        if (bestPath == null)
            bestPath = new NavMeshPath();

        Vector3 away = Vector3.ProjectOnPlane(
            transform.position - target.position,
            Vector3.up
        );

        float distance = away.magnitude;
        away = distance > 0.001f
            ? away / distance
            : Vector3.forward;

        float radius = GetPreferredRadius(target);
        float tolerance = Mathf.Max(0.1f, distanceTolerance);

        bool correctingDistance =
            Mathf.Abs(distance - radius) > tolerance;

        bool targetMoved = Vector3.ProjectOnPlane(
            target.position - previousTargetPosition,
            Vector3.up
        ).sqrMagnitude > 0.5625f;

        if (!targetMoved && agent.hasPath &&
            (agent.pathPending || (agent.pathStatus == NavMeshPathStatus.PathComplete &&
            agent.remainingDistance > agent.stoppingDistance + 0.05f)))
        {
            agent.isStopped = false;
            return true;
        }

        if (!targetMoved && Time.time < nextDestinationTime)
        {
            agent.isStopped = true;
            MovementStatus = "等待下一次换位";
            return true;
        }

        nextDestinationTime = Time.time +
            (correctingDistance
                ? 0.25f
                : Mathf.Max(0.1f, repositionInterval));

        previousTargetPosition = target.position;
        agent.stoppingDistance = 0.15f;

        float bestScore = float.PositiveInfinity;
        Vector3 bestPoint = transform.position;
        bool found = false;
        if (orbitSide == 0f) orbitSide = Random.value < 0.5f ? -1f : 1f;

        for (int i = 0; i < 16; i++)
        {
            // 距离不合适时优先靠近或后退，否则优先侧向换位。
            float angle = i == 0
                ? (correctingDistance ? 0f : orbitSide * orbitAngle)
                : Random.Range(-180f, 180f);

            Vector3 radial =
                Quaternion.AngleAxis(angle, Vector3.up) * away;

            Vector3 candidate = target.position + radial * radius;

            if (!NavMesh.SamplePosition(
                candidate,
                out NavMeshHit hit,
                2f,
                agent.areaMask))
            {
                continue;
            }

            float travel = Vector3.ProjectOnPlane(
                hit.position - transform.position,
                Vector3.up
            ).magnitude;

            if (travel < 0.4f)
                continue;

            if (!agent.CalculatePath(hit.position, candidatePath) ||
                candidatePath.status != NavMeshPathStatus.PathComplete)
            {
                continue;
            }

            float resultingDistance = Vector3.ProjectOnPlane(
                hit.position - target.position,
                Vector3.up
            ).magnitude;

            // 太近时，候选位置必须能拉开距离。
            if (distance < radius - tolerance &&
                resultingDistance <= distance + 0.1f)
            {
                continue;
            }

            // 太远时，候选位置必须能接近玩家。
            if (distance > radius + tolerance &&
                resultingDistance >= distance - 0.1f)
            {
                continue;
            }

            if (resultingDistance <
                Mathf.Max(0.5f, radius - tolerance * 2f))
            {
                continue;
            }

            float score =
                Mathf.Abs(resultingDistance - radius) * 5f +
                travel * 0.05f;

            if (i == 0)
                score -= 1f;

            if (score >= bestScore)
                continue;

            bestScore = score;
            bestPoint = hit.position;
            found = true;
        }

        if (!found)
        {
            agent.isStopped = true;
            MovementStatus = "没有可到达的换位点";
            return false;
        }

        if (!agent.CalculatePath(bestPoint, bestPath) ||
            bestPath.status != NavMeshPathStatus.PathComplete)
        {
            agent.isStopped = true;
            MovementStatus = "换位路径已失效";
            return false;
        }

        bool accepted = agent.SetPath(bestPath);

        if (!accepted)
        {
            agent.isStopped = true;
            MovementStatus = "NavMeshAgent 未接受换位路径";
            return false;
        }

        agent.isStopped = false;

        MovementStatus = distance < radius - tolerance
            ? "后退保持距离"
            : distance > radius + tolerance
                ? "靠近玩家"
                : "围绕玩家游走";

        return true;
    }
}
