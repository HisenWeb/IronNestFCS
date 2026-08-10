using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// Compact read-only physical probe for the five Review switches and two arming levers.
/// The release-build OFF/ON poses are already known, so normal diagnostics only need the durable
/// physical angle before/after a settled movement. Deep hierarchy/component dumps were useful during
/// discovery but made every runtime log unnecessarily large.
/// </summary>
public static class TriggerConsoleProbe {
    private const float RotationEpsilonDegrees = 0.05f;
    private const float SettleSeconds = 0.25f;

    private enum PoseAxis {
        X,
        Z,
    }

    private sealed class ControlState {
        public string Label = "";
        public Transform Pose = null!;
        public PoseAxis Axis;
        public float StableAngle;
        public float LastObservedAngle;
        public bool Dirty;
        public float LastMotionAt;
    }

    private static readonly List<ControlState> Controls = new();
    private static bool _bound;

    public static void BindAndLog() {
        Reset();

        var review = GameObject.Find(".Review Console Parent")?.transform;
        if (review == null) {
            MelonLogger.Warning("[FCS-TRIGGER-PROBE] .Review Console Parent missing");
            return;
        }

        var reviewRoots = new List<Transform>();
        for (var i = 0; i < review.childCount; i++) {
            var child = review.GetChild(i);
            if (child.name.StartsWith(".Check Switch"))
                reviewRoots.Add(child);
        }

        var labels = new[] { "Task", "Bullet", "Rotation", "Elevation", "Ready" };
        for (var i = 0; i < reviewRoots.Count && i < labels.Length; i++) {
            var pose = FindReviewPose(reviewRoots[i]);
            if (pose != null)
                AddControl(labels[i], pose, PoseAxis.Z);
        }

        var armLeft = GameObject.Find(".ArmingLeverParent Left")?.transform;
        var armRight = GameObject.Find(".ArmingLeverParent Right")?.transform;
        if (armLeft != null) AddControl("ArmLeft", armLeft, PoseAxis.X);
        if (armRight != null) AddControl("ArmRight", armRight, PoseAxis.X);

        _bound = Controls.Count > 0;
        MelonLogger.Msg($"[FCS-TRIGGER-PROBE] bound {Controls.Count} compact physical poses");
        foreach (var control in Controls) {
            MelonLogger.Msg(
                $"[FCS-TRIGGER-PROBE] {control.Label} baseline: {AxisName(control.Axis)}={control.StableAngle:F1}°");
        }
    }

    private static Transform? FindReviewPose(Transform root) {
        var transforms = root.GetComponentsInChildren<Transform>(true);
        foreach (var t in transforms) {
            if (t != null && t != root && t.name.StartsWith("knob_25_003"))
                return t;
        }
        return null;
    }

    private static void AddControl(string label, Transform pose, PoseAxis axis) {
        var angle = ReadAngle(pose, axis);
        Controls.Add(new ControlState {
            Label = label,
            Pose = pose,
            Axis = axis,
            StableAngle = angle,
            LastObservedAngle = angle,
        });
    }

    public static void Tick() {
        if (!_bound) return;
        var now = Time.realtimeSinceStartup;

        foreach (var control in Controls) {
            if (control.Pose == null)
                continue;

            var angle = ReadAngle(control.Pose, control.Axis);
            if (Mathf.Abs(Mathf.DeltaAngle(control.LastObservedAngle, angle)) > RotationEpsilonDegrees) {
                control.LastObservedAngle = angle;
                control.Dirty = true;
                control.LastMotionAt = now;
                continue;
            }

            if (!control.Dirty || now - control.LastMotionAt < SettleSeconds)
                continue;

            var settled = ReadAngle(control.Pose, control.Axis);
            if (Mathf.Abs(Mathf.DeltaAngle(control.StableAngle, settled)) > RotationEpsilonDegrees) {
                MelonLogger.Msg(
                    $"[FCS-TRIGGER-PROBE] {control.Label} settled-change: " +
                    $"{AxisName(control.Axis)}={control.StableAngle:F1}°->{settled:F1}°");
            }

            control.StableAngle = settled;
            control.LastObservedAngle = settled;
            control.Dirty = false;
        }
    }

    private static float ReadAngle(Transform pose, PoseAxis axis) {
        var euler = pose.localEulerAngles;
        return NormalizeAngle(axis == PoseAxis.X ? euler.x : euler.z);
    }

    private static string AxisName(PoseAxis axis) => axis == PoseAxis.X ? "X" : "Z";

    private static float NormalizeAngle(float angle) {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }

    public static void Reset() {
        Controls.Clear();
        _bound = false;
    }
}
