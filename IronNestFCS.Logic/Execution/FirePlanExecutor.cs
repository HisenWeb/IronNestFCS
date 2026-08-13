// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Scheduling;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Execution;

/// <summary>
/// Capacity-two FirePlan executor. Per-gun preparation runs independently; current/next determine the automation
/// order and arming suggestion, while actual physical gun state determines which FirePlan was really executed.
/// A FirePlan ends at observed fire; post-shot mechanical recovery remains owned by the physical loading runtime.
/// </summary>
internal sealed class FirePlanExecutor
{
    private const float ElevationTimeoutSeconds = 35f;
    private const float LoadingObservationTimeoutSeconds = 90f;
    private const float AutoFireTimeoutSeconds = 25f;
    private const float SameAzimuthToleranceDegrees = 0.09f;
    private const int FireSettlementBufferFrames = 3;
    private const float ReviewLeadTimeBeforeArmSeconds = 1.5f;

    private readonly FSC _fcs;
    private readonly Dictionary<FirePlan, object> _prepareCoroutines = new();

    private FirePlan? _leftPlan;
    private FirePlan? _rightPlan;
    private FirePlan? _current;
    private FirePlan? _next;

    // This identifies one shared physical-fire wait, not the gun that must fire. Left/right result mapping is
    // performed from physical observations after the shared trigger event.
    private FirePlan? _fireWaitOwner;
    private int _fireWaitGeneration = -1;
    private int _fireWaitSerial;
    private int _activeFireWaitSerial;
    private bool _autoFireIssuedForWait;

    public FirePlanExecutor(FSC fcs)
    {
        _fcs = fcs;
    }

    public ArtilleryTask? LeftTask => _leftPlan?.Task;
    public ArtilleryTask? RightTask => _rightPlan?.Task;

    // "Free" means a FirePlan slot is free. FirePlanner remains authoritative for whether the underlying gun's
    // current loading/recovery snapshot is physically plannable.
    public bool HasFreeGun => _leftPlan == null || _rightPlan == null;

    public FirePlan? GetPlan(LeftRight side) => side == LeftRight.Left ? _leftPlan : _rightPlan;

    public void DisposeState()
    {
        _leftPlan = null;
        _rightPlan = null;
        _current = null;
        _next = null;
        _prepareCoroutines.Clear();
        _fcs.TriggerConsole.ResetGunReadyInputs();
        ClearAllFireWait();
    }

    public bool AddPlan(FirePlan plan, out string reason)
    {
        reason = "";
        if (plan.Generation != _fcs.FirePriority.Generation)
        {
            reason = "stale FirePlan generation";
            return false;
        }

        if (GetPlan(plan.Side) != null)
        {
            reason = $"{plan.Side} already has a FirePlan";
            return false;
        }

        if (plan.Side == LeftRight.Left)
            _leftPlan = plan;
        else
            _rightPlan = plan;

        if (_current != null && !ReferenceEquals(_current, plan) && _next == null)
            _next = plan;

        MelonLogger.Msg($"[FCS Plan] executor accepted {plan.Label}");
        _prepareCoroutines[plan] = _fcs.TrackCoroutine(PrepareLocal(plan));
        EvaluateScheduling();
        return true;
    }

    public void Tick() => EvaluateScheduling();

    public void OnAutoFireEnabled()
    {
        var plan = _fireWaitOwner;
        if (plan == null || _autoFireIssuedForWait)
            return;

        if (_fireWaitGeneration != _fcs.FirePriority.Generation
            || !ReferenceEquals(_current, plan)
            || !IsActive(plan)
            || plan.Task.progress != Progress.WaitingForFire)
        {
            ClearAllFireWait();
            return;
        }

        _autoFireIssuedForWait = true;
        MelonLogger.Msg($"[FCS] AutoFire enabled while T{plan.Task.targetId} is awaiting the shared trigger; firing physical trigger");
        _fcs.TriggerConsole.Fire();
    }

