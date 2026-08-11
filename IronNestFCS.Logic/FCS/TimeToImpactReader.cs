using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// Read-only adapter for the game's mechanical Time To Impact dials.
/// The dial advances at 6 degrees per second. We capture the preset only after the gun reaches
/// WaitingForFire, then store that value on the corresponding FirePlan.
/// </summary>
internal static class TimeToImpactReader
{
    private const float DegreesPerSecond = 6f;
    private const float MinimumUsefulSeconds = 0.05f;
    private const float MaximumUsefulSeconds = 59.95f;

    private static Transform? _leftDial;
    private static Transform? _rightDial;

    public static bool TryReadEstimatedSeconds(LeftRight side, out float seconds)
    {
        seconds = float.NaN;

        var dial = GetDial(side);
        if (dial == null)
            return false;

        try
        {
            // Keep the full 0..360 degree value. Folding around 180 would alias 45s to 15s.
            var angle = dial.localEulerAngles.y % 360f;
            if (angle < 0f)
                angle += 360f;

            seconds = angle / DegreesPerSecond;
            if (seconds < MinimumUsefulSeconds || seconds > MaximumUsefulSeconds)
            {
                seconds = float.NaN;
                return false;
            }

            return true;
        }
        catch
        {
            Invalidate(side);
            seconds = float.NaN;
            return false;
        }
    }

    public static void Reset()
    {
        _leftDial = null;
        _rightDial = null;
    }

    private static Transform? GetDial(LeftRight side)
    {
        var cached = side == LeftRight.Left ? _leftDial : _rightDial;
        if (cached != null)
            return cached;

        var exactName = side == LeftRight.Left ? ".ImpactTimeDial_Left" : ".ImpactTimeDial_Right";
        Transform? fallback = null;

        try
        {
            foreach (var transform in Object.FindObjectsOfType<Transform>(true))
            {
                if (transform == null || !string.Equals(transform.name, exactName, StringComparison.Ordinal))
                    continue;

                var path = BuildPath(transform);
                if (!path.Contains("Time To Impact Dials", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (path.Contains("Main Camera/Static Gun Watch Parent", StringComparison.OrdinalIgnoreCase))
                {
                    cached = transform;
                    break;
                }

                fallback ??= transform;
            }
        }
        catch
        {
            return null;
        }

        cached ??= fallback;
        if (side == LeftRight.Left)
            _leftDial = cached;
        else
            _rightDial = cached;

        return cached;
    }

    private static void Invalidate(LeftRight side)
    {
        if (side == LeftRight.Left)
            _leftDial = null;
        else
            _rightDial = null;
    }

    private static string BuildPath(Transform transform)
    {
        var parts = new List<string>();
        var current = transform;
        var guard = 0;
        while (current != null && guard++ < 32)
        {
            parts.Add(current.name);
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }
}
