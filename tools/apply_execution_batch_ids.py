from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


# FirePlan: carry the committed execution-stack identity only after ComparePair/CommitSingle.
p = Path("IronNestFCS.Logic/Scheduling/FirePlan.cs")
text = p.read_text(encoding="utf-8-sig")
text = replace_once(
    text,
    "    public int Generation { get; }\n\n    public bool Compared { get; set; }\n",
    "    public int Generation { get; }\n\n"
    "    // Zero while merely planned. ComparePair/CommitSingle assigns one id shared by the committed stack.\n"
    "    public int ExecutionBatchId { get; set; }\n"
    "    public bool Compared { get; set; }\n",
    "add FirePlan execution batch id",
)
p.write_text(text, encoding="utf-8")


# FirePriorityCoordinator: the existing commit boundary is the only place that creates a batch id.
p = Path("IronNestFCS.Logic/Scheduling/FirePriorityCoordinator.cs")
text = p.read_text(encoding="utf-8-sig")
text = replace_once(
    text,
    "    private int _generation;\n",
    "    private int _generation;\n    private int _nextExecutionBatchId;\n",
    "add batch serial",
)
text = replace_once(
    text,
    "    public string RightDetail => _rightDetail;\n\n    public void Reset()\n",
    "    public string RightDetail => _rightDetail;\n\n"
    "    private int NextExecutionBatchId() => ++_nextExecutionBatchId;\n\n"
    "    public void Reset()\n",
    "add batch allocator",
)
text = replace_once(
    text,
    "        a.Compared = true;\n        b.Compared = true;\n",
    "        var executionBatchId = NextExecutionBatchId();\n"
    "        a.ExecutionBatchId = executionBatchId;\n"
    "        b.ExecutionBatchId = executionBatchId;\n"
    "        a.Compared = true;\n"
    "        b.Compared = true;\n",
    "assign pair batch id",
)
text = replace_once(
    text,
    "        MelonLogger.Msg($\"[FCS Order] paired once: {first.Label} first, {second.Label} second; {reason}\");\n",
    "        MelonLogger.Msg($\"[FCS Order] batch {executionBatchId} paired once: {first.Label} first, {second.Label} second; {reason}\");\n",
    "log pair batch id",
)
text = replace_once(
    text,
    "    public void CommitSingle(FirePlan plan, string reason)\n    {\n        if (!plan.Compared)\n            plan.Compared = true;\n",
    "    public void CommitSingle(FirePlan plan, string reason)\n    {\n"
    "        if (plan.ExecutionBatchId == 0)\n"
    "            plan.ExecutionBatchId = NextExecutionBatchId();\n\n"
    "        if (!plan.Compared)\n"
    "            plan.Compared = true;\n",
    "assign single batch id",
)
text = replace_once(
    text,
    "        MelonLogger.Msg($\"[FCS Order] single committed: {plan.Label}; {FcsLocalization.LogReason(reason)}\");\n",
    "        MelonLogger.Msg($\"[FCS Order] batch {plan.ExecutionBatchId} single committed: {plan.Label}; {FcsLocalization.LogReason(reason)}\");\n",
    "log single batch id",
)
text = replace_once(
    text,
    "        MelonLogger.Msg($\"[FCS Order] promoting previously compared plan without re-compare: {plan.Label}\");\n",
    "        MelonLogger.Msg($\"[FCS Order] promoting batch {plan.ExecutionBatchId} plan without re-compare: {plan.Label}\");\n",
    "log promoted batch id",
)
p.write_text(text, encoding="utf-8")


