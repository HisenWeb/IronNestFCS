// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;
using IronNestFCS.Logic.FCS;
using MelonLoader;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Owns only task queue/history and serial admission into a planning round. Queueing never reads gun state.
/// FirePlanner chooses the gun and produces the FirePlan; FirePlanExecutor owns the two gun slots.
/// </summary>
internal sealed class TaskDispatcher
{
    private const int RecentTaskLimit = 20;

    private readonly FSC _fcs;
    private readonly Queue<ArtilleryTask> _taskQueue = new();
    private readonly Queue<ArtilleryTask> _recentTasks = new();

    private bool _planning;
    private float _retryNotBefore;

    public int PendingCount => _taskQueue.Count;
    public bool HasPendingOrPlanning => _planning || _taskQueue.Count > 0;
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
        _retryNotBefore = 0f;
    }

    public void EnqueueTask(ArtilleryTask task)
    {
        task.progress = Progress.Pending;
        task.startedAt = FcsRuntimeClock.Now;
        task.completedAt = 0f;
        task.failureReason = "";
        task.chargeCount = 0;
        task.elevation = 0f;

        // Intent-only queue: no gun/loading read here.
        _taskQueue.Enqueue(task);
        MelonLogger.Msg($"[FCS Dispatch] queued T{task.targetId}; pending={_taskQueue.Count}");
        TryDispatch();
    }

    public void TryDispatch()
    {
        if (!FcsRuntimeClock.IsFocused
            || _planning
            || FcsRuntimeClock.Now < _retryNotBefore
            || _taskQueue.Count == 0
            || !_fcs.PlanExecutor.HasFreeGun)
            return;

        var task = _taskQueue.Dequeue();
        _planning = true;
        _fcs.TrackCoroutine(PlanOne(task));
    }

    private IEnumerator PlanOne(ArtilleryTask task)
    {
        FirePlan? plan = null;
        var failureReason = "";

        yield return _fcs.Planner.BuildPlan(task, (result, reason) =>
        {
            plan = result;
            failureReason = reason;
        });

        _planning = false;

        if (plan == null)
        {
            if (failureReason.StartsWith("WAIT:", StringComparison.Ordinal))
            {
                task.progress = Progress.Pending;
                task.failureReason = "";
                PrependTask(task);
                _retryNotBefore = FcsRuntimeClock.Now + 0.5f;
                MelonLogger.Msg($"[FCS Dispatch] T{task.targetId} waiting for a plannable gun snapshot: {failureReason.Substring(5).Trim()}");
                _fcs.PlanExecutor.EvaluateScheduling();
                yield break;
            }

            task.progress = Progress.Failed;
            task.failureReason = string.IsNullOrWhiteSpace(failureReason)
                ? "planning produced no FirePlan"
                : failureReason;
            MelonLogger.Error($"[FCS Dispatch] T{task.targetId} planning failed: {task.failureReason}");
            RecordTaskResult(task);
            TryDispatch();
            _fcs.PlanExecutor.EvaluateScheduling();
            yield break;
        }

        if (!_fcs.PlanExecutor.AddPlan(plan, out var addReason))
        {
            task.progress = Progress.Failed;
            task.failureReason = $"FirePlan admission failed: {addReason}";
            MelonLogger.Error($"[FCS Dispatch] T{task.targetId}: {task.failureReason}");
            RecordTaskResult(task);
        }

        TryDispatch();
        _fcs.PlanExecutor.EvaluateScheduling();
    }

    private void PrependTask(ArtilleryTask task)
    {
        var rest = _taskQueue.ToArray();
        _taskQueue.Clear();
        _taskQueue.Enqueue(task);
        foreach (var queued in rest)
            _taskQueue.Enqueue(queued);
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
