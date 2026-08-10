using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using IronNestFCS.Abstractions;
using MelonLoader;

namespace IronNestFCS;

/// <summary>
/// Loads the reloadable TaskSystem assembly. Persistent physical services are owned by Host and passed
/// through IronNestFCS.Abstractions, so unloading this ALC never owns or stops a loading transaction.
/// </summary>
internal sealed class LogicReloader
{
    private readonly string _logicDllPath;
    private readonly string _logicTypeName;
    private readonly IFcsHostServices _hostServices;

    private AssemblyLoadContext? _alc;
    private IFcsModule? _current;
    private WeakReference? _alcWeakRef;
    private DateTime _lastWriteTime;

    public IFcsModule? Current => _current;

    public LogicReloader(
        string logicDllPath,
        string logicTypeName,
        IFcsHostServices hostServices)
    {
        _logicDllPath = logicDllPath;
        _logicTypeName = logicTypeName;
        _hostServices = hostServices;
    }

    public bool CheckDllUpdated()
    {
        return _current != null
               && _lastWriteTime != File.GetLastWriteTime(_logicDllPath);
    }

    public bool Reload()
    {
        Unload();

        if (!File.Exists(_logicDllPath))
        {
            MelonLogger.Error($"[Reload] Logic dll doesn't exist: {_logicDllPath}");
            return false;
        }

        try
        {
            _alc = new AssemblyLoadContext("IronNestFCS.Logic", isCollectible: true);
            _alcWeakRef = new WeakReference(_alc, trackResurrection: true);

            var bytes = File.ReadAllBytes(_logicDllPath);
            var pdbPath = Path.ChangeExtension(_logicDllPath, ".pdb");
            Assembly asm;
            using (var dllStream = new MemoryStream(bytes))
            {
                if (File.Exists(pdbPath))
                {
                    using var pdbStream = new MemoryStream(File.ReadAllBytes(pdbPath));
                    asm = _alc.LoadFromStream(dllStream, pdbStream);
                }
                else
                {
                    asm = _alc.LoadFromStream(dllStream);
                }
            }

            var type = asm.GetType(_logicTypeName);
            if (type == null)
            {
                MelonLogger.Error($"[Reload] Can't find type {_logicTypeName} in Logic assembly.");
                Unload();
                return false;
            }

            if (Activator.CreateInstance(type) is not IFcsModule module)
            {
                MelonLogger.Error($"[Reload] {_logicTypeName} doesn't implement IFcsModule");
                Unload();
                return false;
            }

            _current = module;
            _lastWriteTime = File.GetLastWriteTime(_logicDllPath);
            var ok = _current.Initialize(_hostServices);
            if (!ok)
            {
                MelonLogger.Warning("[Reload] Logic.Initialize() returned false.");
            }
            else
            {
                _lastWriteTime = File.GetLastWriteTime(_logicDllPath);
                MelonLogger.Msg("[Reload] Logic loaded and initialized successfully.");
            }

            return ok;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[Reload] Load Logic failed: {ex}");
            Unload();
            return false;
        }
    }

    public void Unload()
    {
        if (_current != null)
        {
            try { _current.Shutdown(); }
            catch (Exception ex) { MelonLogger.Error($"[Reload] Logic.Shutdown() exception: {ex}"); }
            _current = null;
        }

        if (_alc != null)
        {
            try { _alc.Unload(); }
            catch (Exception ex) { MelonLogger.Error($"[Reload] ALC.Unload() exception: {ex}"); }
            _alc = null;
        }

        CollectOldContext();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CollectOldContext()
    {
        for (var i = 0; i < 2 && _alcWeakRef is { IsAlive: true }; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
