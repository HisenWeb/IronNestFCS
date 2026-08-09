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
                    // State metadata is diagnostic only. Classification falls back to the observed index.
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

    private static bool AtState(GunPhysicalState state, int index, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.Equals(state.ReloadStateKey, key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return state.ReloadStateIndex == index;
    }

    private static GunPhysicalStateKind Classify(GunPhysicalState state)
    {
        if (!state.IsBound)
            return GunPhysicalStateKind.Unbound;

        // Release-build probe (2026-08-09) observed this full reload sequence:
        //   0 BreachLocked -> 1 BreachUnlocking -> 2 GuideDeploy -> 3 BreechOpen
        //   -> 4 ShellRamming -> 5 SelectPowderCharge -> 6 RamCharges
        //   -> 7 CloseShellGuide -> 8 FinalSequence -> 9 Done -> 0 BreachLocked.
        // Crucially, reloadController.working stayed FALSE throughout long parts of that sequence, including
        // a ~28 s post-shot state-0 window. Therefore working/breechLocked are not sufficient readiness signals.

        var atLocked = AtState(state, 0, "BreachLocked", "BreechLocked");
        var atUnlocking = AtState(state, 1, "BreachUnlocking", "BreechUnlocking");
        var atGuideDeploy = AtState(state, 2, "GuideDeploy");
        var atBreechOpen = AtState(state, 3, "BreechOpen", "BreachOpen");
        var atShellRamming = AtState(state, 4, "ShellRamming");
        var atSelectPowder = AtState(state, 5, "SelectPowderCharge");
        var atRamCharges = AtState(state, 6, "RamCharges");
        var atCloseGuide = AtState(state, 7, "CloseShellGuide");
        var atFinalSequence = AtState(state, 8, "FinalSequence");
        var atDone = AtState(state, 9, "Done");

        // Hard live lock/motion still means do not touch the mechanism regardless of state label.
        if (state.ReloadWorking || state.BreechLocked)
            return GunPhysicalStateKind.Recovering;

        // The only observed normal EMPTY handoff point is state 3 / BreechOpen. Fresh mission startup briefly
        // passes through state 2 first, and post-shot recovery spends a long time empty in states 0/1/2, so
        // accepting "empty + C0" without the state check causes exactly the first-shot/reload races we observed.
        if (state.ShellId == null && state.PowderCharges == 0)
        {
            if (atBreechOpen)
                return GunPhysicalStateKind.EmptyReady;

            if (atLocked || atUnlocking || atGuideDeploy || state.IsReloading || state.PendingReload)
                return GunPhysicalStateKind.PostShotRecovery;

            return GunPhysicalStateKind.Unknown;
        }

        // After shell ramming completes, state 5 waits for charge selection. This is the stable F9-recoverable
        // "shell in chamber, no powder yet" state. State 4 with a chambered shell is still part of the ram cycle.
        if (state.ShellType.HasValue && state.PowderCharges == 0)
        {
            if (atSelectPowder)
                return GunPhysicalStateKind.ShellLoaded;

            if (atShellRamming || atGuideDeploy || atBreechOpen)
                return GunPhysicalStateKind.Recovering;

            return GunPhysicalStateKind.Unknown;
        }

        // A reusable loaded round is only ready once the reload sequence has returned to state 0 AND the gun
        // itself reports CanFire with the reload flow flags clear. Powder becomes visible already in state 6;
        // treating that as LoadedReady made FCS start elevation while states 6->9 were still physically running.
        if (state.ShellType.HasValue && state.PowderCharges > 0 && state.PowderCharges <= 6)
        {
            if (atLocked && state.CanFire && !state.IsReloading && !state.PendingReload)
                return GunPhysicalStateKind.LoadedReady;

            if (atRamCharges || atCloseGuide || atFinalSequence || atDone || atLocked)
                return GunPhysicalStateKind.Recovering;

            return GunPhysicalStateKind.Unknown;
        }

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
                $"post-shot chamber={shell} C{PowderCharges} state={ReloadStateIndex}/{ReloadStateKey} pendingReload={PendingReload} IsReloading={IsReloading}",
            GunPhysicalStateKind.Recovering =>
                $"recovering chamber={shell} C{PowderCharges} state={ReloadStateIndex}/{ReloadStateKey} working={ReloadWorking} breechLocked={BreechLocked}",
            GunPhysicalStateKind.Unknown =>
                $"unknown chamber={shell} C{PowderCharges} CanFire={CanFire} state={ReloadStateIndex}/{ReloadStateKey}",
            _ => "unbound",
        };
    }
}
