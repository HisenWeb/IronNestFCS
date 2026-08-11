using System.Globalization;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// Temporary diagnostics-only probe for the game's shell trajectory stopwatch.
/// It discovers scene objects/components whose names suggest trajectory timing, records compact scalar
/// baselines, then logs only values that change. This is intentionally read-only and only runs when
/// detailed diagnostics are enabled by FcsModule.
/// </summary>
public static class TrajectoryStopwatchProbe {
    private const float SampleIntervalSeconds = 0.25f;
    private const int MaxMembersPerComponent = 80;

    private static readonly string[] CandidateKeywords = {
        "stopwatch",
        "trajectory",
        "flight",
        "timer",
        "clock",
        "impact",
    };

    private static readonly string[] HighValueMemberKeywords = {
        "time",
        "elapsed",
        "duration",
        "remaining",
        "second",
        "running",
        "active",
        "start",
        "stop",
        "value",
        "number",
        "text",
        "display",
        "current",
    };

    private static readonly HashSet<string> IgnoredMembers = new(StringComparer.OrdinalIgnoreCase) {
        "name", "tag", "hideFlags", "enabled", "isActiveAndEnabled",
        "gameObject", "transform", "m_CachedPtr", "Pointer", "WasCollected", "ObjectClass",
    };

    private sealed class WatchedMember {
        public Component Component = null!;
        public MemberInfo Member = null!;
        public string Label = "";
        public string LastValue = "";
    }

    private static readonly List<WatchedMember> Watched = new();
    private static readonly HashSet<int> SeenComponents = new();
    private static bool _bound;
    private static float _nextSampleAt;

    public static void BindAndLog() {
        Reset();

        try {
            var objects = Object.FindObjectsOfType<GameObject>(true);
            var matchedRoots = 0;

            foreach (var go in objects) {
                if (go == null)
                    continue;

                if (ContainsCandidateKeyword(go.name)) {
                    matchedRoots++;
                    foreach (var component in go.GetComponentsInChildren<Component>(true))
                        AddCandidate(component, $"object-name:{go.name}");
                }

                foreach (var component in go.GetComponents<Component>()) {
                    if (component == null)
                        continue;
                    var typeName = component.GetType().FullName ?? component.GetType().Name;
                    if (ContainsCandidateKeyword(typeName))
                        AddCandidate(component, $"component-type:{typeName}");
                }
            }

            _bound = SeenComponents.Count > 0;
            _nextSampleAt = Time.realtimeSinceStartup + SampleIntervalSeconds;
            MelonLogger.Msg(
                $"[FCS-FLIGHT-PROBE] discovery complete: matchedRoots={matchedRoots}, " +
                $"components={SeenComponents.Count}, watchedMembers={Watched.Count}");

            if (!_bound) {
                MelonLogger.Warning(
                    "[FCS-FLIGHT-PROBE] no stopwatch/trajectory/flight/timer/clock/impact candidates found");
            }
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS-FLIGHT-PROBE] discovery failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Tick() {
        if (!_bound)
            return;

        var now = Time.realtimeSinceStartup;
        if (now < _nextSampleAt)
            return;
        _nextSampleAt = now + SampleIntervalSeconds;

        foreach (var watched in Watched) {
            try {
                if (watched.Component == null)
                    continue;
                if (!TryReadValue(watched.Component, watched.Member, out var current))
                    continue;
                if (string.Equals(current, watched.LastValue, StringComparison.Ordinal))
                    continue;

                MelonLogger.Msg(
                    $"[FCS-FLIGHT-PROBE] change t={now:F3} | {watched.Label} | " +
                    $"{watched.LastValue} -> {current}");
                watched.LastValue = current;
            }
            catch {
                // A destroyed IL2CPP object must never interfere with normal fire-control execution.
            }
        }
    }

    private static void AddCandidate(Component? component, string reason) {
        if (component == null || component is Transform)
            return;

        int instanceId;
        try { instanceId = component.GetInstanceID(); }
        catch { return; }
        if (!SeenComponents.Add(instanceId))
            return;

        var path = BuildPath(component.transform);
        var type = component.GetType();
        var typeName = type.FullName ?? type.Name;
        MelonLogger.Msg(
            $"[FCS-FLIGHT-PROBE] candidate | path={path} | component={typeName} | " +
            $"active={component.gameObject.activeInHierarchy} | reason={reason}");

        IEnumerable<MemberInfo> members;
        try {
            members = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Cast<MemberInfo>()
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                .Where(m => !IgnoredMembers.Contains(m.Name))
                .Where(m => IsUsefulMemberType(GetMemberType(m)))
                .OrderByDescending(MemberScore)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxMembersPerComponent)
                .ToArray();
        }
        catch (Exception ex) {
            MelonLogger.Msg(
                $"[FCS-FLIGHT-PROBE] member-enumeration failed | path={path} | component={typeName} | " +
                $"{ex.GetType().Name}:{ex.Message}");
            return;
        }

        var added = 0;
        foreach (var member in members) {
            if (!TryReadValue(component, member, out var value))
                continue;

            var label = $"{path} | {type.Name}.{member.Name}";
            Watched.Add(new WatchedMember {
                Component = component,
                Member = member,
                Label = label,
                LastValue = value,
            });
            added++;
            MelonLogger.Msg($"[FCS-FLIGHT-PROBE] baseline | {label}={value}");
        }

        if (added == 0)
            MelonLogger.Msg($"[FCS-FLIGHT-PROBE] no scalar members | path={path} | component={typeName}");
    }

    private static Type GetMemberType(MemberInfo member) {
        return member switch {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => typeof(object),
        };
    }

    private static bool IsUsefulMemberType(Type type) {
        var effective = Nullable.GetUnderlyingType(type) ?? type;
        return effective.IsPrimitive
               || effective.IsEnum
               || effective == typeof(string)
               || effective == typeof(decimal)
               || effective == typeof(Vector2)
               || effective == typeof(Vector3)
               || effective == typeof(Vector4)
               || effective == typeof(Quaternion);
    }

    private static int MemberScore(MemberInfo member) {
        var score = 0;
        foreach (var keyword in HighValueMemberKeywords) {
            if (member.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                score += 10;
        }
        return score;
    }

    private static bool TryReadValue(Component component, MemberInfo member, out string value) {
        value = "";
        try {
            object? raw = member switch {
                PropertyInfo property => property.GetValue(component),
                FieldInfo field => field.GetValue(component),
                _ => null,
            };
            value = FormatValue(raw);
            return true;
        }
        catch {
            return false;
        }
    }

    private static string FormatValue(object? value) {
        if (value == null)
            return "null";

        string text = value switch {
            float f => f.ToString("0.######", CultureInfo.InvariantCulture),
            double d => d.ToString("0.########", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => value.ToString() ?? "",
        };

        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 240 ? text : text[..240] + "...";
    }

    private static bool ContainsCandidateKeyword(string? value) {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        foreach (var keyword in CandidateKeywords) {
            if (value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string BuildPath(Transform? transform) {
        if (transform == null)
            return "<no-transform>";

        var parts = new List<string>();
        var current = transform;
        var guard = 0;
        while (current != null && guard++ < 24) {
            parts.Add(current.name);
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    public static void Reset() {
        Watched.Clear();
        SeenComponents.Clear();
        _bound = false;
        _nextSampleAt = 0f;
    }
}