# TriggerConsole: consume a batch id; do not infer task/plan lifecycle internally.
p = Path("IronNestFCS.Logic/FCS/TriggerConsole.cs")
text = p.read_text(encoding="utf-8-sig")
text = replace_once(
    text,
    "    private bool _reviewBatchActive;\n    private int _reviewOperationGeneration;\n",
    "    private bool _reviewBatchActive;\n    private int _reviewBatchId;\n",
    "replace review generation with batch id",
)
text = replace_once(
    text,
    "            _reviewBatchActive = false;\n            _reviewOperationGeneration++;\n            LogPhysicalStates(\"bind\");\n",
    "            _reviewBatchActive = false;\n            _reviewBatchId = 0;\n            LogPhysicalStates(\"bind\");\n",
    "reset review batch on bind",
)
old = '''    /// <summary>
    /// Start one independent asynchronous ON operation for each review button. The batch remains active until
    /// ReviewAllOff is called; callers do not pass Plan/current/next state into these operations.
    /// </summary>
    public IReadOnlyList<IEnumerator> BeginReviewAsync() {
        if (_reviewBatchActive)
            return Array.Empty<IEnumerator>();

        _reviewBatchActive = true;
        var generation = ++_reviewOperationGeneration;
        Func<bool> stillCurrent = () => _reviewBatchActive && generation == _reviewOperationGeneration;

        return new IEnumerator[] {
            EnsureReviewState(_taskCheck, _taskPose, true, "TaskCheck", stillCurrent),
            EnsureReviewState(_bulletCheck, _bulletPose, true, "BulletCheck", stillCurrent),
            EnsureReviewState(_rotationCheck, _rotationPose, true, "RotationCheck", stillCurrent),
            EnsureReviewState(_elevationCheck, _elevationPose, true, "ElevationCheck", stillCurrent),
            EnsureReviewState(_readyFire, _readyPose, true, "ReadyToFire", stillCurrent),
        };
    }

    /// <summary>
    /// End the current review-button batch and drive only the five review controls OFF. This is the normal
    /// execution-stack teardown path and intentionally does not touch either arming lever.
    /// </summary>
    public IEnumerator ReviewAllOff(string reason) {
        _reviewBatchActive = false;
        _reviewOperationGeneration++;

        LogPhysicalStates($"before {reason} review all-off");
        yield return EnsureReviewState(_readyFire, _readyPose, false, "ReadyToFire");
        yield return EnsureReviewState(_elevationCheck, _elevationPose, false, "ElevationCheck");
        yield return EnsureReviewState(_rotationCheck, _rotationPose, false, "RotationCheck");
        yield return EnsureReviewState(_bulletCheck, _bulletPose, false, "BulletCheck");
        yield return EnsureReviewState(_taskCheck, _taskPose, false, "TaskCheck");
        LogPhysicalStates($"after {reason} review all-off");
    }

    public IEnumerator ResetPhysicalFireControls(string reason) {
        LogPhysicalStates($"before {reason} full reset");

        // F9/startup clears the whole TaskSystem execution stack, so it resets both independent physical groups.
        // Normal execution-stack teardown calls ReviewAllOff and never reaches these arming levers.
        yield return EnsureArmState(_armLeft, _armLeftPose, false, "Left");
        yield return EnsureArmState(_armRight, _armRightPose, false, "Right");
        yield return ReviewAllOff(reason);

        LogPhysicalStates($"after {reason} full reset");
    }
'''
new = '''    /// <summary>
    /// Start one independent asynchronous ON operation for each review button in the committed execution batch.
    /// The caller supplies the batch identity; this module never reads Plan/current/next state.
    /// </summary>
    public IReadOnlyList<IEnumerator> BeginReviewAsync(int executionBatchId) {
        if (executionBatchId <= 0) {
            MelonLogger.Error($"[FCS] TriggerConsole: invalid review batch id {executionBatchId}");
            return Array.Empty<IEnumerator>();
        }

        // Promotion inside the same committed stack must not re-dispatch review controls or re-run the 1.2s lead.
        if (_reviewBatchActive && _reviewBatchId == executionBatchId)
            return Array.Empty<IEnumerator>();

        _reviewBatchId = executionBatchId;
        _reviewBatchActive = true;
        Func<bool> stillCurrent = () => _reviewBatchActive && _reviewBatchId == executionBatchId;

        return new IEnumerator[] {
            EnsureReviewState(_taskCheck, _taskPose, true, "TaskCheck", stillCurrent),
            EnsureReviewState(_bulletCheck, _bulletPose, true, "BulletCheck", stillCurrent),
            EnsureReviewState(_rotationCheck, _rotationPose, true, "RotationCheck", stillCurrent),
            EnsureReviewState(_elevationCheck, _elevationPose, true, "ElevationCheck", stillCurrent),
            EnsureReviewState(_readyFire, _readyPose, true, "ReadyToFire", stillCurrent),
        };
    }

    /// <summary>
    /// Synchronously close one review batch before its asynchronous physical AllOff is queued. This immediately
    /// invalidates unfinished ON operations and lets a following committed stack start with its own batch id.
    /// </summary>
    public bool EndReviewBatch(int executionBatchId) {
        if (!_reviewBatchActive || _reviewBatchId != executionBatchId)
            return false;

        _reviewBatchActive = false;
        return true;
    }

    /// <summary>
    /// Drive only the five review controls OFF for the ended batch. If a newer batch starts before this coroutine
    /// acquires the physical trigger-console lane, the old AllOff becomes stale and exits without touching it.
    /// </summary>
    public IEnumerator ReviewAllOff(int executionBatchId, string reason) {
        Func<bool> stillEndedBatch = () => !_reviewBatchActive && _reviewBatchId == executionBatchId;
        if (!stillEndedBatch())
            yield break;

        LogPhysicalStates($"before {reason} review all-off batch {executionBatchId}");
        yield return EnsureReviewState(_readyFire, _readyPose, false, "ReadyToFire", stillEndedBatch);
        yield return EnsureReviewState(_elevationCheck, _elevationPose, false, "ElevationCheck", stillEndedBatch);
        yield return EnsureReviewState(_rotationCheck, _rotationPose, false, "RotationCheck", stillEndedBatch);
        yield return EnsureReviewState(_bulletCheck, _bulletPose, false, "BulletCheck", stillEndedBatch);
        yield return EnsureReviewState(_taskCheck, _taskPose, false, "TaskCheck", stillEndedBatch);
        if (stillEndedBatch())
            LogPhysicalStates($"after {reason} review all-off batch {executionBatchId}");
    }

    private IEnumerator ForceReviewAllOff(string reason) {
        // F9/startup destroys the whole TaskSystem execution stack, so no old batch remains valid afterward.
        _reviewBatchActive = false;
        _reviewBatchId = 0;

        yield return EnsureReviewState(_readyFire, _readyPose, false, "ReadyToFire");
        yield return EnsureReviewState(_elevationCheck, _elevationPose, false, "ElevationCheck");
        yield return EnsureReviewState(_rotationCheck, _rotationPose, false, "RotationCheck");
        yield return EnsureReviewState(_bulletCheck, _bulletPose, false, "BulletCheck");
        yield return EnsureReviewState(_taskCheck, _taskPose, false, "TaskCheck");
    }

    public IEnumerator ResetPhysicalFireControls(string reason) {
        LogPhysicalStates($"before {reason} full reset");

        // F9/startup clears the whole TaskSystem execution stack, so it resets both independent physical groups.
        // Normal execution-stack teardown uses the batch-aware ReviewAllOff path and never touches arming levers.
        yield return EnsureArmState(_armLeft, _armLeftPose, false, "Left");
        yield return EnsureArmState(_armRight, _armRightPose, false, "Right");
        yield return ForceReviewAllOff(reason);

        LogPhysicalStates($"after {reason} full reset");
    }
'''
text = replace_once(text, old, new, "replace review lifecycle with execution batch id")
p.write_text(text, encoding="utf-8")


