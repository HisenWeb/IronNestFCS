using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class BallisticCalculator {
    private DialInteractable? distanceDial;
    private DialInteractable? chargeDial;
    private DialInteractable? directionDial;
    private DialInteractable? shellDial;
    private LookAtTarget? calculateButton;
    private OdometerDisplay? elevationDisplay;

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
    
    public IEnumerator SetDistance(float distance) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        distanceDial?.SetDialValue(distance);
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
    }
    
    public IEnumerator SetCharge(float charge) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        chargeDial?.SetDialValue(charge);
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
    }

    public IEnumerator SetDirection(float angle) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        directionDial?.SetDialValue(angle);
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
    }

    public IEnumerator SetShellType(BulletType type) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        shellDial?.SetDialValue((float)type);
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
    }

    public IEnumerator Calculate() {
        yield return FcsRuntimeClock.WaitUntilFocused();
        calculateButton?.OnClickDown();
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
    }
    
    public float GetElevation() {
        return elevationDisplay?.currentNumber ?? 0;
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
