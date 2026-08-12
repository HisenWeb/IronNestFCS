// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Owns only task queue/history and serial admission into planning rounds. Queueing never reads gun state.
/// Pending tasks remain owned by this queue until FirePlanExecutor accepts a concrete FirePlan.
/// </summary>
internal sealed class TaskDispatcher
{
    private const int RecentTaskLimit = 20;
    private const float PhysicalRetryPollSeconds = 0.25f;

    private readonly FSC _fcs;
    private readonly Queue<ArtilleryTask> _taskQueue = new();
    private readonly Queue<ArtilleryTask> _recentTasks = new();

    private bool _planning;
    private bool _dispatchRequested;
    private bool _physicalRetryWaiting;

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
        // Remember only the edge; the next round still re-reads current queue/resource state from scratch.
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
    /// Scan pending tasks in queue order and fill every currently free FirePlan slot that can be planned.
    /// Physical recovery remains authoritative in FirePlanner; an empty plan slot does not imply that the
    /// underlying gun is mechanically ready.
    /// </summary>
    private IEnumerator PlanPendingTasks()
    {
        var attempted = new HashSet<ArtilleryTask>();
        var admittedAny = false;
        var deferredForPhysicalState = false;

        while (_fcs.PlanExecutor.HasFreeGun)
        {
            var task = FindNextUnattempted(attempted);
            if (task == null)
                break;

            attempted.Add(task);

            FirePlan? plan = null;
            var reason = "";
            yield return _fcs.Planner.BuildPlan(task, (result, detail) =>
            {
                plan = result;
                reason = detail;
            });

            if (plan == null)
            {
                task.progress = Progress.Pending;
                task.failureReason = "";
                if (reason.StartsWith("WAIT:", StringComparison.Ordinal))
                    deferredForPhysicalState = true;

                MelonLogger.Msg(
                    $"[FCS Dispatch] T{task.targetId} remains pending; no FirePlan from current snapshot: " +
                    (string.IsNullOrWhiteSpace(reason) ? "no viable gun" : reason));
                continue;
            }

            if (!_fcs.PlanExecutor.AddPlan(plan, out var addReason))
            {
                task.progress = Progress.Pending;
                task.failureReason = "";
                MelonLogger.Warning(
                    $"[FCS Dispatch] T{task.targetId} FirePlan was not admitted and remains pending: {addReason}");
                continue;
            }

            if (!RemovePendingTask(task))
                MelonLogger.Warning($"[FCS Dispatch] admitted T{task.targetId} was no longer present in pending queue");

            admittedAny = true;
            MelonLogger.Msg($"[FCS Dispatch] admitted T{task.targetId}; pending={_taskQueue.Count}");
        }

        _planning = false;

        if (!admittedAny && attempted.Count > 0)
            MelonLogger.Msg($"[FCS Dispatch] planning round deferred {attempted.Count} pending task(s)");

        // A Plan used to stay alive through WaitBackToIdle and that coroutine provided the recovery-complete
        // dispatch edge. Plans now finish at the physical shot, so preserve event-driven dispatch with one
        // temporary waiter only while planning was explicitly deferred by transient physical state.
        if (deferredForPhysicalState && _taskQueue.Count > 0)
            EnsurePhysicalRetryWait();

        // Consume one coalesced trigger that arrived during this planning round. TryDispatch() sets _planning
        // synchronously before starting the next coroutine, so EvaluateScheduling() below can still see that a
        // possible pairing round is active instead of prematurely single-committing an admitted plan.
        if (_dispatchRequested)
        {
            _dispatchRequested = false;
            TryDispatch();
        }

        _fcs.PlanExecutor.EvaluateScheduling();
    }

    private void EnsurePhysicalRetryWait()
    {
        if (_physicalRetryWaiting)
            return;

        _physicalRetryWaiting = true;
        _fcs.TrackCoroutine(WaitForPhysicalPlanningOpportunity());
    }

    private IEnumerator WaitForPhysicalPlanningOpportunity()
    {
        try
        {
            while (_taskQueue.Count > 0)
            {
                yield return FcsRuntimeClock.WaitUntilFocused();

                if (_fcs.PlanExecutor.HasFreeGun && HasPlannableFreeSide())
                    break;

                yield return FcsRuntimeClock.WaitForSeconds(PhysicalRetryPollSeconds);
            }
        }
        finally
        {
            _physicalRetryWaiting = false;
        }

        if (_taskQueue.Count > 0)
        {
            MelonLogger.Msg("[FCS Dispatch] physical recovery opened a planning opportunity; retrying pending tasks");
            TryDispatch();
        }
    }

    private bool HasPlannableFreeSide()
    {
        if (_fcs.PlanExecutor.GetPlan(LeftRight.Left) == null
            && IsPlannable(_fcs.Loading.GetSnapshot(GunSide.Left).PhysicalState))
        {
            return true;
        }

        return _fcs.PlanExecutor.GetPlan(LeftRight.Right) == null
               && IsPlannable(_fcs.Loading.GetSnapshot(GunSide.Right).PhysicalState);
    }

    private static bool IsPlannable(LoadingPhysicalState state) =>
        state == LoadingPhysicalState.EmptyReady
        || state == LoadingPhysicalState.ShellLoaded
        || state == LoadingPhysicalState.LoadedReady;

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
