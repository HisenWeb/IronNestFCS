using IronNestFCS.Abstractions;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(IronNestFCS.FcsHostMod), "IronNestFCS Smart", "1.2.0", "svr2kos2")]
[assembly: MelonGame()]

namespace IronNestFCS;

/// <summary>
/// Stable Host. F9 reloads TaskSystem only; PersistentLoadingSystem remains alive and continues any
/// already-accepted physical loading transaction.
/// </summary>
public class FcsHostMod : MelonMod
{
    private const string ReloadKeyName = "F9";
    private const string HudToggleKeyName = "F8";
    private const string LogicTypeName = "IronNestFCS.Logic.FcsModule";
    private const float InitialBindDelaySeconds = 1f;
    private const float SceneBindDelaySeconds = 3f;
    private const float BindRetryDelaySeconds = 1f;

    private readonly FcsHostServices _hostServices = new();
    private LogicReloader? _reloader;
    private bool _sceneBindPending;
    private float _nextBindAttemptAt;
    private bool _shutdownStarted;
    private bool _hudHidden;

    public override void OnInitializeMelon()
    {
        var logicDir = Path.Combine(
            MelonEnvironment.UserDataDirectory,
            "IronNestFCS");
        Directory.CreateDirectory(logicDir);
        var logicDll = Path.Combine(logicDir, "IronNestFCS.Logic.dll");

        MelonLogger.Msg($"IronNestFCS Smart Host Started. Logic path: {logicDll}");
        MelonLogger.Msg($"Press {ReloadKeyName} to hot reload TaskSystem.");

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

    private static bool HudToggleKeyPressed()
    {
        var kb = Keyboard.current;
        return kb != null && kb.f8Key.wasPressedThisFrame;
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
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

        if (_reloader.Reload())
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
        // Run persistent physical ownership before deciding whether this frame reloads Logic.
        _hostServices.LoadingRuntime.Update();

        if (HudToggleKeyPressed())
            _hudHidden = !_hudHidden;

        if (_reloader == null)
            return;

        if (_sceneBindPending)
        {
            TryActivateScene();
            return;
        }

        if (ReloadKeyPressed() || _reloader.CheckDllUpdated())
        {
            MelonLogger.Msg($"[{ReloadKeyName}] Hot reloading TaskSystem; loading transactions stay alive.");
            _reloader.Reload();
            return;
        }

        try { _reloader.Current?.Update(); }
        catch (Exception ex) { MelonLogger.Error($"Logic.Update() exception: {ex}"); }
    }

    public override void OnGUI()
    {
        if (_hudHidden || _reloader?.Current == null)
            return;

        try { _reloader.Current.OnGui(); }
        catch (Exception ex) { MelonLogger.Error($"Logic.OnGui() exception: {ex}"); }
    }

    private void ShutdownRuntime()
    {
        if (_shutdownStarted)
            return;

        _shutdownStarted = true;
        _sceneBindPending = false;

        // OnApplicationQuit runs before Unity tears down scene objects. Release TaskSystem and persistent
        // physical-click ownership here while LookAtTarget components are still valid. OnDeinitializeMelon
        // remains a fallback for non-application unload paths and is guarded against double disposal.
        _reloader?.Unload();
        _reloader = null;
        _hostServices.LoadingRuntime.Dispose();
    }

    public override void OnApplicationQuit()
    {
        ShutdownRuntime();
    }

    public override void OnDeinitializeMelon()
    {
        ShutdownRuntime();
    }

    private sealed class FcsHostServices : IFcsHostServices
    {
        internal PersistentLoadingSystem LoadingRuntime { get; } = new();
        public ILoadingSystem Loading => LoadingRuntime;
    }
}
