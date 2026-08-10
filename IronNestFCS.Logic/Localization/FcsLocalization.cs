using MelonLoader;

namespace IronNestFCS.Logic.Localization;

/// <summary>
/// Player-facing localization only. Runtime diagnostics intentionally stay in English so one log format
/// can be used for every release package. The selected language is read once when the hot-reloadable Logic
/// assembly starts; pressing F9 reloads the file.
/// </summary>
internal static class FcsLocalization
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    private static string? _language;

    public static string Language => _language ??= LoadLanguage();
    public static bool IsChinese => string.Equals(Language, Chinese, StringComparison.OrdinalIgnoreCase);
    public static float WindowWidth => IsChinese ? 430f : 560f;

    public static string T(string zhCn, string enUs) => IsChinese ? zhCn : enUs;

    public static string OnOff(bool value) => IsChinese
        ? value ? "开" : "关"
        : value ? "ON" : "OFF";

    public static string ProgressText(Progress progress)
    {
        if (!IsChinese)
        {
            return progress switch
            {
                Progress.Pending => "Pending",
                Progress.Calculating => "Ballistic calculation",
                Progress.SelectingBullet => "Selecting shell",
                Progress.LoadingBullet => "Loading shell",
                Progress.LoadingPowder => "Loading charge",
                Progress.WaitLoading => "Waiting for load",
                Progress.Aiming => "Aiming",
                Progress.WaitingForFire => "Ready / waiting to fire",
                Progress.BackToIdle => "Recovering",
                Progress.Finished => "Finished",
                Progress.Failed => "Failed",
                _ => progress.ToString(),
            };
        }

        return progress switch
        {
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
            _ => progress.ToString(),
        };
    }

    public static string UiReason(string reason)
    {
        if (string.Equals(reason, "等待队列为空", StringComparison.Ordinal)
            || string.Equals(reason, "queue empty", StringComparison.OrdinalIgnoreCase))
        {
            return T("等待队列为空", "queue empty");
        }

        return reason;
    }

    public static string LogReason(string reason)
    {
        if (string.Equals(reason, "等待队列为空", StringComparison.Ordinal))
            return "queue empty";
        return reason;
    }

    public static string FailureReason(string reason)
    {
        if (!IsChinese)
            return reason;

        const string incompatiblePrefix = "no compatible gun for current physical loads;";
        if (!reason.StartsWith(incompatiblePrefix, StringComparison.Ordinal))
            return reason;

        var detail = reason.Substring(incompatiblePrefix.Length).Trim()
            .Replace("Left=", "左炮=")
            .Replace("Right=", "右炮=")
            .Replace("loaded ", "已装填 ")
            .Replace("shell-loaded ", "已入膛 ")
            .Replace("empty", "空炮");
        return $"当前实装弹药无法匹配任务；{detail}";
    }

    private static string LoadLanguage()
    {
        var path = Path.Combine(MelonEnvironment.UserDataDirectory, "IronNestFCS", "language.txt");
        try
        {
            if (!File.Exists(path))
                return Chinese; // Preserve existing installs. Public release ZIPs always include this file.

            var raw = File.ReadAllText(path).Trim();
            if (raw.Equals(Chinese, StringComparison.OrdinalIgnoreCase)
                || raw.Equals("zh", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
            {
                return Chinese;
            }

            if (raw.Equals(English, StringComparison.OrdinalIgnoreCase)
                || raw.Equals("en", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            {
                return English;
            }

            MelonLogger.Warning($"[FCS] Unsupported UI language '{raw}', falling back to {Chinese}.");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS] Failed to read UI language config: {ex.Message}");
        }

        return Chinese;
    }
}
