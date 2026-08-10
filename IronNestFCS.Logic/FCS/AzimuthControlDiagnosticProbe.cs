using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// Temporary diagnostic probe for identifying the physical fast/slow azimuth controls.
/// It never writes to game state. Candidates are sampled at low frequency and only
/// changed transforms are logged.
/// </summary>
internal sealed class AzimuthControlDiagnosticProbe
{
    private const float SampleIntervalSeconds = 0.20f;
    private const float TurretLogIntervalSeconds = 0.50f;
    private const float PositionChangeTolerance = 0.0005f;
    private const float RotationChangeToleranceDegrees = 0.05f;
    private const float TurretAngleChangeToleranceDegrees = 0.25f;

    private static readonly string[] NameHints =
    {
        "azimuth", "traverse", "rotation", "rotate", "bearing", "turret",
        "handwheel", "hand wheel", "wheel", "valve", "handle", "lever", "ball"
    };

    private readonly Dictionary<int, CandidateSnapshot> _snapshots = new();
    private readonly Dictionary<int, Transform> _candidates = new();

    private TurretController? _turretController;
    private float _nextSampleTime;
    private float _nextTurretLogTime;
    private float _lastTurretAngle;
    private bool _haveTurretAngle;
    private bool _bound;

    public void TryBind()
    {
        Reset();

        _turretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();

        foreach (var transform in UnityEngine.Object.FindObjectsOfType<Transform>(true))
        {
            if (transform == null || !IsCandidate(transform))
                continue;

            var id = transform.GetInstanceID();
            _candidates[id] = transform;
            _snapshots[id] = Capture(transform);
        }

        if (_turretController != null)
        {
            _lastTurretAngle = _turretController.CurrentAngle;
            _haveTurretAngle = true;
        }

        _nextSampleTime = Time.realtimeSinceStartup + SampleIntervalSeconds;
        _nextTurretLogTime = Time.realtimeSinceStartup + TurretLogIntervalSeconds;
        _bound = true;

        MelonLogger.Msg(
            $"[FCS DIAG AZ] probe armed: candidates={_candidates.Count}. " +
            "Operate fast azimuth once, then slow azimuth once; only changed controls are logged.");
    }

    public void Update()
    {
        if (!_bound)
            return;

        var now = Time.realtimeSinceStartup;
        if (now < _nextSampleTime)
            return;
        _nextSampleTime = now + SampleIntervalSeconds;

        foreach (var pair in _candidates)
        {
            var transform = pair.Value;
            if (transform == null)
                continue;

            var current = Capture(transform);
            if (!_snapshots.TryGetValue(pair.Key, out var previous))
            {
                _snapshots[pair.Key] = current;
                continue;
            }

            var moved = (current.LocalPosition - previous.LocalPosition).magnitude > PositionChangeTolerance;
            var rotated = Quaternion.Angle(current.LocalRotation, previous.LocalRotation) > RotationChangeToleranceDegrees;
            if (!moved && !rotated)
                continue;

            _snapshots[pair.Key] = current;
            MelonLogger.Msg(
                $"[FCS DIAG AZ] CONTROL CHANGE obj={transform.name}#{pair.Key} path={BuildPath(transform)} " +
                $"localPos {previous.LocalPosition:F4}->{current.LocalPosition:F4} " +
                $"localEuler {previous.LocalEuler:F2}->{current.LocalEuler:F2} " +
                $"components=[{GetComponentNames(transform)}] {GetTurretState()}");
        }

        if (_turretController != null && now >= _nextTurretLogTime)
        {
            _nextTurretLogTime = now + TurretLogIntervalSeconds;
            var angle = _turretController.CurrentAngle;
            if (!_haveTurretAngle || Mathf.Abs(Mathf.DeltaAngle(_lastTurretAngle, angle)) >= TurretAngleChangeToleranceDegrees)
            {
                MelonLogger.Msg($"[FCS DIAG AZ] TURRET MOTION {GetTurretState()}");
                _lastTurretAngle = angle;
                _haveTurretAngle = true;
            }
        }
    }

    public void Reset()
    {
        _snapshots.Clear();
        _candidates.Clear();
        _turretController = null;
        _bound = false;
        _haveTurretAngle = false;
        _nextSampleTime = 0f;
        _nextTurretLogTime = 0f;
    }

    private string GetTurretState()
    {
        if (_turretController == null)
            return "turret=<unbound>";

        return $"turretCurrent={_turretController.CurrentAngle:F2}° " +
               $"desired={_turretController.DesiredRotation:F2}° velocity={_turretController.rotationVelocity:F3}";
    }

    private static bool IsCandidate(Transform transform)
    {
        var current = transform;
        for (var depth = 0; current != null && depth < 4; depth++, current = current.parent)
        {
            var name = current.name ?? string.Empty;
            foreach (var hint in NameHints)
            {
                if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        return false;
    }

    private static CandidateSnapshot Capture(Transform transform)
    {
        return new CandidateSnapshot
        {
            LocalPosition = transform.localPosition,
            LocalRotation = transform.localRotation,
            LocalEuler = transform.localEulerAngles
        };
    }

    private static string GetComponentNames(Transform transform)
    {
        try
        {
            var components = transform.gameObject.GetComponents<Component>();
            return string.Join(",", components.Where(c => c != null).Select(c => c.GetType().Name));
        }
        catch (Exception ex)
        {
            return "component-scan-failed:" + ex.GetType().Name;
        }
    }

    private static string BuildPath(Transform transform)
    {
        var names = new List<string>();
        var current = transform;
        while (current != null && names.Count < 8)
        {
            names.Add(current.name + "#" + current.GetInstanceID());
            current = current.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private sealed class CandidateSnapshot
    {
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 LocalEuler;
    }
}
