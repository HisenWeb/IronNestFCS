using System.Collections;
using IronNestFCS.Logic.FCS;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Owns the single shared turret lane, including winner ownership, Second prequeue,
/// provisional preemption and cancellation-safe handoff across F9/generation changes.
/// </summary>
internal sealed class TurretScheduler {
    private const float TurretRotationTimeoutSeconds = 45f;

    private readonly FSC _fcs;
    private readonly CoroutineLock _turretLock = new();

    public TurretScheduler(FSC fcs) {
        _fcs = fcs;
    }

    public void Reset() {
        _turretLock.Reset();
    }

    public void Start(ArtilleryTask task, TurretReservation reservation) {
        _fcs.TrackCoroutine(ReserveAndRotate(task, reservation));
    }

    private IEnumerator WaitForQueueEligibility(ArtilleryTask task, TurretReservation reservation) {
        // Event/state gate only: no scoring timeout. First may enter immediately; Second may prequeue after First
        // physically owns the turret lane. Reset/generation changes cancel stale reservations.
        while (!reservation.Canceled
               && reservation.Generation == _fcs.FirePriority.Generation
               && _fcs.Dispatcher.IsActiveTask(task)
               && !_fcs.FirePriority.CanEnterTurretQueue(task)) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return null;
        }

        if (reservation.Generation != _fcs.FirePriority.Generation
            || !_fcs.Dispatcher.IsActiveTask(task)) {
            reservation.Canceled = true;
        }
    }

    private IEnumerator ReserveAndRotate(ArtilleryTask task, TurretReservation reservation) {
        while (!reservation.Canceled) {
            yield return WaitForQueueEligibility(task, reservation);
            if (reservation.Canceled)
                yield break;

            reservation.Released = false;
            reservation.Acquired = false;
            reservation.Ready = false;
            reservation.HardCommitted = false;
            yield return _turretLock.Acquire(
                () => reservation.Canceled
                      || reservation.Generation != _fcs.FirePriority.Generation
                      || !_fcs.Dispatcher.IsActiveTask(task),
                () => reservation.Acquired = true);

            // Acquire may finish through cancellation without owning the lock.
            if (!reservation.Acquired) {
                reservation.Canceled = true;
                yield break;
            }

            yield return FcsRuntimeClock.WaitUntilFocused();

            if (reservation.Canceled
                || reservation.Generation != _fcs.FirePriority.Generation
                || !_fcs.Dispatcher.IsActiveTask(task)) {
                reservation.Canceled = true;
                ReleaseOnce(reservation);
                yield break;
            }

            if (!_fcs.FirePriority.IsWinner(task)) {
                ReleaseOnce(reservation);
                yield return null;
                continue;
            }

            if (!_fcs.FirePriority.ClaimTurretLane(task, reservation.Generation)) {
                ReleaseOnce(reservation);
                yield return null;
                continue;
            }

            yield return _fcs.Turret.SetRotation(
                task.angel,
                TurretRotationTimeoutSeconds,
                () => reservation.Canceled
                      || reservation.Generation != _fcs.FirePriority.Generation
                      || !_fcs.Dispatcher.IsActiveTask(task)
                      || !_fcs.FirePriority.IsWinner(task));
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (reservation.Canceled
                || reservation.Generation != _fcs.FirePriority.Generation
                || !_fcs.Dispatcher.IsActiveTask(task)
                || !_fcs.FirePriority.IsWinner(task)) {
                ReleaseOnce(reservation);
                if (reservation.Canceled
                    || reservation.Generation != _fcs.FirePriority.Generation
                    || !_fcs.Dispatcher.IsActiveTask(task)) {
                    yield break;
                }
                yield return null;
                continue;
            }

            if (!_fcs.Turret.LastRotationSucceeded) {
                reservation.Failed = true;
                reservation.FailureReason = $"turret could not reach {task.angel:F1}°";
                ReleaseOnce(reservation);
                yield break;
            }

            reservation.Ready = true;

            // Keep the reservation alive after azimuth reaches target. If a provisional owner loses arbitration
            // before hard commit, release and re-enter the normal queue without touching Review/Arm controls.
            while (!reservation.Canceled
                   && reservation.Generation == _fcs.FirePriority.Generation
                   && _fcs.Dispatcher.IsActiveTask(task)
                   && !reservation.HardCommitted
                   && _fcs.FirePriority.IsWinner(task)) {
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return null;
            }

            if (reservation.HardCommitted)
                yield break;

            reservation.Ready = false;
            ReleaseOnce(reservation);
            if (reservation.Canceled
                || reservation.Generation != _fcs.FirePriority.Generation
                || !_fcs.Dispatcher.IsActiveTask(task)) {
                yield break;
            }

            yield return null;
        }
    }

    public void ReleaseOnce(TurretReservation reservation) {
        if (reservation.Acquired && !reservation.Released) {
            reservation.Released = true;
            reservation.Acquired = false;
            reservation.Ready = false;
            reservation.HardCommitted = false;
            _fcs.FirePriority.OnTurretReservationReleased(reservation);
            _turretLock.Release();
        }
    }
}
