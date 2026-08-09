using System.Collections;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// FCS-only clock and focus gate.
///
/// Iron Nest may keep its world simulation running while the window is unfocused, while some
/// interaction/controller systems can lag behind or be re-enabled only after focus returns.
/// FCS therefore stops issuing new commands while unfocused, excludes that time from watchdogs,
/// and gives the game a short settle window after focus restoration before automation resumes.
/// </summary>
public static class FcsRuntimeClock {
    private const float FocusResumeSettleSeconds = 0.25f;

    private static bool initialized;
    private static bool wasApplicationFocused;
    private static float lastGameTime;
    private static float activeTime;
    private static float resumeNotBeforeRealtime;

    /// <summary>Increments whenever the application regains focus.</summary>
    public static int ResumeGeneration { get; private set; }

    /// <summary>
    /// True only when the application has focus and the short post-focus settle window has elapsed.
    /// This is the condition under which FCS may issue a new game interaction.
    /// </summary>
    public static bool IsFocused {
        get {
            SyncFocusState();
            return Application.isFocused && Time.realtimeSinceStartup >= resumeNotBeforeRealtime;
        }
    }

    /// <summary>
    /// Active FCS runtime. It advances only while FCS is allowed to run. Time spent unfocused,
    /// in the post-focus settle window, or at timeScale=0 is excluded automatically.
    /// </summary>
    public static float Now {
        get {
            SyncFocusState();
            return activeTime;
        }
    }

    public static void Reset() {
        initialized = true;
        wasApplicationFocused = Application.isFocused;
        lastGameTime = Time.time;
        activeTime = 0f;
        resumeNotBeforeRealtime = Application.isFocused
            ? Time.realtimeSinceStartup
            : float.PositiveInfinity;
        ResumeGeneration = 0;
    }

    /// <summary>Call once per frame so focus transitions are captured even while no task is polling.</summary>
    public static void Update() {
        SyncFocusState();
    }

    public static IEnumerator WaitUntilFocused() {
        while (true) {
            SyncFocusState();
            if (Application.isFocused && Time.realtimeSinceStartup >= resumeNotBeforeRealtime)
                yield break;
            yield return null;
        }
    }

    /// <summary>
    /// Delay measured in active FCS/game time. It pauses for timeScale=0, focus loss, and the
    /// post-focus settle window.
    /// </summary>
    public static IEnumerator WaitForSeconds(float seconds) {
        var deadline = Now + Mathf.Max(0f, seconds);
        while (Now < deadline) {
            yield return null;
        }
    }

    private static void SyncFocusState() {
        var gameNow = Time.time;
        var realtimeNow = Time.realtimeSinceStartup;
        var applicationFocused = Application.isFocused;

        if (!initialized) {
            initialized = true;
            wasApplicationFocused = applicationFocused;
            lastGameTime = gameNow;
            activeTime = 0f;
            resumeNotBeforeRealtime = applicationFocused
                ? realtimeNow
                : float.PositiveInfinity;
            ResumeGeneration = 0;
            return;
        }

        // Accumulate only the portion of game time during which FCS was already allowed to run.
        // Updating lastGameTime on every call prevents the background interval from being added
        // in one lump when focus returns.
        if (wasApplicationFocused && realtimeNow >= resumeNotBeforeRealtime) {
            activeTime += Mathf.Max(0f, gameNow - lastGameTime);
        }
        lastGameTime = gameNow;

        if (applicationFocused == wasApplicationFocused)
            return;

        if (!applicationFocused) {
            resumeNotBeforeRealtime = float.PositiveInfinity;
            MelonLogger.Msg("[FCS] Game focus lost; automation paused.");
        }
        else {
            ResumeGeneration++;
            resumeNotBeforeRealtime = realtimeNow + FocusResumeSettleSeconds;
            MelonLogger.Msg(
                $"[FCS] Game focus restored; resyncing for {FocusResumeSettleSeconds:F2}s before automation resumes.");
        }

        wasApplicationFocused = applicationFocused;
    }
}
