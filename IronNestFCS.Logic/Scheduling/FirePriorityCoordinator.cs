using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Owns synchronized dual-gun fire-priority arbitration and the provisional-to-hard-commit state machine.
/// It does not own physical turret locking or trigger-console interactions.
/// </summary>
internal sealed class FirePriorityCoordinator {
    private readonly FSC _fcs;

    private FirePriorityCandidate? _leftCandidate;
    private FirePriorityCandidate? _rightCandidate;
    private FirePrioritySession? _session;
    private ArtilleryTask? _winner;
    private ArtilleryTask? _second;
    private ArtilleryTask? _turretLaneOwnerTask;
    private ArtilleryTask? _fireLaneCommittedTask;
    private bool _winnerProvisional;
    private int _generation;
    private string _statusText = "首发仲裁：未触发";
    private string _leftDetail = "";
    private string _rightDetail = "";
    private string _orderText = "";

    public FirePriorityCoordinator(FSC fcs) {
        _fcs = fcs;
    }

    public int Generation => _generation;
    public string StatusText => _statusText;
    public string LeftDetail => _leftDetail;
    public string RightDetail => _rightDetail;

    private TaskDispatcher Dispatcher => _fcs.Dispatcher;
    private ArtilleryTask? LeftTask => Dispatcher.LeftTask;
    private ArtilleryTask? RightTask => Dispatcher.RightTask;

    public void Reset() {
        _generation++;
        _leftCandidate = null;
        _rightCandidate = null;
        _session = null;
        _winner = null;
        _second = null;
        _turretLaneOwnerTask = null;
        _fireLaneCommittedTask = null;
        _winnerProvisional = false;
        _statusText = "首发仲裁：未触发（已重置）";
        _leftDetail = "";
        _rightDetail = "";
        _orderText = "";
    }

    public bool IsWinner(ArtilleryTask task) => ReferenceEquals(_winner, task);

    private FirePriorityCandidate? GetCurrentCandidate(LeftRight side) {
        var candidate = side == LeftRight.Left ? _leftCandidate : _rightCandidate;
        var active = Dispatcher.GetActiveTask(side);
        return candidate != null
               && candidate.Generation == _generation
               && ReferenceEquals(candidate.Task, active)
            ? candidate
            : null;
    }

    private FirePriorityCandidate? GetCandidateForTask(ArtilleryTask task) {
        if (!Dispatcher.TryGetTaskSide(task, out var side))
            return null;
        return GetCurrentCandidate(side);
    }

    private FirePriorityGunPhase GetGunPhase(LeftRight side, ArtilleryTask task) {
        if (!ReferenceEquals(Dispatcher.GetActiveTask(side), task))
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

        // Assigned Pending, calculation/loading/aiming and WaitingForFire remain pre-commit.
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

        return GetGunPhase(LeftRight.Left, LeftTask) == FirePriorityGunPhase.Preparation
               && GetGunPhase(LeftRight.Right, RightTask) == FirePriorityGunPhase.Preparation;
    }

    private bool SessionMatchesCurrentTasks(FirePrioritySession session) {
        return session.Generation == _generation
               && ReferenceEquals(session.LeftTask, LeftTask)
               && ReferenceEquals(session.RightTask, RightTask);
    }

    private void ClearDisplayForNewSession() {
        _leftDetail = "";
        _rightDetail = "";
        _orderText = "";
    }

    private void UpdateWaitingStatus() {
        if (_session == null)
            return;

        var left = GetCurrentCandidate(LeftRight.Left);
        var right = GetCurrentCandidate(LeftRight.Right);
        if (left == null && right != null)
            _statusText = "首发仲裁：等待左炮解算";
        else if (left != null && right == null)
            _statusText = "首发仲裁：等待右炮解算";
        else
            _statusText = "首发仲裁：等待双炮解算";
    }

