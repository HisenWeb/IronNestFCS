using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// Early shell flight-time estimate using the inferred stock charge-speed curve.
/// Repeated in-game measurements strongly match a SmoothStep interpolation from about 210 m/s at C1
/// to about 700 m/s at C6. For a fixed charge, observed flight time remains proportional to range.
/// </summary>
internal static class TimeToImpactEstimator
{
    private const int MinCharge = 1;
    private const int MaxCharge = 6;
    private const float MinSpeedMetersPerSecond = 210f;
    private const float MaxSpeedMetersPerSecond = 700f;

    public static bool TryEstimateSeconds(float distanceKm, int charge, out float seconds)
    {
        seconds = float.NaN;
        if (distanceKm <= 0f || charge is < MinCharge or > MaxCharge)
            return false;

        var normalizedCharge = (charge - MinCharge) / (float)(MaxCharge - MinCharge);
        var speedMetersPerSecond = Mathf.SmoothStep(
            MinSpeedMetersPerSecond,
            MaxSpeedMetersPerSecond,
            normalizedCharge);

        if (float.IsNaN(speedMetersPerSecond)
            || float.IsInfinity(speedMetersPerSecond)
            || speedMetersPerSecond <= 0f)
        {
            return false;
        }

        seconds = distanceKm * 1000f / speedMetersPerSecond;
        return seconds > 0f && !float.IsNaN(seconds) && !float.IsInfinity(seconds);
    }
}
