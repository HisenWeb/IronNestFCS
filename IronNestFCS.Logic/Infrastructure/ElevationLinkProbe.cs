using System.Globalization;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Infrastructure;

/// <summary>
/// Temporary, read-only probe for the game's twin-gun elevation linkage mechanism.
/// Phase 2 narrows observation to the physical linkage controls so we can identify
/// an authoritative Linked/Soloed state without inferring it from gun angles.
/// </summary>
internal sealed class ElevationLinkProbe
{
    private const float SampleIntervalSeconds = 0.10f;
    private const float TransformPositionTolerance = 0.0005f;
    private const float TransformAngleTolerance = 0.02f;
    private const int FieldAttributeStatic = 0x10;
    private const int MaxMetadataMembersPerClass = 96;

    private readonly List<ProbeTarget> _targets = new();
    private readonly List<ObservedField> _observedFields = new();
    private readonly Dictionary<string, string> _lastFieldValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransformState> _lastTransformStates = new(StringComparer.Ordinal);

    private float _nextSampleAt;
    private float _nextBindAttemptAt;
    private bool _bound;
    private string _lastBindSummary = "";

    public void TryBind()
    {
        try
        {
            _targets.Clear();
            _observedFields.Clear();
            _lastFieldValues.Clear();
            _lastTransformStates.Clear();

            var baseplate = GameObject.Find(".Elevation Lever Baseplate")?.transform;
            var rightLever = baseplate?.FindChild(".Elevation Lever Right");
            var lockingBolt = rightLever?.FindChild(".Elevation Lever Locking Bolt");
            var turretRoot = GameObject.Find("TurretSystem")?.transform;

            AddTarget("TurretSystem/Elevation Linking Button", turretRoot?.FindChild("Elevation Linking Button"));
            AddTarget(
                ".Elevation Lever Baseplate/.Elevation Lever Right/.Elevation Lever Locking Bolt",
                lockingBolt);
            AddTarget(
                ".Elevation Lever Baseplate/.Elevation Lever Right/.Elevation Lever Locking Bolt/LINKED",
                lockingBolt?.FindChild("LINKED"));
            AddTarget(
                ".Elevation Lever Baseplate/.Elevation Lever Right/.Elevation Lever Locking Bolt/SOLOED",
                lockingBolt?.FindChild("SOLOED"));

            _bound = _targets.Count == 4;
            _nextSampleAt = 0f;

            var summary = $"targets={_targets.Count}/4";
            if (!_bound)
            {
                if (!string.Equals(_lastBindSummary, summary, StringComparison.Ordinal))
                {
                    _lastBindSummary = summary;
                    MelonLogger.Warning($"[FCS ElevationLinkProbe] phase2 bind partial; {summary}");
                }
                return;
            }

            _lastBindSummary = summary;
            MelonLogger.Msg($"[FCS ElevationLinkProbe] phase2 bind success; {summary}");

            foreach (var target in _targets)
            {
                DumpTargetMetadata(target);
                _lastTransformStates[target.Path] = TransformState.Read(target.Transform);
            }

            MelonLogger.Msg(
                $"[FCS ElevationLinkProbe] phase2 watching primitive state fields={_observedFields.Count}; " +
                "property getters are metadata-only and are never invoked");
        }
        catch (Exception ex)
        {
            _bound = false;
            MelonLogger.Warning(
                $"[FCS ElevationLinkProbe] phase2 bind failed: {ex.GetType().Name}: {ex.Message}");
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
            EmitFieldChanges();
            EmitTargetTransformChanges();
        }
        catch (Exception ex)
        {
            _bound = false;
            MelonLogger.Warning(
                $"[FCS ElevationLinkProbe] phase2 sample failed; rebinding: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Reset()
    {
        _targets.Clear();
        _observedFields.Clear();
        _lastFieldValues.Clear();
        _lastTransformStates.Clear();
        _nextSampleAt = 0f;
        _nextBindAttemptAt = 0f;
        _bound = false;
        _lastBindSummary = "";
    }

    private void AddTarget(string path, Transform? transform)
    {
        if (transform != null)
            _targets.Add(new ProbeTarget(path, transform));
    }

    private void DumpTargetMetadata(ProbeTarget target)
    {
        MelonLogger.Msg(
            $"[FCS ElevationLinkProbe] target path={target.Path} activeSelf={target.Transform.gameObject.activeSelf}");

        try
        {
            var components = target.Transform.gameObject.GetComponents<Component>();
            for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
            {
                var component = components[componentIndex];
                if (component == null)
                    continue;

                try
                {
                    DumpComponentMetadata(target, componentIndex, component);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[FCS ElevationLinkProbe] component probe failed path={target.Path} index={componentIndex}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning(
                $"[FCS ElevationLinkProbe] components read failed path={target.Path}: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DumpComponentMetadata(ProbeTarget target, int componentIndex, Component component)
    {
        var objectPtr = IL2CPP.Il2CppObjectBaseToPtr(component);
        if (objectPtr == IntPtr.Zero)
            return;

        var runtimeClass = IL2CPP.il2cpp_object_get_class(objectPtr);
        if (runtimeClass == IntPtr.Zero)
            return;

        var runtimeClassName = FullClassName(runtimeClass);
        MelonLogger.Msg(
            $"[FCS ElevationLinkProbe] component path={target.Path} index={componentIndex} " +
            $"il2cpp={runtimeClassName} managed={component.GetType().FullName ?? component.GetType().Name}");

        var currentClass = runtimeClass;
        for (var depth = 0; currentClass != IntPtr.Zero && depth < 6; depth++)
        {
            var classNamespace = IL2CPP.il2cpp_class_get_namespace_(currentClass) ?? "";
            var className = FullClassName(currentClass);
            if (IsFrameworkClass(classNamespace, className))
                break;

            DumpClassFields(target, componentIndex, component, currentClass, className);
            DumpClassProperties(target, componentIndex, currentClass, className);
            DumpInterestingMethods(target, componentIndex, currentClass, className);
            currentClass = IL2CPP.il2cpp_class_get_parent(currentClass);
        }
    }

    private void DumpClassFields(
        ProbeTarget target,
        int componentIndex,
        Component component,
        IntPtr klass,
        string className)
    {
        var iter = IntPtr.Zero;
        var count = 0;
        IntPtr field;

        while (count++ < MaxMetadataMembersPerClass
               && (field = IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero)
        {
            var fieldName = IL2CPP.il2cpp_field_get_name_(field) ?? "?";
            var fieldType = IL2CPP.il2cpp_field_get_type(field);
            var typeName = fieldType == IntPtr.Zero
                ? "?"
                : IL2CPP.il2cpp_type_get_name_(fieldType) ?? "?";
            var flags = IL2CPP.il2cpp_field_get_flags(field);
            var isStatic = (flags & FieldAttributeStatic) != 0;
            var isEnum = IsEnum(fieldType);
            var readable = !isStatic && IsReadablePrimitive(typeName, isEnum);
            var watch = readable && ShouldWatchChanges(fieldName, typeName, isEnum);
            var value = readable
                ? ReadFieldValue(component, field, fieldType, typeName, isEnum)
                : isStatic ? "<static-not-read>" : "<metadata-only>";

            var key = BuildFieldKey(target.Path, componentIndex, className, fieldName);
            MelonLogger.Msg(
                $"[FCS ElevationLinkProbe] field path={target.Path} component={componentIndex}:{className} " +
                $"name={fieldName} type={typeName} enum={isEnum} watch={watch} value={value}");

            if (!watch)
                continue;

            _lastFieldValues[key] = value;
            _observedFields.Add(new ObservedField(
                key,
                target.Path,
                componentIndex,
                className,
                fieldName,
                typeName,
                component,
                field,
                fieldType,
                isEnum));
        }
    }

    private static void DumpClassProperties(
        ProbeTarget target,
        int componentIndex,
        IntPtr klass,
        string className)
    {
        var iter = IntPtr.Zero;
        var count = 0;
        IntPtr property;

        while (count++ < MaxMetadataMembersPerClass
               && (property = IL2CPP.il2cpp_class_get_properties(klass, ref iter)) != IntPtr.Zero)
        {
            var name = IL2CPP.il2cpp_property_get_name_(property) ?? "?";
            if (!NameLooksStateRelevant(name))
                continue;

            MelonLogger.Msg(
                $"[FCS ElevationLinkProbe] property path={target.Path} component={componentIndex}:{className} " +
                $"name={name} getter={(IL2CPP.il2cpp_property_get_get_method(property) != IntPtr.Zero)} " +
                $"setter={(IL2CPP.il2cpp_property_get_set_method(property) != IntPtr.Zero)} metadataOnly=true");
        }
    }

    private static void DumpInterestingMethods(
        ProbeTarget target,
        int componentIndex,
        IntPtr klass,
        string className)
    {
        var iter = IntPtr.Zero;
        var count = 0;
        IntPtr method;

        while (count++ < MaxMetadataMembersPerClass
               && (method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
        {
            var name = IL2CPP.il2cpp_method_get_name_(method) ?? "?";
            if (!NameLooksActionRelevant(name))
                continue;

            MelonLogger.Msg(
                $"[FCS ElevationLinkProbe] method path={target.Path} component={componentIndex}:{className} " +
                $"name={name} argc={IL2CPP.il2cpp_method_get_param_count(method)} metadataOnly=true");
        }
    }

    private void EmitFieldChanges()
    {
        foreach (var observed in _observedFields)
        {
            if (observed.Component == null)
                continue;

            var current = ReadFieldValue(
                observed.Component,
                observed.Field,
                observed.FieldType,
                observed.TypeName,
                observed.IsEnum);

            if (!_lastFieldValues.TryGetValue(observed.Key, out var previous))
            {
                _lastFieldValues[observed.Key] = current;
                continue;
            }

            if (string.Equals(previous, current, StringComparison.Ordinal))
                continue;

            _lastFieldValues[observed.Key] = current;
            MelonLogger.Msg(
                $"[FCS ElevationLinkProbe] FIELD CHANGE path={observed.Path} " +
                $"component={observed.ComponentIndex}:{observed.ClassName} name={observed.FieldName} " +
                $"type={observed.TypeName} {previous}->{current}");
        }
    }

    private void EmitTargetTransformChanges()
    {
        foreach (var target in _targets)
        {
            if (target.Transform == null)
                continue;

            var current = TransformState.Read(target.Transform);
            if (!_lastTransformStates.TryGetValue(target.Path, out var previous))
            {
                _lastTransformStates[target.Path] = current;
                continue;
            }

            if (!current.HasMeaningfulChangeFrom(previous))
                continue;

            _lastTransformStates[target.Path] = current;
            MelonLogger.Msg(
                $"[FCS ElevationLinkProbe] TARGET CHANGE path={target.Path} " +
                $"active={previous.ActiveSelf}->{current.ActiveSelf} " +
                $"pos={Format(previous.Position)}->{Format(current.Position)} " +
                $"rot={Format(previous.EulerAngles)}->{Format(current.EulerAngles)}");
        }
    }

    private static string ReadFieldValue(
        Component component,
        IntPtr field,
        IntPtr fieldType,
        string typeName,
        bool isEnum)
    {
        try
        {
            var componentPtr = IL2CPP.Il2CppObjectBaseToPtr(component);
            if (componentPtr == IntPtr.Zero)
                return "<null-component>";

            var boxed = IL2CPP.il2cpp_field_get_value_object(field, componentPtr);
            if (boxed == IntPtr.Zero)
                return "null";

            if (string.Equals(typeName, "System.String", StringComparison.Ordinal))
            {
                var text = IL2CPP.Il2CppStringToManaged(boxed);
                return text == null ? "null" : $"\"{NormalizeInline(text)}\"";
            }

            var data = IL2CPP.il2cpp_object_unbox(boxed);
            if (data == IntPtr.Zero)
                return "<unbox-null>";

            var scalarType = typeName;
            if (isEnum)
            {
                var enumClass = IL2CPP.il2cpp_class_from_il2cpp_type(fieldType);
                var baseType = enumClass == IntPtr.Zero
                    ? IntPtr.Zero
                    : IL2CPP.il2cpp_class_enum_basetype(enumClass);
                scalarType = baseType == IntPtr.Zero
                    ? "System.Int32"
                    : IL2CPP.il2cpp_type_get_name_(baseType) ?? "System.Int32";
            }

            var scalar = ReadScalar(data, scalarType);
            return isEnum ? $"{typeName}({scalar})" : scalar;
        }
        catch (Exception ex)
        {
            return $"<read-failed:{ex.GetType().Name}>";
        }
    }

    private static string ReadScalar(IntPtr data, string typeName)
    {
        return typeName switch
        {
            "System.Boolean" => (Marshal.ReadByte(data) != 0).ToString(),
            "System.SByte" => unchecked((sbyte)Marshal.ReadByte(data)).ToString(CultureInfo.InvariantCulture),
            "System.Byte" => Marshal.ReadByte(data).ToString(CultureInfo.InvariantCulture),
            "System.Int16" => Marshal.ReadInt16(data).ToString(CultureInfo.InvariantCulture),
            "System.UInt16" => unchecked((ushort)Marshal.ReadInt16(data)).ToString(CultureInfo.InvariantCulture),
            "System.Int32" => Marshal.ReadInt32(data).ToString(CultureInfo.InvariantCulture),
            "System.UInt32" => unchecked((uint)Marshal.ReadInt32(data)).ToString(CultureInfo.InvariantCulture),
            "System.Int64" => Marshal.ReadInt64(data).ToString(CultureInfo.InvariantCulture),
            "System.UInt64" => unchecked((ulong)Marshal.ReadInt64(data)).ToString(CultureInfo.InvariantCulture),
            "System.Single" => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data)).ToString("R", CultureInfo.InvariantCulture),
            "System.Double" => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data)).ToString("R", CultureInfo.InvariantCulture),
            "System.Char" => ((char)unchecked((ushort)Marshal.ReadInt16(data))).ToString(),
            _ => "<unsupported-scalar>",
        };
    }

    private static bool IsReadablePrimitive(string typeName, bool isEnum)
    {
        if (isEnum)
            return true;

        return typeName is
            "System.Boolean" or
            "System.SByte" or
            "System.Byte" or
            "System.Int16" or
            "System.UInt16" or
            "System.Int32" or
            "System.UInt32" or
            "System.Int64" or
            "System.UInt64" or
            "System.Single" or
            "System.Double" or
            "System.Char" or
            "System.String";
    }

    private static bool IsEnum(IntPtr fieldType)
    {
        if (fieldType == IntPtr.Zero)
            return false;

        try
        {
            var klass = IL2CPP.il2cpp_class_from_il2cpp_type(fieldType);
            return klass != IntPtr.Zero && IL2CPP.il2cpp_class_is_enum(klass);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldWatchChanges(string fieldName, string typeName, bool isEnum)
    {
        if (isEnum || string.Equals(typeName, "System.Boolean", StringComparison.Ordinal))
            return true;
        return NameLooksStateRelevant(fieldName);
    }

    private static bool NameLooksStateRelevant(string value) => ContainsAny(
        value,
        "link", "solo", "lock", "state", "mode", "active", "enable", "value",
        "target", "current", "toggle", "press", "interact", "coupl", "sync", "associate");

    private static bool NameLooksActionRelevant(string value) => ContainsAny(
        value,
        "link", "solo", "lock", "toggle", "press", "click", "interact", "coupl", "sync", "associate");

    private static bool IsFrameworkClass(string classNamespace, string fullClassName)
    {
        return classNamespace.StartsWith("UnityEngine", StringComparison.Ordinal)
               || classNamespace.StartsWith("System", StringComparison.Ordinal)
               || classNamespace.StartsWith("Il2CppSystem", StringComparison.Ordinal)
               || classNamespace.StartsWith("TMPro", StringComparison.Ordinal)
               || classNamespace.StartsWith("FMOD", StringComparison.Ordinal)
               || fullClassName.StartsWith("UnityEngine.", StringComparison.Ordinal)
               || fullClassName.StartsWith("System.", StringComparison.Ordinal);
    }

    private static string FullClassName(IntPtr klass)
    {
        var name = IL2CPP.il2cpp_class_get_name_(klass) ?? "?";
        var ns = IL2CPP.il2cpp_class_get_namespace_(klass) ?? "";
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string BuildFieldKey(string path, int componentIndex, string className, string fieldName) =>
        $"{path}|{componentIndex}|{className}|{fieldName}";

    private static string NormalizeInline(string value) =>
        value.Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Format(Vector3 value) =>
        $"({value.x:F3},{value.y:F3},{value.z:F3})";

    private static bool ContainsAny(string value, params string[] needles)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private sealed record ProbeTarget(string Path, Transform Transform);

    private sealed record ObservedField(
        string Key,
        string Path,
        int ComponentIndex,
        string ClassName,
        string FieldName,
        string TypeName,
        Component Component,
        IntPtr Field,
        IntPtr FieldType,
        bool IsEnum);

    private readonly record struct TransformState(
        bool ActiveSelf,
        Vector3 Position,
        Vector3 EulerAngles)
    {
        public static TransformState Read(Transform transform) => new(
            transform.gameObject.activeSelf,
            transform.localPosition,
            transform.localEulerAngles);

        public bool HasMeaningfulChangeFrom(TransformState previous)
        {
            return ActiveSelf != previous.ActiveSelf
                   || Vector3.Distance(Position, previous.Position) >= TransformPositionTolerance
                   || Mathf.Abs(Mathf.DeltaAngle(EulerAngles.x, previous.EulerAngles.x)) >= TransformAngleTolerance
                   || Mathf.Abs(Mathf.DeltaAngle(EulerAngles.y, previous.EulerAngles.y)) >= TransformAngleTolerance
                   || Mathf.Abs(Mathf.DeltaAngle(EulerAngles.z, previous.EulerAngles.z)) >= TransformAngleTolerance;
        }
    }
}
