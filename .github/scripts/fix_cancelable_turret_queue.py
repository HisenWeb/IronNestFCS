from pathlib import Path

p = Path('IronNestFCS.Logic/FSC.cs')
s = p.read_text(encoding='utf-8')

old = '''            res.Released = false;
            yield return _turretLock.Acquire();
            res.Acquired = true;
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (res.Generation != _firePriorityGeneration || !IsActiveTask(task)) {
                res.Canceled = true;
                ReleaseTurretOnce(res);
                yield break;
            }
'''

new = '''            res.Released = false;
            res.Acquired = false;
            yield return _turretLock.Acquire(
                () => res.Canceled
                      || res.Generation != _firePriorityGeneration
                      || !IsActiveTask(task),
                () => res.Acquired = true);

            // Acquire may now complete by cancellation without taking the lock. Do not let a stale queued
            // reservation wake after F9/task invalidation and briefly consume the shared turret lane.
            if (!res.Acquired) {
                res.Canceled = true;
                yield break;
            }

            yield return FcsRuntimeClock.WaitUntilFocused();

            if (res.Canceled
                || res.Generation != _firePriorityGeneration
                || !IsActiveTask(task)) {
                res.Canceled = true;
                ReleaseTurretOnce(res);
                yield break;
            }
'''

if old not in s:
    raise SystemExit('turret Acquire block not found')

s = s.replace(old, new, 1)

if 'yield return _turretLock.Acquire();' in s:
    raise SystemExit('uncancelable turret lock wait still remains')
if s.count('yield return _turretLock.Acquire(') != 1:
    raise SystemExit('unexpected turret Acquire call count')
if 'if (!res.Acquired)' not in s:
    raise SystemExit('canceled-acquire guard missing')

p.write_text(s, encoding='utf-8')
