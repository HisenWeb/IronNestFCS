using MelonLoader;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 只读探针：在 Logic 初始化/F9 热重载后打印两门炮的真实物理状态和分类结果。
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
            var state = GunPhysicalState.Read(side);
            if (!state.IsBound)
            {
                MelonLogger.Warning($"[FCS] PhysicalState {side}: GunController unavailable");
                return;
            }

            MelonLogger.Msg(
                $"[FCS] PhysicalState {side}: kind={state.Kind}, chamber={state.ShellId ?? "empty"}, " +
                $"powder={state.PowderCharges}, CanFire={state.CanFire}, IsReloading={state.IsReloading}, " +
                $"pendingReload={state.PendingReload}, reloadState={state.ReloadStateIndex}/{state.ReloadStateKey}, " +
                $"reloadComplete={state.ReloadCompleteState}, reloadWorking={state.ReloadWorking}, " +
                $"breechLocked={state.BreechLocked}, elevation={state.Elevation:F2}");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[FCS] PhysicalState {side}: probe failed: {ex.Message}");
        }
    }
}
