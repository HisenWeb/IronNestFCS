using IronNestFCS.Logic.FCS;
using MelonLoader;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Owns task queues, gun-slot assignment and physical-state-based dispatch/reclassification.
/// Physical game state remains authoritative; task state is never used to infer an empty gun.
/// </summary>
internal sealed class TaskDispatcher {
    private const float PhysicalRecoveryTimeoutSeconds = 30f;
    private const int RecentTaskLimit = 20;

    private readonly FSC _fcs;
    private readonly Queue<ArtilleryTask> _taskQueue = new();
    private readonly Queue<ArtilleryTask> _recentTasks = new();

    private float _leftRecoveryStartedAt = -1f;
    private float _rightRecoveryStartedAt = -1f;
    private bool _leftRecoveryTimeoutLogged;
    private bool _rightRecoveryTimeoutLogged;

    public ArtilleryTask? LeftTask { get; private set; }
    public ArtilleryTask? RightTask { get; private set; }

    public int PendingCount => _taskQueue.Count;
    public Queue<ArtilleryTask> QueueSnapshot => new(_taskQueue);
    public Queue<ArtilleryTask> RecentSnapshot => new(_recentTasks);
    public int CompletedTaskCount { get; private set; }
    public int SuccessfulTaskCount { get; private set; }
    public int FailedTaskCount { get; private set; }

    public TaskDispatcher(FSC fcs) {
        _fcs = fcs;
    }

    public void ResetPhysicalRecoveryTracking() {
        _leftRecoveryStartedAt = -1f;
        _rightRecoveryStartedAt = -1f;
        _leftRecoveryTimeoutLogged = false;
        _rightRecoveryTimeoutLogged = false;
    }

    public void DisposeState() {
        _taskQueue.Clear();
        _recentTasks.Clear();
        LeftTask = null;
        RightTask = null;
        ResetPhysicalRecoveryTracking();
    }

    public void EnqueueTask(ArtilleryTask task) {
        task.progress = Progress.Pending;
        task.startedAt = FcsRuntimeClock.Now;
        task.completedAt = 0f;
        task.failureReason = "";
        task.chargeCount = 0;
        task.elevation = 0f;
        task.dispatchExcludedGunMask = 0;
        _taskQueue.Enqueue(task);
        TryDispatch();
    }

    public void TryDispatch() {
        if (!FcsRuntimeClock.IsFocused)
            return;

        while (_taskQueue.Count > 0) {
            var task = _taskQueue.Peek();
            if (TryChooseGun(task, out var slot, out var mode)) {
                _taskQueue.Dequeue();
                if (slot == LeftRight.Left) LeftTask = task;
                else RightTask = task;
                _fcs.FirePriority.OnTaskAssigned();
                _fcs.TaskRunner.Start(slot, task, mode);
                continue;
            }

            if (LeftTask != null || RightTask != null)
                break;

            var leftState = GunPhysicalState.Read("Left");
            var rightState = GunPhysicalState.Read("Right");

            var waitLeft = ShouldWaitForPhysicalRecovery(LeftRight.Left, leftState);
            var waitRight = ShouldWaitForPhysicalRecovery(LeftRight.Right, rightState);
            if (waitLeft || waitRight)
                break;

            _taskQueue.Dequeue();
            RejectPendingTask(task,
                $"no compatible gun for current physical loads; Left={leftState.Summary()}, Right={rightState.Summary()}");
        }
    }

