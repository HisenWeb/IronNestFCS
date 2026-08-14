// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Owns task queue/history and serial admission into planning rounds. Each round first builds a side-effect-free
/// eligibility matrix, then materializes only the Task x Gun edges selected by TaskGunMatcher before admission.
/// Forced Sync is a one-way queue barrier: normal tasks before it keep normal matching semantics, while tasks
/// after it cannot overtake it. Once it reaches the queue head, it requires both gun slots and expands to L + R.
/// </summary>
internal sealed class TaskDispatcher
{
    private const int RecentTaskLimit = 20;
    private const float PhysicalRetryPollSeconds = 0.25f;
    private const float MatchCoalesceWindowSeconds = 1.0f;
    private const float MatchCoalescePollSeconds = 0.05f;
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
        var mode = task.forcedSync ? " [Forced Sync L+R]" : "";
        MelonLogger.Msg($"[FCS Dispatch] queued T{task.targetId}{mode}; pending={_taskQueue.Count}");
        TryDispatch();
    }

    public void TryDispatch()
    {
        // Planning is serialized, but a trigger that arrives while a round is running must not be lost.
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
    /// One match round coalesces a manually adjacent second task when both gun slots are free, captures one
    /// gun/loading snapshot, computes hard eligibility without game-console side effects, then materializes only
    /// the selected assignments. No FirePlan is admitted until every assignment in the chosen set materializes.
    /// </summary>
    private IEnumerator PlanPendingTasks()
    {
        yield return WaitForMatchCoalesceWindow();

        // Any enqueue observed during the coalescing window is now represented in _taskQueue and will be scanned
        // below. Clear only that consumed edge; a later enqueue during materialization will set it again.
        _dispatchRequested = false;

        var snapshot = _fcs.Planner.CaptureSnapshot();
        // Retry ownership belongs to a free transient gun side, not to whether a particular task had zero
        // candidates. A task may be eligible on Left while Right is still recovering; if Right opens during
        // materialization, pending work must be dispatched into that newly free side.
        var deferredPhysicalMask = SnapshotTransientFreeSideMask(snapshot);
        var admittedAny = false;

        // Forced Sync becomes special only after every normal task before it has left Pending. If it is not the
        // queue head, the normal scan below stops immediately before it and therefore cannot see tasks behind it.
        var head = _taskQueue.Count > 0 ? _taskQueue.Peek() : null;
        if (head?.forcedSync == true)
        {
            yield return PlanForcedSyncHead(head, snapshot, admitted => admittedAny = admitted);
            FinishPlanningRound(admittedAny, 1, deferredPhysicalMask);
            yield break;
        }

        var attempted = new HashSet<ArtilleryTask>();
        var planningResults = new List<TaskPlanningResult>();

        while (true)
        {
            var task = FindNextUnattemptedBeforeForcedSync(attempted);
            if (task == null)
                break;

            attempted.Add(task);
            var result = _fcs.Planner.BuildEligibility(task, snapshot);
            planningResults.Add(result);

            if (!result.HasCandidate)
            {
                task.pendingHint = result.PendingHint;
                task.progress = Progress.Pending;
                task.failureReason = "";

                MelonLogger.Msg(
                    $"[FCS Dispatch] T{task.targetId} remains pending; {result.FailureDetail}");
            }
        }

        var matchAt = FcsRuntimeClock.Now;
        foreach (var result in planningResults)
            result.FinalizeTiming(snapshot.SnapshotAt, matchAt);

        LogEligibilityMatrix(planningResults);

        // Materialization can reveal a ballistic/elevation failure that cannot be known without invoking the game
        // calculator. Exclude only that edge and rematch. Successful materializations are cached so a fallback
        // rematch never creates a second sticker for the same Task x Gun edge.
        var excludedEdges = new HashSet<(ArtilleryTask Task, LeftRight Side)>();
        var materializationCache = new Dictionary<(ArtilleryTask Task, LeftRight Side), FirePlanCandidate>();
        var materialized = new List<MaterializedAssignment>();

        while (true)
        {
            var assignments = TaskGunMatcher.Match(planningResults, excludedEdges);
            if (assignments.Count == 0)
                break;

            MelonLogger.Msg(
                $"[FCS Match] selected {assignments.Count} assignment(s): " +
                string.Join(", ", assignments.Select(DescribeAssignment)));

            materialized.Clear();
            var rematchRequired = false;

            foreach (var assignment in assignments)
            {
                var key = (assignment.Planning.Task, assignment.Candidate.Side);
                if (!materializationCache.TryGetValue(key, out var realized))
                {
                    FirePlanCandidate? resolved = null;
                    var failureReason = "";
                    yield return _fcs.Planner.MaterializeCandidate(
                        assignment.Planning.Task,
                        assignment.Candidate,
                        snapshot,
                        result => resolved = result,
                        reason => failureReason = reason);

                    if (resolved == null)
                    {
                        excludedEdges.Add(key);
                        rematchRequired = true;
                        MelonLogger.Warning(
                            $"[FCS Match] materialization rejected T{assignment.Planning.Task.targetId}" +
                            $"->{assignment.Candidate.Side} {assignment.Candidate.Shell.DisplayName()} " +
                            $"C{assignment.Candidate.Charge}: {failureReason}; rematching remaining edges");
                        break;
                    }

                    realized = resolved;
                    materializationCache[key] = realized;
                }

                materialized.Add(new MaterializedAssignment(assignment, realized));
            }

            if (!rematchRequired)
                break;
        }

        var selectedTasks = new HashSet<ArtilleryTask>();

        if (materialized.Count > 0)
        {
            // Use one common commit time for all successfully materialized plans. Fresh loading/elevation cannot
            // consume time while the physical calculator is producing stickers; an already-running load can.
            var commitAt = FcsRuntimeClock.Now;
            foreach (var item in materialized)
                item.Candidate.FinalizeTiming(snapshot.SnapshotAt, commitAt);

            foreach (var item in materialized)
            {
                var assignment = item.Assignment;
                var task = assignment.Planning.Task;
                var plan = _fcs.Planner.CreatePlan(assignment.Planning, item.Candidate, commitAt);

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
        }

        // Every evaluated but unselected task remains pending. A valid pre-match candidate can lose because a
        // higher-quality complete match consumed the free slots, or because a selected edge failed materialization.
        foreach (var result in planningResults)
        {
            if (selectedTasks.Contains(result.Task))
                continue;

            result.Task.progress = Progress.Pending;
            result.Task.failureReason = "";
            if (result.HasCandidate)
                result.Task.pendingHint = PendingHint.None;
        }

        FinishPlanningRound(admittedAny, attempted.Count, deferredPhysicalMask);
    }

    /// <summary>
    /// A head Forced Sync intent owns both gun slots or neither. It reuses the normal eligibility and ballistic
    /// materialization paths, but there is no matching decision to make: Left and Right are both required.
    /// The queue still contains only the source intent until both FirePlans have been materialized.
    /// </summary>
    private IEnumerator PlanForcedSyncHead(
        ArtilleryTask source,
        FirePlanningSnapshot snapshot,
        Action<bool> completed)
    {
        completed(false);
        source.pendingHint = PendingHint.None;
        source.failureReason = "";

        if (!snapshot.LeftSlotAvailable || !snapshot.RightSlotAvailable)
        {
            source.progress = Progress.Pending;
            MelonLogger.Msg(
                $"[FCS ForcedSync] T{source.targetId} head barrier waiting for both executor slots; " +
                $"Left={(snapshot.LeftSlotAvailable ? "free" : "occupied")}, " +
                $"Right={(snapshot.RightSlotAvailable ? "free" : "occupied")}");
            yield break;
        }

        var planning = _fcs.Planner.BuildEligibility(source, snapshot);
        var matchAt = FcsRuntimeClock.Now;
        planning.FinalizeTiming(snapshot.SnapshotAt, matchAt);

        if (planning.LeftCandidate == null || planning.RightCandidate == null)
        {
            source.progress = Progress.Pending;
            source.pendingHint = planning.PendingHint;
            source.failureReason = "";
            MelonLogger.Msg(
                $"[FCS ForcedSync] T{source.targetId} head barrier waiting for L+R eligibility; " +
                $"Left={DescribeCandidate(planning.LeftCandidate, planning.LeftReason)}; " +
                $"Right={DescribeCandidate(planning.RightCandidate, planning.RightReason)}");
            yield break;
        }

        FirePlanCandidate? leftResolved = null;
        var leftFailure = "";
        yield return _fcs.Planner.MaterializeCandidate(
            source,
            planning.LeftCandidate,
            snapshot,
            result => leftResolved = result,
            reason => leftFailure = reason);

        if (leftResolved == null)
        {
            source.progress = Progress.Pending;
            MelonLogger.Warning(
                $"[FCS ForcedSync] T{source.targetId}->Left materialization rejected; remains pending: {leftFailure}");
            yield break;
        }

        FirePlanCandidate? rightResolved = null;
        var rightFailure = "";
        yield return _fcs.Planner.MaterializeCandidate(
            source,
            planning.RightCandidate,
            snapshot,
            result => rightResolved = result,
            reason => rightFailure = reason);

        if (rightResolved == null)
        {
            source.progress = Progress.Pending;
            MelonLogger.Warning(
                $"[FCS ForcedSync] T{source.targetId}->Right materialization rejected; remains pending: {rightFailure}");
            yield break;
        }

        // Nothing else can claim these slots while this serialized planning round is active. Re-check immediately
        // before admission so a stale snapshot cannot turn a Forced Sync pair into an accidental single plan.
        if (_fcs.PlanExecutor.GetPlan(LeftRight.Left) != null
            || _fcs.PlanExecutor.GetPlan(LeftRight.Right) != null)
        {
            source.progress = Progress.Pending;
            MelonLogger.Warning(
                $"[FCS ForcedSync] T{source.targetId} lost the empty L+R window before admission; remains pending");
            yield break;
        }

        var leftTask = CreateForcedSyncExecutionTask(source);
        var rightTask = CreateForcedSyncExecutionTask(source);
        var leftPlanning = CopyPlanningForExecutionTask(planning, leftTask);
        var rightPlanning = CopyPlanningForExecutionTask(planning, rightTask);

        var commitAt = FcsRuntimeClock.Now;
        leftResolved.FinalizeTiming(snapshot.SnapshotAt, commitAt);
        rightResolved.FinalizeTiming(snapshot.SnapshotAt, commitAt);

        var leftPlan = _fcs.Planner.CreatePlan(leftPlanning, leftResolved, commitAt);
        var rightPlan = _fcs.Planner.CreatePlan(rightPlanning, rightResolved, commitAt);

        if (!_fcs.PlanExecutor.AddPlan(leftPlan, out var leftAddReason))
        {
            source.progress = Progress.Pending;
            MelonLogger.Warning(
                $"[FCS ForcedSync] T{source.targetId}->Left was not admitted and remains pending: {leftAddReason}");
            yield break;
        }

        // AddPlan starts preparation only after one focus yield. Dispatcher is still inside this same serialized
        // planning round, so Right is installed before either side can reach the procurement rendezvous.
        if (!_fcs.PlanExecutor.AddPlan(rightPlan, out var rightAddReason))
        {
            // Both slots were re-checked immediately above; reaching this branch means an executor invariant was
            // violated. Do not leave the source intent queued, which would otherwise duplicate the accepted Left.
            RemovePendingTask(source);
            MelonLogger.Error(
                $"[FCS ForcedSync] T{source.targetId} invariant violation: Left admitted but Right rejected: {rightAddReason}");
            completed(true);
            yield break;
        }

        if (!RemovePendingTask(source))
            MelonLogger.Warning($"[FCS ForcedSync] admitted T{source.targetId} source intent was no longer pending");

        completed(true);
        MelonLogger.Msg(
            $"[FCS ForcedSync] admitted T{source.targetId} as Left + Right; pending={_taskQueue.Count}");
    }

    private void FinishPlanningRound(bool admittedAny, int attemptedCount, int deferredPhysicalMask)
    {
        _planning = false;

        if (!admittedAny && attemptedCount > 0)
            MelonLogger.Msg($"[FCS Dispatch] planning round deferred {attemptedCount} pending task(s)");

        // Plans finish at physical shot, so preserve event-driven dispatch for any free side that was transient
        // in this round's snapshot. If it became plannable while materializing another assignment, immediately
        // open the next planning round; otherwise keep one temporary waiter for the physical transition.
        if (deferredPhysicalMask != 0 && _taskQueue.Count > 0)
        {
            if (CurrentPlannableFreeSideMask(deferredPhysicalMask) != 0)
                _dispatchRequested = true;
            else
                EnsurePhysicalRetryWait(deferredPhysicalMask);
        }

        // Consume one coalesced trigger that arrived after the eligibility scan had already closed. TryDispatch()
        // sets _planning synchronously before the next coroutine starts, preserving scheduler pair waiting.
        if (_dispatchRequested)
        {
            _dispatchRequested = false;
            TryDispatch();
        }

        _fcs.PlanExecutor.EvaluateScheduling();
    }

    private IEnumerator WaitForMatchCoalesceWindow()
    {
        if (_taskQueue.Count != 1
            || _taskQueue.Peek().forcedSync
            || _fcs.PlanExecutor.GetPlan(LeftRight.Left) != null
            || _fcs.PlanExecutor.GetPlan(LeftRight.Right) != null)
        {
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + MatchCoalesceWindowSeconds;
        while (_taskQueue.Count < 2 && FcsRuntimeClock.Now < deadline)
        {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (_taskQueue.Count >= 2)
                break;
            yield return FcsRuntimeClock.WaitForSeconds(MatchCoalescePollSeconds);
        }
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

    private static string DescribeCandidate(TaskGunCandidate? candidate, string failureReason)
    {
        if (candidate != null)
        {
            var eta = candidate.EtaKnown
                ? Math.Max(0f, candidate.EstimatedReadyAt - FcsRuntimeClock.Now).ToString("F1") + "s"
                : "unknown";
            return $"eligible {candidate.Shell.DisplayName()} C{candidate.Charge} preETA={eta}";
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

    private static int SnapshotTransientFreeSideMask(FirePlanningSnapshot snapshot)
    {
        var mask = 0;
        if (snapshot.LeftSlotAvailable && IsTransient(snapshot.LeftLoading.PhysicalState))
            mask |= LeftPhysicalRetryBit;
        if (snapshot.RightSlotAvailable && IsTransient(snapshot.RightLoading.PhysicalState))
            mask |= RightPhysicalRetryBit;
        return mask;
    }

    private int CurrentPlannableFreeSideMask(int sideMask)
    {
        var mask = 0;
        if ((sideMask & LeftPhysicalRetryBit) != 0
            && _fcs.PlanExecutor.GetPlan(LeftRight.Left) == null
            && IsPlannable(_fcs.Loading.GetSnapshot(GunSide.Left).PhysicalState))
        {
            mask |= LeftPhysicalRetryBit;
        }

        if ((sideMask & RightPhysicalRetryBit) != 0
            && _fcs.PlanExecutor.GetPlan(LeftRight.Right) == null
            && IsPlannable(_fcs.Loading.GetSnapshot(GunSide.Right).PhysicalState))
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

    /// <summary>
    /// Return only normal tasks before the first Forced Sync barrier. The barrier itself is handled only when it
    /// reaches queue head; tasks behind it are intentionally invisible to this planning round.
    /// </summary>
    private ArtilleryTask? FindNextUnattemptedBeforeForcedSync(HashSet<ArtilleryTask> attempted)
    {
        foreach (var task in _taskQueue)
        {
            if (task.forcedSync)
                break;
            if (!attempted.Contains(task))
                return task;
        }

        return null;
    }

    private static ArtilleryTask CreateForcedSyncExecutionTask(ArtilleryTask source)
    {
        return new ArtilleryTask
        {
            targetId = source.targetId,
            angel = source.angel,
            distance = source.distance,
            position = source.position,
            bulletType = source.bulletType,
            progress = Progress.Calculating,
            forcedSync = true,
            pendingHint = PendingHint.None,
            chargeCount = 0,
            elevation = 0f,
            startedAt = source.startedAt,
            completedAt = 0f,
            failureReason = "",
            dispatchExcludedGunMask = 0,
        };
    }

    private static TaskPlanningResult CopyPlanningForExecutionTask(
        TaskPlanningResult source,
        ArtilleryTask executionTask)
    {
        return new TaskPlanningResult(
            executionTask,
            source.LeftCandidate,
            source.RightCandidate,
            source.LeftReason,
            source.RightReason,
            source.PendingHint,
            source.FailureDetail,
            source.ShouldWait);
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

    private sealed class MaterializedAssignment
    {
        public TaskGunAssignment Assignment { get; }
        public FirePlanCandidate Candidate { get; }

        public MaterializedAssignment(TaskGunAssignment assignment, FirePlanCandidate candidate)
        {
            Assignment = assignment;
            Candidate = candidate;
        }
    }
}
