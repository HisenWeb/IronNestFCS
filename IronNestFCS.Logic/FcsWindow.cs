using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Localization;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

/// <summary>
/// Combat-focused FCS IMGUI status window. Keep current gun state and firing-solution information visible,
/// while omitting historical/session statistics and internal scheduling diagnostics.
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
            lineCount = 1;
            lineCount += fcs.LeftTask == null ? 1 : 2;
            lineCount += fcs.RightTask == null ? 1 : 2;
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
            $"自动开火：{FcsLocalization.OnOff(fcs.AutoFireEnabled)}    最大装药量：{FcsLocalization.OnOff(fcs.MaxChargeEnabled)}",
            $"Auto Fire: {FcsLocalization.OnOff(fcs.AutoFireEnabled)}    Max Charge: {FcsLocalization.OnOff(fcs.MaxChargeEnabled)}"));

        if (queue.Count > 0)
        {
            Label(FcsLocalization.T($"等待队列：{queue.Count}", $"Pending: {queue.Count}"));
            foreach (var item in queue)
            {
                var position = ConvertPosition(item.position);
                Label(FcsLocalization.T(
                    $"  T{item.targetId} {item.bulletType.DisplayName()} · 打击 {position} · 方位 {item.angel:F1}°",
                    $"  T{item.targetId} {item.bulletType.DisplayName()} · Impact {position} · Az {item.angel:F1}°"));
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
                        $"{gunName}：已装填 {state.ShellType!.Value.DisplayName()} / 装药量{state.PowderCharges}，等待匹配任务",
                        $"{gunName}: loaded {state.ShellType!.Value.DisplayName()} / C{state.PowderCharges}, waiting for matching task"));
                    break;
                case GunPhysicalStateKind.ShellLoaded:
                    label(FcsLocalization.T(
                        $"{gunName}：已入膛 {state.ShellType!.Value.DisplayName()}，等待匹配任务",
                        $"{gunName}: chambered {state.ShellType!.Value.DisplayName()}, waiting for matching task"));
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
        label(FcsLocalization.T(
            $"{gunName}：T{task.targetId} {task.bulletType.DisplayName()} · {FcsLocalization.ProgressText(task.progress)} · {elapsed:F0}秒",
            $"{gunName}: T{task.targetId} {task.bulletType.DisplayName()} · {FcsLocalization.ProgressText(task.progress)} · {elapsed:F0}s"));

        var position = ConvertPosition(task.position);
        var flightZh = float.IsNaN(estimatedFlightSeconds) ? "--" : $"{estimatedFlightSeconds:F1}秒";
        var flightEn = float.IsNaN(estimatedFlightSeconds) ? "--" : $"{estimatedFlightSeconds:F1}s";
        label(FcsLocalization.T(
            $"  打击 {position} · 方位 {task.angel:F1}° · 装药量{task.chargeCount} · 仰角{task.elevation:F1}° · 飞行 {flightZh}",
            $"  Impact {position} · Az {task.angel:F1}° · C{task.chargeCount} · E{task.elevation:F1}° · Flight {flightEn}"));
    }

    /// <summary>Converts a map coordinate into the grid/sub-grid notation used by the tactical map.</summary>
    public static string ConvertPosition(Vector3 position)
    {
        var letterIndex = (int)position.x;
        var zoneCol = letterIndex >= 0 && letterIndex < 26 ? ((char)('A' + letterIndex)).ToString() : "#";
        var zoneRow = (int)position.y + 1;
        var subCol = (int)(position.x * 10) % 10;
        var subRow = (int)(position.y * 10) % 10;

        return $"{zoneCol}{zoneRow}  {subCol}:{subRow}";
    }
}
