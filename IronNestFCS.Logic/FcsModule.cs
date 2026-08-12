using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Infrastructure;

namespace IronNestFCS.Logic;

/// <summary>
/// Reloadable TaskSystem entrypoint. Persistent loading is injected by Host and is never owned/disposed here.
/// </summary>
public class FcsModule : IFcsModule
{
    private FSC? _fcs;
    private FcsWindow? _window;
    private readonly ElevationLinkProbe _elevationLinkProbe = new();

    public bool Initialize(IFcsHostServices hostServices)
    {
        FcsDiagnosticLog.Start(BuildDiagnosticContext);

        _fcs = new FSC(hostServices);
        _window = new FcsWindow(_fcs);

        var bound = _fcs.TryBind();
        if (bound)
            _elevationLinkProbe.TryBind();
        else
            _elevationLinkProbe.Reset();

        var leftPhysical = bound ? SafePhysicalSummary("Left") : "unbound";
        var rightPhysical = bound ? SafePhysicalSummary("Right") : "unbound";
        FcsDiagnosticLog.MarkBindResult(bound, _fcs.FirePriority.Generation, leftPhysical, rightPhysical);

        return bound;
    }

    public void Update()
    {
        var fcs = _fcs;
        if (fcs == null)
            return;

        fcs.Update();
        _elevationLinkProbe.Tick();
    }

    public void OnGui() => _window?.OnGui();

    public void Shutdown()
    {
        try
        {
            _elevationLinkProbe.Reset();
            _fcs?.Dispose();
            _window = null;
            _fcs = null;
        }
        finally
        {
            FcsDiagnosticLog.Stop("logic shutdown/reload");
        }
    }

    private string BuildDiagnosticContext()
    {
        var fcs = _fcs;
        if (fcs == null)
            return "gen=- | L=- | R=-";

        static string TaskContext(ArtilleryTask? task) => task == null ? "-" : $"T{task.targetId}:{task.progress}";
        return $"gen={fcs.FirePriority.Generation} | L={TaskContext(fcs.LeftTask)} | R={TaskContext(fcs.RightTask)}";
    }

    private static string SafePhysicalSummary(string side)
    {
        try { return GunPhysicalState.Read(side).Summary(); }
        catch (Exception ex) { return $"read-failed:{ex.GetType().Name}"; }
    }
}
