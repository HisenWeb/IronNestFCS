// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;
using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Two-stage planner. The pre-match stage reads one immutable gun/loading snapshot and builds side-effect-free
/// Task x Gun eligibility candidates. Only assignments selected by TaskGunMatcher are materialized through the
/// physical ballistic calculator, so rejected alternatives never create game-side ballistic stickers.
/// </summary>
internal sealed class FirePlanner
{
    private const float MaxRangePerChargeKm = 5f;

    private readonly FSC _fcs;

    public FirePlanner(FSC fcs)
    {
        _fcs = fcs;
    }

    public FirePlanningSnapshot CaptureSnapshot()
    {
        var snapshotAt = FcsRuntimeClock.Now;
        var turretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        var currentAzimuth = turretController?.CurrentAngle ?? 0f;

        return new FirePlanningSnapshot(
            snapshotAt,
            currentAzimuth,
            GunPhysicalState.Read("Left"),
            GunPhysicalState.Read("Right"),
            _fcs.Loading.GetSnapshot(GunSide.Left),
            _fcs.Loading.GetSnapshot(GunSide.Right),
            _fcs.PlanExecutor.GetPlan(LeftRight.Left) == null,
            _fcs.PlanExecutor.GetPlan(LeftRight.Right) == null);
    }

    /// <summary>
    /// Compatibility wrapper for callers that still request one plan directly. Selection remains side-effect free;
    /// the ballistic calculator is touched only after one gun has been chosen.
    /// </summary>
    public IEnumerator BuildPlan(ArtilleryTask task, Action<FirePlan?, string> completed)
    {
        var snapshot = CaptureSnapshot();
        var planning = BuildEligibility(task, snapshot);
        var matchAt = FcsRuntimeClock.Now;
        planning.FinalizeTiming(snapshot.SnapshotAt, matchAt);

        var chosen = ChooseCandidate(planning.LeftCandidate, planning.RightCandidate);
        if (chosen == null)
        {
            task.pendingHint = planning.PendingHint;
            completed(null, planning.ShouldWait ? "WAIT: " + planning.FailureDetail : planning.FailureDetail);
            yield break;
        }

        FirePlanCandidate? materialized = null;
        var materializeReason = "";
        yield return MaterializeCandidate(
            task,
            chosen,
            snapshot,
            result => materialized = result,
            reason => materializeReason = reason);

        if (materialized == null)
        {
            completed(null, materializeReason);
            yield break;
        }

        var plannedAt = FcsRuntimeClock.Now;
        materialized.FinalizeTiming(snapshot.SnapshotAt, plannedAt);
        completed(CreatePlan(planning, materialized, plannedAt), "");
    }

    /// <summary>
    /// Build the hard eligibility matrix without operating any physical game console. Shell, charge, current
    /// loading transaction, range and slot availability are resolved here; ballistic/elevation validation is not.
    /// </summary>
    public TaskPlanningResult BuildEligibility(ArtilleryTask task, FirePlanningSnapshot snapshot)
    {
        task.progress = Progress.Calculating;
        task.pendingHint = PendingHint.None;

        MelonLogger.Msg(
            $"[FCS Match] T{task.targetId}: snapshot currentAz={snapshot.CurrentAzimuth:F2}°, " +
            $"Left={snapshot.LeftLoading.PhysicalState}, Right={snapshot.RightLoading.PhysicalState}");

        TaskGunCandidate? left = null;
        TaskGunCandidate? right = null;
        var leftReason = "";
        var rightReason = "";
        var leftHint = PendingHint.None;
        var rightHint = PendingHint.None;

        if (snapshot.LeftSlotAvailable)
        {
            left = BuildEligibilityCandidate(
                task,
                LeftRight.Left,
                snapshot.LeftLoading,
                snapshot.CurrentAzimuth,
                out leftReason,
                out leftHint);
        }
        else
        {
            leftReason = "Left slot occupied";
        }

        if (snapshot.RightSlotAvailable)
        {
            right = BuildEligibilityCandidate(
                task,
                LeftRight.Right,
                snapshot.RightLoading,
                snapshot.CurrentAzimuth,
                out rightReason,
                out rightHint);
        }
        else
        {
            rightReason = "Right slot occupied";
        }

        var pendingHint = CombinePendingHint(leftHint, rightHint);
        var detail = $"no eligible gun in match snapshot; Left={leftReason}; Right={rightReason}";
        var shouldWait = !snapshot.LeftSlotAvailable
                         || !snapshot.RightSlotAvailable
                         || IsTransient(snapshot.LeftLoading.PhysicalState)
                         || IsTransient(snapshot.RightLoading.PhysicalState);

        return new TaskPlanningResult(
            task, left, right, leftReason, rightReason, pendingHint, detail, shouldWait);
    }