# Executor: pass the committed batch id into the independent review module and close that exact batch on drain.
p = Path("IronNestFCS.Logic/Execution/FirePlanExecutor.cs")
text = p.read_text(encoding="utf-8-sig")
old = '''    private bool DispatchReviewProtocolAsync()
    {
        var operations = _fcs.TriggerConsole.BeginReviewAsync();
        if (operations.Count == 0)
            return false;

        foreach (var operation in operations)
            _fcs.TrackCoroutine(operation);

        MelonLogger.Msg("[FCS] TriggerConsole: dispatched independent asynchronous review-button operations");
        return true;
    }
'''
new = '''    private bool DispatchReviewProtocolAsync(int executionBatchId)
    {
        var operations = _fcs.TriggerConsole.BeginReviewAsync(executionBatchId);
        if (operations.Count == 0)
            return false;

        foreach (var operation in operations)
            _fcs.TrackCoroutine(operation);

        MelonLogger.Msg($"[FCS] TriggerConsole: dispatched async review buttons for batch {executionBatchId}");
        return true;
    }
'''
text = replace_once(text, old, new, "pass batch id to review dispatch")
text = replace_once(
    text,
    "                if (DispatchReviewProtocolAsync())\n",
    "                if (DispatchReviewProtocolAsync(plan.ExecutionBatchId))\n",
    "dispatch current plan batch",
)
old = '''        // Compared is the existing committed execution-stack label. When the LAST compared plan
        // leaves, only the independent review-button module is reset. Arming remains owned by the physical
        // firing path and is intentionally not coupled to the review-button lifecycle.
        if (plan.Compared && !HasRemainingComparedPlan())
        {
            MelonLogger.Msg("[FCS Plan] committed stack drained; scheduling review buttons all-off");
            _fcs.TrackCoroutine(_fcs.SharedResources.ResetReviewControlsAfterCommittedStack());
        }
'''
new = '''        // Compared marks committed execution membership; ExecutionBatchId identifies the exact stack. End the
        // review batch synchronously when its LAST plan leaves, then queue physical AllOff. A newer batch id can
        // supersede that queued AllOff without being touched by stale work.
        var executionBatchId = plan.ExecutionBatchId;
        if (plan.Compared
            && executionBatchId > 0
            && !HasRemainingExecutionBatch(executionBatchId)
            && _fcs.TriggerConsole.EndReviewBatch(executionBatchId))
        {
            MelonLogger.Msg($"[FCS Plan] batch {executionBatchId} drained; scheduling review buttons all-off");
            _fcs.TrackCoroutine(_fcs.SharedResources.ResetReviewControlsAfterCommittedStack(executionBatchId));
        }
'''
text = replace_once(text, old, new, "close exact execution batch")
old = '''    private bool HasRemainingComparedPlan()
    {
        return (_leftPlan != null && _leftPlan.Compared && !_leftPlan.CompletionHandled)
               || (_rightPlan != null && _rightPlan.Compared && !_rightPlan.CompletionHandled);
    }
'''
new = '''    private bool HasRemainingExecutionBatch(int executionBatchId)
    {
        return (_leftPlan != null
                && _leftPlan.Compared
                && _leftPlan.ExecutionBatchId == executionBatchId
                && !_leftPlan.CompletionHandled)
               || (_rightPlan != null
                   && _rightPlan.Compared
                   && _rightPlan.ExecutionBatchId == executionBatchId
                   && !_rightPlan.CompletionHandled);
    }
'''
text = replace_once(text, old, new, "check remaining exact batch")
p.write_text(text, encoding="utf-8")


