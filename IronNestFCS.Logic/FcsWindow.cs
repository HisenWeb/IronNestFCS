using MelonLoader;
using UnityEngine;
using IronNestFCS.Logic.FCS;

namespace IronNestFCS.Logic;

/// <summary>
/// 火控系统的 IMGUI 状态窗口。使用绝对 Rect，避免 IL2CPP 下 GUILayout pass 不完整导致控件错位。
/// </summary>
public class FcsWindow
{
    private const int RecentDisplayLimit = 8;

    private readonly FSC fcs;

    private bool showWindow = true;
    private Rect defaultWindowRect = new(40, 40, 430, 220);

    public FcsWindow(FSC fcs)
    {
        this.fcs = fcs;
    }

    public void OnGui()
    {
        if (!showWindow)
            return;

        var queue = fcs.QueueCan;
        var recent = fcs.RecentTasks;
        var recentItems = recent.ToArray();
        var recentStart = Math.Max(0, recentItems.Length - RecentDisplayLimit);

        var lineCount = 2;
        if (fcs.IsBound) {
            lineCount = 0;
            lineCount += fcs.LeftTask == null ? 1 : 2;
            lineCount += fcs.RightTask == null ? 1 : 2;
            lineCount += 1;
            if (!string.IsNullOrEmpty(fcs.FirePriorityLeftDetail)) lineCount += 1;
            if (!string.IsNullOrEmpty(fcs.FirePriorityRightDetail)) lineCount += 1;
            lineCount += 1;
            lineCount += 1 + queue.Count;
            lineCount += 2;

            for (var i = recentStart; i < recentItems.Length; i++) {
                lineCount += 1;
                var task = recentItems[i];
                if (task.progress == Progress.Failed && !string.IsNullOrEmpty(task.failureReason))
                    lineCount += 1;
            }
        }

        var windowRect = defaultWindowRect;
        windowRect.height = 42f + lineCount * 24f;
        GUI.Box(windowRect, "IronNest 火控系统");
        
        float x = windowRect.x + 10f;
        float w = windowRect.width - 20f;
        float y = windowRect.y + 25f;
        const float h = 21f;
        const float gap = 3f;

        void Label(string text) {
            GUI.Label(new Rect(x, y, w, h), text);
            y += h + gap;
        }

        if (!fcs.IsBound)
        {
            Label("等待 Iron Nest 火控场景加载。 ");
            Label("场景就绪后按 F9 重新初始化火控逻辑。 ");
            return;
        }

        DrawGun("左", "Left", fcs.LeftTask, Label);
        DrawGun("右", "Right", fcs.RightTask, Label);

        Label(fcs.FirePriorityStatusText);
        if (!string.IsNullOrEmpty(fcs.FirePriorityLeftDetail))
            Label($"  {fcs.FirePriorityLeftDetail}");
        if (!string.IsNullOrEmpty(fcs.FirePriorityRightDetail))
            Label($"  {fcs.FirePriorityRightDetail}");

        Label($"自动开火：{OnOff(fcs.AutoFireEnabled)}    最大装药：{OnOff(fcs.MaxChargeEnabled)}");

        Label($"等待队列：{queue.Count}");
        foreach (var item in queue)
        {
            Label($"  T{item.targetId} {item.bulletType.DisplayName()}  方位 {item.angel:F1}° / {item.distance:F2}km  {ConvertPosition(item.position)}");
        }

        Label($"本轮：完成 {fcs.CompletedTaskCount}    成功 {fcs.SuccessfulTaskCount}    失败 {fcs.FailedTaskCount}");
        var shownRecent = recentItems.Length - recentStart;
        Label($"近期记录：最近 {shownRecent} 条（内部保留 {recent.Count}/20）");
        for (var i = recentStart; i < recentItems.Length; i++)
        {
            var item = recentItems[i];
            var result = item.progress == Progress.Finished ? "成功" : "失败";
            var duration = item.completedAt > item.startedAt ? item.completedAt - item.startedAt : 0f;
            Label($"  {result} T{item.targetId} {item.bulletType.DisplayName()}  装药{item.chargeCount} 仰角{item.elevation:F1}°  {duration:F0}秒");
            if (item.progress == Progress.Failed && !string.IsNullOrEmpty(item.failureReason)) {
                Label($"    原因：{LocalizeFailureReason(item.failureReason)}");
            }
        }
    }