    /// <summary>
    /// Materialize exactly one already-selected Task x Gun edge. This is the only matching-stage method that is
    /// allowed to operate the physical ballistic calculator and therefore the only stage that can create a sticker.
    /// </summary>
    public IEnumerator MaterializeCandidate(
        ArtilleryTask task,
        TaskGunCandidate candidate,
        FirePlanningSnapshot snapshot,
        Action<FirePlanCandidate?> completed,
        Action<string> failed)
    {
        completed(null);

        var ballistic = new BallisticSolveResult();
        yield return SolveBallistic(task, candidate.Shell, candidate.Charge, ballistic);
        if (!ballistic.Succeeded)
        {
            failed($"{candidate.Shell.DisplayName()} C{candidate.Charge} ballistic calculation failed");
            yield break;
        }

        var physical = candidate.Side == LeftRight.Left
            ? snapshot.LeftPhysical
            : snapshot.RightPhysical;
        var elevation = ballistic.Elevation;
        if (!physical.IsElevationWithinPhysicalRange(elevation))
        {
            failed($"{candidate.Shell.DisplayName()} C{candidate.Charge} elevation {elevation:F2} outside physical range");
            yield break;
        }

        var elevationSeconds = FireReadyEstimator.ElevationSeconds(physical.Elevation, elevation);
        var alignmentScore = FireReadyEstimator.AlignmentScore(
            snapshot.CurrentAzimuth,
            task.angel,
            physical.Elevation,
            elevation);

        completed(new FirePlanCandidate(
            candidate.Side,
            candidate.Shell,
            candidate.Charge,
            elevation,
            candidate.EtaKnown,
            candidate.LoadAlreadyRunning,
            alignmentScore,
            candidate.LoadSeconds,
            elevationSeconds,
            candidate.AzimuthSeconds,
            candidate.LoadLabel));
    }

    public FirePlan CreatePlan(TaskPlanningResult planning, FirePlanCandidate chosen, float plannedAt)
    {
        var task = planning.Task;
        task.pendingHint = PendingHint.None;
        task.bulletType = chosen.Shell;
        task.chargeCount = chosen.Charge;
        task.elevation = chosen.Elevation;

        var plan = new FirePlan(
            task,
            chosen.Side,
            chosen.Shell,
            chosen.Charge,
            chosen.Elevation,
            task.angel,
            plannedAt,
            chosen.EtaKnown,
            chosen.EstimatedLocalReadyAt,
            chosen.AzimuthSeconds,
            chosen.AlignmentScore,
            _fcs.FirePriority.Generation);

        if (TimeToImpactEstimator.TryEstimateSeconds(task.distance, chosen.Charge, out var estimatedTti))
            plan.TrySetEstimatedFlightSeconds(estimatedTti);

        MelonLogger.Msg(
            $"[FCS Plan] T{task.targetId}: committed {plan.Label}, E={plan.Elevation:F2}, Az={plan.Azimuth:F2}, " +
            $"ETA={(plan.EtaKnown ? Math.Max(0f, plan.EstimatedReadyAt - plannedAt).ToString("F1") : "unknown")}s, " +
            $"load={chosen.LoadLabel}");

        return plan;
    }

