using System.Collections;
using IronNestFCS.Abstractions;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(IronNestFCS.FcsHostMod), "IronNestFCS", "1.1.0", "svr2kos2")]
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

    private readonly FcsHostServices _hostServices = new();
    private LogicReloader? _reloader;

    public override void OnInitializeMelon()
    {
        var logicDir = Path.Combine(
            MelonEnvironment.UserDataDirectory,
            "IronNestFCS");
        Directory.CreateDirectory(logicDir);
        var logicDll = Path.Combine(logicDir, "IronNestFCS.Logic.dll");

        MelonLogger.Msg($"IronNestFCS Host Started. Logic path: {logicDll}");
        MelonLogger.Msg($"Press {ReloadKeyName} to hot reload TaskSystem.");

        _hostServices.LoadingRuntime.TryBindScene();
        _reloader = new LogicReloader(
            logicDll,
            LogicTypeName,
            _hostServices);
        _reloader.Reload();
    }

    private static bool ReloadKeyPressed()
    {
        var kb = Keyboard.current;
        return kb != null && kb.f9Key.wasPressedThisFrame;
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        // Scene transitions really do invalidate physical handles. F9 does not.
        _hostServices.LoadingRuntime.OnSceneChanged();
        MelonCoroutines.Start(RebindSceneCoroutine());
    }

    private IEnumerator RebindSceneCoroutine()
    {
        yield return new WaitForSeconds(3f);
        _hostServices.LoadingRuntime.TryBindScene();
        _reloader?.Reload();
    }

    public override void OnUpdate()
    {
        // Run persistent physical ownership before deciding whether this frame reloads Logic.
        _hostServices.LoadingRuntime.Update();

        if (_reloader == null)
            return;

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
        if (_reloader?.Current == null)
            return;

        try { _reloader.Current.OnGui(); }
        catch (Exception ex) { MelonLogger.Error($"Logic.OnGui() exception: {ex}"); }
    }

    public override void OnDeinitializeMelon()
    {
        _reloader?.Unload();
        _reloader = null;
        _hostServices.LoadingRuntime.Dispose();
    }

    private sealed class FcsHostServices : IFcsHostServices
    {
        internal PersistentLoadingSystem LoadingRuntime { get; } = new();
        public ILoadingSystem Loading => LoadingRuntime;
    }
}
