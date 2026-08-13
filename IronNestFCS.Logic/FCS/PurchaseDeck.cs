using Il2Cpp;
using MelonLoader;
using UnityEngine;
using System.Collections;
using static System.Enum;

namespace IronNestFCS.Logic.FCS;

public class PurchaseDeck {
    private Transform? _powderCard;
    private Dictionary<BulletType, Transform> bulletCards = new();
    private LookAtTarget? _buyButton;

    public IReadOnlyCollection<BulletType> AvailableBulletTypes => bulletCards.Keys;

    public bool HasShell(BulletType type) => bulletCards.ContainsKey(type);
    
    public bool TryBind() {
        _powderCard = null;
        bulletCards.Clear();
        _buyButton = null;

        var requisitionConsole = GameObject.Find("Requisition Console").transform;
        var cards = requisitionConsole.GetComponentsInChildren<PunchcardRuntime>();
        foreach (var card in cards) {
            MelonLogger.Msg($"[FCS] PurchaseDeck: Found card {card.CurrentDefinition.ID}");
            if (TryParse(
                    card.CurrentDefinition.ID
                        .Replace("SMOKE", "SMK")
                        .Replace("PCLM", "PLCM")
                        .Replace("Shell", ""),
                    out BulletType type
                )) {
                bulletCards[type] = card.transform;
            }
            else if (card.CurrentDefinition.ID == "PowderCharges") {
                _powderCard = card.transform;
            }
        }
        _buyButton = requisitionConsole.FindChild("Universal Button").GetComponent<LookAtTarget>();
        MelonLogger.Msg($"[FCS] PurchaseDeck: Available shell types = {bulletCards.Count}");
        return true;
    }
    
    private DialInteractable GetLeftRightDial() {
        var consoleBox = GameObject.Find("Console Box").transform;
        return consoleBox.GetComponentInChildren<DialInteractable>();
    }

    public IEnumerator BuyShell(BulletType type, LeftRight leftRight) {
        var card = bulletCards.GetValueOrDefault(type);
        if (card == null) {
            MelonLogger.Error($"[FCS] BuyShell: Can't find {type} card");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        var target = new Vector3(6.4814f, -2.4675f, -22.0968f);
        card.position = target;
        card.GetComponent<DraggableItem>().MoveToSlot();
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
        yield return FcsRuntimeClock.WaitUntilFocused();
        
        switch (leftRight) {
            case LeftRight.Left:
                GetLeftRightDial().SetDialValue(0);
                break;
            case LeftRight.Right:
                GetLeftRightDial().SetDialValue(1);
                break;
        }
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return FcsRuntimeClock.WaitForSeconds(2f);
    }

    public IEnumerator BuyPowders() {
        if (_powderCard == null) {
            MelonLogger.Error("[FCS] BuyPowders: Can't find PowderCharges card");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        _powderCard.position = new Vector3(6.4814f, -2.4675f, -22.0968f);
        _powderCard.GetComponent<DraggableItem>().MoveToSlot();
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return FcsRuntimeClock.WaitForSeconds(2f);
    }
    
}
