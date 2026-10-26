using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DefaultExecutionOrder(100)]
public sealed class BladeTrail : MonoBehaviour
{
    public event System.Action SwingStarted;
    public FirstPersonKatana katana;
    public Material material;
    [Min(0.01f)] public float lifetime = 0.12f;
    [Min(0f), Tooltip("产生新刀光的最低速度（米/秒），取刀光两端中较快的一端；0 表示移动即产生")]
    public float minimumSpeed = 0.5f;
    [Min(0f), Tooltip("达到最大刀光强度的速度（米/秒），应大于 Minimum Speed")]
    public float fullIntensitySpeed = 3f;
    [Range(0f, 8f), Tooltip("指数曲线陡峭程度；0 为线性，越大越偏向快速挥刀时增强")]
    public float exponential = 3f;
    [Range(0f, 1f), Tooltip("刀光起点在刀根到刀尖之间的位置")]
    public float bladeStart = 0f;
    [Range(0f, 1f)] public float bladeEnd = 1f;
    [Range(0f, 1f)] public float opacity = 1f;
    [Tooltip("横向覆盖刀身的比例，1 为完整刀身长度")]
    [Min(0f)] public float widthMultiplier = 1f;
    [Tooltip("沿轨迹年龄收尖：左边是新轨迹，右边是即将消失的轨迹")]
    public AnimationCurve widthOverAge = AnimationCurve.Linear(0f, 1f, 1f, 0f);
    public Gradient colorOverAge = new Gradient
    {
        colorKeys = new[] { new GradientColorKey(Color.white, 0f),
            new GradientColorKey(new Color(0.4f, 0.8f, 1f), 1f) },
        alphaKeys = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) }
    };

    private struct Pose
    {
        public Vector3 root, tip;
        public float time, intensity;
        public bool connect;
    }
    private readonly List<Pose> history = new List<Pose>(128);
    private readonly List<Vector3> vertices = new List<Vector3>(256);
    private readonly List<Color> colors = new List<Color>(256);
    private readonly List<Vector2> uv = new List<Vector2>(256);
    private readonly List<int> triangles = new List<int>(762);
    private GameObject output;
    private Mesh mesh;
    private MeshRenderer meshRenderer;
    private Pose previous;
    private Vector3 previousMotionRoot, previousMotionTip;
    private bool initialized, wasMoving;
    private float lastEmissionTime;
    // Mouse reports can arrive less often than rendered frames. Do not split a stroke
    // just because one intervening frame has no new movement or falls below threshold.
    private const float StrokeGapTolerance = 0.05f;

    private void OnEnable()
    {
        if (katana == null) katana = GetComponentInParent<FirstPersonKatana>();
        // Keep historical geometry in world space, independent of the scaled Cube.
        output = new GameObject("Katana Blade Trail Mesh");
        output.hideFlags = HideFlags.DontSave;
        output.layer = gameObject.layer;
        mesh = new Mesh { name = "Blade Trail" };
        mesh.MarkDynamic();
        output.AddComponent<MeshFilter>().sharedMesh = mesh;
        meshRenderer = output.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        Clear();
    }

    private void LateUpdate()
    {
        if (katana == null || mesh == null) return;
        if (!Application.isFocused || Time.deltaTime <= 0f) { Clear(); return; }
        float now = Time.time;
        float duration = Mathf.Max(0.01f, lifetime);
        while (history.Count > 0 && now - history[0].time >= duration) history.RemoveAt(0);
        Pose current = new Pose
        {
            root = katana.transform.TransformPoint(Vector3.Lerp(katana.localGripPoint, katana.localTipPoint, bladeStart)),
            tip = katana.transform.TransformPoint(Vector3.Lerp(katana.localGripPoint, katana.localTipPoint, Mathf.Max(bladeStart, bladeEnd))),
            time = now, connect = true
        };
        float elapsed = now - previous.time;
        // Measure blade motion relative to the camera, excluding player/dash movement.
        Transform motionSpace = katana.transform.parent;
        Vector3 motionRoot = motionSpace != null ? motionSpace.InverseTransformPoint(current.root) : current.root;
        Vector3 motionTip = motionSpace != null ? motionSpace.InverseTransformPoint(current.tip) : current.tip;
        float distanceSquared = Mathf.Max(Vector3.SqrMagnitude(motionRoot - previousMotionRoot),
            Vector3.SqrMagnitude(motionTip - previousMotionTip));
        float speed = initialized && elapsed > 0f ? Mathf.Sqrt(distanceSquared) / elapsed : 0f;
        current.intensity = EvaluateIntensity(speed);
        bool moving = initialized && elapsed > 0f && distanceSquared > 0f &&
            speed >= Mathf.Max(0f, minimumSpeed);
        if (moving)
        {
            if (!wasMoving)
            {
                Pose first = previous;
                first.connect = false;
                first.intensity = current.intensity;
                Add(first);
                SwingStarted?.Invoke();
            }
            Add(current);
            lastEmissionTime = now;
            wasMoving = true;
        }
        else if (now - lastEmissionTime >= StrokeGapTolerance)
            wasMoving = false;
        previous = current;
        previousMotionRoot = motionRoot;
        previousMotionTip = motionTip;
        initialized = true;
        Rebuild(now, duration);
    }

    private float EvaluateIntensity(float speed)
    {
        float start = Mathf.Max(0f, minimumSpeed);
        float end = Mathf.Max(start + 0.001f, fullIntensitySpeed);
        float t = Mathf.Clamp01((speed - start) / (end - start));
        float k = Mathf.Clamp(exponential, 0f, 8f);
        return k < 0.001f ? t : (Mathf.Exp(k * t) - 1f) / (Mathf.Exp(k) - 1f);
    }

    private void Add(Pose pose)
    {
        if (history.Count >= 128) history.RemoveAt(0);
        history.Add(pose);
    }

    private void Rebuild(float now, float duration)
    {
        vertices.Clear(); colors.Clear(); uv.Clear(); triangles.Clear();
        for (int i = 0; i < history.Count; i++)
        {
            Pose pose = history[i];
            float age = Mathf.Clamp01((now - pose.time) / duration);
            // The head spans the blade; older sections narrow just like the original trail.
            Vector3 center = (pose.root + pose.tip) * 0.5f;
            Vector3 halfWidth = (pose.tip - pose.root) * (0.5f * widthMultiplier * Mathf.Max(0f, widthOverAge.Evaluate(age)));
            vertices.Add(center - halfWidth); vertices.Add(center + halfWidth);
            Color color = colorOverAge.Evaluate(age);
            // Preserve the speed strength of each segment while its age fade continues.
            color.a *= opacity * pose.intensity;
            colors.Add(color); colors.Add(color);
            // U: newest (left) to oldest (right). V: blade root (bottom) to tip (top).
            uv.Add(new Vector2(age, 0f)); uv.Add(new Vector2(age, 1f));
            if (i == 0 || !pose.connect) continue;
            int a = (i - 1) * 2, b = i * 2;
            triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
            triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
        }
        mesh.Clear();
        mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uv);
        mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
        meshRenderer.sharedMaterial = material;
    }

    private void Clear()
    {
        history.Clear(); initialized = false; wasMoving = false;
        lastEmissionTime = float.NegativeInfinity;
        if (mesh != null) mesh.Clear();
    }
    private void OnApplicationFocus(bool focused) { Clear(); }
    private void OnDisable()
    {
        Clear();
        if (output != null) Destroy(output);
        if (mesh != null) Destroy(mesh);
    }
}
