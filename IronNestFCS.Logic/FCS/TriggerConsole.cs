using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class TriggerConsole {
    private const float ReviewOnZ = -90f;
    private const float ReviewOffZ = 0f;
    private const float ReviewPoseTolerance = 10f;
    private const float ArmOnX = -50f;
    private const float ArmOffX = -32f;
    private const float ArmPoseTolerance = 4f;
    private const float PoseTransitionTimeoutSeconds = 2f;

    private LookAtTarget? _taskCheck;
    private LookAtTarget? _bulletCheck;
    private LookAtTarget? _rotationCheck;
    private LookAtTarget? _elevationCheck;
    private LookAtTarget? _readyFire;
    private LookAtTarget? _armLeft;
    private LookAtTarget? _armRight;

    // Physical truth discovered from the release build with TriggerConsoleProbe and verified in-game:
    // review switch knob Z: OFF=0°, ON=-90°;
    // arming lever parent X: OFF=-32°, ON=-50°.
    private Transform? _taskPose;
    private Transform? _bulletPose;
    private Transform? _rotationPose;
    private Transform? _elevationPose;
    private Transform? _readyPose;
    private Transform? _armLeftPose;
    private Transform? _armRightPose;

    private SliderEnergyMomentumSpinner? _fire;

    // TryBind runs again after F9. FSC calls PrepareForNewFireSolution once immediately after a successful bind;
    // that first call is the hot-reload reset hook. Later calls belong to normal tasks and only reconcile ON state.
    private bool _resetPendingAfterBind;

    public bool TryBind() {
        var consoleObject = GameObject.Find(".Review Console Parent");
        if (consoleObject == null) {
            MelonLogger.Error("[FCS] Can't bind trigger console: .Review Console Parent missing");
            return false;
        }

        var controls = new List<(LookAtTarget button, Transform pose)>();
        var console = consoleObject.transform;
        for (var i = 0; i < console.childCount; ++i) {
            var child = console.GetChild(i);
            if (!child.name.StartsWith(".Check Switch")) continue;

            var button = child.GetComponentInChildren<LookAtTarget>();
            var pose = FindReviewPose(child);
            if (button != null && pose != null)
                controls.Add((button, pose));
        }

        if (controls.Count != 5) {
            MelonLogger.Error($"[FCS] Can't bind trigger console: expected 5 review switches with physical knobs, found {controls.Count}");
            return false;
        }

        _taskCheck = controls[0].button;
        _taskPose = controls[0].pose;
        _bulletCheck = controls[1].button;
        _bulletPose = controls[1].pose;
        _rotationCheck = controls[2].button;
        _rotationPose = controls[2].pose;
        _elevationCheck = controls[3].button;
        _elevationPose = controls[3].pose;
        _readyFire = controls[4].button;
        _readyPose = controls[4].pose;

        var armLeftObject = GameObject.Find(".ArmingLeverParent Left");
        var armRightObject = GameObject.Find(".ArmingLeverParent Right");
        _armLeftPose = armLeftObject?.transform;
        _armRightPose = armRightObject?.transform;
        _armLeft = armLeftObject?.GetComponentInChildren<LookAtTarget>();
        _armRight = armRightObject?.GetComponentInChildren<LookAtTarget>();
        _fire = GameObject.Find(".Trigger Core")?.transform.FindChild(".Generator Spinner")
            ?.GetComponentInChildren<SliderEnergyMomentumSpinner>();

        var ok = _armLeft != null && _armRight != null &&
                 _armLeftPose != null && _armRightPose != null && _fire != null;
        if (ok) {
            _resetPendingAfterBind = true;
            LogPhysicalStates("bind");
        }
        return ok;
    }

    private static Transform? FindReviewPose(Transform root) {
        var transforms = root.GetComponentsInChildren<Transform>(true);
        foreach (var t in transforms) {
            if (t != null && t != root && t.name.StartsWith("knob_25_003"))
                return t;
        }
        return null;
    }

    public void Fire() {
        _fire?.AddEnergy(255);
    }

    private static float NormalizeAngle(float angle) {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }

    private static bool TryReadReviewState(Transform? pose, out bool on, out float z) {
        on = false;
        z = 0f;
        if (pose == null) return false;

        z = NormalizeAngle(pose.localEulerAngles.z);
        var onDistance = Mathf.Abs(Mathf.DeltaAngle(z, ReviewOnZ));
        var offDistance = Mathf.Abs(Mathf.DeltaAngle(z, ReviewOffZ));
        on = onDistance < offDistance;
        return Mathf.Min(onDistance, offDistance) <= ReviewPoseTolerance;
    }

    private static bool TryReadArmState(Transform? pose, out bool on, out float x) {
        on = false;
        x = 0f;
        if (pose == null) return false;

        x = NormalizeAngle(pose.localEulerAngles.x);
        var onDistance = Mathf.Abs(Mathf.DeltaAngle(x, ArmOnX));
        var offDistance = Mathf.Abs(Mathf.DeltaAngle(x, ArmOffX));
        on = onDistance < offDistance;
        return Mathf.Min(onDistance, offDistance) <= ArmPoseTolerance;
    }

    private static IEnumerator EnsureReviewState(LookAtTarget? control, Transform? pose, bool desiredOn, string name) {
        if (control == null || pose == null) {
            MelonLogger.Error($"[FCS] TriggerConsole: missing {name} control/pose");
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + PoseTransitionTimeoutSeconds;
        bool current;
        float angle;
        while (!TryReadReviewState(pose, out current, out angle)) {
            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Warning($"[FCS] TriggerConsole: {name} physical pose ambiguous at Z={angle:F1}°; not toggling blindly");
                yield break;
            }
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return new WaitForSeconds(0.05f);
        }

        if (current == desiredOn)
            yield break;

        yield return FcsSceneInteractor.WaitAndClick(control);

        deadline = FcsRuntimeClock.Now + PoseTransitionTimeoutSeconds;
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (TryReadReviewState(pose, out var after, out angle) && after == desiredOn)
                yield break;
            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Warning(
                    $"[FCS] TriggerConsole: {name} did not reach {(desiredOn ? "ON" : "OFF")} physical pose; Z={angle:F1}°");
                yield break;
            }
            yield return new WaitForSeconds(0.05f);
        }
    }

    private static IEnumerator ThrowArm(LookAtTarget arm) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        FcsSceneInteractor.BeginPhysicalClick(arm);

        // Once an arm action starts, always complete the down/up pair even if focus changes during the hold.
        // If F9 stops this coroutine, FcsSceneInteractor.ShutDown releases the tracked control before unload.
        yield return new WaitForSeconds(0.2f);
        FcsSceneInteractor.EndPhysicalClick(arm);
    }

    private static IEnumerator EnsureArmState(LookAtTarget? arm, Transform? pose, bool desiredOn, string name) {
        if (arm == null || pose == null) {
            MelonLogger.Error($"[FCS] TriggerConsole: missing {name} arming control/pose");
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + PoseTransitionTimeoutSeconds;
        bool current;
        float angle;
        while (!TryReadArmState(pose, out current, out angle)) {
            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Warning($"[FCS] TriggerConsole: {name} arm pose ambiguous at X={angle:F1}°; not toggling blindly");
                yield break;
            }
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return new WaitForSeconds(0.05f);
        }

        if (current == desiredOn)
            yield break;

        yield return ThrowArm(arm);

        deadline = FcsRuntimeClock.Now + PoseTransitionTimeoutSeconds;
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (TryReadArmState(pose, out var after, out angle) && after == desiredOn)
                yield break;
            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Warning(
                    $"[FCS] TriggerConsole: {name} arm did not reach {(desiredOn ? "ON" : "OFF")} physical pose; X={angle:F1}°");
                yield break;
            }
            yield return new WaitForSeconds(0.05f);
        }
    }

    private static string ReviewStateText(Transform? pose) {
        return TryReadReviewState(pose, out var on, out var z)
            ? $"{(on ? "ON" : "OFF")}({z:F0}°)"
            : $"?({z:F0}°)";
    }

    private static string ArmStateText(Transform? pose) {
        return TryReadArmState(pose, out var on, out var x)
            ? $"{(on ? "ON" : "OFF")}({x:F0}°)"
            : $"?({x:F0}°)";
    }

    private void LogPhysicalStates(string reason) {
        MelonLogger.Msg(
            $"[FCS] TriggerConsole physical ({reason}): " +
            $"Task={ReviewStateText(_taskPose)} Bullet={ReviewStateText(_bulletPose)} " +
            $"Rotation={ReviewStateText(_rotationPose)} Elevation={ReviewStateText(_elevationPose)} " +
            $"Ready={ReviewStateText(_readyPose)} ArmL={ArmStateText(_armLeftPose)} ArmR={ArmStateText(_armRightPose)}");
    }

    private IEnumerator ResetPhysicalFireControlsAfterBind() {
        LogPhysicalStates("before F9 reset");

        // Arming is independent of the FCS task object and survives Logic hot reload. Clear only controls that
        // are physically ON; never infer state from LookAtTarget.GetActive()/isClicked.
        yield return EnsureArmState(_armLeft, _armLeftPose, false, "Left");
        yield return EnsureArmState(_armRight, _armRightPose, false, "Right");

        // Clear the review dependency chain backwards, re-reading the physical pose before every action.
        yield return EnsureReviewState(_readyFire, _readyPose, false, "ReadyToFire");
        yield return EnsureReviewState(_elevationCheck, _elevationPose, false, "ElevationCheck");
        yield return EnsureReviewState(_rotationCheck, _rotationPose, false, "RotationCheck");
        yield return EnsureReviewState(_bulletCheck, _bulletPose, false, "BulletCheck");
        yield return EnsureReviewState(_taskCheck, _taskPose, false, "TaskCheck");

        LogPhysicalStates("after F9 reset");
    }

    /// <summary>
    /// The first call after TryBind is the F9/startup recovery hook and resets the shared trigger console from
    /// its REAL physical poses. Shell, powder, elevation and the rest of each gun's physical state are untouched.
    /// Later calls belong to normal tasks and must not clear shared controls.
    /// </summary>
    public IEnumerator PrepareForNewFireSolution(LeftRight leftRight) {
        if (_resetPendingAfterBind) {
            _resetPendingAfterBind = false;
            yield return ResetPhysicalFireControlsAfterBind();
            yield break;
        }

        LogPhysicalStates("before fire solution");
    }

    public IEnumerator Arm(LeftRight leftRight) {
        if (leftRight == LeftRight.Left)
            yield return EnsureArmState(_armLeft, _armLeftPose, true, "Left");
        else
            yield return EnsureArmState(_armRight, _armRightPose, true, "Right");

        yield return FcsRuntimeClock.WaitForSeconds(0.75f);
        LogPhysicalStates("after arm");
    }

    public IEnumerator ConfirmTask() {
        yield return EnsureReviewState(_taskCheck, _taskPose, true, "TaskCheck");
    }

    public IEnumerator ConfirmBullet() {
        yield return EnsureReviewState(_bulletCheck, _bulletPose, true, "BulletCheck");
    }

    public IEnumerator ConfirmRotation() {
        yield return EnsureReviewState(_rotationCheck, _rotationPose, true, "RotationCheck");
    }

    public IEnumerator ConfirmElevation() {
        yield return EnsureReviewState(_elevationCheck, _elevationPose, true, "ElevationCheck");
    }

    public IEnumerator ReadyToFire() {
        yield return EnsureReviewState(_readyFire, _readyPose, true, "ReadyToFire");
    }
}