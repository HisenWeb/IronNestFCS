using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// Read-only diagnostic probe for measuring the real azimuth/elevation slew rates from physical angles.
/// It never writes controller targets and does not participate in fire-order arbitration.
/// </summary>
public static class AimingSpeedProbe {
    private const float SampleIntervalSeconds = 0.10f;
    private const float MinimumMovingSpeed = 0.20f;
    private const float MaximumPlausibleSpeed = 120f;
    private const int StationarySamplesToCloseSegment = 3;
    private const int MinimumSegmentSamples = 5;
    private const float StableBandFraction = 0.20f;

    private sealed class AxisSampler {
        public readonly string Name;
        public readonly List<float> Segment = new();
        public readonly List<float> AcceptedSegments = new();
        public float PreviousAngle;
        public bool HasPrevious;
        public int StationarySamples;

        public AxisSampler(string name) {
            Name = name;
        }

        public void Reset() {
            Segment.Clear();
            AcceptedSegments.Clear();
            PreviousAngle = 0f;
            HasPrevious = false;
            StationarySamples = 0;
        }
    }

    private static TurretController? _turret;
    private static GunController? _leftGun;
    private static GunController? _rightGun;
    private static readonly AxisSampler Azimuth = new("AZ");
    private static readonly AxisSampler LeftElevation = new("EL Left");
    private static readonly AxisSampler RightElevation = new("EL Right");
    private static float _lastSampleTime;
    private static bool _bound;

    public static void Reset() {
        _turret = null;
        _leftGun = null;
        _rightGun = null;
        _lastSampleTime = 0f;
        _bound = false;
        Azimuth.Reset();
        LeftElevation.Reset();
        RightElevation.Reset();
    }

    public static bool BindAndLog() {
        _turret = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        _leftGun = GameObject.Find("GunLeft")?.GetComponent<GunController>();
        _rightGun = GameObject.Find("GunRight")?.GetComponent<GunController>();
        _bound = _turret != null && _leftGun != null && _rightGun != null;
        _lastSampleTime = Time.unscaledTime;

        if (_bound) {
            Seed(Azimuth, _turret!.CurrentAngle);
            Seed(LeftElevation, _leftGun!.CurrentElevation);
            Seed(RightElevation, _rightGun!.CurrentElevation);
            MelonLogger.Msg("[FCS SpeedProbe] bound; measuring physical azimuth/left-elevation/right-elevation slew rates");
        }
        else {
            MelonLogger.Warning(
                $"[FCS SpeedProbe] bind incomplete: turret={_turret != null}, left={_leftGun != null}, right={_rightGun != null}");
        }
        return _bound;
    }

    public static void Tick() {
        if (!_bound) {
            if (!BindAndLog())
                return;
        }

        var now = Time.unscaledTime;
        var dt = now - _lastSampleTime;
        if (dt < SampleIntervalSeconds)
            return;

        // Ignore unusually long frame gaps instead of turning them into artificially low speeds.
        if (dt > 0.50f) {
            _lastSampleTime = now;
            if (_turret != null) Seed(Azimuth, _turret.CurrentAngle);
            if (_leftGun != null) Seed(LeftElevation, _leftGun.CurrentElevation);
            if (_rightGun != null) Seed(RightElevation, _rightGun.CurrentElevation);
            return;
        }

        _lastSampleTime = now;
        if (_turret != null)
            Sample(Azimuth, _turret.CurrentAngle, dt, wrapAngle: true);
        if (_leftGun != null)
            Sample(LeftElevation, _leftGun.CurrentElevation, dt, wrapAngle: false);
        if (_rightGun != null)
            Sample(RightElevation, _rightGun.CurrentElevation, dt, wrapAngle: false);
    }

    private static void Seed(AxisSampler axis, float angle) {
        axis.PreviousAngle = angle;
        axis.HasPrevious = true;
    }

    private static void Sample(AxisSampler axis, float currentAngle, float dt, bool wrapAngle) {
        if (!axis.HasPrevious) {
            Seed(axis, currentAngle);
            return;
        }

        var delta = wrapAngle
            ? Mathf.Abs(Mathf.DeltaAngle(axis.PreviousAngle, currentAngle))
            : Mathf.Abs(currentAngle - axis.PreviousAngle);
        axis.PreviousAngle = currentAngle;

        var speed = delta / Mathf.Max(dt, 0.001f);
        if (speed >= MinimumMovingSpeed && speed <= MaximumPlausibleSpeed) {
            axis.Segment.Add(speed);
            axis.StationarySamples = 0;
            return;
        }

        axis.StationarySamples++;
        if (axis.StationarySamples < StationarySamplesToCloseSegment)
            return;

        CloseSegment(axis);
        axis.StationarySamples = 0;
    }

    private static void CloseSegment(AxisSampler axis) {
        if (axis.Segment.Count < MinimumSegmentSamples) {
            axis.Segment.Clear();
            return;
        }

        var median = Median(axis.Segment);
        var minStable = median * (1f - StableBandFraction);
        var maxStable = median * (1f + StableBandFraction);
        var stableSum = 0f;
        var stableCount = 0;
        foreach (var sample in axis.Segment) {
            if (sample < minStable || sample > maxStable)
                continue;
            stableSum += sample;
            stableCount++;
        }

        var stableAverage = stableCount > 0 ? stableSum / stableCount : median;
        axis.AcceptedSegments.Add(stableAverage);
        MelonLogger.Msg(
            $"[FCS SpeedProbe] {axis.Name} segment: samples={axis.Segment.Count}, " +
            $"median={median:F2}°/s, stableAvg={stableAverage:F2}°/s, stableSamples={stableCount}");
        axis.Segment.Clear();

        LogAggregateAndRatio();
    }

    private static void LogAggregateAndRatio() {
        var az = AggregateMedian(Azimuth);
        var left = AggregateMedian(LeftElevation);
        var right = AggregateMedian(RightElevation);

        if (az.HasValue) {
            MelonLogger.Msg(
                $"[FCS SpeedProbe] aggregate: AZ={az.Value:F2}°/s, " +
                $"EL-L={(left.HasValue ? left.Value.ToString("F2") : "-")}°/s, " +
                $"EL-R={(right.HasValue ? right.Value.ToString("F2") : "-")}°/s");
        }

        var elevationValues = new List<float>();
        if (left.HasValue) elevationValues.Add(left.Value);
        if (right.HasValue) elevationValues.Add(right.Value);
        if (!az.HasValue || elevationValues.Count == 0)
            return;

        var elevation = Median(elevationValues);
        if (az.Value <= 0.001f || elevation <= 0.001f)
            return;

        // Elevation is the 1.000 reference. Multiplying azimuth degrees by this coefficient converts them
        // to elevation-speed-equivalent degrees, so equal normalized values represent equal physical time.
        var azimuthCoefficient = elevation / az.Value;
        MelonLogger.Msg(
            $"[FCS SpeedProbe] normalized speed ratio: EL=1.000, AZ={azimuthCoefficient:F3} " +
            $"(AZ {az.Value:F2}°/s vs EL {elevation:F2}°/s)");
    }

    private static float? AggregateMedian(AxisSampler axis) {
        return axis.AcceptedSegments.Count == 0 ? null : Median(axis.AcceptedSegments);
    }

    private static float Median(List<float> values) {
        if (values.Count == 0)
            return 0f;

        var copy = values.ToArray();
        Array.Sort(copy);
        var middle = copy.Length / 2;
        return copy.Length % 2 == 0
            ? (copy[middle - 1] + copy[middle]) * 0.5f
            : copy[middle];
    }
}
