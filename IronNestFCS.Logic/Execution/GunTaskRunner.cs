using System.Collections;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Scheduling;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Execution;

/// <summary>
/// Executes one gun task from ballistic solve through physical preparation, hard commit and fire.
/// Global arbitration and turret ownership are delegated to their coordinators.
/// </summary>
internal sealed class GunTaskRunner {
    private const float ReloadReadyTimeoutSeconds = 60f;
    private const float LoadingTimeoutSeconds = 60f;
    private const float ElevationTimeoutSeconds = 35f;
    private const float AutoTurretWaitTimeoutSeconds = 90f;
    private const float ManualTurretWaitTimeoutSeconds = 300f;
    private const float AutoFireTimeoutSeconds = 25f;
    private const float ManualFireTimeoutSeconds = 300f;

    private readonly FSC _fcs;

    public GunTaskRunner(FSC fcs) {
        _fcs = fcs;
    }

    public void Start(LeftRight side, ArtilleryTask task, GunTaskMode mode) {
        _fcs.TrackCoroutine(Run(side, task, mode));
    }

    private IEnumerator Run(LeftRight side, ArtilleryTask task, GunTaskMode mode) {
        yield return FcsRuntimeClock.WaitUntilFocused();

        var taskGeneration = _fcs.FirePriority.Generation;
        var gunSys = side == LeftRight.Left ? _fcs.LeftGun : _fcs.RightGun;
        var sideName = side == LeftRight.Left ? "Left" : "Right";
        var turret = new TurretReservation(task, taskGeneration);

        var initialState = GunPhysicalState.Read(sideName);
        if (mode == GunTaskMode.FreshLoad && !initialState.EmptyReady) {
            _fcs.Dispatcher.RequeueForPhysicalReclassification(
                side, task, turret, $"expected empty gun, got {initialState.Summary()}");
            yield break;
        }
        if (mode == GunTaskMode.CompleteShellLoaded
            && !initialState.CanCompleteShellFor(task.bulletType)) {
            _fcs.Dispatcher.RequeueForPhysicalReclassification(
                side, task, turret, $"expected shell-loaded {task.bulletType.DisplayName()}, got {initialState.Summary()}");
            yield break;
        }
        if (mode == GunTaskMode.ReuseLoadedRound
            && !initialState.CanReuseLoadedFor(task.bulletType)) {
            _fcs.Dispatcher.RequeueForPhysicalReclassification(
                side, task, turret, $"expected loaded {task.bulletType.DisplayName()}, got {initialState.Summary()}");
            yield break;
        }

        int powderCount;
        if (mode == GunTaskMode.ReuseLoadedRound) {
            // MinimumCharge is an automatic policy, not a range verdict for a fixed already-loaded round.
            powderCount = initialState.PowderCharges;
        }
        else {
            powderCount = _fcs.SceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);
        }
        task.chargeCount = powderCount;

        float elevation = 0f;
        var viable = true;
        var failureReason = "";

        // Ballistic, requisition and trigger are physically distinct shared resources.
        task.progress = Progress.Calculating;
        MelonLogger.Msg($"[FCS Resource] {side} T{task.targetId}: waiting ballistic console");
        yield return _fcs.SharedResources.Ballistic.Acquire();
        try {
            MelonLogger.Msg($"[FCS Resource] {side} T{task.targetId}: acquired ballistic console");
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.SetDistance(task.distance);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.SetDirection(task.angel);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.SetCharge(powderCount);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.SetShellType(task.bulletType);
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.Calculate();
            yield return FcsRuntimeClock.WaitUntilFocused();
            elevation = _fcs.BallisticCalculator.GetElevation();
            task.elevation = elevation;
        }
        finally {
            _fcs.SharedResources.Ballistic.Release();
            MelonLogger.Msg($"[FCS Resource] {side} T{task.targetId}: released ballistic console");
        }

        if (!_fcs.BallisticCalculator.LastCalculationSucceeded
            || float.IsNaN(elevation)
            || float.IsInfinity(elevation)) {
            viable = false;
            failureReason =
                $"ballistic calculation failed for {task.bulletType.DisplayName()} C{powderCount} at {task.distance:F2}km";
        }
        else {
            var stateForRange = GunPhysicalState.Read(sideName);
            if (!stateForRange.IsElevationWithinPhysicalRange(elevation)) {
                viable = false;
                failureReason =
                    $"{task.bulletType.DisplayName()} C{powderCount} has no usable elevation for {task.distance:F2}km (E={elevation:F2})";
            }
        }

