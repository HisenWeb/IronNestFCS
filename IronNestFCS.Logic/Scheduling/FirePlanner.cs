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
/// Builds Task x Gun candidates from one immutable planning snapshot. Candidate eligibility is decided here;
/// TaskGunMatcher decides which eligible candidates should actually consume the currently free gun slots.
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
    /// Compatibility wrapper for callers that still want one task to choose one gun immediately.
    /// Batch dispatch uses BuildCandidates + TaskGunMatcher instead.
    /// </summary>
    public IEnumerator BuildPlan(ArtilleryTask task, Action<FirePlan?, string> completed)
    {
        var snapshot = CaptureSnapshot();
        TaskPlanningResult? planning = null;
        yield return BuildCandidates(task, snapshot, result => planning = result);

        if (planning == null)
        {
            completed(null, "planning result unavailable");
            yield break;
        }

        var plannedAt = FcsRuntimeClock.Now;
        planning.FinalizeTiming(snapshot.SnapshotAt, plannedAt);
        var chosen = ChooseCandidate(planning.LeftCandidate, planning.RightCandidate);
        if (chosen == null)
        {
            task.pendingHint = planning.PendingHint;
            completed(null, planning.ShouldWait ? "WAIT: " + planning.FailureDetail : planning.FailureDetail);
            yield break;
        }

        completed(CreatePlan(planning, chosen, plannedAt), "");
    }

    public IEnumerator BuildCandidates(
        ArtilleryTask task,
        FirePlanningSnapshot snapshot,
        Action<TaskPlanningResult> completed)
    {
        task.progress = Progress.Calculating;
        task.pendingHint = PendingHint.None;

        MelonLogger.Msg(
            $"[FCS Plan] T{task.targetId}: match snapshot currentAz={snapshot.CurrentAzimuth:F2}°, " +
            $"Left={snapshot.LeftLoading.PhysicalState}, Right={snapshot.RightLoading.PhysicalState}");

        var ballisticCache = new Dictionary<(BulletType Shell, int Charge), BallisticSolveResult>();
        FirePlanCandidate? left = null;
        FirePlanCandidate? right = null;
        var leftReason = "";
        var rightReason = "";
        var leftHint = PendingHint.None;
        var rightHint = PendingHint.None;

        if (snapshot.LeftSlotAvailable)
        {
            yield return BuildCandidate(
                task,
                LeftRight.Left,
                snapshot.LeftPhysical,
                snapshot.LeftLoading,
                snapshot.CurrentAzimuth,
                ballisticCache,
                result => left = result,
                reason => leftReason = reason,
                hint => leftHint = hint);
        }
        else
        {
            leftReason = "Left slot occupied";
        }

        if (snapshot.RightSlotAvailable)
        {
            yield return BuildCandidate(
                task,
                LeftRight.Right,
                snapshot.RightPhysical,
                snapshot.RightLoading,
                snapshot.CurrentAzimuth,
                ballisticCache,
                result => right = result,
                reason => rightReason = reason,
                hint => rightHint = hint);
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

        completed(new TaskPlanningResult(
            task, left, right, leftReason, rightReason, pendingHint, detail, shouldWait));
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

    private IEnumerator BuildCandidate(
        ArtilleryTask task,
        LeftRight side,
        GunPhysicalState physical,
        LoadingSnapshot loading,
        float currentAzimuth,
        Dictionary<(BulletType Shell, int Charge), BallisticSolveResult> ballisticCache,
        Action<FirePlanCandidate?> setResult,
        Action<string> setReason,
        Action<PendingHint> setPendingHint)
    {
        setResult(null);
        setPendingHint(PendingHint.None);

        if (!loading.IsBound)
        {
            setReason("persistent loading system unbound");
            yield break;
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
            setReason(resolveReason);
            yield break;
        }

        if (shell != task.bulletType)
        {
            setPendingHint(PendingHint.ShellMismatch);
            setReason($"loaded {shell.DisplayName()} does not match requested {task.bulletType.DisplayName()}");
            MelonLogger.Msg(
                $"[FCS Plan] T{task.targetId}: quick reject {side}; " +
                $"shell={shell.DisplayName()} requested={task.bulletType.DisplayName()}");
            yield break;
        }

        if (charge is < 1 or > 6)
        {
            setReason($"invalid charge C{charge}");
            yield break;
        }

        var maxRangeKm = charge * MaxRangePerChargeKm;
        if (task.distance > maxRangeKm)
        {
            setPendingHint(PendingHint.ChargeRangeInsufficient);
            setReason($"{shell.DisplayName()} C{charge} max range {maxRangeKm:F2}km < target {task.distance:F2}km");
            MelonLogger.Msg(
                $"[FCS Plan] T{task.targetId}: quick reject {side} {shell.DisplayName()} C{charge}; " +
                $"target={task.distance:F2}km > max={maxRangeKm:F2}km");
            yield break;
        }

        var key = (shell, charge);
        if (!ballisticCache.TryGetValue(key, out var ballistic))
        {
            ballistic = new BallisticSolveResult();
            yield return SolveBallistic(task, shell, charge, ballistic);
            ballisticCache[key] = ballistic;
        }
        else
        {
            MelonLogger.Msg(
                $"[FCS BALLISTIC] planning cache hit: T{task.targetId} {shell.DisplayName()} C{charge} " +
                $"reuses E={(ballistic.Succeeded ? ballistic.Elevation.ToString("F2") : "failed")}");
        }

        if (!ballistic.Succeeded)
        {
            setReason($"{shell.DisplayName()} C{charge} ballistic calculation failed");
            yield break;
        }

        var elevation = ballistic.Elevation;
        if (!physical.IsElevationWithinPhysicalRange(elevation))
        {
            setReason($"{shell.DisplayName()} C{charge} elevation {elevation:F2} outside physical range");
            yield break;
        }

        var elevationSeconds = FireReadyEstimator.ElevationSeconds(physical.Elevation, elevation);
        var azimuthSeconds = FireReadyEstimator.AzimuthSeconds(currentAzimuth, task.angel);
        var alignmentScore = FireReadyEstimator.AlignmentScore(currentAzimuth, task.angel, physical.Elevation, elevation);

        setResult(new FirePlanCandidate(
            side,
            shell,
            charge,
            elevation,
            loadKnown,
            loadAlreadyRunning,
            alignmentScore,
            loadKnown ? loadSeconds : 0f,
            elevationSeconds,
            azimuthSeconds,
            loadLabel));
        setReason("eligible");
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

    private static FirePlanCandidate? ChooseCandidate(FirePlanCandidate? left, FirePlanCandidate? right)
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

        var alignmentDelta = left.AlignmentScore - right.AlignmentScore;
        if (Mathf.Abs(alignmentDelta) <= FireReadyEstimator.AlignmentTieTolerance)
            return left;
        return alignmentDelta < 0f ? left : right;
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

internal sealed class TaskPlanningResult
{
    public ArtilleryTask Task { get; }
    public FirePlanCandidate? LeftCandidate { get; }
    public FirePlanCandidate? RightCandidate { get; }
    public string LeftReason { get; }
    public string RightReason { get; }
    public PendingHint PendingHint { get; }
    public string FailureDetail { get; }
    public bool ShouldWait { get; }

    public bool HasCandidate => LeftCandidate != null || RightCandidate != null;

    public TaskPlanningResult(
        ArtilleryTask task,
        FirePlanCandidate? leftCandidate,
        FirePlanCandidate? rightCandidate,
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

    public FirePlanCandidate? CandidateFor(LeftRight side) =>
        side == LeftRight.Left ? LeftCandidate : RightCandidate;
}
