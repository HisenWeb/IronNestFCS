using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Stateless task-to-gun matcher. FirePlanner supplies only side-effect-free eligibility edges; this class
/// chooses the best non-conflicting assignment and never touches the physical ballistic calculator.
/// Pending task order is preserved before soft gun-assignment costs are considered.
/// </summary>
internal static class TaskGunMatcher
{
    public static IReadOnlyList<TaskGunAssignment> Match(
        IReadOnlyList<TaskPlanningResult> tasks,
        ISet<(ArtilleryTask Task, LeftRight Side)>? excludedEdges = null)
    {
        List<TaskGunAssignment>? best = null;
        var queueRanks = BuildQueueRanks(tasks);

        foreach (var task in tasks)
        {
            if (IsAllowed(task, task.LeftCandidate, excludedEdges))
                Consider(new List<TaskGunAssignment> { new(task, task.LeftCandidate!) }, queueRanks, ref best);
            if (IsAllowed(task, task.RightCandidate, excludedEdges))
                Consider(new List<TaskGunAssignment> { new(task, task.RightCandidate!) }, queueRanks, ref best);
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
                    queueRanks,
                    ref best);
            }
        }

        if (best != null)
            return best;
        return Array.Empty<TaskGunAssignment>();
    }

    private static Dictionary<TaskPlanningResult, int> BuildQueueRanks(IReadOnlyList<TaskPlanningResult> tasks)
    {
        var ranks = new Dictionary<TaskPlanningResult, int>(tasks.Count);
        for (var i = 0; i < tasks.Count; i++)
            ranks[tasks[i]] = i;
        return ranks;
    }

    private static bool IsAllowed(
        TaskPlanningResult planning,
        TaskGunCandidate? candidate,
        ISet<(ArtilleryTask Task, LeftRight Side)>? excludedEdges)
    {
        return candidate != null
               && (excludedEdges == null || !excludedEdges.Contains((planning.Task, candidate.Side)));
    }

    private static void Consider(
        List<TaskGunAssignment> candidate,
        Dictionary<TaskPlanningResult, int> queueRanks,
        ref List<TaskGunAssignment>? best)
    {
        if (best == null || Compare(candidate, best, queueRanks) < 0)
            best = candidate;
    }

    // Negative means a is the better solution.
    private static int Compare(
        IReadOnlyList<TaskGunAssignment> a,
        IReadOnlyList<TaskGunAssignment> b,
        Dictionary<TaskPlanningResult, int> queueRanks)
    {
        // Hard priority #1: fill as many currently available gun slots as possible.
        if (a.Count != b.Count)
            return b.Count.CompareTo(a.Count);

        // Hard priority #2: preserve dispatcher queue order among equally complete feasible matches.
        // Eligibility already reflects the current physical loading state, so a later task may bypass an older
        // one only when the older task cannot participate in an equally complete feasible assignment.
        var taskPriority = CompareTaskPriority(a, b, queueRanks);
        if (taskPriority != 0)
            return taskPriority;

        // From here on both solutions contain the same pending task set. Soft costs only decide which gun each
        // already-selected task should use; they must never change which tactical task is admitted first.
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

        // AzimuthSeconds already uses FireReadyEstimator's canonical signed-bearing conversion. Convert it
        // back to degrees for the existing alignment tolerance instead of trusting the legacy AzimuthScore field.
        var aAzimuth = a.Sum(CorrectAzimuthScore);
        var bAzimuth = b.Sum(CorrectAzimuthScore);
        if (Math.Abs(aAzimuth - bAzimuth) > FireReadyEstimator.AlignmentTieTolerance)
            return aAzimuth.CompareTo(bAzimuth);

        return 0;
    }

    private static int CompareTaskPriority(
        IReadOnlyList<TaskGunAssignment> a,
        IReadOnlyList<TaskGunAssignment> b,
        Dictionary<TaskPlanningResult, int> queueRanks)
    {
        var aRanks = a.Select(x => queueRanks[x.Planning]).OrderBy(x => x).ToArray();
        var bRanks = b.Select(x => queueRanks[x.Planning]).OrderBy(x => x).ToArray();

        for (var i = 0; i < aRanks.Length; i++)
        {
            if (aRanks[i] != bRanks[i])
                return aRanks[i].CompareTo(bRanks[i]);
        }

        return 0;
    }

    private static float CorrectAzimuthScore(TaskGunAssignment assignment)
    {
        return assignment.Candidate.AzimuthSeconds * FireReadyEstimator.AzimuthSlewDegreesPerSecond;
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
