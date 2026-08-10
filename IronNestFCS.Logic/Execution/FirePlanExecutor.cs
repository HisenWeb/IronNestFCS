// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Scheduling;
using MelonLoader;

namespace IronNestFCS.Logic.Execution;

/// <summary>
/// Capacity-two FirePlan executor. Per-gun preparation runs independently; shared azimuth/fire execution
/// follows the committed one-shot order. A freed gun slot may be refilled immediately, but a compared plan
/// is never compared again.
/// </summary>
internal sealed class FirePlanExecutor
{
    private const float ElevationTimeoutSeconds = 35f;
    private const float LoadingObservationTimeoutSeconds = 90f;
    private const float AutoFireTimeoutSeconds = 25f;
    private const float ManualFireTimeoutSeconds = 300f;

    private readonly FSC _fcs;

    private FirePlan? _leftPlan;
    private FirePlan? _rightPlan;
    private FirePlan? _current;
    private FirePlan? _next;

    private FirePlan? _armedWaitingPlan;
    private int _armedWaitingGeneration = -1;

    public FirePlanExecutor(FSC fcs)
    {
        _fcs = fcs;
    }

    public ArtilleryTask? LeftTask => _leftPlan?.Task;
    public ArtilleryTask? RightTask => _rightPlan?.Task;
    public bool HasFreeGun => _leftPlan == null || _rightPlan == null;

    public FirePlan? GetPlan(LeftRight side) => side == LeftRight.Left ? _leftPlan : _rightPlan;

    public void DisposeState()
    {
        _leftPlan = null;
        _rightPlan = null;
        _current = null;
        _next = null;
        _armedWaitingPlan = null;
        _armedWaitingGeneration = -1;
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
        _fcs.TrackCoroutine(PrepareLocal(plan));
        EvaluateScheduling();
        return true;
    }

    public void Tick() => EvaluateScheduling();

    public void OnAutoFireEnabled()
    {
        var plan = _armedWaitingPlan;
        if (plan == null)
            return;

        if (_armedWaitingGeneration != _fcs.FirePriority.Generation
            || !ReferenceEquals(_current, plan)
            || plan.Failed
            || plan.Task.progress != Progress.WaitingForFire)
        {
            _armedWaitingPlan = null;
            _armedWaitingGeneration = -1;
            return;
        }

        _armedWaitingPlan = null;
        _armedWaitingGeneration = -1;
        MelonLogger.Msg($"[FCS] AutoFire enabled while T{plan.Task.targetId} is armed; firing committed plan");
        _fcs.TriggerConsole.Fire();
    }

    /// <summary>
    /// Two unpaired plans compare once. A compared Second is promoted without re-comparison. One unpaired
    /// plan waits only while the task queue can still provide a partner; otherwise it single-commits.
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
        if (!gun.LastElevationSucceeded)
        {
            FailPlan(plan, $"elevation did not reach {plan.Elevation:F1}°");
            yield break;
        }

        plan.LocalReady = true;
        plan.Task.progress = Progress.WaitingForFire;
        MelonLogger.Msg($"[FCS Plan] {plan.Label}: local ready (LoadedReady + elevation)");
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

        var autoFireIssued = false;
        try
        {
            yield return _fcs.SharedResources.Trigger.Acquire();
            try
            {
                if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                    yield break;

                yield return _fcs.TriggerConsole.PrepareForNewFireSolution(plan.Side);
                yield return _fcs.TriggerConsole.ConfirmTask();
                yield return _fcs.TriggerConsole.ConfirmBullet();
                yield return _fcs.TriggerConsole.ConfirmRotation();
                yield return _fcs.TriggerConsole.ConfirmElevation();
                yield return _fcs.TriggerConsole.ReadyToFire();
                yield return _fcs.TriggerConsole.Arm(plan.Side);

                if (_fcs.SceneInteractor.AutoFire)
                {
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    _fcs.TriggerConsole.Fire();
                    autoFireIssued = true;
                }
            }
            finally
            {
                _fcs.SharedResources.Trigger.Release();
            }

            if (!autoFireIssued)
            {
                _armedWaitingPlan = plan;
                _armedWaitingGeneration = plan.Generation;
            }

            var gun = plan.Side == LeftRight.Left ? _fcs.LeftGun : _fcs.RightGun;
            var fireTimeout = autoFireIssued || _fcs.SceneInteractor.AutoFire
                ? AutoFireTimeoutSeconds
                : ManualFireTimeoutSeconds;

            yield return gun.WaitFire(fireTimeout);

            if (ReferenceEquals(_armedWaitingPlan, plan))
            {
                _armedWaitingPlan = null;
                _armedWaitingGeneration = -1;
            }

            if (!gun.LastFireObserved)
            {
                FailPlan(plan, autoFireIssued || _fcs.SceneInteractor.AutoFire
                    ? "automatic fire was not observed"
                    : "manual fire wait timed out");
                yield break;
            }

            plan.ShotObserved = true;
            _fcs.FirePriority.MarkShot(plan);

            // Advance shared order immediately after the shot; fired gun remains occupied until EmptyReady.
            ReleaseSharedAfterShot(plan);

            plan.Task.progress = Progress.BackToIdle;
            yield return gun.WaitBackToIdle();
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (plan.Failed)
                yield break;

            plan.Task.progress = Progress.Finished;
            _fcs.Dispatcher.RecordTaskResult(plan.Task);
            ReleaseGunSlot(plan);
        }
        finally
        {
            if (ReferenceEquals(_armedWaitingPlan, plan))
            {
                _armedWaitingPlan = null;
                _armedWaitingGeneration = -1;
            }
        }
    }

    private void ReleaseSharedAfterShot(FirePlan plan)
    {
        if (!ReferenceEquals(_current, plan))
            return;

        _current = null;

        if (_next != null && _next.Compared && IsActive(_next))
        {
            var promoted = _next;
            _next = null;
            SetCurrent(promoted, promote: true);
            return;
        }

        EvaluateScheduling();
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

        if (ReferenceEquals(_armedWaitingPlan, plan))
        {
            _armedWaitingPlan = null;
            _armedWaitingGeneration = -1;
        }

        if (ReferenceEquals(_current, plan))
            _current = null;
        if (ReferenceEquals(_next, plan))
            _next = null;

        _fcs.Dispatcher.RecordTaskResult(plan.Task);
        ReleaseGunSlot(plan);
        EvaluateScheduling();
    }

    private void ReleaseGunSlot(FirePlan plan)
    {
        if (plan.CompletionHandled)
            return;

        plan.CompletionHandled = true;
        var gun = plan.Side == LeftRight.Left ? _fcs.LeftGun : _fcs.RightGun;
        gun.ReleaseElevationOverride();

        if (plan.Side == LeftRight.Left && ReferenceEquals(_leftPlan, plan))
            _leftPlan = null;
        if (plan.Side == LeftRight.Right && ReferenceEquals(_rightPlan, plan))
            _rightPlan = null;
        if (ReferenceEquals(_next, plan))
            _next = null;

        _fcs.Dispatcher.TryDispatch();
        EvaluateScheduling();
    }

    private bool IsActive(FirePlan plan)
    {
        return plan.Generation == _fcs.FirePriority.Generation
               && ReferenceEquals(GetPlan(plan.Side), plan)
               && !plan.CompletionHandled;
    }
}
