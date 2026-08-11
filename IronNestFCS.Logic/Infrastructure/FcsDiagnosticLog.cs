using System.Diagnostics;
using System.Text;
using MelonLoader;
using MelonLoader.Logging;
using MelonLoader.Utils;

namespace IronNestFCS.Logic.Infrastructure;

/// <summary>
/// Mirrors FCS-owned MelonLogger output into compact diagnostic files that survive Logic F9 reloads.
/// By default only problems.log is written. Set UserData/IronNestFCS/diagnostics.txt to "on"
/// to restore the full categorized diagnostic set for troubleshooting.
/// Logging is diagnostic-only: every filesystem/callback failure is swallowed so it can never block fire control.
/// </summary>
internal static class FcsDiagnosticLog {
    private const int MaxRunDirectories = 20;
    private const string DiagnosticsFileName = "diagnostics.txt";

    private static readonly object Sync = new();
    private static readonly string[] FileNames = {
        "all",
        "dispatch",
        "ballistic",
        "reload",
        "order",
        "arbitration",
        "turret",
        "trigger",
        "flight",
        "problems",
    };

    private static readonly Dictionary<string, StreamWriter> Writers =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool _started;
    private static bool _detailedDiagnostics;
    private static string _runDirectory = "";
    private static string _sessionId = "";
    private static Func<string>? _contextProvider;

    public static string RunDirectory => _runDirectory;
    public static bool DetailedDiagnosticsEnabled => _detailedDiagnostics;

    public static void Start(Func<string>? contextProvider = null) {
        lock (Sync) {
            if (_started) {
                _contextProvider = contextProvider;
                return;
            }

            try {
                _contextProvider = contextProvider;
                var process = Process.GetCurrentProcess();
                var processStarted = process.StartTime;
                var configRoot = Path.Combine(
                    MelonEnvironment.UserDataDirectory,
                    "IronNestFCS");
                Directory.CreateDirectory(configRoot);

                _detailedDiagnostics = ReadDetailedDiagnosticsSetting(configRoot);

                var logsRoot = Path.Combine(configRoot, "Logs");
                var dayDirectory = Path.Combine(logsRoot, processStarted.ToString("yyyy-MM-dd"));
                _runDirectory = Path.Combine(
                    dayDirectory,
                    $"run-{processStarted:HHmmss}-pid{process.Id}");
                Directory.CreateDirectory(_runDirectory);

                if (_detailedDiagnostics) {
                    foreach (var name in FileNames)
                        Writers[name] = OpenWriter(Path.Combine(_runDirectory, name + ".log"));
                }
                else {
                    Writers["problems"] = OpenWriter(Path.Combine(_runDirectory, "problems.log"));
                }

                _sessionId = DateTime.Now.ToString("HHmmss.fff");
                _started = true;

                MelonLogger.MsgDrawingCallbackHandler += OnMessage;
                MelonLogger.WarningCallbackHandler += OnWarning;
                MelonLogger.ErrorCallbackHandler += OnError;

                WriteMarkerToAll(
                    $"LOGIC SESSION START | session={_sessionId} | detailedDiagnostics={(_detailedDiagnostics ? "on" : "off")} | " +
                    $"processStart={processStarted:yyyy-MM-dd HH:mm:ss} | pid={process.Id}");
                CleanupOldRuns(logsRoot, _runDirectory);
            }
            catch {
                DetachCallbacksNoThrow();
                DisposeWritersNoThrow();
                _started = false;
                _detailedDiagnostics = false;
                _contextProvider = null;
            }
        }
    }

    public static void MarkBindResult(bool bound, int generation, string leftPhysical, string rightPhysical) {
        lock (Sync) {
            if (!_started)
                return;

            if (!_detailedDiagnostics && bound)
                return;

            try {
                WriteMarkerToAll(
                    $"BIND {(bound ? "SUCCESS" : "FAILED")} | session={_sessionId} | gen={generation} | " +
                    $"Left={Normalize(leftPhysical)} | Right={Normalize(rightPhysical)}");
            }
            catch {
            }
        }
    }

    public static void Stop(string reason) {
        lock (Sync) {
            if (!_started)
                return;

            try {
                WriteMarkerToAll(
                    $"LOGIC SESSION END | session={_sessionId} | reason={Normalize(reason)} | context={SafeContext()}");
            }
            catch {
            }
            finally {
                DetachCallbacksNoThrow();
                DisposeWritersNoThrow();
                _contextProvider = null;
                _started = false;
                _detailedDiagnostics = false;
            }
        }
    }

