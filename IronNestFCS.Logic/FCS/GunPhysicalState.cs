using Il2Cpp;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 游戏内火炮的实时物理状态。它与 ArtilleryTask 完全独立：F9 可以清空任务，
/// 但炮膛里的弹、已装药量和装填机构状态仍由游戏本身保存并可重新读取。
/// </summary>
public sealed class GunPhysicalState
{
    public string Side { get; private set; } = "";
    public bool IsBound { get; private set; }
    public string? ShellId { get; private set; }
    public BulletType? ShellType { get; private set; }
    public int PowderCharges { get; private set; }
    public bool CanFire { get; private set; }
    public bool ReloadWorking { get; private set; }
    public bool BreechLocked { get; private set; }
    public float Elevation { get; private set; }
    public float MinElevation { get; private set; }
    public float MaxElevation { get; private set; } = 60f;

    /// <summary>机构已经稳定，可以据此做一次调度判断。</summary>
    public bool IsStable => IsBound && !ReloadWorking && !BreechLocked;

    /// <summary>炮中已有一发完整可击发的弹药，不能再按普通空炮流程装填。</summary>
    public bool LoadedReady =>
        IsStable && ShellType.HasValue && PowderCharges > 0 && CanFire;

    /// <summary>炮膛/装药均为空，且机构没有正在动作，可以接普通装填任务。</summary>
    public bool EmptyReady =>
        IsStable && ShellId == null && PowderCharges == 0;

    public static GunPhysicalState Read(string side)
    {
        var state = new GunPhysicalState { Side = side };
        try
        {
            var gun = GameObject.Find("Gun" + side)?.GetComponent<GunController>();
            if (gun == null)
                return state;

            state.IsBound = true;
            state.ShellId = gun.ChamberedShellBlueprint?.shellDefinition?.ShellId;
            state.PowderCharges = gun.PowderCharges;
            state.CanFire = gun.CanFire;
            state.BreechLocked = gun.ExternalReloadLoweringLocked;
            state.Elevation = gun.CurrentElevation;

            var reload = gun.artilleryReloadController;
            state.ReloadWorking = reload != null && reload.working;

            if (!string.IsNullOrEmpty(state.ShellId))
            {
                var normalized = state.ShellId == "PCLM" ? "PLCM" : state.ShellId;
                if (Enum.TryParse<BulletType>(normalized, true, out var type))
                    state.ShellType = type;
            }

            var elevationBase = GameObject.Find(".Elevation Lever Baseplate");
            var elevationLever = elevationBase?.transform.FindChild(".Elevation Lever " + side)
                ?.GetComponent<LinearSliderInteractable>();
            if (elevationLever != null)
            {
                state.MinElevation = Mathf.Min(elevationLever.minOutputValue, elevationLever.maxOutputValue);
                state.MaxElevation = Mathf.Max(elevationLever.minOutputValue, elevationLever.maxOutputValue);
            }
        }
        catch
        {
            state.IsBound = false;
        }

        return state;
    }

    /// <summary>
    /// 保守判断当前已装弹药是否可以直接用于目标。最低装药量只作为调度前置门槛；
    /// 真正的仰角仍由游戏自带弹道计算器使用当前实际 PowderCharges 重新解算。
    /// </summary>
    public bool CanReuseFor(BulletType requestedShell, float distance)
    {
        if (!LoadedReady || ShellType != requestedShell)
            return false;

        return PowderCharges >= BallisticCalculator.MinimumCharge(distance);
    }

    public bool IsElevationWithinPhysicalRange(float elevation)
    {
        return !float.IsNaN(elevation)
               && !float.IsInfinity(elevation)
               && elevation >= MinElevation
               && elevation <= MaxElevation;
    }

    public string Summary()
    {
        if (!IsBound)
            return "unbound";

        if (LoadedReady)
            return $"loaded {ShellType!.Value.DisplayName()} C{PowderCharges}";

        if (EmptyReady)
            return "empty";

        var shell = ShellType.HasValue ? ShellType.Value.DisplayName() : ShellId ?? "empty";
        return $"state chamber={shell} C{PowderCharges} CanFire={CanFire} working={ReloadWorking} breechLocked={BreechLocked}";
    }
}