    /// <summary>
    /// Two unpaired plans compare once. A compared plan is promoted without re-comparison. One unpaired plan
    /// waits only while an active planning round can still provide a partner; otherwise it single-commits.
    /// </summary>
    public void EvaluateScheduling()
    {
        if (_current != null)
            return;

        var active = ActivePlans();
        if (active.Count == 0)
            return;

        var committed = active.FirstOrDefault(p => p.Compared);
        if (committed != null)
        {
            _next = active.FirstOrDefault(p => !ReferenceEquals(p, committed));
            SetCurrent(committed, promote: true);
            return;
        }

        if (active.Count >= 2)
        {
            var a = active[0];
            var b = active[1];
            var first = _fcs.FirePriority.ComparePair(a, b);
            _next = ReferenceEquals(first, a) ? b : a;
            SetCurrent(first, promote: false);
            return;
        }

        var single = active[0];
        if (_fcs.Dispatcher.HasPendingOrPlanning)
        {
            _next = single;
            _fcs.FirePriority.MarkWaitingForPair(single);
            return;
        }

        _next = null;
        _fcs.FirePriority.CommitSingle(single, "等待队列为空");
        SetCurrent(single, promote: false);
    }

    private List<FirePlan> ActivePlans()
    {
        var result = new List<FirePlan>(2);
        if (_leftPlan != null && !_leftPlan.Failed && !_leftPlan.ShotObserved)
            result.Add(_leftPlan);
        if (_rightPlan != null && !_rightPlan.Failed && !_rightPlan.ShotObserved)
            result.Add(_rightPlan);
        return result;
    }

    private void SetCurrent(FirePlan plan, bool promote)
    {
        if (_current != null)
            return;

        _current = plan;
        if (ReferenceEquals(_next, plan))
            _next = null;

        if (promote)
            _fcs.FirePriority.PromoteCommitted(plan);

        // Azimuth has no loading dependency. Start immediately after order commit.
        // Review-button dispatch is intentionally independent and is requested later by RunShared.
        _fcs.TrackCoroutine(RunShared(plan));
    }

    private IEnumerator PrepareLocal(FirePlan plan)
    {
        yield return FcsRuntimeClock.WaitUntilFocused();
        if (!IsActive(plan))
            yield break;

        var gun = plan.Side == LeftRight.Left ? _fcs.LeftGun : _fcs.RightGun;

        // Requisition remains TaskSystem-owned. The persistent transaction is accepted only after resources
        // exist, so F9 during requisition abandons intent but never a half-owned loading transaction.
        var loadingBeforePurchase = _fcs.Loading.GetSnapshot(plan.HostSide);
        var needsPersistentLoad = !loadingBeforePurchase.HasTransaction
                                  && loadingBeforePurchase.PhysicalState != LoadingPhysicalState.LoadedReady;

        if (needsPersistentLoad)
        {
            plan.Task.progress = Progress.SelectingBullet;
            yield return _fcs.SharedResources.Requisition.Acquire();
            try
            {
                var attempts = 0;
                while (gun.RemainingCharges() < plan.Charge && attempts < 10)
                {
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    yield return _fcs.PurchaseDeck.BuyPowders();
                    attempts++;
                }

                if (gun.RemainingCharges() < plan.Charge)
                {
                    FailPlan(plan, $"powder unavailable: need {plan.Charge}, have {gun.RemainingCharges()}");
                    yield break;
                }

                if (loadingBeforePurchase.PhysicalState == LoadingPhysicalState.EmptyReady
                    && !gun.HaveBulletInCylinder(plan.Shell))
                {
                    if (!gun.HaveEmptyShellInCylinder())
                    {
                        FailPlan(plan, $"no {plan.Shell.DisplayName()} shell and cylinder has no empty slot");
                        yield break;
                    }

                    yield return _fcs.PurchaseDeck.BuyShell(plan.Shell, plan.Side);
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    if (!gun.HaveBulletInCylinder(plan.Shell))
                    {
                        FailPlan(plan, $"purchase of {plan.Shell.DisplayName()} did not reach cylinder");
                        yield break;
                    }
                }
            }
            finally
            {
                _fcs.SharedResources.Requisition.Release();
            }
        }

        if (!IsActive(plan))
            yield break;

        if (!_fcs.Loading.TryRequest(plan.LoadRequest, out var loadReason))
        {
            FailPlan(plan, $"loading request rejected: {loadReason}");
            yield break;
        }

        var loadingDeadline = FcsRuntimeClock.Now + LoadingObservationTimeoutSeconds;
        while (true)
        {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (!IsActive(plan))
                yield break;

            var snapshot = _fcs.Loading.GetSnapshot(plan.HostSide);
            if (snapshot.Matches(plan.LoadRequest))
                break;

            if (snapshot.TransactionState == LoadingTransactionState.Failed)
            {
                FailPlan(plan, string.IsNullOrWhiteSpace(snapshot.FailureReason)
                    ? "persistent loading transaction failed"
                    : snapshot.FailureReason);
                yield break;
            }

            plan.Task.progress = snapshot.TransactionState switch
            {
                LoadingTransactionState.LoadingShell => Progress.LoadingBullet,
                LoadingTransactionState.LoadingPowder => Progress.LoadingPowder,
                LoadingTransactionState.WaitingLoadedReady => Progress.WaitLoading,
                _ => Progress.WaitLoading,
            };

            if (FcsRuntimeClock.Now >= loadingDeadline)
            {
                FailPlan(plan, $"persistent loading observation timed out; physical={snapshot.PhysicalState}, tx={snapshot.TransactionState}");
                yield break;
            }

            yield return FcsRuntimeClock.WaitForSeconds(0.25f);
        }

        // Left/right elevation are independent. Start immediately at physical LoadedReady.
        plan.Task.progress = Progress.Aiming;
        yield return gun.SetElevation(plan.Elevation, ElevationTimeoutSeconds);

        // A different gun may have been physically fired while this plan was still preparing. If the physical
        // settlement consumed this plan, do not turn cancellation into a false elevation failure.
        if (!IsActive(plan))
            yield break;

        if (!gun.LastElevationSucceeded)
        {
            FailPlan(plan, $"elevation did not reach {plan.Elevation:F1}°");
            yield break;
        }

        plan.LocalReady = true;
        plan.Task.progress = Progress.WaitingForFire;
        MelonLogger.Msg($"[FCS Plan] {plan.Label}: local ready (LoadedReady + elevation)");

        // A non-current ready plan is only a follower candidate for the current shared-fire opportunity. It never
        // inherits the scheduler's _next identity; eligibility is derived fresh from the live fire-wait state.
        _fcs.TrackCoroutine(TryArmReadyFollowerDuringCurrentWait(plan));
    }

