using Il2Cpp;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public enum GunPhysicalStateKind
{
    Unbound,
    EmptyReady,
    ShellLoaded,
    LoadedReady,
    Recovering,
    PostShotRecovery,
    Unknown,
}

/// <summary>
/// 游戏内火炮的实时物理状态。它与 ArtilleryTask 完全独立：F9 可以清空任务，
/// 但炮膛里的弹、已装药量和装填机构状态仍由游戏本身保存并可重新读取。
/// </summary>
public sealed class GunPhysicalState
{
    public string Side { get; private set; } = "";
    public bool IsBound { get; private set; }
    public GunPhysicalStateKind Kind { get; private set; } = GunPhysicalStateKind.Unbound;

    public string? ShellId { get; private set; }
    public BulletType? ShellType { get; private set; }
    public int PowderCharges { get; private set; }

    public bool CanFire { get; private set; }
    public bool IsReloading { get; private set; }
    public bool PendingReload { get; private set; }
    public bool ReloadWorking { get; private set; }
    public bool BreechLocked { get; private set; }
    public int ReloadStateIndex { get; private set; } = -1;
    public string ReloadStateKey { get; private set; } = "unknown";
    public bool ReloadCompleteState { get; private set; }

    public float Elevation { get; private set; }
    public float MinElevation { get; private set; }
    public float MaxElevation { get; private set; } = 60f;

    public bool EmptyReady => Kind == GunPhysicalStateKind.EmptyReady;
    public bool ShellLoaded => Kind == GunPhysicalStateKind.ShellLoaded;
    public bool LoadedReady => Kind == GunPhysicalStateKind.LoadedReady;
    public bool IsRecognizedStable => EmptyReady || ShellLoaded || LoadedReady;
    public bool NeedsRecoveryWait =>
        Kind == GunPhysicalStateKind.Recovering
        || Kind == GunPhysicalStateKind.PostShotRecovery
        || Kind == GunPhysicalStateKind.Unknown
        || Kind == GunPhysicalStateKind.Unbound;

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
            state.IsReloading = gun.IsReloading;
            state.PendingReload = gun.pendingReload;
            state.BreechLocked = gun.ExternalReloadLoweringLocked;
            state.Elevation = gun.CurrentElevation;

            var reload = gun.artilleryReloadController;
            if (reload != null)
            {
                state.ReloadWorking = reload.working;
                state.ReloadStateIndex = reload.CurrentStateIndex;
                try
                {
                    var current = reload.CurrentState;
                    if (current != null)
                    {
                        state.ReloadStateKey = current.stateKey ?? "unknown";
                        state.ReloadCompleteState = current.isReloadCompleteState;
                    }
                }
                catch
                {
                    // State metadata is diagnostic only. Classification must still work if it is unavailable.
                }
            }

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

            state.Kind = Classify(state);
        }
        catch
        {
            state.IsBound = false;
            state.Kind = GunPhysicalStateKind.Unbound;
        }

        return state;
    }

    private static GunPhysicalStateKind Classify(GunPhysicalState state)
    {
        if (!state.IsBound)
            return GunPhysicalStateKind.Unbound;

        // Anything that is visibly moving/locked is a transient state. Do not issue a new control action;
        // wait for the game to settle and then classify the complete snapshot again.
        if (state.ReloadWorking || state.BreechLocked)
            return GunPhysicalStateKind.Recovering;

        // After a shot the game can report an empty chamber while pendingReload/IsReloading remain true
        // even when reloadController.working is already false. This must never be mistaken for an idle empty gun.
        if (state.PendingReload || state.IsReloading)
        {
            if (state.ShellId == null && state.PowderCharges == 0)
                return GunPhysicalStateKind.PostShotRecovery;

            return GunPhysicalStateKind.Recovering;
        }

        if (state.ShellId == null && state.PowderCharges == 0)
            return GunPhysicalStateKind.EmptyReady;

        // A shell in the chamber with no committed powder is a valid stable intermediate state.
        // After F9 it can be resumed by selecting a same-shell target and loading only the required charge.
        if (state.ShellType.HasValue && state.PowderCharges == 0)
            return GunPhysicalStateKind.ShellLoaded;

        // CanFire is deliberately NOT required here. The game exposes it as a fire-flow flag and other
        // implementations have observed it true even at powder=0; shell + committed powder + stable mechanism
        // is the durable physical definition we need for retargeting.
        if (state.ShellType.HasValue && state.PowderCharges > 0 && state.PowderCharges <= 6)
            return GunPhysicalStateKind.LoadedReady;

        // Examples: powder with no shell, unknown shell id, or an out-of-range powder count.
        // Give these states a bounded recovery window before declaring them unusable.
        return GunPhysicalStateKind.Unknown;
    }

    public bool CanReuseLoadedFor(BulletType requestedShell)
    {
        return LoadedReady && ShellType == requestedShell;
    }

    public bool CanCompleteShellFor(BulletType requestedShell)
    {
        return ShellLoaded && ShellType == requestedShell;
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

        var shell = ShellType.HasValue ? ShellType.Value.DisplayName() : ShellId ?? "empty";
        return Kind switch
        {
            GunPhysicalStateKind.EmptyReady => "empty",
            GunPhysicalStateKind.ShellLoaded => $"shell-loaded {shell} C0",
            GunPhysicalStateKind.LoadedReady => $"loaded {shell} C{PowderCharges}",
            GunPhysicalStateKind.PostShotRecovery =>
                $"post-shot chamber={shell} C{PowderCharges} pendingReload={PendingReload} IsReloading={IsReloading}",
            GunPhysicalStateKind.Recovering =>
                $"recovering chamber={shell} C{PowderCharges} state={ReloadStateIndex}/{ReloadStateKey} working={ReloadWorking} breechLocked={BreechLocked}",
            GunPhysicalStateKind.Unknown =>
                $"unknown chamber={shell} C{PowderCharges} CanFire={CanFire} state={ReloadStateIndex}/{ReloadStateKey}",
            _ => "unbound",
        };
    }
}
