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

public class ArtilleryTask {
    public int targetId;

    /// <summary>
    /// Scheduling priority (0-100, default 50). Higher wins gun assignment before charge-resource
    /// protection; >= 90 (e.g. counter-battery) also skips the match coalesce window. Set by the
    /// caller before EnqueueTask; not reset on enqueue.
    /// </summary>
    public int priority = 50;

    /// <summary>
    /// Fixed aim point on the map (map-local), captured at enqueue. When present, the
    /// firing solution (angel/distance) is re-derived from the CURRENT turret-piece
    /// position at every planning round — late binding, so origin recalibration while
    /// the task waits in queue automatically corrects the solution.
    /// </summary>
    public bool hasAimPoint;
    public UnityEngine.Vector3 aimLocal;
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
}