    private TaskGunCandidate? BuildEligibilityCandidate(
        ArtilleryTask task,
        LeftRight side,
        LoadingSnapshot loading,
        float currentAzimuth,
        out string reason,
        out PendingHint pendingHint)
    {
        reason = "";
        pendingHint = PendingHint.None;

        if (!loading.IsBound)
        {
            reason = "persistent loading system unbound";
            return null;
        }

        if (!TryResolveRound(
                task,
                loading,
                out var shell,
                out var charge,
                out var loadKnown,
                out var loadAlreadyRunning,
                out var loadSeconds,
                out var loadLabel,
                out var resolveReason))
        {
            reason = resolveReason;
            return null;
        }

        if (shell != task.bulletType)
        {
            pendingHint = PendingHint.ShellMismatch;
            reason = $"loaded {shell.DisplayName()} does not match requested {task.bulletType.DisplayName()}";
            MelonLogger.Msg(
                $"[FCS Match] T{task.targetId}: quick reject {side}; " +
                $"shell={shell.DisplayName()} requested={task.bulletType.DisplayName()}");
            return null;
        }

        if (charge is < 1 or > 6)
        {
            reason = $"invalid charge C{charge}";
            return null;
        }

        var maxRangeKm = charge * MaxRangePerChargeKm;
        if (task.distance > maxRangeKm)
        {
            pendingHint = PendingHint.ChargeRangeInsufficient;
            reason = $"{shell.DisplayName()} C{charge} max range {maxRangeKm:F2}km < target {task.distance:F2}km";
            MelonLogger.Msg(
                $"[FCS Match] T{task.targetId}: quick reject {side} {shell.DisplayName()} C{charge}; " +
                $"target={task.distance:F2}km > max={maxRangeKm:F2}km");
            return null;
        }

        var azimuthSeconds = FireReadyEstimator.AzimuthSeconds(currentAzimuth, task.angel);
        var azimuthScore = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, task.angel));
        reason = "eligible";

        return new TaskGunCandidate(
            side,
            shell,
            charge,
            loadKnown,
            loadAlreadyRunning,
            loadKnown ? loadSeconds : 0f,
            azimuthSeconds,
            azimuthScore,
            loadLabel);
    }

    private IEnumerator SolveBallistic(
        ArtilleryTask task,
        BulletType shell,
        int charge,
        BallisticSolveResult result)
    {
        yield return _fcs.SharedResources.Ballistic.Acquire();
        try
        {
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.SetDistance(task.distance);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.SetDirection(task.angel);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.SetCharge(charge);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.SetShellType(shell);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.Calculate();
            yield return FcsRuntimeClock.WaitUntilFocused();

            var elevation = _fcs.BallisticCalculator.GetElevation();
            result.Elevation = elevation;
            result.Succeeded = _fcs.BallisticCalculator.LastCalculationSucceeded
                               && !float.IsNaN(elevation)
                               && !float.IsInfinity(elevation);
        }
        finally
        {
            _fcs.SharedResources.Ballistic.Release();
        }
    }

    private bool TryResolveRound(
        ArtilleryTask task,
        LoadingSnapshot loading,
        out BulletType shell,
        out int charge,
        out bool loadKnown,
        out bool loadAlreadyRunning,
        out float loadSeconds,
        out string loadLabel,
        out string reason)
    {
        shell = task.bulletType;
        charge = 0;
        loadKnown = false;
        loadAlreadyRunning = false;
        loadSeconds = 0f;
        loadLabel = "";
        reason = "";

        if (loading.HasTransaction
            && loading.TransactionState != LoadingTransactionState.Failed
            && loading.RequestedShell.HasValue
            && loading.RequestedCharge > 0)
        {
            shell = (BulletType)(int)loading.RequestedShell.Value;
            charge = loading.RequestedCharge;

            if (loading.TransactionState == LoadingTransactionState.LoadedReady && loading.LoadedReady)
            {
                loadKnown = true;
                loadSeconds = 0f;
                loadLabel = "persistent transaction loaded";
            }
            else if (loading.EstimatedRemainingSeconds.HasValue)
            {
                loadKnown = true;
                loadAlreadyRunning = true;
                loadSeconds = loading.EstimatedRemainingSeconds.Value;
                loadLabel = "persistent transaction ETA";
            }
            else
            {
                loadLabel = "persistent transaction ETA unknown";
            }

            return true;
        }

        switch (loading.PhysicalState)
        {
            case LoadingPhysicalState.LoadedReady:
                if (!loading.ActualShell.HasValue || loading.ActualCharge <= 0)
                {
                    reason = "loaded physical state missing shell/charge";
                    return false;
                }

                shell = (BulletType)(int)loading.ActualShell.Value;
                charge = loading.ActualCharge;
                loadKnown = true;
                loadSeconds = 0f;
                loadLabel = "already loaded";
                return true;

            case LoadingPhysicalState.ShellLoaded:
                if (!loading.ActualShell.HasValue)
                {
                    reason = "shell-loaded physical state missing shell type";
                    return false;
                }

                shell = (BulletType)(int)loading.ActualShell.Value;
                charge = _fcs.SceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);
                loadKnown = false;
                loadLabel = "shell-loaded remaining ETA not measured";
                return true;

            case LoadingPhysicalState.EmptyReady:
                shell = task.bulletType;
                charge = _fcs.SceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);
                loadKnown = true;
                loadSeconds = FireReadyEstimator.FreshLoadReadySeconds;
                loadLabel = "fresh load baseline";
                return true;

            default:
                reason = $"physical loading state {loading.PhysicalState} is not plannable";
                return false;
        }
    }

    private static PendingHint CombinePendingHint(PendingHint left, PendingHint right)
    {
        if (left == right)
            return left;
        if (left == PendingHint.None || right == PendingHint.None)
            return PendingHint.None;
        return PendingHint.AmmoMismatch;
    }

    private static bool IsTransient(LoadingPhysicalState state)
    {
        return state == LoadingPhysicalState.Recovering
               || state == LoadingPhysicalState.PostShotRecovery
               || state == LoadingPhysicalState.Unknown
               || state == LoadingPhysicalState.Unbound;
    }

    private static TaskGunCandidate? ChooseCandidate(TaskGunCandidate? left, TaskGunCandidate? right)
    {
        if (left == null)
            return right;
        if (right == null)
            return left;

        if (left.EtaKnown && right.EtaKnown)
        {
            var delta = left.EstimatedReadyAt - right.EstimatedReadyAt;
            if (Mathf.Abs(delta) <= FireReadyEstimator.EtaTieToleranceSeconds)
                return left;
            return delta < 0f ? left : right;
        }

        var azimuthDelta = left.AzimuthScore - right.AzimuthScore;
        if (Mathf.Abs(azimuthDelta) <= FireReadyEstimator.AlignmentTieTolerance)
            return left;
        return azimuthDelta < 0f ? left : right;
    }

    private sealed class BallisticSolveResult
    {
        public bool Succeeded { get; set; }
        public float Elevation { get; set; } = float.NaN;
    }
}

