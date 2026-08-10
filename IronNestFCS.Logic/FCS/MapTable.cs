using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class MapTable {
    private const float MarkerSampleIntervalSeconds = 0.1f;
    private const float MarkerStabilizeTimeoutSeconds = 2f;
    private const float MarkerStableToleranceLocal = 0.0025f;
    private const int MarkerStableSampleCount = 3;

    private Transform? turret;
    private Transform? diagnosticMapSurface;
    private Dictionary<int, Transform> artilleries = new();
    private Transform? fireMissionRoot;
    private FireMission? fireMission;
    private bool turretLocationFallbackWarned;
    
    public bool TryBind() {
        artilleries = new Dictionary<int, Transform>();
        diagnosticMapSurface = null;
        fireMissionRoot = null;
        fireMission = null;
        turretLocationFallbackWarned = false;

        var turretObject = GameObject.Find("Player Turret Piece");
        if (turretObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Player Turret Piece，当前场景尚未就绪");
            return false;
        }

        var mapObject = GameObject.Find("Draggable Surface");
        if (mapObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Draggable Surface，当前场景尚未就绪");
            return false;
        }

        turret = turretObject.transform;
        diagnosticMapSurface = mapObject.transform;
        var map = mapObject.transform;
        for (var i = 0; i < map.childCount; ++i) {
            var t = map.GetChild(i);
            if (t.name != "MapToken_Artillery") continue;
            var tmp = t.GetComponentInChildren<Il2CppTMPro.TextMeshPro>();
            if (tmp == null) continue;
            if (!int.TryParse(tmp.text, out var id)) continue;
            artilleries[id] = t;
        }

        if (artilleries.Count == 0) {
            MelonLogger.Warning("[FCS] 未找到任何 MapToken_Artillery，当前场景尚未就绪");
            return false;
        }

        MelonLogger.Msg($"[FCS] 找到 Player Turret Piece: {turret}, Artilleries: {artilleries.Count}");
        LogLegacyBindDiagnostics();

        var fireMissionObject = GameObject.Find("Fire Mission Root");
        if (fireMissionObject != null) {
            fireMissionRoot = fireMissionObject.transform;
            fireMission = fireMissionRoot.GetComponent<FireMission>();
            if (fireMission == null) {
                MelonLogger.Warning("[FCS] Fire Mission Root 存在但缺少 FireMission 组件；调试实体功能不可用");
            }
        }
        else {
            MelonLogger.Msg("[FCS] Fire Mission Root 不存在；忽略（不影响地图标记火控）");
        }

        return true;
    }

    private ArtilleryTask BuildMarkTarget(Vector3 artilleryLocalPosition, Vector3 target) {
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        return new ArtilleryTask {
            angel = angle,
            distance = dist,
            position = artilleryLocalPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f)
        };
    }

    private static float GetDiagnosticAzimuth(Vector3 target) {
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        return angle;
    }

    // High-cla algorithm: use the game's TurretLocation anchor as the authoritative firing origin.
    // Player Turret Piece remains only as a fallback when TurretLocation is unavailable.
    private Vector3 GetActiveTurretLocal() {
        if (diagnosticMapSurface != null) {
            var turretLocationObject = GameObject.Find("TurretLocation");
            if (turretLocationObject != null) {
                return diagnosticMapSurface.InverseTransformPoint(turretLocationObject.transform.position);
            }
        }

        if (!turretLocationFallbackWarned) {
            turretLocationFallbackWarned = true;
            MelonLogger.Warning("[FCS] TurretLocation unavailable; falling back to Player Turret Piece.localPosition");
        }
        return turret?.localPosition ?? Vector3.zero;
    }

    private void LogLegacyBindDiagnostics() {
        if (turret == null || diagnosticMapSurface == null) return;

        var legacy = turret.localPosition;
        var converted = diagnosticMapSurface.InverseTransformPoint(turret.position);
        var parent = turret.parent;
        var turretId = turret.GetInstanceID();
        var siblingIndex = turret.GetSiblingIndex();
        var parentName = parent != null ? parent.name : "<none>";
        var parentId = parent != null ? parent.GetInstanceID() : 0;
        var parentChildCount = parent != null ? parent.childCount : 0;
        var mapId = diagnosticMapSurface.GetInstanceID();
        var sameParent = parent == diagnosticMapSurface;

        MelonLogger.Msg(
            $"[FCS DIAG LEGACY] bind turret={turret.name}#{turretId}, sibling={siblingIndex}/{parentChildCount}, " +
            $"turretWorld={turret.position:F4}, turretLocal(legacy-compare)={legacy:F4}, " +
            $"turretOnMap(converted-compare)={converted:F4}, delta(compare-legacy)={(converted - legacy):F4}");
        MelonLogger.Msg(
            $"[FCS DIAG LEGACY] hierarchy turretParent={parentName}#{parentId}, " +
            $"mapSurface={diagnosticMapSurface.name}#{mapId}, sameParent={sameParent}");
    }

    private void LogLegacyAimDiagnostics(int index, Vector3 markerLocal, Vector3 legacyTarget) {
        if (turret == null || diagnosticMapSurface == null) return;

        var legacyTurretLocal = turret.localPosition;
        var convertedTurretOnMap = diagnosticMapSurface.InverseTransformPoint(turret.position);
        var compareTarget = markerLocal - convertedTurretOnMap;
        var legacyAzimuth = GetDiagnosticAzimuth(legacyTarget);
        var compareAzimuth = GetDiagnosticAzimuth(compareTarget);
        var legacyDistance = legacyTarget.magnitude * 3.8164f;
        var compareDistance = compareTarget.magnitude * 3.8164f;
        var parent = turret.parent;
        var turretId = turret.GetInstanceID();
        var siblingIndex = turret.GetSiblingIndex();
        var parentId = parent != null ? parent.GetInstanceID() : 0;
        var parentChildCount = parent != null ? parent.childCount : 0;

        MelonLogger.Msg(
            $"[FCS DIAG LEGACY] T{index} turret={turret.name}#{turretId}, parent#{parentId}, sibling={siblingIndex}/{parentChildCount}, " +
            $"marker={markerLocal:F4}, turretLocal(legacy-compare)={legacyTurretLocal:F4}, " +
            $"turretOnMap(converted-compare)={convertedTurretOnMap:F4}, delta(compare-legacy)={(convertedTurretOnMap - legacyTurretLocal):F4}");
        MelonLogger.Msg(
            $"[FCS DIAG LEGACY] T{index} compare legacy az={legacyAzimuth:F3}° dist={legacyDistance:F4}km target={legacyTarget:F4} | " +
            $"converted az={compareAzimuth:F3}° dist={compareDistance:F4}km target={compareTarget:F4} | " +
            $"delta compare-legacy: az={Mathf.DeltaAngle(legacyAzimuth, compareAzimuth):+0.000;-0.000;0.000}° dist={(compareDistance - legacyDistance):+0.0000;-0.0000;0.0000}km");
    }

    private void LogHighClaAimDiagnostics(int index, Vector3 markerLocal, Vector3 activeTarget) {
        var activeOrigin = markerLocal - activeTarget;
        var activeAzimuth = GetDiagnosticAzimuth(activeTarget);
        var activeDistance = activeTarget.magnitude * 3.8164f;
        MelonLogger.Msg(
            $"[FCS DIAG HIGHCLA] T{index} ACTIVE TurretLocation origin={activeOrigin:F4}, marker={markerLocal:F4}, " +
            $"az={activeAzimuth:F3}° dist={activeDistance:F4}km target={activeTarget:F4}");
    }

    public ArtilleryTask? GetMarkTarget(int index) {
        if (turret == null) {
            MelonLogger.Error("[FCS] GetMarkTarget: turret unbound");
            return null;
        }

        if (!artilleries.TryGetValue(index, out var artillery)) {
            MelonLogger.Error($"[FCS] GetMarkTarget: artillery marker T{index} not found");
            return null;
        }

        var markerLocal = artillery.localPosition;
        var activeTarget = markerLocal - GetActiveTurretLocal();
        var legacyTarget = markerLocal - turret.localPosition;
        LogLegacyAimDiagnostics(index, markerLocal, legacyTarget);
        LogHighClaAimDiagnostics(index, markerLocal, activeTarget);
        return BuildMarkTarget(markerLocal, activeTarget);
    }

    public IEnumerator GetStableMarkTarget(int index, Action<ArtilleryTask?> completed,
        float timeoutSeconds = MarkerStabilizeTimeoutSeconds) {
        if (turret == null) {
            MelonLogger.Error("[FCS] GetStableMarkTarget: turret unbound");
            completed(null);
            yield break;
        }

        if (!artilleries.TryGetValue(index, out var artillery)) {
            MelonLogger.Error($"[FCS] GetStableMarkTarget: artillery marker T{index} not found");
            completed(null);
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + Mathf.Max(0.5f, timeoutSeconds);
        var previousRelative = Vector3.zero;
        var havePrevious = false;
        var stableSamples = 0;
        var sampleCount = 0;
        var lastDelta = 0f;

        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (FcsRuntimeClock.Now >= deadline)
                break;

            var markerLocal = artillery.localPosition;
            var relative = markerLocal - GetActiveTurretLocal();
            sampleCount++;

            if (!havePrevious) {
                previousRelative = relative;
                havePrevious = true;
                stableSamples = 1;
            }
            else {
                lastDelta = (relative - previousRelative).magnitude;
                stableSamples = lastDelta <= MarkerStableToleranceLocal
                    ? stableSamples + 1
                    : 1;
                previousRelative = relative;
            }

            if (stableSamples >= MarkerStableSampleCount) {
                var task = BuildMarkTarget(markerLocal, relative);
                MelonLogger.Msg(
                    $"[FCS] T{index} marker stabilized: samples={sampleCount}, drift={lastDelta:F5}, " +
                    $"azimuth={task.angel:F2}°, distance={task.distance:F3}km");
                LogLegacyAimDiagnostics(index, markerLocal, markerLocal - turret.localPosition);
                LogHighClaAimDiagnostics(index, markerLocal, relative);
                completed(task);
                yield break;
            }

            yield return FcsRuntimeClock.WaitForSeconds(MarkerSampleIntervalSeconds);
        }

        MelonLogger.Warning(
            $"[FCS] T{index} marker did not stabilize within {timeoutSeconds:F1}s; " +
            $"last drift={lastDelta:F5}. Task was not queued; click T{index} again after the map settles.");
        completed(null);
    }

    public List<EntityLocation> GetAllFireMissionEntities() {
        List<EntityLocation> res = new();
        if (fireMissionRoot == null) {
            return res;
        }

        for (var i = 0; i < fireMissionRoot.childCount; ++i) {
            var m = fireMissionRoot.GetChild(i).GetComponent<EntityLocation>();
            if (m != null) res.Add(m);
        }
        return res;
    }
    
}
