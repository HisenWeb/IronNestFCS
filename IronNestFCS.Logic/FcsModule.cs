using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;

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
        window = new FcsWindow(fcs);
        PhysicalStateProbe.Reset();
        bool bound = fcs.TryBind();
        if (bound)
        {
            // Read-only baseline for the full reload/fire state timeline.
            PhysicalStateProbe.LogCurrentState();
        }
        // 返回绑定结果仅用于 Host 日志；窗口实例已建好，未绑定时会显示提示，
        // 进入场景后按 F9 重载即可绑定。
        return bound;
    }

    public void Update()
    {
        fcs.Update();

        // Probe is intentionally outside FSC's focus gate. The game can keep its reload mechanism running
        // while unfocused, and that background transition is exactly what this diagnostic needs to capture.
        if (fcs.IsBound)
            PhysicalStateProbe.Tick();
    }

    public void OnGui()
    {
        window?.OnGui();
    }

    public void Shutdown()
    {
        fcs.Dispose();
        PhysicalStateProbe.Reset();
        window = null;
    }
}