# SharedConsoleCoordinator: serialize physical AllOff for the batch id supplied by the executor.
p = Path("IronNestFCS.Logic/Infrastructure/SharedConsoleCoordinator.cs")
text = p.read_text(encoding="utf-8-sig")
old = '''    /// <summary>
    /// A normal committed FirePlan stack turns only the five independent review buttons OFF after its last
    /// Compared plan leaves the executor. Arming remains owned by the physical firing path.
    /// </summary>
    public IEnumerator ResetReviewControlsAfterCommittedStack() {
        yield return FcsRuntimeClock.WaitUntilFocused();
        yield return Trigger.Acquire();
        try {
            yield return _fcs.TriggerConsole.ReviewAllOff("committed stack drained");
        }
        finally {
            Trigger.Release();
        }
    }
'''
new = '''    /// <summary>
    /// Serialize physical AllOff for one ended execution batch. TriggerConsole rechecks that exact batch id after
    /// the lock is acquired, so a newer committed stack cannot be affected by stale teardown work.
    /// </summary>
    public IEnumerator ResetReviewControlsAfterCommittedStack(int executionBatchId) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        yield return Trigger.Acquire();
        try {
            yield return _fcs.TriggerConsole.ReviewAllOff(executionBatchId, "committed stack drained");
        }
        finally {
            Trigger.Release();
        }
    }
'''
text = replace_once(text, old, new, "batch-aware shared review reset")
p.write_text(text, encoding="utf-8")


# Architecture invariants.
plan = Path("IronNestFCS.Logic/Scheduling/FirePlan.cs").read_text(encoding="utf-8")
priority = Path("IronNestFCS.Logic/Scheduling/FirePriorityCoordinator.cs").read_text(encoding="utf-8")
trigger = Path("IronNestFCS.Logic/FCS/TriggerConsole.cs").read_text(encoding="utf-8")
executor = Path("IronNestFCS.Logic/Execution/FirePlanExecutor.cs").read_text(encoding="utf-8")
shared = Path("IronNestFCS.Logic/Infrastructure/SharedConsoleCoordinator.cs").read_text(encoding="utf-8")

required = [
    (plan, "public int ExecutionBatchId { get; set; }"),
    (priority, "a.ExecutionBatchId = executionBatchId;"),
    (priority, "b.ExecutionBatchId = executionBatchId;"),
    (priority, "plan.ExecutionBatchId = NextExecutionBatchId();"),
    (trigger, "BeginReviewAsync(int executionBatchId)"),
    (trigger, "public bool EndReviewBatch(int executionBatchId)"),
    (trigger, "ReviewAllOff(int executionBatchId, string reason)"),
    (executor, "DispatchReviewProtocolAsync(plan.ExecutionBatchId)"),
    (executor, "TriggerConsole.EndReviewBatch(executionBatchId)"),
    (executor, "HasRemainingExecutionBatch(executionBatchId)"),
    (shared, "ResetReviewControlsAfterCommittedStack(int executionBatchId)"),
]
for haystack, needle in required:
    if needle not in haystack:
        raise SystemExit(f"missing invariant: {needle}")

forbidden = [
    (trigger, "_reviewOperationGeneration"),
    (trigger, "BeginReviewAsync()"),
    (executor, "HasRemainingComparedPlan"),
    (shared, "ResetReviewControlsAfterCommittedStack()"),
]
for haystack, needle in forbidden:
    if needle in haystack:
        raise SystemExit(f"stale implementation remains: {needle}")

print("Execution-batch review lifecycle patch applied and invariants passed.")
