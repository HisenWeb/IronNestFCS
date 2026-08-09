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
    public string FirePriorityStatusText => _firePriorityStatusText;
    public string FirePriorityLeftDetail => _firePriorityLeftDetail;
    public string FirePriorityRightDetail => _firePriorityRightDetail;

    private readonly CoroutineLock _deskLock = new();
    private readonly CoroutineLock _turretLock = new();
    private readonly List<object> _runningCoroutines = new();

    private float _leftRecoveryStartedAt = -1f;
    private float _rightRecoveryStartedAt = -1f;
    private bool _leftRecoveryTimeoutLogged;
    private bool _rightRecoveryTimeoutLogged;

    private FirePriorityCandidate? _leftFireCandidate;
    private FirePriorityCandidate? _rightFireCandidate;
    private FirePrioritySession? _firePrioritySession;
    private ArtilleryTask? _firePriorityWinner;
    private ArtilleryTask? _firePrioritySecond;
    private ArtilleryTask? _fireLaneCommittedTask;
    private bool _firePriorityWinnerProvisional;
    private int _firePriorityGeneration;
    private string _firePriorityStatusText = "首发仲裁：未触发";
    private string _firePriorityLeftDetail = "";
    private string _firePriorityRightDetail = "";
    private string _firePriorityOrderText = "";

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
        _firePriorityGeneration++;
        _leftFireCandidate = null;
        _rightFireCandidate = null;
        _firePrioritySession = null;
        _firePriorityWinner = null;
        _firePrioritySecond = null;
        _fireLaneCommittedTask = null;
        _firePriorityWinnerProvisional = false;
        _firePriorityStatusText = "首发仲裁：未触发（已重置）";
        _firePriorityLeftDetail = "";
        _firePriorityRightDetail = "";
        _firePriorityOrderText = "";
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
                OnTaskAssignedForFirePriority();
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

    private ArtilleryTask? GetActiveTask(LeftRight side) {
        return side == LeftRight.Left ? LeftTask : RightTask;
    }

    private static LeftRight OtherSide(LeftRight side) {
        return side == LeftRight.Left ? LeftRight.Right : LeftRight.Left;
    }

    private bool TryGetTaskSide(ArtilleryTask task, out LeftRight side) {
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

    private FirePriorityCandidate? GetCurrentCandidate(LeftRight side) {
        var candidate = side == LeftRight.Left ? _leftFireCandidate : _rightFireCandidate;
        var active = GetActiveTask(side);
        return candidate != null
               && candidate.Generation == _firePriorityGeneration
               && ReferenceEquals(candidate.Task, active)
            ? candidate
            : null;
    }

    private FirePriorityCandidate? GetCandidateForTask(ArtilleryTask task) {
        if (!TryGetTaskSide(task, out var side))
            return null;
        return GetCurrentCandidate(side);
    }

    private FirePriorityGunPhase GetFirePriorityGunPhase(LeftRight side, ArtilleryTask task) {
        if (!ReferenceEquals(GetActiveTask(side), task))
            return FirePriorityGunPhase.Unavailable;

        if (ReferenceEquals(_fireLaneCommittedTask, task))
            return FirePriorityGunPhase.FireCommitted;

        if (task.progress == Progress.BackToIdle)
            return FirePriorityGunPhase.PostShotRecovery;

        if (task.progress == Progress.Finished || task.progress == Progress.Failed)
            return FirePriorityGunPhase.Unavailable;

        var physical = GunPhysicalState.Read(side == LeftRight.Left ? "Left" : "Right");
        if (physical.Kind == GunPhysicalStateKind.PostShotRecovery)
            return FirePriorityGunPhase.PostShotRecovery;

        if (physical.Kind == GunPhysicalStateKind.Unbound
            || physical.Kind == GunPhysicalStateKind.Unknown)
            return FirePriorityGunPhase.Unavailable;

        // An assigned Pending task, calculation/loading/aiming, and WaitingForFire all remain in the same
        // pre-commit band. WaitingForFire only means local preparation is complete; the order is not committed
        // until this task actually acquires the shared turret lock.
        return FirePriorityGunPhase.Preparation;
    }

    private static string PhaseName(FirePriorityGunPhase phase) {
        return phase switch {
            FirePriorityGunPhase.Preparation => "准备态",
            FirePriorityGunPhase.FireCommitted => "已取得共享击发权",
            FirePriorityGunPhase.PostShotRecovery => "击发后复位",
            _ => "不可用",
        };
    }

    private bool CanArbitrateCurrentTasks() {
        if (LeftTask == null || RightTask == null || _fireLaneCommittedTask != null)
            return false;

        return GetFirePriorityGunPhase(LeftRight.Left, LeftTask) == FirePriorityGunPhase.Preparation
               && GetFirePriorityGunPhase(LeftRight.Right, RightTask) == FirePriorityGunPhase.Preparation;
    }

    private bool SessionMatchesCurrentTasks(FirePrioritySession session) {
        return session.Generation == _firePriorityGeneration
               && ReferenceEquals(session.LeftTask, LeftTask)
               && ReferenceEquals(session.RightTask, RightTask);
    }

    private void ClearArbitrationDisplayForNewSession() {
        _firePriorityLeftDetail = "";
        _firePriorityRightDetail = "";
        _firePriorityOrderText = "";
    }

    private void UpdateArbitrationWaitingStatus() {
        if (_firePrioritySession == null)
            return;

        var left = GetCurrentCandidate(LeftRight.Left);
        var right = GetCurrentCandidate(LeftRight.Right);
        if (left == null && right != null)
            _firePriorityStatusText = "首发仲裁：等待左炮解算";
        else if (left != null && right == null)
            _firePriorityStatusText = "首发仲裁：等待右炮解算";
        else
            _firePriorityStatusText = "首发仲裁：等待双炮解算";
    }

    private void OpenFirePrioritySession(string reason) {
        if (LeftTask == null || RightTask == null || !CanArbitrateCurrentTasks())
            return;

        _firePrioritySession = new FirePrioritySession(_firePriorityGeneration, LeftTask, RightTask);
        _firePriorityWinner = null;
        _firePrioritySecond = null;
        _firePriorityWinnerProvisional = false;
        ClearArbitrationDisplayForNewSession();
        UpdateArbitrationWaitingStatus();
        MelonLogger.Msg(
            $"[FCS] Fire arbitration session gen={_firePriorityGeneration}: Left=T{LeftTask.targetId}, " +
            $"Right=T{RightTask.targetId}; {reason}");
        TryCompleteFirePrioritySession();
    }

    private void CancelFirePrioritySession(string reason, bool updateUi = true) {
        if (_firePrioritySession == null)
            return;

        MelonLogger.Msg($"[FCS] Fire arbitration session canceled: {reason}");
        _firePrioritySession = null;
        if (updateUi) {
            _firePriorityStatusText = $"首发仲裁：已取消（{reason}）";
            _firePriorityLeftDetail = "";
            _firePriorityRightDetail = "";
            _firePriorityOrderText = "";
        }
    }

    /// <summary>
    /// Unified cleanup for an abnormal task exit. This deliberately does NOT bump the global generation:
    /// F9/Dispose use ResetFirePriorityTracking() for that. Local failures must invalidate the broken pair
    /// without killing a healthy task on the other gun. The failed task's candidate/session/order is removed,
    /// while an already committed or fixed non-provisional winner on the other gun is preserved.
    /// </summary>
    private void InvalidateFirePriorityForAbnormalTask(ArtilleryTask task, string reason) {
        var preservedWinner = _firePriorityWinner != null
                              && !ReferenceEquals(_firePriorityWinner, task)
                              && IsActiveTask(_firePriorityWinner)
                              && (!_firePriorityWinnerProvisional
                                  || ReferenceEquals(_fireLaneCommittedTask, _firePriorityWinner))
            ? _firePriorityWinner
            : null;

        if (_leftFireCandidate != null && ReferenceEquals(_leftFireCandidate.Task, task))
            _leftFireCandidate = null;
        if (_rightFireCandidate != null && ReferenceEquals(_rightFireCandidate.Task, task))
            _rightFireCandidate = null;
        if (_leftFireCandidate != null && !ReferenceEquals(_leftFireCandidate.Task, LeftTask))
            _leftFireCandidate = null;
        if (_rightFireCandidate != null && !ReferenceEquals(_rightFireCandidate.Task, RightTask))
            _rightFireCandidate = null;

        _firePrioritySession = null;
        _firePriorityWinner = preservedWinner;
        _firePrioritySecond = null;
        _firePriorityWinnerProvisional = false;
        _firePriorityLeftDetail = "";
        _firePriorityRightDetail = "";
        _firePriorityOrderText = "";

        if (preservedWinner != null) {
            if (!ReferenceEquals(_fireLaneCommittedTask, preservedWinner))
                _fireLaneCommittedTask = null;
            _firePriorityStatusText =
                $"首发仲裁：异常清理（{reason}），保持 T{preservedWinner.targetId} 优先";
        }
        else {
            if (ReferenceEquals(_fireLaneCommittedTask, task)
                || _fireLaneCommittedTask == null
                || !IsActiveTask(_fireLaneCommittedTask)) {
                _fireLaneCommittedTask = null;
            }
            _firePriorityStatusText = $"首发仲裁：已清理（{reason}）";
        }

        MelonLogger.Warning(
            $"[FCS] Fire arbitration invalidated by T{task.targetId}: {reason}; " +
            $"preservedWinner={(preservedWinner == null ? "none" : $"T{preservedWinner.targetId}")}");
    }

    private void OnTaskAssignedForFirePriority() {
        if (_fireLaneCommittedTask != null || LeftTask == null || RightTask == null)
            return;

        // Only a provisional single-gun winner may be reopened. A promoted second shot from an already-arbitrated
        // pair is fixed and must stay ahead of any freshly dispatched task, even while the other gun recovers.
        if (_firePriorityWinner != null
            && _firePriorityWinnerProvisional
            && _firePrioritySecond == null
            && GetCandidateForTask(_firePriorityWinner) != null
            && CanArbitrateCurrentTasks()) {
            var previousWinner = _firePriorityWinner;
            _firePriorityWinner = null;
            _firePriorityWinnerProvisional = false;
            MelonLogger.Msg(
                $"[FCS] T{previousWinner.targetId}: second synchronized task arrived before fire-lane commit; reopening arbitration");
            OpenFirePrioritySession("second synchronized task assigned before fire-lane commit");
            return;
        }

        if (_firePriorityWinner == null && _firePrioritySession == null) {
            var left = GetCurrentCandidate(LeftRight.Left);
            var right = GetCurrentCandidate(LeftRight.Right);
            if ((left != null || right != null) && CanArbitrateCurrentTasks())
                OpenFirePrioritySession("task assignment completed a synchronized pair");
        }
    }

    private bool RegisterBallisticSolution(LeftRight side, ArtilleryTask task, int generation) {
        if (generation != _firePriorityGeneration || !ReferenceEquals(GetActiveTask(side), task)) {
            MelonLogger.Warning(
                $"[FCS] {side} T{task.targetId}: discarded stale ballistic solution " +
                $"(solveGen={generation}, currentGen={_firePriorityGeneration}, active={ReferenceEquals(GetActiveTask(side), task)})");
            return false;
        }

        var candidate = new FirePriorityCandidate(side, task, FcsRuntimeClock.Now, generation);
        if (side == LeftRight.Left) _leftFireCandidate = candidate;
        else _rightFireCandidate = candidate;

        MelonLogger.Msg($"[FCS] {side} T{task.targetId}: ballistic solution registered for arbitration gen={generation}");

        if (ReferenceEquals(_firePriorityWinner, task) || ReferenceEquals(_firePrioritySecond, task))
            return true;

        if (_firePriorityWinner != null) {
            // If a single-gun winner has not actually committed the shared turret yet, a newly solved second task
            // still belongs to the same synchronized preparation round. Reopen Promise.all-style arbitration.
            if (_firePriorityWinnerProvisional
                && _fireLaneCommittedTask == null
                && _firePrioritySecond == null
                && CanArbitrateCurrentTasks()) {
                var previousWinner = _firePriorityWinner;
                _firePriorityWinner = null;
                _firePriorityWinnerProvisional = false;
                MelonLogger.Msg(
                    $"[FCS] {side} T{task.targetId}: second solution arrived before T{previousWinner.targetId} committed; reopening arbitration");
                OpenFirePrioritySession("second ballistic solution arrived before fire-lane commit");
                return true;
            }

            // A committed or promoted shot is never preempted. A later solution can only become the fixed second
            // shot behind it.
            if (_firePrioritySecond == null && !ReferenceEquals(_firePriorityWinner, task)) {
                _firePrioritySecond = task;
                _firePriorityWinnerProvisional = false;
                MelonLogger.Msg($"[FCS] {side} T{task.targetId}: queued behind existing fire-lane owner");
            }
            return true;
        }

        if (_firePrioritySession != null) {
            if (!SessionMatchesCurrentTasks(_firePrioritySession)) {
                CancelFirePrioritySession("任务已变化", false);
            }
            else {
                TryCompleteFirePrioritySession();
                return true;
            }
        }

        EvaluateFirePriorityTrigger();
        return true;
    }

    private void EvaluateFirePriorityTrigger() {
        if (_firePriorityWinner != null)
            return;

        if (_fireLaneCommittedTask != null) {
            if (IsActiveTask(_fireLaneCommittedTask)) {
                _firePriorityWinner = _fireLaneCommittedTask;
                _firePriorityWinnerProvisional = false;
                var other = ReferenceEquals(LeftTask, _fireLaneCommittedTask) ? RightTask : LeftTask;
                if (other != null && GetCandidateForTask(other) != null)
                    _firePrioritySecond = other;
                _firePriorityStatusText = $"首发仲裁：顺序锁定 T{_fireLaneCommittedTask.targetId}";
            }
            return;
        }

        var left = GetCurrentCandidate(LeftRight.Left);
        var right = GetCurrentCandidate(LeftRight.Right);

        if (LeftTask == null && RightTask == null)
            return;

        if (LeftTask == null) {
            if (right != null)
                SetSingleFirePriority(right.Task, "仅右炮有当前任务");
            return;
        }
        if (RightTask == null) {
            if (left != null)
                SetSingleFirePriority(left.Task, "仅左炮有当前任务");
            return;
        }

        if (CanArbitrateCurrentTasks()) {
            if (left != null || right != null)
                OpenFirePrioritySession("双炮处于同步准备态");
            return;
        }

        ResolveStateGateFallback(left, right);
    }

    private void TryCompleteFirePrioritySession() {
        var session = _firePrioritySession;
        if (session == null)
            return;

        if (!SessionMatchesCurrentTasks(session)) {
            CancelFirePrioritySession("任务已变化");
            EvaluateFirePriorityTrigger();
            return;
        }

        if (!CanArbitrateCurrentTasks()) {
            CancelFirePrioritySession("双炮状态不再同步", false);
            ResolveStateGateFallback(GetCurrentCandidate(LeftRight.Left), GetCurrentCandidate(LeftRight.Right));
            return;
        }

        var left = GetCurrentCandidate(LeftRight.Left);
        var right = GetCurrentCandidate(LeftRight.Right);
        if (left == null || right == null) {
            UpdateArbitrationWaitingStatus();
            return;
        }

        ResolveFirePriorityPair(left, right);
    }

    private void ResolveStateGateFallback(FirePriorityCandidate? left, FirePriorityCandidate? right) {
        var leftPhase = LeftTask == null
            ? FirePriorityGunPhase.Unavailable
            : GetFirePriorityGunPhase(LeftRight.Left, LeftTask);
        var rightPhase = RightTask == null
            ? FirePriorityGunPhase.Unavailable
            : GetFirePriorityGunPhase(LeftRight.Right, RightTask);

        if (leftPhase == FirePriorityGunPhase.FireCommitted && LeftTask != null) {
            _firePriorityWinner = LeftTask;
            _firePriorityWinnerProvisional = false;
            if (right != null && rightPhase == FirePriorityGunPhase.Preparation)
                _firePrioritySecond = right.Task;
            _firePriorityStatusText = $"首发仲裁：顺序锁定 T{LeftTask.targetId}";
            return;
        }
        if (rightPhase == FirePriorityGunPhase.FireCommitted && RightTask != null) {
            _firePriorityWinner = RightTask;
            _firePriorityWinnerProvisional = false;
            if (left != null && leftPhase == FirePriorityGunPhase.Preparation)
                _firePrioritySecond = left.Task;
            _firePriorityStatusText = $"首发仲裁：顺序锁定 T{RightTask.targetId}";
            return;
        }

        var leftEligible = left != null && leftPhase == FirePriorityGunPhase.Preparation;
        var rightEligible = right != null && rightPhase == FirePriorityGunPhase.Preparation;

        if (leftEligible && !rightEligible) {
            SetSingleFirePriority(left!.Task, $"右炮为{PhaseName(rightPhase)}");
            return;
        }
        if (rightEligible && !leftEligible) {
            SetSingleFirePriority(right!.Task, $"左炮为{PhaseName(leftPhase)}");
            return;
        }

        if (leftEligible && rightEligible) {
            OpenFirePrioritySession("状态门恢复为同步准备态");
            return;
        }

        _firePriorityStatusText =
            $"首发仲裁：未触发（左炮{PhaseName(leftPhase)} / 右炮{PhaseName(rightPhase)}）";
        _firePriorityLeftDetail = "";
        _firePriorityRightDetail = "";
    }

    private void SetSingleFirePriority(ArtilleryTask task, string reason) {
        _firePrioritySession = null;
        _firePriorityWinner = task;
        _firePrioritySecond = null;
        _firePriorityWinnerProvisional = true;
        _firePriorityOrderText = "";
        _firePriorityLeftDetail = "";
        _firePriorityRightDetail = "";
        _firePriorityStatusText = $"首发仲裁：未触发（{reason}，T{task.targetId}优先）";
        MelonLogger.Msg($"[FCS] Fire priority: T{task.targetId} first; {reason}");
    }

    private void SetPairFirePriority(
        FirePriorityCandidate winner,
        FirePriorityCandidate loser,
        string reason) {
        _firePrioritySession = null;
        _firePriorityWinner = winner.Task;
        _firePrioritySecond = loser.Task;
        _firePriorityWinnerProvisional = false;
        _firePriorityOrderText = $"T{winner.Task.targetId} → T{loser.Task.targetId}";
        _firePriorityStatusText = $"首发仲裁：已完成 {_firePriorityOrderText}";
        MelonLogger.Msg(
            $"[FCS] Fire priority: T{winner.Task.targetId} first, second=T{loser.Task.targetId}; {reason}");
    }

    private void ResolveFirePriorityPair(FirePriorityCandidate left, FirePriorityCandidate right) {
        if (!CanArbitrateCurrentTasks()) {
            ResolveStateGateFallback(left, right);
            return;
        }

        var turretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        if (turretController == null) {
            var winnerFallback = FirstByOriginalOrder(left, right);
            var loserFallback = ReferenceEquals(winnerFallback, left) ? right : left;
            _firePriorityLeftDetail = $"左T{left.Task.targetId}：已解算（炮塔方位不可用）";
            _firePriorityRightDetail = $"右T{right.Task.targetId}：已解算（炮塔方位不可用）";
            SetPairFirePriority(
                winnerFallback,
                loserFallback,
                "turret angle unavailable; keeping original solved order");
            return;
        }

        var currentAzimuth = turretController.CurrentAngle;
        var leftElevation = GunPhysicalState.Read("Left").Elevation;
        var rightElevation = GunPhysicalState.Read("Right").Elevation;

        var leftAzimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -left.Task.angel));
        var rightAzimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -right.Task.angel));
        var leftElevationDelta = Mathf.Abs(left.Task.elevation - leftElevation);
        var rightElevationDelta = Mathf.Abs(right.Task.elevation - rightElevation);

        // Measured release-build slew rates are approximately AZ=4 deg/s and EL=2 deg/s.
        // Use azimuth degrees as the common time-equivalent unit: 1 degree of elevation costs the
        // same time as 2 degrees of azimuth. Since both axes move in parallel, readiness is gated
        // by the slower remaining axis rather than by the sum of both movements.
        var leftElevationEquivalent = leftElevationDelta * 2f;
        var rightElevationEquivalent = rightElevationDelta * 2f;
        var leftScore = Mathf.Max(leftAzimuthDelta, leftElevationEquivalent);
        var rightScore = Mathf.Max(rightAzimuthDelta, rightElevationEquivalent);

        _firePriorityLeftDetail =
            $"左T{left.Task.targetId}：{leftScore:F1}（方{leftAzimuthDelta:F1} / 仰{leftElevationDelta:F1}×2={leftElevationEquivalent:F1}）";
        _firePriorityRightDetail =
            $"右T{right.Task.targetId}：{rightScore:F1}（方{rightAzimuthDelta:F1} / 仰{rightElevationDelta:F1}×2={rightElevationEquivalent:F1}）";

        FirePriorityCandidate winner;
        FirePriorityCandidate loser;
        string reason;

        if (Mathf.Abs(leftScore - rightScore) <= FirePriorityScoreTieTolerance) {
            winner = FirstByOriginalOrder(left, right);
            loser = ReferenceEquals(winner, left) ? right : left;
            reason =
                $"alignment scores tied; currentAz={currentAzimuth:F1}°, " +
                $"Left T{left.Task.targetId}={leftScore:F1}°, Right T{right.Task.targetId}={rightScore:F1}°; " +
                "keeping original solved order";
        }
        else if (leftScore < rightScore) {
            winner = left;
            loser = right;
            reason =
                $"currentAz={currentAzimuth:F1}°, Left T{left.Task.targetId}={leftScore:F1}° " +
                $"(az {leftAzimuthDelta:F1}, el {leftElevationDelta:F1}x2={leftElevationEquivalent:F1}) < Right T{right.Task.targetId}={rightScore:F1} " +
                $"(az {rightAzimuthDelta:F1}, el {rightElevationDelta:F1}x2={rightElevationEquivalent:F1})";
        }
        else {
            winner = right;
            loser = left;
            reason =
                $"currentAz={currentAzimuth:F1}°, Right T{right.Task.targetId}={rightScore:F1}° " +
                $"(az {rightAzimuthDelta:F1}, el {rightElevationDelta:F1}x2={rightElevationEquivalent:F1}) < Left T{left.Task.targetId}={leftScore:F1} " +
                $"(az {leftAzimuthDelta:F1}, el {leftElevationDelta:F1}x2={leftElevationEquivalent:F1})";
        }

        SetPairFirePriority(winner, loser, reason);
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

    private bool CommitFireLane(ArtilleryTask task, int generation) {
        if (generation != _firePriorityGeneration
            || !ReferenceEquals(_firePriorityWinner, task)
            || !IsActiveTask(task))
            return false;

        _fireLaneCommittedTask = task;
        _firePriorityWinnerProvisional = false;
        _firePriorityStatusText = !string.IsNullOrEmpty(_firePriorityOrderText)
            ? $"首发仲裁：顺序锁定 {_firePriorityOrderText}"
            : $"首发仲裁：T{task.targetId} 已取得共享击发权";
        MelonLogger.Msg($"[FCS] T{task.targetId}: shared fire lane committed");
        return true;
    }

    private bool CanEnterTurretQueue(ArtilleryTask task) {
        if (ReferenceEquals(_firePriorityWinner, task))
            return true;

        // Once a pair has been resolved and First has actually committed the shared fire lane, let the fixed
        // Second enter CoroutineLock.Acquire() immediately. It waits behind First while that lock is held,
        // reproducing the pre-decoupling reservation pipeline without allowing Second to rotate out of order.
        return ReferenceEquals(_firePrioritySecond, task)
               && _firePriorityWinner != null
               && ReferenceEquals(_fireLaneCommittedTask, _firePriorityWinner)
               && IsActiveTask(_firePriorityWinner);
    }

    private IEnumerator WaitForTurretQueueEligibility(ArtilleryTask task, TurretReservation res) {
        // Event/state gate only: no scoring timeout. A resolved First may enter immediately; its fixed Second may
        // prequeue only after First owns the lane. Reset/generation changes still cancel stale reservations.
        while (!res.Canceled
               && res.Generation == _firePriorityGeneration
               && IsActiveTask(task)
               && !CanEnterTurretQueue(task)) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return null;
        }

        if (res.Generation != _firePriorityGeneration || !IsActiveTask(task))
            res.Canceled = true;
    }

    private void ReleaseFirePriorityAfterSuccessfulShot(ArtilleryTask task) {
        var sessionContainedTask = _firePrioritySession != null
                                   && (ReferenceEquals(_firePrioritySession.LeftTask, task)
                                       || ReferenceEquals(_firePrioritySession.RightTask, task));
        if (sessionContainedTask)
            CancelFirePrioritySession("任务被取消或重新分配", false);

        if (_leftFireCandidate != null && ReferenceEquals(_leftFireCandidate.Task, task))
            _leftFireCandidate = null;
        if (_rightFireCandidate != null && ReferenceEquals(_rightFireCandidate.Task, task))
            _rightFireCandidate = null;

        if (ReferenceEquals(_firePrioritySecond, task))
            _firePrioritySecond = null;

        if (ReferenceEquals(_firePriorityWinner, task)) {
            var next = _firePrioritySecond;
            _firePriorityWinner = null;
            _firePrioritySecond = null;
            _firePriorityWinnerProvisional = false;
            if (ReferenceEquals(_fireLaneCommittedTask, task))
                _fireLaneCommittedTask = null;

            if (next != null
                && IsActiveTask(next)
                && GetCandidateForTask(next) != null
                && TryGetTaskSide(next, out var nextSide)
                && GetFirePriorityGunPhase(nextSide, next) == FirePriorityGunPhase.Preparation) {
                _firePriorityWinner = next;
                _firePriorityWinnerProvisional = false;
                _firePriorityStatusText = !string.IsNullOrEmpty(_firePriorityOrderText)
                    ? $"首发仲裁：第二炮优先 T{next.targetId}（{_firePriorityOrderText}）"
                    : $"首发仲裁：T{next.targetId} 优先";
                MelonLogger.Msg($"[FCS] Fire priority: promoting T{next.targetId} after previous shot released");
                return;
            }
        }

        if (_firePriorityWinner == null) {
            EvaluateFirePriorityTrigger();

            if (_firePriorityWinner == null
                && _firePrioritySession == null
                && GetCurrentCandidate(LeftRight.Left) == null
                && GetCurrentCandidate(LeftRight.Right) == null
                && !string.IsNullOrEmpty(_firePriorityOrderText)) {
                _firePriorityStatusText = $"首发仲裁：本轮完成 {_firePriorityOrderText}";
            }
        }
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
        InvalidateFirePriorityForAbnormalTask(task, $"任务重分类：{reason}");
        ClearSlotWithoutDispatch(leftRight);
        EvaluateFirePriorityTrigger();
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
        InvalidateFirePriorityForAbnormalTask(task, $"改派另一门炮：{reason}");
        ClearSlotWithoutDispatch(leftRight);
        EvaluateFirePriorityTrigger();
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
        InvalidateFirePriorityForAbnormalTask(task, $"任务失败：{reason}");
        ClearSlotWithoutDispatch(leftRight);
        EvaluateFirePriorityTrigger();
        MelonLogger.Error($"[FCS] {leftRight} T{task.targetId} failed: {reason}");
        RecordTaskResult(task);
        TryDispatch();
    }

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task, GunTaskMode mode) {
        yield return FcsRuntimeClock.WaitUntilFocused();

        var taskGeneration = _firePriorityGeneration;
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;
        var sideName = leftRight == LeftRight.Left ? "Left" : "Right";
        var turret = new TurretReservation(task, taskGeneration);

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

        if (taskGeneration != _firePriorityGeneration || !ReferenceEquals(GetActiveTask(leftRight), task)) {
            MelonLogger.Warning(
                $"[FCS] {leftRight} T{task.targetId}: task generation changed during ballistic solve; discarding stale routine");
            yield break;
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

        // Read-only preparation timing probe. Start exactly where the current arbitration candidate is
        // registered, so the timings measure work that remains *after* the decision point rather than earlier
        // calculator/purchase work. These values are diagnostics only and never feed scheduling.
        var prepProbeStartedAt = FcsRuntimeClock.Now;
        var prepProbeLoadedReadyAt = -1f;
        var prepProbeElevationStartedAt = -1f;
        var prepProbeStateAtArbitration = GunPhysicalState.Read(sideName);
        var prepProbeTurret = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        var prepProbeCurrentAzimuth = prepProbeTurret?.CurrentAngle ?? 0f;
        var prepProbeAzimuthDelta = prepProbeTurret == null
            ? -1f
            : Mathf.Abs(Mathf.DeltaAngle(prepProbeCurrentAzimuth, -task.angel));
        var prepProbeElevationDelta = Mathf.Abs(task.elevation - prepProbeStateAtArbitration.Elevation);
        MelonLogger.Msg(
            $"[FCS PrepProbe] {leftRight} T{task.targetId} arbitration-start: mode={mode}, " +
            $"physical={prepProbeStateAtArbitration.Summary()}, " +
            $"azDelta={(prepProbeAzimuthDelta < 0f ? "-" : prepProbeAzimuthDelta.ToString("F1"))}°, " +
            $"el={prepProbeStateAtArbitration.Elevation:F1}°->{task.elevation:F1}° " +
            $"(delta={prepProbeElevationDelta:F1}°, x2={prepProbeElevationDelta * 2f:F1})");

        // Promise.all-like synchronization: valid solutions are registered once. If both guns are in the
        // preparation band, the first solution waits for the second real result; there is no artificial timer.
        // A reset changes taskGeneration, so a late pre-reset result can never join the new arbitration session.
        if (!RegisterBallisticSolution(leftRight, task, taskGeneration))
            yield break;
        _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));

        if (mode == GunTaskMode.ReuseLoadedRound) {
            prepProbeLoadedReadyAt = FcsRuntimeClock.Now;
            MelonLogger.Msg(
                $"[FCS PrepProbe] {leftRight} T{task.targetId} loaded-ready: mode={mode}, " +
                $"after={prepProbeLoadedReadyAt - prepProbeStartedAt:F2}s (already loaded at arbitration)");
        }

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

            prepProbeLoadedReadyAt = FcsRuntimeClock.Now;
            MelonLogger.Msg(
                $"[FCS PrepProbe] {leftRight} T{task.targetId} loaded-ready: mode={mode}, " +
                $"after={prepProbeLoadedReadyAt - prepProbeStartedAt:F2}s");
        }

        task.progress = Progress.Aiming;
        prepProbeElevationStartedAt = FcsRuntimeClock.Now;
        var prepProbeElevationStart = GunPhysicalState.Read(sideName).Elevation;
        MelonLogger.Msg(
            $"[FCS PrepProbe] {leftRight} T{task.targetId} elevation-start: mode={mode}, " +
            $"after={prepProbeElevationStartedAt - prepProbeStartedAt:F2}s, " +
            $"current={prepProbeElevationStart:F1}°, target={elevation:F1}°, " +
            $"delta={Mathf.Abs(elevation - prepProbeElevationStart):F1}°");
        yield return gunSys.SetElevation(elevation, ElevationTimeoutSeconds);
        if (!gunSys.LastElevationSucceeded) {
            AbortTask(leftRight, task, turret, $"elevation did not reach {elevation:F1}°");
            yield break;
        }

        var prepProbeLocalReadyAt = FcsRuntimeClock.Now;
        var prepProbeLoadSeconds = prepProbeLoadedReadyAt >= 0f
            ? prepProbeLoadedReadyAt - prepProbeStartedAt
            : -1f;
        var prepProbeAfterLoadSeconds = prepProbeLoadedReadyAt >= 0f
            ? prepProbeLocalReadyAt - prepProbeLoadedReadyAt
            : -1f;
        var prepProbeElevationMoveSeconds = prepProbeElevationStartedAt >= 0f
            ? prepProbeLocalReadyAt - prepProbeElevationStartedAt
            : -1f;
        MelonLogger.Msg(
            $"[FCS PrepProbe] {leftRight} T{task.targetId} local-ready: mode={mode}, " +
            $"total={prepProbeLocalReadyAt - prepProbeStartedAt:F2}s, " +
            $"toLoaded={(prepProbeLoadSeconds < 0f ? "-" : prepProbeLoadSeconds.ToString("F2"))}s, " +
            $"loadedToReady={(prepProbeAfterLoadSeconds < 0f ? "-" : prepProbeAfterLoadSeconds.ToString("F2"))}s, " +
            $"elevationMove={(prepProbeElevationMoveSeconds < 0f ? "-" : prepProbeElevationMoveSeconds.ToString("F2"))}s");

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

            // Promote the fixed Second before releasing the shared turret lock. A prequeued Second therefore
            // observes itself as the winner as soon as Acquire() completes, with no post-shot scheduling gap.
            if (gunSys.LastFireObserved)
                ReleaseFirePriorityAfterSuccessfulShot(task);
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

    private sealed class FirePriorityCandidate {
        public LeftRight Side { get; }
        public ArtilleryTask Task { get; }
        public float SolvedAt { get; }
        public int Generation { get; }

        public FirePriorityCandidate(LeftRight side, ArtilleryTask task, float solvedAt, int generation) {
            Side = side;
            Task = task;
            SolvedAt = solvedAt;
            Generation = generation;
        }
    }

    private sealed class FirePrioritySession {
        public int Generation { get; }
        public ArtilleryTask LeftTask { get; }
        public ArtilleryTask RightTask { get; }

        public FirePrioritySession(int generation, ArtilleryTask leftTask, ArtilleryTask rightTask) {
            Generation = generation;
            LeftTask = leftTask;
            RightTask = rightTask;
        }
    }

    private sealed class TurretReservation {
        public ArtilleryTask Task { get; }
        public int Generation { get; }
        public bool Acquired;
        public bool Ready;
        public bool Failed;
        public bool Canceled;
        public bool Released;
        public string FailureReason = "";

        public TurretReservation(ArtilleryTask task, int generation) {
            Task = task;
            Generation = generation;
        }
    }

    private IEnumerator ReserveTurretAndRotate(ArtilleryTask task, TurretReservation res) {
        while (!res.Canceled) {
            yield return WaitForTurretQueueEligibility(task, res);
            if (res.Canceled)
                yield break;

            // First and the already-fixed Second can both reach this Acquire. First gets the free lock; Second
            // reaches it only after First has committed, so it waits behind the held lock and is ready to take over
            // immediately after a confirmed shot releases it.
            res.Released = false;
            res.Acquired = false;
            yield return _turretLock.Acquire(
                () => res.Canceled
                      || res.Generation != _firePriorityGeneration
                      || !IsActiveTask(task),
                () => res.Acquired = true);

            // Acquire may now complete by cancellation without taking the lock. Do not let a stale queued
            // reservation wake after F9/task invalidation and briefly consume the shared turret lane.
            if (!res.Acquired) {
                res.Canceled = true;
                yield break;
            }

            yield return FcsRuntimeClock.WaitUntilFocused();

            if (res.Canceled
                || res.Generation != _firePriorityGeneration
                || !IsActiveTask(task)) {
                res.Canceled = true;
                ReleaseTurretOnce(res);
                yield break;
            }

            if (!ReferenceEquals(_firePriorityWinner, task)) {
                ReleaseTurretOnce(res);
                yield return null;
                continue;
            }

            if (!CommitFireLane(task, res.Generation)) {
                ReleaseTurretOnce(res);
                yield return null;
                continue;
            }

            yield return Turret.SetRotation(
                task.angel,
                TurretRotationTimeoutSeconds,
                () => res.Canceled
                      || res.Generation != _firePriorityGeneration
                      || !IsActiveTask(task)
                      || !ReferenceEquals(_firePriorityWinner, task));
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (res.Canceled
                || res.Generation != _firePriorityGeneration
                || !IsActiveTask(task)
                || !ReferenceEquals(_firePriorityWinner, task)) {
                ReleaseTurretOnce(res);
                if (res.Canceled || res.Generation != _firePriorityGeneration || !IsActiveTask(task))
                    yield break;
                yield return null;
                continue;
            }

            if (!Turret.LastRotationSucceeded) {
                res.Failed = true;
                res.FailureReason = $"turret could not reach {task.angel:F1}°";
                ReleaseTurretOnce(res);
                // The owning task routine will observe res.Failed and run the unified abnormal cleanup.
                yield break;
            }

            res.Ready = true;
            yield break;
        }
    }

    private void ReleaseTurretOnce(TurretReservation res) {
        if (res.Acquired && !res.Released) {
            res.Released = true;
            res.Acquired = false;
            if (ReferenceEquals(_fireLaneCommittedTask, res.Task))
                _fireLaneCommittedTask = null;
            _turretLock.Release();
        }
    }
}
