using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Shared visual-only pool. All damage and parry decisions remain hitscan based.
public sealed class CombatEffects : MonoBehaviour
{
    public Material effectMaterial;
    [Header("Tracer / 曳光")]
    public Color shotColor = new Color(1f, 0.72f, 0.18f, 1f);
    public Color deflectColor = new Color(1f, 0.48f, 0.12f, 1f);
    public Color perfectColor = new Color(0.2f, 0.9f, 1f, 1f);
    [Min(0.001f)] public float tracerWidth = 0.035f;
    [Min(0.01f)] public float tracerLifetime = 0.12f;
    [Min(0.01f)] public float perfectTracerLifetime = 0.2f;
    [Min(0.1f)] public float deflectDistance = 5f;
    [Header("Sparks / 火花")]
    [Min(1)] public int deflectSparkCount = 14;
    [Min(1)] public int perfectSparkCount = 28;
    [Min(0.01f)] public float sparkLifetime = 0.28f;
    [Min(0.1f)] public float sparkSpeed = 5f;
    [Min(0.001f)] public float sparkSize = 0.035f;

    private sealed class Tracer
    {
        internal LineRenderer line;
        internal Color color;
        internal float started, duration;
    }

    private static CombatEffects instance;
    private readonly List<Tracer> tracers = new List<Tracer>();
    private readonly List<ParticleSystem> bursts = new List<ParticleSystem>();
    private const int MaxTracers = 96, MaxBursts = 32;

    private static CombatEffects Instance
    {
        get
        {
            if (instance != null) return instance;
            GameObject prefab = Resources.Load<GameObject>("CombatEffects");
            if (prefab != null) instance = Instantiate(prefab).GetComponent<CombatEffects>();
            return instance;
        }
    }

    private void Awake()
    {
        instance = this;
    }

    public static void Shot(Vector3 start, Vector3 end)
    {
        CombatEffects fx = Instance;
        if (fx != null) fx.DrawTracer(start, end, fx.shotColor, fx.tracerLifetime);
    }

    public static void Perfect(Vector3 position, Vector3 returnEnd, Vector3 incoming)
    {
        CombatEffects fx = Instance;
        if (fx == null) return;
        fx.EmitSparks(position, -incoming, fx.perfectColor, fx.perfectSparkCount);
        fx.DrawTracer(position, returnEnd, fx.perfectColor, fx.perfectTracerLifetime, 1.5f);
    }

    public static void Deflect(Vector3 position, Vector3 incoming, Transform view)
    {
        CombatEffects fx = Instance;
        if (fx == null) return;
        Vector3 right = view != null ? view.right : Vector3.right;
        Vector3 direction = (right * (Random.value < 0.5f ? -1f : 1f) + Vector3.up * 0.7f - incoming * 0.3f).normalized;
        Vector3 end = position + direction * fx.deflectDistance;
        if (Physics.Raycast(position, direction, out RaycastHit hit, fx.deflectDistance,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) end = hit.point;
        fx.EmitSparks(position, direction, fx.deflectColor, fx.deflectSparkCount);
        fx.DrawTracer(position, end, fx.deflectColor, fx.tracerLifetime);
    }

    private Tracer DrawTracer(Vector3 start, Vector3 end, Color color, float duration, float widthScale = 1f)
    {
        if (effectMaterial == null || (end - start).sqrMagnitude < 0.000001f) return null;
        Tracer slot = tracers.Find(t => !t.line.enabled);
        if (slot == null && tracers.Count < MaxTracers)
        {
            var output = new GameObject("Bullet Tracer");
            output.transform.SetParent(transform, false);
            var line = output.AddComponent<LineRenderer>();
            line.sharedMaterial = effectMaterial;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.numCapVertices = 3;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            slot = new Tracer { line = line };
            tracers.Add(slot);
        }
        if (slot == null)
        {
            foreach (Tracer candidate in tracers)
                if (slot == null || candidate.started < slot.started) slot = candidate;
            if (slot == null) return null;
        }
        slot.started = Time.time;
        slot.duration = Mathf.Max(0.01f, duration);
        slot.color = color;
        slot.line.enabled = true;
        slot.line.startWidth = Mathf.Max(0.001f, tracerWidth) * widthScale;
        slot.line.endWidth = slot.line.startWidth * 0.35f;
        slot.line.startColor = slot.line.endColor = color;
        slot.line.SetPosition(0, start);
        slot.line.SetPosition(1, end);
        return slot;
    }

    private void EmitSparks(Vector3 position, Vector3 direction, Color color, int count)
    {
        if (effectMaterial == null) return;
        ParticleSystem ps = bursts.Find(p => !p.IsAlive(true));
        if (ps == null && bursts.Count < MaxBursts)
        {
            var output = new GameObject("Parry Sparks");
            output.transform.SetParent(transform, false);
            ps = output.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = Mathf.Max(0.01f, sparkLifetime);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 128;
            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 65f;
            shape.radius = 0.025f;
            var fade = ps.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            fade.color = gradient;
            var renderer = output.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = effectMaterial;
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 2f;
            renderer.velocityScale = 0.08f;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            bursts.Add(ps);
        }
        if (ps == null) return;
        ps.transform.SetPositionAndRotation(position,
            Quaternion.LookRotation(direction.sqrMagnitude > 0.001f ? direction : Vector3.up));
        var settings = ps.main;
        float lifetime = Mathf.Max(0.01f, sparkLifetime);
        settings.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.4f, lifetime);
        settings.startSpeed = new ParticleSystem.MinMaxCurve(sparkSpeed * 0.4f, sparkSpeed);
        settings.startSize = new ParticleSystem.MinMaxCurve(sparkSize * 0.5f, sparkSize);
        settings.startColor = color;
        settings.gravityModifier = 0.6f;
        ps.Play();
        ps.Emit(Mathf.Clamp(count, 1, 128));
    }

    private void Update()
    {
        foreach (Tracer tracer in tracers)
        {
            if (!tracer.line.enabled) continue;
            float age = (Time.time - tracer.started) / tracer.duration;
            if (age >= 1f) { tracer.line.enabled = false; continue; }
            Color color = tracer.color;
            color.a *= 1f - age;
            tracer.line.startColor = tracer.line.endColor = color;
        }
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }
}
