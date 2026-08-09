using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class BallisticCalculator {
    private const float DialSettleSeconds = 0.5f;
    private const float CalculateClickTimeoutSeconds = 10f;
    private const float ResultSampleIntervalSeconds = 0.1f;
    private const float ResultMinimumSettleSeconds = 0.6f;
    private const float ResultSettleTimeoutSeconds = 3f;
    private const float ResultStableTolerance = 0.01f;
    private const int ResultStableSampleCount = 3;

    private DialInteractable? distanceDial;
    private DialInteractable? chargeDial;
    private DialInteractable? directionDial;
    private DialInteractable? shellDial;
    private LookAtTarget? calculateButton;
    private OdometerDisplay? elevationDisplay;

    private float requestedDistance;
    private float requestedCharge;
    private float requestedDirection;
    private BulletType requestedShell = BulletType.HE;

    private bool lastClickAccepted;
    private bool lastSettleSucceeded;
    private bool lastCalculationSucceeded;
    private float lastSettledElevation = float.NaN;

    public bool LastCalculationSucceeded => lastCalculationSucceeded;

    public bool TryBind() {
        var controls = GameObject.Find("Balistic Calculator Controls");
        if (controls == null) return Missing("Balistic Calculator Controls");

        var rangeParent = controls.transform.FindChild(".Range Dial Parent");
        if (rangeParent == null) return Missing(".Range Dial Parent");
        distanceDial = rangeParent.GetComponentInChildren<DialInteractable>();

        var chargeParent = controls.transform.FindChild(".Charge Dial Parent");
        if (chargeParent == null) return Missing(".Charge Dial Parent");
        chargeDial = chargeParent.GetComponentInChildren<DialInteractable>();

        directionDial = GameObject.Find(".Gross Range Dial")?.GetComponentInChildren<DialInteractable>();
        calculateButton = GameObject.Find("Calculate Universal Button")?.GetComponent<LookAtTarget>();
        elevationDisplay = GameObject.Find("Odomiter Output Elivation")?.GetComponent<OdometerDisplay>();
        shellDial = GameObject.Find(".Shell Dial")?.GetComponent<DialInteractable>();

        lastCalculationSucceeded = false;
        lastSettledElevation = float.NaN;

        return distanceDial != null
               && chargeDial != null
               && directionDial != null
               && calculateButton != null
               && elevationDisplay != null
               && shellDial != null;
    }

    private static bool Missing(string name) {
        MelonLogger.Warning($"[FCS] Can't find {name}，scene may not be loaded yet.");
        return false;
    }

    private static bool IsFinite(float value) {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void InvalidateResult() {
        lastCalculationSucceeded = false;
        lastSettledElevation = float.NaN;
    }
    
    public IEnumerator SetDistance(float distance) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        InvalidateResult();
        requestedDistance = distance;
        distanceDial?.SetDialValue(distance);
        yield return FcsRuntimeClock.WaitForSeconds(DialSettleSeconds);
    }
    
    public IEnumerator SetCharge(float charge) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        InvalidateResult();
        requestedCharge = charge;
        chargeDial?.SetDialValue(charge);
        yield return FcsRuntimeClock.WaitForSeconds(DialSettleSeconds);
    }

    public IEnumerator SetDirection(float angle) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        InvalidateResult();
        requestedDirection = angle;
        directionDial?.SetDialValue(angle);
        yield return FcsRuntimeClock.WaitForSeconds(DialSettleSeconds);
    }

    public IEnumerator SetShellType(BulletType type) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        InvalidateResult();
        requestedShell = type;
        shellDial?.SetDialValue((float)type);
        yield return FcsRuntimeClock.WaitForSeconds(DialSettleSeconds);
    }

    private IEnumerator ClickCalculateOnce() {
        lastClickAccepted = false;
        if (calculateButton == null) {
            MelonLogger.Error("[FCS BALLISTIC] Calculate button is not bound");
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + CalculateClickTimeoutSeconds;
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (calculateButton.isActive
                && calculateButton.nextAllowedClickTime <= Time.realtimeSinceStartup) {
                break;
            }

            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Error(
                    $"[FCS BALLISTIC] Calculate button did not become clickable within " +
                    $"{CalculateClickTimeoutSeconds:F0}s");
                yield break;
            }

            yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        }

        yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        yield return FcsRuntimeClock.WaitUntilFocused();
        calculateButton.OnClickDown();

        // Finish an accepted physical click even if focus changes between down and up.
        yield return new WaitForSeconds(0.1f);
        calculateButton.OnClickUp();
        lastClickAccepted = true;
    }

    private IEnumerator WaitForElevationSettled() {
        lastSettleSucceeded = false;
        if (elevationDisplay == null) {
            MelonLogger.Error("[FCS BALLISTIC] Elevation display is not bound");
            yield break;
        }

        var startedAt = FcsRuntimeClock.Now;
        var deadline = startedAt + ResultSettleTimeoutSeconds;
        var previous = elevationDisplay.currentNumber;
        var previousValid = IsFinite(previous);
        var stableSamples = 0;

        while (FcsRuntimeClock.Now < deadline) {
            yield return FcsRuntimeClock.WaitForSeconds(ResultSampleIntervalSeconds);
            yield return FcsRuntimeClock.WaitUntilFocused();

            var current = elevationDisplay.currentNumber;
            if (!IsFinite(current)) {
                stableSamples = 0;
                previousValid = false;
                continue;
            }

            if (previousValid && Mathf.Abs(current - previous) <= ResultStableTolerance)
                stableSamples++;
            else
                stableSamples = 1;

            previous = current;
            previousValid = true;

            if (FcsRuntimeClock.Now - startedAt >= ResultMinimumSettleSeconds
                && stableSamples >= ResultStableSampleCount) {
                lastSettledElevation = current;
                lastSettleSucceeded = true;
                yield break;
            }
        }

        MelonLogger.Error(
            $"[FCS BALLISTIC] Elevation output did not settle within {ResultSettleTimeoutSeconds:F1}s; " +
            $"last={(previousValid ? previous.ToString("F2") : "invalid")}");
    }

    public IEnumerator Calculate() {
        InvalidateResult();

        var before = elevationDisplay?.currentNumber ?? float.NaN;
        var verificationRetry = false;

        yield return FcsRuntimeClock.WaitUntilFocused();
        yield return ClickCalculateOnce();
        if (!lastClickAccepted)
            yield break;

        yield return WaitForElevationSettled();
        if (!lastSettleSucceeded)
            yield break;

        var firstResult = lastSettledElevation;

        // A stale output is most dangerous immediately after rebind/F9: the display may still show the previous
        // solution even though all four input dials have just been changed. If a full accepted Calculate click
        // leaves the display numerically unchanged, verify it once with a second complete click. Legitimately
        // identical solutions simply produce the same value twice; stale results get another chance to refresh.
        if (IsFinite(before) && Mathf.Abs(firstResult - before) <= ResultStableTolerance) {
            verificationRetry = true;
            MelonLogger.Warning(
                $"[FCS BALLISTIC] Calculate output remained {firstResult:F2} after input update; " +
                "verifying with a second full click");

            yield return ClickCalculateOnce();
            if (!lastClickAccepted)
                yield break;

            yield return WaitForElevationSettled();
            if (!lastSettleSucceeded)
                yield break;
        }

        lastCalculationSucceeded = true;
        MelonLogger.Msg(
            $"[FCS BALLISTIC] input: distance={requestedDistance:F3}km, direction={requestedDirection:F2}°, " +
            $"shell={requestedShell}, charge=C{requestedCharge:F0}; " +
            $"before={(IsFinite(before) ? before.ToString("F2") : "invalid")}°, " +
            $"output={lastSettledElevation:F2}°, verifyRetry={verificationRetry}");
    }
    
    public float GetElevation() {
        return lastCalculationSucceeded ? lastSettledElevation : float.NaN;
    }

    public static int MinimumCharge(float distance) {
        return distance switch {
            < 5.0f => 1,
            < 10.0f => 2,
            < 15.0f => 3,
            < 20.0f => 4,
            < 25.0f => 5,
            _ => 6
        };
    }
    
}
