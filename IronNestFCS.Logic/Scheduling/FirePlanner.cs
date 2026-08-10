using System.Collections;
using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// One task planning round: capture one state snapshot, solve viable gun candidates, compare them,
/// and emit one immutable FirePlan. Queueing itself never reads gun state.
/// </summary>
internal sealed class FirePlanner
{
    private readonly FSC _fcs;

    public FirePlanner(FSC fcs)
    {
        _fcs = fcs;
    }

    public IEnumerator BuildPlan(ArtilleryTask task, Action<FirePlan?, string> completed)
    {
        task.progress = Progress.Calculating;

        var snapshotAt = FcsRuntimeClock.Now;
        var turretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        var currentAzimuth = turretController?.CurrentAngle ?? 0f;

        // Exactly one physical/loading snapshot per planning round. Azimuth is also captured once here;
        // the resulting FirePlan stores only the target azimuth and never continuously re-plans from motion.
        var leftPhysical = GunPhysicalState.Read("Left");
        var rightPhysical = GunPhysicalState.Read("Right");
        var leftLoading = _fcs.Loading.GetSnapshot(GunSide.Left);
        var rightLoading = _fcs.Loading.GetSnapshot(GunSide.Right);
        var ballisticCache = new Dictionary<(BulletType Shell, int Charge), BallisticSolveResult>();

        MelonLogger.Msg(
            $"[FCS Plan] T{task.targetId}: snapshot currentAz={currentAzimuth:F2}°, " +
            $"Left={leftLoading.PhysicalState}, Right={rightLoading.PhysicalState}");

        FirePlanCandidate? left = null;
        FirePlanCandidate? right = null;
        var leftReason = "";
        var rightReason = "";

        if (_fcs.PlanExecutor.GetPlan(LeftRight.Left) == null)
        {
            yield return BuildCandidate(task, LeftRight.Left, leftPhysical, leftLoading, currentAzimuth,
                ballisticCache, result => left = result, reason => leftReason = reason);
        }
        else
        {
            leftReason = "Left slot occupied";
        }

        if (_fcs.PlanExecutor.GetPlan(LeftRight.Right) == null)
        {
            yield return BuildCandidate(task, LeftRight.Right, rightPhysical, rightLoading, currentAzimuth,
                ballisticCache, result => right = result, reason => rightReason = reason);
        }
        else
        {
            rightReason = "Right slot occupied";
        }

        // Both candidates are finalized against the same decision time. This is important: fresh loading does
        // not run while the calculator is producing stickers, while an already accepted persistent transaction does.
        var plannedAt = FcsRuntimeClock.Now;
        left?.FinalizeTiming(snapshotAt, plannedAt);
        right?.FinalizeTiming(snapshotAt, plannedAt);

        var chosen = ChooseCandidate(left, right);
        if (chosen == null)
        {
            var leftOccupied = _fcs.PlanExecutor.GetPlan(LeftRight.Left) != null;
            var rightOccupied = _fcs.PlanExecutor.GetPlan(LeftRight.Right) != null;
            var transient = leftOccupied || rightOccupied
                            || IsTransient(leftLoading.PhysicalState)
                            || IsTransient(rightLoading.PhysicalState);

            var detail = $"no viable gun in planning snapshot; Left={leftReason}; Right={rightReason}";
            completed(null, transient ? "WAIT: " + detail : detail);
            yield break;
        }

        task.bulletType = chosen.Shell;
        task.chargeCount = chosen.Charge;
        task.elevation = chosen.Elevation;

        var plan = new FirePlan(task, chosen.Side, chosen.Shell, chosen.Charge, chosen.Elevation, task.angel,
            plannedAt, chosen.EtaKnown, chosen.EstimatedLocalReadyAt, chosen.AzimuthSeconds,
            chosen.AlignmentScore, _fcs.FirePriority.Generation);

        MelonLogger.Msg(
            $"[FCS Plan] T{task.targetId}: committed {plan.Label}, E={plan.Elevation:F2}, Az={plan.Azimuth:F2}, " +
            $"ETA={(plan.EtaKnown ? Math.Max(0f, plan.EstimatedReadyAt - plannedAt).ToString("F1") : "unknown")}s, " +
            $"load={chosen.LoadLabel}");

        completed(plan, "");
    }

    private IEnumerator BuildCandidate(
        ArtilleryTask task,
        LeftRight side,
        GunPhysicalState physical,
        LoadingSnapshot loading,
        float currentAzimuth,
        Dictionary<(BulletType Shell, int Charge), BallisticSolveResult> ballisticCache,
        Action<FirePlanCandidate?> setResult,
        Action<string> setReason)
    {
        setResult(null);

        if (!loading.IsBound)
        {
            setReason("persistent loading system unbound");
            yield break;
        }

        if (!TryResolveRound(task, loading, out var shell, out var charge, out var loadKnown,
                out var loadAlreadyRunning, out var loadSeconds, out var loadLabel, out var resolveReason))
        {
            setReason(resolveReason);
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

        setResult(new FirePlanCandidate(side, shell, charge, elevation, loadKnown, loadAlreadyRunning,
            alignmentScore, loadKnown ? loadSeconds : 0f, elevationSeconds, azimuthSeconds, loadLabel));
        setReason("viable");
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
