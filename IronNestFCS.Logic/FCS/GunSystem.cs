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
    private string _surfix = "";

    private CylinderShellSelector? shellSelector;
    private readonly List<string?> bullets = new();
    private LookAtTarget? nextBulletButton;
    private LookAtTarget? loadBulletButton;
    private readonly List<LookAtTarget> powderButtons = new();
    private LookAtTarget? loadPowderButton;
    private GunController? gunController;
    private LinearSliderInteractable? elevationLever;
    private OdometerDisplay? remainingCharges;
    private TextMeshPro? shellId;

    public bool LastElevationSucceeded { get; private set; }
    public bool LastFireObserved { get; private set; }

    public bool TryBind(string surfix) {
        _surfix = surfix;
        powderButtons.Clear();

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
        var elevationBase = GameObject.Find(".Elevation Lever Baseplate");
        elevationLever = elevationBase?.transform.FindChild(".Elevation Lever " + surfix)
            ?.GetComponent<LinearSliderInteractable>();

        var ok = remainingCharges != null
                 && nextBulletButton != null
                 && shellSelector != null
                 && loadBulletButton != null
                 && powderButtons.Count >= 6
                 && loadPowderButton != null
                 && gunController != null
                 && elevationLever != null;
        if (!ok) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: one or more controls could not be bound");
        }
        return ok;
    }
    
    public bool CanFire() {
        return gunController != null && gunController.CanFire;
    }

    public IEnumerator SetElevation(float elevation, float timeoutSeconds = 30f) {
        LastElevationSucceeded = false;
        if (elevationLever == null || gunController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Elevation lever or gun controller unbound");
            yield break;
        }

        var deadline = Time.realtimeSinceStartup + Mathf.Max(1f, timeoutSeconds);
        elevationLever.SetSliderValue(elevation);
        yield return new WaitForSeconds(0.1f);
        while (!Mathf.Approximately(gunController.CurrentElevation, elevation)) {
            if (Time.realtimeSinceStartup >= deadline) {
                MelonLogger.Error(
                    $"[FCS] GunSystem {_surfix}: elevation timeout, current={gunController.CurrentElevation:F2}, target={elevation:F2}");
                yield break;
            }
            elevationLever.SetSliderValue(elevation);
            yield return new WaitForSeconds(0.5f);
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
    
    /// <summary>
    /// 装填指定弹种：先把弹仓转到目标弹，再按装填。转弹仓每步之间要等动画/物理完成。
    /// </summary>
    public IEnumerator LoadBullet(BulletType type) {
        RefreshBullets();
        if (bullets.Count == 0 || !bullets.Contains(type.ToString())) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: No {type} available in cylinder");
            yield break;
        }
        
        for (var i = 0; i < bullets.Count; ++i) {
            if (bullets.Count > 0 && bullets[0] == type.ToString()) {
                break;
            }
            NextBullet();
            yield return new WaitForSeconds(1.5f);
            RefreshBullets();
        }
        if (bullets.Count == 0 || bullets[0] != type.ToString()) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Can't find {type} after rotation, current: {string.Join(", ", bullets)}");
            yield break;
        }
        yield return FcsSceneInteractor.WaitAndClick(loadBulletButton);
    }

    private IEnumerator SelectPowder(int count) {
        if (count < 0 || count > powderButtons.Count) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: invalid powder count {count}, available buttons={powderButtons.Count}");
            yield break;
        }
        for (var i = 0; i < count; i++) {
            yield return FcsSceneInteractor.WaitAndClick(powderButtons[i]);
        }
    }

    public IEnumerator LoadPowder(int count) {
        yield return SelectPowder(count);
        yield return FcsSceneInteractor.WaitAndClick(loadPowderButton);
    }

    public bool HaveBulletInCylinder(BulletType type) {
        RefreshBullets();
        return bullets.Contains(type.ToString());
    }
    
    public bool HaveEmptyShellInCylinder() {
        RefreshBullets();
        return bullets.Contains(null);
    }

    public IEnumerator WaitBackToIdle(float timeoutSeconds = 30f) {
        if (gunController == null)
            yield break;

        var deadline = Time.realtimeSinceStartup + Mathf.Max(1f, timeoutSeconds);
        while (gunController.elevationChangeVelocity != 0) {
            if (Time.realtimeSinceStartup >= deadline) {
                MelonLogger.Warning($"[FCS] GunSystem {_surfix}: return-to-idle movement timed out; releasing task slot anyway");
                break;
            }
            yield return new WaitForSeconds(0.1f);
        }
        // Preserve the original post-shot recovery delay, but the movement wait above is now bounded.
        yield return new WaitForSeconds(13f);
    }

    public IEnumerator WaitFire(float timeoutSeconds = 20f) {
        LastFireObserved = false;
        if (gunController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: gun controller unbound while waiting for fire");
            yield break;
        }

        var deadline = Time.realtimeSinceStartup + Mathf.Max(1f, timeoutSeconds);
        while (!gunController.pendingReload) {
            if (Time.realtimeSinceStartup >= deadline) {
                MelonLogger.Error($"[FCS] GunSystem {_surfix}: fire was not observed before timeout");
                yield break;
            }
            yield return new WaitForSeconds(0.1f);
        }
        LastFireObserved = true;
    }
    
    public int RemainingCharges() {
        return remainingCharges == null ? 0 : (int)remainingCharges.CurrentNumber;
    }

}