internal sealed class FirePlanningSnapshot
{
    public float SnapshotAt { get; }
    public float CurrentAzimuth { get; }
    public GunPhysicalState LeftPhysical { get; }
    public GunPhysicalState RightPhysical { get; }
    public LoadingSnapshot LeftLoading { get; }
    public LoadingSnapshot RightLoading { get; }
    public bool LeftSlotAvailable { get; }
    public bool RightSlotAvailable { get; }

    public FirePlanningSnapshot(
        float snapshotAt,
        float currentAzimuth,
        GunPhysicalState leftPhysical,
        GunPhysicalState rightPhysical,
        LoadingSnapshot leftLoading,
        LoadingSnapshot rightLoading,
        bool leftSlotAvailable,
        bool rightSlotAvailable)
    {
        SnapshotAt = snapshotAt;
        CurrentAzimuth = currentAzimuth;
        LeftPhysical = leftPhysical;
        RightPhysical = rightPhysical;
        LeftLoading = leftLoading;
        RightLoading = rightLoading;
        LeftSlotAvailable = leftSlotAvailable;
        RightSlotAvailable = rightSlotAvailable;
    }
}

/// <summary>
/// Side-effect-free Task x Gun edge used only for matching. It intentionally has no ballistic elevation.
/// </summary>
internal sealed class TaskGunCandidate
{
    public LeftRight Side { get; }
    public BulletType Shell { get; }
    public int Charge { get; }
    public bool EtaKnown { get; }
    public bool LoadAlreadyRunning { get; }
    public float LoadSeconds { get; }
    public float AzimuthSeconds { get; }
    public float AzimuthScore { get; }
    public string LoadLabel { get; }
    public float EstimatedLocalReadyAt { get; private set; } = float.NaN;
    public float EstimatedReadyAt { get; private set; } = float.NaN;

