using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// Read-only diagnostic for the five Review Console switches and the two arming levers.
/// We deliberately do not trust LookAtTarget.GetActive()/isClicked here. Instead we watch the
/// actual transform/animator hierarchy and print only after a physical control has finished moving.
/// This lets us discover a durable OFF/ON signal without changing normal FCS behavior.
/// </summary>
public static class TriggerConsoleProbe {
    private const float PositionEpsilon = 0.00005f;
    private const float RotationEpsilonDegrees = 0.05f;
    private const float SettleSeconds = 0.25f;

    private sealed class NodeState {
        public Transform Transform = null!;
        public string Path = "";
        public Vector3 StablePosition;
        public Quaternion StableRotation;
        public Vector3 LastObservedPosition;
        public Quaternion LastObservedRotation;
    }

    private sealed class ControlState {
        public string Label = "";
        public Transform Root = null!;
        public LookAtTarget? LookAt;
        public readonly List<NodeState> Nodes = new();
        public readonly List<Animator> Animators = new();
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
        for (var i = 0; i < reviewRoots.Count && i < labels.Length; i++)
            AddControl(labels[i], reviewRoots[i]);

        var armLeft = GameObject.Find(".ArmingLeverParent Left")?.transform;
        var armRight = GameObject.Find(".ArmingLeverParent Right")?.transform;
        if (armLeft != null) AddControl("ArmLeft", armLeft);
        if (armRight != null) AddControl("ArmRight", armRight);

        _bound = Controls.Count > 0;
        MelonLogger.Msg($"[FCS-TRIGGER-PROBE] bound {Controls.Count} controls; toggle them normally and only settled physical changes will be logged");

        foreach (var control in Controls) {
            LogControlSnapshot(control, "baseline", changedOnly: false);
            DumpComponentLayout(control);
        }
    }

    private static void AddControl(string label, Transform root) {
        var state = new ControlState {
            Label = label,
            Root = root,
            LookAt = root.GetComponentInChildren<LookAtTarget>(true)
        };

        var transforms = root.GetComponentsInChildren<Transform>(true);
        foreach (var t in transforms) {
            if (t == null) continue;
            state.Nodes.Add(new NodeState {
                Transform = t,
                Path = RelativePath(root, t),
                StablePosition = t.localPosition,
                StableRotation = t.localRotation,
                LastObservedPosition = t.localPosition,
                LastObservedRotation = t.localRotation,
            });
        }

        var animators = root.GetComponentsInChildren<Animator>(true);
        foreach (var animator in animators) {
            if (animator != null)
                state.Animators.Add(animator);
        }

        Controls.Add(state);
    }

    public static void Tick() {
        if (!_bound) return;
        var now = Time.realtimeSinceStartup;

        foreach (var control in Controls) {
            var movedThisFrame = false;
            foreach (var node in control.Nodes) {
                var t = node.Transform;
                if (t == null) continue;

                var p = t.localPosition;
                var r = t.localRotation;
                if ((p - node.LastObservedPosition).sqrMagnitude > PositionEpsilon * PositionEpsilon ||
                    Quaternion.Angle(r, node.LastObservedRotation) > RotationEpsilonDegrees) {
                    movedThisFrame = true;
                }

                node.LastObservedPosition = p;
                node.LastObservedRotation = r;
            }

            if (movedThisFrame) {
                control.Dirty = true;
                control.LastMotionAt = now;
                continue;
            }

            if (!control.Dirty || now - control.LastMotionAt < SettleSeconds)
                continue;

            LogControlSnapshot(control, "settled-change", changedOnly: true);
            foreach (var node in control.Nodes) {
                var t = node.Transform;
                if (t == null) continue;
                node.StablePosition = t.localPosition;
                node.StableRotation = t.localRotation;
            }
            control.Dirty = false;
        }
    }

