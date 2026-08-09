from pathlib import Path
import re

path = Path('IronNestFCS.Logic/FSC.cs')
text = path.read_text(encoding='utf-8-sig')


def replace_once(old: str, new: str):
    global text
    if old not in text:
        raise RuntimeError(f'missing replacement anchor: {old[:80]!r}')
    text = text.replace(old, new, 1)


replace_once(
    '    private const float FirePriorityScoreTieTolerance = 0.05f;\n',
    '''    // Release-build probes: turret azimuth ~= 4 deg/s, gun elevation ~= 2 deg/s.\n    // FreshLoad probe pairs converged at 32.20s / 32.27s from ballistic registration to LoadedReady.\n    private const float AzimuthSlewDegreesPerSecond = 4f;\n    private const float ElevationSlewDegreesPerSecond = 2f;\n    private const float FreshLoadReadySeconds = 32.25f;\n    private const float FirePriorityEtaTieToleranceSeconds = 0.10f;\n    private const float FirePriorityAlignmentTieTolerance = 0.05f;\n''')

replace_once(
    '    private bool RegisterBallisticSolution(LeftRight side, ArtilleryTask task, int generation) {\n',
    '    private bool RegisterBallisticSolution(LeftRight side, ArtilleryTask task, int generation, GunTaskMode mode) {\n')

replace_once(
    '        var candidate = new FirePriorityCandidate(side, task, FcsRuntimeClock.Now, generation);\n',
    '        var candidate = new FirePriorityCandidate(side, task, FcsRuntimeClock.Now, generation, mode);\n')

replace_once(
    '        if (!RegisterBallisticSolution(leftRight, task, taskGeneration))\n',
    '        if (!RegisterBallisticSolution(leftRight, task, taskGeneration, mode))\n')

method_pattern = re.compile(
    r'    private void ResolveFirePriorityPair\(FirePriorityCandidate left, FirePriorityCandidate right\) \{.*?\n    private static FirePriorityCandidate FirstByOriginalOrder',
    re.S)

