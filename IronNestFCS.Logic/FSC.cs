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

    private const float ReloadReadyTimeoutSeconds = 60f;
    private const float LoadingTimeoutSeconds = 60f;
    private const float ElevationTimeoutSeconds = 35f;
    private const float TurretRotationTimeoutSeconds = 45f;
    private const float AutoTurretWaitTimeoutSeconds = 90f;
    private const float ManualTurretWaitTimeoutSeconds = 300f;
    private const float AutoFireTimeoutSeconds = 25f;
    private const float ManualFireTimeoutSeconds = 300f;
    private const float PhysicalRecoveryTimeoutSeconds = 30f;
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

    private readonly CoroutineLock _deskLock = new();
    private readonly CoroutineLock _turretLock = new();
    private readonly List<object> _runningCoroutines = new();

    private float _leftRecoveryStartedAt = -1f;
    private float _rightRecoveryStartedAt = -1f;
    private bool _leftRecoveryTimeoutLogged;
    private bool _rightRecoveryTimeoutLogged;

    private enum GunTaskMode {
        FreshLoad,
        CompleteShellLoaded,
        ReuseLoadedRound,
    }

    public FSC() {
        _sceneInteractor = new FcsSceneInteractor(this);
    }

    public bool IsBound { get; private set; } = false;

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

    public bool TryBind()
    {
        _sceneInteractor = new FcsSceneInteractor(this);
        _harmony = new HarmonyInstance(HarmonyId);
        _deskLock.Reset();
        _turretLock.Reset();
        FcsRuntimeClock.Reset();
        ResetPhysicalRecoveryTracking();

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
            _runningCoroutines.Add(MelonCoroutines.Start(ResetSharedFireControlsAfterBind()));
            _runningCoroutines.Add(MelonCoroutines.Start(ReplenishPowderLoop()));
        }
        return IsBound;
    }

    public void Update() {
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

        LeftGun.ReleaseElevationOverride();
        RightGun.ReleaseElevationOverride();

        _taskQueue.Clear();
        _recentTasks.Clear();
        LeftTask = null;
        RightTask = null;
        ResetPhysicalRecoveryTracking();

        _sceneInteractor.ShutDown();
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] UnpatchSelf failed: {ex}"); }
        _harmony = null;
    }

    private void ResetPhysicalRecoveryTracking() {
        _leftRecoveryStartedAt = -1f;
        _rightRecoveryStartedAt = -1f;
        _leftRecoveryTimeoutLogged = false;
        _rightRecoveryTimeoutLogged = false;
    }

    /// <summary>
    /// F9 abandons the old task but the physical review switches/arming levers survive in the game scene.
    /// Reset those shared controls immediately after rebind so the system has a known firing baseline even
    /// before a new target is selected. PrepareForNewFireSolution disarms BOTH guns.
    /// </summary>
    private IEnumerator ResetSharedFireControlsAfterBind() {
        yield return FcsRuntimeClock.WaitUntilFocused();
        yield return _deskLock.Acquire();
        try {
            yield return TriggerConsole.PrepareForNewFireSolution(LeftRight.Left);
        }
        finally {
            _deskLock.Release();
        }
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
        task.dispatchExcludedGunMask = 0;
        _taskQueue.Enqueue(task);
        TryDispatch();
    }

    private void TryDispatch() {
        if (!FcsRuntimeClock.IsFocused)
            return;

        while (_taskQueue.Count > 0) {
            var task = _taskQueue.Peek();
            if (TryChooseGun(task, out var slot, out var mode)) {
                _taskQueue.Dequeue();
                if (slot == LeftRight.Left) LeftTask = task;
                else RightTask = task;
                StartTaskRoutine(slot, task, mode);
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

        // Exclusion only applies to the exact fixed loaded configuration that was already tried and found
        // unable to solve this target. If that gun later becomes shell-only or empty, it is a new usable state.
        if (LeftTask == null && !IsGunExcluded(task, LeftRight.Left)
            && leftState != null && leftState.CanReuseLoadedFor(task.bulletType)) {
            slot = LeftRight.Left;
            mode = GunTaskMode.ReuseLoadedRound;
            ResetPhysicalRecoveryTracking(LeftRight.Left, leftState);
            return true;
        }
        if (RightTask == null && !IsGunExcluded(task, LeftRight.Right)
            && rightState != null && rightState.CanReuseLoadedFor(task.bulletType)) {
            slot = LeftRight.Right;
            mode = GunTaskMode.ReuseLoadedRound;
            ResetPhysicalRecoveryTracking(LeftRight.Right, rightState);
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

    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task, GunTaskMode mode) {
        var handle = MelonCoroutines.Start(RunTaskRoutine(leftRight, task, mode));
        _runningCoroutines.Add(handle);
    }

    private void ClearSlotWithoutDispatch(LeftRight leftRight) {
        if (leftRight == LeftRight.Left) {
            LeftGun.ReleaseElevationOverride();
            LeftTask = null;
        }
        else {
            RightGun.ReleaseElevationOverride();
            RightTask = null;
        }
    }

    private void ReleaseSlot(LeftRight leftRight) {
        ClearSlotWithoutDispatch(leftRight);
        TryDispatch();
    }

    private void PrependTask(ArtilleryTask task) {
        var rest = _taskQueue.ToArray();
        _taskQueue.Clear();
        _taskQueue.Enqueue(task);
        foreach (var queued in rest)
            _taskQueue.Enqueue(queued);
    }

    private void RequeueForPhysicalReclassification(
        LeftRight leftRight,
        ArtilleryTask task,
        TurretReservation turret,
        string reason) {
        task.progress = Progress.Pending;
        task.failureReason = "";
        turret.Canceled = true;
        ReleaseTurretOnce(turret);
        ClearSlotWithoutDispatch(leftRight);
        PrependTask(task);
        MelonLogger.Warning($"[FCS] {leftRight} T{task.targetId}: state changed, reclassifying instead of failing: {reason}");
    }

    private void RetryOnAnotherGun(
        LeftRight leftRight,
        ArtilleryTask task,
        TurretReservation turret,
        string reason) {
        task.dispatchExcludedGunMask |= GunMask(leftRight);
        task.progress = Progress.Pending;
        task.failureReason = "";
        turret.Canceled = true;
        ReleaseTurretOnce(turret);
        ClearSlotWithoutDispatch(leftRight);
        PrependTask(task);
        MelonLogger.Warning(
            $"[FCS] {leftRight} T{task.targetId}: current preloaded configuration rejected ({reason}); trying another gun");
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

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task, GunTaskMode mode) {
        yield return FcsRuntimeClock.WaitUntilFocused();

        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;
        var sideName = leftRight == LeftRight.Left ? "Left" : "Right";
        var turret = new TurretReservation();

        var initialState = GunPhysicalState.Read(sideName);
        if (mode == GunTaskMode.FreshLoad && !initialState.EmptyReady) {
            RequeueForPhysicalReclassification(
                leftRight, task, turret, $"expected empty gun, got {initialState.Summary()}");
            yield break;
        }
        if (mode == GunTaskMode.CompleteShellLoaded
            && !initialState.CanCompleteShellFor(task.bulletType)) {
            RequeueForPhysicalReclassification(
                leftRight, task, turret, $"expected shell-loaded {task.bulletType.DisplayName()}, got {initialState.Summary()}");
            yield break;
        }
        if (mode == GunTaskMode.ReuseLoadedRound
            && !initialState.CanReuseLoadedFor(task.bulletType)) {
            RequeueForPhysicalReclassification(
                leftRight, task, turret, $"expected loaded {task.bulletType.DisplayName()}, got {initialState.Summary()}");
            yield break;
        }

        // Normal and shell-only recovery can select their charge from the new target, so turret rotation may
        // overlap their loading. A fully loaded round has a fixed charge: solve/validate it first, then move turret.
        if (mode != GunTaskMode.ReuseLoadedRound) {
            _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));
        }

        int powderCount;
        if (mode == GunTaskMode.ReuseLoadedRound) {
            // Do NOT use MinimumCharge as a range verdict here. It is only the FCS's preferred automatic
            // charge policy; a fixed already-loaded charge must be tested with the game's actual calculator.
            powderCount = initialState.PowderCharges;
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

            var stateForRange = GunPhysicalState.Read(sideName);
            if (!stateForRange.IsElevationWithinPhysicalRange(elevation)) {
                viable = false;
                failureReason =
                    $"{task.bulletType.DisplayName()} C{powderCount} has no usable elevation for {task.distance:F2}km (E={elevation:F2})";
            }

            if (viable && mode != GunTaskMode.ReuseLoadedRound) {
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
            }

            if (viable && mode == GunTaskMode.FreshLoad) {
                task.progress = Progress.SelectingBullet;
                if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
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
            if (mode == GunTaskMode.ReuseLoadedRound) {
                RetryOnAnotherGun(leftRight, task, turret, failureReason);
            }
            else {
                AbortTask(leftRight, task, turret, failureReason);
            }
            yield break;
        }

        if (mode == GunTaskMode.ReuseLoadedRound) {
            var loadedState = GunPhysicalState.Read(sideName);
            if (!loadedState.LoadedReady
                || loadedState.ShellType != task.bulletType
                || loadedState.PowderCharges != powderCount) {
                RequeueForPhysicalReclassification(
                    leftRight,
                    task,
                    turret,
                    $"loaded round changed before retargeting; {loadedState.Summary()}");
                yield break;
            }

            MelonLogger.Msg(
                $"[FCS] {leftRight} T{task.targetId}: reusing chambered {task.bulletType.DisplayName()} C{powderCount}");
            _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));
        }
        else {
            if (mode == GunTaskMode.FreshLoad) {
                var beforeShellLoad = GunPhysicalState.Read(sideName);
                if (!beforeShellLoad.EmptyReady) {
                    RequeueForPhysicalReclassification(
                        leftRight, task, turret, $"gun changed before shell load; {beforeShellLoad.Summary()}");
                    yield break;
                }

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
            }
            else {
                var shellState = GunPhysicalState.Read(sideName);
                if (!shellState.CanCompleteShellFor(task.bulletType)) {
                    RequeueForPhysicalReclassification(
                        leftRight, task, turret, $"chamber changed before powder load; {shellState.Summary()}");
                    yield break;
                }

                MelonLogger.Msg(
                    $"[FCS] {leftRight} T{task.targetId}: resuming chambered {task.bulletType.DisplayName()} with no powder");
                yield return gunSys.WaitForReloadReady(ReloadReadyTimeoutSeconds);
                if (!gunSys.LastReloadReadySucceeded) {
                    AbortTask(leftRight, task, turret, "reload mechanism was not ready to resume powder loading");
                    yield break;
                }
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
                var loaded = GunPhysicalState.Read(sideName);
                if (loaded.LoadedReady
                    && loaded.ShellType == task.bulletType
                    && loaded.PowderCharges == powderCount)
                    break;

                if (FcsRuntimeClock.Now >= loadingDeadline) {
                    AbortTask(
                        leftRight,
                        task,
                        turret,
                        $"loading did not converge to {task.bulletType.DisplayName()} C{powderCount}; {loaded.Summary()}");
                    yield break;
                }
                yield return FcsRuntimeClock.WaitForSeconds(0.25f);
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
                yield return TriggerConsole.PrepareForNewFireSolution(leftRight);
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
