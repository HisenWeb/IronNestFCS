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
    /// The review-console switches are game actions, not a reliable generic toggle API. GetActive() is useful
    /// for some two-state cockpit levers, but treating it as the authoritative ON/OFF position here caused
    /// valid confirmation clicks to be skipped. Keep this hook as a no-op so existing FSC call sites remain
    /// compatible; a new firing solution is rebuilt by replaying the normal confirmation sequence below.
    /// </summary>
    public IEnumerator PrepareForNewFireSolution(LeftRight leftRight) {
        yield break;
    }

    public IEnumerator Arm(LeftRight leftRight) {
        var arm = leftRight == LeftRight.Left ? _armLeft : _armRight;
        if (arm == null) {
            MelonLogger.Error($"[FCS] TriggerConsole: missing {leftRight} arming lever");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        arm.OnClickDown();

        // Complete an already-started lever click even if focus changes in this short interval.
        yield return new WaitForSeconds(0.2f);
        arm.OnClickUp();
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
