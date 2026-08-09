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

        var ok = _armLeft != null && _armRight != null && _fire != null;
        if (ok) LogLatchedStates("bind");
        return ok;
    }

    public void Fire() {
        _fire?.AddEnergy(255);
    }

    private static bool TryGetClickedState(LookAtTarget? control, out bool clicked) {
        clicked = false;
        if (control == null) return false;
        try {
            // LookAtTarget.GetActive()/isActive describe interaction availability and are not a reliable
            // representation of a physical toggle's latched position. `isClicked` is the durable click target
            // state used by the control/animator and survives Logic hot reloads.
            clicked = control.isClicked;
            return true;
        }
        catch {
            return false;
        }
    }

    private static IEnumerator EnsureReviewConfirmed(LookAtTarget? control, string name) {
        if (control == null) {
            MelonLogger.Error($"[FCS] TriggerConsole: missing {name}");
            yield break;
        }

        if (TryGetClickedState(control, out var current) && current) {
            MelonLogger.Msg($"[FCS] TriggerConsole: {name} already confirmed; preserving latched state");
            yield break;
        }

        yield return FcsSceneInteractor.WaitAndClick(control);
        yield return FcsRuntimeClock.WaitForSeconds(0.15f);

        if (TryGetClickedState(control, out var after) && !after) {
            MelonLogger.Warning($"[FCS] TriggerConsole: {name} click did not latch ON");
        }
    }

    private static IEnumerator EnsureArmSelected(LookAtTarget? arm, string name) {
        if (arm == null) {
            MelonLogger.Error($"[FCS] TriggerConsole: missing {name} arming control");
            yield break;
        }

        if (TryGetClickedState(arm, out var current) && current) {
            MelonLogger.Msg($"[FCS] TriggerConsole: {name} already armed; preserving latched state");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        arm.OnClickDown();

        // Preserve the original proven arming interaction. Do not touch the opposite gun; selecting a side is
        // game-owned and the game itself handles any previous side selection.
        yield return new WaitForSeconds(0.2f);
        arm.OnClickUp();
        yield return FcsRuntimeClock.WaitForSeconds(0.25f);

        if (TryGetClickedState(arm, out var after) && !after) {
            MelonLogger.Warning($"[FCS] TriggerConsole: {name} arm action did not latch ON");
        }
    }

    private void LogLatchedStates(string reason) {
        static string S(LookAtTarget? c) => TryGetClickedState(c, out var v) ? (v ? "ON" : "OFF") : "?";
        MelonLogger.Msg(
            $"[FCS] TriggerConsole state ({reason}): " +
            $"Task={S(_taskCheck)} Bullet={S(_bulletCheck)} Rotation={S(_rotationCheck)} " +
            $"Elevation={S(_elevationCheck)} Ready={S(_readyFire)} ArmL={S(_armLeft)} ArmR={S(_armRight)}");
    }

    /// <summary>
    /// F9 abandons FCS task ownership but does not reset the physical trigger console in the game scene.
    /// Do not toggle anything here. A later task reconciles each required control idempotently from `isClicked`.
    /// </summary>
    public IEnumerator PrepareForNewFireSolution(LeftRight leftRight) {
        LogLatchedStates("before fire solution");
        yield break;
    }

    public IEnumerator Arm(LeftRight leftRight) {
        if (leftRight == LeftRight.Left) {
            yield return EnsureArmSelected(_armLeft, "Left");
        }
        else {
            yield return EnsureArmSelected(_armRight, "Right");
        }
        yield return FcsRuntimeClock.WaitForSeconds(0.75f);
        LogLatchedStates("after arm");
    }

    public IEnumerator ConfirmTask() {
        yield return EnsureReviewConfirmed(_taskCheck, "TaskCheck");
    }

    public IEnumerator ConfirmBullet() {
        yield return EnsureReviewConfirmed(_bulletCheck, "BulletCheck");
    }

    public IEnumerator ConfirmRotation() {
        yield return EnsureReviewConfirmed(_rotationCheck, "RotationCheck");
    }

    public IEnumerator ConfirmElevation() {
        yield return EnsureReviewConfirmed(_elevationCheck, "ElevationCheck");
    }

    public IEnumerator ReadyToFire() {
        yield return EnsureReviewConfirmed(_readyFire, "ReadyToFire");
    }
}
