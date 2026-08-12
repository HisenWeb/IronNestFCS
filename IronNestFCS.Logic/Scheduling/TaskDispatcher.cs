// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Owns task queue/history and serial admission into planning rounds. A planning round now evaluates all
/// currently pending tasks against one gun/loading snapshot, then admits the best non-conflicting match.
/// </summary>
internal sealed class TaskDispatcher
{
    private const int RecentTaskLimit = 20;
    private const float PhysicalRetryPollSeconds = 0.25f;
    private const int LeftPhysicalRetryBit = 1;
    private const int RightPhysicalRetryBit = 2;

    private readonly FSC _fcs;
    private readonly Queue<ArtilleryTask> _taskQueue = new();
    private readonly Queue<ArtilleryTask> _recentTasks = new();

    private bool _planning;
    private bool _dispatchRequested;
    private bool _physicalRetryWaiting;
    private int _physicalRetryMask;

    public int PendingCount => _taskQueue.Count;

    // FirePlanExecutor uses this to decide whether a lone plan should wait for a possible partner.
    // Only an active planning round can still produce that partner; deferred pending tasks alone cannot.
    public bool HasPendingOrPlanning => _planning;

    public Queue<ArtilleryTask> QueueSnapshot => new(_taskQueue);
    public Queue<ArtilleryTask> RecentSnapshot => new(_recentTasks);

    public int CompletedTaskCount { get; private set; }
    public int SuccessfulTaskCount { get; private set; }
    public int FailedTaskCount { get; private set; }

    public TaskDispatcher(FSC fcs)
    {
        _fcs = fcs;
    }

    public void DisposeState()
    {
        _taskQueue.Clear();
        _recentTasks.Clear();
        _planning = false;
        _dispatchRequested = false;
        _physicalRetryWaiting = false;
        _physicalRetryMask = 0;
    }

    public void EnqueueTask(ArtilleryTask task)
    {
        task.progress = Progress.Pending;
        task.pendingHint = PendingHint.None;
        task.startedAt = FcsRuntimeClock.Now;
        task.completedAt = 0f;
        task.failureReason = "";
        task.chargeCount = 0;
        task.elevation = 0f;
        task.dispatchExcludedGunMask = 0;

        // Intent-only queue: no gun/loading read here.
        _taskQueue.Enqueue(task);
        MelonLogger.Msg($"[FCS Dispatch] queued T{task.targetId}; pending={_taskQueue.Count}");
        TryDispatch();
    }

    public void TryDispatch()
    {
        // Planning is serialized, but a trigger that arrives while a round is running must not be lost.
        // Remember only the edge; the current round will also pick up newly queued tasks while it is scanning.
        if (_planning)
        {
            _dispatchRequested = true;
            return;
        }

        if (!FcsRuntimeClock.IsFocused
            || _taskQueue.Count == 0
            || !_fcs.PlanExecutor.HasFreeGun)
            return;

        _dispatchRequested = false;
        _planning = true;
        _fcs.TrackCoroutine(PlanPendingTasks());
    }