    private IEnumerator RunShared(FirePlan plan)
    {
        if (!ReferenceEquals(_current, plan) || !IsActive(plan))
            yield break;

        yield return FcsRuntimeClock.WaitUntilFocused();
        MelonLogger.Msg($"[FCS Plan] {plan.Label}: shared execution start; rotating azimuth immediately");

        yield return _fcs.Turret.SetRotation(plan.Azimuth, 45f, () =>
            plan.Generation != _fcs.FirePriority.Generation
            || plan.Failed
            || !ReferenceEquals(_current, plan)
            || !IsActive(plan));

        if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
            yield break;

        if (!_fcs.Turret.LastRotationSucceeded)
        {
            FailPlan(plan, $"turret could not reach {plan.Azimuth:F1}°");
            yield break;
        }

        plan.AzimuthReady = true;

        while (!plan.LocalReady)
        {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                yield break;
            yield return null;
        }

        var fireWaitToken = 0;
        var autoFireIssued = false;
        PhysicalFireWatch? leftWatch = null;
        PhysicalFireWatch? rightWatch = null;

        try
        {
            yield return _fcs.SharedResources.Trigger.Acquire();
            try
            {
                if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                    yield break;

                // The firing lever is shared and the player may already have manipulated either safety. Capture
                // both baselines before touching any review/arming controls so a player-triggered shot during the
                // console protocol is still reconciled from physical reality.
                leftWatch = BeginFireWatch(_fcs.LeftGun, "Left");
                rightWatch = BeginFireWatch(_fcs.RightGun, "Right");

                yield return _fcs.TriggerConsole.PrepareForNewFireSolution(plan.Side);

                // Once this gun is physically ready for the shared fire stage, publish only that fact to the
                // independent review-button controller. The controller owns physical switch convergence; the
                // executor preserves the 1.5 s visual lead before arming without waiting for button completion.
                _fcs.TriggerConsole.SetGunReady(plan.Side, true);
                yield return FcsRuntimeClock.WaitForSeconds(ReviewLeadTimeBeforeArmSeconds);

                PollFireWatch(leftWatch);
                PollFireWatch(rightWatch);
                if (leftWatch.Observed || rightWatch.Observed)
                {
                    yield return CompleteSettlementWindow(leftWatch, rightWatch);
                    if (SettleObservedShots(leftWatch.Observed, rightWatch.Observed) > 0)
                        yield break;

                    leftWatch = BeginFireWatch(_fcs.LeftGun, "Left");
                    rightWatch = BeginFireWatch(_fcs.RightGun, "Right");
                }

                // The scheduled current owns its normal arm path. Follower eligibility is a separate rule and is
                // re-evaluated immediately before the follower touches its own safety.
                yield return _fcs.TriggerConsole.ArmSelected(plan.Side, null);

                // If the player fired during the physical arming operation, settle that reality before issuing an
                // automatic trigger pull. This preserves player authority and prevents an accidental second shot.
                PollFireWatch(leftWatch);
                PollFireWatch(rightWatch);
                if (leftWatch.Observed || rightWatch.Observed)
                {
                    yield return CompleteSettlementWindow(leftWatch, rightWatch);
                    if (SettleObservedShots(leftWatch.Observed, rightWatch.Observed) > 0)
                        yield break;

                    leftWatch = BeginFireWatch(_fcs.LeftGun, "Left");
                    rightWatch = BeginFireWatch(_fcs.RightGun, "Right");
                }

                fireWaitToken = BeginFireWait(plan);

                // The other active plan may already be LocalReady. Treat it only as a follower candidate and give
                // it one fresh non-blocking eligibility check against the newly opened shared-fire wait.
                var followerCandidate = plan.Side == LeftRight.Left ? _rightPlan : _leftPlan;
                if (followerCandidate != null
                    && !ReferenceEquals(followerCandidate, plan)
                    && followerCandidate.LocalReady)
                {
                    _fcs.TrackCoroutine(TryArmReadyFollowerDuringCurrentWait(followerCandidate));
                }

                if (_fcs.SceneInteractor.AutoFire)
                {
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    _autoFireIssuedForWait = true;
                    _fcs.TriggerConsole.Fire();
                    autoFireIssued = true;
                }
            }
            finally
            {
                _fcs.SharedResources.Trigger.Release();
            }

            if (leftWatch == null || rightWatch == null)
                yield break;

            // Manual fire is intentionally open-ended. Only an Auto Fire attempt gets a deadline so a failed
            // automation can still recover without turning deliberate wait-and-fire missions into task failures.
            float? autoFireDeadline = autoFireIssued || _autoFireIssuedForWait || _fcs.SceneInteractor.AutoFire
                ? FcsRuntimeClock.Now + AutoFireTimeoutSeconds
                : null;
            var resumeGeneration = FcsRuntimeClock.ResumeGeneration;

            while (true)
            {
                yield return FcsRuntimeClock.WaitUntilFocused();
                if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                    yield break;

                if (resumeGeneration != FcsRuntimeClock.ResumeGeneration)
                {
                    resumeGeneration = FcsRuntimeClock.ResumeGeneration;
                    MelonLogger.Msg(
                        $"[FCS Fire] reconciled after focus restore; Left={GunPhysicalState.Read("Left").Summary()}, " +
                        $"Right={GunPhysicalState.Read("Right").Summary()}");
                }

                PollFireWatch(leftWatch);
                PollFireWatch(rightWatch);

                if (leftWatch.Observed || rightWatch.Observed)
                {
                    yield return CompleteSettlementWindow(leftWatch, rightWatch);
                    var consumed = SettleObservedShots(leftWatch.Observed, rightWatch.Observed);
                    if (consumed > 0)
                        yield break;

                    // A gun with no active FirePlan may still have been fired manually. That physical event is real
                    // but must not fail/consume the current plan. Re-baseline both sides and keep waiting.
                    MelonLogger.Msg("[FCS Fire] observed physical fire without a matching active FirePlan; continuing current wait");
                    leftWatch = BeginFireWatch(_fcs.LeftGun, "Left");
                    rightWatch = BeginFireWatch(_fcs.RightGun, "Right");
                }

                if (!autoFireDeadline.HasValue && (_autoFireIssuedForWait || _fcs.SceneInteractor.AutoFire))
                    autoFireDeadline = FcsRuntimeClock.Now + AutoFireTimeoutSeconds;

                if (autoFireDeadline.HasValue && FcsRuntimeClock.Now >= autoFireDeadline.Value)
                {
                    FailPlan(plan, "automatic fire was not observed");
                    yield break;
                }

                yield return FcsRuntimeClock.WaitForSeconds(0.1f);
            }
        }
        finally
        {
            ClearFireWait(plan, fireWaitToken);
        }
    }

