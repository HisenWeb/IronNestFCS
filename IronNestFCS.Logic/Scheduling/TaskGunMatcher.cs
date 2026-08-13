using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Stateless task-to-gun matcher. FirePlanner supplies only side-effect-free eligibility edges; this class
/// chooses the best non-conflicting assignment and never touches the physical ballistic calculator.
/// Scarce charge/range capability is protected before pending task order; timing/alignment remain soft costs.
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

        // Narrow single-task exception: when the same task can use either an already-loaded gun or a completely
        // empty gun, consume the compatible LoadedReady round instead of starting a redundant fresh load.
        // This never participates in multi-task set selection, so the existing charge/range protection is intact.
        var loadedReadyPreference = CompareSingleTaskLoadedReadyOverEmptyReady(a, b);
        if (loadedReadyPreference != 0)
            return loadedReadyPreference;

        // Hard priority #2: protect scarce charge/range capability. A short-range task should prefer the lower
        // charge when that leaves a higher-charge gun available for a task that actually needs the extra range.
        var aMaxChargeExcess = a.Max(ChargeExcess);
        var bMaxChargeExcess = b.Max(ChargeExcess);
        if (aMaxChargeExcess != bMaxChargeExcess)
            return aMaxChargeExcess.CompareTo(bMaxChargeExcess);

        var aTotalChargeExcess = a.Sum(ChargeExcess);
        var bTotalChargeExcess = b.Sum(ChargeExcess);
        if (aTotalChargeExcess != bTotalChargeExcess)
            return aTotalChargeExcess.CompareTo(bTotalChargeExcess);

        // Hard priority #3: once equally good charge-resource matches are known, preserve dispatcher queue order.
        // A later task may bypass an older one only when eligibility/cardinality/charge fit make that necessary.
        var taskPriority = CompareTaskPriority(a, b, queueRanks);
        if (taskPriority != 0)
            return taskPriority;

        // From here on both solutions contain the same pending task set with the same charge fit. Timing and
        // alignment only decide which gun each already-selected task should use; they cannot reorder targets.

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

    private static int CompareSingleTaskLoadedReadyOverEmptyReady(
        IReadOnlyList<TaskGunAssignment> a,
        IReadOnlyList<TaskGunAssignment> b)
    {
        if (a.Count != 1
            || b.Count != 1
            || !ReferenceEquals(a[0].Planning.Task, b[0].Planning.Task))
        {
            return 0;
        }

        var aLoadedReady = IsLoadedReadyCandidate(a[0].Candidate);
        var bLoadedReady = IsLoadedReadyCandidate(b[0].Candidate);
        var aEmptyReady = IsEmptyReadyCandidate(a[0].Candidate);
        var bEmptyReady = IsEmptyReadyCandidate(b[0].Candidate);

        if (aLoadedReady && bEmptyReady)
            return -1;
        if (bLoadedReady && aEmptyReady)
            return 1;
        return 0;
    }

    // FirePlanner's canonical candidate encoding is exact for these two physical states:
    // LoadedReady => known zero remaining load; EmptyReady => known fresh-load baseline.
    private static bool IsLoadedReadyCandidate(TaskGunCandidate candidate)
    {
        return candidate.EtaKnown
               && !candidate.LoadAlreadyRunning
               && candidate.LoadSeconds == 0f;
    }

    private static bool IsEmptyReadyCandidate(TaskGunCandidate candidate)
    {
        return candidate.EtaKnown
               && !candidate.LoadAlreadyRunning
               && candidate.LoadSeconds == FireReadyEstimator.FreshLoadReadySeconds;
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
