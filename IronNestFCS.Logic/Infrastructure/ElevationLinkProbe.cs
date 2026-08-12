using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Infrastructure;

/// <summary>
/// Temporary diagnostic probe for the game's twin-gun elevation linkage device.
/// Read-only by design: it never writes controller, slider, or transform state.
/// Remove after the physical linkage contract has been identified and verified.
/// </summary>
internal sealed class ElevationLinkProbe
{
    private const float SampleIntervalSeconds = 0.10f;
    private const float ElevationChangeTolerance = 0.02f;
    private const float TransformPositionTolerance = 0.001f;
    private const float TransformAngleTolerance = 0.05f;
    private const float TransformScaleTolerance = 0.001f;

    private TurretController? _turretController;
    private GunController? _leftGun;
    private GunController? _rightGun;
    private LinearSliderInteractable? _leftSlider;
    private LinearSliderInteractable? _rightSlider;

    private readonly Dictionary<string, Transform> _trackedTransforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransformState> _lastTransformStates = new(StringComparer.Ordinal);

    private ElevationState? _lastElevationState;
    private float _nextSampleAt;
    private float _nextBindAttemptAt;
    private bool _bound;

    public void TryBind()
    {
        try
        {
            _turretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
            _leftGun = GameObject.Find("GunLeft")?.GetComponent<GunController>();
            _rightGun = GameObject.Find("GunRight")?.GetComponent<GunController>();

            var baseplate = GameObject.Find(".Elevation Lever Baseplate")?.transform;
            _leftSlider = baseplate?.FindChild(".Elevation Lever Left")?.GetComponent<LinearSliderInteractable>();
            _rightSlider = baseplate?.FindChild(".Elevation Lever Right")?.GetComponent<LinearSliderInteractable>();

            _bound = _turretController != null
                     && _leftGun != null
                     && _rightGun != null
                     && _leftSlider != null
                     && _rightSlider != null;

            _trackedTransforms.Clear();
            _lastTransformStates.Clear();

            if (baseplate != null)
                CollectTransforms(baseplate, baseplate.name, includeAllDescendants: true);

            var turretRoot = GameObject.Find("TurretSystem")?.transform;
            if (turretRoot != null)
                CollectTransforms(turretRoot, turretRoot.name, includeAllDescendants: false);

            _lastElevationState = null;
            _nextSampleAt = 0f;

            MelonLogger.Msg(
                $"[FCS ElevationLinkProbe] bind {(_bound ? "success" : "partial")}; " +
                $"turret={(_turretController != null)}, leftGun={(_leftGun != null)}, rightGun={(_rightGun != null)}, " +
                $"leftSlider={(_leftSlider != null)}, rightSlider={(_rightSlider != null)}, " +
                $"trackedTransforms={_trackedTransforms.Count}");

            DumpTrackedHierarchy();
            EmitElevationState(force: true);
            CaptureTransformBaselines();
        }
        catch (Exception ex)
        {
            _bound = false;
            MelonLogger.Warning($"[FCS ElevationLinkProbe] bind failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Tick()
    {
        var now = FcsRuntimeClock.Now;
        if (now < _nextSampleAt)
            return;
        _nextSampleAt = now + SampleIntervalSeconds;

        if (!_bound)
        {
            if (now >= _nextBindAttemptAt)
            {
                _nextBindAttemptAt = now + 1f;
                TryBind();
            }
            return;
        }

        try
        {
            EmitElevationState(force: false);
            EmitTransformChanges();
        }
        catch (Exception ex)
        {
            _bound = false;
            MelonLogger.Warning($"[FCS ElevationLinkProbe] sample failed; rebinding: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Reset()
    {
        _turretController = null;
        _leftGun = null;
        _rightGun = null;
        _leftSlider = null;
        _rightSlider = null;
        _trackedTransforms.Clear();
        _lastTransformStates.Clear();
        _lastElevationState = null;
        _nextSampleAt = 0f;
        _nextBindAttemptAt = 0f;
        _bound = false;
    }

    private void EmitElevationState(bool force)
    {
        if (_turretController == null || _leftGun == null || _rightGun == null
            || _leftSlider == null || _rightSlider == null)
        {
            _bound = false;
            return;
        }

        var current = new ElevationState(
            _turretController.driveGunElevationsFromController,
            _leftSlider.Value,
            _leftGun.CurrentElevation,
            _leftGun.DesiredElevationAngle,
            _leftGun.elevationChangeVelocity,
            _rightSlider.Value,
            _rightGun.CurrentElevation,
            _rightGun.DesiredElevationAngle,
            _rightGun.elevationChangeVelocity);

        if (!force && _lastElevationState.HasValue && !current.HasMeaningfulChangeFrom(_lastElevationState.Value))
            return;

        _lastElevationState = current;
        MelonLogger.Msg(
            $"[FCS ElevationLinkProbe] state " +
            $"driveFromController={current.DriveFromController} | " +
            $"L slider={current.LeftSlider:F3} current={current.LeftCurrent:F3} desired={current.LeftDesired:F3} vel={current.LeftVelocity:F3} | " +
            $"R slider={current.RightSlider:F3} current={current.RightCurrent:F3} desired={current.RightDesired:F3} vel={current.RightVelocity:F3} | " +
            $"delta current={Mathf.DeltaAngle(current.LeftCurrent, current.RightCurrent):F3} desired={Mathf.DeltaAngle(current.LeftDesired, current.RightDesired):F3}");
    }

    private void CollectTransforms(Transform root, string path, bool includeAllDescendants)
    {
        var shouldTrack = includeAllDescendants || NameLooksRelevant(root.name);
        if (shouldTrack && !_trackedTransforms.ContainsKey(path))
            _trackedTransforms[path] = root;

        for (var i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            CollectTransforms(child, path + "/" + child.name, includeAllDescendants);
        }
    }

    private static bool NameLooksRelevant(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return ContainsAny(name,
            "elev", "lever", "link", "coupl", "sync", "connect", "lock", "associate", "pair");
    }

    private void DumpTrackedHierarchy()
    {
        foreach (var pair in _trackedTransforms.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var transform = pair.Value;
            if (transform == null)
                continue;

            MelonLogger.Msg(
                $"[FCS ElevationLinkProbe] object path={pair.Key} active={transform.gameObject.activeSelf} " +
                $"components={DescribeComponents(transform.gameObject)}");
        }
    }

    private static string DescribeComponents(GameObject gameObject)
    {
        try
        {
            var components = gameObject.GetComponents<Component>();
            if (components == null || components.Length == 0)
                return "-";

            var names = new List<string>(components.Length);
            foreach (var component in components)
            {
                if (component == null)
                    continue;
                names.Add(component.GetType().Name);
            }
            return names.Count == 0 ? "-" : string.Join(",", names);
        }
        catch (Exception ex)
        {
            return "read-failed:" + ex.GetType().Name;
        }
    }

    private void CaptureTransformBaselines()
    {
        foreach (var pair in _trackedTransforms)
        {
            var transform = pair.Value;
            if (transform == null)
                continue;
            _lastTransformStates[pair.Key] = TransformState.Read(transform);
        }
    }

    private void EmitTransformChanges()
    {
        foreach (var pair in _trackedTransforms)
        {
            var transform = pair.Value;
            if (transform == null)
                continue;

            var current = TransformState.Read(transform);
            if (!_lastTransformStates.TryGetValue(pair.Key, out var previous))
            {
                _lastTransformStates[pair.Key] = current;
                continue;
            }

            if (!current.HasMeaningfulChangeFrom(previous))
                continue;

            _lastTransformStates[pair.Key] = current;
            MelonLogger.Msg(
                $"[FCS ElevationLinkProbe] transform path={pair.Key} " +
                $"active={previous.Active}->{current.Active} " +
                $"pos={Format(previous.Position)}->{Format(current.Position)} " +
                $"rot={Format(previous.EulerAngles)}->{Format(current.EulerAngles)} " +
                $"scale={Format(previous.Scale)}->{Format(current.Scale)}");
        }
    }

    private static string Format(Vector3 value) => $"({value.x:F3},{value.y:F3},{value.z:F3})";

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private readonly record struct ElevationState(
        bool DriveFromController,
        float LeftSlider,
        float LeftCurrent,
        float LeftDesired,
        float LeftVelocity,
        float RightSlider,
        float RightCurrent,
        float RightDesired,
        float RightVelocity)
    {
        public bool HasMeaningfulChangeFrom(ElevationState previous)
        {
            return DriveFromController != previous.DriveFromController
                   || Changed(LeftSlider, previous.LeftSlider)
                   || Changed(LeftCurrent, previous.LeftCurrent)
                   || Changed(LeftDesired, previous.LeftDesired)
                   || Changed(LeftVelocity, previous.LeftVelocity)
                   || Changed(RightSlider, previous.RightSlider)
                   || Changed(RightCurrent, previous.RightCurrent)
                   || Changed(RightDesired, previous.RightDesired)
                   || Changed(RightVelocity, previous.RightVelocity);
        }

        private static bool Changed(float value, float previous) =>
            Mathf.Abs(value - previous) >= ElevationChangeTolerance;
    }

    private readonly record struct TransformState(
        bool Active,
        Vector3 Position,
        Vector3 EulerAngles,
        Vector3 Scale)
    {
        public static TransformState Read(Transform transform) => new(
            transform.gameObject.activeSelf,
            transform.localPosition,
            transform.localEulerAngles,
            transform.localScale);

        public bool HasMeaningfulChangeFrom(TransformState previous)
        {
            return Active != previous.Active
                   || Vector3.Distance(Position, previous.Position) >= TransformPositionTolerance
                   || Mathf.Abs(Mathf.DeltaAngle(EulerAngles.x, previous.EulerAngles.x)) >= TransformAngleTolerance
                   || Mathf.Abs(Mathf.DeltaAngle(EulerAngles.y, previous.EulerAngles.y)) >= TransformAngleTolerance
                   || Mathf.Abs(Mathf.DeltaAngle(EulerAngles.z, previous.EulerAngles.z)) >= TransformAngleTolerance
                   || Vector3.Distance(Scale, previous.Scale) >= TransformScaleTolerance;
        }
    }
}
