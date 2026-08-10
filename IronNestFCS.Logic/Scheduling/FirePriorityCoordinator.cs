using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Localization;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// One-shot FirePlan ordering. A plan is compared at most once. This coordinator owns no turret lock,
/// physical phase gate, provisional winner, or dynamic re-arbitration.
/// </summary>
internal sealed class FirePriorityCoordinator
{
    private int _generation;
    private string _statusText = FcsLocalization.T("射击顺序：未提交", "Firing order: not committed");
    private string _leftDetail = "";
    private string _rightDetail = "";

    public int Generation => _generation;
    public string StatusText => _statusText;
    public string LeftDetail => _leftDetail;
    public string RightDetail => _rightDetail;

    public void Reset()
    {
        _generation++;
        _statusText = FcsLocalization.T("射击顺序：未提交（已重置）", "Firing order: not committed (reset)");
        _leftDetail = "";
        _rightDetail = "";
    }

    public FirePlan ComparePair(FirePlan a, FirePlan b)
    {
        if (a.Compared || b.Compared)
            throw new InvalidOperationException("FirePlan comparison is one-shot; compared plans must never be compared again.");

        FirePlan first;
        FirePlan second;
        string reason;

        if (a.EtaKnown && b.EtaKnown)
        {
            // Neither unpaired plan owns shared azimuth yet. Evaluate both from the same comparison instant,
            // while preserving each plan's fixed planning-snapshot azimuth distance and local-ready estimate.
            var comparisonAt = FcsRuntimeClock.Now;
            var aReadyAt = a.RefreshEstimatedReadyAt(comparisonAt);
            var bReadyAt = b.RefreshEstimatedReadyAt(comparisonAt);
            var delta = aReadyAt - bReadyAt;

            if (Mathf.Abs(delta) <= FireReadyEstimator.EtaTieToleranceSeconds)
            {
                first = a.PlannedAt <= b.PlannedAt ? a : b;
                second = ReferenceEquals(first, a) ? b : a;
                reason = "ETA tie; keeping planning order";
            }
            else
            {
                first = delta < 0f ? a : b;
                second = ReferenceEquals(first, a) ? b : a;
                reason = $"readyAt {first.EstimatedReadyAt:F1} < {second.EstimatedReadyAt:F1}";
            }
        }
        else
        {
            var delta = a.AlignmentScore - b.AlignmentScore;
            if (Mathf.Abs(delta) <= FireReadyEstimator.AlignmentTieTolerance)
            {
                first = a.PlannedAt <= b.PlannedAt ? a : b;
                second = ReferenceEquals(first, a) ? b : a;
                reason = "ETA unavailable and alignment tied; keeping planning order";
            }
            else
            {
                first = delta < 0f ? a : b;
                second = ReferenceEquals(first, a) ? b : a;
                reason = $"ETA unavailable; alignment {first.AlignmentScore:F1} < {second.AlignmentScore:F1}";
            }
        }

        a.Compared = true;
        b.Compared = true;
        UpdateDetails(a, b);
        _statusText = FcsLocalization.T(
            $"射击顺序：T{first.Task.targetId} → T{second.Task.targetId}（一次性比对）",
            $"Firing order: T{first.Task.targetId} → T{second.Task.targetId} (compared once)");
        MelonLogger.Msg($"[FCS Order] paired once: {first.Label} first, {second.Label} second; {reason}");
        return first;
    }

    public void CommitSingle(FirePlan plan, string reason)
    {
        if (!plan.Compared)
            plan.Compared = true;

        if (plan.EtaKnown)
            plan.RefreshEstimatedReadyAt(FcsRuntimeClock.Now);

        UpdateDetails(plan, null);
        var uiReason = FcsLocalization.UiReason(reason);
        _statusText = FcsLocalization.T(
            $"射击顺序：T{plan.Task.targetId} 单独执行（{uiReason}）",
            $"Firing order: T{plan.Task.targetId} single commit ({uiReason})");
        MelonLogger.Msg($"[FCS Order] single committed: {plan.Label}; {FcsLocalization.LogReason(reason)}");
    }

    public void PromoteCommitted(FirePlan plan)
    {
        _statusText = FcsLocalization.T(
            $"射击顺序：T{plan.Task.targetId} 按既定顺序执行",
            $"Firing order: T{plan.Task.targetId} promoted in committed order");
        MelonLogger.Msg($"[FCS Order] promoting previously compared plan without re-compare: {plan.Label}");
    }

    public void MarkWaitingForPair(FirePlan plan)
    {
        UpdateDetails(plan, null);
        _statusText = FcsLocalization.T(
            $"射击顺序：T{plan.Task.targetId} 未比对，等待另一个 FirePlan",
            $"Firing order: T{plan.Task.targetId} unpaired, waiting for another FirePlan");
    }

    public void MarkShot(FirePlan plan)
    {
        _statusText = FcsLocalization.T(
            $"射击顺序：T{plan.Task.targetId} 已击发，推进既定顺序",
            $"Firing order: T{plan.Task.targetId} fired, advancing committed order");
    }

    private void UpdateDetails(FirePlan? a, FirePlan? b)
    {
        _leftDetail = DetailForSide(LeftRight.Left, a, b);
        _rightDetail = DetailForSide(LeftRight.Right, a, b);
    }

    private static string DetailForSide(LeftRight side, FirePlan? a, FirePlan? b)
    {
        var plan = a?.Side == side ? a : b?.Side == side ? b : null;
        if (plan == null)
            return "";

        var sideName = side == LeftRight.Left
            ? FcsLocalization.T("左炮", "Left")
            : FcsLocalization.T("右炮", "Right");

        if (plan.EtaKnown)
        {
            var eta = Math.Max(0f, plan.EstimatedReadyAt - FcsRuntimeClock.Now);
            return FcsLocalization.T(
                $"{sideName} T{plan.Task.targetId}: 计划ETA {eta:F1}s，E{plan.Elevation:F1} / Az{plan.Azimuth:F1}",
                $"{sideName} T{plan.Task.targetId}: planned ETA {eta:F1}s, E{plan.Elevation:F1} / Az{plan.Azimuth:F1}");
        }

        return FcsLocalization.T(
            $"{sideName} T{plan.Task.targetId}: ETA待测，alignment={plan.AlignmentScore:F1}",
            $"{sideName} T{plan.Task.targetId}: ETA unavailable, alignment={plan.AlignmentScore:F1}");
    }
}
