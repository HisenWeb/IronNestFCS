using IronNestFCS.Abstractions;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(IronNestFCS.FcsHostMod), "IronNestFCS Smart", "1.1.1", "svr2kos2")]
[assembly: MelonGame()]

namespace IronNestFCS;

/// <summary>
/// Stable Host. F9 reloads TaskSystem only; PersistentLoadingSystem remains alive and continues any
/// already-accepted physical loading transaction.
/// </summary>
public class FcsHostMod : MelonMod
{
    private const string ReloadKeyName = "F9";
    private const string LogicTypeName = "IronNestFCS.Logic.FcsModule";
    private const float InitialBindDelaySeconds = 1f;
    private const float SceneBindDelaySeconds = 3f;
    private const float BindRetryDelaySeconds = 1f;

    private readonly FcsHostServices _hostServices = new();
    private LogicReloader? _reloader;
    private MapCoordinateDiagnosticProbe? _mapCoordinateDiag;
    private bool _sceneBindPending;
    private float _nextBindAttemptAt;

    public override void OnInitializeMelon()
    {
        var logicDir = Path.Combine(
            MelonEnvironment.UserDataDirectory,
            "IronNestFCS");
        Directory.CreateDirectory(logicDir);
        var logicDll = Path.Combine(logicDir, "IronNestFCS.Logic.dll");

        MelonLogger.Msg($"IronNestFCS Smart Host Started. Logic path: {logicDll}");
        MelonLogger.Msg($"Press {ReloadKeyName} to hot reload TaskSystem.");

        // Diagnostic branch only: this probe lives in Host specifically so coordinate changes that happen
        // while Logic is unloaded/reloaded are still captured. It never mutates any Transform.
        _mapCoordinateDiag = new MapCoordinateDiagnosticProbe();
        MelonLogger.Msg($"[FCS DIAG HOST] map coordinate trace: {_mapCoordinateDiag.Path}");

        _reloader = new LogicReloader(
            logicDll,
            LogicTypeName,
            _hostServices);

        // Do not instantiate TaskSystem before the persistent physical runtime is bound. During process start
        // the game objects commonly do not exist yet; loading Logic at that point only creates a false failed
        // diagnostic session which immediately has to be replaced.
        ScheduleSceneBind(InitialBindDelaySeconds);
    }

    private static bool ReloadKeyPressed()
    {
        var kb = Keyboard.current;
        return kb != null && kb.f9Key.wasPressedThisFrame;
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _mapCoordinateDiag?.SceneChanged(buildIndex, sceneName);

        // A real scene transition invalidates both TaskSystem handles and persistent physical handles.
        // Stop TaskSystem immediately and perform one serialized delayed rebind instead of stacking coroutines.
        _reloader?.Unload();
        _hostServices.LoadingRuntime.OnSceneChanged();
        ScheduleSceneBind(SceneBindDelaySeconds);
    }

    private void ScheduleSceneBind(float delaySeconds)
    {
        _sceneBindPending = true;
        _nextBindAttemptAt = Time.unscaledTime + Math.Max(0f, delaySeconds);
    }

    private void TryActivateScene()
    {
        if (!_sceneBindPending || _reloader == null || Time.unscaledTime < _nextBindAttemptAt)
            return;

        if (!_hostServices.LoadingRuntime.IsBound && !_hostServices.LoadingRuntime.TryBindScene())
        {
            _nextBindAttemptAt = Time.unscaledTime + BindRetryDelaySeconds;
            return;
        }

        _mapCoordinateDiag?.Mark("scene-logic-activate-begin");
        var reloadOk = _reloader.Reload();
        _mapCoordinateDiag?.Mark($"scene-logic-activate-end ok={reloadOk}");
        if (reloadOk)
        {
            _sceneBindPending = false;
            return;
        }

        // Loading may already be ready while another scene-owned console is still appearing. Retry Logic only;
        // do not tear down a successfully bound persistent loader just because TaskSystem binding was early.
        _nextBindAttemptAt = Time.unscaledTime + BindRetryDelaySeconds;
    }

    public override void OnUpdate()
    {
        // Sample before every Host-owned state update. This runs even when reloadable Logic is absent.
        _mapCoordinateDiag?.Tick();

        // Run persistent physical ownership before deciding whether this frame reloads Logic.
        _hostServices.LoadingRuntime.Update();

        if (_reloader == null)
            return;

        if (_sceneBindPending)
        {
            TryActivateScene();
            return;
        }

        var keyReload = ReloadKeyPressed();
        var dllReload = !keyReload && _reloader.CheckDllUpdated();
        if (keyReload || dllReload)
        {
            var cause = keyReload ? ReloadKeyName : "dll-updated";
            MelonLogger.Msg($"[{ReloadKeyName}] Hot reloading TaskSystem; loading transactions stay alive.");
            _mapCoordinateDiag?.Mark($"logic-reload-begin cause={cause}");
            var ok = _reloader.Reload();
            _mapCoordinateDiag?.Mark($"logic-reload-end cause={cause} ok={ok}");
            return;
        }

        try { _reloader.Current?.Update(); }
        catch (Exception ex) { MelonLogger.Error($"Logic.Update() exception: {ex}"); }

        // A second sample catches synchronous Transform changes caused by this frame's Logic update.
        _mapCoordinateDiag?.Tick();
    }

    public override void OnGUI()
    {
        if (_reloader?.Current == null)
            return;

        try { _reloader.Current.OnGui(); }
        catch (Exception ex) { MelonLogger.Error($"Logic.OnGui() exception: {ex}"); }
    }

    public override void OnDeinitializeMelon()
    {
        _sceneBindPending = false;
        _mapCoordinateDiag?.Mark("host-deinitialize-begin");
        _reloader?.Unload();
        _reloader = null;
        _hostServices.LoadingRuntime.Dispose();
        _mapCoordinateDiag?.Mark("host-deinitialize-end");
        _mapCoordinateDiag?.Dispose();
        _mapCoordinateDiag = null;
    }

    private sealed class FcsHostServices : IFcsHostServices
    {
        internal PersistentLoadingSystem LoadingRuntime { get; } = new();
        public ILoadingSystem Loading => LoadingRuntime;
    }
}
