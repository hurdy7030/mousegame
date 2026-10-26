using System;
using System.Collections.Generic;

public enum ShotParryResult { None, Deflected, Reflected }

// Pure timing rules shared by gameplay and the standalone regression checks.
public readonly struct ParryTiming
{
    public readonly float duration, perfectStart, perfectEnd;

    public ParryTiming(float window, float perfectOffset, float perfectWindow)
    {
        duration = Math.Max(0f, window);
        perfectStart = Math.Min(duration, Math.Max(0f, perfectOffset));
        perfectEnd = Math.Min(duration, perfectStart + Math.Max(0f, perfectWindow));
    }

    public ShotParryResult Evaluate(float elapsed, bool spamming)
    {
        if (elapsed < 0f || elapsed > duration || duration <= 0f) return ShotParryResult.None;
        if (!spamming && perfectEnd > perfectStart && elapsed >= perfectStart && elapsed <= perfectEnd)
            return ShotParryResult.Reflected;
        return ShotParryResult.Deflected;
    }
}

public sealed class ParrySpamTracker
{
    private readonly Queue<float> swings = new Queue<float>();
    private float blockedUntil = float.NegativeInfinity;

    public int RecentSwingCount => swings.Count;
    public float CooldownRemaining(float now) => Math.Max(0f, blockedUntil - now);
    public bool IsSpamming(float now) => now < blockedUntil;

    public void RecordSwing(float now, float historyWindow, int allowedSwings, float cooldown)
    {
        Prune(now, historyWindow);
        swings.Enqueue(now);
        if (swings.Count > Math.Max(1, allowedSwings))
            blockedUntil = Math.Max(blockedUntil, now + Math.Max(0f, cooldown));
    }

    public void Prune(float now, float historyWindow)
    {
        float start = now - Math.Max(0.01f, historyWindow);
        while (swings.Count > 0 && swings.Peek() < start) swings.Dequeue();
    }

    public void Clear()
    {
        swings.Clear();
        blockedUntil = float.NegativeInfinity;
    }
}
