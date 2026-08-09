using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class Turret {
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
    
    public IEnumerator SetRotation(float angle, float timeoutSeconds = 45f) {
        LastRotationSucceeded = false;
        if (_turret == null) {
            MelonLogger.Error("[FCS] Aiming: unbound TurretController");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        _turret.DesiredRotation = -angle;
        var deadline = FcsRuntimeClock.Now + Mathf.Max(1f, timeoutSeconds);
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);

        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (_turret.rotationVelocity == 0)
                break;

            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Error($"[FCS] Turret rotation timed out at target {angle:F1}°");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.25f);
        }
        LastRotationSucceeded = true;
    }
    
}