    private IEnumerator TryArmReadyFollowerDuringCurrentWait(FirePlan follower)
    {
        yield return FcsRuntimeClock.WaitUntilFocused();

        var current = _current;
        if (current == null || !CanFollowerArm(current, follower, out _))
            yield break;

        // The first check prevents pointless Trigger-lane work. It is not authorization: the live relationship may
        // change while this coroutine waits for the shared physical console lane.
        yield return _fcs.SharedResources.Trigger.Acquire();
        try
        {
            current = _current;
            if (current == null || !CanFollowerArm(current, follower, out var delta))
                yield break;

            MelonLogger.Msg(
                $"[FCS Plan] ready follower arms without waiting: {follower.Label}; current={current.Label}; " +
                $"azimuth delta={delta:F3}°");
            yield return _fcs.TriggerConsole.ArmSelected(follower.Side, null);
        }
        finally
        {
            _fcs.SharedResources.Trigger.Release();
        }
    }

    private bool CanFollowerArm(FirePlan current, FirePlan follower, out float azimuthDelta)
    {
        azimuthDelta = float.PositiveInfinity;

        if (!ReferenceEquals(_current, current)
            || ReferenceEquals(current, follower)
            || current.Side == follower.Side
            || !IsActive(current)
            || !IsActive(follower)
            || !current.LocalReady
            || !current.AzimuthReady
            || !follower.LocalReady
            || !ReferenceEquals(_fireWaitOwner, current)
            || _fireWaitGeneration != current.Generation
            || _autoFireIssuedForWait)
        {
            return false;
        }

        azimuthDelta = Mathf.Abs(Mathf.DeltaAngle(current.Azimuth, follower.Azimuth));
        return azimuthDelta <= SameAzimuthToleranceDegrees;
    }

