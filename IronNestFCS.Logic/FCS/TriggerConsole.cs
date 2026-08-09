using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class TriggerConsole {
    private LookAtTarget? _taskCheck;
    private LookAtTarget? _bulletCheck;
    private LookAtTarget? _rotationCheck;
    private LookAtTarget? _elevationCheck;
    private LookAtTarget? _readyFire;
    private LookAtTarget? _armLeft;
    private LookAtTarget? _armRight;
    private SliderEnergyMomentumSpinner? _fire;

    public bool TryBind() {
        var consoleObject = GameObject.Find(".Review Console Parent");
        if (consoleObject == null) {
            MelonLogger.Error("[FCS] Can't bind trigger console: .Review Console Parent missing");
            return false;
        }

        var buttons = new List<LookAtTarget>();
        var console = consoleObject.transform;
        for (var i = 0; i < console.childCount; ++i) {
            var child = console.GetChild(i);
            if (!child.name.StartsWith(".Check Switch")) continue;
            var button = child.GetComponentInChildren<LookAtTarget>();
            if (button != null)
                buttons.Add(button);
        }

        if (buttons.Count != 5) {
            MelonLogger.Error($"[FCS] Can't bind trigger console: expected 5 review switches, found {buttons.Count}");
            return false;
        }

        _taskCheck = buttons[0];
        _bulletCheck = buttons[1];
        _rotationCheck = buttons[2];
        _elevationCheck = buttons[3];
        _readyFire = buttons[4];
        _armLeft = GameObject.Find(".ArmingLeverParent Left")?.GetComponentInChildren<LookAtTarget>();
        _armRight = GameObject.Find(".ArmingLeverParent Right")?.GetComponentInChildren<LookAtTarget>();
        _fire = GameObject.Find(".Trigger Core")?.transform.FindChild(".Generator Spinner")
            ?.GetComponentInChildren<SliderEnergyMomentumSpinner>();

        return _armLeft != null && _armRight != null && _fire != null;
    }

    public void Fire() {
        _fire?.AddEnergy(255);
    }

    /// <summary>
    /// Review switches and arming levers are two-state controls. After F9 they may still be left in
    /// the old task's state, so blindly clicking them can toggle an already-correct control OFF.
    /// GetActive() exposes the latched on/off state; use it to drive every control idempotently.
    /// </summary>
    private static bool TryGetToggleState(LookAtTarget? control, out bool active) {
        active = false;
        if (control == null) return false;
        try {
            active = control.GetActive();
            return true;
        }
        catch {
            return false;
        }
    }

    private static IEnumerator SetToggleState(
        LookAtTarget? control,
        bool desired,
        string controlName,
        float timeoutSeconds = 5f) {
        if (control == null) {
            MelonLogger.Error($"[FCS] TriggerConsole: missing {controlName}");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();

        if (TryGetToggleState(control, out var current)) {
            if (current == desired)
                yield break;
        }
        else if (!desired) {
            MelonLogger.Warning(
                $"[FCS] TriggerConsole: can't read {controlName} state; leaving it unchanged during reset");
            yield break;
        }

        yield return FcsSceneInteractor.WaitAndClick(control, timeoutSeconds);
        yield return FcsRuntimeClock.WaitUntilFocused();

        if (TryGetToggleState(control, out var after) && after != desired) {
            MelonLogger.Warning(
                $"[FCS] TriggerConsole: {controlName} did not reach requested state {(desired ? "ON" : "OFF")}");
        }
    }

    /// <summary>
    /// Put the shared review console and the selected gun's arming lever into a known baseline before
    /// confirming a new target. This makes retargeting after F9 safe even if the old task was reset
    /// halfway through review/arming.
    /// </summary>
    public IEnumerator PrepareForNewFireSolution(LeftRight leftRight) {
        var arm = leftRight == LeftRight.Left ? _armLeft : _armRight;

        yield return SetToggleState(arm, false,
            leftRight == LeftRight.Left ? "Left arming lever" : "Right arming lever");
        yield return SetToggleState(_readyFire, false, "ReadyToFire");
        yield return SetToggleState(_elevationCheck, false, "ElevationCheck");
        yield return SetToggleState(_rotationCheck, false, "RotationCheck");
        yield return SetToggleState(_bulletCheck, false, "BulletCheck");
        yield return SetToggleState(_taskCheck, false, "TaskCheck");
    }

    public IEnumerator Arm(LeftRight leftRight) {
        var arm = leftRight == LeftRight.Left ? _armLeft : _armRight;
        yield return SetToggleState(
            arm,
            true,
            leftRight == LeftRight.Left ? "Left arming lever" : "Right arming lever");
        yield return FcsRuntimeClock.WaitForSeconds(1f);
    }
    
    public IEnumerator ConfirmTask() {
        yield return SetToggleState(_taskCheck, true, "TaskCheck");
    }

    public IEnumerator ConfirmBullet() {
        yield return SetToggleState(_bulletCheck, true, "BulletCheck");
    }

    public IEnumerator ConfirmRotation() {
        yield return SetToggleState(_rotationCheck, true, "RotationCheck");
    }

    public IEnumerator ConfirmElevation() {
        yield return SetToggleState(_elevationCheck, true, "ElevationCheck");
    }

    public IEnumerator ReadyToFire() {
        yield return SetToggleState(_readyFire, true, "ReadyToFire");
    }
}