method_replacement = '''    private FireReadyEstimate EstimateFireReady(
        FirePriorityCandidate candidate,
        GunPhysicalState physical,
        float currentAzimuth) {
        var azimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -candidate.Task.angel));
        var elevationDelta = Mathf.Abs(candidate.Task.elevation - physical.Elevation);
        var azimuthSeconds = azimuthDelta / AzimuthSlewDegreesPerSecond;
        var elevationSeconds = elevationDelta / ElevationSlewDegreesPerSecond;

        var loadKnown = true;
        var loadSeconds = 0f;
        var loadLabel = "已装填";
        var physicalMatchesTask = physical.LoadedReady
                                  && physical.ShellType == candidate.Task.bulletType
                                  && physical.PowderCharges == candidate.Task.chargeCount;

        if (physicalMatchesTask) {
            loadSeconds = 0f;
        }
        else if (physical.LoadedReady) {
            loadKnown = false;
            loadLabel = "实装弹药与任务不一致";
        }
        else if (candidate.Mode == GunTaskMode.FreshLoad) {
            var elapsed = Mathf.Max(0f, FcsRuntimeClock.Now - candidate.SolvedAt);
            loadSeconds = Mathf.Max(0f, FreshLoadReadySeconds - elapsed);
            loadLabel = $"FreshLoad 已过{elapsed:F1}s";

            // The 32.25s baseline is an estimate, not a readiness override. If the real gun is still loading
            // after the measured baseline, stop trusting the estimate and fall back to the old alignment model.
            if (elapsed > FreshLoadReadySeconds && !physical.LoadedReady) {
                loadKnown = false;
                loadLabel = $"FreshLoad 超过{FreshLoadReadySeconds:F2}s仍未就绪";
            }
        }
        else if (candidate.Mode == GunTaskMode.ReuseLoadedRound) {
            loadKnown = false;
            loadLabel = "复用弹物理状态已变化";
        }
        else {
            // CompleteShellLoaded begins with shell-in-chamber/C0. We have not yet measured a reliable remaining
            // powder/final-sequence ETA, so do not invent one for scheduling.
            loadKnown = false;
            loadLabel = "半装填ETA待测";
        }

        var localSeconds = loadKnown ? loadSeconds + elevationSeconds : float.NaN;
        var totalSeconds = loadKnown ? Mathf.Max(localSeconds, azimuthSeconds) : float.NaN;
        var alignmentScore = Mathf.Max(azimuthDelta, elevationDelta * 2f);

        return new FireReadyEstimate(
            loadKnown,
            loadLabel,
            loadSeconds,
            elevationSeconds,
            azimuthSeconds,
            totalSeconds,
            alignmentScore);
    }

    private static string FormatEtaDetail(
        string sideLabel,
        FirePriorityCandidate candidate,
        FireReadyEstimate eta) {
        if (eta.LoadKnown) {
            return
                $"{sideLabel}T{candidate.Task.targetId}：预计{eta.TotalSeconds:F1}s（装{eta.LoadSeconds:F1}+仰{eta.ElevationSeconds:F1} / 方{eta.AzimuthSeconds:F1}）";
        }

        var alignmentSeconds = Mathf.Max(eta.ElevationSeconds, eta.AzimuthSeconds);
        return
            $"{sideLabel}T{candidate.Task.targetId}：ETA待测（{eta.LoadLabel}；仅对准{alignmentSeconds:F1}s）";
    }

    private void ResolveFirePriorityPair(FirePriorityCandidate left, FirePriorityCandidate right) {
        if (!CanArbitrateCurrentTasks()) {
            ResolveStateGateFallback(left, right);
            return;
        }

        var turretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        if (turretController == null) {
            var winnerFallback = FirstByOriginalOrder(left, right);
            var loserFallback = ReferenceEquals(winnerFallback, left) ? right : left;
            _firePriorityLeftDetail = $"左T{left.Task.targetId}：已解算（炮塔方位不可用）";
            _firePriorityRightDetail = $"右T{right.Task.targetId}：已解算（炮塔方位不可用）";
            SetPairFirePriority(
                winnerFallback,
                loserFallback,
                "turret angle unavailable; keeping original solved order");
            return;
        }

        var currentAzimuth = turretController.CurrentAngle;
        var leftPhysical = GunPhysicalState.Read("Left");
        var rightPhysical = GunPhysicalState.Read("Right");
        var leftEta = EstimateFireReady(left, leftPhysical, currentAzimuth);
        var rightEta = EstimateFireReady(right, rightPhysical, currentAzimuth);

        _firePriorityLeftDetail = FormatEtaDetail("左", left, leftEta);
        _firePriorityRightDetail = FormatEtaDetail("右", right, rightEta);

        FirePriorityCandidate winner;
        FirePriorityCandidate loser;
        string reason;

        if (leftEta.LoadKnown && rightEta.LoadKnown) {
            if (Mathf.Abs(leftEta.TotalSeconds - rightEta.TotalSeconds) <= FirePriorityEtaTieToleranceSeconds) {
                winner = FirstByOriginalOrder(left, right);
                loser = ReferenceEquals(winner, left) ? right : left;
                reason =
                    $"fire-ready ETA tied; currentAz={currentAzimuth:F1}°, " +
                    $"Left T{left.Task.targetId}={leftEta.TotalSeconds:F1}s " +
                    $"(load {leftEta.LoadSeconds:F1}+el {leftEta.ElevationSeconds:F1}, az {leftEta.AzimuthSeconds:F1}), " +
                    $"Right T{right.Task.targetId}={rightEta.TotalSeconds:F1}s " +
                    $"(load {rightEta.LoadSeconds:F1}+el {rightEta.ElevationSeconds:F1}, az {rightEta.AzimuthSeconds:F1}); " +
                    "keeping original solved order";
            }
            else if (leftEta.TotalSeconds < rightEta.TotalSeconds) {
                winner = left;
                loser = right;
                reason =
                    $"ETA Left T{left.Task.targetId}={leftEta.TotalSeconds:F1}s " +
                    $"(load {leftEta.LoadSeconds:F1}+el {leftEta.ElevationSeconds:F1}, az {leftEta.AzimuthSeconds:F1}) < " +
                    $"Right T{right.Task.targetId}={rightEta.TotalSeconds:F1}s " +
                    $"(load {rightEta.LoadSeconds:F1}+el {rightEta.ElevationSeconds:F1}, az {rightEta.AzimuthSeconds:F1})";
            }
            else {
                winner = right;
                loser = left;
                reason =
                    $"ETA Right T{right.Task.targetId}={rightEta.TotalSeconds:F1}s " +
                    $"(load {rightEta.LoadSeconds:F1}+el {rightEta.ElevationSeconds:F1}, az {rightEta.AzimuthSeconds:F1}) < " +
                    $"Left T{left.Task.targetId}={leftEta.TotalSeconds:F1}s " +
                    $"(load {leftEta.LoadSeconds:F1}+el {leftEta.ElevationSeconds:F1}, az {leftEta.AzimuthSeconds:F1})";
            }
        }
        else {
            // At least one load phase has no measured ETA yet. Fall back to the already-tested normalized
            // alignment comparison rather than fabricating a load duration.
            if (Mathf.Abs(leftEta.AlignmentScore - rightEta.AlignmentScore) <= FirePriorityAlignmentTieTolerance) {
                winner = FirstByOriginalOrder(left, right);
                loser = ReferenceEquals(winner, left) ? right : left;
            }
            else if (leftEta.AlignmentScore < rightEta.AlignmentScore) {
                winner = left;
                loser = right;
            }
            else {
                winner = right;
                loser = left;
            }

            reason =
                $"load ETA unavailable; alignment fallback: Left T{left.Task.targetId}={leftEta.AlignmentScore:F1} " +
                $"({leftEta.LoadLabel}), Right T{right.Task.targetId}={rightEta.AlignmentScore:F1} ({rightEta.LoadLabel})";
        }

        SetPairFirePriority(winner, loser, reason);
    }

    private static FirePriorityCandidate FirstByOriginalOrder'''

text, count = method_pattern.subn(lambda _: method_replacement, text, count=1)
if count != 1:
    raise RuntimeError(f'ResolveFirePriorityPair replacement count={count}')

candidate_pattern = re.compile(
    r'    private sealed class FirePriorityCandidate \{.*?\n    \}\n\n    private sealed class FirePrioritySession',
    re.S)

candidate_replacement = '''    private sealed class FireReadyEstimate {
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

    private sealed class FirePriorityCandidate {
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

    private sealed class FirePrioritySession'''

text, count = candidate_pattern.subn(lambda _: candidate_replacement, text, count=1)
if count != 1:
    raise RuntimeError(f'FirePriorityCandidate replacement count={count}')

path.write_text(text, encoding='utf-8')