    private static string OnOff(bool value) => value ? "开" : "关";

    private static string LocalizeFailureReason(string reason)
    {
        const string incompatiblePrefix = "no compatible gun for current physical loads;";
        if (reason.StartsWith(incompatiblePrefix, StringComparison.Ordinal))
        {
            var detail = reason.Substring(incompatiblePrefix.Length).Trim();
            detail = detail
                .Replace("Left=", "左炮=")
                .Replace("Right=", "右炮=")
                .Replace("loaded ", "已装填 ")
                .Replace("shell-loaded ", "已入膛 ")
                .Replace("empty", "空炮");
            return $"当前实装弹药无法匹配任务；{detail}";
        }

        return reason;
    }

    private static void DrawGun(string name, string side, ArtilleryTask? task, Action<string> label)
    {
        if (task == null) {
            var state = GunPhysicalState.Read(side);
            switch (state.Kind) {
                case GunPhysicalStateKind.LoadedReady:
                    label($"{name}炮：已装填 {state.ShellType!.Value.DisplayName()} / 装药{state.PowderCharges}，等待目标");
                    break;
                case GunPhysicalStateKind.ShellLoaded:
                    label($"{name}炮：已入膛 {state.ShellType!.Value.DisplayName()} / 未装药，等待同弹种目标");
                    break;
                case GunPhysicalStateKind.EmptyReady:
                    label($"{name}炮：空闲（空炮）");
                    break;
                case GunPhysicalStateKind.PostShotRecovery:
                    label($"{name}炮：击发后复位中");
                    break;
                case GunPhysicalStateKind.Recovering:
                    label($"{name}炮：状态恢复中  {state.Summary()}");
                    break;
                case GunPhysicalStateKind.Unknown:
                    label($"{name}炮：状态待确认  {state.Summary()}");
                    break;
                default:
                    label($"{name}炮：未绑定");
                    break;
            }
            return;
        }

        var elapsed = task.startedAt > 0f ? FcsRuntimeClock.Now - task.startedAt : 0f;
        label($"{name}炮：T{task.targetId} {task.bulletType.DisplayName()}  {ProgressText(task.progress)}  {elapsed:F0}秒");
        label($"  方位 {task.angel:F1}° / 距离 {task.distance:F2}km   装药 {task.chargeCount}   仰角 {task.elevation:F1}°");
    }

    private static string ProgressText(Progress progress)
    {
        return progress switch {
            Progress.Pending => "等待",
            Progress.Calculating => "弹道解算",
            Progress.SelectingBullet => "选弹",
            Progress.LoadingBullet => "装弹",
            Progress.LoadingPowder => "装药",
            Progress.WaitLoading => "等待装填完成",
            Progress.Aiming => "瞄准",
            Progress.WaitingForFire => "等待开火",
            Progress.BackToIdle => "复位",
            Progress.Finished => "完成",
            Progress.Failed => "失败",
            _ => progress.ToString()
        };
    }

    /// <summary>计算坐标点所对应的区域字符串。</summary>
    public static string ConvertPosition(Vector3 position)
    {
        int leterIndex = (int)position.x;
        string zoneCol = leterIndex >= 0 && leterIndex < 26 ? ((char)('A' + leterIndex)).ToString() : "#";
        int zoneRow = (int)position.y + 1;
        int subCol = (int)(position.x * 10) % 10;
        int subRow = (int)(position.y * 10) % 10;

        return $"{zoneCol}{zoneRow}  {subCol}:{subRow}";
    }
}
