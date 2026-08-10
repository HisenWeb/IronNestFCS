using System.Collections;
using Il2Cpp;
using IronNestFCS.Abstractions;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS;

/// <summary>
/// F9-persistent physical loading runtime. It lives in the stable Host assembly, owns its own
/// coroutines/click lifetime, and never receives task/target/aiming/fire decisions.
/// </summary>
internal sealed class PersistentLoadingSystem : ILoadingSystem, IDisposable
{
    private readonly PersistentGunLoader _left = new(GunSide.Left);
    private readonly PersistentGunLoader _right = new(GunSide.Right);

    public bool IsBound => _left.IsBound && _right.IsBound;

    public void OnSceneChanged()
    {
        _left.ResetForScene();
        _right.ResetForScene();
        PersistentPhysicalClicks.ReleaseAll("scene change");
        PersistentRuntimeClock.Reset();
    }

    public bool TryBindScene()
    {
        PersistentRuntimeClock.Reset();
        var left = _left.TryBind();
        var right = _right.TryBind();
        var ok = left && right;
        MelonLogger.Msg($"[FCS Loading] persistent loading bind {(ok ? "success" : "failed")}");
        return ok;
    }

    public void Update()
    {
        PersistentRuntimeClock.Update();
        _left.Reconcile();
        _right.Reconcile();
    }

    public LoadingSnapshot GetSnapshot(GunSide side) => Loader(side).Snapshot();

    public bool TryRequest(LoadRequest request, out string reason) =>
        Loader(request.Side).TryRequest(request, out reason);

    public void Dispose()
    {
        _left.ResetForScene();
        _right.ResetForScene();
        PersistentPhysicalClicks.ReleaseAll("host shutdown");
    }

    private PersistentGunLoader Loader(GunSide side) =>
        side == GunSide.Left ? _left : _right;
}

internal sealed class PersistentGunLoader
{
    private const float ReloadControlTimeoutSeconds = 60f;
    private const float ShellChamberTimeoutSeconds = 15f;
    private const float PowderControlResumeGraceSeconds = 2f;
    private const float PowderCommitTimeoutSeconds = 12f;
    private const float LoadingTimeoutSeconds = 60f;
    private const float RecoveryElevationVelocityTolerance = 0.05f;
    private const float FreshLoadReadySeconds = 32.25f;

    private readonly GunSide _side;
    private readonly string _sideName;
    private readonly List<string?> _bullets = new();
    private readonly List<LookAtTarget> _powderButtons = new();

    private CylinderShellSelector? _shellSelector;
    private LookAtTarget? _nextBulletButton;
    private LookAtTarget? _loadBulletButton;
    private LookAtTarget? _loadPowderButton;
    private GunController? _gunController;
    private ArtilleryReloadController? _reloadController;

    private object? _transactionCoroutine;
    private LoadingTransaction? _transaction;

    private bool _lastReloadReadySucceeded;
    private bool _lastReloadActionSucceeded;
    private string _lastReloadFailureReason = "";

    public bool IsBound { get; private set; }

    public PersistentGunLoader(GunSide side)
    {
        _side = side;
        _sideName = side == GunSide.Left ? "Left" : "Right";
    }

    public void ResetForScene()
    {
        if (_transactionCoroutine != null)
        {
            try { MelonCoroutines.Stop(_transactionCoroutine); }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[FCS Loading] {_sideName}: stop transaction failed: {ex.Message}");
            }
        }