    private void OpenSession(string reason) {
        if (LeftTask == null || RightTask == null || !CanArbitrateCurrentTasks())
            return;

        _session = new FirePrioritySession(_generation, LeftTask, RightTask);
        _winner = null;
        _second = null;
        _winnerProvisional = false;
        ClearDisplayForNewSession();
        UpdateWaitingStatus();
        MelonLogger.Msg(
            $"[FCS] Fire arbitration session gen={_generation}: Left=T{LeftTask.targetId}, " +
            $"Right=T{RightTask.targetId}; {reason}");
        TryCompleteSession();
    }

    private void CancelSession(string reason, bool updateUi = true) {
        if (_session == null)
            return;

        MelonLogger.Msg($"[FCS] Fire arbitration session canceled: {reason}");
        _session = null;
        if (updateUi) {
            _statusText = $"首发仲裁：已取消（{reason}）";
            _leftDetail = "";
            _rightDetail = "";
            _orderText = "";
        }
    }

    /// <summary>
    /// Local task failure invalidates only the broken pair. F9/Dispose use Reset() and generation invalidation.
    /// </summary>
    public void InvalidateForAbnormalTask(ArtilleryTask task, string reason) {
        var preservedWinner = _winner != null
                              && !ReferenceEquals(_winner, task)
                              && Dispatcher.IsActiveTask(_winner)
            ? _winner
            : null;
        var preservedWinnerProvisional = preservedWinner != null && _winnerProvisional;

        if (_leftCandidate != null && ReferenceEquals(_leftCandidate.Task, task))
            _leftCandidate = null;
        if (_rightCandidate != null && ReferenceEquals(_rightCandidate.Task, task))
            _rightCandidate = null;
        if (_leftCandidate != null && !ReferenceEquals(_leftCandidate.Task, LeftTask))
            _leftCandidate = null;
        if (_rightCandidate != null && !ReferenceEquals(_rightCandidate.Task, RightTask))
            _rightCandidate = null;

        _session = null;
        _winner = preservedWinner;
        _second = null;
        _winnerProvisional = preservedWinnerProvisional;
        _leftDetail = "";
        _rightDetail = "";
        _orderText = "";

        if (ReferenceEquals(_turretLaneOwnerTask, task)
            || (_turretLaneOwnerTask != null && !Dispatcher.IsActiveTask(_turretLaneOwnerTask))) {
            _turretLaneOwnerTask = null;
        }

        if (preservedWinner != null) {
            if (!ReferenceEquals(_fireLaneCommittedTask, preservedWinner))
                _fireLaneCommittedTask = null;
            _statusText = preservedWinnerProvisional
                ? $"首发仲裁：异常清理（{reason}），保持 T{preservedWinner.targetId} 临时优先"
                : $"首发仲裁：异常清理（{reason}），保持 T{preservedWinner.targetId} 优先";
        }
        else {
            if (ReferenceEquals(_fireLaneCommittedTask, task)
                || _fireLaneCommittedTask == null
                || !Dispatcher.IsActiveTask(_fireLaneCommittedTask)) {
                _fireLaneCommittedTask = null;
            }
            _statusText = $"首发仲裁：已清理（{reason}）";
        }

        MelonLogger.Warning(
            $"[FCS] Fire arbitration invalidated by T{task.targetId}: {reason}; " +
            $"preservedWinner={(preservedWinner == null ? "none" : $"T{preservedWinner.targetId}")}");
    }

    public void OnTaskAssigned() {
        if (_fireLaneCommittedTask != null || LeftTask == null || RightTask == null)
            return;

        // Assignment alone must not interrupt a provisional owner. Wait for a real second ballistic solution.
        if (_winner == null && _session == null) {
            var left = GetCurrentCandidate(LeftRight.Left);
            var right = GetCurrentCandidate(LeftRight.Right);
            if ((left != null || right != null) && CanArbitrateCurrentTasks())
                OpenSession("task assignment completed a synchronized pair");
        }
    }

