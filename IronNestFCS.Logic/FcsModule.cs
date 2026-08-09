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
        bool bound = fcs.TryBind();
        if (bound)
        {
            // Read-only diagnostic: verify whether the game exposes enough physical gun state
            // (especially PowderCharges) to reconstruct loaded guns after an F9 hot reload.
            PhysicalStateProbe.LogCurrentState();
        }
        // 返回绑定结果仅用于 Host 日志；窗口实例已建好，未绑定时会显示提示，
        // 进入场景后按 F9 重载即可绑定。
        return bound;
    }

    public void Update()
    {
        fcs.Update();
        // 高频火控逻辑入口：读炮塔/目标状态、算弹道等。后续在 FSC 上加方法并在此调用。
    }

    public void OnGui()
    {
        window?.OnGui();
    }

    public void Shutdown()
    {
        fcs.Dispose();
        window = null;
    }
}
