using System.Collections;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public enum BulletType {
    AP = 1,
    APHE = 2,
    ATMC = 3,
    CLMN = 4,
    CYAN = 5,
    DRIL = 6,
    EQKE = 7,
    FLCH = 8,
    HCHE = 9,
    HE = 10,
    INCN = 11,
    LE = 12,
    PLCM = 13,
    PHGN = 14,
    PRPG = 15,
    SMK = 16,
    STAR = 17,
    TEAR = 18,
    THRM = 19,
    WP = 20,
}

public class GunSystem {
    private const float ElevationToleranceDegrees = 0.05f;
    private const float ReloadControlTimeoutSeconds = 60f;
    private const float ShellChamberTimeoutSeconds = 15f;
    private const float PowderControlResumeGraceSeconds = 2f;
    private const float PowderCommitTimeoutSeconds = 12f;
    private const float MinimumPostShotRecoverySeconds = 13f;
    private const float RecoveryElevationVelocityTolerance = 0.05f;
    private const float ReloadTraceIntervalSeconds = 5f;
    private const float ControlTraceIntervalSeconds = 3f;

    private string _surfix = "";

    private CylinderShellSelector? shellSelector;
    private readonly List<string?> bullets = new();
    private LookAtTarget? nextBulletButton;
    private LookAtTarget? loadBulletButton;
    private readonly List<LookAtTarget> powderButtons = new();
    private LookAtTarget? loadPowderButton;
    private GunController? gunController;
    private ArtilleryReloadController? reloadController;
    private LinearSliderInteractable? elevationLever;
    private OdometerDisplay? remainingCharges;
    private TextMeshPro? shellId;

    // In the release build TurretController can re-derive each gun's elevation target
    // from the physical elevation controls every frame. FCS needs to temporarily own
    // that target so the two guns can hold independent precomputed elevations.
    private static TurretController? sharedTurretController;
    private static int elevationOverrideUsers;
    private static bool? savedDriveGunElevationsFromController;
    private bool elevationOverrideHeld;

    public bool LastElevationSucceeded { get; private set; }
    public bool LastFireObserved { get; private set; }
    public bool LastReloadReadySucceeded { get; private set; }
    public bool LastReloadActionSucceeded { get; private set; }
    public string LastReloadFailureReason { get; private set; } = "";