    public bool RegisterBallisticSolution(LeftRight side, ArtilleryTask task, int generation, GunTaskMode mode) {
        if (generation != _generation || !ReferenceEquals(Dispatcher.GetActiveTask(side), task)) {
            MelonLogger.Warning(
                $"[FCS] {side} T{task.targetId}: discarded stale ballistic solution " +
                $"(solveGen={generation}, currentGen={_generation}, active={ReferenceEquals(Dispatcher.GetActiveTask(side), task)})");
            return false;
        }

        var candidate = new FirePriorityCandidate(side, task, FcsRuntimeClock.Now, generation, mode);
        if (side == LeftRight.Left) _leftCandidate = candidate;
        else _rightCandidate = candidate;

        MelonLogger.Msg($"[FCS] {side} T{task.targetId}: ballistic solution registered for arbitration gen={generation}");

        if (ReferenceEquals(_winner, task) || ReferenceEquals(_second, task))
            return true;

        if (_winner != null) {
            if (_winnerProvisional
                && _fireLaneCommittedTask == null
                && _second == null
                && CanArbitrateCurrentTasks()) {
                var previousWinner = _winner;
                _winner = null;
                _winnerProvisional = false;
                MelonLogger.Msg(
                    $"[FCS] {side} T{task.targetId}: second solution arrived before T{previousWinner.targetId} committed; reopening arbitration");
                OpenSession("second ballistic solution arrived before fire-lane commit");
                return true;
            }

            if (_second == null && !ReferenceEquals(_winner, task)) {
                _second = task;
                MelonLogger.Msg($"[FCS] {side} T{task.targetId}: queued behind current fire priority");
            }
            return true;
        }

        if (_session != null) {
            if (!SessionMatchesCurrentTasks(_session)) {
                CancelSession("任务已变化", false);
            }
            else {
                TryCompleteSession();
                return true;
            }
        }

        EvaluateTrigger();
        return true;
    }

    public void EvaluateTrigger() {
        if (_winner != null)
            return;

        if (_fireLaneCommittedTask != null) {
            if (Dispatcher.IsActiveTask(_fireLaneCommittedTask)) {
                _winner = _fireLaneCommittedTask;
                _winnerProvisional = false;
                var other = ReferenceEquals(LeftTask, _fireLaneCommittedTask) ? RightTask : LeftTask;
                if (other != null && GetCandidateForTask(other) != null)
                    _second = other;
                _statusText = $"首发仲裁：顺序锁定 T{_fireLaneCommittedTask.targetId}";
            }
            return;
        }

        var left = GetCurrentCandidate(LeftRight.Left);
        var right = GetCurrentCandidate(LeftRight.Right);

        if (LeftTask == null && RightTask == null)
            return;

        if (LeftTask == null) {
            if (right != null)
                SetSingle(right.Task, "仅右炮有当前任务");
            return;
        }
        if (RightTask == null) {
            if (left != null)
                SetSingle(left.Task, "仅左炮有当前任务");
            return;
        }

        if (CanArbitrateCurrentTasks()) {
            if (left != null || right != null)
                OpenSession("双炮处于同步准备态");
            return;
        }

        ResolveStateGateFallback(left, right);
    }

    private void TryCompleteSession() {
        var session = _session;
        if (session == null)
            return;

        if (!SessionMatchesCurrentTasks(session)) {
            CancelSession("任务已变化");
            EvaluateTrigger();
            return;
        }

        if (!CanArbitrateCurrentTasks()) {
            CancelSession("双炮状态不再同步", false);
            ResolveStateGateFallback(GetCurrentCandidate(LeftRight.Left), GetCurrentCandidate(LeftRight.Right));
            return;
        }

        var left = GetCurrentCandidate(LeftRight.Left);
        var right = GetCurrentCandidate(LeftRight.Right);
        if (left == null || right == null) {
            UpdateWaitingStatus();
            return;
        }

        ResolvePair(left, right);
    }