        _transactionCoroutine = null;
        _transaction = null;
        _shellSelector = null;
        _nextBulletButton = null;
        _loadBulletButton = null;
        _loadPowderButton = null;
        _gunController = null;
        _reloadController = null;
        _powderButtons.Clear();
        _bullets.Clear();
        IsBound = false;
    }

    public bool TryBind()
    {
        ResetForScene();

        var gunSystemObject = GameObject.Find("Gun System " + _sideName);
        if (gunSystemObject == null)
        {
            MelonLogger.Warning($"[FCS Loading] {_sideName}: Gun System not found");
            return false;
        }

        var gunSystem = gunSystemObject.transform;
        var reloadingConsole = gunSystem.Find("--Reloading Console");
        if (reloadingConsole == null)
            return false;

        _shellSelector = gunSystem.GetComponentInChildren<CylinderShellSelector>();
        _nextBulletButton = reloadingConsole.Find("Universal Button Move Cylinder")?.GetComponent<LookAtTarget>();
        _loadBulletButton = reloadingConsole.FindChild("Universal Button Load shell Rammer")?.GetComponent<LookAtTarget>();

        var powderController = reloadingConsole.Find("PowderChargeController");
        if (powderController != null)
        {
            for (var i = 0; i < powderController.childCount; i++)
            {
                var child = powderController.GetChild(i);
                if (!child.name.StartsWith("Button Dispencer"))
                    continue;
                var button = child.GetComponent<LookAtTarget>();
                if (button != null)
                    _powderButtons.Add(button);
            }
        }

        _loadPowderButton = reloadingConsole.FindChild("Universal Button Charge Rammer (1)")?.GetComponent<LookAtTarget>();
        _gunController = GameObject.Find("Gun" + _sideName)?.GetComponent<GunController>();
        _reloadController = _gunController?.artilleryReloadController;

        IsBound = _shellSelector != null
                  && _nextBulletButton != null
                  && _loadBulletButton != null
                  && _powderButtons.Count >= 6
                  && _loadPowderButton != null
                  && _gunController != null;

        if (!IsBound)
            MelonLogger.Warning($"[FCS Loading] {_sideName}: one or more reload controls are unbound");
        else
            MelonLogger.Msg($"[FCS Loading] {_sideName}: persistent loader bound");

        return IsBound;
    }

    public void Reconcile()
    {
        if (!IsBound || _transaction == null)
            return;

        var physical = PersistentGunPhysicalState.Read(_sideName);

        if (_transaction.State == LoadingTransactionState.LoadedReady)
        {
            if (!physical.Matches(_transaction.Request))
            {
                MelonLogger.Msg(
                    $"[FCS Loading] {_sideName}: completed transaction released after physical round changed; {physical.Summary()}");
                _transaction = null;
                _transactionCoroutine = null;
            }
            return;
        }

        if (_transaction.State == LoadingTransactionState.Failed)
            return;

        if (physical.Matches(_transaction.Request))
        {
            _transaction.State = LoadingTransactionState.LoadedReady;
            _transaction.FailureReason = "";
            MelonLogger.Msg($"[FCS Loading] {_sideName}: transaction reconciled as LoadedReady {_transaction.Request}");
        }
    }

    public LoadingSnapshot Snapshot()
    {
        var physical = PersistentGunPhysicalState.Read(_sideName);
        var tx = _transaction;

        float? remaining = null;
        if (tx != null)
        {
            if (tx.State == LoadingTransactionState.LoadedReady)
                remaining = 0f;
            else if (tx.EstimatedTotalSeconds.HasValue && tx.State != LoadingTransactionState.Failed)
                remaining = Mathf.Max(0f, tx.EstimatedTotalSeconds.Value - (PersistentRuntimeClock.Now - tx.StartedAt));
        }

        return new LoadingSnapshot
        {
            Side = _side,
            IsBound = physical.IsBound,
            PhysicalState = physical.PublicKind,
            ActualShell = physical.ShellType,
            ActualCharge = physical.PowderCharges,
            HasTransaction = tx != null,
            TransactionState = tx?.State ?? LoadingTransactionState.Idle,
            RequestedShell = tx?.Request.Shell,
            RequestedCharge = tx?.Request.Charge ?? 0,
            EstimatedRemainingSeconds = remaining,
            FailureReason = tx?.FailureReason ?? "",
        };
    }

    public bool TryRequest(LoadRequest request, out string reason)
    {
        reason = "";
        if (!IsBound)
        {
            reason = $"{_sideName} persistent loader is not bound";
            return false;
        }

        if (request.Shell == ShellTypeCode.None || request.Charge < 1 || request.Charge > 6)
        {
            reason = $"invalid load request {request}";
            return false;
        }

        var physical = PersistentGunPhysicalState.Read(_sideName);

        if (_transaction != null)
        {
            if (_transaction.Request.SameTarget(request) && _transaction.State != LoadingTransactionState.Failed)
                return true;

            if (_transaction.State != LoadingTransactionState.Failed
                && _transaction.State != LoadingTransactionState.LoadedReady)
            {
                reason = $"{_sideName} already owns accepted transaction {_transaction.Request} ({_transaction.State}); refusing overwrite with {request}";
                return false;
            }

            if (_transaction.State == LoadingTransactionState.LoadedReady && physical.Matches(_transaction.Request))
            {
                reason = $"{_sideName} completed round {_transaction.Request} is still physically present; refusing overwrite with {request}";
                return false;
            }

            _transaction = null;
            _transactionCoroutine = null;
        }

        if (physical.Kind == PersistentGunPhysicalStateKind.LoadedReady)
        {
            if (!physical.Matches(request))
            {
                reason = $"{_sideName} already physically loaded {physical.ShellType}/C{physical.PowderCharges}";
                return false;
            }

            _transaction = new LoadingTransaction(request, PersistentRuntimeClock.Now, 0f)
            {
                State = LoadingTransactionState.LoadedReady,
            };
            return true;
        }

        PersistentGunPhysicalStateKind startKind;
        float? estimatedTotal;
        if (physical.Kind == PersistentGunPhysicalStateKind.EmptyReady)
        {
            startKind = PersistentGunPhysicalStateKind.EmptyReady;
            estimatedTotal = FreshLoadReadySeconds;
        }
        else if (physical.Kind == PersistentGunPhysicalStateKind.ShellLoaded && physical.ShellType == request.Shell)
        {
            startKind = PersistentGunPhysicalStateKind.ShellLoaded;
            estimatedTotal = null;
        }
        else
        {
            reason = $"{_sideName} cannot accept {request} from physical state {physical.Summary()}";
            return false;
        }

        _transaction = new LoadingTransaction(request, PersistentRuntimeClock.Now, estimatedTotal);
        _transactionCoroutine = MelonCoroutines.Start(RunTransaction(_transaction, startKind));
        MelonLogger.Msg($"[FCS Loading] {_sideName}: accepted {_transaction.Request}; start={physical.Summary()}");
        return true;
    }

    private IEnumerator RunTransaction(LoadingTransaction tx, PersistentGunPhysicalStateKind startKind)
    {
        yield return PersistentRuntimeClock.WaitUntilFocused();

        if (startKind == PersistentGunPhysicalStateKind.EmptyReady)
        {
            tx.State = LoadingTransactionState.LoadingShell;
            yield return LoadBullet(tx.Request.Shell);
            if (!_lastReloadActionSucceeded)
            {
                Fail(tx, _lastReloadFailureReason);
                yield break;
            }
        }

        var beforePowder = PersistentGunPhysicalState.Read(_sideName);
        if (beforePowder.Matches(tx.Request))
        {
            tx.State = LoadingTransactionState.LoadedReady;
            yield break;
        }

        if (beforePowder.Kind != PersistentGunPhysicalStateKind.ShellLoaded || beforePowder.ShellType != tx.Request.Shell)
        {
            Fail(tx, $"expected shell-loaded {tx.Request.Shell}, got {beforePowder.Summary()}");
            yield break;
        }

        tx.State = LoadingTransactionState.LoadingPowder;
        yield return LoadPowder(tx.Request.Charge);
        if (!_lastReloadActionSucceeded)
        {
            Fail(tx, _lastReloadFailureReason);
            yield break;
        }

        tx.State = LoadingTransactionState.WaitingLoadedReady;
        var deadline = PersistentRuntimeClock.Now + LoadingTimeoutSeconds;
        while (true)
        {
            yield return PersistentRuntimeClock.WaitUntilFocused();
            var loaded = PersistentGunPhysicalState.Read(_sideName);
            if (loaded.Matches(tx.Request))
            {
                tx.State = LoadingTransactionState.LoadedReady;
                tx.FailureReason = "";
                MelonLogger.Msg($"[FCS Loading] {_sideName}: LoadedReady {tx.Request}");
                yield break;
            }

            if (loaded.Kind == PersistentGunPhysicalStateKind.LoadedReady && !loaded.Matches(tx.Request))
            {
                Fail(tx, $"loaded round mismatch for {tx.Request}; physical={loaded.Summary()}");
                yield break;
            }

            if (PersistentRuntimeClock.Now >= deadline)
            {
                Fail(tx, $"loading did not converge to {tx.Request}; physical={loaded.Summary()}");
                yield break;
            }

            yield return PersistentRuntimeClock.WaitForSeconds(0.25f);
        }
    }

    private void Fail(LoadingTransaction tx, string reason)
    {
        if (!ReferenceEquals(_transaction, tx))
            return;

        tx.State = LoadingTransactionState.Failed;
        tx.FailureReason = string.IsNullOrWhiteSpace(reason) ? "persistent loading failed" : reason;
        MelonLogger.Error($"[FCS Loading] {_sideName}: {tx.FailureReason}");
    }

    private static string NormalizeShellId(string? id) => id == "PCLM" ? "PLCM" : id ?? "";

    private string BulletInChamber() =>
        NormalizeShellId(_gunController?.ChamberedShellBlueprint?.shellDefinition?.ShellId);

    private void RefreshBullets()
    {
        _bullets.Clear();
        if (_shellSelector == null)
            return;

        foreach (var shell in _shellSelector.bullets)
        {
            var id = NormalizeShellId(shell?.GetComponent<ShellBlueprint>()?.shellDefinition?.ShellId);
            _bullets.Add(string.IsNullOrEmpty(id) ? null : id);
        }
    }

    private void FailReloadAction(string reason)
    {
        _lastReloadActionSucceeded = false;
        _lastReloadFailureReason = reason;
        MelonLogger.Error($"[FCS Loading] {_sideName}: {reason}");
    }

    private IEnumerator WaitForReloadReady(PersistentGunPhysicalStateKind expectedStableKind, float timeoutSeconds)
    {
        _lastReloadReadySucceeded = false;
        if (_gunController == null)
            yield break;

        var deadline = PersistentRuntimeClock.Now + Mathf.Max(1f, timeoutSeconds);
        while (true)
        {
            yield return PersistentRuntimeClock.WaitUntilFocused();
            var physical = PersistentGunPhysicalState.Read(_sideName);
            var interactionReady = _reloadController == null
                ? !_gunController.ExternalReloadLoweringLocked
                : physical.Kind == expectedStableKind;
            var breechReady = !_gunController.ExternalReloadLoweringLocked;
            var motionReady = Mathf.Abs(_gunController.elevationChangeVelocity) <= RecoveryElevationVelocityTolerance;

            if (interactionReady && breechReady && motionReady)
                break;

            if (PersistentRuntimeClock.Now >= deadline)
            {
                MelonLogger.Error($"[FCS Loading] {_sideName}: reload handoff timeout {expectedStableKind}; {physical.Summary()}");
                yield break;
            }

            yield return PersistentRuntimeClock.WaitForSeconds(0.25f);
        }

        yield return PersistentRuntimeClock.WaitForSeconds(0.5f);
        _lastReloadReadySucceeded = true;
    }

    private IEnumerator ClickReloadControl(LookAtTarget? button, string controlName, float timeoutSeconds = ReloadControlTimeoutSeconds)
    {
        _lastReloadActionSucceeded = false;
        _lastReloadFailureReason = "";
        if (button == null)
        {
            FailReloadAction($"reload control missing: {controlName}");
            yield break;
        }

        var deadline = PersistentRuntimeClock.Now + Mathf.Max(0.1f, timeoutSeconds);
        while (true)
        {
            yield return PersistentRuntimeClock.WaitUntilFocused();
            if (button.isActive && button.nextAllowedClickTime <= Time.realtimeSinceStartup)
                break;

            if (PersistentRuntimeClock.Now >= deadline)
            {
                FailReloadAction($"reload control timed out: {controlName}");
                yield break;
            }

            yield return PersistentRuntimeClock.WaitForSeconds(0.1f);
        }

        yield return PersistentRuntimeClock.WaitForSeconds(0.1f);
        try { PersistentPhysicalClicks.Begin(button); }
        catch (Exception ex)
        {
            FailReloadAction($"reload click-down failed ({controlName}): {ex.Message}");
            yield break;
        }

        yield return new WaitForSeconds(0.1f);
        try { PersistentPhysicalClicks.End(button); }
        catch (Exception ex)
        {
            FailReloadAction($"reload click-up failed ({controlName}): {ex.Message}");
            yield break;
        }

        _lastReloadActionSucceeded = true;
    }

    private IEnumerator WaitForChamberedShell(ShellTypeCode type, float timeoutSeconds = ShellChamberTimeoutSeconds)
    {
        _lastReloadActionSucceeded = false;
        var expected = type.ToString();
        var deadline = PersistentRuntimeClock.Now + Mathf.Max(1f, timeoutSeconds);

        while (true)
        {
            yield return PersistentRuntimeClock.WaitUntilFocused();
            var chamber = BulletInChamber();
            if (chamber == expected)
            {
                _lastReloadActionSucceeded = true;
                yield break;
            }

            if (PersistentRuntimeClock.Now >= deadline)
            {
                FailReloadAction($"shell rammer did not chamber {expected}; chamber={chamber}");
                yield break;
            }

            yield return PersistentRuntimeClock.WaitForSeconds(0.1f);
        }
    }

    private IEnumerator LoadBullet(ShellTypeCode type)
    {
        _lastReloadActionSucceeded = false;
        _lastReloadFailureReason = "";

        RefreshBullets();
        var expected = type.ToString();
        if (_bullets.Count == 0 || !_bullets.Contains(expected))
        {
            FailReloadAction($"No {type} available in cylinder");
            yield break;
        }

        for (var i = 0; i < _bullets.Count; i++)
        {
            yield return PersistentRuntimeClock.WaitUntilFocused();
            if (_bullets.Count > 0 && _bullets[0] == expected)
                break;

            yield return ClickReloadControl(_nextBulletButton, "Universal Button Move Cylinder", 10f);
            if (!_lastReloadActionSucceeded)
                yield break;

            yield return PersistentRuntimeClock.WaitForSeconds(1.5f);
            RefreshBullets();
        }

        if (_bullets.Count == 0 || _bullets[0] != expected)
        {
            FailReloadAction($"Can't align cylinder to {type}");
            yield break;
        }

        yield return WaitForReloadReady(PersistentGunPhysicalStateKind.EmptyReady, ReloadControlTimeoutSeconds);
        if (!_lastReloadReadySucceeded)
        {
            FailReloadAction("reload mechanism was not ready after cylinder positioning");
            yield break;
        }

        yield return ClickReloadControl(_loadBulletButton, "Universal Button Load shell Rammer");
        if (!_lastReloadActionSucceeded)
            yield break;

        yield return WaitForChamberedShell(type);
        if (!_lastReloadActionSucceeded)
            yield break;

        yield return WaitForReloadReady(PersistentGunPhysicalStateKind.ShellLoaded, ReloadControlTimeoutSeconds);
        if (!_lastReloadReadySucceeded)
        {
            FailReloadAction("reload mechanism did not settle after shell ramming");
            yield break;
        }

        _lastReloadActionSucceeded = true;
    }

    private string PowderControlsSummary(int requiredCount)
    {
        var count = Math.Min(requiredCount, _powderButtons.Count);
        var states = new List<string>();
        for (var i = 0; i < count; i++)
            states.Add($"{i + 1}:{(_powderButtons[i].isActive ? "A" : "I")}");
        return $"rammer={(_loadPowderButton?.isActive == true ? "A" : "I")}, required=[{string.Join(",", states)}]";
    }

    private IEnumerator WaitForPowderCommit(int expectedCount, string shellAtStart)
    {
        _lastReloadActionSucceeded = false;
        var deadline = PersistentRuntimeClock.Now + PowderCommitTimeoutSeconds;

        while (true)
        {
            yield return PersistentRuntimeClock.WaitUntilFocused();
            var physical = PersistentGunPhysicalState.Read(_sideName);

            if (physical.PowderCharges > 0)
            {
                if (physical.PowderCharges != expectedCount)
                {
                    FailReloadAction($"powder commit mismatch: expected C{expectedCount}, physical C{physical.PowderCharges}; {physical.Summary()}");
                    yield break;
                }

                var shellNow = physical.ShellId;
                if (!string.IsNullOrEmpty(shellAtStart) && !string.IsNullOrEmpty(shellNow) && shellNow != shellAtStart)
                {
                    FailReloadAction($"shell changed while committing powder: expected {shellAtStart}, got {shellNow}");
                    yield break;
                }

                _lastReloadActionSucceeded = true;
                yield break;
            }

            if (physical.Kind == PersistentGunPhysicalStateKind.EmptyReady || physical.Kind == PersistentGunPhysicalStateKind.PostShotRecovery)
            {
                FailReloadAction($"powder commit lost chambered shell; {physical.Summary()}");
                yield break;
            }

            if (PersistentRuntimeClock.Now >= deadline)
            {
                FailReloadAction($"powder rammer did not commit C{expectedCount}; {physical.Summary()}, {PowderControlsSummary(expectedCount)}");
                yield break;
            }

            yield return PersistentRuntimeClock.WaitForSeconds(0.1f);
        }
    }

    private IEnumerator LoadPowder(int count)
    {
        _lastReloadActionSucceeded = false;
        _lastReloadFailureReason = "";

        if (count <= 0 || count > _powderButtons.Count)
        {
            FailReloadAction($"invalid powder count {count}");
            yield break;
        }

        var startState = PersistentGunPhysicalState.Read(_sideName);
        if (startState.Kind != PersistentGunPhysicalStateKind.ShellLoaded)
        {
            FailReloadAction($"powder loading requires ShellLoaded; {startState.Summary()}");
            yield break;
        }

        var shellAtStart = startState.ShellId;

        // Proven recovery rule: if the hidden tray already contains staged powder, never replay
        // dispenser clicks. Commit once and let durable PowderCharges prove the actual count.
        var stagedBeforeSelection = _loadPowderButton?.isActive == true;
        if (stagedBeforeSelection)
        {
            var anyRequiredInactive = false;
            for (var i = 0; i < count; i++)
            {
                if (_powderButtons[i].isActive)
                    continue;
                anyRequiredInactive = true;
                break;
            }
            stagedBeforeSelection = anyRequiredInactive;
        }

        if (!stagedBeforeSelection)
        {
            for (var i = 0; i < count; i++)
            {
                yield return PersistentRuntimeClock.WaitUntilFocused();

                var physical = PersistentGunPhysicalState.Read(_sideName);
                if (physical.PowderCharges > 0)
                    break;

                if (physical.Kind != PersistentGunPhysicalStateKind.ShellLoaded)
                {
                    FailReloadAction($"reload state left ShellLoaded during powder selection; {physical.Summary()}");
                    yield break;
                }

                var button = _powderButtons[i];
                if (!button.isActive)
                {
                    var resumeDeadline = PersistentRuntimeClock.Now + PowderControlResumeGraceSeconds;
                    while (!button.isActive && _loadPowderButton?.isActive != true && PersistentRuntimeClock.Now < resumeDeadline)
                        yield return PersistentRuntimeClock.WaitForSeconds(0.1f);

                    if (!button.isActive)
                    {
                        if (_loadPowderButton?.isActive == true)
                            break;

                        FailReloadAction($"required powder dispenser {i + 1} inactive; {PowderControlsSummary(count)}");
                        yield break;
                    }
                }

                yield return ClickReloadControl(button, $"Button Dispencer ({i + 1})", 10f);
                if (!_lastReloadActionSucceeded)
                    yield break;
            }
        }

        var beforeRam = PersistentGunPhysicalState.Read(_sideName);
        if (beforeRam.PowderCharges == 0)
        {
            if (beforeRam.Kind != PersistentGunPhysicalStateKind.ShellLoaded)
            {
                FailReloadAction($"powder state changed before charge rammer; {beforeRam.Summary()}");
                yield break;
            }

            yield return ClickReloadControl(_loadPowderButton, "Universal Button Charge Rammer (1)", 10f);
            if (!_lastReloadActionSucceeded)
                yield break;
        }

        yield return WaitForPowderCommit(count, shellAtStart);
    }

    private sealed class LoadingTransaction
    {
        public LoadRequest Request { get; }
        public float StartedAt { get; }
        public float? EstimatedTotalSeconds { get; }
        public LoadingTransactionState State;
        public string FailureReason = "";

        public LoadingTransaction(LoadRequest request, float startedAt, float? estimatedTotalSeconds)
        {
            Request = request;
            StartedAt = startedAt;
            EstimatedTotalSeconds = estimatedTotalSeconds;
            State = LoadingTransactionState.Idle;
        }
    }
}

