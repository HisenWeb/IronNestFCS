using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 只读状态时间线探针。初始化时打印基线，此后仅在关键物理字段发生变化时打印。
/// 用于验证装填/击发状态机是否存在“working=false 但稍后又继续动作”的假稳定窗口。
/// 不修改任何游戏状态。
/// </summary>
public static class PhysicalStateProbe
{
    private sealed class TraceSlot
    {
        public bool HasValue;
        public string Signature = "";
        public float ChangedAt;
    }

    private static readonly TraceSlot Left = new();
    private static readonly TraceSlot Right = new();

    public static void Reset()
    {
        ResetSlot(Left);
        ResetSlot(Right);
    }

    private static void ResetSlot(TraceSlot slot)
    {
        slot.HasValue = false;
        slot.Signature = "";
        slot.ChangedAt = 0f;
    }

    public static void LogCurrentState()
    {
        LogGun("Left", Left, true);
        LogGun("Right", Right, true);
    }

    /// <summary>
    /// 每帧调用，但只有状态签名变化时才输出日志。故不会因为帧率刷屏。
    /// 注意：这里故意不受 FCS 失焦门控影响，因为游戏在后台仍可能继续装填/击发动作。
    /// </summary>
    public static void Tick()
    {
        LogGun("Left", Left, false);
        LogGun("Right", Right, false);
    }

    private static void LogGun(string side, TraceSlot slot, bool force)
    {
        try
        {
            var state = GunPhysicalState.Read(side);
            var signature = Signature(state);
            var now = Time.realtimeSinceStartup;

            if (!force && slot.HasValue && signature == slot.Signature)
                return;

            if (!state.IsBound)
            {
                if (!slot.HasValue || signature != slot.Signature || force)
                    MelonLogger.Warning($"[FCS-PROBE] {side}: GunController unavailable");
            }
            else
            {
                var idleCandidate = !state.ReloadWorking && !state.BreechLocked;
                var prefix = slot.HasValue
                    ? $"change after {(now - slot.ChangedAt):F3}s"
                    : "baseline";

                MelonLogger.Msg(
                    $"[FCS-PROBE] {side} {prefix}: kind={state.Kind}, chamber={state.ShellId ?? "empty"}, " +
                    $"powder={state.PowderCharges}, CanFire={state.CanFire}, IsReloading={state.IsReloading}, " +
                    $"pendingReload={state.PendingReload}, reloadState={state.ReloadStateIndex}/{state.ReloadStateKey}, " +
                    $"reloadComplete={state.ReloadCompleteState}, reloadWorking={state.ReloadWorking}, " +
                    $"breechLocked={state.BreechLocked}, idleCandidate={idleCandidate}, elevation={state.Elevation:F2}");
            }

            slot.HasValue = true;
            slot.Signature = signature;
            slot.ChangedAt = now;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[FCS-PROBE] {side}: probe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 仰角故意不进入签名，否则瞄准过程中会每帧刷日志；输出行仍会携带当时仰角供参考。
    /// </summary>
    private static string Signature(GunPhysicalState state)
    {
        if (!state.IsBound)
            return "unbound";

        return string.Join("|",
            state.ShellId ?? "empty",
            state.PowderCharges,
            state.CanFire,
            state.IsReloading,
            state.PendingReload,
            state.ReloadStateIndex,
            state.ReloadStateKey,
            state.ReloadCompleteState,
            state.ReloadWorking,
            state.BreechLocked);
    }
}
