using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

// Read the final weapon pose after FirstPersonKatana and BladeTrail.
[DefaultExecutionOrder(200)]
[DisallowMultipleComponent]
public sealed class KatanaParry : MonoBehaviour
{
    public FirstPersonKatana katana;
    [Header("Parry windows / 弹反时间窗口（秒）")]
    [Min(0.01f), Tooltip("射线命中玩家后，允许挥刀弹开的总时间；超时才结算命中")]
    public float parryWindow = 0.3f;
    [Min(0f), Tooltip("精准反弹窗口的时长；实际范围会限制在大窗口内")]
    public float perfectWindow = 0.08f;
    [Min(0f), Tooltip("小窗口从射线命中后第几秒开始；0 表示立即开始")]
    public float perfectWindowOffset = 0.1f;

    [Header("Swing recognition / 挥刀识别")]
    [Min(0.01f), Tooltip("刀身相对相机的最低运动速度（米/秒）；走动、转动视角不单独算挥刀")]
    public float minimumSwingSpeed = 0.5f;
    [Min(0.001f), Tooltip("一次有效挥刀至少移动的距离（米）")]
    public float minimumSwingDistance = 0.04f;
    [Min(0.01f), Tooltip("停刀多久后，下次运动算一次新挥刀（秒）")]
    public float swingResetDelay = 0.08f;
    [Range(45f, 180f), Tooltip("相对本次挥刀方向反转超过该角度，算新的一次挥刀；持续乱甩也会计数")]
    public float reversalAngle = 110f;

    [Header("Anti-spam / 防乱挥（只限制精准反弹）")]
    [Min(0.01f), Tooltip("统计最近多少秒内的挥刀次数")]
    public float spamHistoryWindow = 0.6f;
    [Min(1), Tooltip("统计窗口内允许的挥刀次数；超过后只能普通弹开")]
    public int maxSwingsInWindow = 3;
    [Min(0f), Tooltip("触发乱挥后，禁用精准反弹多久；继续乱挥会延长（秒）")]
    public float spamPenaltyDuration = 0.5f;

    [Header("Feedback / 反馈")]
    public bool showParryHUD = true;
    public bool logResults = true;
    public UnityEvent onHit = new UnityEvent();
    public UnityEvent onDeflect = new UnityEvent();
    public UnityEvent onPerfectParry = new UnityEvent();
    [Header("Runtime / 运行状态")]
    [SerializeField] private int pendingShotCount, recentSwingCount;
    [SerializeField] private int hitCount, deflectCount, perfectParryCount;
    [SerializeField] private bool spamming;
    [SerializeField] private string lastResult;

    private sealed class IncomingShot
    {
        public EnemyBehav shooter;
        public Vector3 origin, impact;
        public float openedAt;
        public ParryTiming timing;
    }

    private readonly List<IncomingShot> shots = new List<IncomingShot>();
    private readonly ParrySpamTracker spam = new ParrySpamTracker();
    private Vector3 previousRoot, previousTip, strokeDirection;
    private float lastMotionTime, strokeDistance, resultUntil;
    private bool poseInitialized, strokeTriggered;

    private void OnEnable()
    {
        if (katana == null) katana = GetComponentInChildren<FirstPersonKatana>();
        ResetInput();
        spam.Clear();
    }

    // Each actual hitscan hit gets its own window; misses never enter this queue.
    public void ReceiveShot(EnemyBehav shooter, Vector3 origin, Vector3 impact)
    {
        if (!isActiveAndEnabled) return;
        float duration = Mathf.Max(0.01f, parryWindow);
        shots.Add(new IncomingShot
        {
            shooter = shooter, origin = origin, impact = impact, openedAt = Time.time,
            timing = new ParryTiming(duration, perfectWindowOffset, perfectWindow)
        });
        pendingShotCount = shots.Count;
    }

    private void LateUpdate()
    {
        if (Time.deltaTime <= 0f) return;
        float now = Time.time;
        spam.Prune(now, spamHistoryWindow);
        if (Application.isFocused) ReadSwing(now);
        else ResetInput();

        // Evaluate input before expiration, so input on the deadline is accepted.
        for (int i = shots.Count - 1; i >= 0; i--)
        {
            IncomingShot shot = shots[i];
            if (now - shot.openedAt <= shot.timing.duration) continue;
            shots.RemoveAt(i);
            hitCount++;
            CombatEffects.Shot(shot.origin, shot.impact);
            SetResult("HIT (damage not implemented)");
            Debug.DrawLine(shot.origin, shot.impact, Color.red, 0.3f);
            onHit.Invoke();
        }
        pendingShotCount = shots.Count;
        recentSwingCount = spam.RecentSwingCount;
        spamming = spam.IsSpamming(now);
    }