    public TaskGunCandidate(
        LeftRight side,
        BulletType shell,
        int charge,
        bool etaKnown,
        bool loadAlreadyRunning,
        float loadSeconds,
        float azimuthSeconds,
        float azimuthScore,
        string loadLabel)
    {
        Side = side;
        Shell = shell;
        Charge = charge;
        EtaKnown = etaKnown;
        LoadAlreadyRunning = loadAlreadyRunning;
        LoadSeconds = loadSeconds;
        AzimuthSeconds = azimuthSeconds;
        AzimuthScore = azimuthScore;
        LoadLabel = loadLabel;
    }

    public void FinalizeTiming(float snapshotAt, float decisionAt)
    {
        if (!EtaKnown)
        {
            EstimatedLocalReadyAt = float.NaN;
            EstimatedReadyAt = float.NaN;
            return;
        }

        var loadReadyAt = LoadAlreadyRunning
            ? snapshotAt + LoadSeconds
            : decisionAt + LoadSeconds;

        // Elevation is intentionally unknown before matching. This estimate is only a soft matching cost;
        // FirePlanCandidate recomputes the full load + elevation + azimuth timing after materialization.
        EstimatedLocalReadyAt = Math.Max(decisionAt, loadReadyAt);
        EstimatedReadyAt = Math.Max(EstimatedLocalReadyAt, decisionAt + AzimuthSeconds);
    }
}

internal sealed class TaskPlanningResult
{
    public ArtilleryTask Task { get; }
    public TaskGunCandidate? LeftCandidate { get; }
    public TaskGunCandidate? RightCandidate { get; }
    public string LeftReason { get; }
    public string RightReason { get; }
    public PendingHint PendingHint { get; }
    public string FailureDetail { get; }
    public bool ShouldWait { get; }

    public bool HasCandidate => LeftCandidate != null || RightCandidate != null;

    public TaskPlanningResult(
        ArtilleryTask task,
        TaskGunCandidate? leftCandidate,
        TaskGunCandidate? rightCandidate,
        string leftReason,
        string rightReason,
        PendingHint pendingHint,
        string failureDetail,
        bool shouldWait)
    {
        Task = task;
        LeftCandidate = leftCandidate;
        RightCandidate = rightCandidate;
        LeftReason = leftReason;
        RightReason = rightReason;
        PendingHint = pendingHint;
        FailureDetail = failureDetail;
        ShouldWait = shouldWait;
    }

    public void FinalizeTiming(float snapshotAt, float decisionAt)
    {
        LeftCandidate?.FinalizeTiming(snapshotAt, decisionAt);
        RightCandidate?.FinalizeTiming(snapshotAt, decisionAt);
    }

    public TaskGunCandidate? CandidateFor(LeftRight side) =>
        side == LeftRight.Left ? LeftCandidate : RightCandidate;
}
