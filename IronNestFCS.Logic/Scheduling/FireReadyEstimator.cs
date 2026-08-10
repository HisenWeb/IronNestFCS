using UnityEngine;

namespace IronNestFCS.Logic.Scheduling;

internal static class FireReadyEstimator
{
    public const float AzimuthSlewDegreesPerSecond = 4f;
    public const float ElevationSlewDegreesPerSecond = 2f;
    public const float FreshLoadReadySeconds = 32.25f;
    public const float EtaTieToleranceSeconds = 0.10f;
    public const float AlignmentTieTolerance = 0.05f;

    public static float AzimuthSeconds(float currentAzimuth, float targetBearing)
    {
        var delta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -targetBearing));
        return delta / AzimuthSlewDegreesPerSecond;
    }

    public static float ElevationSeconds(float currentElevation, float targetElevation)
    {
        return Mathf.Abs(targetElevation - currentElevation) / ElevationSlewDegreesPerSecond;
    }

    public static float AlignmentScore(float currentAzimuth, float targetBearing, float currentElevation, float targetElevation)
    {
        var azimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -targetBearing));
        var elevationDelta = Mathf.Abs(targetElevation - currentElevation);
        return Mathf.Max(azimuthDelta, elevationDelta * 2f);
    }
}
