using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 只读探针：在 Logic 初始化/F9 热重载后读取游戏里每门炮的真实物理状态。
/// 用于验证 GunController.PowderCharges 是否可以作为跨重载恢复装药量的权威来源。
/// 不修改任何游戏状态。
/// </summary>
public static class PhysicalStateProbe
{
    public static void LogCurrentState()
    {
        LogGun("Left");
        LogGun("Right");
    }

    private static void LogGun(string side)
    {
        try
        {
            var gun = GameObject.Find("Gun" + side)?.GetComponent<GunController>();
            if (gun == null)
            {
                MelonLogger.Warning($"[FCS] PhysicalState {side}: GunController unavailable");
                return;
            }

            var reload = gun.artilleryReloadController;
            var chamber = gun.ChamberedShellBlueprint?.shellDefinition?.ShellId ?? "empty";
            var reloadState = reload == null ? "unknown" : reload.CurrentStateIndex.ToString();
            var reloadWorking = reload != null && reload.working;

            MelonLogger.Msg(
                $"[FCS] PhysicalState {side}: chamber={chamber}, powder={gun.PowderCharges}, " +
                $"CanFire={gun.CanFire}, IsReloading={gun.IsReloading}, pendingReload={gun.pendingReload}, " +
                $"reloadState={reloadState}, reloadWorking={reloadWorking}, " +
                $"breechLocked={gun.ExternalReloadLoweringLocked}, elevation={gun.CurrentElevation:F2}");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[FCS] PhysicalState {side}: probe failed: {ex.Message}");
        }
    }
}
