using HarmonyInstance = HarmonyLib.Harmony;
using System.Collections;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

public enum LeftRight {
    Left,
    Right,
}

/// <summary>
/// 纯火控领域逻辑：查找游戏对象、读取游戏数据、操控游戏内交互（dial 等）。
/// </summary>
public class FSC
{
    private const string HarmonyId = "com.svr2kos2.ironnestfcs.logic";

    private const float PowderCheckInterval = 5f;
    private const int PowderReplenishThreshold = 6;

    // Watchdogs. A failed game interaction must never occupy a gun/turret forever.
    private const float ReloadReadyTimeoutSeconds = 60f;
    private const float LoadingTimeoutSeconds = 60f;
    private const float ElevationTimeoutSeconds = 35f;
    private const float TurretRotationTimeoutSeconds = 45f;
    private const float AutoTurretWaitTimeoutSeconds = 90f;
    private const float ManualTurretWaitTimeoutSeconds = 300f;
    private const float AutoFireTimeoutSeconds = 25f;
    private const float ManualFireTimeoutSeconds = 300f;
    private const int RecentTaskLimit = 20;

    private HarmonyInstance? _harmony;
    
    private FcsSceneInteractor _sceneInteractor;
    private readonly PurchaseDeck _purchaseDeck = new();
    public readonly MapTable MapTable = new MapTable();
    public readonly BallisticCalculator BallisticCalculator = new BallisticCalculator();
    public readonly GunSystem LeftGun = new GunSystem();
    public readonly GunSystem RightGun = new GunSystem();
    public readonly Turret Turret = new Turret();
    public readonly TriggerConsole TriggerConsole = new();
    
    private readonly Queue<ArtilleryTask> _taskQueue = new();
    private readonly Queue<ArtilleryTask> _recentTasks = new();

    public ArtilleryTask? LeftTask { get; private set; }
    public ArtilleryTask? RightTask { get; private set; }

    public int PendingCount => _taskQueue.Count;
    public Queue<ArtilleryTask> QueueCan => new Queue<ArtilleryTask>(_taskQueue);
    public Queue<ArtilleryTask> RecentTasks => new Queue<ArtilleryTask>(_recentTasks);
    public bool AutoFireEnabled => _sceneInteractor.AutoFire;
    public bool MaxChargeEnabled => _sceneInteractor.maxCharge;
    public int CompletedTaskCount { get; private set; }
    public int SuccessfulTaskCount { get; private set; }
    public int FailedTaskCount { get; private set; }

    /// <summary>
    /// 弹道计算器、采购台和确认台是共享短操作硬件。
    /// </summary>
    private readonly CoroutineLock _deskLock = new();

    /// <summary>
    /// 炮塔方向是两炮共享资源；一个任务拿到方向后一直持有到该发完成/失败。
    /// </summary>
    private readonly CoroutineLock _turretLock = new();

    private readonly List<object> _runningCoroutines = new();

    public FSC() {
        _sceneInteractor = new FcsSceneInteractor(this);
    }

    public bool IsBound { get; private set; } = false;

    /// <summary>
    /// 对场景绑定做统一异常隔离。正式版对象名发生变化时应表现为“未绑定”，而不是直接炸掉整个 Logic。
    /// </summary>
    private static bool TryBindSafe(string name, Func<bool> binder) {
        try {
            var ok = binder();
            if (!ok) MelonLogger.Warning($"[FCS] Bind failed: {name}");
            return ok;
        }
        catch (Exception ex) {
            MelonLogger.Error($"[FCS] Bind exception in {name}: {ex}");
            return false;
        }
    }

