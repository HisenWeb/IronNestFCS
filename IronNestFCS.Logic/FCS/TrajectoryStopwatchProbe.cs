using MelonLoader;
using Il2CppTMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// Temporary diagnostics-only probe for the game's shell time-to-impact display.
/// Discovery from the first broad probe showed stable scene objects named
/// .Time To Impact Dials / .ImpactTimeDial_Left / .ImpactTimeDial_Right.
/// This focused probe reads those exact display objects and their TMP text directly,
/// avoiding generic Component reflection under IL2CPP.
/// </summary>
public static class TrajectoryStopwatchProbe {
    private const float SampleIntervalSeconds = 0.10f;
    private const float RotationEpsilonDegrees = 0.05f;

    private sealed class DialState {
        public string Side = "";
        public Transform Dial = null!;
        public TMP_Text? Text;
        public Transform? Sfx;
        public bool LastActive;
        public bool LastSfxActive;
        public string LastText = "";
        public Vector3 LastEuler;
    }

    private sealed class NeedleState {
        public string Side = "";
        public Transform Needle = null!;
        public bool LastActive;
        public Vector3 LastEuler;
    }

    private static readonly List<DialState> Dials = new();
    private static readonly List<NeedleState> Needles = new();
    private static bool _bound;
    private static float _nextSampleAt;