    private bool TryChooseGun(ArtilleryTask task, out LeftRight slot, out GunTaskMode mode) {
        slot = LeftRight.Left;
        mode = GunTaskMode.FreshLoad;

        var leftState = LeftTask == null ? GunPhysicalState.Read("Left") : null;
        var rightState = RightTask == null ? GunPhysicalState.Read("Right") : null;

        // Exclusion only applies to the exact fixed loaded configuration already tried and rejected.
        // Shell-only or empty is a new usable physical state.
        var leftReusable = LeftTask == null
                           && !IsGunExcluded(task, LeftRight.Left)
                           && leftState != null
                           && leftState.CanReuseLoadedFor(task.bulletType);
        var rightReusable = RightTask == null
                            && !IsGunExcluded(task, LeftRight.Right)
                            && rightState != null
                            && rightState.CanReuseLoadedFor(task.bulletType);

        if (leftReusable || rightReusable) {
            if (leftReusable && rightReusable) {
                var preferredCharge = BallisticCalculator.MinimumCharge(task.distance);
                var leftScore = LoadedRoundDispatchScore(leftState!.PowderCharges, preferredCharge);
                var rightScore = LoadedRoundDispatchScore(rightState!.PowderCharges, preferredCharge);

                // Preserve deterministic Left-first behavior only for a genuine suitability tie. Otherwise use
                // the already-loaded round whose charge best fits the target's normal charge band. This matters
                // especially after F9, where both physical rounds survive but their C-values can differ.
                slot = rightScore < leftScore ? LeftRight.Right : LeftRight.Left;
                var chosen = slot == LeftRight.Left ? leftState : rightState;
                MelonLogger.Msg(
                    $"[FCS] T{task.targetId}: preloaded pairing preferred=C{preferredCharge}; " +
                    $"Left=C{leftState.PowderCharges} score={leftScore}, Right=C{rightState.PowderCharges} score={rightScore}; " +
                    $"choosing {slot} C{chosen!.PowderCharges}");
            }
            else {
                slot = leftReusable ? LeftRight.Left : LeftRight.Right;
            }

            mode = GunTaskMode.ReuseLoadedRound;
            ResetPhysicalRecoveryTracking(slot, slot == LeftRight.Left ? leftState! : rightState!);
            return true;
        }

        if (LeftTask == null
            && leftState != null && leftState.CanCompleteShellFor(task.bulletType)) {
            slot = LeftRight.Left;
            mode = GunTaskMode.CompleteShellLoaded;
            ResetPhysicalRecoveryTracking(LeftRight.Left, leftState);
            return true;
        }
        if (RightTask == null
            && rightState != null && rightState.CanCompleteShellFor(task.bulletType)) {
            slot = LeftRight.Right;
            mode = GunTaskMode.CompleteShellLoaded;
            ResetPhysicalRecoveryTracking(LeftRight.Right, rightState);
            return true;
        }

        if (LeftTask == null && leftState != null && leftState.EmptyReady) {
            slot = LeftRight.Left;
            mode = GunTaskMode.FreshLoad;
            ResetPhysicalRecoveryTracking(LeftRight.Left, leftState);
            return true;
        }
        if (RightTask == null && rightState != null && rightState.EmptyReady) {
            slot = LeftRight.Right;
            mode = GunTaskMode.FreshLoad;
            ResetPhysicalRecoveryTracking(LeftRight.Right, rightState);
            return true;
        }

        return false;
    }

    private static int LoadedRoundDispatchScore(int actualCharge, int preferredCharge) {
        var delta = actualCharge - preferredCharge;
        if (delta == 0)
            return 0;

        // A higher-than-normal charge is usually still a usable ballistic configuration and will be verified by
        // the real calculator. A charge below MinimumCharge is much more likely to be out of range, so prefer any
        // available over-charge before an under-charge while still allowing the latter as a fallback.
        return delta > 0 ? delta : 100 - delta;
    }

    private static int GunMask(LeftRight side) => side == LeftRight.Left ? 1 : 2;

    private static bool IsGunExcluded(ArtilleryTask task, LeftRight side) {
        return (task.dispatchExcludedGunMask & GunMask(side)) != 0;
    }

    private bool ShouldWaitForPhysicalRecovery(LeftRight side, GunPhysicalState state) {
        if (state.IsRecognizedStable) {
            ResetPhysicalRecoveryTracking(side, state);
            return false;
        }

        var now = FcsRuntimeClock.Now;
        float startedAt;
        bool timeoutLogged;

        if (side == LeftRight.Left) {
            if (_leftRecoveryStartedAt < 0f) {
                _leftRecoveryStartedAt = now;
                _leftRecoveryTimeoutLogged = false;
                MelonLogger.Msg($"[FCS] Left physical state waiting for recovery: {state.Summary()}");
            }
            startedAt = _leftRecoveryStartedAt;
            timeoutLogged = _leftRecoveryTimeoutLogged;
        }
        else {
            if (_rightRecoveryStartedAt < 0f) {
                _rightRecoveryStartedAt = now;
                _rightRecoveryTimeoutLogged = false;
                MelonLogger.Msg($"[FCS] Right physical state waiting for recovery: {state.Summary()}");
            }
            startedAt = _rightRecoveryStartedAt;
            timeoutLogged = _rightRecoveryTimeoutLogged;
        }

        if (now - startedAt < PhysicalRecoveryTimeoutSeconds)
            return true;

        if (!timeoutLogged) {
            MelonLogger.Error(
                $"[FCS] {side} physical state did not converge within {PhysicalRecoveryTimeoutSeconds:F0}s: {state.Summary()}");
            if (side == LeftRight.Left) _leftRecoveryTimeoutLogged = true;
            else _rightRecoveryTimeoutLogged = true;
        }

        return false;
    }

