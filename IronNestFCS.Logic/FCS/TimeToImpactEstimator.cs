namespace IronNestFCS.Logic.FCS;

/// <summary>
/// Early shell flight-time estimate derived from repeated in-game Time-To-Impact measurements.
/// For each valid powder charge, observed flight time is proportional to range:
/// TTI(seconds) = distance(km) * secondsPerKm(charge).
/// </summary>
internal static class TimeToImpactEstimator
{
    // Measured in-game coefficients, seconds per kilometre.
    private const float C1SecondsPerKm = 4.758869f;
    private const float C2SecondsPerKm = 3.830061f;
    private const float C3SecondsPerKm = 2.613011f;
    private const float C4SecondsPerKm = 1.894451f;
    private const float C5SecondsPerKm = 1.540442f;
    private const float C6SecondsPerKm = 1.427168f;

    public static bool TryEstimateSeconds(float distanceKm, int charge, out float seconds)
    {
        seconds = float.NaN;
        if (distanceKm <= 0f)
            return false;

        var secondsPerKm = charge switch
        {
            1 => C1SecondsPerKm,
            2 => C2SecondsPerKm,
            3 => C3SecondsPerKm,
            4 => C4SecondsPerKm,
            5 => C5SecondsPerKm,
            6 => C6SecondsPerKm,
            _ => float.NaN,
        };

        if (float.IsNaN(secondsPerKm) || float.IsInfinity(secondsPerKm) || secondsPerKm <= 0f)
            return false;

        seconds = distanceKm * secondsPerKm;
        return seconds > 0f && !float.IsNaN(seconds) && !float.IsInfinity(seconds);
    }
}
