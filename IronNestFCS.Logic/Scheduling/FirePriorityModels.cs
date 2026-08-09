using IronNestFCS.Logic.FCS;

namespace IronNestFCS.Logic.Scheduling;

internal enum GunTaskMode {
    FreshLoad,
    CompleteShellLoaded,
    ReuseLoadedRound,
}

internal enum FirePriorityGunPhase {
    Preparation,
    FireCommitted,
    PostShotRecovery,
    Unavailable,
}

internal sealed class FireReadyEstimate {
    public bool LoadKnown { get; }
    public string LoadLabel { get; }
    public float LoadSeconds { get; }
    public float ElevationSeconds { get; }
    public float AzimuthSeconds { get; }
    public float TotalSeconds { get; }
    public float AlignmentScore { get; }

    public FireReadyEstimate(
        bool loadKnown,
        string loadLabel,
        float loadSeconds,
        float elevationSeconds,
        float azimuthSeconds,
        float totalSeconds,
        float alignmentScore) {
        LoadKnown = loadKnown;
        LoadLabel = loadLabel;
        LoadSeconds = loadSeconds;
        ElevationSeconds = elevationSeconds;
        AzimuthSeconds = azimuthSeconds;
        TotalSeconds = totalSeconds;
        AlignmentScore = alignmentScore;
    }
}

internal sealed class FirePriorityCandidate {
    public LeftRight Side { get; }
    public ArtilleryTask Task { get; }
    public float SolvedAt { get; }
    public int Generation { get; }
    public GunTaskMode Mode { get; }

    public FirePriorityCandidate(
        LeftRight side,
        ArtilleryTask task,
        float solvedAt,
        int generation,
        GunTaskMode mode) {
        Side = side;
        Task = task;
        SolvedAt = solvedAt;
        Generation = generation;
        Mode = mode;
    }
}

internal sealed class FirePrioritySession {
    public int Generation { get; }
    public ArtilleryTask LeftTask { get; }
    public ArtilleryTask RightTask { get; }

    public FirePrioritySession(int generation, ArtilleryTask leftTask, ArtilleryTask rightTask) {
        Generation = generation;
        LeftTask = leftTask;
        RightTask = rightTask;
    }
}

internal sealed class TurretReservation {
    public ArtilleryTask Task { get; }
    public int Generation { get; }
    public bool Acquired;
    public bool Ready;
    public bool Failed;
    public bool Canceled;
    public bool Released;
    public bool HardCommitted;
    public string FailureReason = "";

    public TurretReservation(ArtilleryTask task, int generation) {
        Task = task;
        Generation = generation;
    }
}
