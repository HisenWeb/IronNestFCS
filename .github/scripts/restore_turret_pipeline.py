from pathlib import Path
import subprocess

base = '2a888482afd1233160b9aed0d3c6d8f27418a1a0'
subprocess.run([
    'git', 'checkout', base, '--',
    'IronNestFCS.Logic/FCS/TriggerConsole.cs',
    'IronNestFCS.Logic/FSC.cs'
], check=True)

p = Path('IronNestFCS.Logic/FSC.cs')
s = p.read_text(encoding='utf-8')

old_wait = """    private IEnumerator WaitForFirePriority(ArtilleryTask task, TurretReservation res) {
        // Pure Promise.all-like wait: no scoring timeout and no polling/recalculation. The winner is produced by
        // task-assignment/ballistic-solution events. F9 changes the generation, which cancels stale waiters.
        while (!res.Canceled
               && res.Generation == _firePriorityGeneration
               && IsActiveTask(task)
               && !ReferenceEquals(_firePriorityWinner, task)) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return null;
        }

        if (res.Generation != _firePriorityGeneration || !IsActiveTask(task))
            res.Canceled = true;
    }
"""
new_wait = """    private bool CanEnterTurretQueue(ArtilleryTask task) {
        if (ReferenceEquals(_firePriorityWinner, task))
            return true;

        // Once a pair has been resolved and First has actually committed the shared fire lane, let the fixed
        // Second enter CoroutineLock.Acquire() immediately. It waits behind First while that lock is held,
        // reproducing the pre-decoupling reservation pipeline without allowing Second to rotate out of order.
        return ReferenceEquals(_firePrioritySecond, task)
               && _firePriorityWinner != null
               && ReferenceEquals(_fireLaneCommittedTask, _firePriorityWinner)
               && IsActiveTask(_firePriorityWinner);
    }

    private IEnumerator WaitForTurretQueueEligibility(ArtilleryTask task, TurretReservation res) {
        // Event/state gate only: no scoring timeout. A resolved First may enter immediately; its fixed Second may
        // prequeue only after First owns the lane. Reset/generation changes still cancel stale reservations.
        while (!res.Canceled
               && res.Generation == _firePriorityGeneration
               && IsActiveTask(task)
               && !CanEnterTurretQueue(task)) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return null;
        }

        if (res.Generation != _firePriorityGeneration || !IsActiveTask(task))
            res.Canceled = true;
    }
"""
if old_wait not in s:
    raise SystemExit('WaitForFirePriority block not found')
s = s.replace(old_wait, new_wait, 1)

old_call = '            yield return WaitForFirePriority(task, res);'
if old_call not in s:
    raise SystemExit('reservation wait call not found')
s = s.replace(old_call, '            yield return WaitForTurretQueueEligibility(task, res);', 1)

old_comment = """            // A provisional single winner can be revoked if a synchronized second task arrives before the lock
            // is committed. Keep this reservation coroutine alive so it can wait for the new Promise.all result.
"""
new_comment = """            // First and the already-fixed Second can both reach this Acquire. First gets the free lock; Second
            // reaches it only after First has committed, so it waits behind the held lock and is ready to take over
            // immediately after a confirmed shot releases it.
"""
if old_comment not in s:
    raise SystemExit('reservation comment not found')
s = s.replace(old_comment, new_comment, 1)

marker = '''            var fireTimeout = _sceneInteractor.AutoFire
                ? AutoFireTimeoutSeconds
                : ManualFireTimeoutSeconds;
            yield return gunSys.WaitFire(fireTimeout);
        }
        finally {
            ReleaseTurretOnce(turret);
        }

        if (!gunSys.LastFireObserved) {
        AbortTask(leftRight, task, turret,
            _sceneInteractor.AutoFire ? "automatic fire was not observed" : "manual fire wait timed out");
        yield break;
    }

    // A fixed Second is promoted only after a real shot is observed. Timeouts and failures
    // invalidate the broken arbitration instead of pretending the first shot completed.
    ReleaseFirePriorityAfterSuccessfulShot(task);
'''
replacement = '''            var fireTimeout = _sceneInteractor.AutoFire
                ? AutoFireTimeoutSeconds
                : ManualFireTimeoutSeconds;
            yield return gunSys.WaitFire(fireTimeout);

            // Promote the fixed Second before releasing the shared turret lock. A prequeued Second therefore
            // observes itself as the winner as soon as Acquire() completes, with no post-shot scheduling gap.
            if (gunSys.LastFireObserved)
                ReleaseFirePriorityAfterSuccessfulShot(task);
        }
        finally {
            ReleaseTurretOnce(turret);
        }

        if (!gunSys.LastFireObserved) {
            AbortTask(leftRight, task, turret,
                _sceneInteractor.AutoFire ? "automatic fire was not observed" : "manual fire wait timed out");
            yield break;
        }
'''
if marker not in s:
    raise SystemExit('fire promotion block not found')
s = s.replace(marker, replacement, 1)

if '_triggerResetRequiredTask' in s:
    raise SystemExit('early-trigger reset state remains')
if 'PrepareTriggerConsoleForTask' in s:
    raise SystemExit('early-trigger helper remains')
if 'WaitForFirePriority(' in s:
    raise SystemExit('old winner-only gate remains')
if s.count('ReleaseFirePriorityAfterSuccessfulShot(task);') != 1:
    raise SystemExit('promotion count unexpected')

p.write_text(s, encoding='utf-8')
