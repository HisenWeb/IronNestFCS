from pathlib import Path

p = Path('IronNestFCS.Logic/FSC.cs')
s = p.read_text(encoding='utf-8-sig')

old = '''        // Promise.all-like synchronization: valid solutions are registered once. If both guns are in the
        // preparation band, the first solution waits for the second real result; there is no artificial timer.
        // A reset changes taskGeneration, so a late pre-reset result can never join the new arbitration session.
        if (!RegisterBallisticSolution(leftRight, task, taskGeneration))
            yield break;
        _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));

        if (mode != GunTaskMode.ReuseLoadedRound) {
'''
new = '''        // Read-only preparation timing probe. Start exactly where the current arbitration candidate is
        // registered, so the timings measure work that remains *after* the decision point rather than earlier
        // calculator/purchase work. These values are diagnostics only and never feed scheduling.
        var prepProbeStartedAt = FcsRuntimeClock.Now;
        var prepProbeLoadedReadyAt = -1f;
        var prepProbeElevationStartedAt = -1f;
        var prepProbeStateAtArbitration = GunPhysicalState.Read(sideName);
        var prepProbeTurret = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        var prepProbeCurrentAzimuth = prepProbeTurret?.CurrentAngle ?? 0f;
        var prepProbeAzimuthDelta = prepProbeTurret == null
            ? -1f
            : Mathf.Abs(Mathf.DeltaAngle(prepProbeCurrentAzimuth, -task.angel));
        var prepProbeElevationDelta = Mathf.Abs(task.elevation - prepProbeStateAtArbitration.Elevation);
        MelonLogger.Msg(
            $"[FCS PrepProbe] {leftRight} T{task.targetId} arbitration-start: mode={mode}, " +
            $"physical={prepProbeStateAtArbitration.Summary()}, " +
            $"azDelta={(prepProbeAzimuthDelta < 0f ? "-" : prepProbeAzimuthDelta.ToString("F1"))}°, " +
            $"el={prepProbeStateAtArbitration.Elevation:F1}°->{task.elevation:F1}° " +
            $"(delta={prepProbeElevationDelta:F1}°, x2={prepProbeElevationDelta * 2f:F1})");

        // Promise.all-like synchronization: valid solutions are registered once. If both guns are in the
        // preparation band, the first solution waits for the second real result; there is no artificial timer.
        // A reset changes taskGeneration, so a late pre-reset result can never join the new arbitration session.
        if (!RegisterBallisticSolution(leftRight, task, taskGeneration))
            yield break;
        _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));

        if (mode == GunTaskMode.ReuseLoadedRound) {
            prepProbeLoadedReadyAt = FcsRuntimeClock.Now;
            MelonLogger.Msg(
                $"[FCS PrepProbe] {leftRight} T{task.targetId} loaded-ready: mode={mode}, " +
                $"after={prepProbeLoadedReadyAt - prepProbeStartedAt:F2}s (already loaded at arbitration)");
        }

        if (mode != GunTaskMode.ReuseLoadedRound) {
'''
if old not in s:
    raise SystemExit('registration anchor not found')
s = s.replace(old, new, 1)

old = '''                yield return FcsRuntimeClock.WaitForSeconds(0.25f);
            }
        }

        task.progress = Progress.Aiming;
        yield return gunSys.SetElevation(elevation, ElevationTimeoutSeconds);
'''
new = '''                yield return FcsRuntimeClock.WaitForSeconds(0.25f);
            }

            prepProbeLoadedReadyAt = FcsRuntimeClock.Now;
            MelonLogger.Msg(
                $"[FCS PrepProbe] {leftRight} T{task.targetId} loaded-ready: mode={mode}, " +
                $"after={prepProbeLoadedReadyAt - prepProbeStartedAt:F2}s");
        }

        task.progress = Progress.Aiming;
        prepProbeElevationStartedAt = FcsRuntimeClock.Now;
        var prepProbeElevationStart = GunPhysicalState.Read(sideName).Elevation;
        MelonLogger.Msg(
            $"[FCS PrepProbe] {leftRight} T{task.targetId} elevation-start: mode={mode}, " +
            $"after={prepProbeElevationStartedAt - prepProbeStartedAt:F2}s, " +
            $"current={prepProbeElevationStart:F1}°, target={elevation:F1}°, " +
            $"delta={Mathf.Abs(elevation - prepProbeElevationStart):F1}°");
        yield return gunSys.SetElevation(elevation, ElevationTimeoutSeconds);
'''
if old not in s:
    raise SystemExit('loaded/elevation anchor not found')
s = s.replace(old, new, 1)

old = '''        if (!gunSys.LastElevationSucceeded) {
            AbortTask(leftRight, task, turret, $"elevation did not reach {elevation:F1}°");
            yield break;
        }

        task.progress = Progress.WaitingForFire;
'''
new = '''        if (!gunSys.LastElevationSucceeded) {
            AbortTask(leftRight, task, turret, $"elevation did not reach {elevation:F1}°");
            yield break;
        }

        var prepProbeLocalReadyAt = FcsRuntimeClock.Now;
        var prepProbeLoadSeconds = prepProbeLoadedReadyAt >= 0f
            ? prepProbeLoadedReadyAt - prepProbeStartedAt
            : -1f;
        var prepProbeAfterLoadSeconds = prepProbeLoadedReadyAt >= 0f
            ? prepProbeLocalReadyAt - prepProbeLoadedReadyAt
            : -1f;
        var prepProbeElevationMoveSeconds = prepProbeElevationStartedAt >= 0f
            ? prepProbeLocalReadyAt - prepProbeElevationStartedAt
            : -1f;
        MelonLogger.Msg(
            $"[FCS PrepProbe] {leftRight} T{task.targetId} local-ready: mode={mode}, " +
            $"total={prepProbeLocalReadyAt - prepProbeStartedAt:F2}s, " +
            $"toLoaded={(prepProbeLoadSeconds < 0f ? "-" : prepProbeLoadSeconds.ToString("F2"))}s, " +
            $"loadedToReady={(prepProbeAfterLoadSeconds < 0f ? "-" : prepProbeAfterLoadSeconds.ToString("F2"))}s, " +
            $"elevationMove={(prepProbeElevationMoveSeconds < 0f ? "-" : prepProbeElevationMoveSeconds.ToString("F2"))}s");

        task.progress = Progress.WaitingForFire;
'''
if old not in s:
    raise SystemExit('local-ready anchor not found')
s = s.replace(old, new, 1)

if '[FCS PrepProbe]' not in s:
    raise SystemExit('probe marker missing after patch')

p.write_text(s, encoding='utf-8')