    /// <summary>查找并绑定游戏对象。返回 false 表示当前场景还没有目标控件。</summary>
    public bool TryBind()
    {
        _sceneInteractor = new FcsSceneInteractor(this);
        _harmony = new HarmonyInstance(HarmonyId);
        _deskLock.Reset();
        _turretLock.Reset();
        FcsRuntimeClock.Reset();

        // Do not create any in-world FCS controls until all Iron Nest-specific scene
        // objects have been found. This makes the universal MelonGame loader safe.
        IsBound = TryBindSafe(nameof(MapTable), MapTable.TryBind)
                  && TryBindSafe(nameof(BallisticCalculator), BallisticCalculator.TryBind)
                  && TryBindSafe("LeftGun", () => LeftGun.TryBind("Left"))
                  && TryBindSafe("RightGun", () => RightGun.TryBind("Right"))
                  && TryBindSafe(nameof(PurchaseDeck), _purchaseDeck.TryBind)
                  && TryBindSafe(nameof(Turret), Turret.TryBind)
                  && TryBindSafe(nameof(TriggerConsole), TriggerConsole.TryBind);

        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        if (IsBound) {
            _sceneInteractor.Initialize();
            _runningCoroutines.Add(MelonCoroutines.Start(ReplenishPowderLoop()));
        }
        return IsBound;
    }

    public void Update() {
        // Track focus even while the game keeps running in the background. This freezes the
        // FCS-only clock during Alt+Tab without changing the game's own run-in-background policy.
        FcsRuntimeClock.Update();
        if (!FcsRuntimeClock.IsFocused)
            return;

        _sceneInteractor.Update();
        TryDispatch();
    }
    
