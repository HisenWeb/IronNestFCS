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

    private static bool TryGetLeverState(LookAtTarget? lever, out bool active) {
        active = false;
        if (lever == null) return false;
        try {
            active = lever.GetActive();
            return true;
        }
        catch {
            return false;
        }
    }

    private static IEnumerator ThrowLever(LookAtTarget lever) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        lever.OnClickDown();

        // The arming lever is a physical two-state lever. Keep the original, proven hold time rather than
        // routing it through the generic review-console click helper.
        yield return new WaitForSeconds(0.2f);
        lever.OnClickUp();
        yield return FcsRuntimeClock.WaitForSeconds(0.25f);
    }

    private static IEnumerator SetArmState(LookAtTarget? lever, bool armed, string name) {
        if (lever == null) {
            MelonLogger.Error($"[FCS] TriggerConsole: missing {name}");
            yield break;
        }

        if (!TryGetLeverState(lever, out var current)) {
            MelonLogger.Warning($"[FCS] TriggerConsole: can't read {name} state; leaving it unchanged");
            yield break;
        }

        if (current == armed)
            yield break;

        yield return ThrowLever(lever);

        if (TryGetLeverState(lever, out var after)) {
            if (after != armed) {
                MelonLogger.Warning(
                    $"[FCS] TriggerConsole: {name} did not reach requested state {(armed ? "ARMED" : "SAFE")}");
            }
        }
        else {
            MelonLogger.Warning($"[FCS] TriggerConsole: couldn't verify {name} after lever throw");
        }
    }

    /// <summary>
    /// Review-console checks are action switches and are rebuilt by replaying their normal click sequence.
    /// The two gun arming levers are different: they are durable two-state controls, so every new fire solution
    /// first places BOTH guns on safe. F9 uses this same hook, which removes any armed state left by an abandoned task.
    /// </summary>
    public IEnumerator PrepareForNewFireSolution(LeftRight leftRight) {
        yield return SetArmState(_armLeft, false, "Left arming lever");
        yield return SetArmState(_armRight, false, "Right arming lever");
    }

    public IEnumerator Arm(LeftRight leftRight) {
        // Defensive invariant: only the selected gun may be armed. This also self-heals manual/F9 residue.
        if (leftRight == LeftRight.Left) {
            yield return SetArmState(_armRight, false, "Right arming lever");
            yield return SetArmState(_armLeft, true, "Left arming lever");
        }
        else {
            yield return SetArmState(_armLeft, false, "Left arming lever");
            yield return SetArmState(_armRight, true, "Right arming lever");
        }
        yield return FcsRuntimeClock.WaitForSeconds(1f);
    }

    public IEnumerator ConfirmTask() {
        yield return FcsSceneInteractor.WaitAndClick(_taskCheck);
    }

    public IEnumerator ConfirmBullet() {
        yield return FcsSceneInteractor.WaitAndClick(_bulletCheck);
    }

    public IEnumerator ConfirmRotation() {
        yield return FcsSceneInteractor.WaitAndClick(_rotationCheck);
    }

    public IEnumerator ConfirmElevation() {
        yield return FcsSceneInteractor.WaitAndClick(_elevationCheck);
    }

    public IEnumerator ReadyToFire() {
        yield return FcsSceneInteractor.WaitAndClick(_readyFire);
    }
}
