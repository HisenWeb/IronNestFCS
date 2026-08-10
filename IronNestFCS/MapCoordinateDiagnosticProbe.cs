using System.Diagnostics;
using MelonLoader.Utils;
using UnityEngine;

namespace IronNestFCS;

/// <summary>
/// Diagnostic-only probe for legacy marker, converted marker, and TurretLocation origins.
/// Lives in the stable Host so it keeps sampling across TaskSystem F9 unload/reload gaps.
/// It never mutates scene objects or participates in fire-control calculations.
/// </summary>
internal sealed class MapCoordinateDiagnosticProbe : IDisposable
{
    private const float SampleIntervalSeconds = 0.05f;
    private const float HeartbeatIntervalSeconds = 5f;
    private const float PositionTolerance = 0.00001f;
    private const float RotationToleranceDegrees = 0.001f;
    private const float ScaleTolerance = 0.00001f;

    private readonly StreamWriter? _writer;
    private readonly string _path = "";
    private Snapshot? _last;
    private float _nextSampleAt;
    private float _nextHeartbeatAt;
    private int _sequence;
    private bool _disposed;

    public string Path => _path;

    public MapCoordinateDiagnosticProbe()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var processStarted = process.StartTime;
            var logsRoot = System.IO.Path.Combine(
                MelonEnvironment.UserDataDirectory,
                "IronNestFCS",
                "Logs");
            var dayDirectory = System.IO.Path.Combine(logsRoot, processStarted.ToString("yyyy-MM-dd"));
            var runDirectory = System.IO.Path.Combine(
                dayDirectory,
                $"run-{processStarted:HHmmss}-pid{process.Id}");
            Directory.CreateDirectory(runDirectory);