internal enum PersistentGunPhysicalStateKind
{
    Unbound,
    EmptyReady,
    ShellLoaded,
    LoadedReady,
    Recovering,
    PostShotRecovery,
    Unknown,
}

internal sealed class PersistentGunPhysicalState
{
    public bool IsBound;
    public PersistentGunPhysicalStateKind Kind;
    public string ShellId = "";
    public ShellTypeCode? ShellType;
    public int PowderCharges;
    public bool CanFire;
    public bool IsReloading;
    public bool PendingReload;
    public bool ReloadWorking;
    public bool BreechLocked;
    public int ReloadStateIndex = -1;
    public string ReloadStateKey = "unknown";

    public LoadingPhysicalState PublicKind => Kind switch
    {
        PersistentGunPhysicalStateKind.EmptyReady => LoadingPhysicalState.EmptyReady,
        PersistentGunPhysicalStateKind.ShellLoaded => LoadingPhysicalState.ShellLoaded,
        PersistentGunPhysicalStateKind.LoadedReady => LoadingPhysicalState.LoadedReady,
        PersistentGunPhysicalStateKind.Recovering => LoadingPhysicalState.Recovering,
        PersistentGunPhysicalStateKind.PostShotRecovery => LoadingPhysicalState.PostShotRecovery,
        PersistentGunPhysicalStateKind.Unknown => LoadingPhysicalState.Unknown,
        _ => LoadingPhysicalState.Unbound,
    };

