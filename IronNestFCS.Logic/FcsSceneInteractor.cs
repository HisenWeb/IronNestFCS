using System.Collections;
using Il2Cpp;
using Il2CppTMPro;
using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Localization;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public class FcsSceneInteractor
{
    private readonly FSC _fcs;
    private readonly List<GameObject> _destroyOnShutdown = new();
    private readonly ClickRaycaster _clicks = new();
    private readonly List<object> _localCoroutines = new();
    private bool _shuttingDown;

    // TaskSystem-owned physical clicks only. Persistent loading has a separate Host-side tracker.
    private static readonly List<LookAtTarget> HeldPhysicalClicks = new();

    public BulletType selectedBulletType = BulletType.HE;
    private readonly List<GameObject> _bulletTypeButtons = new();
    private readonly Dictionary<int, GameObject> _targetButtons = new();

    public bool AutoFire;
    public bool maxCharge;

    public FcsSceneInteractor(FSC fcs)
    {
        _fcs = fcs;
    }

    public void Initialize()
    {
        _shuttingDown = false;
        InitializeBulletTypeButtons();
        InitializeTargetButtons();
    }

    private void InitializeBulletTypeButtons()
    {
        const float z = -18.4181f;
        var x = 0.8f;
        var y = -0.65f;

        foreach (BulletType type in Enum.GetValues(typeof(BulletType)))
        {
            var captured = type;
            GameObject? button = null;
            button = AddButton(() =>
            {
                selectedBulletType = captured;
                foreach (var item in _bulletTypeButtons)
                    SetColor(item, item == button ? Color.green : Color.white);
            }, type == BulletType.HE ? Color.green : Color.white);

            button.transform.position = new Vector3(x, y, z);
            button.transform.localScale = Vector3.one * 0.02f;
            _bulletTypeButtons.Add(button);

            var text = AddText(type.DisplayName(), 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one;

            x -= 0.05f;
            y -= 0.0045f;
        }
    }

    private void InitializeTargetButtons()
    {
        const float z = -18.5881f;
        var x = 0.8f;
        var y = -0.65f;

        TextMeshPro? autoFireLabel = null;
        GameObject? autoFireButton = null;
        autoFireButton = AddButton(() =>
        {
            AutoFire = !AutoFire;
            MelonLogger.Msg($"[FCS] AutoFire toggled {(AutoFire ? "ON" : "OFF")}");
            if (AutoFire)
                _fcs.PlanExecutor.OnAutoFireEnabled();

            SetColor(autoFireButton!, AutoFire ? Color.red : Color.white);
            if (autoFireLabel != null)
                autoFireLabel.text = AutoFireText(AutoFire);
        }, Color.white);

        autoFireButton.transform.position = new Vector3(x, y, z);
        autoFireButton.transform.localScale = Vector3.one * 0.02f;
        var autoFireText = AddText(AutoFireText(false), 14f);
        autoFireLabel = autoFireText.GetComponent<TextMeshPro>();
        autoFireText.transform.SetParent(autoFireButton.transform, false);
        autoFireText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        autoFireText.transform.localScale = Vector3.one;

        x -= 0.05f;
        y -= 0.0045f;

        TextMeshPro? maxChargeLabel = null;
        GameObject? maxChargeButton = null;
        maxChargeButton = AddButton(() =>
        {
            maxCharge = !maxCharge;
            MelonLogger.Msg($"[FCS] MaxCharge toggled {(maxCharge ? "ON" : "OFF")}");
            SetColor(maxChargeButton!, maxCharge ? Color.red : Color.white);
            if (maxChargeLabel != null)
                maxChargeLabel.text = MaxChargeText(maxCharge);
        }, Color.white);

        maxChargeButton.transform.position = new Vector3(x, y, z);
        maxChargeButton.transform.localScale = Vector3.one * 0.02f;
        var maxChargeText = AddText(MaxChargeText(false), 14f);
        maxChargeLabel = maxChargeText.GetComponent<TextMeshPro>();
        maxChargeText.transform.SetParent(maxChargeButton.transform, false);
        maxChargeText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        maxChargeText.transform.localScale = Vector3.one;

        x -= 0.05f;
        y -= 0.0045f;

        for (var i = 1; i <= 4; i++)
        {
            var targetId = i;
            GameObject? button = null;
            button = AddButton(() =>
            {
                var bulletAtClick = selectedBulletType;
                SetColor(button!, Color.gray);
                var collider = button!.GetComponent<Collider>();
                if (collider != null)
                    collider.enabled = false;

                var handle = MelonCoroutines.Start(QueueStableTarget(targetId, bulletAtClick, button));
                _localCoroutines.Add(handle);
            }, Color.red);

            button.transform.position = new Vector3(x, y, z);
            button.transform.localScale = Vector3.one * 0.02f;
            _targetButtons[targetId] = button;

            var text = AddText("T" + targetId, 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one;

            x -= 0.05f;
            y -= 0.0045f;
        }
    }

    private static string AutoFireText(bool enabled) =>
        FcsLocalization.T($"自动开火：{FcsLocalization.OnOff(enabled)}", $"Auto Fire: {FcsLocalization.OnOff(enabled)}");

    private static string MaxChargeText(bool enabled) =>
        FcsLocalization.T($"最大装药：{FcsLocalization.OnOff(enabled)}", $"Max Charge: {FcsLocalization.OnOff(enabled)}");

    private IEnumerator QueueStableTarget(int targetId, BulletType bulletType, GameObject button)
    {
        if (_shuttingDown)
            yield break;

        var clickedAt = FcsRuntimeClock.Now;
        ArtilleryTask? task = null;
        yield return _fcs.MapTable.GetStableMarkTarget(targetId, result => task = result);

        if (_shuttingDown)
            yield break;
        yield return FcsRuntimeClock.WaitUntilFocused();
        if (_shuttingDown)
            yield break;

        if (task != null)
        {
            task.targetId = targetId;
            // Queue is intent-only. Physical state is captured later, once, in FirePlanner.
            task.bulletType = bulletType;
            _fcs.EnqueueTask(task);
        }

        var remainingCooldown = 1f - (FcsRuntimeClock.Now - clickedAt);
        if (remainingCooldown > 0f)
            yield return FcsRuntimeClock.WaitForSeconds(remainingCooldown);

        if (_shuttingDown)
            yield break;
        yield return FcsRuntimeClock.WaitUntilFocused();
        if (_shuttingDown)
            yield break;

        SetColor(button, Color.red);
        var targetCollider = button.GetComponent<Collider>();
        if (targetCollider != null)
            targetCollider.enabled = true;
    }

    public void TaskFinished(ArtilleryTask task) { }

    public void Update()
    {
        if (FcsRuntimeClock.IsFocused)
            _clicks.Update();
    }

    public void ShutDown()
    {
        _shuttingDown = true;

        foreach (var handle in _localCoroutines)
        {
            try { MelonCoroutines.Stop(handle); }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[FCS] Stop scene interaction coroutine failed: {ex.Message}");
            }
        }
        _localCoroutines.Clear();

        ReleaseHeldPhysicalClicks("logic shutdown/F9");
        _clicks.Clear();

        foreach (var obj in _destroyOnShutdown)
            Object.Destroy(obj);
        _destroyOnShutdown.Clear();
    }

    public GameObject AddButton(Action onClick) => AddButton(onClick, Color.white);

    public GameObject AddButton(Action onClick, Color color)
    {
        var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _destroyOnShutdown.Add(button);
        var collider = button.GetComponent<Collider>();
        _clicks.Register(collider, onClick);
        SetColor(button, color);
        return button;
    }

    public static void SetColor(GameObject go, Color color)
    {
        var renderer = go.GetComponent<Renderer>();
        if (renderer == null)
            return;

        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            if (renderer.material != null)
                renderer.material.color = color;
            return;
        }

        var mat = new Material(shader);
        mat.color = color;
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
        renderer.material = mat;
    }

    public GameObject AddText(string text, float fontSize = 4f)
    {
        var go = new GameObject("FcsText");
        _destroyOnShutdown.Add(go);
        go.transform.Rotate(new Vector3(90, 0, 0));
        go.transform.Rotate(new Vector3(0, 0, -90));
        var tmp = go.AddComponent<TextMeshPro>();
        if (tmp.font == null && TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        return go;
    }

    public static void BeginPhysicalClick(LookAtTarget button)
    {
        button.OnClickDown();
        if (!HeldPhysicalClicks.Contains(button))
            HeldPhysicalClicks.Add(button);
    }

    public static void EndPhysicalClick(LookAtTarget button)
    {
        try { button.OnClickUp(); }
        finally { HeldPhysicalClicks.Remove(button); }
    }

    public static void ReleaseHeldPhysicalClicks(string reason)
    {
        if (HeldPhysicalClicks.Count == 0)
            return;

        var held = HeldPhysicalClicks.ToArray();
        HeldPhysicalClicks.Clear();
        foreach (var button in held)
        {
            try
            {
                button.OnClickUp();
                MelonLogger.Warning($"[FCS] Released TaskSystem click during {reason}: {button.gameObject.name}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[FCS] Failed to release TaskSystem click during {reason}: {ex.Message}");
            }
        }
    }

    public static IEnumerator WaitAndClick(LookAtTarget? button, float timeoutSeconds = 10f)
    {
        if (button == null)
            yield break;

        var deadline = FcsRuntimeClock.Now + Mathf.Max(0.1f, timeoutSeconds);
        while (true)
        {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (button.isActive && button.nextAllowedClickTime <= Time.realtimeSinceStartup)
                break;

            if (FcsRuntimeClock.Now >= deadline)
            {
                MelonLogger.Error($"[FCS] WaitAndClick timeout: {button.gameObject.name}");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        }

        yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        yield return FcsRuntimeClock.WaitUntilFocused();
        BeginPhysicalClick(button);
        yield return new WaitForSeconds(0.1f);
        EndPhysicalClick(button);
    }

    public static IEnumerator InvokeDelay(Action action, float delay)
    {
        yield return FcsRuntimeClock.WaitForSeconds(delay);
        yield return FcsRuntimeClock.WaitUntilFocused();
        action();
    }
}