    public static void BindAndLog() {
        Reset();

        try {
            BindImpactDial("Left", ".ImpactTimeDial_Left", "SFX_Recon_Countdown LEFT");
            BindImpactDial("Right", ".ImpactTimeDial_Right", "SFX_Recon_Countdown RIGHT");
            BindStopwatchNeedle("Left", "Left Gun Needle");
            BindStopwatchNeedle("Right", "Right Gun Needle");

            _bound = Dials.Count > 0 || Needles.Count > 0;
            _nextSampleAt = Time.realtimeSinceStartup + SampleIntervalSeconds;

            MelonLogger.Msg(
                $"[FCS-FLIGHT-PROBE] focused bind complete: dials={Dials.Count}, needles={Needles.Count}");

            if (!_bound)
                MelonLogger.Warning("[FCS-FLIGHT-PROBE] focused bind found no time-to-impact dials or stopwatch needles");
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS-FLIGHT-PROBE] focused bind failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Tick() {
        if (!_bound)
            return;

        var now = Time.realtimeSinceStartup;
        if (now < _nextSampleAt)
            return;
        _nextSampleAt = now + SampleIntervalSeconds;

        foreach (var dial in Dials) {
            try {
                if (dial.Dial == null)
                    continue;

                var active = dial.Dial.gameObject.activeInHierarchy;
                var text = SafeText(dial.Text);
                var euler = NormalizeEuler(dial.Dial.localEulerAngles);
                var sfxActive = dial.Sfx != null && dial.Sfx.gameObject.activeInHierarchy;

                if (active != dial.LastActive
                    || sfxActive != dial.LastSfxActive
                    || !string.Equals(text, dial.LastText, StringComparison.Ordinal)
                    || EulerChanged(dial.LastEuler, euler)) {
                    MelonLogger.Msg(
                        $"[FCS-FLIGHT-PROBE] TTI {dial.Side} t={now:F3} | " +
                        $"active={active} | text='{text}' | euler={FormatEuler(euler)} | sfx={sfxActive}");
                    dial.LastActive = active;
                    dial.LastSfxActive = sfxActive;
                    dial.LastText = text;
                    dial.LastEuler = euler;
                }
            }
            catch {
                // Probe must never interfere with fire control.
            }
        }

        foreach (var needle in Needles) {
            try {
                if (needle.Needle == null)
                    continue;

                var active = needle.Needle.gameObject.activeInHierarchy;
                var euler = NormalizeEuler(needle.Needle.localEulerAngles);
                if (active != needle.LastActive || EulerChanged(needle.LastEuler, euler)) {
                    MelonLogger.Msg(
                        $"[FCS-FLIGHT-PROBE] STOPWATCH {needle.Side} t={now:F3} | " +
                        $"active={active} | euler={FormatEuler(euler)}");
                    needle.LastActive = active;
                    needle.LastEuler = euler;
                }
            }
            catch {
            }
        }
    }

    private static void BindImpactDial(string side, string exactName, string sfxName) {
        var transform = FindPreferredTransform(exactName, requireTimeToImpactPath: true);
        if (transform == null) {
            MelonLogger.Warning($"[FCS-FLIGHT-PROBE] TTI {side} dial missing: {exactName}");
            return;
        }

        TMP_Text? text = null;
        try {
            var texts = transform.GetComponentsInChildren<TMP_Text>(true);
            if (texts.Length > 0)
                text = texts[0];
        }
        catch {
        }

        Transform? sfx = null;
        try {
            foreach (var child in transform.GetComponentsInChildren<Transform>(true)) {
                if (child != null && string.Equals(child.name, sfxName, StringComparison.Ordinal)) {
                    sfx = child;
                    break;
                }
            }
        }
        catch {
        }

        var state = new DialState {
            Side = side,
            Dial = transform,
            Text = text,
            Sfx = sfx,
            LastActive = transform.gameObject.activeInHierarchy,
            LastSfxActive = sfx != null && sfx.gameObject.activeInHierarchy,
            LastText = SafeText(text),
            LastEuler = NormalizeEuler(transform.localEulerAngles),
        };
        Dials.Add(state);

        MelonLogger.Msg(
            $"[FCS-FLIGHT-PROBE] TTI {side} baseline | path={BuildPath(transform)} | " +
            $"active={state.LastActive} | text='{state.LastText}' | euler={FormatEuler(state.LastEuler)} | " +
            $"sfx={state.LastSfxActive} | tmp={(text == null ? "missing" : text.GetType().Name)}");
    }

    private static void BindStopwatchNeedle(string side, string exactName) {
        var transform = FindPreferredTransform(exactName, requireTimeToImpactPath: false);
        if (transform == null)
            return;

        var path = BuildPath(transform);
        if (!path.Contains("/StopWatch/", StringComparison.OrdinalIgnoreCase))
            return;

        var state = new NeedleState {
            Side = side,
            Needle = transform,
            LastActive = transform.gameObject.activeInHierarchy,
            LastEuler = NormalizeEuler(transform.localEulerAngles),
        };
        Needles.Add(state);
        MelonLogger.Msg(
            $"[FCS-FLIGHT-PROBE] STOPWATCH {side} baseline | path={path} | " +
            $"active={state.LastActive} | euler={FormatEuler(state.LastEuler)}");
    }

    private static Transform? FindPreferredTransform(string exactName, bool requireTimeToImpactPath) {
        Transform? fallback = null;
        foreach (var transform in Object.FindObjectsOfType<Transform>(true)) {
            if (transform == null || !string.Equals(transform.name, exactName, StringComparison.Ordinal))
                continue;

            var path = BuildPath(transform);
            if (requireTimeToImpactPath
                && !path.Contains("Time To Impact Dials", StringComparison.OrdinalIgnoreCase))
                continue;

            // Prefer the always-active Static Gun Watch mirror discovered in the first probe.
            if (path.Contains("Main Camera/Static Gun Watch Parent", StringComparison.OrdinalIgnoreCase))
                return transform;

            fallback ??= transform;
        }
        return fallback;
    }

    private static string SafeText(TMP_Text? text) {
        try { return text?.text?.Replace("\r", " ").Replace("\n", " ").Trim() ?? "<no-tmp>"; }
        catch { return "<read-failed>"; }
    }

    private static bool EulerChanged(Vector3 a, Vector3 b) {
        return Mathf.Abs(Mathf.DeltaAngle(a.x, b.x)) > RotationEpsilonDegrees
               || Mathf.Abs(Mathf.DeltaAngle(a.y, b.y)) > RotationEpsilonDegrees
               || Mathf.Abs(Mathf.DeltaAngle(a.z, b.z)) > RotationEpsilonDegrees;
    }

    private static Vector3 NormalizeEuler(Vector3 euler) {
        return new Vector3(NormalizeAngle(euler.x), NormalizeAngle(euler.y), NormalizeAngle(euler.z));
    }

    private static float NormalizeAngle(float angle) {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }

    private static string FormatEuler(Vector3 euler) => $"({euler.x:F1},{euler.y:F1},{euler.z:F1})";

    private static string BuildPath(Transform? transform) {
        if (transform == null)
            return "<no-transform>";

        var parts = new List<string>();
        var current = transform;
        var guard = 0;
        while (current != null && guard++ < 32) {
            parts.Add(current.name);
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    public static void Reset() {
        Dials.Clear();
        Needles.Clear();
        _bound = false;
        _nextSampleAt = 0f;
    }
}
