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
    public float angel;
    public float distance;
    public Vector3 position;
    public BulletType bulletType;
    public Progress progress;

    // Submission-mode marker. A Forced Sync queue entry remains one intent until it reaches the head barrier,
    // then TaskDispatcher materializes independent Left + Right execution tasks from that intent.
    public bool forcedSync;

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