    public bool Matches(LoadRequest request) =>
        Kind == PersistentGunPhysicalStateKind.LoadedReady
        && ShellType == request.Shell
        && PowderCharges == request.Charge;

    public static PersistentGunPhysicalState Read(string side)
    {
        var state = new PersistentGunPhysicalState();
        try
        {
            var gun = GameObject.Find("Gun" + side)?.GetComponent<GunController>();
            if (gun == null)
                return state;

            state.IsBound = true;
            state.ShellId = NormalizeShellId(gun.ChamberedShellBlueprint?.shellDefinition?.ShellId);
            state.PowderCharges = gun.PowderCharges;
            state.CanFire = gun.CanFire;
            state.IsReloading = gun.IsReloading;
            state.PendingReload = gun.pendingReload;
            state.BreechLocked = gun.ExternalReloadLoweringLocked;

            if (!string.IsNullOrEmpty(state.ShellId) && Enum.TryParse<LoadingShellName>(state.ShellId, true, out var parsed))
                state.ShellType = (ShellTypeCode)(int)parsed;

            var reload = gun.artilleryReloadController;
            if (reload != null)
            {
                state.ReloadWorking = reload.working;
                state.ReloadStateIndex = reload.CurrentStateIndex;
                try
                {
                    var current = reload.CurrentState;
                    if (current != null)
                        state.ReloadStateKey = current.stateKey ?? "unknown";
                }
                catch { }
            }

            state.Kind = Classify(state);
        }
        catch
        {
            state.IsBound = false;
            state.Kind = PersistentGunPhysicalStateKind.Unbound;
        }

        return state;
    }

