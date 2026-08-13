using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

internal sealed class RequisitionProbe : IDisposable
{
    private const float SampleIntervalSeconds = 0.10f;

    private readonly Transform _root;
    private float _nextSampleAt;
    private string _lastCardFingerprint = "";
    private string _lastControlFingerprint = "";
    private int _cardChangeSerial;
    private int _controlChangeSerial;

    public RequisitionProbe(Transform root)
    {
        _root = root;
        MelonLogger.Msg($"[FCS RequisitionProbe] bound root={BuildPath(root)} t={Time.unscaledTime:F3}");
        DumpCandidateHierarchy();
        Sample(true);
    }

    public void Dispose()
    {
    }

    public void Tick()
    {
        if (_root == null || Time.unscaledTime < _nextSampleAt)
            return;

        _nextSampleAt = Time.unscaledTime + SampleIntervalSeconds;
        Sample(false);
    }

    private void Sample(bool force)
    {
        SampleControls(force);
        SampleCards(force);
    }

    private void SampleCards(bool force)
    {
        PunchcardRuntime[] cards;
        try { cards = _root.GetComponentsInChildren<PunchcardRuntime>(true); }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS RequisitionProbe] card scan failed: {ex.Message}");
            return;
        }

        var rows = cards
            .Select(card => new CardRow(
                card.GetInstanceID(),
                BuildPath(card.transform),
                SafeDefinitionId(card),
                card.gameObject.activeInHierarchy,
                card.transform.position))
            .OrderBy(row => row.Path, StringComparer.Ordinal)
            .ThenBy(row => row.InstanceId)
            .ToArray();

        var fingerprint = string.Join("|", rows.Select(row =>
            $"{row.InstanceId}:{row.Path}:{row.DefinitionId}:{row.Active}"));

        if (!force && fingerprint == _lastCardFingerprint)
            return;

        _lastCardFingerprint = fingerprint;
        _cardChangeSerial++;
        MelonLogger.Msg($"[FCS RequisitionProbe] CARDS #{_cardChangeSerial} t={Time.unscaledTime:F3} count={rows.Length}");
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            MelonLogger.Msg(
                $"[FCS RequisitionProbe]   card[{i}] instance={row.InstanceId} def={row.DefinitionId} " +
                $"active={row.Active} path={row.Path} pos=({row.Position.x:F3},{row.Position.y:F3},{row.Position.z:F3})");
        }
    }

    private void SampleControls(bool force)
    {
        LookAtTarget[] controls;
        try { controls = _root.GetComponentsInChildren<LookAtTarget>(true); }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS RequisitionProbe] control scan failed: {ex.Message}");
            return;
        }

        var rows = controls
            .Select(control => new ControlRow(
                control.GetInstanceID(),
                BuildPath(control.transform),
                control.isActive,
                control.nextAllowedClickTime,
                control.transform.localEulerAngles))
            .OrderBy(row => row.Path, StringComparer.Ordinal)
            .ThenBy(row => row.InstanceId)
            .ToArray();

        var fingerprint = string.Join("|", rows.Select(row =>
            $"{row.InstanceId}:{row.Path}:{row.IsActive}:{row.NextAllowedClickTime:F3}:" +
            $"{row.LocalEuler.x:F1},{row.LocalEuler.y:F1},{row.LocalEuler.z:F1}"));

        if (!force && fingerprint == _lastControlFingerprint)
            return;

        _lastControlFingerprint = fingerprint;
        _controlChangeSerial++;
        MelonLogger.Msg($"[FCS RequisitionProbe] CONTROLS #{_controlChangeSerial} t={Time.unscaledTime:F3} count={rows.Length}");
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            MelonLogger.Msg(
                $"[FCS RequisitionProbe]   control[{i}] instance={row.InstanceId} active={row.IsActive} " +
                $"nextClick={row.NextAllowedClickTime:F3} localEuler=({row.LocalEuler.x:F1},{row.LocalEuler.y:F1},{row.LocalEuler.z:F1}) " +
                $"path={row.Path}");
        }
    }

    private void DumpCandidateHierarchy()
    {
        Transform[] nodes;
        try { nodes = _root.GetComponentsInChildren<Transform>(true); }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS RequisitionProbe] hierarchy scan failed: {ex.Message}");
            return;
        }

        MelonLogger.Msg($"[FCS RequisitionProbe] HIERARCHY candidates begin nodes={nodes.Length}");
        foreach (var node in nodes)
        {
            string[] componentNames;
            try
            {
                componentNames = node.GetComponents<Component>()
                    .Where(component => component != null)
                    .Select(component => component.GetType().FullName ?? component.GetType().Name)
                    .ToArray();
            }
            catch
            {
                componentNames = Array.Empty<string>();
            }

            var searchable = (node.name + " " + string.Join(" ", componentNames)).ToLowerInvariant();
            var relevant = searchable.Contains("lever")
                           || searchable.Contains("refresh")
                           || searchable.Contains("interact")
                           || searchable.Contains("handle")
                           || searchable.Contains("switch")
                           || searchable.Contains("button")
                           || searchable.Contains("punch")
                           || searchable.Contains("requisition")
                           || node.GetComponent<LookAtTarget>() != null;

            if (relevant)
                MelonLogger.Msg($"[FCS RequisitionProbe]   node path={BuildPath(node)} components=[{string.Join(", ", componentNames)}]");
        }
        MelonLogger.Msg("[FCS RequisitionProbe] HIERARCHY candidates end");
    }

    private static string SafeDefinitionId(PunchcardRuntime card)
    {
        try
        {
            var definition = card.CurrentDefinition;
            return definition == null || string.IsNullOrWhiteSpace(definition.ID) ? "<null>" : definition.ID;
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}>";
        }
    }

    private string BuildPath(Transform node)
    {
        var names = new List<string>();
        var current = node;
        while (current != null)
        {
            names.Add(current.name);
            if (current == _root)
                break;
            current = current.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private sealed record CardRow(int InstanceId, string Path, string DefinitionId, bool Active, Vector3 Position);
    private sealed record ControlRow(int InstanceId, string Path, bool IsActive, float NextAllowedClickTime, Vector3 LocalEuler);
}
