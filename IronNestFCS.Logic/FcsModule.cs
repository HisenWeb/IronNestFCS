using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Infrastructure;

namespace IronNestFCS.Logic;

/// <summary>
/// Logic 程序集的入口，由 Host 反射实例化（类型全名见 Host 的 LogicTypeName）。
/// 负责组装领域逻辑 <see cref="FSC"/>、点击检测 <see cref="ClickRaycaster"/> 与 UI <see cref="FcsWindow"/>，
/// 并把 Host 的生命周期回调转发下去。本身不含具体火控逻辑或绘制代码。
/// </summary>
public class FcsModule : IFcsModule
{
    private readonly FSC fcs = new();
    private FcsWindow? window;

    public bool Initialize()
    {
        // Start file diagnostics before binding so bind failures and all existing MelonLogger probes are captured.
        // The run directory is derived from the game process start time, therefore F9 hot reloads append to the
        // same files while receiving a fresh logic-session marker.
        FcsDiagnosticLog.Start(BuildDiagnosticContext);

        window = new FcsWindow(fcs);
        PhysicalStateProbe.Reset();
        TriggerConsoleProbe.Reset();
        AimingSpeedProbe.Reset();
        bool bound = fcs.TryBind();

        var leftPhysical = bound ? SafePhysicalSummary("Left") : "unbound";
        var rightPhysical = bound ? SafePhysicalSummary("Right") : "unbound";
        FcsDiagnosticLog.MarkBindResult(
            bound,
            fcs.FirePriority.Generation,
            leftPhysical,
            rightPhysical);

        if (bound)
        {
            // Read-only baseline for the full reload/fire state timeline.
            PhysicalStateProbe.LogCurrentState();
            // Read-only physical-state probe for the five review switches + two arming levers.
            TriggerConsoleProbe.BindAndLog();
            // Read-only probe for real physical azimuth/elevation slew rates. This never affects arbitration.
            AimingSpeedProbe.BindAndLog();
        }
        // 返回绑定结果仅用于 Host 日志；窗口实例已建好，未绑定时会显示提示，
        // 进入场景后按 F9 重载即可绑定。
        return bound;
    }

    public void Update()
    {
        fcs.Update();

        // Probes are intentionally outside FSC's focus gate. The game can keep mechanisms/animations running
        // while unfocused, and those physical transitions are exactly what the diagnostics need to capture.
        if (fcs.IsBound)
        {
            PhysicalStateProbe.Tick();
            TriggerConsoleProbe.Tick();
            AimingSpeedProbe.Tick();
        }
    }

    public void OnGui()
    {
        window?.OnGui();
    }

    public void Shutdown()
    {
        try {
            // Keep the diagnostic callback attached through Dispose so cancellation/release/F9 cleanup is present
            // in the same session log as the operations that led to it.
            fcs.Dispose();
            PhysicalStateProbe.Reset();
            TriggerConsoleProbe.Reset();
            AimingSpeedProbe.Reset();
            window = null;
        }
        finally {
            FcsDiagnosticLog.Stop("logic shutdown/reload");
        }
    }

    private string BuildDiagnosticContext()
    {
        static string TaskContext(ArtilleryTask? task)
        {
            return task == null ? "-" : $"T{task.targetId}:{task.progress}";
        }

        return
            $"gen={fcs.FirePriority.Generation} | " +
            $"L={TaskContext(fcs.LeftTask)} | R={TaskContext(fcs.RightTask)}";
    }

    private static string SafePhysicalSummary(string side)
    {
        try { return GunPhysicalState.Read(side).Summary(); }
        catch (Exception ex) { return $"read-failed:{ex.GetType().Name}"; }
    }
}