    /// <summary>
    /// One match round captures gun/loading state once, evaluates every pending task against that same snapshot,
    /// then chooses a maximum-cardinality assignment. Charge fit is resolved before ETA/alignment soft costs.
    /// </summary>
    private IEnumerator PlanPendingTasks()
    {
        var snapshot = _fcs.Planner.CaptureSnapshot();
        var attempted = new HashSet<ArtilleryTask>();
        var planningResults = new List<TaskPlanningResult>();
        var deferredPhysicalMask = 0;
        var admittedAny = false;

        // Do not remove tasks while building the matrix. A task queued during this coroutine is naturally picked
        // up by FindNextUnattempted and joins the same planning window if it arrives before the scan closes.
        while (true)
        {
            var task = FindNextUnattempted(attempted);
            if (task == null)
                break;

            attempted.Add(task);

            TaskPlanningResult? result = null;
            yield return _fcs.Planner.BuildCandidates(task, snapshot, value => result = value);
            if (result == null)
            {
                task.progress = Progress.Pending;
                task.failureReason = "";
                MelonLogger.Warning($"[FCS Dispatch] T{task.targetId}: planner returned no candidate result");
                continue;
            }

            planningResults.Add(result);

            if (!result.HasCandidate)
            {
                task.pendingHint = result.PendingHint;
                task.progress = Progress.Pending;
                task.failureReason = "";

                if (result.ShouldWait)
                    deferredPhysicalMask |= CurrentTransientFreeSideMask();

                MelonLogger.Msg(
                    $"[FCS Dispatch] T{task.targetId} remains pending; {result.FailureDetail}");
            }
        }

        var decisionAt = FcsRuntimeClock.Now;
        foreach (var result in planningResults)
            result.FinalizeTiming(snapshot.SnapshotAt, decisionAt);

        LogEligibilityMatrix(planningResults);

        var assignments = TaskGunMatcher.Match(planningResults);
        var selectedTasks = new HashSet<ArtilleryTask>();

        if (assignments.Count > 0)
        {
            MelonLogger.Msg(
                $"[FCS Match] selected {assignments.Count} assignment(s): " +
                string.Join(", ", assignments.Select(DescribeAssignment)));
        }

        foreach (var assignment in assignments)
        {
            var task = assignment.Planning.Task;
            var plan = _fcs.Planner.CreatePlan(assignment.Planning, assignment.Candidate, decisionAt);

            if (!_fcs.PlanExecutor.AddPlan(plan, out var addReason))
            {
                task.progress = Progress.Pending;
                task.pendingHint = PendingHint.None;
                task.failureReason = "";
                MelonLogger.Warning(
                    $"[FCS Dispatch] T{task.targetId} matched FirePlan was not admitted and remains pending: {addReason}");
                continue;
            }

            if (!RemovePendingTask(task))
                MelonLogger.Warning($"[FCS Dispatch] admitted T{task.targetId} was no longer present in pending queue");

            selectedTasks.Add(task);
            admittedAny = true;
            MelonLogger.Msg($"[FCS Dispatch] admitted T{task.targetId}; pending={_taskQueue.Count}");
        }

        // Every evaluated but unselected task remains pending. A valid candidate can lose only because the current
        // free slots were consumed by a higher-quality complete match; that is not a failure and needs no hint.
        foreach (var result in planningResults)
        {
            if (selectedTasks.Contains(result.Task))
                continue;

            result.Task.progress = Progress.Pending;
            result.Task.failureReason = "";
            if (result.HasCandidate)
                result.Task.pendingHint = PendingHint.None;
        }

        _planning = false;

        if (!admittedAny && attempted.Count > 0)
            MelonLogger.Msg($"[FCS Dispatch] planning round deferred {attempted.Count} pending task(s)");

        // Plans finish at physical shot, so preserve event-driven dispatch with one temporary waiter only for
        // concrete free side(s) currently blocked by physical recovery.
        if (deferredPhysicalMask != 0 && _taskQueue.Count > 0)
            EnsurePhysicalRetryWait(deferredPhysicalMask);

        // Consume one coalesced trigger that arrived during this planning round. TryDispatch() sets _planning
        // synchronously before starting the next coroutine, so EvaluateScheduling() can still see a possible
        // partner-producing round instead of prematurely single-committing an admitted plan.
        if (_dispatchRequested)
        {
            _dispatchRequested = false;
            TryDispatch();
        }

        _fcs.PlanExecutor.EvaluateScheduling();
    }

    private void LogEligibilityMatrix(IReadOnlyList<TaskPlanningResult> results)
    {
        foreach (var result in results)
        {
            var left = DescribeCandidate(result.LeftCandidate, result.LeftReason);
            var right = DescribeCandidate(result.RightCandidate, result.RightReason);
            MelonLogger.Msg($"[FCS Match] T{result.Task.targetId}: Left={left}; Right={right}");
        }
    }

    private static string DescribeCandidate(FirePlanCandidate? candidate, string failureReason)
    {
        if (candidate != null)
        {
            var eta = candidate.EtaKnown
                ? Math.Max(0f, candidate.EstimatedReadyAt - FcsRuntimeClock.Now).ToString("F1") + "s"
                : "unknown";
            return $"eligible {candidate.Shell.DisplayName()} C{candidate.Charge} ETA={eta}";
        }

        return string.IsNullOrWhiteSpace(failureReason) ? "ineligible" : failureReason;
    }

    private static string DescribeAssignment(TaskGunAssignment assignment)
    {
        var task = assignment.Planning.Task;
        var candidate = assignment.Candidate;
        var minimumCharge = BallisticCalculator.MinimumCharge(task.distance);
        var chargeExcess = Math.Max(0, candidate.Charge - minimumCharge);
        return $"T{task.targetId}->{candidate.Side} {candidate.Shell.DisplayName()} C{candidate.Charge} " +
               $"(chargeExcess={chargeExcess})";
    }

