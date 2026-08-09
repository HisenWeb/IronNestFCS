using IronNestFCS.Logic.FCS;
using UnityEngine;

namespace IronNestFCS.Logic.Scheduling;

internal static class FireReadyEstimator {
    // Release-build probes: turret azimuth ~= 4 deg/s, gun elevation ~= 2 deg/s.
    // FreshLoad probe pairs converged at 32.20s / 32.27s from ballistic registration to LoadedReady.
    private const float AzimuthSlewDegreesPerSecond = 4f;
    private const float ElevationSlewDegreesPerSecond = 2f;
    private const float FreshLoadReadySeconds = 32.25f;

    public const float EtaTieToleranceSeconds = 0.10f;
    public const float AlignmentTieTolerance = 0.05f;

    public static FireReadyEstimate Estimate(
        FirePriorityCandidate candidate,
        GunPhysicalState physical,
        float currentAzimuth,
        float now) {
        var azimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -candidate.Task.angel));
        var elevationDelta = Mathf.Abs(candidate.Task.elevation - physical.Elevation);
        var azimuthSeconds = azimuthDelta / AzimuthSlewDegreesPerSecond;
        var elevationSeconds = elevationDelta / ElevationSlewDegreesPerSecond;

        var loadKnown = true;
        var loadSeconds = 0f;
        var loadLabel = "已装填";
        var physicalMatchesTask = physical.LoadedReady
                                  && physical.ShellType == candidate.Task.bulletType
                                  && physical.PowderCharges == candidate.Task.chargeCount;

        if (physicalMatchesTask) {
            loadSeconds = 0f;
        }
        else if (physical.LoadedReady) {
            loadKnown = false;
            loadLabel = "实装弹药与任务不一致";
        }
        else if (candidate.Mode == GunTaskMode.FreshLoad) {
            var elapsed = Mathf.Max(0f, now - candidate.SolvedAt);
            loadSeconds = Mathf.Max(0f, FreshLoadReadySeconds - elapsed);
            loadLabel = $"FreshLoad 已过{elapsed:F1}s";

            // The 32.25s baseline is an estimate, not a readiness override. If the real gun is still loading
            // after the measured baseline, stop trusting the estimate and fall back to the old alignment model.
            if (elapsed > FreshLoadReadySeconds && !physical.LoadedReady) {
                loadKnown = false;
                loadLabel = $"FreshLoad 超过{FreshLoadReadySeconds:F2}s仍未就绪";
            }
        }
        else if (candidate.Mode == GunTaskMode.ReuseLoadedRound) {
            loadKnown = false;
            loadLabel = "复用弹物理状态已变化";
        }
        else {
            // CompleteShellLoaded begins with shell-in-chamber/C0. We have not yet measured a reliable remaining
            // powder/final-sequence ETA, so do not invent one for scheduling.
            loadKnown = false;
            loadLabel = "半装填ETA待测";
        }

        var localSeconds = loadKnown ? loadSeconds + elevationSeconds : float.NaN;
        var totalSeconds = loadKnown ? Mathf.Max(localSeconds, azimuthSeconds) : float.NaN;
        var alignmentScore = Mathf.Max(azimuthDelta, elevationDelta * 2f);

        return new FireReadyEstimate(
            loadKnown,
            loadLabel,
            loadSeconds,
            elevationSeconds,
            azimuthSeconds,
            totalSeconds,
            alignmentScore);
    }

    public static string FormatDetail(
        string sideLabel,
        FirePriorityCandidate candidate,
        FireReadyEstimate eta) {
        if (eta.LoadKnown) {
            return
                $"{sideLabel}T{candidate.Task.targetId}：预计{eta.TotalSeconds:F1}s（装{eta.LoadSeconds:F1}+仰{eta.ElevationSeconds:F1} / 方{eta.AzimuthSeconds:F1}）";
        }

        var alignmentSeconds = Mathf.Max(eta.ElevationSeconds, eta.AzimuthSeconds);
        return
            $"{sideLabel}T{candidate.Task.targetId}：ETA待测（{eta.LoadLabel}；仅对准{alignmentSeconds:F1}s）";
    }

    public static FirePriorityCandidate FirstByOriginalOrder(
        FirePriorityCandidate left,
        FirePriorityCandidate right) {
        if (left.SolvedAt < right.SolvedAt)
            return left;
        if (right.SolvedAt < left.SolvedAt)
            return right;
        return left;
    }
}
