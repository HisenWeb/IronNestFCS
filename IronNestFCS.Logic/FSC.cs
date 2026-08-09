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
    private const float LoadingTimeoutSeconds = 60f;
    private const float ElevationTimeoutSeconds = 35f;
    private const float TurretRotationTimeoutSeconds = 45f;
    private const float AutoTurretWaitTimeoutSeconds = 90f;
    private const float ManualTurretWaitTimeoutSeconds = 300f;
    private const float AutoFireTimeoutSeconds = 25f;
    private const float ManualFireTimeoutSeconds = 300f;
    private const int RecentTaskLimit = 4;

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
        _sceneInteractor.Update();
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
            yield return new WaitForSeconds(PowderCheckInterval);
            var charges = Math.Min(LeftGun.RemainingCharges(), RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;
            MelonLogger.Msg(
                $"[FCS] AutoReplenish: powder charges {charges} < {PowderReplenishThreshold}, buying one");
            yield return _deskLock.Acquire();
            try {
                yield return _purchaseDeck.BuyPowders();
            }
            finally {
                _deskLock.Release();
            }
        }
    }

    public IEnumerator ExposeAllEntities() {
        while (true) {
            foreach (var m in MapTable.GetAllFireMissionEntities()) {
                var vr = m.transform.FindChild("VisualRoot");
                vr.gameObject.SetActive(true);
                vr.FindChild("Info").gameObject.SetActive(true);
            }
            yield return new WaitForSeconds(1f);
        }
    }

    public void EnqueueTask(ArtilleryTask task) {
        task.progress = Progress.Pending;
        task.startedAt = Time.realtimeSinceStartup;
        task.completedAt = 0f;
        task.failureReason = "";
        task.chargeCount = 0;
        task.elevation = 0f;
        _taskQueue.Enqueue(task);
        TryDispatch();
    }

    private void TryDispatch() {
        while (_taskQueue.Count > 0) {
            LeftRight slot;
            if (LeftTask == null) slot = LeftRight.Left;
            else if (RightTask == null) slot = LeftRight.Right;
            else break;

            var task = _taskQueue.Dequeue();
            if (slot == LeftRight.Left) LeftTask = task;
            else RightTask = task;
            StartTaskRoutine(slot, task);
        }
    }

    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var handle = MelonCoroutines.Start(RunTaskRoutine(leftRight, task));
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
        task.completedAt = Time.realtimeSinceStartup;
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

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;

        var turret = new TurretReservation();
        _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));
        
        var powderCount = _sceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);
        task.chargeCount = powderCount;

        float elevation = 0f;
        bool viable = true;
        string failureReason = "";

        yield return _deskLock.Acquire();
        try {
            task.progress = Progress.Calculating;
            yield return BallisticCalculator.SetDistance(task.distance);
            yield return BallisticCalculator.SetDirection(task.angel);
            yield return BallisticCalculator.SetCharge(powderCount);
            yield return BallisticCalculator.SetShellType(task.bulletType);
            yield return BallisticCalculator.Calculate();
            elevation = BallisticCalculator.GetElevation();
            task.elevation = elevation;

            var powderPurchaseAttempts = 0;
            while (gunSys.RemainingCharges() < powderCount && powderPurchaseAttempts < 10) {
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
                    yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                    if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                        viable = false;
                        failureReason = $"purchase of {task.bulletType} did not reach the cylinder";
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

        task.progress = Progress.LoadingBullet;
        yield return gunSys.LoadBullet(task.bulletType);

        task.progress = Progress.LoadingPowder;
        yield return gunSys.LoadPowder(powderCount);

        task.progress = Progress.WaitLoading;
        var loadingDeadline = Time.realtimeSinceStartup + LoadingTimeoutSeconds;
        while (!gunSys.CanFire() && Time.realtimeSinceStartup < loadingDeadline) {
            yield return new WaitForSeconds(0.5f);
        }
        if (!gunSys.CanFire()) {
            AbortTask(leftRight, task, turret, $"loading timed out after {LoadingTimeoutSeconds:F0}s");
            yield break;
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
        var turretDeadline = Time.realtimeSinceStartup + turretWaitTimeout;
        while (!turret.Ready && !turret.Failed && Time.realtimeSinceStartup < turretDeadline) {
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

        // Direction stays locked until the shot is observed. The desk lock is only held
        // while touching the shared confirmation controls, so the other gun may continue
        // ballistic calculation/loading in parallel.
        try {
            yield return _deskLock.Acquire();
            try {
                yield return TriggerConsole.ConfirmTask();
                yield return TriggerConsole.ConfirmBullet();
                yield return TriggerConsole.ConfirmRotation();
                yield return TriggerConsole.ConfirmElevation();
                yield return TriggerConsole.ReadyToFire();
                yield return TriggerConsole.Arm(leftRight);
                if (_sceneInteractor.AutoFire) {
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
        if (res.Canceled) {
            ReleaseTurretOnce(res);
            yield break;
        }

        yield return Turret.SetRotation(task.angel, TurretRotationTimeoutSeconds);
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
