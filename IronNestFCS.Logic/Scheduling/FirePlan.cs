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
    public float EstimatedReadyAt { get; }
    public float AlignmentScore { get; }
    public int Generation { get; }

    public bool Compared { get; set; }
    public bool LocalReady { get; set; }
    public bool AzimuthReady { get; set; }
    public bool Failed { get; set; }
    public bool ShotObserved { get; set; }
    public bool CompletionHandled { get; set; }
    public string FailureReason { get; set; } = "";

    public FirePlan(
        ArtilleryTask task,
        LeftRight side,
        BulletType shell,
        int charge,
        float elevation,
        float azimuth,
        float plannedAt,
        bool etaKnown,
        float estimatedReadyAt,
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
        EstimatedReadyAt = estimatedReadyAt;
        AlignmentScore = alignmentScore;
        Generation = generation;
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
    public float EstimatedReadyAt { get; }
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
        float estimatedReadyAt,
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
        EstimatedReadyAt = estimatedReadyAt;
        AlignmentScore = alignmentScore;
        LoadSeconds = loadSeconds;
        ElevationSeconds = elevationSeconds;
        AzimuthSeconds = azimuthSeconds;
        LoadLabel = loadLabel;
    }
}
