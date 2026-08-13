using IronNestFCS.Logic.FCS;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.Localization;

/// <summary>
/// Player-facing localization only. Chinese is enabled only when the game's localized left TTI label
/// explicitly renders 左; every other state/language falls back to English. Runtime diagnostics stay in
/// English so one log format can be used for every release package.
/// </summary>
internal static class FcsLocalization
{
    private const float LanguagePollSeconds = 1f;
    private const string ChineseLeftLabel = "左";

    private static bool _isChinese;
    private static TMP_Text? _languageProbeText;
    private static float _nextLanguagePollAt;

    public static bool IsChinese => _isChinese;
    public static float WindowWidth => IsChinese ? 490f : 560f;

    public static string T(string zhCn, string enUs) => IsChinese ? zhCn : enUs;

    public static string OnOff(bool value) => IsChinese
        ? value ? "开" : "关"
        : value ? "ON" : "OFF";

    /// <summary>
    /// Bind to the game's localized left Time-To-Impact label. Only the exact Chinese label 左 selects the
    /// Chinese FCS UI; a missing probe, an empty label, English, or any other locale selects English.
    /// </summary>
    public static void BindGameLanguage()
    {
        _isChinese = false;
        _languageProbeText = null;
        _nextLanguagePollAt = Time.realtimeSinceStartup + LanguagePollSeconds;

        try
        {
            _languageProbeText = FindPreferredLanguageProbe();
            if (_languageProbeText == null)
            {
                MelonLogger.Msg("[FCS] UI language probe not found; using en-US fallback");
                return;
            }

            var probe = SafeText(_languageProbeText);
            _isChinese = IsChineseProbe(probe);
            MelonLogger.Msg(
                $"[FCS] UI language detected from game: {(_isChinese ? "zh-CN" : "en-US")} (probe='{probe}')");
        }
        catch (Exception ex)
        {
            _isChinese = false;
            _languageProbeText = null;
            MelonLogger.Warning($"[FCS] Game UI language detection failed; using en-US: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-read only the cached TMP label once per second. If the game rebuilds the localized UI object, find
    /// that one probe again. No scene-wide text scan is performed during normal polling.
    /// </summary>
    public static void TickGameLanguage()
    {
        var now = Time.realtimeSinceStartup;
        if (now < _nextLanguagePollAt)
            return;
        _nextLanguagePollAt = now + LanguagePollSeconds;

        try
        {
            if (_languageProbeText == null)
            {
                _languageProbeText = FindPreferredLanguageProbe();
                if (_languageProbeText == null)
                {
                    if (_isChinese)
                    {
                        _isChinese = false;
                        MelonLogger.Msg("[FCS] Game UI language probe unavailable; changed to en-US fallback");
                    }
                    return;
                }
            }

            var probe = SafeText(_languageProbeText);
            var chinese = IsChineseProbe(probe);
            if (chinese == _isChinese)
                return;

            _isChinese = chinese;
            MelonLogger.Msg(
                $"[FCS] UI language changed: {(_isChinese ? "zh-CN" : "en-US")} (probe='{probe}')");
        }
        catch
        {
            _languageProbeText = null;
            if (_isChinese)
            {
                _isChinese = false;
                MelonLogger.Msg("[FCS] Game UI language probe lost; changed to en-US fallback");
            }
        }
    }

    public static void ResetGameLanguage()
    {
        _languageProbeText = null;
        _isChinese = false;
        _nextLanguagePollAt = 0f;
    }

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
            return T("当前没有可立即形成配对的计划", "no immediately available partner plan");
        }

        return reason;
    }

    public static string LogReason(string reason)
    {
        if (string.Equals(reason, "等待队列为空", StringComparison.Ordinal)
            || string.Equals(reason, "queue empty", StringComparison.OrdinalIgnoreCase))
        {
            return "no immediately available partner FirePlan";
        }

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

    private static TMP_Text? FindPreferredLanguageProbe()
    {
        TMP_Text? fallback = null;
        foreach (var transform in Object.FindObjectsOfType<Transform>(true))
        {
            if (transform == null
                || !string.Equals(transform.name, ".ImpactTimeDial_Left", StringComparison.Ordinal))
            {
                continue;
            }

            var path = BuildPath(transform);
            if (!path.Contains("Time To Impact Dials", StringComparison.OrdinalIgnoreCase))
                continue;

            TMP_Text? text = null;
            try
            {
                var texts = transform.GetComponentsInChildren<TMP_Text>(true);
                if (texts.Length > 0)
                    text = texts[0];
            }
            catch
            {
                continue;
            }

            if (text == null)
                continue;

            if (path.Contains("Main Camera/Static Gun Watch Parent", StringComparison.OrdinalIgnoreCase))
                return text;

            fallback ??= text;
        }

        return fallback;
    }

    private static bool IsChineseProbe(string value) =>
        string.Equals(value.Trim(), ChineseLeftLabel, StringComparison.Ordinal);

    private static string SafeText(TMP_Text? text)
    {
        try { return text?.text?.Replace("\r", " ").Replace("\n", " ").Trim() ?? ""; }
        catch { return ""; }
    }

    private static string BuildPath(Transform? transform)
    {
        if (transform == null)
            return "<no-transform>";

        var parts = new List<string>();
        var current = transform;
        var guard = 0;
        while (current != null && guard++ < 32)
        {
            parts.Add(current.name);
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }
}
