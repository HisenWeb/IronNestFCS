using System.Collections;
using IronNestFCS.Logic.FCS;
using MelonLoader;

namespace IronNestFCS.Logic.Infrastructure;

/// <summary>
/// Owns serialization for the three physically distinct shared operator consoles.
/// Per-gun reload/elevation work and the shared turret lane live in other modules.
/// </summary>
internal sealed class SharedConsoleCoordinator {
    private const float PowderCheckInterval = 5f;
    private const int PowderReplenishThreshold = 6;

    private readonly FSC _fcs;

    public CoroutineLock Ballistic { get; } = new();
    public CoroutineLock Requisition { get; } = new();
    public CoroutineLock Trigger { get; } = new();

    public SharedConsoleCoordinator(FSC fcs) {
        _fcs = fcs;
    }

    public void Reset() {
        Ballistic.Reset();
        Requisition.Reset();
        Trigger.Reset();
    }

    /// <summary>
    /// F9 abandons the old task but the physical review switches/arming levers survive in the game scene.
    /// Reset those controls immediately after bind so the next firing solution starts from a known baseline.
    /// </summary>
    public IEnumerator ResetFireControlsAfterBind() {
        yield return FcsRuntimeClock.WaitUntilFocused();
        yield return Trigger.Acquire();
        try {
            yield return _fcs.TriggerConsole.PrepareForNewFireSolution(LeftRight.Left);
        }
        finally {
            Trigger.Release();
        }
    }

    /// <summary>
    /// A normal committed FirePlan stack resets the shared review/arming console only after its last Compared
    /// plan leaves the executor. This reset is serialized ahead of the next stack's trigger-console work.
    /// </summary>
    public IEnumerator ResetFireControlsAfterCommittedStack() {
        yield return FcsRuntimeClock.WaitUntilFocused();
        yield return Trigger.Acquire();
        try {
            yield return _fcs.TriggerConsole.ResetPhysicalFireControls("committed stack drained");
        }
        finally {
            Trigger.Release();
        }
    }

    public IEnumerator ReplenishPowderLoop() {
        while (true) {
            yield return FcsRuntimeClock.WaitForSeconds(PowderCheckInterval);
            yield return FcsRuntimeClock.WaitUntilFocused();

            var charges = Math.Min(_fcs.LeftGun.RemainingCharges(), _fcs.RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;

            MelonLogger.Msg(
                $"[FCS] AutoReplenish: powder charges {charges} < {PowderReplenishThreshold}, buying one");
            yield return Requisition.Acquire();
            try {
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _fcs.PurchaseDeck.BuyPowders();
            }
            finally {
                Requisition.Release();
            }
        }
    }
}
