using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Localization;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

/// <summary>
/// Compact FCS IMGUI status window. The HUD answers what each gun is doing, what fires next,
/// and why queued work is waiting; detailed firing-solution data remains on the in-game stickers.
/// </summary>
public class FcsWindow
{
    private readonly FSC fcs;
    private Rect defaultWindowRect = new(40, 40, 430, 220);

    public FcsWindow(FSC fcs)
    {
        this.fcs = fcs;
    }

    public void OnGui()
    {
        var queue = fcs.QueueCan;
        var hasActiveTask = fcs.LeftTask != null || fcs.RightTask != null;
        var showPriority = hasActiveTask && !string.IsNullOrWhiteSpace(fcs.FirePriorityStatusText);

        var lineCount = 2;
        if (fcs.IsBound)
        {
            // Left gun + right gun + controls are always visible.
            lineCount = 3;
            if (showPriority)
                lineCount += 1;
            if (queue.Count > 0)
                lineCount += 1 + queue.Count;
        }

        var windowRect = defaultWindowRect;
        windowRect.width = FcsLocalization.WindowWidth;
        windowRect.height = 42f + lineCount * 24f;
        GUI.Box(windowRect, FcsLocalization.T("IronNest 火控系统", "IronNest Fire Control System"));

        var x = windowRect.x + 10f;
        var w = windowRect.width - 20f;
        var y = windowRect.y + 25f;
        const float h = 21f;
        const float gap = 3f;

        void Label(string text)
        {
            GUI.Label(new Rect(x, y, w, h), text);
            y += h + gap;
        }

        if (!fcs.IsBound)
        {
            Label(FcsLocalization.T(
                "等待 Iron Nest 火控场景加载。",
                "Waiting for an Iron Nest fire-control scene."));
            Label(FcsLocalization.T(
                "场景就绪后按 F9 重新初始化火控逻辑。",
                "Press F9 after the scene is ready to reinitialize the TaskSystem."));
            return;
        }

        DrawGun(
            FcsLocalization.T("左炮", "Left gun"),
            "Left",
            fcs.LeftTask,
            fcs.PlanExecutor.GetPlan(LeftRight.Left)?.EstimatedFlightSeconds ?? float.NaN,
            Label);
        DrawGun(
            FcsLocalization.T("右炮", "Right gun"),
            "Right",
            fcs.RightTask,
            fcs.PlanExecutor.GetPlan(LeftRight.Right)?.EstimatedFlightSeconds ?? float.NaN,
            Label);

        if (showPriority)
            Label(fcs.FirePriorityStatusText);

        Label(FcsLocalization.T(
            $"自动开火：{FcsLocalization.OnOff(fcs.AutoFireEnabled)}    最大装药：{FcsLocalization.OnOff(fcs.MaxChargeEnabled)}",
            $"Auto Fire: {FcsLocalization.OnOff(fcs.AutoFireEnabled)}    Max Charge: {FcsLocalization.OnOff(fcs.MaxChargeEnabled)}"));

        if (queue.Count > 0)
        {
            Label(FcsLocalization.T($"等待队列：{queue.Count}", $"Pending: {queue.Count}"));
            foreach (var item in queue)
            {
                var hintZh = item.pendingHint switch
                {
                    PendingHint.ShellMismatch => " · 弹种不匹配",
                    PendingHint.ChargeRangeInsufficient => " · 装药射程不足",
                    PendingHint.AmmoMismatch => " · 装药射程不足",
                    _ => "",
                };
                var hintEn = item.pendingHint switch
                {
                    PendingHint.ShellMismatch => " · shell mismatch",
                    PendingHint.ChargeRangeInsufficient => " · charge range insufficient",
                    PendingHint.AmmoMismatch => " · charge range insufficient",
                    _ => "",
                };

                Label(FcsLocalization.T(
                    $"  T{item.targetId} {item.bulletType.DisplayName()}{hintZh}",
                    $"  T{item.targetId} {item.bulletType.DisplayName()}{hintEn}"));
            }
        }
    }

    private static void DrawGun(
        string gunName,
        string side,
        ArtilleryTask? task,
        float estimatedFlightSeconds,
        Action<string> label)
    {
        if (task == null)
        {
            var state = GunPhysicalState.Read(side);
            switch (state.Kind)
            {
                case GunPhysicalStateKind.LoadedReady:
                    label(FcsLocalization.T(
                        $"{gunName}：已装填 {state.ShellType!.Value.DisplayName()} / 装药{state.PowderCharges}，等待目标",
                        $"{gunName}: loaded {state.ShellType!.Value.DisplayName()} / C{state.PowderCharges}, waiting for target"));
                    break;
                case GunPhysicalStateKind.ShellLoaded:
                    label(FcsLocalization.T(
                        $"{gunName}：已入膛 {state.ShellType!.Value.DisplayName()}，等待同弹种目标",
                        $"{gunName}: chambered {state.ShellType!.Value.DisplayName()}, waiting for matching target"));
                    break;
                case GunPhysicalStateKind.EmptyReady:
                    label(FcsLocalization.T($"{gunName}：空闲（空炮）", $"{gunName}: idle / empty"));
                    break;
                case GunPhysicalStateKind.PostShotRecovery:
                    label(FcsLocalization.T($"{gunName}：击发后复位中", $"{gunName}: post-shot recovery"));
                    break;
                case GunPhysicalStateKind.Recovering:
                    label(FcsLocalization.T($"{gunName}：状态恢复中", $"{gunName}: recovering"));
                    break;
                case GunPhysicalStateKind.Unknown:
                    label(FcsLocalization.T($"{gunName}：状态待确认", $"{gunName}: state unknown"));
                    break;
                default:
                    label(FcsLocalization.T($"{gunName}：未绑定", $"{gunName}: unbound"));
                    break;
            }
            return;
        }

        var elapsed = task.startedAt > 0f ? FcsRuntimeClock.Now - task.startedAt : 0f;
        var flightZh = float.IsNaN(estimatedFlightSeconds) ? "" : $" · 飞行 {estimatedFlightSeconds:F1}秒";
        var flightEn = float.IsNaN(estimatedFlightSeconds) ? "" : $" · Flight {estimatedFlightSeconds:F1}s";

        label(FcsLocalization.T(
            $"{gunName}：T{task.targetId} {task.bulletType.DisplayName()} · {FcsLocalization.ProgressText(task.progress)} · {elapsed:F0}秒{flightZh}",
            $"{gunName}: T{task.targetId} {task.bulletType.DisplayName()} · {FcsLocalization.ProgressText(task.progress)} · {elapsed:F0}s{flightEn}"));
    }
}