            _path = System.IO.Path.Combine(runDirectory, "mapdiag.log");
            var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream) { AutoFlush = true };
            WriteRaw(
                $"START | processStart={processStarted:yyyy-MM-dd HH:mm:ss} | pid={process.Id} | " +
                $"sample={SampleIntervalSeconds:F2}s | diagnostic-only legacy-active/new-compare-only/TurretLocation-compare-only");
        }
        catch
        {
            _writer = null;
        }
    }

    public void Mark(string reason)
    {
        if (_disposed) return;
        try
        {
            WriteRaw($"MARK | {reason}");
            Tick(force: true, reason: "mark:" + reason);
        }
        catch
        {
        }
    }

    public void SceneChanged(int buildIndex, string sceneName)
    {
        if (_disposed) return;
        try
        {
            WriteRaw($"SCENE | build={buildIndex} name={sceneName}");
            _last = null;
            Tick(force: true, reason: "scene-change");
        }
        catch
        {
        }
    }

    public void Tick(bool force = false, string reason = "tick")
    {
        if (_disposed || _writer == null)
            return;

        try
        {
            var now = Time.unscaledTime;
            if (!force && now < _nextSampleAt)
                return;
            _nextSampleAt = now + SampleIntervalSeconds;

            var current = Capture();
            if (current == null)
            {
                if (_last != null)
                {
                    WriteRaw($"LOST | reason={reason} | previous={Format(_last)}");
                    _last = null;
                }
                return;
            }

            if (_last == null)
            {
                WriteRaw($"ACQUIRE | reason={reason} | {Format(current)}");
                _last = current;
                _nextHeartbeatAt = now + HeartbeatIntervalSeconds;
                return;
            }

            var changes = DescribeChanges(_last, current);
            if (changes.Count > 0)
            {
                WriteRaw(
                    $"CHANGE | reason={reason} | fields={string.Join(",", changes)} | " +
                    $"before={Format(_last)} | after={Format(current)}");
                _nextHeartbeatAt = now + HeartbeatIntervalSeconds;
            }
            else if (force || now >= _nextHeartbeatAt)
            {
                WriteRaw($"SNAPSHOT | reason={reason} | {Format(current)}");
                _nextHeartbeatAt = now + HeartbeatIntervalSeconds;
            }

            _last = current;
        }
        catch (Exception ex)
        {
            try { WriteRaw($"PROBE-ERROR | {ex.GetType().Name}: {ex.Message}"); }
            catch { }
        }
    }

    private static Snapshot? Capture()
    {
        var turretObject = GameObject.Find("Player Turret Piece");
        var mapObject = GameObject.Find("Draggable Surface");
        if (turretObject == null || mapObject == null)
            return null;

        var turret = turretObject.transform;
        var map = mapObject.transform;
        var turretParent = turret.parent;
        var mapParent = map.parent;
        var turretWorld = turret.position;
        var legacy = turret.localPosition;
        var converted = map.InverseTransformPoint(turretWorld);

        var turretLocationObject = GameObject.Find("TurretLocation");
        var turretLocation = turretLocationObject != null ? turretLocationObject.transform : null;
        var turretLocationParent = turretLocation != null ? turretLocation.parent : null;
        var turretLocationExists = turretLocation != null;
        var turretLocationWorld = turretLocationExists ? turretLocation!.position : Vector3.zero;
        var turretLocationOnMap = turretLocationExists
            ? map.InverseTransformPoint(turretLocationWorld)
            : Vector3.zero;

        return new Snapshot
        {
            TurretId = turret.GetInstanceID(),
            MapId = map.GetInstanceID(),
            TurretParentId = turretParent != null ? turretParent.GetInstanceID() : 0,
            MapParentId = mapParent != null ? mapParent.GetInstanceID() : 0,
            TurretSibling = turret.GetSiblingIndex(),
            MapSibling = map.GetSiblingIndex(),
            TurretParentChildCount = turretParent != null ? turretParent.childCount : 0,
            MapParentChildCount = mapParent != null ? mapParent.childCount : 0,
            SameParent = turretParent == map,
            TurretPath = GetPath(turret),
            MapPath = GetPath(map),
            TurretParentPath = GetPath(turretParent),
            MapParentPath = GetPath(mapParent),
            TurretLocalPosition = turret.localPosition,
            TurretWorldPosition = turretWorld,
            TurretLocalRotation = turret.localRotation,
            TurretWorldRotation = turret.rotation,
            TurretLocalEuler = turret.localEulerAngles,
            TurretWorldEuler = turret.eulerAngles,
            TurretLocalScale = turret.localScale,
            TurretLossyScale = turret.lossyScale,
            MapLocalPosition = map.localPosition,
            MapWorldPosition = map.position,
            MapLocalRotation = map.localRotation,
            MapWorldRotation = map.rotation,
            MapLocalEuler = map.localEulerAngles,
            MapWorldEuler = map.eulerAngles,
            MapLocalScale = map.localScale,
            MapLossyScale = map.lossyScale,
            LegacyOrigin = legacy,
            ConvertedOrigin = converted,
            OriginDelta = converted - legacy,

            TurretLocationExists = turretLocationExists,
            TurretLocationId = turretLocationExists ? turretLocation!.GetInstanceID() : 0,
            TurretLocationParentId = turretLocationParent != null ? turretLocationParent.GetInstanceID() : 0,
            TurretLocationSibling = turretLocationExists ? turretLocation!.GetSiblingIndex() : -1,
            TurretLocationParentChildCount = turretLocationParent != null ? turretLocationParent.childCount : 0,
            TurretLocationPath = GetPath(turretLocation),
            TurretLocationParentPath = GetPath(turretLocationParent),
            TurretLocationActiveSelf = turretLocationObject != null && turretLocationObject.activeSelf,
            TurretLocationActiveInHierarchy = turretLocationObject != null && turretLocationObject.activeInHierarchy,
            TurretLocationLocalPosition = turretLocationExists ? turretLocation!.localPosition : Vector3.zero,
            TurretLocationWorldPosition = turretLocationWorld,
            TurretLocationLocalRotation = turretLocationExists ? turretLocation!.localRotation : Quaternion.identity,
            TurretLocationWorldRotation = turretLocationExists ? turretLocation!.rotation : Quaternion.identity,
            TurretLocationLocalEuler = turretLocationExists ? turretLocation!.localEulerAngles : Vector3.zero,
            TurretLocationWorldEuler = turretLocationExists ? turretLocation!.eulerAngles : Vector3.zero,
            TurretLocationLocalScale = turretLocationExists ? turretLocation!.localScale : Vector3.zero,
            TurretLocationLossyScale = turretLocationExists ? turretLocation!.lossyScale : Vector3.zero,
            TurretLocationOnMap = turretLocationOnMap,
            TurretLocationMinusLegacy = turretLocationExists ? turretLocationOnMap - legacy : Vector3.zero,
            TurretLocationMinusConverted = turretLocationExists ? turretLocationOnMap - converted : Vector3.zero,
        };
    }

    private static List<string> DescribeChanges(Snapshot before, Snapshot after)
    {
        var changes = new List<string>();

        if (before.TurretId != after.TurretId) changes.Add("turret.id");
        if (before.MapId != after.MapId) changes.Add("map.id");
        if (before.TurretParentId != after.TurretParentId) changes.Add("turret.parent");
        if (before.MapParentId != after.MapParentId) changes.Add("map.parent");
        if (before.TurretSibling != after.TurretSibling) changes.Add("turret.sibling");
        if (before.MapSibling != after.MapSibling) changes.Add("map.sibling");
        if (before.TurretParentChildCount != after.TurretParentChildCount) changes.Add("turret.parentChildCount");
        if (before.MapParentChildCount != after.MapParentChildCount) changes.Add("map.parentChildCount");
        if (before.SameParent != after.SameParent) changes.Add("sameParent");
        if (!string.Equals(before.TurretPath, after.TurretPath, StringComparison.Ordinal)) changes.Add("turret.path");
        if (!string.Equals(before.MapPath, after.MapPath, StringComparison.Ordinal)) changes.Add("map.path");

        AddVectorChange(changes, "turret.localPos", before.TurretLocalPosition, after.TurretLocalPosition, PositionTolerance);
        AddVectorChange(changes, "turret.worldPos", before.TurretWorldPosition, after.TurretWorldPosition, PositionTolerance);
        AddRotationChange(changes, "turret.localRot", before.TurretLocalRotation, after.TurretLocalRotation);
        AddRotationChange(changes, "turret.worldRot", before.TurretWorldRotation, after.TurretWorldRotation);
        AddVectorChange(changes, "turret.localScale", before.TurretLocalScale, after.TurretLocalScale, ScaleTolerance);
        AddVectorChange(changes, "turret.lossyScale", before.TurretLossyScale, after.TurretLossyScale, ScaleTolerance);

        AddVectorChange(changes, "map.localPos", before.MapLocalPosition, after.MapLocalPosition, PositionTolerance);
        AddVectorChange(changes, "map.worldPos", before.MapWorldPosition, after.MapWorldPosition, PositionTolerance);
        AddRotationChange(changes, "map.localRot", before.MapLocalRotation, after.MapLocalRotation);
        AddRotationChange(changes, "map.worldRot", before.MapWorldRotation, after.MapWorldRotation);
        AddVectorChange(changes, "map.localScale", before.MapLocalScale, after.MapLocalScale, ScaleTolerance);
        AddVectorChange(changes, "map.lossyScale", before.MapLossyScale, after.MapLossyScale, ScaleTolerance);

        AddVectorChange(changes, "legacyOrigin", before.LegacyOrigin, after.LegacyOrigin, PositionTolerance);
        AddVectorChange(changes, "convertedOrigin", before.ConvertedOrigin, after.ConvertedOrigin, PositionTolerance);
        AddVectorChange(changes, "originDelta", before.OriginDelta, after.OriginDelta, PositionTolerance);

        if (before.TurretLocationExists != after.TurretLocationExists) changes.Add("turretLocation.exists");
        if (before.TurretLocationId != after.TurretLocationId) changes.Add("turretLocation.id");
        if (before.TurretLocationParentId != after.TurretLocationParentId) changes.Add("turretLocation.parent");
        if (before.TurretLocationSibling != after.TurretLocationSibling) changes.Add("turretLocation.sibling");
        if (before.TurretLocationParentChildCount != after.TurretLocationParentChildCount) changes.Add("turretLocation.parentChildCount");
        if (before.TurretLocationActiveSelf != after.TurretLocationActiveSelf) changes.Add("turretLocation.activeSelf");
        if (before.TurretLocationActiveInHierarchy != after.TurretLocationActiveInHierarchy) changes.Add("turretLocation.activeInHierarchy");
        if (!string.Equals(before.TurretLocationPath, after.TurretLocationPath, StringComparison.Ordinal)) changes.Add("turretLocation.path");
        if (!string.Equals(before.TurretLocationParentPath, after.TurretLocationParentPath, StringComparison.Ordinal)) changes.Add("turretLocation.parentPath");
        AddVectorChange(changes, "turretLocation.localPos", before.TurretLocationLocalPosition, after.TurretLocationLocalPosition, PositionTolerance);
        AddVectorChange(changes, "turretLocation.worldPos", before.TurretLocationWorldPosition, after.TurretLocationWorldPosition, PositionTolerance);
        AddRotationChange(changes, "turretLocation.localRot", before.TurretLocationLocalRotation, after.TurretLocationLocalRotation);
        AddRotationChange(changes, "turretLocation.worldRot", before.TurretLocationWorldRotation, after.TurretLocationWorldRotation);
        AddVectorChange(changes, "turretLocation.localScale", before.TurretLocationLocalScale, after.TurretLocationLocalScale, ScaleTolerance);
        AddVectorChange(changes, "turretLocation.lossyScale", before.TurretLocationLossyScale, after.TurretLocationLossyScale, ScaleTolerance);
        AddVectorChange(changes, "turretLocation.onMap", before.TurretLocationOnMap, after.TurretLocationOnMap, PositionTolerance);
        AddVectorChange(changes, "turretLocation-minus-legacy", before.TurretLocationMinusLegacy, after.TurretLocationMinusLegacy, PositionTolerance);
        AddVectorChange(changes, "turretLocation-minus-converted", before.TurretLocationMinusConverted, after.TurretLocationMinusConverted, PositionTolerance);

        return changes;
    }

    private static void AddVectorChange(List<string> changes, string name, Vector3 before, Vector3 after, float tolerance)
    {
        if ((after - before).sqrMagnitude > tolerance * tolerance)
            changes.Add(name);
    }

    private static void AddRotationChange(List<string> changes, string name, Quaternion before, Quaternion after)
    {
        if (Quaternion.Angle(before, after) > RotationToleranceDegrees)
            changes.Add(name);
    }

    private static string GetPath(Transform? transform)
    {
        if (transform == null)
            return "<none>";

        try
        {
            var parts = new List<string>();
            var current = transform;
            var depth = 0;
            while (current != null && depth++ < 32)
            {
                parts.Add($"{current.name}#{current.GetInstanceID()}");
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
        catch
        {
            return "<path-error>";
        }
    }

    private string Format(Snapshot s)
    {
        return
            $"seq={++_sequence} t={Time.unscaledTime:F3} " +
            $"turret=#{s.TurretId} parent=#{s.TurretParentId} sibling={s.TurretSibling}/{s.TurretParentChildCount} " +
            $"sameParent={s.SameParent} path={s.TurretPath} parentPath={s.TurretParentPath} " +
            $"localPos={V(s.TurretLocalPosition)} worldPos={V(s.TurretWorldPosition)} " +
            $"localEuler={V(s.TurretLocalEuler)} worldEuler={V(s.TurretWorldEuler)} " +
            $"localScale={V(s.TurretLocalScale)} lossyScale={V(s.TurretLossyScale)} | " +
            $"map=#{s.MapId} parent=#{s.MapParentId} sibling={s.MapSibling}/{s.MapParentChildCount} " +
            $"path={s.MapPath} parentPath={s.MapParentPath} " +
            $"localPos={V(s.MapLocalPosition)} worldPos={V(s.MapWorldPosition)} " +
            $"localEuler={V(s.MapLocalEuler)} worldEuler={V(s.MapWorldEuler)} " +
            $"localScale={V(s.MapLocalScale)} lossyScale={V(s.MapLossyScale)} | " +
            $"legacyOrigin={V(s.LegacyOrigin)} convertedOrigin={V(s.ConvertedOrigin)} delta={V(s.OriginDelta)} | " +
            $"turretLocation exists={s.TurretLocationExists} id=#{s.TurretLocationId} parent=#{s.TurretLocationParentId} " +
            $"sibling={s.TurretLocationSibling}/{s.TurretLocationParentChildCount} active={s.TurretLocationActiveSelf}/{s.TurretLocationActiveInHierarchy} " +
            $"path={s.TurretLocationPath} parentPath={s.TurretLocationParentPath} " +
            $"localPos={V(s.TurretLocationLocalPosition)} worldPos={V(s.TurretLocationWorldPosition)} " +
            $"localEuler={V(s.TurretLocationLocalEuler)} worldEuler={V(s.TurretLocationWorldEuler)} " +
            $"localScale={V(s.TurretLocationLocalScale)} lossyScale={V(s.TurretLocationLossyScale)} " +
            $"onMap={V(s.TurretLocationOnMap)} minusLegacy={V(s.TurretLocationMinusLegacy)} minusConverted={V(s.TurretLocationMinusConverted)}";
    }

    private static string V(Vector3 value) => $"({value.x:F5},{value.y:F5},{value.z:F5})";

    private void WriteRaw(string text)
    {
        if (_writer == null)
            return;
        _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} | {text}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { WriteRaw("STOP"); } catch { }
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
    }

    private sealed class Snapshot
    {
        public int TurretId;
        public int MapId;
        public int TurretParentId;
        public int MapParentId;
        public int TurretSibling;
        public int MapSibling;
        public int TurretParentChildCount;
        public int MapParentChildCount;
        public bool SameParent;
        public string TurretPath = "";
        public string MapPath = "";
        public string TurretParentPath = "";
        public string MapParentPath = "";
        public Vector3 TurretLocalPosition;
        public Vector3 TurretWorldPosition;
        public Quaternion TurretLocalRotation;
        public Quaternion TurretWorldRotation;
        public Vector3 TurretLocalEuler;
        public Vector3 TurretWorldEuler;
        public Vector3 TurretLocalScale;
        public Vector3 TurretLossyScale;
        public Vector3 MapLocalPosition;
        public Vector3 MapWorldPosition;
        public Quaternion MapLocalRotation;
        public Quaternion MapWorldRotation;
        public Vector3 MapLocalEuler;
        public Vector3 MapWorldEuler;
        public Vector3 MapLocalScale;
        public Vector3 MapLossyScale;
        public Vector3 LegacyOrigin;
        public Vector3 ConvertedOrigin;
        public Vector3 OriginDelta;

        public bool TurretLocationExists;
        public int TurretLocationId;
        public int TurretLocationParentId;
        public int TurretLocationSibling;
        public int TurretLocationParentChildCount;
        public bool TurretLocationActiveSelf;
        public bool TurretLocationActiveInHierarchy;
        public string TurretLocationPath = "";
        public string TurretLocationParentPath = "";
        public Vector3 TurretLocationLocalPosition;
        public Vector3 TurretLocationWorldPosition;
        public Quaternion TurretLocationLocalRotation;
        public Quaternion TurretLocationWorldRotation;
        public Vector3 TurretLocationLocalEuler;
        public Vector3 TurretLocationWorldEuler;
        public Vector3 TurretLocationLocalScale;
        public Vector3 TurretLocationLossyScale;
        public Vector3 TurretLocationOnMap;
        public Vector3 TurretLocationMinusLegacy;
        public Vector3 TurretLocationMinusConverted;
    }
}
