using System.Collections;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// FCS-only clock and focus gate.
///
/// Iron Nest can keep running while its window is unfocused. In that state some game-side
/// interaction/animation systems do not advance reliably, so FCS must not keep issuing controls
/// or consume watchdog budget. This clock advances with Time.time while focused, excludes any
/// period spent unfocused, and naturally also stops when the game sets timeScale to zero.
/// </summary>
public static class FcsRuntimeClock {
    private static bool initialized;
    private static bool wasFocused;
    private static float focusLostAt;
    private static float excludedUnfocusedTime;

    public static bool IsFocused => Application.isFocused;

    public static float Now {
        get {
            SyncFocusState();
            var now = Time.time;
            return wasFocused
                ? now - excludedUnfocusedTime
                : focusLostAt - excludedUnfocusedTime;
        }
    }

    public static void Reset() {
        initialized = true;
        wasFocused = Application.isFocused;
        focusLostAt = Time.time;
        excludedUnfocusedTime = 0f;
    }

    /// <summary>Call once per frame so focus transitions are captured even while no task is polling.</summary>
    public static void Update() {
        SyncFocusState();
    }

    public static IEnumerator WaitUntilFocused() {
        while (!Application.isFocused) {
            SyncFocusState();
            yield return null;
        }
        SyncFocusState();
    }

    /// <summary>
    /// Delay measured in active FCS/game time. It pauses both for timeScale=0 and for focus loss.
    /// </summary>
    public static IEnumerator WaitForSeconds(float seconds) {
        var deadline = Now + Mathf.Max(0f, seconds);
        while (Now < deadline) {
            yield return null;
        }
    }

    private static void SyncFocusState() {
        var now = Time.time;
        var focused = Application.isFocused;

        if (!initialized) {
            initialized = true;
            wasFocused = focused;
            focusLostAt = now;
            excludedUnfocusedTime = 0f;
            return;
        }

        if (focused == wasFocused)
            return;

        if (!focused) {
            focusLostAt = now;
            MelonLogger.Msg("[FCS] Game focus lost; automation paused.");
        }
        else {
            excludedUnfocusedTime += Mathf.Max(0f, now - focusLostAt);
            MelonLogger.Msg("[FCS] Game focus restored; automation resumed.");
        }

        wasFocused = focused;
    }
}
