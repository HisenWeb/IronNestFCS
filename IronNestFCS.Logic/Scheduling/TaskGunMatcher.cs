using IronNestFCS.Logic.FCS;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Stateless task-to-gun matcher. Eligibility is decided by FirePlanner; this class only chooses the
/// best non-conflicting assignment from already-eligible Task x Gun candidates.
/// </summary>
internal static class TaskGunMatcher
{
    public static IReadOnlyList<TaskGunAssignment> Match(IReadOnlyList<TaskPlanningResult> tasks)
    {
        List<TaskGunAssignment>? best = null;

        foreach (var task in tasks)
        {
            if (task.LeftCandidate != null)
                Consider(new List<TaskGunAssignment> { new(task, task.LeftCandidate) }, ref best);
            if (task.RightCandidate != null)
                Consider(new List<TaskGunAssignment> { new(task, task.RightCandidate) }, ref best);
        }

        // Two guns are the architectural maximum. Enumerate only the two possible side slots while
        // requiring distinct tasks; eligibility has already removed impossible Task x Gun edges.
        foreach (var leftTask in tasks)
        {
            if (leftTask.LeftCandidate == null)
                continue;

            foreach (var rightTask in tasks)
            {
                if (ReferenceEquals(leftTask.Task, rightTask.Task) || rightTask.RightCandidate == null)
                    continue;

                Consider(
                    new List<TaskGunAssignment>
                    {
                        new(leftTask, leftTask.LeftCandidate),
                        new(rightTask, rightTask.RightCandidate),
                    },
                    ref best);
            }
        }

        if (best != null)
            return best;
        return Array.Empty<TaskGunAssignment>();
    }

    private static void Consider(List<TaskGunAssignment> candidate, ref List<TaskGunAssignment>? best)
    {
        if (best == null || Compare(candidate, best) < 0)
            best = candidate;
    }

    // Negative means a is the better solution.
    private static int Compare(IReadOnlyList<TaskGunAssignment> a, IReadOnlyList<TaskGunAssignment> b)
    {
        // Hard priority #1: fill as many currently available gun slots as possible.
        if (a.Count != b.Count)
            return b.Count.CompareTo(a.Count);

        // Charge fit is deliberately ahead of ETA/alignment. A higher charge that is not required by the
        // target is a scarcer range resource and should be preserved when another complete assignment can.
        var aMaxChargeExcess = a.Max(ChargeExcess);
        var bMaxChargeExcess = b.Max(ChargeExcess);
        if (aMaxChargeExcess != bMaxChargeExcess)
            return aMaxChargeExcess.CompareTo(bMaxChargeExcess);

        var aTotalChargeExcess = a.Sum(ChargeExcess);
        var bTotalChargeExcess = b.Sum(ChargeExcess);
        if (aTotalChargeExcess != bTotalChargeExcess)
            return aTotalChargeExcess.CompareTo(bTotalChargeExcess);

        var aAllEtaKnown = a.All(x => x.Candidate.EtaKnown);
        var bAllEtaKnown = b.All(x => x.Candidate.EtaKnown);
        if (aAllEtaKnown && bAllEtaKnown)
        {
            var aMaxReady = a.Max(x => x.Candidate.EstimatedReadyAt);
            var bMaxReady = b.Max(x => x.Candidate.EstimatedReadyAt);
            if (Math.Abs(aMaxReady - bMaxReady) > FireReadyEstimator.EtaTieToleranceSeconds)
                return aMaxReady.CompareTo(bMaxReady);

            var aTotalReady = a.Sum(x => x.Candidate.EstimatedReadyAt);
            var bTotalReady = b.Sum(x => x.Candidate.EstimatedReadyAt);
            if (Math.Abs(aTotalReady - bTotalReady) > FireReadyEstimator.EtaTieToleranceSeconds)
                return aTotalReady.CompareTo(bTotalReady);
        }

        // Keep the existing planner philosophy for unknown ETA: use current alignment as the fallback cost.
        var aAlignment = a.Sum(x => x.Candidate.AlignmentScore);
        var bAlignment = b.Sum(x => x.Candidate.AlignmentScore);
        if (Math.Abs(aAlignment - bAlignment) > FireReadyEstimator.AlignmentTieTolerance)
            return aAlignment.CompareTo(bAlignment);

        return 0;
    }

    private static int ChargeExcess(TaskGunAssignment assignment)
    {
        var minimum = BallisticCalculator.MinimumCharge(assignment.Planning.Task.distance);
        return Math.Max(0, assignment.Candidate.Charge - minimum);
    }
}

internal sealed class TaskGunAssignment
{
    public TaskPlanningResult Planning { get; }
    public FirePlanCandidate Candidate { get; }

    public TaskGunAssignment(TaskPlanningResult planning, FirePlanCandidate candidate)
    {
        Planning = planning;
        Candidate = candidate;
    }
}