    private static string NormalizeShellId(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return "";
        return id == "PCLM" ? "PLCM" : id;
    }

    private static bool AtState(PersistentGunPhysicalState state, int index, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.Equals(state.ReloadStateKey, key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return state.ReloadStateIndex == index;
    }

    private static PersistentGunPhysicalStateKind Classify(PersistentGunPhysicalState state)
    {
        if (!state.IsBound)
            return PersistentGunPhysicalStateKind.Unbound;

        var atLocked = AtState(state, 0, "BreachLocked", "BreechLocked");
        var atUnlocking = AtState(state, 1, "BreachUnlocking", "BreechUnlocking");
        var atGuideDeploy = AtState(state, 2, "GuideDeploy");
        var atBreechOpen = AtState(state, 3, "BreechOpen", "BreachOpen");
        var atShellRamming = AtState(state, 4, "ShellRamming");
        var atSelectPowder = AtState(state, 5, "SelectPowderCharge");
        var atRamCharges = AtState(state, 6, "RamCharges");
        var atCloseGuide = AtState(state, 7, "CloseShellGuide");
        var atFinalSequence = AtState(state, 8, "FinalSequence");
        var atDone = AtState(state, 9, "Done");

        if (state.ReloadWorking || state.BreechLocked)
            return PersistentGunPhysicalStateKind.Recovering;

        if (string.IsNullOrEmpty(state.ShellId) && state.PowderCharges == 0)
        {
            if (atBreechOpen)
                return PersistentGunPhysicalStateKind.EmptyReady;
            if (atLocked || atUnlocking || atGuideDeploy || state.IsReloading || state.PendingReload)
                return PersistentGunPhysicalStateKind.PostShotRecovery;
            return PersistentGunPhysicalStateKind.Unknown;
        }

        if (state.ShellType.HasValue && state.PowderCharges == 0)
        {
            if (atSelectPowder)
                return PersistentGunPhysicalStateKind.ShellLoaded;
            if (atShellRamming || atGuideDeploy || atBreechOpen)
                return PersistentGunPhysicalStateKind.Recovering;
            return PersistentGunPhysicalStateKind.Unknown;
        }

        if (state.ShellType.HasValue && state.PowderCharges > 0 && state.PowderCharges <= 6)
        {
            if (atLocked && state.CanFire && !state.IsReloading && !state.PendingReload)
                return PersistentGunPhysicalStateKind.LoadedReady;
            if (atRamCharges || atCloseGuide || atFinalSequence || atDone || atLocked)
                return PersistentGunPhysicalStateKind.Recovering;
            return PersistentGunPhysicalStateKind.Unknown;
        }

        return PersistentGunPhysicalStateKind.Unknown;
    }

    public string Summary()
    {
        var shell = ShellType?.ToString() ?? (string.IsNullOrEmpty(ShellId) ? "empty" : ShellId);
        return $"{Kind} chamber={shell} C{PowderCharges} state={ReloadStateIndex}/{ReloadStateKey}";
    }

    private enum LoadingShellName
    {
        AP = 1, APHE = 2, ATMC = 3, CLMN = 4, CYAN = 5, DRIL = 6, EQKE = 7, FLCH = 8,
        HCHE = 9, HE = 10, INCN = 11, LE = 12, PLCM = 13, PHGN = 14, PRPG = 15, SMK = 16,
        STAR = 17, TEAR = 18, THRM = 19, WP = 20,
    }
}

internal static class PersistentPhysicalClicks
{
    private static readonly List<LookAtTarget> Held = new();

