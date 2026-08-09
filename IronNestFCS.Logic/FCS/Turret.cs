using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class Turret {
    private const float ExpectedAzimuthSpeedDegreesPerSecond = 4f;
    private const float RotationTimeoutSafetyMultiplier = 1.25f;
    private const float RotationTimeoutBufferSeconds = 5f;
    private const float MaximumDynamicRotationTimeoutSeconds = 75f;

    private TurretController? _turret;

    public bool LastRotationSucceeded { get; private set; }

    public bool TryBind() {
        var turretObj = GameObject.Find("TurretSystem");
        if (turretObj == null) {
            MelonLogger.Error("[FCS] Aiming: Can't find TurretSystem");
            return false;
        }
        _turret = turretObj.GetComponent<TurretController>();
        if (_turret == null)
            return false;

        // F9 intentionally discards the old FCS target. The game-side TurretController survives the
        // Logic reload, so its previous DesiredRotation would otherwise keep slewing toward the abandoned
        // target. Rebind from the live physical angle to cancel that stale intent without teleporting.
        try {
            _turret.DesiredRotation = _turret.CurrentAngle;
            MelonLogger.Msg($"[FCS] Turret rebind: holding current azimuth {_turret.CurrentAngle:F2}°");
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] Turret rebind: couldn't cancel stale rotation target: {ex.Message}");
        }
        return true;
    }

    private void HoldCurrentAzimuth(string reason) {
        if (_turret == null)
            return;

        try {
            _turret.DesiredRotation = _turret.CurrentAngle;
            MelonLogger.Msg($"[FCS] Turret rotation canceled; holding {_turret.CurrentAngle:F2}° ({reason})");
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] Turret rotation cancel failed: {ex.Message}");
        }
    }
    
    public IEnumerator SetRotation(
        float angle,
        float timeoutSeconds = 45f,
        Func<bool>? cancelRequested = null) {
        LastRotationSucceeded = false;
        if (_turret == null) {
            MelonLogger.Error("[FCS] Aiming: unbound TurretController");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        if (cancelRequested?.Invoke() == true) {
            HoldCurrentAzimuth("canceled before rotation start");
            yield break;
        }

        var targetAzimuth = -angle;
        var startingAzimuth = _turret.CurrentAngle;
        var rotationDelta = Mathf.Abs(Mathf.DeltaAngle(startingAzimuth, targetAzimuth));
        var estimatedRotationSeconds = rotationDelta / ExpectedAzimuthSpeedDegreesPerSecond;
        var dynamicTimeoutSeconds = Mathf.Clamp(
            estimatedRotationSeconds * RotationTimeoutSafetyMultiplier + RotationTimeoutBufferSeconds,
            1f,
            MaximumDynamicRotationTimeoutSeconds);
        var effectiveTimeoutSeconds = Mathf.Max(timeoutSeconds, dynamicTimeoutSeconds);

        if (effectiveTimeoutSeconds > timeoutSeconds + 0.01f) {
            MelonLogger.Msg(
                $"[FCS] Turret rotation watchdog extended: delta={rotationDelta:F1}°, " +
                $"estimated={estimatedRotationSeconds:F1}s, timeout={effectiveTimeoutSeconds:F1}s");
        }

        _turret.DesiredRotation = targetAzimuth;
        var deadline = FcsRuntimeClock.Now + Mathf.Max(1f, effectiveTimeoutSeconds);
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);

        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (cancelRequested?.Invoke() == true) {
                HoldCurrentAzimuth("task/arbitration invalidated");
                yield break;
            }

            if (_turret.rotationVelocity == 0)
                break;

            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Error(
                    $"[FCS] Turret rotation timed out at target {angle:F1}° " +
                    $"after {effectiveTimeoutSeconds:F1}s (delta={rotationDelta:F1}°)");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.25f);
        }

        if (cancelRequested?.Invoke() == true) {
            HoldCurrentAzimuth("canceled at rotation completion");
            yield break;
        }

        LastRotationSucceeded = true;
    }
    
}
