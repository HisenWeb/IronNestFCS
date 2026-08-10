namespace IronNestFCS.Abstractions;

public enum GunSide
{
    Left,
    Right,
}

public enum ShellTypeCode
{
    None = 0,
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

public enum LoadingPhysicalState
{
    Unbound,
    EmptyReady,
    ShellLoaded,
    LoadedReady,
    Recovering,
    PostShotRecovery,
    Unknown,
}

public enum LoadingTransactionState
{
    Idle,
    LoadingShell,
    LoadingPowder,
    WaitingLoadedReady,
    LoadedReady,
    Failed,
}

public sealed class LoadRequest
{
    public GunSide Side { get; }
    public ShellTypeCode Shell { get; }
    public int Charge { get; }

    public LoadRequest(GunSide side, ShellTypeCode shell, int charge)
    {
        Side = side;
        Shell = shell;
        Charge = charge;
    }

    public bool SameTarget(LoadRequest other)
    {
        return Side == other.Side && Shell == other.Shell && Charge == other.Charge;
    }

    public override string ToString() => $"{Side}/{Shell}/C{Charge}";
}

public sealed class LoadingSnapshot
{
    public GunSide Side { get; init; }
    public bool IsBound { get; init; }
    public LoadingPhysicalState PhysicalState { get; init; }
    public ShellTypeCode? ActualShell { get; init; }
    public int ActualCharge { get; init; }

    public bool HasTransaction { get; init; }
    public LoadingTransactionState TransactionState { get; init; }
    public ShellTypeCode? RequestedShell { get; init; }
    public int RequestedCharge { get; init; }
    public float? EstimatedRemainingSeconds { get; init; }
    public string FailureReason { get; init; } = "";

    public bool LoadedReady =>
        PhysicalState == LoadingPhysicalState.LoadedReady
        && ActualShell.HasValue
        && ActualCharge > 0;

    public bool Matches(LoadRequest request)
    {
        return Side == request.Side
               && LoadedReady
               && ActualShell == request.Shell
               && ActualCharge == request.Charge;
    }
}

public interface ILoadingSystem
{
    bool IsBound { get; }

    LoadingSnapshot GetSnapshot(GunSide side);

    /// <summary>
    /// Accept a concrete loading transaction. Once accepted, a different request for the same gun
    /// must be rejected until the physical transaction finishes or fails.
    /// </summary>
    bool TryRequest(LoadRequest request, out string reason);
}

public interface IFcsHostServices
{
    ILoadingSystem Loading { get; }
}