    private void ResolveStateGateFallback(FirePriorityCandidate? left, FirePriorityCandidate? right) {
        var leftPhase = LeftTask == null
            ? FirePriorityGunPhase.Unavailable
            : GetGunPhase(LeftRight.Left, LeftTask);
        var rightPhase = RightTask == null
            ? FirePriorityGunPhase.Unavailable
            : GetGunPhase(LeftRight.Right, RightTask);

        if (leftPhase == FirePriorityGunPhase.FireCommitted && LeftTask != null) {
            _winner = LeftTask;
            _winnerProvisional = false;
            if (right != null && rightPhase == FirePriorityGunPhase.Preparation)
                _second = right.Task;
            _statusText = $"首发仲裁：顺序锁定 T{LeftTask.targetId}";
            return;
        }
        if (rightPhase == FirePriorityGunPhase.FireCommitted && RightTask != null) {
            _winner = RightTask;
            _winnerProvisional = false;
            if (left != null && leftPhase == FirePriorityGunPhase.Preparation)
                _second = left.Task;
            _statusText = $"首发仲裁：顺序锁定 T{RightTask.targetId}";
            return;
        }

        var leftEligible = left != null && leftPhase == FirePriorityGunPhase.Preparation;
        var rightEligible = right != null && rightPhase == FirePriorityGunPhase.Preparation;

        if (leftEligible && !rightEligible) {
            SetSingle(left!.Task, $"右炮为{PhaseName(rightPhase)}");
            return;
        }
        if (rightEligible && !leftEligible) {
            SetSingle(right!.Task, $"左炮为{PhaseName(leftPhase)}");
            return;
        }

        if (leftEligible && rightEligible) {
            OpenSession("状态门恢复为同步准备态");
            return;
        }

        _statusText =
            $"首发仲裁：未触发（左炮{PhaseName(leftPhase)} / 右炮{PhaseName(rightPhase)}）";
        _leftDetail = "";
        _rightDetail = "";
    }

    private void SetSingle(ArtilleryTask task, string reason) {
        _session = null;
        _winner = task;
        _second = null;
        _winnerProvisional = true;
        _orderText = "";
        _leftDetail = "";
        _rightDetail = "";
        _statusText = $"首发仲裁：未触发（{reason}，T{task.targetId}优先）";
        MelonLogger.Msg($"[FCS] Fire priority: T{task.targetId} first; {reason}");
    }

    private void SetPair(FirePriorityCandidate winner, FirePriorityCandidate loser, string reason) {
        _session = null;
        _winner = winner.Task;
        _second = loser.Task;
        _winnerProvisional = true;
        _orderText = $"T{winner.Task.targetId} → T{loser.Task.targetId}";
        _statusText = $"首发仲裁：已完成 {_orderText}";
        MelonLogger.Msg(
            $"[FCS] Fire priority: T{winner.Task.targetId} first, second=T{loser.Task.targetId}; {reason}");
    }