    public static void Begin(LookAtTarget button)
    {
        button.OnClickDown();
        if (!Held.Contains(button))
            Held.Add(button);
    }

    public static void End(LookAtTarget button)
    {
        try { button.OnClickUp(); }
        finally { Held.Remove(button); }
    }

    public static void ReleaseAll(string reason)
    {
        if (Held.Count == 0)
            return;

        var held = Held.ToArray();
        Held.Clear();
        foreach (var button in held)
        {
            try
            {
                button.OnClickUp();
                MelonLogger.Warning($"[FCS Loading] released persistent held click during {reason}: {button.gameObject.name}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[FCS Loading] failed to release held click during {reason}: {ex.Message}");
            }
        }
    }
}

internal static class PersistentRuntimeClock
{
    private const float FocusResumeSettleSeconds = 0.25f;

    private static bool _initialized;
    private static bool _wasFocused;
    private static float _lastGameTime;
    private static float _activeTime;
    private static float _resumeNotBeforeRealtime;

    public static float Now
    {
        get { Sync(); return _activeTime; }
    }

    public static void Reset()
    {
        _initialized = true;
        _wasFocused = Application.isFocused;
        _lastGameTime = Time.time;
        _activeTime = 0f;
        _resumeNotBeforeRealtime = Application.isFocused ? Time.realtimeSinceStartup : float.PositiveInfinity;
    }

    public static void Update() => Sync();

    public static IEnumerator WaitUntilFocused()
    {
        while (true)
        {
            Sync();
            if (Application.isFocused && Time.realtimeSinceStartup >= _resumeNotBeforeRealtime)
                yield break;
            yield return null;
        }
    }

    public static IEnumerator WaitForSeconds(float seconds)
    {
        var deadline = Now + Mathf.Max(0f, seconds);
        while (Now < deadline)
            yield return null;
    }

    private static void Sync()
    {
        var gameNow = Time.time;
        var realtimeNow = Time.realtimeSinceStartup;
        var focused = Application.isFocused;

        if (!_initialized)
        {
            Reset();
            return;
        }

        if (_wasFocused && realtimeNow >= _resumeNotBeforeRealtime)
            _activeTime += Mathf.Max(0f, gameNow - _lastGameTime);

        _lastGameTime = gameNow;
        if (focused == _wasFocused)
            return;

        _resumeNotBeforeRealtime = focused ? realtimeNow + FocusResumeSettleSeconds : float.PositiveInfinity;
        _wasFocused = focused;
    }
}
