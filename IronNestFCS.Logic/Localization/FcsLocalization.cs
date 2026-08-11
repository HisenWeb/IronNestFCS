using IronNestFCS.Logic.FCS;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.Localization;

/// <summary>
/// Player-facing localization only. The UI follows the game's currently rendered language automatically.
/// Chinese is selected only when the game UI provides Chinese evidence; any missing/unsupported detection
/// falls back to English. Runtime diagnostics intentionally stay in English so one log format can be used
/// for every release package.
/// </summary>
internal static class FcsLocalization
{
    private const float LanguagePollSeconds = 1f;

    private static bool _isChinese;
    private static TMP_Text? _languageProbeText;
    private static float _nextLanguagePollAt;

    public static bool IsChinese => _isChinese;
    public static float WindowWidth => IsChinese ? 430f : 560f;

    public static string T(string zhCn, string enUs) => IsChinese ? zhCn : enUs;

    public static string OnOff(bool value) => IsChinese
        ? value ? "开" : "关"
        : value ? "ON" : "OFF";

    /// <summary>
    /// Bind to a stable localized game label after the fire-control scene is ready, then infer the current
    /// game language from the text the game itself is rendering. No user language configuration is required.
    /// </summary>
    public static void BindGameLanguage()
    {
        _isChinese = false; // English is the required fallback.
        _languageProbeText = null;
        _nextLanguagePollAt = Time.realtimeSinceStartup + LanguagePollSeconds;

        try
        {
            _languageProbeText = FindPreferredLanguageProbe();
            if (_languageProbeText != null)
            {
                var probe = SafeText(_languageProbeText);
                if (TryInferFromPreferredProbe(probe, out var chinese))
                {
                    _isChinese = chinese;
                    MelonLogger.Msg($"[FCS] UI language detected from game: {(_isChinese ? "zh-CN" : "en-US")} (probe='{probe}')");
                    return;
                }
            }

            // Fallback scene scan. We only opt into Chinese when the currently rendered game UI contains
            // clear simplified-Chinese evidence; otherwise the requested fallback is English.
            var inspected = 0;
            foreach (var text in Object.FindObjectsOfType<TMP_Text>(true))
            {
                if (text == null)
                    continue;

                var value = SafeText(text);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                inspected++;
                if (!ContainsSimplifiedChineseSignal(value))
                    continue;

                _isChinese = true;
                MelonLogger.Msg($"[FCS] UI language detected from game: zh-CN (scene text scan, inspected={inspected})");
                return;
            }

            MelonLogger.Msg($"[FCS] UI language detected from game: en-US fallback (scene text scan, inspected={inspected})");
        }
        catch (Exception ex)
        {
            _isChinese = false;
            _languageProbeText = null;
            MelonLogger.Warning($"[FCS] Game UI language detection failed; using en-US: {ex.Message}");
        }
    }

    /// <summary>
    /// Follow an in-game language change without requiring a Logic reload. The cached probe avoids repeated
    /// scene-wide searches during normal play.
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
                return;

            var probe = SafeText(_languageProbeText);
            if (!TryInferFromPreferredProbe(probe, out var chinese) || chinese == _isChinese)
                return;

            _isChinese = chinese;
            MelonLogger.Msg($"[FCS] Game UI language changed: {(_isChinese ? "zh-CN" : "en-US")} (probe='{probe}')");
        }
        catch
        {
            // Losing the cached UI object must never affect fire-control execution or force a language flip.
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

            TMP_Text[] texts;
            try { texts = transform.GetComponentsInChildren<TMP_Text>(true); }
            catch { continue; }
            if (texts.Length == 0)
                continue;

            var text = texts[0];
            if (text == null)
                continue;

            // Prefer the stable watch mirror already used by the TTI reader/probe.
            if (path.Contains("Main Camera/Static Gun Watch Parent", StringComparison.OrdinalIgnoreCase))
                return text;

            fallback ??= text;
        }

        return fallback;
    }

    private static bool TryInferFromPreferredProbe(string value, out bool chinese)
    {
        chinese = false;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();

        // The game's Chinese TTI dial currently renders 左. English/other locales render non-Chinese text.
        // This is game UI state, not an OS-language guess.
        if (string.Equals(trimmed, "左", StringComparison.Ordinal))
        {
            chinese = true;
            return true;
        }

        if (ContainsSimplifiedChineseSignal(trimmed))
        {
            chinese = true;
            return true;
        }

        chinese = false;
        return true;
    }

    private static bool ContainsSimplifiedChineseSignal(string value)
    {
        // Prefer characters whose simplified forms differ from common traditional/Japanese forms, so an
        // unsupported locale is less likely to be mistaken for Chinese. English remains the fallback.
        const string signals = "药弹击发时间设置关闭开启进务战敌舰队车这为从后门线显该实装载远达标计预飞";
        foreach (var ch in value)
        {
            if (signals.IndexOf(ch) >= 0)
                return true;
        }
        return false;
    }

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
