// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;
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

    private readonly FSC _fcs;
    private readonly Queue<ArtilleryTask> _taskQueue = new();
    private readonly Queue<ArtilleryTask> _recentTasks = new();

    private bool _planning;

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
    }

    public void EnqueueTask(ArtilleryTask task)
    {
        task.progress = Progress.Pending;
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
        if (!FcsRuntimeClock.IsFocused
            || _planning
            || _taskQueue.Count == 0
            || !_fcs.PlanExecutor.HasFreeGun)
            return;

        _planning = true;
        _fcs.TrackCoroutine(PlanPendingTasks());
    }

    /// <summary>
    /// Scan pending tasks in queue order and fill every currently free gun slot that can be planned.
    /// A task that cannot produce a FirePlan from the current physical snapshot remains Pending in place;
    /// later tasks are still allowed to use a gun that is currently compatible with them.
    /// </summary>
    private IEnumerator PlanPendingTasks()
    {
        var attempted = new HashSet<ArtilleryTask>();
        var admittedAny = false;

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

        _fcs.PlanExecutor.EvaluateScheduling();
    }

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
