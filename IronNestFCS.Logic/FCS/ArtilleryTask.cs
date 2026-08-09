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

public class ArtilleryTask {
    public int targetId;
    public float angel;
    public float distance;
    public Vector3 position;
    public BulletType bulletType;
    public Progress progress;

    // Snapshot of the solved firing data. Keeping it on the task lets the UI show
    // exactly what the automation decided instead of only the current phase.
    public int chargeCount;
    public float elevation;

    // Runtime diagnostics used by the watchdog/recovery path and the recent-task UI.
    public float startedAt;
    public float completedAt;
    public string failureReason = "";
}
