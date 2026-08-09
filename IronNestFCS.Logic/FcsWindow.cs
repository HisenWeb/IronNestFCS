using MelonLoader;
using UnityEngine;
using IronNestFCS.Logic.FCS;

namespace IronNestFCS.Logic;

/// <summary>
/// 火控系统的 IMGUI 状态窗口。使用绝对 Rect，避免 IL2CPP 下 GUILayout pass 不完整导致控件错位。
/// </summary>
public class FcsWindow
{
    private readonly FSC fcs;

    private bool showWindow = true;
    private Rect defaultWindowRect = new(40, 40, 410, 220);

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
        var lineCount = 2;
        if (fcs.IsBound) {
            lineCount = 7; // left/right headings + idle/task lines + queue/recent headings
            if (fcs.LeftTask != null) lineCount += 1;
            if (fcs.RightTask != null) lineCount += 1;
            lineCount += queue.Count;
            foreach (var task in recent) {
                lineCount += 1;
                if (task.progress == Progress.Failed && !string.IsNullOrEmpty(task.failureReason))
                    lineCount += 1;
            }
        }

        var windowRect = defaultWindowRect;
        windowRect.height = 42f + lineCount * 24f;
        GUI.Box(windowRect, "IronNest FCS");
        
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
            Label("Waiting for Iron Nest fire-control scene.");
            Label("Press F9 to retry binding after the scene is ready.");
            return;
        }

        DrawGun("Left", fcs.LeftTask, Label);
        DrawGun("Right", fcs.RightTask, Label);

        Label($"Queued: {queue.Count}");
        foreach (var item in queue)
        {
            Label($"  T{item.targetId} {item.bulletType}  {item.angel:F1}°/{item.distance:F2}km  {ConvertPosition(item.position)}");
        }

        Label($"Recent: {recent.Count}");
        foreach (var item in recent)
        {
            var result = item.progress == Progress.Finished ? "OK" : "FAILED";
            var duration = item.completedAt > item.startedAt ? item.completedAt - item.startedAt : 0f;
            Label($"  {result} T{item.targetId} {item.bulletType}  C{item.chargeCount} E{item.elevation:F1}°  {duration:F0}s");
            if (item.progress == Progress.Failed && !string.IsNullOrEmpty(item.failureReason)) {
                Label($"    {item.failureReason}");
            }
        }
    }

    private static void DrawGun(string name, ArtilleryTask? task, Action<string> label)
    {
        if (task == null) {
            label($"{name} Gun: Idle");
            return;
        }

        var elapsed = task.startedAt > 0f ? Time.realtimeSinceStartup - task.startedAt : 0f;
        label($"{name} Gun: T{task.targetId} {task.bulletType}  {task.progress}  {elapsed:F0}s");
        label($"  {task.angel:F1}° / {task.distance:F2}km   Charge {task.chargeCount}   Elev {task.elevation:F1}°");
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