    private void ResolvePair(FirePriorityCandidate left, FirePriorityCandidate right) {
        if (!CanArbitrateCurrentTasks()) {
            ResolveStateGateFallback(left, right);
            return;
        }

        var turretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        if (turretController == null) {
            var winnerFallback = FireReadyEstimator.FirstByOriginalOrder(left, right);
            var loserFallback = ReferenceEquals(winnerFallback, left) ? right : left;
            _leftDetail = $"左T{left.Task.targetId}：已解算（炮塔方位不可用）";
            _rightDetail = $"右T{right.Task.targetId}：已解算（炮塔方位不可用）";
            SetPair(winnerFallback, loserFallback, "turret angle unavailable; keeping original solved order");
            return;
        }

        var currentAzimuth = turretController.CurrentAngle;
        var leftPhysical = GunPhysicalState.Read("Left");
        var rightPhysical = GunPhysicalState.Read("Right");
        var leftEta = FireReadyEstimator.Estimate(left, leftPhysical, currentAzimuth, FcsRuntimeClock.Now);
        var rightEta = FireReadyEstimator.Estimate(right, rightPhysical, currentAzimuth, FcsRuntimeClock.Now);

        _leftDetail = FireReadyEstimator.FormatDetail("左", left, leftEta);
        _rightDetail = FireReadyEstimator.FormatDetail("右", right, rightEta);

        FirePriorityCandidate winner;
        FirePriorityCandidate loser;
        string reason;

        if (leftEta.LoadKnown && rightEta.LoadKnown) {
            if (Mathf.Abs(leftEta.TotalSeconds - rightEta.TotalSeconds) <= FireReadyEstimator.EtaTieToleranceSeconds) {
                winner = FireReadyEstimator.FirstByOriginalOrder(left, right);
                loser = ReferenceEquals(winner, left) ? right : left;
                reason =
                    $"fire-ready ETA tied; currentAz={currentAzimuth:F1}°, " +
                    $"Left T{left.Task.targetId}={leftEta.TotalSeconds:F1}s " +
                    $"(load {leftEta.LoadSeconds:F1}+el {leftEta.ElevationSeconds:F1}, az {leftEta.AzimuthSeconds:F1}), " +
                    $"Right T{right.Task.targetId}={rightEta.TotalSeconds:F1}s " +
                    $"(load {rightEta.LoadSeconds:F1}+el {rightEta.ElevationSeconds:F1}, az {rightEta.AzimuthSeconds:F1}); " +
                    "keeping original solved order";
            }
            else if (leftEta.TotalSeconds < rightEta.TotalSeconds) {
                winner = left;
                loser = right;
                reason =
                    $"ETA Left T{left.Task.targetId}={leftEta.TotalSeconds:F1}s " +
                    $"(load {leftEta.LoadSeconds:F1}+el {leftEta.ElevationSeconds:F1}, az {leftEta.AzimuthSeconds:F1}) < " +
                    $"Right T{right.Task.targetId}={rightEta.TotalSeconds:F1}s " +
                    $"(load {rightEta.LoadSeconds:F1}+el {rightEta.ElevationSeconds:F1}, az {rightEta.AzimuthSeconds:F1})";
            }
            else {
                winner = right;
                loser = left;
                reason =
                    $"ETA Right T{right.Task.targetId}={rightEta.TotalSeconds:F1}s " +
                    $"(load {rightEta.LoadSeconds:F1}+el {rightEta.ElevationSeconds:F1}, az {rightEta.AzimuthSeconds:F1}) < " +
                    $"Left T{left.Task.targetId}={leftEta.TotalSeconds:F1}s " +
                    $"(load {leftEta.LoadSeconds:F1}+el {leftEta.ElevationSeconds:F1}, az {leftEta.AzimuthSeconds:F1})";
            }
        }
        else {
            if (Mathf.Abs(leftEta.AlignmentScore - rightEta.AlignmentScore) <= FireReadyEstimator.AlignmentTieTolerance) {
                winner = FireReadyEstimator.FirstByOriginalOrder(left, right);
                loser = ReferenceEquals(winner, left) ? right : left;
            }
            else if (leftEta.AlignmentScore < rightEta.AlignmentScore) {
                winner = left;
                loser = right;
            }
            else {
                winner = right;
                loser = left;
            }

            reason =
                $"load ETA unavailable; alignment fallback: Left T{left.Task.targetId}={leftEta.AlignmentScore:F1} " +
                $"({leftEta.LoadLabel}), Right T{right.Task.targetId}={rightEta.AlignmentScore:F1} ({rightEta.LoadLabel})";
        }

        SetPair(winner, loser, reason);
    }