    private int CurrentTransientFreeSideMask()
    {
        var mask = 0;
        if (_fcs.PlanExecutor.GetPlan(LeftRight.Left) == null
            && IsTransient(_fcs.Loading.GetSnapshot(GunSide.Left).PhysicalState))
        {
            mask |= LeftPhysicalRetryBit;
        }

        if (_fcs.PlanExecutor.GetPlan(LeftRight.Right) == null
            && IsTransient(_fcs.Loading.GetSnapshot(GunSide.Right).PhysicalState))
        {
            mask |= RightPhysicalRetryBit;
        }

        return mask;
    }

    private void EnsurePhysicalRetryWait(int sideMask)
    {
        _physicalRetryMask |= sideMask;
        if (_physicalRetryWaiting)
            return;

        _physicalRetryWaiting = true;
        _fcs.TrackCoroutine(WaitForPhysicalPlanningOpportunity());
    }

    private IEnumerator WaitForPhysicalPlanningOpportunity()
    {
        var shouldRetry = false;
        try
        {
            while (_taskQueue.Count > 0 && _physicalRetryMask != 0)
            {
                yield return FcsRuntimeClock.WaitUntilFocused();

                if ((_physicalRetryMask & LeftPhysicalRetryBit) != 0)
                {
                    if (_fcs.PlanExecutor.GetPlan(LeftRight.Left) != null)
                    {
                        _physicalRetryMask &= ~LeftPhysicalRetryBit;
                    }
                    else if (IsPlannable(_fcs.Loading.GetSnapshot(GunSide.Left).PhysicalState))
                    {
                        shouldRetry = true;
                        break;
                    }
                }

                if ((_physicalRetryMask & RightPhysicalRetryBit) != 0)
                {
                    if (_fcs.PlanExecutor.GetPlan(LeftRight.Right) != null)
                    {
                        _physicalRetryMask &= ~RightPhysicalRetryBit;
                    }
                    else if (IsPlannable(_fcs.Loading.GetSnapshot(GunSide.Right).PhysicalState))
                    {
                        shouldRetry = true;
                        break;
                    }
                }

                if (!shouldRetry)
                    yield return FcsRuntimeClock.WaitForSeconds(PhysicalRetryPollSeconds);
            }
        }
        finally
        {
            _physicalRetryWaiting = false;
            _physicalRetryMask = 0;
        }

        if (shouldRetry && _taskQueue.Count > 0)
        {
            MelonLogger.Msg("[FCS Dispatch] physical recovery opened a planning opportunity; retrying pending tasks");
            TryDispatch();
        }
    }

    private static bool IsPlannable(LoadingPhysicalState state) =>
        state == LoadingPhysicalState.EmptyReady
        || state == LoadingPhysicalState.ShellLoaded
        || state == LoadingPhysicalState.LoadedReady;

    private static bool IsTransient(LoadingPhysicalState state) =>
        state == LoadingPhysicalState.Recovering
        || state == LoadingPhysicalState.PostShotRecovery
        || state == LoadingPhysicalState.Unknown
        || state == LoadingPhysicalState.Unbound;

    private ArtilleryTask? FindNextUnattempted(HashSet<ArtilleryTask> attempted)
    {
        foreach (var task in _taskQueue)
        {
            if (!attempted.Contains(task))
                return task;
        }

        return null;
    }

    private bool RemovePendingTask(ArtilleryTask target)
    {
        var items = _taskQueue.ToArray();
        _taskQueue.Clear();

        var removed = false;
        foreach (var task in items)
        {
            if (!removed && ReferenceEquals(task, target))
            {
                removed = true;
                continue;
            }

            _taskQueue.Enqueue(task);
        }

        return removed;
    }

    public void RecordTaskResult(ArtilleryTask task)
    {
        task.completedAt = FcsRuntimeClock.Now;
        CompletedTaskCount++;
        if (task.progress == Progress.Finished)
            SuccessfulTaskCount++;
        else if (task.progress == Progress.Failed)
            FailedTaskCount++;

        _recentTasks.Enqueue(task);
        while (_recentTasks.Count > RecentTaskLimit)
            _recentTasks.Dequeue();
        _fcs.SceneInteractor.TaskFinished(task);
    }
}