    public bool TryBind(string surfix) {
        _surfix = surfix;
        powderButtons.Clear();
        elevationOverrideHeld = false;
        LastReloadFailureReason = "";

        var gunSystemObject = GameObject.Find("Gun System " + surfix);
        if (gunSystemObject == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find Gun System");
            return false;
        }
        var gunSystem = gunSystemObject.transform;

        var reloadingConsole = gunSystem.Find("--Reloading Console");
        if (reloadingConsole == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find --Reloading Console");
            return false;
        }

        remainingCharges = reloadingConsole.GetComponentInChildren<OdometerDisplay>();
        var nextBulletObject = reloadingConsole.Find("Universal Button Move Cylinder");
        nextBulletButton = nextBulletObject?.GetComponent<LookAtTarget>();
        shellSelector = gunSystem.GetComponentInChildren<CylinderShellSelector>();

        shellId = GameObject.Find("Shell ID " + surfix)?.GetComponent<TextMeshPro>();
        var loadShell = reloadingConsole.FindChild("Universal Button Load shell Rammer");
        if (loadShell == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find Universal Button Load shell Rammer");
            return false;
        }
        loadBulletButton = loadShell.GetComponent<LookAtTarget>();

        var powderController = reloadingConsole.Find("PowderChargeController");
        if (powderController == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find PowderChargeController");
            return false;
        }
        for (var i = 0; i < powderController.childCount; ++i) {
            var child = powderController.GetChild(i);
            if (!child.name.StartsWith("Button Dispencer")) continue;
            var button = child.GetComponent<LookAtTarget>();
            if (button == null) {
                MelonLogger.Error($"[FCS] GunSystem {surfix}: Found {child.name} but lack of LookAtTarget Component");
                return false;
            }
            powderButtons.Add(button);
        }

        var loadPowderObject = reloadingConsole.FindChild("Universal Button Charge Rammer (1)");
        loadPowderButton = loadPowderObject?.GetComponent<LookAtTarget>();
        gunController = GameObject.Find("Gun" + surfix)?.GetComponent<GunController>();
        reloadController = gunController?.artilleryReloadController;
        var elevationBase = GameObject.Find(".Elevation Lever Baseplate");
        elevationLever = elevationBase?.transform.FindChild(".Elevation Lever " + surfix)
            ?.GetComponent<LinearSliderInteractable>();
        sharedTurretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();

        if (elevationLever != null && gunController != null) {
            MelonLogger.Msg(
                $"[FCS] GunSystem {surfix}: elevation slider value={elevationLever.Value:F2}, " +
                $"range={elevationLever.minOutputValue:F2}..{elevationLever.maxOutputValue:F2}, " +
                $"gun current={gunController.CurrentElevation:F2}, desired={gunController.DesiredElevationAngle:F2}");
        }
        if (reloadController != null) {
            MelonLogger.Msg(
                $"[FCS] GunSystem {surfix}: reload state={reloadController.CurrentStateIndex}, working={reloadController.working}");
        }
        else {
            MelonLogger.Warning($"[FCS] GunSystem {surfix}: ArtilleryReloadController unavailable; reload recovery will use fallback checks");
        }

        var ok = remainingCharges != null
                 && nextBulletButton != null
                 && shellSelector != null
                 && loadBulletButton != null
                 && powderButtons.Count >= 6
                 && loadPowderButton != null
                 && gunController != null
                 && elevationLever != null
                 && sharedTurretController != null;
        if (!ok) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: one or more controls could not be bound");
        }
        return ok;
    }
    
    public bool CanFire() {
        return gunController != null && gunController.CanFire;
    }

    private bool AcquireElevationOverride() {
        if (elevationOverrideHeld) return true;

        if (sharedTurretController == null) {
            sharedTurretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        }
        if (sharedTurretController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: TurretController unavailable for elevation override");
            return false;
        }

        try {
            if (elevationOverrideUsers == 0) {
                savedDriveGunElevationsFromController = sharedTurretController.driveGunElevationsFromController;
                sharedTurretController.driveGunElevationsFromController = false;
                MelonLogger.Msg(
                    $"[FCS] Elevation override acquired; saved driveGunElevationsFromController={savedDriveGunElevationsFromController}");
            }
            elevationOverrideUsers++;
            elevationOverrideHeld = true;
            return true;
        }
        catch (Exception ex) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: failed to acquire elevation override: {ex.Message}");
            return false;
        }
    }

    public void ReleaseElevationOverride() {
        if (!elevationOverrideHeld) return;
        elevationOverrideHeld = false;
        elevationOverrideUsers = Math.Max(0, elevationOverrideUsers - 1);

        if (elevationOverrideUsers != 0) return;
        try {
            if (sharedTurretController != null && savedDriveGunElevationsFromController.HasValue) {
                sharedTurretController.driveGunElevationsFromController = savedDriveGunElevationsFromController.Value;
                MelonLogger.Msg(
                    $"[FCS] Elevation override released; restored driveGunElevationsFromController={savedDriveGunElevationsFromController.Value}");
            }
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] Failed to restore gun elevation drive: {ex.Message}");
        }
        finally {
            savedDriveGunElevationsFromController = null;
        }
    }

    public IEnumerator SetElevation(float elevation, float timeoutSeconds = 30f) {
        LastElevationSucceeded = false;
        if (elevationLever == null || gunController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Elevation lever or gun controller unbound");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        if (!AcquireElevationOverride()) {
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + Mathf.Max(1f, timeoutSeconds);

        gunController.SetDesiredElevation(elevation);

        var sliderMin = Mathf.Min(elevationLever.minOutputValue, elevationLever.maxOutputValue);
        var sliderMax = Mathf.Max(elevationLever.minOutputValue, elevationLever.maxOutputValue);
        if (elevation >= sliderMin && elevation <= sliderMax) {
            elevationLever.SetSliderValue(elevation);
        }
        else {
            MelonLogger.Warning(
                $"[FCS] GunSystem {_surfix}: target {elevation:F2}° is outside elevation slider range " +
                $"{sliderMin:F2}..{sliderMax:F2}; driving GunController directly");
        }

        yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (Mathf.Abs(gunController.CurrentElevation - elevation) <= ElevationToleranceDegrees)
                break;

            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Error(
                    $"[FCS] GunSystem {_surfix}: elevation timeout, current={gunController.CurrentElevation:F2}, " +
                    $"desired={gunController.DesiredElevationAngle:F2}, target={elevation:F2}, " +
                    $"slider={elevationLever.Value:F2} range={sliderMin:F2}..{sliderMax:F2}");
                yield break;
            }

            gunController.SetDesiredElevation(elevation);
            yield return FcsRuntimeClock.WaitForSeconds(0.25f);
        }
        LastElevationSucceeded = true;
    }

    private static string? NormalizeShellId(string? id) {
        return id == "PCLM" ? "PLCM" : id;
    }
    
    public string? BulletInChamber() {
        return NormalizeShellId(gunController?.ChamberedShellBlueprint?.shellDefinition?.ShellId);
    }
    
    public bool IsChamberEmpty() {
        return BulletInChamber() == null;
    }

    private void RefreshBullets() {
        bullets.Clear();
        if (shellSelector == null) return;
        foreach (var shell in shellSelector.bullets) {
            bullets.Add(NormalizeShellId(shell?.GetComponent<ShellBlueprint>()?.shellDefinition?.ShellId));
        }
        MelonLogger.Msg($"[FCS] GunSystem {_surfix}: Cylinder bullets: {string.Join(", ", bullets)}");
    }

    // Kept for compatibility with older callers. The automation path below uses ClickReloadControl so cylinder
    // rotation participates in the same complete down/up and F9-release discipline as every other reload control.
    public void NextBullet() {
        if (nextBulletButton == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: NextBulletButton unbound");
            return;
        }
        MelonLogger.Warning($"[FCS ReloadTrace] {_surfix}: legacy NextBullet() invoked; completing click synchronously");
        try {
            FcsSceneInteractor.BeginPhysicalClick(nextBulletButton);
            FcsSceneInteractor.EndPhysicalClick(nextBulletButton);
        }
        catch (Exception ex) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: legacy NextBullet click failed: {ex.Message}");
        }
    }

    private void FailReloadAction(string reason) {
        LastReloadActionSucceeded = false;
        LastReloadFailureReason = reason;
        MelonLogger.Error($"[FCS] GunSystem {_surfix}: {reason}");
    }

    private IEnumerator WaitForReloadReady(
        GunPhysicalStateKind? expectedStableKind,
        float timeoutSeconds) {
        LastReloadReadySucceeded = false;
        if (gunController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: gun controller unbound while waiting for reload readiness");
            yield break;
        }

        var startedAt = FcsRuntimeClock.Now;
        var deadline = startedAt + Mathf.Max(1f, timeoutSeconds);
        var nextTraceAt = startedAt + ReloadTraceIntervalSeconds;
        var expectedText = expectedStableKind.HasValue
            ? expectedStableKind.Value.ToString()
            : "EmptyReady/ShellLoaded";
        MelonLogger.Msg(
            $"[FCS ReloadTrace] {_surfix}: waiting handoff={expectedText}; start={GunPhysicalState.Read(_surfix).Summary()}");

        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            var physical = GunPhysicalState.Read(_surfix);
            var interactionReady = reloadController == null
                ? !gunController.ExternalReloadLoweringLocked
                : expectedStableKind.HasValue
                    ? physical.Kind == expectedStableKind.Value
                    : physical.EmptyReady || physical.ShellLoaded;
            var breechReady = !gunController.ExternalReloadLoweringLocked;
            var motionReady = Mathf.Abs(gunController.elevationChangeVelocity) <= RecoveryElevationVelocityTolerance;
            if (interactionReady && breechReady && motionReady) {
                MelonLogger.Msg(
                    $"[FCS ReloadTrace] {_surfix}: handoff={expectedText} ready after " +
                    $"{FcsRuntimeClock.Now - startedAt:F1}s; {physical.Summary()}");
                break;
            }

            if (FcsRuntimeClock.Now >= nextTraceAt) {
                MelonLogger.Warning(
                    $"[FCS Stall] {_surfix}: waiting reload handoff={expectedText} for " +
                    $"{FcsRuntimeClock.Now - startedAt:F1}s; physical={physical.Summary()}, " +
                    $"breechLocked={gunController.ExternalReloadLoweringLocked}, " +
                    $"elevationVelocity={gunController.elevationChangeVelocity:F3}");
                nextTraceAt += ReloadTraceIntervalSeconds;
            }

            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Error(
                    $"[FCS] GunSystem {_surfix}: reload mechanism did not reach expected safe handoff {expectedText}; " +
                    $"physical={physical.Summary()}, breechLocked={gunController.ExternalReloadLoweringLocked}, " +
                    $"elevationVelocity={gunController.elevationChangeVelocity:F3}");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.25f);
        }

        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
        yield return FcsRuntimeClock.WaitUntilFocused();
        LastReloadReadySucceeded = true;
    }

    /// <summary>
    /// Wait for a release-build reload interaction handoff that has been verified by the physical-state probe.
    /// If the caller is already at EmptyReady or ShellLoaded, lock onto that exact handoff so an unrelated
    /// stable state cannot satisfy the wait after a concurrent physical change/F9 recovery.
    /// </summary>
    public IEnumerator WaitForReloadReady(float timeoutSeconds = ReloadControlTimeoutSeconds) {
        var initial = GunPhysicalState.Read(_surfix);
        GunPhysicalStateKind? expected = initial.EmptyReady
            ? GunPhysicalStateKind.EmptyReady
            : initial.ShellLoaded
                ? GunPhysicalStateKind.ShellLoaded
                : null;
        yield return WaitForReloadReady(expected, timeoutSeconds);
    }

    private IEnumerator ClickReloadControl(LookAtTarget? button, string controlName,
        float timeoutSeconds = ReloadControlTimeoutSeconds) {
        LastReloadActionSucceeded = false;
        LastReloadFailureReason = "";
        if (button == null) {
            FailReloadAction($"reload control missing: {controlName}");
            yield break;
        }

        var startedAt = FcsRuntimeClock.Now;
        var deadline = startedAt + Mathf.Max(0.1f, timeoutSeconds);
        var nextTraceAt = startedAt + ControlTraceIntervalSeconds;
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (button.isActive && button.nextAllowedClickTime <= Time.realtimeSinceStartup)
                break;

            if (FcsRuntimeClock.Now >= nextTraceAt) {
                MelonLogger.Warning(
                    $"[FCS Stall] {_surfix}: reload control '{controlName}' not clickable after " +
                    $"{FcsRuntimeClock.Now - startedAt:F1}s; active={button.isActive}, " +
                    $"nextAllowed={button.nextAllowedClickTime:F2}, realtime={Time.realtimeSinceStartup:F2}, " +
                    $"physical={GunPhysicalState.Read(_surfix).Summary()}");
                nextTraceAt += ControlTraceIntervalSeconds;
            }

            if (FcsRuntimeClock.Now >= deadline) {
                FailReloadAction($"reload control timed out: {controlName}");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        }

        MelonLogger.Msg(
            $"[FCS ReloadTrace] {_surfix}: clicking '{controlName}' after {FcsRuntimeClock.Now - startedAt:F2}s; " +
            $"physical={GunPhysicalState.Read(_surfix).Summary()}");
        yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        yield return FcsRuntimeClock.WaitUntilFocused();
        try {
            FcsSceneInteractor.BeginPhysicalClick(button);
        }
        catch (Exception ex) {
            FailReloadAction($"reload control click-down failed ({controlName}): {ex.Message}");
            yield break;
        }

        // Once a click starts, always complete the down/up pair even if focus changes in between.
        // FcsSceneInteractor also tracks the held control so F9/Shutdown can release it if this coroutine stops.
        yield return new WaitForSeconds(0.1f);
        try {
            FcsSceneInteractor.EndPhysicalClick(button);
        }
        catch (Exception ex) {
            FailReloadAction($"reload control click-up failed ({controlName}): {ex.Message}");
            yield break;
        }

        LastReloadActionSucceeded = true;
        MelonLogger.Msg($"[FCS ReloadTrace] {_surfix}: completed click '{controlName}'");
    }

    private IEnumerator WaitForChamberedShell(BulletType type,
        float timeoutSeconds = ShellChamberTimeoutSeconds) {
        LastReloadActionSucceeded = false;
        var expected = type.ToString();
        var startedAt = FcsRuntimeClock.Now;
        var deadline = startedAt + Mathf.Max(1f, timeoutSeconds);
        var nextTraceAt = startedAt + ControlTraceIntervalSeconds;

        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            var chamber = BulletInChamber();
            if (chamber == expected) {
                MelonLogger.Msg(
                    $"[FCS ReloadTrace] {_surfix}: chamber confirmed {expected} after " +
                    $"{FcsRuntimeClock.Now - startedAt:F1}s; {GunPhysicalState.Read(_surfix).Summary()}");
                LastReloadActionSucceeded = true;
                yield break;
            }

            if (FcsRuntimeClock.Now >= nextTraceAt) {
                MelonLogger.Warning(
                    $"[FCS Stall] {_surfix}: waiting chamber={expected} for " +
                    $"{FcsRuntimeClock.Now - startedAt:F1}s; chamber={chamber ?? "empty"}, " +
                    $"physical={GunPhysicalState.Read(_surfix).Summary()}");
                nextTraceAt += ControlTraceIntervalSeconds;
            }

            if (FcsRuntimeClock.Now >= deadline) {
                FailReloadAction(
                    $"shell rammer did not chamber {expected}; chamber={chamber ?? "empty"}");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        }
    }
    
    /// <summary>
    /// 装填指定弹种：先把弹仓转到目标弹，再确认装填机构稳定，推弹后确认炮弹确实进入炮膛。
    /// </summary>
    public IEnumerator LoadBullet(BulletType type) {
        LastReloadActionSucceeded = false;
        LastReloadFailureReason = "";
        yield return FcsRuntimeClock.WaitUntilFocused();
        MelonLogger.Msg(
            $"[FCS ReloadTrace] {_surfix}: LoadBullet({type}) start; physical={GunPhysicalState.Read(_surfix).Summary()}");
        RefreshBullets();
        if (bullets.Count == 0 || !bullets.Contains(type.ToString())) {
            FailReloadAction($"No {type} available in cylinder");
            yield break;
        }
        
        for (var i = 0; i < bullets.Count; ++i) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (bullets.Count > 0 && bullets[0] == type.ToString()) {
                break;
            }

            MelonLogger.Msg(
                $"[FCS ReloadTrace] {_surfix}: rotating cylinder for {type}; step={i + 1}/{bullets.Count}, " +
                $"front={bullets[0] ?? "empty"}");
            yield return ClickReloadControl(nextBulletButton, "Universal Button Move Cylinder", 10f);
            if (!LastReloadActionSucceeded) yield break;

            yield return FcsRuntimeClock.WaitForSeconds(1.5f);
            yield return FcsRuntimeClock.WaitUntilFocused();
            RefreshBullets();
        }
        if (bullets.Count == 0 || bullets[0] != type.ToString()) {
            FailReloadAction($"Can't find {type} after cylinder rotation, current: {string.Join(", ", bullets)}");
            yield break;
        }

        MelonLogger.Msg($"[FCS ReloadTrace] {_surfix}: cylinder aligned to {type}");

        // Cylinder positioning belongs to the empty-gun handoff. Do not accept ShellLoaded here: that would
        // allow a concurrent/manual chamber change to fall through and attempt to ram a second shell.
        yield return WaitForReloadReady(GunPhysicalStateKind.EmptyReady, ReloadControlTimeoutSeconds);
        if (!LastReloadReadySucceeded) {
            FailReloadAction("reload mechanism was not ready after cylinder positioning");
            yield break;
        }

        yield return ClickReloadControl(loadBulletButton, "Universal Button Load shell Rammer");
        if (!LastReloadActionSucceeded) yield break;

        // A successful OnClickDown/OnClickUp only proves that the UI accepted the interaction.
        // Do not proceed to powder until the durable game state confirms the requested shell is
        // actually in the chamber.
        yield return WaitForChamberedShell(type);
        if (!LastReloadActionSucceeded) yield break;

        // After the shell appears, wait specifically for state 5/SelectPowderCharge. EmptyReady is not a valid
        // completion for this phase and must never be allowed to masquerade as a successful shell ram.
        yield return WaitForReloadReady(GunPhysicalStateKind.ShellLoaded, ReloadControlTimeoutSeconds);
        if (!LastReloadReadySucceeded) {
            FailReloadAction("reload mechanism did not settle after shell ramming");
            yield break;
        }
        LastReloadActionSucceeded = true;
        MelonLogger.Msg(
            $"[FCS ReloadTrace] {_surfix}: LoadBullet({type}) complete; {GunPhysicalState.Read(_surfix).Summary()}");
    }

    private string PowderControlsSummary(int requiredCount) {
        var count = Math.Min(requiredCount, powderButtons.Count);
        var states = new List<string>();
        for (var i = 0; i < count; i++) {
            states.Add($"{i + 1}:{(powderButtons[i].isActive ? "A" : "I")}");
        }
        return $"rammer={(loadPowderButton?.isActive == true ? "A" : "I")}, required=[{string.Join(",", states)}]";
    }

    private IEnumerator WaitForPowderCommit(int expectedCount, string? shellAtStart) {
        LastReloadActionSucceeded = false;
        var startedAt = FcsRuntimeClock.Now;
        var deadline = startedAt + PowderCommitTimeoutSeconds;
        var nextTraceAt = startedAt + ControlTraceIntervalSeconds;

        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            var physical = GunPhysicalState.Read(_surfix);

            if (physical.PowderCharges > 0) {
                if (physical.PowderCharges != expectedCount) {
                    FailReloadAction(
                        $"powder commit mismatch: expected C{expectedCount}, physical C{physical.PowderCharges}; " +
                        $"{physical.Summary()}");
                    yield break;
                }

                var shellNow = NormalizeShellId(physical.ShellId);
                if (shellAtStart != null && shellNow != null && shellNow != shellAtStart) {
                    FailReloadAction(
                        $"shell changed while committing powder: expected {shellAtStart}, got {shellNow}; {physical.Summary()}");
                    yield break;
                }

                MelonLogger.Msg(
                    $"[FCS ReloadResume] {_surfix}: powder committed as C{physical.PowderCharges} after " +
                    $"{FcsRuntimeClock.Now - startedAt:F1}s; {physical.Summary()}");
                LastReloadActionSucceeded = true;
                yield break;
            }

            if (physical.EmptyReady || physical.Kind == GunPhysicalStateKind.PostShotRecovery) {
                FailReloadAction($"powder commit lost chambered shell; {physical.Summary()}");
                yield break;
            }

            if (FcsRuntimeClock.Now >= nextTraceAt) {
                MelonLogger.Warning(
                    $"[FCS Stall] {_surfix}: waiting powder commit C{expectedCount} for " +
                    $"{FcsRuntimeClock.Now - startedAt:F1}s; physical={physical.Summary()}, " +
                    PowderControlsSummary(expectedCount));
                nextTraceAt += ControlTraceIntervalSeconds;
            }

            if (FcsRuntimeClock.Now >= deadline) {
                FailReloadAction(
                    $"powder rammer did not commit C{expectedCount} within {PowderCommitTimeoutSeconds:F0}s; " +
                    $"{physical.Summary()}, {PowderControlsSummary(expectedCount)}");
                yield break;
            }

            yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        }
    }

    public IEnumerator LoadPowder(int count) {
        LastReloadActionSucceeded = false;
        LastReloadFailureReason = "";
        if (count <= 0 || count > powderButtons.Count) {
            FailReloadAction($"invalid powder count {count}, available buttons={powderButtons.Count}");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        var startState = GunPhysicalState.Read(_surfix);
        if (!startState.ShellLoaded) {
            FailReloadAction($"powder loading requires shell-loaded handoff; {startState.Summary()}");
            yield break;
        }

        var shellAtStart = NormalizeShellId(startState.ShellId);
        MelonLogger.Msg(
            $"[FCS ReloadResume] {_surfix}: powder stage start expected=C{count}, " +
            $"physical={startState.Summary()}, {PowderControlsSummary(count)}");

        // If F9 resumes after the game has already staged any required powder, the exact hidden staged count is
        // not observable while GunController still reports C0. Do not mix guessed old state with new dispenser
        // clicks. Commit the existing tray once, then let the durable PowderCharges value prove whether it matches
        // this task. A mismatch fails visibly instead of silently adding more charge to an unknown tray.
        var stagedBeforeSelection = loadPowderButton?.isActive == true;
        if (stagedBeforeSelection) {
            var anyRequiredInactive = false;
            for (var i = 0; i < count; i++) {
                if (powderButtons[i].isActive) continue;
                anyRequiredInactive = true;
                break;
            }
            stagedBeforeSelection = anyRequiredInactive;
        }

        if (stagedBeforeSelection) {
            MelonLogger.Warning(
                $"[FCS ReloadResume] {_surfix}: staged powder detected before selection; " +
                $"skipping all dispenser replay and committing existing tray; {PowderControlsSummary(count)}");
        }
        else {
            for (var i = 0; i < count; i++) {
                yield return FcsRuntimeClock.WaitUntilFocused();

                var physical = GunPhysicalState.Read(_surfix);
                if (physical.PowderCharges > 0) {
                    MelonLogger.Warning(
                        $"[FCS ReloadResume] {_surfix}: powder became committed while selecting charges; " +
                        $"verifying physical C{physical.PowderCharges}");
                    break;
                }
                if (!physical.ShellLoaded) {
                    FailReloadAction($"reload state left shell-loaded handoff before powder selection completed; {physical.Summary()}");
                    yield break;
                }

                var button = powderButtons[i];
                if (!button.isActive) {
                    // Give the release-build control a short grace window to return. If it stays inactive while
                    // the charge rammer is active, a staged tray appeared during this selection pass. From that
                    // point onward stop replaying dispensers entirely; the final physical count decides success.
                    var resumeDeadline = FcsRuntimeClock.Now + PowderControlResumeGraceSeconds;
                    while (!button.isActive
                           && loadPowderButton?.isActive != true
                           && FcsRuntimeClock.Now < resumeDeadline) {
                        yield return FcsRuntimeClock.WaitUntilFocused();
                        yield return FcsRuntimeClock.WaitForSeconds(0.1f);
                    }

                    if (!button.isActive) {
                        if (loadPowderButton?.isActive == true) {
                            MelonLogger.Warning(
                                $"[FCS ReloadResume] {_surfix}: staged powder appeared at dispenser {i + 1}; " +
                                "stopping all further dispenser replay and committing the current tray");
                            break;
                        }

                        FailReloadAction(
                            $"required powder dispenser {i + 1} is inactive with no staged-charge evidence; " +
                            PowderControlsSummary(count));
                        yield break;
                    }
                }

                yield return ClickReloadControl(button, $"Button Dispencer ({i + 1})", 10f);
                if (!LastReloadActionSucceeded) yield break;
            }
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        var beforeRam = GunPhysicalState.Read(_surfix);
        if (beforeRam.PowderCharges == 0) {
            if (!beforeRam.ShellLoaded) {
                FailReloadAction($"powder state changed before charge rammer; {beforeRam.Summary()}");
                yield break;
            }

            MelonLogger.Msg(
                $"[FCS ReloadTrace] {_surfix}: committing powder tray for expected C{count}; " +
                $"physical={beforeRam.Summary()}, {PowderControlsSummary(count)}");
            yield return ClickReloadControl(loadPowderButton, "Universal Button Charge Rammer (1)", 10f);
            if (!LastReloadActionSucceeded) yield break;
        }

        // A button click is not success. Verify the durable GunController charge count as soon as state 6+
        // exposes it; this catches both a no-op rammer and an F9-resumed tray containing more charge than the
        // newly requested target. A mismatched committed round is left intact for later physical reclassification.
        yield return WaitForPowderCommit(count, shellAtStart);
    }

    public bool HaveBulletInCylinder(BulletType type) {
        RefreshBullets();
        return bullets.Contains(type.ToString());
    }
    
    public bool HaveEmptyShellInCylinder() {
        RefreshBullets();
        return bullets.Contains(null);
    }

    public IEnumerator WaitBackToIdle(float timeoutSeconds = 60f) {
        if (gunController == null)
            yield break;

        // Once the shot has been observed FCS no longer owns the firing elevation. Release the override now so
        // the game's reload/recovery system can lower the barrel and complete its normal return-to-load cycle.
        ReleaseElevationOverride();

        // The release-build probe showed a long empty state-0 interval after firing, followed later by
        // 1/BreachUnlocking -> 2/GuideDeploy -> 3/BreechOpen. `working` remained false through much of this,
        // so the old minimum-delay + !working test completed the task too early and the UI switched to idle
        // while the gun was still physically returning. Keep BackToIdle alive until the verified final handoff.
        var minimumRecoveryUntilGameTime = Time.time + MinimumPostShotRecoverySeconds;
        var deadline = FcsRuntimeClock.Now + Mathf.Max(MinimumPostShotRecoverySeconds, timeoutSeconds);
        var emptyReadyVelocityBlockLogged = false;

        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            var physical = GunPhysicalState.Read(_surfix);
            var minimumDelayDone = Time.time >= minimumRecoveryUntilGameTime;
            var motionReady = Mathf.Abs(gunController.elevationChangeVelocity) <= RecoveryElevationVelocityTolerance;
            var recoveryComplete = reloadController == null
                ? !gunController.ExternalReloadLoweringLocked && motionReady
                : physical.EmptyReady && motionReady;

            if (physical.EmptyReady && !motionReady && !emptyReadyVelocityBlockLogged) {
                emptyReadyVelocityBlockLogged = true;
                MelonLogger.Warning(
                    $"[FCS] GunSystem {_surfix}: EmptyReady reached but residual elevation velocity " +
                    $"{gunController.elevationChangeVelocity:F4} exceeds tolerance " +
                    $"{RecoveryElevationVelocityTolerance:F2}; waiting for settle");
            }

            if (minimumDelayDone && recoveryComplete)
                break;

            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Warning(
                    $"[FCS] GunSystem {_surfix}: post-shot recovery timed out; " +
                    $"physical={physical.Summary()}, breechLocked={gunController.ExternalReloadLoweringLocked}, " +
                    $"elevationVelocity={gunController.elevationChangeVelocity:F3}. " +
                    $"The next task will re-check the physical reload state before touching controls.");
                break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        }
    }

    public IEnumerator WaitFire(float timeoutSeconds = 20f) {
        LastFireObserved = false;
        if (gunController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: gun controller unbound while waiting for fire");
            yield break;
        }

        // pendingReload is a useful signal, but it may be transient. If a shot and part of the
        // recovery sequence happen while the application is unfocused, that flag can be missed.
        // Snapshot the loaded chamber as a second durable signal: a shell that was chambered before
        // the wait and is gone after focus returns also proves that the shot/reload transition ran.
        var chamberAtStart = BulletInChamber();
        var deadline = FcsRuntimeClock.Now + Mathf.Max(1f, timeoutSeconds);
        var resumeGeneration = FcsRuntimeClock.ResumeGeneration;

        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (resumeGeneration != FcsRuntimeClock.ResumeGeneration) {
                resumeGeneration = FcsRuntimeClock.ResumeGeneration;
                MelonLogger.Msg(
                    $"[FCS] GunSystem {_surfix}: reconciled after focus restore; " +
                    $"pendingReload={gunController.pendingReload}, CanFire={gunController.CanFire}, " +
                    $"chamber={BulletInChamber() ?? "empty"}, reloadState=" +
                    (reloadController == null
                        ? "unknown"
                        : $"{reloadController.CurrentStateIndex}, working={reloadController.working}"));
            }

            if (gunController.pendingReload) {
                LastFireObserved = true;
                yield break;
            }

            var chamberNow = BulletInChamber();
            if (chamberAtStart != null && chamberNow == null) {
                MelonLogger.Msg(
                    $"[FCS] GunSystem {_surfix}: fire inferred from chamber transition after state reconciliation");
                LastFireObserved = true;
                yield break;
            }

            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Error(
                    $"[FCS] GunSystem {_surfix}: fire was not observed before timeout; " +
                    $"pendingReload={gunController.pendingReload}, CanFire={gunController.CanFire}, " +
                    $"chamber={chamberNow ?? "empty"}");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        }
    }
    
    public int RemainingCharges() {
        return remainingCharges == null ? 0 : (int)remainingCharges.CurrentNumber;
    }

}
