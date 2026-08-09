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
    private Dictionary<int, Transform> artilleries = new();
    private Transform? fireMissionRoot;
    private FireMission? fireMission;
    
    public bool TryBind() {
        artilleries = new Dictionary<int, Transform>();
        fireMissionRoot = null;
        fireMission = null;

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

        // Fire Mission Root is only used by the optional entity-exposure/debug helper.
        // Some release scenes no longer contain this object, so it must not block
        // the core map-marker fire-control path (T1/T2/...).
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

    public ArtilleryTask? GetMarkTarget(int index) {
        if (turret == null) {
            MelonLogger.Error("[FCS] GetMarkTarget: turret unbound");
            return null;
        }

        if (!artilleries.TryGetValue(index, out var artillery)) {
            MelonLogger.Error($"[FCS] GetMarkTarget: artillery marker T{index} not found");
            return null;
        }

        var target = artillery.localPosition - turret.localPosition;
        return BuildMarkTarget(artillery.localPosition, target);
    }

    /// <summary>
    /// Release scenes can update tactical-map token transforms for a few frames while the map/task
    /// is being initialized. Capturing that transient position makes an occasional first shot use
    /// the wrong range/azimuth even though retrying the same target is correct. Sample the relative
    /// marker vector until it is stable for several consecutive reads, then snapshot the task.
    /// </summary>
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

        var deadline = Time.realtimeSinceStartup + Mathf.Max(0.5f, timeoutSeconds);
        var previousRelative = Vector3.zero;
        var havePrevious = false;
        var stableSamples = 0;
        var sampleCount = 0;
        var lastDelta = 0f;

        while (Time.realtimeSinceStartup < deadline) {
            var markerLocal = artillery.localPosition;
            var turretLocal = turret.localPosition;
            var relative = markerLocal - turretLocal;
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
                completed(task);
                yield break;
            }

            yield return new WaitForSeconds(MarkerSampleIntervalSeconds);
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
