using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public enum Progress {
    Pending,
    Calculating,
    SelectingBullet,
    LoadingBullet,
    LoadingPowder,
    WaitLoading,
    Aiming,
    WaitingForFire,
    BackToIdle,
    Finished,
    Failed,
}

public enum PendingHint {
    None,
    ShellMismatch,
    ChargeRangeInsufficient,
    AmmoMismatch,
}

/// <summary>
/// One fire mission. Every member below is a public field on purpose: the external bridge
/// (IronNestAgentBridge) reflects over this type with GetField and builds instances through
/// Activator.CreateInstance, so the public parameterless constructor, the member names, the
/// member kind (field, never property) and the field types are all a frozen contract.
/// A freshly constructed instance must already be safe to enqueue — in particular
/// failureReason is "" (never null) and serial stays 0 so the dispatcher can stamp it.
/// </summary>
public class ArtilleryTask {
    // targetId is a recyclable map-marker id and may repeat; 0 is a legal value meaning
    // "no marker, pure aim-point task" (the bridge always enqueues with targetId = 0).
    // It is never a usable external handle — use serial for that.
    public int targetId;
    public float angel;
    public float distance;
    public Vector3 position;
    public BulletType bulletType;
    public Progress progress;

    // Lightweight UI hint for a task that remains in the pending queue.
    public PendingHint pendingHint;

    // Snapshot of the solved firing data. Keeping it on the task lets the UI show
    // exactly what the automation decided instead of only the current phase.
    public int chargeCount;
    public float elevation;

    // Runtime diagnostics used by the watchdog/recovery path and the recent-task UI.
    public float startedAt;
    public float completedAt;
    public string failureReason = "";

    // Runtime-only dispatch memory. If a preloaded gun is tried and its fixed shell/charge cannot
    // solve the target, exclude that side and let the same task fall back to the other gun.
    // Bit 0 = Left, bit 1 = Right. Reset when a brand-new target is enqueued.
    public int dispatchExcludedGunMask;

    // Globally unique mission number #N, stamped by the dispatcher with a zero-value sentinel
    // (if (serial == 0) serial = ++counter). A non-zero value is always kept as-is, including a
    // value pre-set by an external caller, and requeueing (urgent preemption, load recovery)
    // never renumbers a task. Do not replace the sentinel with a private "already enqueued"
    // flag: that would change the observable "external serial is honoured" behaviour.
    public int serial;

    // Scheduling priority 0-100, >= 90 is urgent. Set by the caller before enqueueing;
    // enqueueing never resets it. Must stay a public int — the bridge's TrySetPriority uses
    // default BindingFlags and therefore only sees public members.
    public int priority = 50;

    // Aim point in map-local units, frozen at enqueue time. Late-bound gunnery solutions
    // (origin re-survey, motion lead) are only possible for a task that carries one.
    public bool hasAimPoint;
    public Vector3 aimLocal;

    // Non-empty = FCS tracks this map entity itself and fits a motion model from the samples.
    public string trackEntityId = "";

    // The model has not been refreshed for over 90s and is still being extrapolated.
    public bool trackingLost;

    // A linear motion model exists. External callers (bridge/LLM) may set the model directly
    // without asking FCS to track anything, hence this is independent of trackEntityId.
    public bool hasMotion;

    // p(t) = motionOriginLocal + motionVelLocalPerSec * (t - motionT0), t on the mission clock.
    public Vector3 motionOriginLocal;
    public Vector3 motionVelLocalPerSec;
    public float motionT0;

    // The agent re-aimed this task after it was enqueued. It opens the execution-time refresh
    // gates for otherwise static tasks.
    public bool aimAdjusted;

    // Shared retry counter for both powder recovery paths (commit mismatch and dispenser stall).
    public int loadRetryCount;

    // Queue lifetime. firstEnqueuedAt uses the same zero-value sentinel as serial so the window
    // is measured from the original command, not from the latest requeue.
    public float validForSeconds;
    public float firstEnqueuedAt;

    /// <summary>
    /// Motion status phrase shared by the HUD and external readers.
    /// The branch predicate is the disjunction of hasMotion and trackEntityId, not "is there a
    /// model": a tracked target that has never been sampled (always in the fog, or only one
    /// sample so far) still reports as tracked, with a zero velocity vector.
    /// </summary>
    public string MotionSuffix(bool zh) {
        if (!hasMotion && trackEntityId.Length == 0)
            return aimAdjusted ? (zh ? " · 已改瞄" : " · re-aimed") : "";

        // Speed deliberately uses the full Vector3 magnitude (z included): motionVelLocalPerSec is
        // a frozen public field that the bridge/LLM may write with a non-zero z, and only FCS's own
        // UpdateEntityMotion zeroes it. The horizontal Vector2 gauge used by ApplyMotionModel /
        // ShortenedAim is NOT interchangeable here — it would change both the reported km/h and
        // whether the task falls into the "static" branch below.
        var speedKmh = motionVelLocalPerSec.magnitude * 3.8164f * 3600f;

        // Course, on the other hand, is horizontal only.
        var course = Mathf.Atan2(motionVelLocalPerSec.x, motionVelLocalPerSec.y) * Mathf.Rad2Deg;
        if (course < 0f)
            course += 360f;

        var head = trackEntityId.Length > 0
            ? (zh ? $"跟踪 {trackEntityId}" : $"track {trackEntityId}")
            : (zh ? "运动模型" : "motion");
        var lost = trackingLost ? (zh ? "·失联外推" : "·extrapolating") : "";

        if (speedKmh < 0.5f)
            return zh ? $" · {head}(静止){lost}" : $" · {head}(static){lost}";

        return $" · {head} {speedKmh:F0}km/h→{course:000}°{lost}";
    }
}