    private static void LogControlSnapshot(ControlState control, string reason, bool changedOnly) {
        var look = control.LookAt;
        string lookState;
        if (look == null) {
            lookState = "LookAt=null";
        }
        else {
            string active = "?", getActive = "?", clicked = "?", next = "?";
            try { active = look.isActive.ToString(); } catch { }
            try { getActive = look.GetActive().ToString(); } catch { }
            try { clicked = look.isClicked.ToString(); } catch { }
            try { next = look.nextAllowedClickTime.ToString("0.000"); } catch { }
            lookState = $"isActive={active} GetActive={getActive} isClicked={clicked} nextClick={next}";
        }

        MelonLogger.Msg($"[FCS-TRIGGER-PROBE] {control.Label} {reason}: {lookState}");

        var any = false;
        foreach (var node in control.Nodes) {
            var t = node.Transform;
            if (t == null) continue;
            var posChanged = (t.localPosition - node.StablePosition).sqrMagnitude > PositionEpsilon * PositionEpsilon;
            var rotChanged = Quaternion.Angle(t.localRotation, node.StableRotation) > RotationEpsilonDegrees;
            if (changedOnly && !posChanged && !rotChanged)
                continue;

            any = true;
            var e = NormalizeEuler(t.localEulerAngles);
            var oldE = NormalizeEuler(node.StableRotation.eulerAngles);
            MelonLogger.Msg(
                $"[FCS-TRIGGER-PROBE]   node={node.Path} " +
                $"pos {Fmt(node.StablePosition)} -> {Fmt(t.localPosition)} " +
                $"euler {Fmt(oldE)} -> {Fmt(e)}");
        }

        if (changedOnly && !any)
            MelonLogger.Msg($"[FCS-TRIGGER-PROBE]   no Transform delta survived settle; checking Animator state only");

        foreach (var animator in control.Animators) {
            if (animator == null) continue;
            try {
                var info = animator.GetCurrentAnimatorStateInfo(0);
                MelonLogger.Msg(
                    $"[FCS-TRIGGER-PROBE]   animator={RelativePath(control.Root, animator.transform)} " +
                    $"enabled={animator.enabled} state={info.fullPathHash} nt={info.normalizedTime:0.000} len={info.length:0.000}");
            }
            catch (Exception ex) {
                MelonLogger.Warning($"[FCS-TRIGGER-PROBE]   animator read failed: {ex.Message}");
            }
        }
    }

    private static void DumpComponentLayout(ControlState control) {
        MelonLogger.Msg($"[FCS-TRIGGER-PROBE] {control.Label} component layout:");
        foreach (var node in control.Nodes) {
            var t = node.Transform;
            if (t == null) continue;
            try {
                var components = t.gameObject.GetComponents<Component>();
                if (components == null || components.Length == 0) continue;
                var names = new List<string>();
                foreach (var component in components) {
                    if (component == null) continue;
                    names.Add(component.GetType().FullName ?? component.GetType().Name);
                }
                if (names.Count > 0)
                    MelonLogger.Msg($"[FCS-TRIGGER-PROBE]   {node.Path}: {string.Join(", ", names)}");
            }
            catch (Exception ex) {
                MelonLogger.Warning($"[FCS-TRIGGER-PROBE]   component dump failed at {node.Path}: {ex.Message}");
            }
        }
    }

    private static string RelativePath(Transform root, Transform current) {
        if (current == root) return root.name;
        var parts = new Stack<string>();
        var cursor = current;
        while (cursor != null && cursor != root) {
            parts.Push(cursor.name);
            cursor = cursor.parent;
        }
        return root.name + "/" + string.Join("/", parts);
    }

    private static Vector3 NormalizeEuler(Vector3 e) {
        return new Vector3(NormalizeAngle(e.x), NormalizeAngle(e.y), NormalizeAngle(e.z));
    }

    private static float NormalizeAngle(float angle) {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        return angle;
    }

    private static string Fmt(Vector3 v) => $"({v.x:0.0000},{v.y:0.0000},{v.z:0.0000})";

    public static void Reset() {
        Controls.Clear();
        _bound = false;
    }
}
