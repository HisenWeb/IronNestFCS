using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// Temporary targeted diagnostic for the fast azimuth lever.
/// It never writes to game state. On bind it dumps the native IL2CPP component/API
/// surface under Aiming Console/Locking Lever Rotation/Lever, then logs lever motion
/// plus low-frequency turret response while the lever is held.
/// </summary>
internal sealed class AzimuthControlDiagnosticProbe
{
    private const float SampleIntervalSeconds = 0.10f;
    private const float HoldLogIntervalSeconds = 0.25f;
    private const float LeverChangeToleranceDegrees = 0.20f;
    private const float ActiveLeverThresholdDegrees = 0.50f;
    private const float ActiveVelocityThreshold = 0.05f;
    private const int ApiChildDepth = 3;

    private Transform? _fastLever;
    private TurretController? _turretController;
    private float _nextSampleTime;
    private float _nextHoldLogTime;
    private float _lastLeverY;
    private bool _haveLeverY;
    private bool _bound;

    public void TryBind()
    {
        Reset();

        _turretController = GameObject.Find("TurretSystem")?.GetComponent<TurretController>();
        _fastLever = FindFastLever();

        if (_fastLever == null)
        {
            MelonLogger.Warning("[FCS DIAG AZ API] fast lever not found: Aiming Console/Locking Lever Rotation/Lever");
            return;
        }

        _lastLeverY = NormalizeSignedAngle(_fastLever.localEulerAngles.y);
        _haveLeverY = true;
        _nextSampleTime = Time.realtimeSinceStartup + SampleIntervalSeconds;
        _nextHoldLogTime = Time.realtimeSinceStartup + HoldLogIntervalSeconds;
        _bound = true;

        MelonLogger.Msg(
            $"[FCS DIAG AZ API] fast lever found obj={_fastLever.name}#{_fastLever.GetInstanceID()} " +
            $"path={BuildPath(_fastLever)} initialY={_lastLeverY:F2}° {GetTurretState()}");

        DumpApiTree(_fastLever, ApiChildDepth);

        MelonLogger.Msg(
            "[FCS DIAG AZ API] TEST: for each direction, hold FAST azimuth lever at full deflection until speed stabilizes, " +
            "then move toward center until the slowest sustained non-zero speed stabilizes, then return to center and stop. " +
            "Repeat in the opposite direction. No automatic control is applied by this probe.");
    }

    public void Update()
    {
        if (!_bound || _fastLever == null)
            return;

        var now = Time.realtimeSinceStartup;
        if (now < _nextSampleTime)
            return;
        _nextSampleTime = now + SampleIntervalSeconds;

        var leverY = NormalizeSignedAngle(_fastLever.localEulerAngles.y);
        if (!_haveLeverY || Mathf.Abs(Mathf.DeltaAngle(_lastLeverY, leverY)) >= LeverChangeToleranceDegrees)
        {
            MelonLogger.Msg(
                $"[FCS DIAG AZ API] LEVER y={leverY:F2}° delta={Mathf.DeltaAngle(_lastLeverY, leverY):F2}° " +
                $"{GetTurretState()}");
            _lastLeverY = leverY;
            _haveLeverY = true;
        }

        if (now >= _nextHoldLogTime)
        {
            _nextHoldLogTime = now + HoldLogIntervalSeconds;

            var movingLever = Mathf.Abs(leverY) >= ActiveLeverThresholdDegrees;
            var movingTurret = _turretController != null &&
                               Mathf.Abs(_turretController.rotationVelocity) >= ActiveVelocityThreshold;
            if (movingLever || movingTurret)
            {
                MelonLogger.Msg(
                    $"[FCS DIAG AZ API] HOLD y={leverY:F2}° {GetTurretState()}");
            }
        }
    }

    public void Reset()
    {
        _fastLever = null;
        _turretController = null;
        _bound = false;
        _haveLeverY = false;
        _nextSampleTime = 0f;
        _nextHoldLogTime = 0f;
        _lastLeverY = 0f;
    }

    private static Transform? FindFastLever()
    {
        foreach (var transform in UnityEngine.Object.FindObjectsOfType<Transform>(true))
        {
            if (transform == null || !string.Equals(transform.name, "Lever", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = BuildPath(transform);
            if (path.IndexOf("Aiming Console", StringComparison.OrdinalIgnoreCase) >= 0 &&
                path.IndexOf("Locking Lever Rotation", StringComparison.OrdinalIgnoreCase) >= 0)
                return transform;
        }

        return null;
    }

    private static void DumpApiTree(Transform root, int maxDepth)
    {
        DumpTransformApi(root);
        DumpChildrenApi(root, 1, maxDepth);
    }

    private static void DumpChildrenApi(Transform parent, int depth, int maxDepth)
    {
        if (depth > maxDepth)
            return;

        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child == null)
                continue;

            DumpTransformApi(child);
            DumpChildrenApi(child, depth + 1, maxDepth);
        }
    }

    private static void DumpTransformApi(Transform transform)
    {
        try
        {
            foreach (var component in transform.gameObject.GetComponents<Component>())
            {
                if (component == null)
                    continue;

                var type = component.GetIl2CppType();
                var fullName = type.FullName ?? type.Name;
                if (fullName.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                    fullName.StartsWith("Il2CppSystem.", StringComparison.Ordinal))
                    continue;

                MelonLogger.Msg(
                    $"[FCS DIAG AZ API] COMPONENT path={BuildPath(transform)} type={fullName}");

                foreach (var field in type.GetFields())
                {
                    if (field?.DeclaringType == null || field.DeclaringType.Name != type.Name)
                        continue;
                    MelonLogger.Msg($"[FCS DIAG AZ API]   FIELD {field}");
                }

                foreach (var property in type.GetProperties())
                {
                    if (property?.DeclaringType == null || property.DeclaringType.Name != type.Name)
                        continue;
                    MelonLogger.Msg($"[FCS DIAG AZ API]   PROPERTY {property}");
                }

                foreach (var method in type.GetMethods())
                {
                    if (method?.DeclaringType == null || method.DeclaringType.Name != type.Name)
                        continue;
                    MelonLogger.Msg($"[FCS DIAG AZ API]   METHOD {method}");
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning(
                $"[FCS DIAG AZ API] component/API dump failed path={BuildPath(transform)}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string GetTurretState()
    {
        if (_turretController == null)
            return "turret=<unbound>";

        return $"turretCurrent={_turretController.CurrentAngle:F2}° " +
               $"desired={_turretController.DesiredRotation:F2}° velocity={_turretController.rotationVelocity:F3}";
    }

    private static float NormalizeSignedAngle(float angle)
    {
        return angle > 180f ? angle - 360f : angle;
    }

    private static string BuildPath(Transform transform)
    {
        var names = new List<string>();
        var current = transform;
        while (current != null && names.Count < 10)
        {
            names.Add(current.name + "#" + current.GetInstanceID());
            current = current.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }
}
