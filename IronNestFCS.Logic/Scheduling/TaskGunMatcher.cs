using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Stateless task-to-gun matcher. FirePlanner supplies only side-effect-free eligibility edges; this class
/// chooses the best non-conflicting assignment and never touches the physical ballistic calculator.
/// </summary>
internal static class TaskGunMatcher
{
    public static IReadOnlyList<TaskGunAssignment> Match(
        IReadOnlyList<TaskPlanningResult> tasks,
        ISet<(ArtilleryTask Task, LeftRight Side)>? excludedEdges = null)
    {
        List<TaskGunAssignment>? best = null;

        foreach (var task in tasks)
        {
            if (IsAllowed(task, task.LeftCandidate, excludedEdges))
                Consider(new List<TaskGunAssignment> { new(task, task.LeftCandidate!) }, ref best);
            if (IsAllowed(task, task.RightCandidate, excludedEdges))
                Consider(new List<TaskGunAssignment> { new(task, task.RightCandidate!) }, ref best);
        }

        // Two guns are the architectural maximum. Enumerate only the two possible side slots while
        // requiring distinct tasks; eligibility has already removed impossible Task x Gun edges.
        foreach (var leftTask in tasks)
        {
            if (!IsAllowed(leftTask, leftTask.LeftCandidate, excludedEdges))
                continue;

            foreach (var rightTask in tasks)
            {
                if (ReferenceEquals(leftTask.Task, rightTask.Task)
                    || !IsAllowed(rightTask, rightTask.RightCandidate, excludedEdges))
                {
                    continue;
                }

                Consider(
                    new List<TaskGunAssignment>
                    {
                        new(leftTask, leftTask.LeftCandidate!),
                        new(rightTask, rightTask.RightCandidate!),
                    },
                    ref best);
            }
        }

        if (best != null)
            return best;
        return Array.Empty<TaskGunAssignment>();
    }

    private static bool IsAllowed(
        TaskPlanningResult planning,
        TaskGunCandidate? candidate,
        ISet<(ArtilleryTask Task, LeftRight Side)>? excludedEdges)
    {
        return candidate != null
               && (excludedEdges == null || !excludedEdges.Contains((planning.Task, candidate.Side)));
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

        // Charge fit stays ahead of soft timing costs: preserve scarce range capability whenever the same
        // number of tasks can be covered with a tighter charge-to-range fit.
        var aMaxChargeExcess = a.Max(ChargeExcess);
        var bMaxChargeExcess = b.Max(ChargeExcess);
        if (aMaxChargeExcess != bMaxChargeExcess)
            return aMaxChargeExcess.CompareTo(bMaxChargeExcess);

        var aTotalChargeExcess = a.Sum(ChargeExcess);
        var bTotalChargeExcess = b.Sum(ChargeExcess);
        if (aTotalChargeExcess != bTotalChargeExcess)
            return aTotalChargeExcess.CompareTo(bTotalChargeExcess);

        // Pre-match ETA contains loading + shared azimuth only. Elevation is deliberately absent because
        // obtaining it would invoke the physical calculator and create a sticker before the match is final.
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

        var aAzimuth = a.Sum(x => x.Candidate.AzimuthScore);
        var bAzimuth = b.Sum(x => x.Candidate.AzimuthScore);
        if (Math.Abs(aAzimuth - bAzimuth) > FireReadyEstimator.AlignmentTieTolerance)
            return aAzimuth.CompareTo(bAzimuth);

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
    public TaskGunCandidate Candidate { get; }

    public TaskGunAssignment(TaskPlanningResult planning, TaskGunCandidate candidate)
    {
        Planning = planning;
        Candidate = candidate;
    }
}
