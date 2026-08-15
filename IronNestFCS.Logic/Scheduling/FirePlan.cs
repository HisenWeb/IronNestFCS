using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Immutable firing decision produced by one planning round. Gun/shell/charge/elevation/azimuth never
/// change during execution. Switching gun means discarding this plan and planning again.
/// </summary>
internal sealed class FirePlan
{
    public ArtilleryTask Task { get; }
    public LeftRight Side { get; }
    public BulletType Shell { get; }
    public int Charge { get; }
    public float Elevation { get; }
    public float Azimuth { get; }
    public float PlannedAt { get; }
    public bool EtaKnown { get; }
    public float EstimatedLocalReadyAt { get; }
    public float AzimuthSeconds { get; }
    public float EstimatedReadyAt { get; private set; }
    public float EstimatedFlightSeconds { get; private set; } = float.NaN;
    public float AlignmentScore { get; }
    public int Generation { get; }

    // Zero while merely planned. ComparePair/CommitSingle assigns one id shared by the committed stack.
    public int ExecutionBatchId { get; set; }
    public bool Compared { get; set; }
    public bool LocalReady { get; set; }
    public bool AzimuthReady { get; set; }
    public bool Failed { get; set; }
    public bool ShotObserved { get; set; }
    public bool CompletionHandled { get; set; }
    public string FailureReason { get; set; } = "";

    // Experimental SmoothStep validation state. These fields only support one-shot comparison logging
    // against the game's mechanical Time-To-Impact dial once WaitingForFire is reached.
    public float TtiValidationLastDialSeconds { get; set; } = float.NaN;
    public int TtiValidationStableSamples { get; set; }
    public bool TtiValidationLogged { get; set; }

    public FirePlan(
        ArtilleryTask task,
        LeftRight side,
        BulletType shell,
        int charge,
        float elevation,
        float azimuth,
        float plannedAt,
        bool etaKnown,
        float estimatedLocalReadyAt,
        float azimuthSeconds,
        float alignmentScore,
        int generation)
    {
        Task = task;
        Side = side;
        Shell = shell;
        Charge = charge;
        Elevation = elevation;
        Azimuth = azimuth;
        PlannedAt = plannedAt;
        EtaKnown = etaKnown;
        EstimatedLocalReadyAt = estimatedLocalReadyAt;
        AzimuthSeconds = azimuthSeconds;
        AlignmentScore = alignmentScore;
        Generation = generation;
        EstimatedReadyAt = etaKnown
            ? Math.Max(estimatedLocalReadyAt, plannedAt + azimuthSeconds)
            : float.NaN;
    }

    /// <summary>
    /// Estimate completion if this plan receives the shared azimuth lane at sharedStartAt. The azimuth
    /// distance itself remains the one captured by this planning round; this does not re-read or re-plan.
    /// </summary>
    public float RefreshEstimatedReadyAt(float sharedStartAt)
    {
        EstimatedReadyAt = EtaKnown
            ? Math.Max(EstimatedLocalReadyAt, sharedStartAt + AzimuthSeconds)
            : float.NaN;
        return EstimatedReadyAt;
    }

    public bool TrySetEstimatedFlightSeconds(float seconds)
    {
        if (!float.IsNaN(EstimatedFlightSeconds)
            || float.IsNaN(seconds)
            || float.IsInfinity(seconds)
            || seconds <= 0f)
        {
            return false;
        }

        EstimatedFlightSeconds = seconds;
        return true;
    }

    public GunSide HostSide => Side == LeftRight.Left ? GunSide.Left : GunSide.Right;
    public LoadRequest LoadRequest => new(HostSide, (ShellTypeCode)(int)Shell, Charge);
    public string Label => $"{Side} T{Task.targetId} {Shell.DisplayName()} C{Charge}";
}

internal sealed class FirePlanCandidate
{
    public LeftRight Side { get; }
    public BulletType Shell { get; }
    public int Charge { get; }
    public float Elevation { get; }
    public bool EtaKnown { get; }
    public bool LoadAlreadyRunning { get; }
    public float EstimatedLocalReadyAt { get; private set; }
    public float EstimatedReadyAt { get; private set; }
    public float AlignmentScore { get; }
    public float LoadSeconds { get; }
    public float ElevationSeconds { get; }
    public float AzimuthSeconds { get; }
    public string LoadLabel { get; }

    public FirePlanCandidate(
        LeftRight side,
        BulletType shell,
        int charge,
        float elevation,
        bool etaKnown,
        bool loadAlreadyRunning,
        float alignmentScore,
        float loadSeconds,
        float elevationSeconds,
        float azimuthSeconds,
        string loadLabel)
    {
        Side = side;
        Shell = shell;
        Charge = charge;
        Elevation = elevation;
        EtaKnown = etaKnown;
        LoadAlreadyRunning = loadAlreadyRunning;
        AlignmentScore = alignmentScore;
        LoadSeconds = loadSeconds;
        ElevationSeconds = elevationSeconds;
        AzimuthSeconds = azimuthSeconds;
        LoadLabel = loadLabel;
        EstimatedLocalReadyAt = float.NaN;
        EstimatedReadyAt = float.NaN;
    }

    /// <summary>
    /// Finalize both gun candidates against one common planning-decision timestamp. Only an already-running
    /// persistent transaction is allowed to consume time while ballistics are being solved. Fresh loading and
    /// elevation cannot start until a FirePlan is actually committed.
    /// </summary>
    public void FinalizeTiming(float snapshotAt, float decisionAt)
    {
        if (!EtaKnown)
        {
            EstimatedLocalReadyAt = float.NaN;
            EstimatedReadyAt = float.NaN;
            return;
        }

        var loadReadyAt = LoadAlreadyRunning
            ? snapshotAt + LoadSeconds
            : decisionAt + LoadSeconds;

        EstimatedLocalReadyAt = Math.Max(decisionAt, loadReadyAt) + ElevationSeconds;
        EstimatedReadyAt = Math.Max(EstimatedLocalReadyAt, decisionAt + AzimuthSeconds);
    }
}
