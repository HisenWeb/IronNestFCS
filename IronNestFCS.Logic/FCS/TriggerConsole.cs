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
    /// F9 invalidates the abandoned fire solution, but the five review switches are rebuilt by the normal
    /// confirmation sequence and the arming controls must NOT be treated as generic readable toggles.
    /// In the release build Universal Button Arm Left/Right behave as side-selection actions; GetActive()
    /// is not a reliable indication of their latched visual/armed state. Touching both sides here caused an
    /// unnecessary Right->Left handoff that also reset the already-confirmed review console.
    /// </summary>
    public IEnumerator PrepareForNewFireSolution(LeftRight leftRight) {
        yield break;
    }

    public IEnumerator Arm(LeftRight leftRight) {
        var arm = leftRight == LeftRight.Left ? _armLeft : _armRight;
        if (arm == null) {
            MelonLogger.Error($"[FCS] TriggerConsole: missing {leftRight} arming control");
            yield break;
        }

        // Preserve the original proven game interaction: issue exactly ONE arm action for the side that owns
        // the current fire solution. The game handles the opposite side; FCS must not pre-toggle both levers.
        yield return FcsRuntimeClock.WaitUntilFocused();
        arm.OnClickDown();

        // Once a lever action has started, always complete it even if focus changes during this short hold.
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
