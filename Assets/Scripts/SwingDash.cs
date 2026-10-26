using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[RequireComponent(typeof(CharacterController))]
public sealed class SwingDash : MonoBehaviour
{
    public Camera viewCamera;
    [Header("Mouse / 鼠标")]
    [Min(0f), Tooltip("当前帧鼠标移动量达到该值时尝试 Dash（像素/帧）；仍受 CD 限制")]
    public float minimumFrameDistance = 8f;
    [Header("Dash / 冲刺")]
    [Min(0f), Tooltip("一次挥刀向前冲刺的距离（米）；碰撞会阻挡冲刺")]
    public float dashDistance = 1.5f;
    [Min(0.01f), Tooltip("冲刺持续时间（秒）")]
    public float dashDuration = 0.25f;
    [Tooltip("X 是冲刺进度 0～1，Y 是相对速度；默认先快后慢。曲线面积会归一化以保持指定距离")]
    public AnimationCurve speedCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
    [Header("Cooldown / 冷却")]
    [Min(0f), Tooltip("从成功触发 Dash 时开始计算的冷却时间（秒）；0 表示无冷却")]
    public float dashCooldown = 1f;
    [Tooltip("Canvas 中用于显示 Dash 冷却的文本组件")]
    public Text cooldownText;

    [Header("Runtime Debug / 运行时观察")]
    [SerializeField] private float currentFrameDistance;
    [SerializeField] private string dashStatus = "等待挥动";
    [SerializeField] private int dashTriggerCount;
    [SerializeField] private float lastDashTravelled;
    [SerializeField] private string lastDashResult = "尚未触发";

    private const int CurveSteps = 128;
    private readonly float[] cumulative = new float[CurveSteps + 1];
    private CharacterController controller;
    private PlayerView playerView;
    private Vector3 direction;
    private float elapsed, activeDuration, activeDistance;
    private bool dashing;
    private float lastDashTime = float.NegativeInfinity;

    public float CooldownRemaining => Mathf.Max(0f, lastDashTime + Mathf.Max(0f, dashCooldown) - Time.time);

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        playerView = GetComponent<PlayerView>();
        if (cooldownText != null && cooldownText.font == null)
            cooldownText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    private void OnEnable()
    {
        if (viewCamera == null) viewCamera = GetComponentInChildren<Camera>();
        // A weapon collider is part of the player, not an obstacle to its own dash.
        FirstPersonKatana weapon = GetComponentInChildren<FirstPersonKatana>();
        if (controller != null && weapon != null)
            foreach (Collider weaponCollider in weapon.GetComponentsInChildren<Collider>(true))
                if (weaponCollider != controller) Physics.IgnoreCollision(controller, weaponCollider);
    }

    private bool StartDash()
    {
        if (CooldownRemaining > 0f) { dashStatus = "Dash 冷却中"; return false; }
        if (dashing) { dashStatus = "冲刺尚未结束"; return false; }
        if (viewCamera == null) { dashStatus = "无法冲刺：未指定相机"; return false; }
        if (controller == null || !controller.enabled) { dashStatus = "无法冲刺：CharacterController 未启用"; return false; }
        if (dashDistance <= 0f) { dashStatus = "无法冲刺：Dash Distance 为 0"; return false; }
        Vector3 forward = Vector3.ProjectOnPlane(viewCamera.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.000001f) { dashStatus = "无法冲刺：相机朝向垂直"; return false; }
        if (!BakeCurve()) { dashStatus = "无法冲刺：速度曲线面积为 0"; return false; }
        direction = forward.normalized;
        activeDuration = Mathf.Max(0.01f, dashDuration);
        activeDistance = Mathf.Max(0f, dashDistance);
        elapsed = 0f;
        dashing = true;
        lastDashTime = Time.time;
        lastDashTravelled = 0f;
        dashTriggerCount++;
        dashStatus = "冲刺中";
        lastDashResult = "冲刺中";
        return true;
    }

    private bool BakeCurve()
    {
        cumulative[0] = 0f;
        float previousSpeed = EvaluateSpeed(0f);
        for (int i = 1; i <= CurveSteps; i++)
        {
            float speed = EvaluateSpeed(i / (float)CurveSteps);
            cumulative[i] = cumulative[i - 1] + (previousSpeed + speed) * (0.5f / CurveSteps);
            previousSpeed = speed;
        }
        float area = cumulative[CurveSteps];
        if (area <= 0.000001f) return false;
        for (int i = 1; i <= CurveSteps; i++) cumulative[i] /= area;
        return true;
    }

    private float EvaluateSpeed(float progress)
    {
        float speed = speedCurve == null ? 1f - progress : speedCurve.Evaluate(progress);
        return float.IsNaN(speed) || float.IsInfinity(speed) ? 0f : Mathf.Max(0f, speed);
    }

    private float DistanceFraction(float progress)
    {
        float index = Mathf.Clamp01(progress) * CurveSteps;
        int lower = Mathf.Min(Mathf.FloorToInt(index), CurveSteps - 1);
        return Mathf.Lerp(cumulative[lower], cumulative[lower + 1], index - lower);
    }

    private void Update()
    {
        currentFrameDistance = Mouse.current != null && Application.isFocused && Time.deltaTime > 0f
            ? (playerView != null && playerView.isActiveAndEnabled ? playerView.MouseDelta.magnitude :
                Mouse.current.delta.ReadValue().magnitude) : 0f;
        if (currentFrameDistance > 0f && currentFrameDistance >= Mathf.Max(0f, minimumFrameDistance))
            StartDash();
        if (!dashing) return;
        if (controller == null || !controller.enabled) { dashing = false; dashStatus = "CharacterController 未启用"; return; }
        float before = DistanceFraction(elapsed / activeDuration);
        elapsed = Mathf.Min(elapsed + Time.deltaTime, activeDuration);
        float after = DistanceFraction(elapsed / activeDuration);
        // Integrate the speed curve over this frame, so distance is independent of FPS.
        Vector3 beforePosition = transform.position;
        CollisionFlags flags = controller.Move(direction * ((after - before) * activeDistance));
        lastDashTravelled += Vector3.Distance(beforePosition, transform.position);
        if ((flags & CollisionFlags.Sides) != 0)
        {
            dashing = false;
            dashStatus = "已触发，但被碰撞阻挡";
            lastDashResult = dashStatus;
        }
        else if (elapsed >= activeDuration)
        {
            dashing = false;
            dashStatus = "冲刺完成";
            lastDashResult = dashStatus;
        }
    }

    private void OnApplicationFocus(bool focused)
    {
        if (!focused) dashing = false;
    }

    private void LateUpdate()
    {
        if (cooldownText == null) return;
        float remaining = CooldownRemaining;
        string text = remaining > 0f ? "DASH  " + (Mathf.Ceil(remaining * 10f) / 10f).ToString("0.0") + "s" :
            dashing ? "DASHING" : "DASH READY";
        if (cooldownText.text != text) cooldownText.text = text;
    }

    private void OnDisable()
    {
        dashing = false;
    }
}
