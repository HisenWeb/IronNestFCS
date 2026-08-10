using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Localization;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

/// <summary>
/// FCS IMGUI status window. Absolute Rect positioning avoids incomplete GUILayout passes under IL2CPP.
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
        if (fcs.IsBound)
        {
            lineCount = 0;
            lineCount += fcs.LeftTask == null ? 1 : 2;
            lineCount += fcs.RightTask == null ? 1 : 2;
            lineCount += 1;
            if (!string.IsNullOrEmpty(fcs.FirePriorityLeftDetail)) lineCount += 1;
            if (!string.IsNullOrEmpty(fcs.FirePriorityRightDetail)) lineCount += 1;
            lineCount += 1;
            lineCount += 1 + queue.Count;
            lineCount += 2;

            for (var i = recentStart; i < recentItems.Length; i++)
            {
                lineCount += 1;
                var task = recentItems[i];
                if (task.progress == Progress.Failed && !string.IsNullOrEmpty(task.failureReason))
                    lineCount += 1;
            }
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

        DrawGun(FcsLocalization.T("左炮", "Left gun"), "Left", fcs.LeftTask, Label);
        DrawGun(FcsLocalization.T("右炮", "Right gun"), "Right", fcs.RightTask, Label);

        Label(fcs.FirePriorityStatusText);
        if (!string.IsNullOrEmpty(fcs.FirePriorityLeftDetail))
            Label($"  {fcs.FirePriorityLeftDetail}");
        if (!string.IsNullOrEmpty(fcs.FirePriorityRightDetail))
            Label($"  {fcs.FirePriorityRightDetail}");

        Label(FcsLocalization.T(
            $"自动开火：{FcsLocalization.OnOff(fcs.AutoFireEnabled)}    最大装药：{FcsLocalization.OnOff(fcs.MaxChargeEnabled)}",
            $"Auto Fire: {FcsLocalization.OnOff(fcs.AutoFireEnabled)}    Max Charge: {FcsLocalization.OnOff(fcs.MaxChargeEnabled)}"));

        Label(FcsLocalization.T($"等待队列：{queue.Count}", $"Pending queue: {queue.Count}"));
        foreach (var item in queue)
        {
            Label(FcsLocalization.T(
                $"  T{item.targetId} {item.bulletType.DisplayName()}  方位 {item.angel:F1}° / {item.distance:F2}km  {ConvertPosition(item.position)}",
                $"  T{item.targetId} {item.bulletType.DisplayName()}  Az {item.angel:F1}° / {item.distance:F2}km  {ConvertPosition(item.position)}"));
        }

        Label(FcsLocalization.T(
            $"本轮：完成 {fcs.CompletedTaskCount}    成功 {fcs.SuccessfulTaskCount}    失败 {fcs.FailedTaskCount}",
            $"Session: completed {fcs.CompletedTaskCount}    success {fcs.SuccessfulTaskCount}    failed {fcs.FailedTaskCount}"));

        var shownRecent = recentItems.Length - recentStart;
        Label(FcsLocalization.T(
            $"近期记录：最近 {shownRecent} 条（内部保留 {recent.Count}/20）",
            $"Recent: showing {shownRecent} (kept {recent.Count}/20)"));

        for (var i = recentStart; i < recentItems.Length; i++)
        {
            var item = recentItems[i];
            var result = item.progress == Progress.Finished
                ? FcsLocalization.T("成功", "SUCCESS")
                : FcsLocalization.T("失败", "FAILED");
            var duration = item.completedAt > item.startedAt ? item.completedAt - item.startedAt : 0f;

            Label(FcsLocalization.T(
                $"  {result} T{item.targetId} {item.bulletType.DisplayName()}  装药{item.chargeCount} 仰角{item.elevation:F1}°  {duration:F0}秒",
                $"  {result} T{item.targetId} {item.bulletType.DisplayName()}  C{item.chargeCount} E{item.elevation:F1}°  {duration:F0}s"));

            if (item.progress == Progress.Failed && !string.IsNullOrEmpty(item.failureReason))
            {
                Label(FcsLocalization.T(
                    $"    原因：{FcsLocalization.FailureReason(item.failureReason)}",
                    $"    Reason: {FcsLocalization.FailureReason(item.failureReason)}"));
            }
        }
    }

    private static void DrawGun(string gunName, string side, ArtilleryTask? task, Action<string> label)
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
                        $"{gunName}：已入膛 {state.ShellType!.Value.DisplayName()} / 未装药，等待同弹种目标",
                        $"{gunName}: chambered {state.ShellType!.Value.DisplayName()} / no charge, waiting for matching target"));
                    break;
                case GunPhysicalStateKind.EmptyReady:
                    label(FcsLocalization.T($"{gunName}：空闲（空炮）", $"{gunName}: idle / empty"));
                    break;
                case GunPhysicalStateKind.PostShotRecovery:
                    label(FcsLocalization.T($"{gunName}：击发后复位中", $"{gunName}: post-shot recovery"));
                    break;
                case GunPhysicalStateKind.Recovering:
                    label(FcsLocalization.T($"{gunName}：状态恢复中  {state.Summary()}", $"{gunName}: recovering  {state.Summary()}"));
                    break;
                case GunPhysicalStateKind.Unknown:
                    label(FcsLocalization.T($"{gunName}：状态待确认  {state.Summary()}", $"{gunName}: state unknown  {state.Summary()}"));
                    break;
                default:
                    label(FcsLocalization.T($"{gunName}：未绑定", $"{gunName}: unbound"));
                    break;
            }
            return;
        }

        var elapsed = task.startedAt > 0f ? FcsRuntimeClock.Now - task.startedAt : 0f;
        label(FcsLocalization.T(
            $"{gunName}：T{task.targetId} {task.bulletType.DisplayName()}  {FcsLocalization.ProgressText(task.progress)}  {elapsed:F0}秒",
            $"{gunName}: T{task.targetId} {task.bulletType.DisplayName()}  {FcsLocalization.ProgressText(task.progress)}  {elapsed:F0}s"));
        label(FcsLocalization.T(
            $"  方位 {task.angel:F1}° / 距离 {task.distance:F2}km   装药 {task.chargeCount}   仰角 {task.elevation:F1}°",
            $"  Az {task.angel:F1}° / Range {task.distance:F2}km   Charge {task.chargeCount}   Elevation {task.elevation:F1}°"));
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
