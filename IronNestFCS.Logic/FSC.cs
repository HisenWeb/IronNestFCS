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
    private const float FirePrioritySolveBufferSeconds = 2.7f;
    private const float FirePriorityScoreTieTolerance = 0.05f;
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

    private FirePriorityCandidate? _leftFireCandidate;
    private FirePriorityCandidate? _rightFireCandidate;
    private ArtilleryTask? _firePriorityWinner;
    private ArtilleryTask? _firePrioritySecond;
    private bool _firePriorityArbitrationRunning;
    private int _firePriorityArbitrationVersion;

    private enum GunTaskMode {
        FreshLoad,
        CompleteShellLoaded,
        ReuseLoadedRound,
    }

    private enum FirePriorityGunPhase {
        Preparation,
        FireCommitted,
        PostShotRecovery,
        Unavailable,
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
        ResetFirePriorityTracking();

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
        ResetFirePriorityTracking();

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

    private void ResetFirePriorityTracking() {
        _leftFireCandidate = null;
        _rightFireCandidate = null;
        _firePriorityWinner = null;
        _firePrioritySecond = null;
        _firePriorityArbitrationRunning = false;
        _firePriorityArbitrationVersion++;
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

    private bool IsActiveTask(ArtilleryTask task) {
        return ReferenceEquals(LeftTask, task) || ReferenceEquals(RightTask, task);
    }

    private FirePriorityCandidate? GetCurrentCandidate(LeftRight side) {
        var candidate = side == LeftRight.Left ? _leftFireCandidate : _rightFireCandidate;
        var active = side == LeftRight.Left ? LeftTask : RightTask;
        return candidate != null && ReferenceEquals(candidate.Task, active) ? candidate : null;
    }

    private ArtilleryTask? GetActiveTask(LeftRight side) {
        return side == LeftRight.Left ? LeftTask : RightTask;
    }

    private static LeftRight OtherSide(LeftRight side) {
        return side == LeftRight.Left ? LeftRight.Right : LeftRight.Left;
    }

    private FirePriorityGunPhase GetFirePriorityGunPhase(LeftRight side, ArtilleryTask task) {
        if (!ReferenceEquals(GetActiveTask(side), task))
            return FirePriorityGunPhase.Unavailable;

        if (task.progress == Progress.WaitingForFire)
            return FirePriorityGunPhase.FireCommitted;

        if (task.progress == Progress.BackToIdle)
            return FirePriorityGunPhase.PostShotRecovery;

        if (task.progress == Progress.Finished
            || task.progress == Progress.Failed
            || task.progress == Progress.Pending)
            return FirePriorityGunPhase.Unavailable;

        var physical = GunPhysicalState.Read(side == LeftRight.Left ? "Left" : "Right");
        if (physical.Kind == GunPhysicalStateKind.PostShotRecovery)
            return FirePriorityGunPhase.PostShotRecovery;

        if (physical.Kind == GunPhysicalStateKind.Unbound
            || physical.Kind == GunPhysicalStateKind.Unknown)
            return FirePriorityGunPhase.Unavailable;

        // EmptyReady / ShellLoaded / LoadedReady and in-progress reload Recovering all belong to the same
        // arbitration preparation band. They may be at different exact reload substates; what matters is that
        // neither gun has crossed into the shared fire path or the post-shot return-to-zero path.
        return FirePriorityGunPhase.Preparation;
    }

    private static string PhaseName(FirePriorityGunPhase phase) {
        return phase switch {
            FirePriorityGunPhase.Preparation => "preparation",
            FirePriorityGunPhase.FireCommitted => "fire-committed",
            FirePriorityGunPhase.PostShotRecovery => "post-shot-recovery",
            _ => "unavailable",
        };
    }

    private bool CanArbitrateTogether(FirePriorityCandidate left, FirePriorityCandidate right) {
        if (!ReferenceEquals(LeftTask, left.Task) || !ReferenceEquals(RightTask, right.Task))
            return false;

        return GetFirePriorityGunPhase(LeftRight.Left, left.Task) == FirePriorityGunPhase.Preparation
               && GetFirePriorityGunPhase(LeftRight.Right, right.Task) == FirePriorityGunPhase.Preparation;
    }

    private void CancelFirePriorityArbitration() {
        _firePriorityArbitrationVersion++;
        _firePriorityArbitrationRunning = false;
    }

    private void RegisterBallisticSolution(LeftRight side, ArtilleryTask task) {
        var candidate = new FirePriorityCandidate(side, task, FcsRuntimeClock.Now);
        if (side == LeftRight.Left) _leftFireCandidate = candidate;
        else _rightFireCandidate = candidate;

        if (ReferenceEquals(_firePriorityWinner, task) || ReferenceEquals(_firePrioritySecond, task))
            return;

        // Once a previous task already owns the fire lane, do not reopen arbitration. A newly solved task can
        // only become the committed second task behind that owner. This preserves the already-loaded/unfired gun
        // over any task that arrives later from the queue.
        if (_firePriorityWinner != null) {
            if (_firePrioritySecond == null && !ReferenceEquals(_firePriorityWinner, task)) {
                _firePrioritySecond = task;
                MelonLogger.Msg($"[FCS] {side} T{task.targetId}: ballistic solution ready; queued behind committed fire lane owner");
            }
            return;
        }

        TryStartFirePriorityArbitration();
    }

    private void TryStartFirePriorityArbitration() {
        if (_firePriorityWinner != null || _firePriorityArbitrationRunning)
            return;

        var left = GetCurrentCandidate(LeftRight.Left);
        var right = GetCurrentCandidate(LeftRight.Right);
        if (left == null && right == null)
            return;

        if (left != null && right != null) {
            if (CanArbitrateTogether(left, right)) {
                ResolveFirePriorityPair(left, right);
            }
            else {
                CommitOriginalFireOrder(left, right, "both solutions exist but gun phases are no longer synchronized for arbitration");
            }
            return;
        }

        var only = left ?? right!;
        var otherSide = OtherSide(only.Side);
        var otherTask = GetActiveTask(otherSide);
        if (otherTask == null) {
            SetFirePriority(only.Task, null,
                $"{only.Side} T{only.Task.targetId} is the only active solved task");
            return;
        }

        var ownPhase = GetFirePriorityGunPhase(only.Side, only.Task);
        var otherPhase = GetFirePriorityGunPhase(otherSide, otherTask);
        if (ownPhase != FirePriorityGunPhase.Preparation
            || otherPhase != FirePriorityGunPhase.Preparation) {
            SetFirePriority(
                only.Task,
                otherTask,
                $"state gate skipped arbitration: {only.Side}={PhaseName(ownPhase)}, {otherSide}={PhaseName(otherPhase)}");
            return;
        }

        _firePriorityArbitrationRunning = true;
        var version = ++_firePriorityArbitrationVersion;
        MelonLogger.Msg(
            $"[FCS] {only.Side} T{only.Task.targetId}: synchronized preparation detected; " +
            $"waiting up to {FirePrioritySolveBufferSeconds:F1}s for the other gun's ballistic solution");
        _runningCoroutines.Add(MelonCoroutines.Start(FirePrioritySolveBuffer(only, version)));
    }

    private IEnumerator FirePrioritySolveBuffer(FirePriorityCandidate first, int version) {
        var deadline = FcsRuntimeClock.Now + FirePrioritySolveBufferSeconds;
        try {
            while (version == _firePriorityArbitrationVersion
                   && _firePriorityWinner == null
                   && IsActiveTask(first.Task)) {
                yield return FcsRuntimeClock.WaitUntilFocused();

                var left = GetCurrentCandidate(LeftRight.Left);
                var right = GetCurrentCandidate(LeftRight.Right);
                if (left != null && right != null) {
                    if (CanArbitrateTogether(left, right)) {
                        ResolveFirePriorityPair(left, right);
                    }
                    else {
                        CommitOriginalFireOrder(left, right,
                            "both solutions arrived after the synchronized preparation gate had closed");
                    }
                    yield break;
                }

                var otherSide = OtherSide(first.Side);
                var otherTask = GetActiveTask(otherSide);
                if (otherTask == null) {
                    SetFirePriority(first.Task, null,
                        $"{first.Side} T{first.Task.targetId} lost its competing active task during solve buffer");
                    yield break;
                }

                var firstPhase = GetFirePriorityGunPhase(first.Side, first.Task);
                var otherPhase = GetFirePriorityGunPhase(otherSide, otherTask);
                if (firstPhase != FirePriorityGunPhase.Preparation
                    || otherPhase != FirePriorityGunPhase.Preparation) {
                    SetFirePriority(
                        first.Task,
                        otherTask,
                        $"state gate closed during solve buffer: {first.Side}={PhaseName(firstPhase)}, " +
                        $"{otherSide}={PhaseName(otherPhase)}");
                    yield break;
                }

                if (FcsRuntimeClock.Now >= deadline)
                    break;

                yield return FcsRuntimeClock.WaitForSeconds(0.1f);
            }

            if (version != _firePriorityArbitrationVersion
                || _firePriorityWinner != null
                || !IsActiveTask(first.Task))
                yield break;

            var finalLeft = GetCurrentCandidate(LeftRight.Left);
            var finalRight = GetCurrentCandidate(LeftRight.Right);
            if (finalLeft != null && finalRight != null && CanArbitrateTogether(finalLeft, finalRight)) {
                ResolveFirePriorityPair(finalLeft, finalRight);
                yield break;
            }

            var finalOtherTask = GetActiveTask(OtherSide(first.Side));
            SetFirePriority(
                first.Task,
                finalOtherTask,
                $"{first.Side} T{first.Task.targetId} kept original first-solved order after " +
                $"{FirePrioritySolveBufferSeconds:F1}s synchronized solve buffer expired");
        }
        finally {
            if (version == _firePriorityArbitrationVersion)
                _firePriorityArbitrationRunning = false;
        }
    }

    private void CommitOriginalFireOrder(
        FirePriorityCandidate left,
        FirePriorityCandidate right,
        string reason) {
        var winner = FirstByOriginalOrder(left, right);
        var loser = ReferenceEquals(winner, left) ? right : left;
        SetFirePriority(winner.Task, loser.Task, reason + "; keeping original solved order");
    }

    private void ResolveFirePriorityPair(FirePriorityCandidate left, FirePriorityCandidate right) {
        FirePriorityCandidate winner;
        FirePriorityCandidate loser;
        string reason;

        var turretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        if (turretController == null) {
            winner = FirstByOriginalOrder(left, right);
            loser = ReferenceEquals(winner, left) ? right : left;
            reason = "turret angle unavailable; falling back to original solved order";
        }
        else {
            var currentAzimuth = turretController.CurrentAngle;
            var leftElevation = GunPhysicalState.Read("Left").Elevation;
            var rightElevation = GunPhysicalState.Read("Right").Elevation;

            var leftAzimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -left.Task.angel));
            var rightAzimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -right.Task.angel));
            var leftElevationDelta = Mathf.Abs(left.Task.elevation - leftElevation);
            var rightElevationDelta = Mathf.Abs(right.Task.elevation - rightElevation);
            var leftScore = leftAzimuthDelta + leftElevationDelta;
            var rightScore = rightAzimuthDelta + rightElevationDelta;

            if (Mathf.Abs(leftScore - rightScore) <= FirePriorityScoreTieTolerance) {
                winner = FirstByOriginalOrder(left, right);
                loser = ReferenceEquals(winner, left) ? right : left;
                reason =
                    $"alignment scores tied; currentAz={currentAzimuth:F1}°, " +
                    $"Left T{left.Task.targetId}={leftScore:F1}° (az {leftAzimuthDelta:F1}+el {leftElevationDelta:F1}), " +
                    $"Right T{right.Task.targetId}={rightScore:F1}° (az {rightAzimuthDelta:F1}+el {rightElevationDelta:F1}); " +
                    "keeping original solved order";
            }
            else if (leftScore < rightScore) {
                winner = left;
                loser = right;
                reason =
                    $"currentAz={currentAzimuth:F1}°, Left T{left.Task.targetId}={leftScore:F1}° " +
                    $"(az {leftAzimuthDelta:F1}+el {leftElevationDelta:F1}) < Right T{right.Task.targetId}={rightScore:F1}° " +
                    $"(az {rightAzimuthDelta:F1}+el {rightElevationDelta:F1})";
            }
            else {
                winner = right;
                loser = left;
                reason =
                    $"currentAz={currentAzimuth:F1}°, Right T{right.Task.targetId}={rightScore:F1}° " +
                    $"(az {rightAzimuthDelta:F1}+el {rightElevationDelta:F1}) < Left T{left.Task.targetId}={leftScore:F1}° " +
                    $"(az {leftAzimuthDelta:F1}+el {leftElevationDelta:F1})";
            }
        }

        SetFirePriority(winner.Task, loser.Task, reason);
    }

    private static FirePriorityCandidate FirstByOriginalOrder(
        FirePriorityCandidate left,
        FirePriorityCandidate right) {
        if (left.SolvedAt < right.SolvedAt)
            return left;
        if (right.SolvedAt < left.SolvedAt)
            return right;
        return left;
    }

    private void SetFirePriority(ArtilleryTask winner, ArtilleryTask? second, string reason) {
        CancelFirePriorityArbitration();
        _firePriorityWinner = winner;
        _firePrioritySecond = second != null && !ReferenceEquals(second, winner) ? second : null;

        var secondText = _firePrioritySecond == null ? "none" : $"T{_firePrioritySecond.targetId}";
        MelonLogger.Msg(
            $"[FCS] Fire priority: T{winner.targetId} first, second={secondText}; {reason}");
    }

    private IEnumerator WaitForFirePriority(ArtilleryTask task, TurretReservation res) {
        // This waiter is intentionally passive. Priority calculation is event-triggered by valid ballistic
        // solutions plus synchronized gun states; waiting tasks never recalculate or poll scoring every frame.
        while (!res.Canceled && !ReferenceEquals(_firePriorityWinner, task)) {
            if (!IsActiveTask(task)) {
                res.Canceled = true;
                yield break;
            }

            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return null;
        }
    }

    private void ReleaseFirePriority(ArtilleryTask task) {
        var removedCandidate = false;
        if (_leftFireCandidate != null && ReferenceEquals(_leftFireCandidate.Task, task)) {
            _leftFireCandidate = null;
            removedCandidate = true;
        }
        if (_rightFireCandidate != null && ReferenceEquals(_rightFireCandidate.Task, task)) {
            _rightFireCandidate = null;
            removedCandidate = true;
        }

        if (ReferenceEquals(_firePrioritySecond, task))
            _firePrioritySecond = null;

        if (ReferenceEquals(_firePriorityWinner, task)) {
            _firePriorityWinner = null;
            var next = _firePrioritySecond;
            _firePrioritySecond = null;
            CancelFirePriorityArbitration();

            if (next != null && IsActiveTask(next)) {
                _firePriorityWinner = next;
                MelonLogger.Msg($"[FCS] Fire priority: promoting T{next.targetId} after previous lane owner released");
                return;
            }
        }

        if (_firePriorityWinner == null && removedCandidate) {
            CancelFirePriorityArbitration();
            TryStartFirePriorityArbitration();
        }
    }

    private void ClearSlotWithoutDispatch(LeftRight leftRight) {
        ArtilleryTask? releasedTask;
        if (leftRight == LeftRight.Left) {
            releasedTask = LeftTask;
            LeftGun.ReleaseElevationOverride();
            LeftTask = null;
        }
        else {
            releasedTask = RightTask;
            RightGun.ReleaseElevationOverride();
            RightTask = null;
        }

        if (releasedTask != null)
            ReleaseFirePriority(releasedTask);
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
        }

        // Register one durable solution. State-gated arbitration is triggered from this event only; it is not
        // recalculated from Update(). If both guns are still in the preparation band, a short one-shot buffer
        // gives the other active task a chance to finish its physical calculator pass.
        RegisterBallisticSolution(leftRight, task);
        _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));

        if (mode != GunTaskMode.ReuseLoadedRound) {
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
            ReleaseFirePriority(task);
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

    private sealed class FirePriorityCandidate {
        public LeftRight Side { get; }
        public ArtilleryTask Task { get; }
        public float SolvedAt { get; }

        public FirePriorityCandidate(LeftRight side, ArtilleryTask task, float solvedAt) {
            Side = side;
            Task = task;
            SolvedAt = solvedAt;
        }
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
        yield return WaitForFirePriority(task, res);
        if (res.Canceled)
            yield break;

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
            ReleaseFirePriority(task);
            yield break;
        }

        res.Ready = true;
        if (res.Canceled) {
            ReleaseTurretOnce(res);
            ReleaseFirePriority(task);
        }
    }

    private void ReleaseTurretOnce(TurretReservation res) {
        if (res.Acquired && !res.Released) {
            res.Released = true;
            _turretLock.Release();
        }
    }
}