    private void ReadSwing(float now)
    {
        if (katana == null || !katana.isActiveAndEnabled) { ResetInput(); return; }
        Transform space = katana.transform.parent;
        Vector3 root = katana.transform.TransformPoint(katana.localGripPoint);
        Vector3 tip = katana.transform.TransformPoint(katana.localTipPoint);
        if (space != null) { root = space.InverseTransformPoint(root); tip = space.InverseTransformPoint(tip); }
        if (!poseInitialized)
        {
            previousRoot = root; previousTip = tip; poseInitialized = true;
            return;
        }
        Vector3 rootDelta = root - previousRoot, tipDelta = tip - previousTip;
        previousRoot = root; previousTip = tip;
        Vector3 motion = tipDelta.sqrMagnitude >= rootDelta.sqrMagnitude ? tipDelta : rootDelta;
        float distance = motion.magnitude;
        if (distance / Time.deltaTime < Mathf.Max(0.01f, minimumSwingSpeed)) return;

        Vector3 direction = motion / distance;
        bool reversed = strokeDirection.sqrMagnitude > 0f && Vector3.Angle(strokeDirection, direction) >= reversalAngle;
        if (now - lastMotionTime > Mathf.Max(swingResetDelay, Time.deltaTime * 1.5f) || reversed)
        {
            strokeDistance = 0f;
            strokeTriggered = false;
            strokeDirection = direction;
        }
        if (strokeDirection.sqrMagnitude == 0f) strokeDirection = direction;
        lastMotionTime = now;
        strokeDistance += distance;
        if (strokeTriggered || strokeDistance < Mathf.Max(0.001f, minimumSwingDistance)) return;
        strokeTriggered = true;
        spam.RecordSwing(now, spamHistoryWindow, maxSwingsInWindow, spamPenaltyDuration);
        TryParry(now);
    }

    private void TryParry(float now)
    {
        // One deliberate swing handles one shot, including during enemy bursts.
        for (int i = 0; i < shots.Count; i++)
        {
            IncomingShot shot = shots[i];
            ShotParryResult result = shot.timing.Evaluate(now - shot.openedAt, spam.IsSpamming(now));
            if (result == ShotParryResult.None) continue;
            shots.RemoveAt(i);
            Vector3 contact = GetBladeContact(shot.impact);
            // Only reveal the incoming beam once its final endpoint is known.
            CombatEffects.Shot(shot.origin, contact);
            Vector3 incoming = (shot.impact - shot.origin).normalized;
            if (result == ShotParryResult.Reflected)
            {
                perfectParryCount++;
                Vector3 returnEnd = contact;
                bool hitEnemy = shot.shooter != null && shot.shooter.ReceiveReflectedShot(transform, contact, out returnEnd);
                CombatEffects.Perfect(contact, returnEnd, incoming);
                SetResult(hitEnemy ? "PERFECT - RETURN HIT" : "PERFECT - RETURN BLOCKED");
                onPerfectParry.Invoke();
            }
            else
            {
                deflectCount++;
                CombatEffects.Deflect(contact, incoming, katana != null ? katana.transform.parent : transform);
                SetResult(spam.IsSpamming(now) ? "DEFLECT (SPAM: PERFECT DISABLED)" : "DEFLECT");
                Debug.DrawLine(contact, contact + Vector3.up * 2f, Color.cyan, 0.3f);
                onDeflect.Invoke();
            }
            return;
        }
    }

    private Vector3 GetBladeContact(Vector3 fallback)
    {
        return katana != null
            ? katana.transform.TransformPoint(Vector3.Lerp(katana.localGripPoint, katana.localTipPoint, 0.65f))
            : fallback;
    }

    private void SetResult(string result)
    {
        lastResult = result;
        resultUntil = Time.time + 1f;
        if (logResults) Debug.Log(result, this);
    }

    private void ResetInput()
    {
        poseInitialized = false; strokeTriggered = false;
        strokeDistance = 0f; strokeDirection = Vector3.zero;
        lastMotionTime = float.NegativeInfinity;
    }

    private void OnApplicationFocus(bool focused) { ResetInput(); }

    private void OnDisable()
    {
        shots.Clear(); spam.Clear(); ResetInput();
        pendingShotCount = recentSwingCount = 0; spamming = false;
    }

    private void OnGUI()
    {
        if (!showParryHUD) return;
        const float width = 340f;
        float x = (Screen.width - width) * 0.5f, y = Screen.height * 0.7f;
        Color savedColor = GUI.color;
        if (shots.Count > 0)
        {
            IncomingShot shot = shots[0];
            float duration = shot.timing.duration;
            float elapsed = Time.time - shot.openedAt;
            GUI.Box(new Rect(x, y, width, 24f), GUIContent.none);
            GUI.color = spam.IsSpamming(Time.time) ? Color.gray : Color.green;
            GUI.DrawTexture(new Rect(x + width * shot.timing.perfectStart / duration, y,
                width * (shot.timing.perfectEnd - shot.timing.perfectStart) / duration, 24f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(x + width * Mathf.Clamp01(elapsed / duration), y, 2f, 24f), Texture2D.whiteTexture);
            GUI.Label(new Rect(x, y - 24f, width, 24f), "SWING TO PARRY | GREEN = PERFECT | SHOTS: " + shots.Count);
        }
        GUI.color = Color.white;
        if (Time.time < resultUntil) GUI.Label(new Rect(x, y + 28f, width + 120f, 24f), lastResult);
        if (spam.IsSpamming(Time.time))
            GUI.Label(new Rect(x, y + 52f, width, 24f), "SPAM - PERFECT LOCKED: " + spam.CooldownRemaining(Time.time).ToString("0.0") + "s");
        GUI.color = savedColor;
    }
}
