using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class MapTable {
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
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        var task = new ArtilleryTask {
            angel = angle,
            distance = dist,
            position = artillery.localPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f)
        };
        return task;
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
