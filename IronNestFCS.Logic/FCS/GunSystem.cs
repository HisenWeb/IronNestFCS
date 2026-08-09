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
    private const float MinimumPostShotRecoverySeconds = 13f;
    private const float RecoveryElevationVelocityTolerance = 0.05f;

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
    
    public string? BulletInChamber() {
        return gunController?.ChamberedShellBlueprint?.shellDefinition?.ShellId;
    }
    
    public bool IsChamberEmpty() {
        return BulletInChamber() == null;
    }

    private void RefreshBullets() {
        bullets.Clear();
        if (shellSelector == null) return;
        foreach (var shell in shellSelector.bullets) {
            bullets.Add(shell?.GetComponent<ShellBlueprint>()?.shellDefinition?.ShellId);
        }
        MelonLogger.Msg($"[FCS] GunSystem {_surfix}: Cylinder bullets: {string.Join(", ", bullets)}");
    }

    public void NextBullet() {
        if (nextBulletButton == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: NextBulletButton unbound");
            return;
        }
        MelonLogger.Msg("[GunSystem] NextBullet");
        nextBulletButton.OnClickDown();
    }

    private void FailReloadAction(string reason) {
        LastReloadActionSucceeded = false;
        LastReloadFailureReason = reason;
        MelonLogger.Error($"[FCS] GunSystem {_surfix}: {reason}");
    }

    /// <summary>
    /// Wait for a release-build reload interaction handoff that has been verified by the physical-state probe.
    /// `reloadController.working` is NOT a readiness signal in this build: it stays false through long portions
    /// of states 0..9. The safe interaction nodes we observed are state 3/BreechOpen for an empty gun and
    /// state 5/SelectPowderCharge for a chambered shell with no powder.
    /// </summary>
    public IEnumerator WaitForReloadReady(float timeoutSeconds = ReloadControlTimeoutSeconds) {
        LastReloadReadySucceeded = false;
        if (gunController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: gun controller unbound while waiting for reload readiness");
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + Mathf.Max(1f, timeoutSeconds);
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            var physical = GunPhysicalState.Read(_surfix);
            var interactionReady = reloadController == null
                ? !gunController.ExternalReloadLoweringLocked
                : physical.EmptyReady || physical.ShellLoaded;
            var breechReady = !gunController.ExternalReloadLoweringLocked;
            var motionReady = Mathf.Abs(gunController.elevationChangeVelocity) <= RecoveryElevationVelocityTolerance;
            if (interactionReady && breechReady && motionReady) break;

            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Error(
                    $"[FCS] GunSystem {_surfix}: reload mechanism did not reach a safe interaction handoff; " +
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

    private IEnumerator ClickReloadControl(LookAtTarget? button, string controlName,
        float timeoutSeconds = ReloadControlTimeoutSeconds) {
        LastReloadActionSucceeded = false;
        LastReloadFailureReason = "";
        if (button == null) {
            FailReloadAction($"reload control missing: {controlName}");
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + Mathf.Max(0.1f, timeoutSeconds);
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (button.isActive && button.nextAllowedClickTime <= Time.realtimeSinceStartup)
                break;

            if (FcsRuntimeClock.Now >= deadline) {
                FailReloadAction($"reload control timed out: {controlName}");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        }

        yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        yield return FcsRuntimeClock.WaitUntilFocused();
        try {
            button.OnClickDown();
        }
        catch (Exception ex) {
            FailReloadAction($"reload control click-down failed ({controlName}): {ex.Message}");
            yield break;
        }

        // Once a click starts, always complete the down/up pair even if focus changes in between.
        // Leaving a LookAtTarget held down is worse than finishing the already-started interaction.
        yield return new WaitForSeconds(0.1f);
        try {
            button.OnClickUp();
        }
        catch (Exception ex) {
            FailReloadAction($"reload control click-up failed ({controlName}): {ex.Message}");
            yield break;
        }

        LastReloadActionSucceeded = true;
    }

    private IEnumerator WaitForChamberedShell(BulletType type,
        float timeoutSeconds = ShellChamberTimeoutSeconds) {
        LastReloadActionSucceeded = false;
        var expected = type.ToString();
        var deadline = FcsRuntimeClock.Now + Mathf.Max(1f, timeoutSeconds);

        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            var chamber = BulletInChamber();
            if (chamber == expected) {
                LastReloadActionSucceeded = true;
                yield break;
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
            NextBullet();
            yield return FcsRuntimeClock.WaitForSeconds(1.5f);
            yield return FcsRuntimeClock.WaitUntilFocused();
            RefreshBullets();
        }
        if (bullets.Count == 0 || bullets[0] != type.ToString()) {
            FailReloadAction($"Can't find {type} after cylinder rotation, current: {string.Join(", ", bullets)}");
            yield break;
        }

        // The cylinder can report the correct shell before the rammer/breech is ready for the next
        // interaction. Re-check the real mechanism state after cylinder positioning instead of
        // relying only on the pre-LoadBullet readiness check in FSC.
        yield return WaitForReloadReady();
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

        // Let the rammer/breech finish its physical cycle before handing control to powder loading.
        yield return WaitForReloadReady();
        if (!LastReloadReadySucceeded) {
            FailReloadAction("reload mechanism did not settle after shell ramming");
            yield break;
        }
        LastReloadActionSucceeded = true;
    }

    public IEnumerator LoadPowder(int count) {
        LastReloadActionSucceeded = false;
        LastReloadFailureReason = "";
        if (count < 0 || count > powderButtons.Count) {
            FailReloadAction($"invalid powder count {count}, available buttons={powderButtons.Count}");
            yield break;
        }

        for (var i = 0; i < count; i++) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return ClickReloadControl(powderButtons[i], $"Button Dispencer ({i + 1})");
            if (!LastReloadActionSucceeded) yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        yield return ClickReloadControl(loadPowderButton, "Universal Button Charge Rammer (1)");
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