    private void ResetPhysicalRecoveryTracking(LeftRight side, GunPhysicalState state) {
        if (!state.IsRecognizedStable)
            return;

        if (side == LeftRight.Left) {
            if (_leftRecoveryStartedAt >= 0f)
                MelonLogger.Msg($"[FCS] Left physical state recovered as {state.Summary()}");
            _leftRecoveryStartedAt = -1f;
            _leftRecoveryTimeoutLogged = false;
        }
        else {
            if (_rightRecoveryStartedAt >= 0f)
                MelonLogger.Msg($"[FCS] Right physical state recovered as {state.Summary()}");
            _rightRecoveryStartedAt = -1f;
            _rightRecoveryTimeoutLogged = false;
        }
    }

    private void RejectPendingTask(ArtilleryTask task, string reason) {
        task.progress = Progress.Failed;
        task.failureReason = reason;
        MelonLogger.Error($"[FCS] T{task.targetId} rejected before dispatch: {reason}");
        RecordTaskResult(task);
    }

    public bool IsActiveTask(ArtilleryTask task) {
        return ReferenceEquals(LeftTask, task) || ReferenceEquals(RightTask, task);
    }

    public ArtilleryTask? GetActiveTask(LeftRight side) {
        return side == LeftRight.Left ? LeftTask : RightTask;
    }

    public bool TryGetTaskSide(ArtilleryTask task, out LeftRight side) {
        if (ReferenceEquals(LeftTask, task)) {
            side = LeftRight.Left;
            return true;
        }
        if (ReferenceEquals(RightTask, task)) {
            side = LeftRight.Right;
            return true;
        }
        side = LeftRight.Left;
        return false;
    }

    public void ClearSlotWithoutDispatch(LeftRight side) {
        if (side == LeftRight.Left) {
            _fcs.LeftGun.ReleaseElevationOverride();
            LeftTask = null;
        }
        else {
            _fcs.RightGun.ReleaseElevationOverride();
            RightTask = null;
        }
    }

    public void ReleaseSlot(LeftRight side) {
        ClearSlotWithoutDispatch(side);
        TryDispatch();
    }

    private void PrependTask(ArtilleryTask task) {
        var rest = _taskQueue.ToArray();
        _taskQueue.Clear();
        _taskQueue.Enqueue(task);
        foreach (var queued in rest)
            _taskQueue.Enqueue(queued);
    }

    public void RequeueForPhysicalReclassification(
        LeftRight side,
        ArtilleryTask task,
        TurretReservation turret,
        string reason) {
        task.progress = Progress.Pending;
        task.failureReason = "";
        turret.Canceled = true;
        _fcs.TurretScheduler.ReleaseOnce(turret);
        _fcs.FirePriority.InvalidateForAbnormalTask(task, $"任务重分类：{reason}");
        ClearSlotWithoutDispatch(side);
        _fcs.FirePriority.EvaluateTrigger();
        PrependTask(task);
        MelonLogger.Warning($"[FCS] {side} T{task.targetId}: state changed, reclassifying instead of failing: {reason}");
    }

    public void RetryOnAnotherGun(
        LeftRight side,
        ArtilleryTask task,
        TurretReservation turret,
        string reason) {
        task.dispatchExcludedGunMask |= GunMask(side);
        task.progress = Progress.Pending;
        task.failureReason = "";
        turret.Canceled = true;
        _fcs.TurretScheduler.ReleaseOnce(turret);
        _fcs.FirePriority.InvalidateForAbnormalTask(task, $"改派另一门炮：{reason}");
        ClearSlotWithoutDispatch(side);
        _fcs.FirePriority.EvaluateTrigger();
        PrependTask(task);
        MelonLogger.Warning(
            $"[FCS] {side} T{task.targetId}: current preloaded configuration rejected ({reason}); trying another gun");
    }

    public void RecordTaskResult(ArtilleryTask task) {
        task.completedAt = FcsRuntimeClock.Now;
        CompletedTaskCount++;
        if (task.progress == Progress.Finished) SuccessfulTaskCount++;
        else if (task.progress == Progress.Failed) FailedTaskCount++;

        _recentTasks.Enqueue(task);
        while (_recentTasks.Count > RecentTaskLimit)
            _recentTasks.Dequeue();
        _fcs.SceneInteractor.TaskFinished(task);
    }

    public void AbortTask(LeftRight side, ArtilleryTask task, TurretReservation turret, string reason) {
        task.progress = Progress.Failed;
        task.failureReason = reason;
        turret.Canceled = true;
        _fcs.TurretScheduler.ReleaseOnce(turret);
        _fcs.FirePriority.InvalidateForAbnormalTask(task, $"任务失败：{reason}");
        ClearSlotWithoutDispatch(side);
        _fcs.FirePriority.EvaluateTrigger();
        MelonLogger.Error($"[FCS] {side} T{task.targetId} failed: {reason}");
        RecordTaskResult(task);
        TryDispatch();
    }
}