    public void Dispose()
    {
        foreach (var handle in _runningCoroutines) {
            try { MelonCoroutines.Stop(handle); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] Stop coroutines failed: {ex}"); }
        }
        _runningCoroutines.Clear();

        // A hot reload can stop a task while it owns release-version gun elevation
        // control. Always restore the game's original controller mode before unloading.
        LeftGun.ReleaseElevationOverride();
        RightGun.ReleaseElevationOverride();

        _taskQueue.Clear();
        _recentTasks.Clear();
        LeftTask = null;
        RightTask = null;

        _sceneInteractor.ShutDown();
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] UnpatchSelf failed: {ex}"); }
        _harmony = null;
    }

    private IEnumerator ReplenishPowderLoop() {
        while (true) {
            yield return FcsRuntimeClock.WaitForSeconds(PowderCheckInterval);
            yield return FcsRuntimeClock.WaitUntilFocused();

            var charges = Math.Min(LeftGun.RemainingCharges(), RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;
            MelonLogger.Msg(
                $"[FCS] AutoReplenish: powder charges {charges} < {PowderReplenishThreshold}, buying one");
            yield return _deskLock.Acquire();
            try {
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _purchaseDeck.BuyPowders();
            }
            finally {
                _deskLock.Release();
            }
        }
    }

    public IEnumerator ExposeAllEntities() {
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            foreach (var m in MapTable.GetAllFireMissionEntities()) {
                var vr = m.transform.FindChild("VisualRoot");
                vr.gameObject.SetActive(true);
                vr.FindChild("Info").gameObject.SetActive(true);
            }
            yield return FcsRuntimeClock.WaitForSeconds(1f);
        }
    }

    public void EnqueueTask(ArtilleryTask task) {
        task.progress = Progress.Pending;
        task.startedAt = FcsRuntimeClock.Now;
        task.completedAt = 0f;
        task.failureReason = "";
        task.chargeCount = 0;
        task.elevation = 0f;
        _taskQueue.Enqueue(task);
        TryDispatch();
    }

    /// <summary>
    /// 调度不再把 LeftTask/RightTask == null 等同于“炮是空的”。F9 会清掉任务对象，
    /// 但真实炮膛/装药仍留在游戏里；因此每次派发都重新读取左右炮物理状态。
    /// 优先复用已经装填且与目标兼容的炮，其次才把普通任务交给真正的空炮。
    /// </summary>
    private void TryDispatch() {
        if (!FcsRuntimeClock.IsFocused)
            return;

        while (_taskQueue.Count > 0) {
            var task = _taskQueue.Peek();
            if (TryChooseGun(task, out var slot, out var reuseLoadedRound)) {
                _taskQueue.Dequeue();
                if (slot == LeftRight.Left) LeftTask = task;
                else RightTask = task;
                StartTaskRoutine(slot, task, reuseLoadedRound);
                continue;
            }

            // 有炮正在执行任务时，队首暂时无法派发并不代表目标无效；等当前任务完成后再试。
            if (LeftTask != null || RightTask != null)
                break;

            var leftState = GunPhysicalState.Read("Left");
            var rightState = GunPhysicalState.Read("Right");

            // 装填机构还在动作或状态暂时不可判定时不要抢控制权，留在队列等待下一帧。
            if (!leftState.IsStable || !rightState.IsStable)
                break;

            // 两门炮都没有正在执行的 FCS 任务，而且物理状态稳定，但没有任何一门能接这个目标。
            // 这种任务不能无限堵住队列；拒绝本次目标，保留炮内现有弹药不动。
            _taskQueue.Dequeue();
            RejectPendingTask(task,
                $"no compatible gun for current physical loads; Left={leftState.Summary()}, Right={rightState.Summary()}");
        }
    }

    private bool TryChooseGun(ArtilleryTask task, out LeftRight slot, out bool reuseLoadedRound) {
        slot = LeftRight.Left;
        reuseLoadedRound = false;

        var leftState = LeftTask == null ? GunPhysicalState.Read("Left") : null;
        var rightState = RightTask == null ? GunPhysicalState.Read("Right") : null;

        // 已装填炮优先：避免一边已有可用炮弹，却又在另一边重新装一发相同任务。
        if (LeftTask == null && leftState != null && leftState.CanReuseFor(task.bulletType, task.distance)) {
            slot = LeftRight.Left;
            reuseLoadedRound = true;
            return true;
        }
        if (RightTask == null && rightState != null && rightState.CanReuseFor(task.bulletType, task.distance)) {
            slot = LeftRight.Right;
            reuseLoadedRound = true;
            return true;
        }

        // 普通装填只允许进入真正的空炮。已装填但不兼容的炮绝不能再塞一发到它后面。
        if (LeftTask == null && leftState != null && leftState.EmptyReady) {
            slot = LeftRight.Left;
            return true;
        }
        if (RightTask == null && rightState != null && rightState.EmptyReady) {
            slot = LeftRight.Right;
            return true;
        }

        return false;
    }

    private void RejectPendingTask(ArtilleryTask task, string reason) {
        task.progress = Progress.Failed;
        task.failureReason = reason;
        MelonLogger.Error($"[FCS] T{task.targetId} rejected before dispatch: {reason}");
        RecordTaskResult(task);
    }

    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task, bool reuseLoadedRound) {
        var handle = MelonCoroutines.Start(RunTaskRoutine(leftRight, task, reuseLoadedRound));
        _runningCoroutines.Add(handle);
    }

    private void ReleaseSlot(LeftRight leftRight) {
        if (leftRight == LeftRight.Left) {
            LeftGun.ReleaseElevationOverride();
            LeftTask = null;
        }
        else {
            RightGun.ReleaseElevationOverride();
            RightTask = null;
        }
        TryDispatch();
    }

    private void RecordTaskResult(ArtilleryTask task) {
        task.completedAt = FcsRuntimeClock.Now;
        CompletedTaskCount++;
        if (task.progress == Progress.Finished) SuccessfulTaskCount++;
        else if (task.progress == Progress.Failed) FailedTaskCount++;

        _recentTasks.Enqueue(task);
        while (_recentTasks.Count > RecentTaskLimit)
            _recentTasks.Dequeue();
        _sceneInteractor.TaskFinished(task);
    }

    private void AbortTask(LeftRight leftRight, ArtilleryTask task, TurretReservation turret, string reason) {
        task.progress = Progress.Failed;
        task.failureReason = reason;
        turret.Canceled = true;
        ReleaseTurretOnce(turret);
        MelonLogger.Error($"[FCS] {leftRight} T{task.targetId} failed: {reason}");
        RecordTaskResult(task);
        ReleaseSlot(leftRight);
    }

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task, bool reuseLoadedRound) {
        yield return FcsRuntimeClock.WaitUntilFocused();

        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;
        var sideName = leftRight == LeftRight.Left ? "Left" : "Right";
        var turret = new TurretReservation();

        // 普通任务继续保持原有优化：一开始就预约/旋转炮塔，与解算和装填并行。
        // 已装填重定向任务则先用现有装药重新解算并校验，确认目标可用后才动炮塔。
        if (!reuseLoadedRound) {
            _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));
        }

        int powderCount;
        GunPhysicalState? loadedState = null;
        if (reuseLoadedRound) {
            loadedState = GunPhysicalState.Read(sideName);
            if (!loadedState.CanReuseFor(task.bulletType, task.distance)) {
                AbortTask(leftRight, task, turret,
                    $"loaded round changed or cannot cover target; {loadedState.Summary()}");
                yield break;
            }
            powderCount = loadedState.PowderCharges;
        }
        else {
            powderCount = _sceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);
        }
        task.chargeCount = powderCount;

        float elevation = 0f;
        bool viable = true;
        string failureReason = "";

        yield return _deskLock.Acquire();
        try {
            yield return FcsRuntimeClock.WaitUntilFocused();
            task.progress = Progress.Calculating;
            yield return BallisticCalculator.SetDistance(task.distance);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return BallisticCalculator.SetDirection(task.angel);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return BallisticCalculator.SetCharge(powderCount);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return BallisticCalculator.SetShellType(task.bulletType);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return BallisticCalculator.Calculate();
            yield return FcsRuntimeClock.WaitUntilFocused();
            elevation = BallisticCalculator.GetElevation();
            task.elevation = elevation;

            if (reuseLoadedRound) {
                // 当前炮弹/装药已经无法改变，只能使用游戏计算器给出的新仰角。
                // 若结果超出该炮物理仰角范围，则拒绝这个目标，但不动炮里的现有弹药。
                loadedState = GunPhysicalState.Read(sideName);
                if (!loadedState.IsElevationWithinPhysicalRange(elevation)) {
                    viable = false;
                    failureReason =
                        $"current loaded {task.bulletType.DisplayName()} C{powderCount} has no usable elevation for {task.distance:F2}km";
                }
            }
            else {
                var powderPurchaseAttempts = 0;
                while (gunSys.RemainingCharges() < powderCount && powderPurchaseAttempts < 10) {
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    yield return _purchaseDeck.BuyPowders();
                    powderPurchaseAttempts++;
                }
                if (gunSys.RemainingCharges() < powderCount) {
                    viable = false;
                    failureReason = $"powder unavailable: need {powderCount}, have {gunSys.RemainingCharges()}";
                }

                task.progress = Progress.SelectingBullet;
                if (viable && !gunSys.HaveBulletInCylinder(task.bulletType)) {
                    if (!gunSys.HaveEmptyShellInCylinder()) {
                        viable = false;
                        failureReason = $"no {task.bulletType} shell and cylinder has no empty slot";
                    }
                    else {
                        yield return FcsRuntimeClock.WaitUntilFocused();
                        yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                        yield return FcsRuntimeClock.WaitUntilFocused();
                        if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                            viable = false;
                            failureReason = $"purchase of {task.bulletType} did not reach the cylinder";
                        }
                    }
                }
            }
        }
        finally {
            _deskLock.Release();
        }

        if (!viable) {
            AbortTask(leftRight, task, turret, failureReason);
            yield break;
        }

        if (reuseLoadedRound) {
            // 解算期间游戏状态可能变化，因此真正接管前再核对一次炮膛和装药。
            loadedState = GunPhysicalState.Read(sideName);
            if (!loadedState.LoadedReady
                || loadedState.ShellType != task.bulletType
                || loadedState.PowderCharges != powderCount) {
                AbortTask(leftRight, task, turret,
                    $"loaded round changed before retargeting; {loadedState.Summary()}");
                yield break;
            }

            MelonLogger.Msg(
                $"[FCS] {leftRight} T{task.targetId}: reusing chambered {task.bulletType.DisplayName()} C{powderCount}");
            _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));
        }
        else {
            task.progress = Progress.LoadingBullet;
            yield return gunSys.WaitForReloadReady(ReloadReadyTimeoutSeconds);
            if (!gunSys.LastReloadReadySucceeded) {
                AbortTask(leftRight, task, turret, "reload mechanism was not ready for the next cycle");
                yield break;
            }

            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return gunSys.LoadBullet(task.bulletType);
            if (!gunSys.LastReloadActionSucceeded) {
                AbortTask(leftRight, task, turret,
                    string.IsNullOrEmpty(gunSys.LastReloadFailureReason)
                        ? "shell loading control failed"
                        : gunSys.LastReloadFailureReason);
                yield break;
            }

            task.progress = Progress.LoadingPowder;
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return gunSys.LoadPowder(powderCount);
            if (!gunSys.LastReloadActionSucceeded) {
                AbortTask(leftRight, task, turret,
                    string.IsNullOrEmpty(gunSys.LastReloadFailureReason)
                        ? "powder loading control failed"
                        : gunSys.LastReloadFailureReason);
                yield break;
            }

            task.progress = Progress.WaitLoading;
            var loadingDeadline = FcsRuntimeClock.Now + LoadingTimeoutSeconds;
            while (true) {
                yield return FcsRuntimeClock.WaitUntilFocused();
                if (gunSys.CanFire()) break;
                if (FcsRuntimeClock.Now >= loadingDeadline) {
                    AbortTask(leftRight, task, turret, $"loading timed out after {LoadingTimeoutSeconds:F0}s");
                    yield break;
                }
                yield return new WaitForSeconds(0.5f);
            }
        }

        task.progress = Progress.Aiming;
        yield return gunSys.SetElevation(elevation, ElevationTimeoutSeconds);
        if (!gunSys.LastElevationSucceeded) {
            AbortTask(leftRight, task, turret, $"elevation did not reach {elevation:F1}°");
            yield break;
        }

        task.progress = Progress.WaitingForFire;
        var turretWaitTimeout = _sceneInteractor.AutoFire
            ? AutoTurretWaitTimeoutSeconds
            : ManualTurretWaitTimeoutSeconds;
        var turretDeadline = FcsRuntimeClock.Now + turretWaitTimeout;
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (turret.Ready || turret.Failed) break;
            if (FcsRuntimeClock.Now >= turretDeadline) break;
            yield return null;
        }
        if (turret.Failed) {
            AbortTask(leftRight, task, turret, turret.FailureReason);
            yield break;
        }
        if (!turret.Ready) {
            AbortTask(leftRight, task, turret, $"turret reservation timed out after {turretWaitTimeout:F0}s");
            yield break;
        }

        try {
            yield return _deskLock.Acquire();
            try {
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return TriggerConsole.ConfirmTask();
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return TriggerConsole.ConfirmBullet();
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return TriggerConsole.ConfirmRotation();
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return TriggerConsole.ConfirmElevation();
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return TriggerConsole.ReadyToFire();
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return TriggerConsole.Arm(leftRight);
                if (_sceneInteractor.AutoFire) {
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    TriggerConsole.Fire();
                }
            }
            finally {
                _deskLock.Release();
            }

            var fireTimeout = _sceneInteractor.AutoFire
                ? AutoFireTimeoutSeconds
                : ManualFireTimeoutSeconds;
            yield return gunSys.WaitFire(fireTimeout);
        }
        finally {
            ReleaseTurretOnce(turret);
        }

        if (!gunSys.LastFireObserved) {
            AbortTask(leftRight, task, turret,
                _sceneInteractor.AutoFire ? "automatic fire was not observed" : "manual fire wait timed out");
            yield break;
        }

        task.progress = Progress.BackToIdle;
        yield return gunSys.WaitBackToIdle();
        yield return FcsRuntimeClock.WaitUntilFocused();
        task.progress = Progress.Finished;
        RecordTaskResult(task);
        ReleaseSlot(leftRight);
    }

    private sealed class TurretReservation {
        public bool Acquired;
        public bool Ready;
        public bool Failed;
        public bool Canceled;
        public bool Released;
        public string FailureReason = "";
    }

    private IEnumerator ReserveTurretAndRotate(ArtilleryTask task, TurretReservation res) {
        yield return _turretLock.Acquire();
        res.Acquired = true;
        yield return FcsRuntimeClock.WaitUntilFocused();
        if (res.Canceled) {
            ReleaseTurretOnce(res);
            yield break;
        }

        yield return Turret.SetRotation(task.angel, TurretRotationTimeoutSeconds);
        yield return FcsRuntimeClock.WaitUntilFocused();
        if (!Turret.LastRotationSucceeded) {
            res.Failed = true;
            res.FailureReason = $"turret could not reach {task.angel:F1}°";
            ReleaseTurretOnce(res);
            yield break;
        }

        res.Ready = true;
        if (res.Canceled) {
            ReleaseTurretOnce(res);
        }
    }

    private void ReleaseTurretOnce(TurretReservation res) {
        if (res.Acquired && !res.Released) {
            res.Released = true;
            _turretLock.Release();
        }
    }
}