    private PhysicalFireWatch BeginFireWatch(GunSystem gun, string sideName)
    {
        var physical = GunPhysicalState.Read(sideName);
        var watch = new PhysicalFireWatch(
            gun,
            sideName,
            gun.BulletInChamber(),
            physical.PendingReload);

        MelonLogger.Msg(
            $"[FCS Fire] {sideName} baseline: chamber={watch.ChamberAtStart ?? "empty"}, " +
            $"pendingReload={watch.PendingReloadAtStart}, physical={physical.Summary()}");
        return watch;
    }

    private static void PollFireWatch(PhysicalFireWatch watch)
    {
        if (watch.Observed)
            return;

        var physical = GunPhysicalState.Read(watch.SideName);
        var chamberNow = watch.Gun.BulletInChamber();

        // Observe a transition from the baseline, not merely a state that was already true when the shared wait
        // began. This matters because the other gun may legitimately still be recovering from an older shot.
        var pendingReloadTransition = !watch.PendingReloadAtStart && physical.PendingReload;
        var chamberTransition = watch.ChamberAtStart != null && chamberNow == null;
        if (pendingReloadTransition || chamberTransition)
        {
            watch.Observed = true;
            MelonLogger.Msg(
                $"[FCS Fire] {watch.SideName} shot observed; baseline={watch.ChamberAtStart ?? "empty"}, " +
                $"now={chamberNow ?? "empty"}, pendingReload={physical.PendingReload}, physical={physical.Summary()}");
        }
    }

    private IEnumerator CompleteSettlementWindow(PhysicalFireWatch leftWatch, PhysicalFireWatch rightWatch)
    {
        for (var i = 0; i < FireSettlementBufferFrames; i++)
            yield return null;

        yield return FcsRuntimeClock.WaitUntilFocused();
        PollFireWatch(leftWatch);
        PollFireWatch(rightWatch);
    }

    /// <summary>
    /// Consume every active FirePlan whose gun actually fired in this physical event. current/next are discarded as
    /// execution pointers and rebuilt from the remaining plans afterward; they never dictate the physical result.
    /// </summary>
    private int SettleObservedShots(bool leftFired, bool rightFired)
    {
        var left = leftFired && _leftPlan != null && IsActive(_leftPlan) ? _leftPlan : null;
        var right = rightFired && _rightPlan != null && IsActive(_rightPlan) ? _rightPlan : null;
        if (left == null && right == null)
            return 0;

        var consumed = 0;
        _current = null;
        _next = null;
        ClearAllFireWait();

        if (left != null)
        {
            CompletePlanFromObservedShot(left);
            consumed++;
        }

        if (right != null && !ReferenceEquals(right, left))
        {
            CompletePlanFromObservedShot(right);
            consumed++;
        }

        MelonLogger.Msg(
            $"[FCS Fire] physical settlement complete: Left={(left != null ? $"T{left.Task.targetId}" : "-")}, " +
            $"Right={(right != null ? $"T{right.Task.targetId}" : "-")}; rebuilding scheduling from reality");

        // Batch first, then trigger planning/scheduling once. FirePlanner will re-read physical/loading state; a
        // just-fired gun therefore remains Pending/recovery-gated even though its FirePlan slot is already free.
        _fcs.Dispatcher.TryDispatch();
        EvaluateScheduling();
        return consumed;
    }