        if (viable && mode != GunTaskMode.ReuseLoadedRound) {
            if (mode == GunTaskMode.FreshLoad)
                task.progress = Progress.SelectingBullet;

            MelonLogger.Msg($"[FCS Resource] {side} T{task.targetId}: waiting requisition console");
            yield return _fcs.SharedResources.Requisition.Acquire();
            try {
                MelonLogger.Msg($"[FCS Resource] {side} T{task.targetId}: acquired requisition console");

                var powderPurchaseAttempts = 0;
                while (gunSys.RemainingCharges() < powderCount && powderPurchaseAttempts < 10) {
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    yield return _fcs.PurchaseDeck.BuyPowders();
                    powderPurchaseAttempts++;
                }
                if (gunSys.RemainingCharges() < powderCount) {
                    viable = false;
                    failureReason = $"powder unavailable: need {powderCount}, have {gunSys.RemainingCharges()}";
                }

                if (viable && mode == GunTaskMode.FreshLoad) {
                    if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                        if (!gunSys.HaveEmptyShellInCylinder()) {
                            viable = false;
                            failureReason = $"no {task.bulletType} shell and cylinder has no empty slot";
                        }
                        else {
                            yield return FcsRuntimeClock.WaitUntilFocused();
                            yield return _fcs.PurchaseDeck.BuyShell(task.bulletType, side);
                            yield return FcsRuntimeClock.WaitUntilFocused();
                            if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                                viable = false;
                                failureReason = $"purchase of {task.bulletType} did not reach the cylinder";
                            }
                        }
                    }
                }
            }
            finally {
                _fcs.SharedResources.Requisition.Release();
                MelonLogger.Msg($"[FCS Resource] {side} T{task.targetId}: released requisition console");
            }
        }

        if (taskGeneration != _fcs.FirePriority.Generation
            || !ReferenceEquals(_fcs.Dispatcher.GetActiveTask(side), task)) {
            MelonLogger.Warning(
                $"[FCS] {side} T{task.targetId}: task generation changed during ballistic/requisition preparation; discarding stale routine");
            yield break;
        }

        if (!viable) {
            if (mode == GunTaskMode.ReuseLoadedRound)
                _fcs.Dispatcher.RetryOnAnotherGun(side, task, turret, failureReason);
            else
                _fcs.Dispatcher.AbortTask(side, task, turret, failureReason);
            yield break;
        }

        if (mode == GunTaskMode.ReuseLoadedRound) {
            var loadedState = GunPhysicalState.Read(sideName);
            if (!loadedState.LoadedReady
                || loadedState.ShellType != task.bulletType
                || loadedState.PowderCharges != powderCount) {
                _fcs.Dispatcher.RequeueForPhysicalReclassification(
                    side,
                    task,
                    turret,
                    $"loaded round changed before retargeting; {loadedState.Summary()}");
                yield break;
            }

            MelonLogger.Msg(
                $"[FCS] {side} T{task.targetId}: reusing chambered {task.bulletType.DisplayName()} C{powderCount}");
        }

        // Read-only preparation timing probe. These measurements never feed scheduling.
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
            $"[FCS PrepProbe] {side} T{task.targetId} arbitration-start: mode={mode}, " +
            $"physical={prepProbeStateAtArbitration.Summary()}, " +
            $"azDelta={(prepProbeAzimuthDelta < 0f ? "-" : prepProbeAzimuthDelta.ToString("F1"))}°, " +
            $"el={prepProbeStateAtArbitration.Elevation:F1}°->{task.elevation:F1}° " +
            $"(delta={prepProbeElevationDelta:F1}°, x2={prepProbeElevationDelta * 2f:F1})");

        // Promise.all-like synchronization: valid solutions are registered once. If both guns are in the
        // preparation band, the first solution waits for the second real result; there is no artificial timer.
        if (!_fcs.FirePriority.RegisterBallisticSolution(side, task, taskGeneration, mode))
            yield break;
        _fcs.TurretScheduler.Start(task, turret);

        if (mode == GunTaskMode.ReuseLoadedRound) {
            prepProbeLoadedReadyAt = FcsRuntimeClock.Now;
            MelonLogger.Msg(
                $"[FCS PrepProbe] {side} T{task.targetId} loaded-ready: mode={mode}, " +
                $"after={prepProbeLoadedReadyAt - prepProbeStartedAt:F2}s (already loaded at arbitration)");
        }

        if (mode != GunTaskMode.ReuseLoadedRound) {
            if (mode == GunTaskMode.FreshLoad) {
                var beforeShellLoad = GunPhysicalState.Read(sideName);
                if (!beforeShellLoad.EmptyReady) {
                    _fcs.Dispatcher.RequeueForPhysicalReclassification(
                        side, task, turret, $"gun changed before shell load; {beforeShellLoad.Summary()}");
                    yield break;
                }

                task.progress = Progress.LoadingBullet;
                yield return gunSys.WaitForReloadReady(ReloadReadyTimeoutSeconds);
                if (!gunSys.LastReloadReadySucceeded) {
                    _fcs.Dispatcher.AbortTask(side, task, turret, "reload mechanism was not ready for the next cycle");
                    yield break;
                }

                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return gunSys.LoadBullet(task.bulletType);
                if (!gunSys.LastReloadActionSucceeded) {
                    _fcs.Dispatcher.AbortTask(side, task, turret,
                        string.IsNullOrEmpty(gunSys.LastReloadFailureReason)
                            ? "shell loading control failed"
                            : gunSys.LastReloadFailureReason);
                    yield break;
                }
            }
            else {
                var shellState = GunPhysicalState.Read(sideName);
                if (!shellState.CanCompleteShellFor(task.bulletType)) {
                    _fcs.Dispatcher.RequeueForPhysicalReclassification(
                        side, task, turret, $"chamber changed before powder load; {shellState.Summary()}");
                    yield break;
                }

                MelonLogger.Msg(
                    $"[FCS] {side} T{task.targetId}: resuming chambered {task.bulletType.DisplayName()} with no powder");
                yield return gunSys.WaitForReloadReady(ReloadReadyTimeoutSeconds);
                if (!gunSys.LastReloadReadySucceeded) {
                    _fcs.Dispatcher.AbortTask(side, task, turret, "reload mechanism was not ready to resume powder loading");
                    yield break;
                }
            }

            task.progress = Progress.LoadingPowder;
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return gunSys.LoadPowder(powderCount);
            if (!gunSys.LastReloadActionSucceeded) {
                _fcs.Dispatcher.AbortTask(side, task, turret,
                    string.IsNullOrEmpty(gunSys.LastReloadFailureReason)
                        ? "powder loading control failed"
                        : gunSys.LastReloadFailureReason);
                yield break;
            }

            task.progress = Progress.WaitLoading;
            var loadingDeadline = FcsRuntimeClock.Now + LoadingTimeoutSeconds;
            while (true) {
                yield return FcsRuntimeClock.WaitUntilFocused();
                var loaded = GunPhysicalState.Read(sideName);
                if (loaded.LoadedReady
                    && loaded.ShellType == task.bulletType
                    && loaded.PowderCharges == powderCount)
                    break;

                if (FcsRuntimeClock.Now >= loadingDeadline) {
                    _fcs.Dispatcher.AbortTask(
                        side,
                        task,
                        turret,
                        $"loading did not converge to {task.bulletType.DisplayName()} C{powderCount}; {loaded.Summary()}");
                    yield break;
                }
                yield return FcsRuntimeClock.WaitForSeconds(0.25f);
            }

            prepProbeLoadedReadyAt = FcsRuntimeClock.Now;
            MelonLogger.Msg(
                $"[FCS PrepProbe] {side} T{task.targetId} loaded-ready: mode={mode}, " +
                $"after={prepProbeLoadedReadyAt - prepProbeStartedAt:F2}s");
        }

        task.progress = Progress.Aiming;
        prepProbeElevationStartedAt = FcsRuntimeClock.Now;
        var prepProbeElevationStart = GunPhysicalState.Read(sideName).Elevation;
        MelonLogger.Msg(
            $"[FCS PrepProbe] {side} T{task.targetId} elevation-start: mode={mode}, " +
            $"after={prepProbeElevationStartedAt - prepProbeStartedAt:F2}s, " +
            $"current={prepProbeElevationStart:F1}°, target={elevation:F1}°, " +
            $"delta={Mathf.Abs(elevation - prepProbeElevationStart):F1}°");
        yield return gunSys.SetElevation(elevation, ElevationTimeoutSeconds);
        if (!gunSys.LastElevationSucceeded) {
            _fcs.Dispatcher.AbortTask(side, task, turret, $"elevation did not reach {elevation:F1}°");
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
            $"[FCS PrepProbe] {side} T{task.targetId} local-ready: mode={mode}, " +
            $"total={prepProbeLocalReadyAt - prepProbeStartedAt:F2}s, " +
            $"toLoaded={(prepProbeLoadSeconds < 0f ? "-" : prepProbeLoadSeconds.ToString("F2"))}s, " +
            $"loadedToReady={(prepProbeAfterLoadSeconds < 0f ? "-" : prepProbeAfterLoadSeconds.ToString("F2"))}s, " +
            $"elevationMove={(prepProbeElevationMoveSeconds < 0f ? "-" : prepProbeElevationMoveSeconds.ToString("F2"))}s");

        task.progress = Progress.WaitingForFire;
        var turretWaitTimeout = _fcs.SceneInteractor.AutoFire
            ? AutoTurretWaitTimeoutSeconds
            : ManualTurretWaitTimeoutSeconds;
        var turretDeadline = FcsRuntimeClock.Now + turretWaitTimeout;

        // Hard commit only after BOTH local elevation and shared azimuth are physically ready.
        while (true) {
            while (true) {
                yield return FcsRuntimeClock.WaitUntilFocused();
                if (turret.Ready || turret.Failed) break;
                if (FcsRuntimeClock.Now >= turretDeadline) break;
                yield return null;
            }
            if (turret.Failed) {
                _fcs.Dispatcher.AbortTask(side, task, turret, turret.FailureReason);
                yield break;
            }
            if (!turret.Ready && FcsRuntimeClock.Now >= turretDeadline) {
                _fcs.Dispatcher.AbortTask(side, task, turret,
                    $"turret reservation timed out after {turretWaitTimeout:F0}s");
                yield break;
            }

            if (_fcs.FirePriority.CommitFireLane(task, taskGeneration, turret))
                break;

            // Arbitration changed between Ready observation and commit. Do not touch shared fire controls.
            yield return null;
        }

        try {
            yield return _fcs.SharedResources.Trigger.Acquire();
            try {
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _fcs.TriggerConsole.PrepareForNewFireSolution(side);
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _fcs.TriggerConsole.ConfirmTask();
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _fcs.TriggerConsole.ConfirmBullet();
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _fcs.TriggerConsole.ConfirmRotation();
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _fcs.TriggerConsole.ConfirmElevation();
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _fcs.TriggerConsole.ReadyToFire();
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _fcs.TriggerConsole.Arm(side);
                if (_fcs.SceneInteractor.AutoFire) {
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    _fcs.TriggerConsole.Fire();
                }
            }
            finally {
                _fcs.SharedResources.Trigger.Release();
            }

            var fireTimeout = _fcs.SceneInteractor.AutoFire
                ? AutoFireTimeoutSeconds
                : ManualFireTimeoutSeconds;
            yield return gunSys.WaitFire(fireTimeout);

            // Promote Second before releasing the turret lock, preserving the no-gap handoff.
            if (gunSys.LastFireObserved)
                _fcs.FirePriority.ReleaseAfterSuccessfulShot(task);
        }
        finally {
            _fcs.TurretScheduler.ReleaseOnce(turret);
        }

        if (!gunSys.LastFireObserved) {
            _fcs.Dispatcher.AbortTask(side, task, turret,
                _fcs.SceneInteractor.AutoFire ? "automatic fire was not observed" : "manual fire wait timed out");
            yield break;
        }

        task.progress = Progress.BackToIdle;
        yield return gunSys.WaitBackToIdle();
        yield return FcsRuntimeClock.WaitUntilFocused();
        task.progress = Progress.Finished;
        _fcs.Dispatcher.RecordTaskResult(task);
        _fcs.Dispatcher.ReleaseSlot(side);
    }
}