    private static bool ReadDetailedDiagnosticsSetting(string configRoot) {
        try {
            var path = Path.Combine(configRoot, DiagnosticsFileName);
            if (!File.Exists(path)) {
                File.WriteAllText(path, "off\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return false;
            }

            var value = File.ReadAllText(path).Trim();
            return value.Equals("on", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("full", StringComparison.OrdinalIgnoreCase);
        }
        catch {
            return false;
        }
    }

    private static StreamWriter OpenWriter(string path) {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) {
            AutoFlush = true,
        };
    }

    private static void OnMessage(ColorARGB sectionColor, ColorARGB textColor, string section, string text) {
        Capture("INFO", section, text);
    }

    private static void OnWarning(string section, string text) {
        Capture("WARN", section, text);
    }

    private static void OnError(string section, string text) {
        Capture("ERROR", section, text);
    }

    private static void Capture(string level, string section, string text) {
        lock (Sync) {
            if (!_started || !IsFcsMessage(section, text))
                return;

            try {
                var problem = IsProblemSignal(level, text);
                if (!_detailedDiagnostics && !problem)
                    return;

                var category = Classify(text);
                var line = FormatLine(level, category, section, text);

                if (_detailedDiagnostics) {
                    Write("all", line);
                    Write(category, line);
                }

                if (problem)
                    Write("problems", line);
            }
            catch {
                // Never log a logging failure through MelonLogger; doing so would recurse through callbacks.
            }
        }
    }

    private static string FormatLine(string level, string category, string section, string text) {
        return
            $"{DateTime.Now:HH:mm:ss.fff} | {level,-5} | {category.ToUpperInvariant(),-11} | " +
            $"session={_sessionId} | {SafeContext()} | section={Normalize(section)} | {Normalize(text)}";
    }

    private static string SafeContext() {
        try {
            var context = _contextProvider?.Invoke();
            return string.IsNullOrWhiteSpace(context) ? "gen=- | L=- | R=-" : Normalize(context);
        }
        catch {
            return "gen=? | L=? | R=?";
        }
    }

    private static bool IsFcsMessage(string section, string text) {
        if (!string.IsNullOrEmpty(section)
            && section.Contains("IronNestFCS", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (string.IsNullOrEmpty(text))
            return false;

        return text.Contains("[FCS", StringComparison.OrdinalIgnoreCase)
               || text.Contains("IronNest FCS", StringComparison.OrdinalIgnoreCase);
    }

    private static string Classify(string text) {
        var value = text ?? "";

        if (ContainsAny(value, "[FCS-FLIGHT-PROBE]", "FLIGHT-PROBE"))
            return "flight";

        if (ContainsAny(value,
                "BALLISTIC", "ballistic", "Calculate", "calculator"))
            return "ballistic";

        if (ContainsAny(value,
                "[FCS Order]", "paired once", "single committed",
                "promoting previously compared plan", "waiting for pair"))
            return "order";

        if (ContainsAny(value,
                "Fire arbitration", "Fire priority", "arbitration",
                "ballistic solution registered", "queued behind current fire priority",
                "首发仲裁", "hard-committed", "fire lane", "provisionally"))
            return "arbitration";

        if (ContainsAny(value,
                "Trigger", "trigger console", "Review", "Arm", "ReadyToFire",
                "ConfirmTask", "ConfirmBullet", "ConfirmRotation", "ConfirmElevation",
                "AutoFire", "automatic fire", "manual fire"))
            return "trigger";

        if (ContainsAny(value,
                "turret", "Turret", "azimuth", "shared turret lane"))
            return "turret";

        if (ContainsAny(value,
                "[FCS Loading]", "LoadingSystem", "LoadingTransaction", "LoadedReady", "loading transaction",
                "ReloadTrace", "reload", "Reload", "powder", "Powder", "shell", "Shell",
                "chamber", "cylinder", "rammer", "LoadBullet", "LoadPowder", "physical state",
                "PHYSICAL", "requisition", "BuyShell", "BuyPowders", "loaded-ready"))
            return "reload";

        return "dispatch";
    }

    private static bool IsProblemSignal(string level, string text) {
        // Every warning/error is actionable enough to belong in the compact problem rollup.
        if (!string.Equals(level, "INFO", StringComparison.OrdinalIgnoreCase))
            return true;

        // INFO-level physical recovery transitions and normal F9 reset traces are intentionally excluded.
        // Only exceptional state-machine events are promoted so problems.log stays a useful first-look file.
        return ContainsAny(text,
            "[FCS Stall]", "STALL", "invalidated", "discarded stale", "reclass",
            "rejected", "F9 recovery failed", "F9 reset failed",
            "automatic fire was not observed", "manual fire wait timed out");
    }

    private static bool ContainsAny(string value, params string[] needles) {
        foreach (var needle in needles) {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Normalize(string? value) {
        if (string.IsNullOrEmpty(value))
            return "-";

        return value
            .Replace("\r\n", " ↩ ")
            .Replace("\n", " ↩ ")
            .Replace("\r", " ↩ ");
    }

    private static void WriteMarkerToAll(string marker) {
        var line = $"{DateTime.Now:HH:mm:ss.fff} | ===== | SESSION     | {marker}";
        foreach (var name in FileNames)
            Write(name, line);
    }

    private static void Write(string name, string line) {
        if (Writers.TryGetValue(name, out var writer))
            writer.WriteLine(line);
    }

    private static void CleanupOldRuns(string logsRoot, string currentRun) {
        try {
            if (!Directory.Exists(logsRoot))
                return;

            var runs = Directory
                .GetDirectories(logsRoot, "run-*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(path, currentRun, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetCreationTimeUtc)
                .ToArray();

            foreach (var oldRun in runs.Skip(Math.Max(0, MaxRunDirectories - 1))) {
                try { Directory.Delete(oldRun, recursive: true); }
                catch { }
            }

            foreach (var dayDirectory in Directory.GetDirectories(logsRoot)) {
                try {
                    if (!Directory.EnumerateFileSystemEntries(dayDirectory).Any())
                        Directory.Delete(dayDirectory);
                }
                catch { }
            }
        }
        catch {
        }
    }

    private static void DetachCallbacksNoThrow() {
        try { MelonLogger.MsgDrawingCallbackHandler -= OnMessage; }
        catch { }
        try { MelonLogger.WarningCallbackHandler -= OnWarning; }
        catch { }
        try { MelonLogger.ErrorCallbackHandler -= OnError; }
        catch { }
    }

    private static void DisposeWritersNoThrow() {
        foreach (var writer in Writers.Values) {
            try { writer.Flush(); }
            catch { }
            try { writer.Dispose(); }
            catch { }
        }
        Writers.Clear();
    }
}