    private void CompletePlanFromObservedShot(FirePlan plan)
    {
        if (plan.CompletionHandled)
            return;

        // A player may physically fire a non-current plan while its preparation coroutine is still running.
        // Stop that old intent so it cannot continue driving elevation after the physical round is already gone.
        if (!plan.LocalReady)
            CancelPreparation(plan);

        plan.ShotObserved = true;
        _fcs.FirePriority.MarkShot(plan);
        plan.Task.progress = Progress.Finished;
        plan.Task.failureReason = "";
        _fcs.Dispatcher.RecordTaskResult(plan.Task);
        ReleaseGunSlot(plan, notify: false);
    }

    private void CancelPreparation(FirePlan plan)
    {
        if (!_prepareCoroutines.TryGetValue(plan, out var handle))
            return;

        try
        {
            MelonCoroutines.Stop(handle);
            MelonLogger.Msg($"[FCS Plan] stopped obsolete preparation after physical fire: {plan.Label}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS Plan] failed to stop obsolete preparation for {plan.Label}: {ex.Message}");
        }
        finally
        {
            _prepareCoroutines.Remove(plan);
        }
    }

    private int BeginFireWait(FirePlan plan)
    {
        var token = ++_fireWaitSerial;
        _activeFireWaitSerial = token;
        _fireWaitOwner = plan;
        _fireWaitGeneration = plan.Generation;
        _autoFireIssuedForWait = false;
        return token;
    }

    private void ClearFireWait(FirePlan plan, int token)
    {
        if (token == 0
            || token != _activeFireWaitSerial
            || !ReferenceEquals(_fireWaitOwner, plan))
        {
            return;
        }

        ClearAllFireWait();
    }

    private void ClearAllFireWait()
    {
        _fireWaitOwner = null;
        _fireWaitGeneration = -1;
        _activeFireWaitSerial = 0;
        _autoFireIssuedForWait = false;
    }

    private void FailPlan(FirePlan plan, string reason)
    {
        if (plan.CompletionHandled)
            return;

        plan.Failed = true;
        plan.FailureReason = reason;
        plan.Task.progress = Progress.Failed;
        plan.Task.failureReason = reason;
        MelonLogger.Error($"[FCS Plan] {plan.Label} failed: {reason}");

        if (ReferenceEquals(_fireWaitOwner, plan))
            ClearAllFireWait();
        if (ReferenceEquals(_current, plan))
            _current = null;
        if (ReferenceEquals(_next, plan))
            _next = null;

        _fcs.Dispatcher.RecordTaskResult(plan.Task);
        ReleaseGunSlot(plan, notify: true);
    }

    private void ReleaseGunSlot(FirePlan plan, bool notify)
    {
        if (plan.CompletionHandled)
            return;

        plan.CompletionHandled = true;
        _prepareCoroutines.Remove(plan);

        var gun = plan.Side == LeftRight.Left ? _fcs.LeftGun : _fcs.RightGun;
        gun.ReleaseElevationOverride();
        _fcs.TriggerConsole.SetGunReady(plan.Side, false);

        if (plan.Side == LeftRight.Left && ReferenceEquals(_leftPlan, plan))
            _leftPlan = null;
        if (plan.Side == LeftRight.Right && ReferenceEquals(_rightPlan, plan))
            _rightPlan = null;
        if (ReferenceEquals(_current, plan))
            _current = null;
        if (ReferenceEquals(_next, plan))
            _next = null;

        if (!notify)
            return;

        _fcs.Dispatcher.TryDispatch();
        EvaluateScheduling();
    }

    private bool IsActive(FirePlan plan)
    {
        return plan.Generation == _fcs.FirePriority.Generation
               && ReferenceEquals(GetPlan(plan.Side), plan)
               && !plan.CompletionHandled;
    }

    private sealed class PhysicalFireWatch
    {
        public GunSystem Gun { get; }
        public string SideName { get; }
        public string? ChamberAtStart { get; }
        public bool PendingReloadAtStart { get; }
        public bool Observed { get; set; }

        public PhysicalFireWatch(
            GunSystem gun,
            string sideName,
            string? chamberAtStart,
            bool pendingReloadAtStart)
        {
            Gun = gun;
            SideName = sideName;
            ChamberAtStart = chamberAtStart;
            PendingReloadAtStart = pendingReloadAtStart;
        }
    }
}