    public bool ClaimTurretLane(ArtilleryTask task, int generation) {
        if (generation != _generation
            || !ReferenceEquals(_winner, task)
            || !Dispatcher.IsActiveTask(task))
            return false;

        _turretLaneOwnerTask = task;
        _statusText = !string.IsNullOrEmpty(_orderText)
            ? $"首发仲裁：临时顺序 {_orderText}（T{task.targetId} 使用方位）"
            : $"首发仲裁：T{task.targetId} 临时优先（使用共享方位）";
        MelonLogger.Msg($"[FCS] T{task.targetId}: shared turret lane claimed provisionally");
        return true;
    }

    public bool CommitFireLane(ArtilleryTask task, int generation, TurretReservation reservation) {
        if (generation != _generation
            || !ReferenceEquals(_winner, task)
            || !Dispatcher.IsActiveTask(task)
            || !reservation.Acquired
            || !reservation.Ready
            || !ReferenceEquals(_turretLaneOwnerTask, task))
            return false;

        _fireLaneCommittedTask = task;
        reservation.HardCommitted = true;
        _winnerProvisional = false;
        _statusText = !string.IsNullOrEmpty(_orderText)
            ? $"首发仲裁：顺序锁定 {_orderText}"
            : $"首发仲裁：T{task.targetId} 已取得共享击发权";
        MelonLogger.Msg($"[FCS] T{task.targetId}: shared fire lane hard-committed before Review Console");
        return true;
    }

    public bool CanEnterTurretQueue(ArtilleryTask task) {
        if (ReferenceEquals(_winner, task))
            return true;

        return ReferenceEquals(_second, task)
               && _winner != null
               && ReferenceEquals(_turretLaneOwnerTask, _winner)
               && Dispatcher.IsActiveTask(_winner);
    }

    public void OnTurretReservationReleased(TurretReservation reservation) {
        if (ReferenceEquals(_turretLaneOwnerTask, reservation.Task))
            _turretLaneOwnerTask = null;
        if (ReferenceEquals(_fireLaneCommittedTask, reservation.Task))
            _fireLaneCommittedTask = null;
    }

    public void ReleaseAfterSuccessfulShot(ArtilleryTask task) {
        var sessionContainedTask = _session != null
                                   && (ReferenceEquals(_session.LeftTask, task)
                                       || ReferenceEquals(_session.RightTask, task));
        if (sessionContainedTask)
            CancelSession("任务被取消或重新分配", false);

        if (_leftCandidate != null && ReferenceEquals(_leftCandidate.Task, task))
            _leftCandidate = null;
        if (_rightCandidate != null && ReferenceEquals(_rightCandidate.Task, task))
            _rightCandidate = null;

        if (ReferenceEquals(_second, task))
            _second = null;

        if (ReferenceEquals(_winner, task)) {
            var next = _second;
            _winner = null;
            _second = null;
            _winnerProvisional = false;
            if (ReferenceEquals(_fireLaneCommittedTask, task))
                _fireLaneCommittedTask = null;

            if (next != null
                && Dispatcher.IsActiveTask(next)
                && GetCandidateForTask(next) != null
                && Dispatcher.TryGetTaskSide(next, out var nextSide)
                && GetGunPhase(nextSide, next) == FirePriorityGunPhase.Preparation) {
                _winner = next;
                _winnerProvisional = true;
                _statusText = !string.IsNullOrEmpty(_orderText)
                    ? $"首发仲裁：第二炮临时优先 T{next.targetId}（{_orderText}）"
                    : $"首发仲裁：T{next.targetId} 临时优先";
                MelonLogger.Msg($"[FCS] Fire priority: promoting T{next.targetId} provisionally after previous shot");
                return;
            }
        }

        if (_winner == null) {
            EvaluateTrigger();

            if (_winner == null
                && _session == null
                && GetCurrentCandidate(LeftRight.Left) == null
                && GetCurrentCandidate(LeftRight.Right) == null
                && !string.IsNullOrEmpty(_orderText)) {
                _statusText = $"首发仲裁：本轮完成 {_orderText}";
            }
        }
    }
}
