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
        return _turret != null;
    }
    
    public IEnumerator SetRotation(float angle, float timeoutSeconds = 45f) {
        LastRotationSucceeded = false;
        if (_turret == null) {
            MelonLogger.Error("[FCS] Aiming: unbound TurretController");
            yield break;
        }

        _turret.DesiredRotation = -angle;
        var deadline = Time.realtimeSinceStartup + Mathf.Max(1f, timeoutSeconds);
        yield return new WaitForSeconds(0.5f);
        while (_turret.rotationVelocity != 0) {
            if (Time.realtimeSinceStartup >= deadline) {
                MelonLogger.Error($"[FCS] Turret rotation timed out at target {angle:F1}°");
                yield break;
            }
            yield return new WaitForSeconds(0.25f);
        }
        LastRotationSucceeded = true;
    }
    
}
