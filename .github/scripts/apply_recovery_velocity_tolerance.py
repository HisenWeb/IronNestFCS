from pathlib import Path

p = Path('IronNestFCS.Logic/FCS/GunSystem.cs')
s = p.read_text(encoding='utf-8-sig')

old = '''    private const float MinimumPostShotRecoverySeconds = 13f;\n'''
new = '''    private const float MinimumPostShotRecoverySeconds = 13f;\n    private const float RecoveryElevationVelocityTolerance = 0.05f;\n'''
if old not in s:
    raise SystemExit('constant anchor not found')
s = s.replace(old, new, 1)

count = s.count('var motionReady = gunController.elevationChangeVelocity == 0;')
if count != 2:
    raise SystemExit(f'expected 2 exact velocity checks, found {count}')
s = s.replace(
    'var motionReady = gunController.elevationChangeVelocity == 0;',
    'var motionReady = Mathf.Abs(gunController.elevationChangeVelocity) <= RecoveryElevationVelocityTolerance;')

old = '''        var minimumRecoveryUntilGameTime = Time.time + MinimumPostShotRecoverySeconds;\n        var deadline = FcsRuntimeClock.Now + Mathf.Max(MinimumPostShotRecoverySeconds, timeoutSeconds);\n\n        while (true) {\n'''
new = '''        var minimumRecoveryUntilGameTime = Time.time + MinimumPostShotRecoverySeconds;\n        var deadline = FcsRuntimeClock.Now + Mathf.Max(MinimumPostShotRecoverySeconds, timeoutSeconds);\n        var emptyReadyVelocityBlockLogged = false;\n\n        while (true) {\n'''
if old not in s:
    raise SystemExit('WaitBackToIdle deadline anchor not found')
s = s.replace(old, new, 1)

old = '''            var recoveryComplete = reloadController == null\n                ? !gunController.ExternalReloadLoweringLocked && motionReady\n                : physical.EmptyReady && motionReady;\n\n            if (minimumDelayDone && recoveryComplete)\n'''
new = '''            var recoveryComplete = reloadController == null\n                ? !gunController.ExternalReloadLoweringLocked && motionReady\n                : physical.EmptyReady && motionReady;\n\n            if (physical.EmptyReady && !motionReady && !emptyReadyVelocityBlockLogged) {\n                emptyReadyVelocityBlockLogged = true;\n                MelonLogger.Warning(\n                    $"[FCS] GunSystem {_surfix}: EmptyReady reached but residual elevation velocity " +\n                    $"{gunController.elevationChangeVelocity:F4} exceeds tolerance " +\n                    $"{RecoveryElevationVelocityTolerance:F2}; waiting for settle");\n            }\n\n            if (minimumDelayDone && recoveryComplete)\n'''
if old not in s:
    raise SystemExit('WaitBackToIdle recovery anchor not found')
s = s.replace(old, new, 1)

if 'elevationChangeVelocity == 0' in s:
    raise SystemExit('exact velocity check remains')

p.write_text(s, encoding='utf-8')
