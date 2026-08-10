using System.Collections;
using Il2Cpp;
using Il2CppTMPro;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public class FcsSceneInteractor {
    private FSC fcs;

    private List<GameObject> destroyOnShutdown = new();
    private readonly ClickRaycaster clicks = new();
    private readonly List<object> localCoroutines = new();
    private bool shuttingDown;

    // A Logic F9 can stop a coroutine between LookAtTarget.OnClickDown/OnClickUp. Keep every physical
    // down/up pair that FCS owns in one place so Shutdown can always release a half-finished interaction
    // before the old AssemblyLoadContext is unloaded.
    private static readonly List<LookAtTarget> heldPhysicalClicks = new();

    public BulletType selectedBulletType = BulletType.HE;
    private List<GameObject> bulletTypeBtns = new();
    private readonly Dictionary<int, GameObject> targetButtons = new();

    public bool AutoFire = false;
    public bool maxCharge = false;

    public FcsSceneInteractor(FSC fcs) {
        this.fcs = fcs;
    }

    public void Initialize() {
        shuttingDown = false;
        InitializeBulletTypeButtons();
        InitializeTargetButtons();
    }

    private void InitializeBulletTypeButtons() {
        const float z = -18.4181f;
        var x = 0.8f;
        var y = -0.65f;
        foreach (BulletType type in Enum.GetValues(typeof(BulletType))) {
            BulletType captured = type;
            GameObject button = null;
            button = AddButton(() => {
                selectedBulletType = captured;
                foreach (var btn in bulletTypeBtns) {
                    SetColor(btn, btn == button ? Color.green : Color.white);
                }
            }, type == BulletType.HE ? Color.green : Color.white);
            button.transform.position = new Vector3(x, y, z);
            button.transform.localScale = Vector3.one * 0.02f;
            bulletTypeBtns.Add(button);
            var text = AddText(type.DisplayName(), 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one * 1.0f;
            x -= 0.05f;
            y -= 0.0045f;
        }
    }

    /// <summary>
    /// 4 个目标按钮（对应地图上 1~4 号炮兵标记）。点击后先对地图标记做短暂稳定采样，
    /// 再把目标快照入队，避免正式版场景初始化时首帧 Transform 瞬态造成第一发坐标偶发错误。
    /// </summary>
    private void InitializeTargetButtons() {
        const float z = -18.5881f;
        var x = 0.8f;
        var y = -0.65f;
        
        TextMeshPro? autoFireLabel = null;
        GameObject? autoFireButton = null;
        autoFireButton = AddButton(() => {
            AutoFire = !AutoFire;
            MelonLogger.Msg($"[FCS] AutoFire toggled {(AutoFire ? "ON" : "OFF")}");
            SetColor(autoFireButton, AutoFire ? Color.red : Color.white);
            if (autoFireLabel != null)
                autoFireLabel.text = AutoFire ? "自动开火：开" : "自动开火：关";
        }, AutoFire ? Color.red : Color.white);
        autoFireButton.transform.position = new Vector3(x, y, z);
        autoFireButton.transform.localScale = Vector3.one * 0.02f;
        var autoFireText = AddText("自动开火：关", 14f);
        autoFireLabel = autoFireText.GetComponent<TextMeshPro>();
        autoFireText.transform.SetParent(autoFireButton.transform, false);
        autoFireText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        autoFireText.transform.localScale = Vector3.one * 1.0f;
        
        x -= 0.05f;
        y -= 0.0045f;
        
        TextMeshPro? maxChargeLabel = null;
        GameObject maxChargeButton = null;
        maxChargeButton = AddButton(() => {
            maxCharge = !maxCharge;
            MelonLogger.Msg($"[FCS] MaxCharge toggled {(maxCharge ? "ON" : "OFF")}");
            SetColor(maxChargeButton, maxCharge ? Color.red : Color.white);
            if (maxChargeLabel != null)
                maxChargeLabel.text = maxCharge ? "最大装药：开" : "最大装药：关";
        }, maxCharge ? Color.red : Color.white);
        maxChargeButton.transform.position = new Vector3(x, y, z);
        maxChargeButton.transform.localScale = Vector3.one * 0.02f;
        var maxChargeText = AddText("最大装药：关", 14f);
        maxChargeLabel = maxChargeText.GetComponent<TextMeshPro>();
        maxChargeText.transform.SetParent(maxChargeButton.transform, false);
        maxChargeText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        maxChargeText.transform.localScale = Vector3.one * 1.0f;
        
        x -= 0.05f;
        y -= 0.0045f;
        
        for (var i = 1; i <= 4; i++) {
            var targetId = i;
            GameObject button = null;
            button = AddButton(() => {
                var bulletAtClick = selectedBulletType;
                SetColor(button, Color.gray);
                var collider = button.GetComponent<Collider>();
                if (collider != null) collider.enabled = false;
                var handle = MelonCoroutines.Start(QueueStableTarget(targetId, bulletAtClick, button));
                localCoroutines.Add(handle);
            }, Color.red);
            button.transform.position = new Vector3(x, y, z);
            button.transform.localScale = Vector3.one * 0.02f;
            targetButtons[targetId] = button;
            var text = AddText("T" + targetId, 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one * 1.0f;
            x -= 0.05f;
            y -= 0.0045f;
        }
    }

    /// <summary>
    /// The shell selector is an auto-loading preference, not a command to overwrite a round that already
    /// physically exists in a gun. When a target is created after the player manually preloads a gun, adopt
    /// that gun's real shell type so the recovered/decoupled scheduler can solve the round that is actually
    /// present. If a preloaded gun matching the UI preference exists, keep the preference; otherwise use the
    /// first free preloaded gun (Left, then Right). Empty guns still use the UI-selected shell as before.
    /// </summary>
    private BulletType ResolveBulletTypeForNewTarget(BulletType selected, int targetId) {
        var left = fcs.LeftTask == null ? GunPhysicalState.Read("Left") : null;
        var right = fcs.RightTask == null ? GunPhysicalState.Read("Right") : null;

        static bool HasPhysicalShell(GunPhysicalState? state) {
            return state != null
                   && (state.LoadedReady || state.ShellLoaded)
                   && state.ShellType.HasValue;
        }

        if (HasPhysicalShell(left) && left!.ShellType == selected)
            return selected;
        if (HasPhysicalShell(right) && right!.ShellType == selected)
            return selected;

        GunPhysicalState? adopted = null;
        if (HasPhysicalShell(left)) adopted = left;
        else if (HasPhysicalShell(right)) adopted = right;

        if (adopted?.ShellType is BulletType actual) {
            MelonLogger.Msg(
                $"[FCS] T{targetId}: adopting physical {adopted.Side} shell {actual.DisplayName()} " +
                $"instead of UI preference {selected.DisplayName()}; {adopted.Summary()}");
            return actual;
        }

        return selected;
    }

    private IEnumerator QueueStableTarget(int targetId, BulletType bulletType, GameObject button) {
        if (shuttingDown)
            yield break;

        var clickedAt = FcsRuntimeClock.Now;
        ArtilleryTask? task = null;
        yield return fcs.MapTable.GetStableMarkTarget(targetId, result => task = result);
        if (shuttingDown)
            yield break;
        yield return FcsRuntimeClock.WaitUntilFocused();
        if (shuttingDown)
            yield break;

        if (task != null) {
            task.targetId = targetId;
            task.bulletType = ResolveBulletTypeForNewTarget(bulletType, targetId);
            fcs.EnqueueTask(task);
        }

        var remainingCooldown = 1f - (FcsRuntimeClock.Now - clickedAt);
        if (remainingCooldown > 0f) {
            yield return FcsRuntimeClock.WaitForSeconds(remainingCooldown);
        }

        if (shuttingDown)
            yield break;
        yield return FcsRuntimeClock.WaitUntilFocused();
        if (shuttingDown)
            yield break;
        if (button != null) {
            SetColor(button, Color.red);
            var collider = button.GetComponent<Collider>();
            if (collider != null) collider.enabled = true;
        }
    }

    public void TaskFinished(ArtilleryTask task) {
    }
    
    public void Update() {
        if (!FcsRuntimeClock.IsFocused)
            return;
        clicks.Update();
    }

    public void ShutDown() {
        shuttingDown = true;
        foreach (var handle in localCoroutines) {
            try { MelonCoroutines.Stop(handle); }
            catch (Exception ex) { MelonLogger.Warning($"[FCS] Stop scene interaction coroutine failed: {ex.Message}"); }
        }
        localCoroutines.Clear();

        ReleaseHeldPhysicalClicks("logic shutdown/F9");
        clicks.Clear();
        foreach (var obj in destroyOnShutdown) {
            Object.Destroy(obj);
        }
    }
    
    public GameObject AddButton(Action onClick) {
        return AddButton(onClick, Color.white);
    }

    public GameObject AddButton(Action onClick, Color color) {
        var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
        destroyOnShutdown.Add(button);
        var collider = button.GetComponent<Collider>();
        clicks.Register(collider, onClick);
        SetColor(button, color);
        return button;
    }

    public static void SetColor(GameObject go, Color color) {
        var renderer = go.GetComponent<Renderer>();
        if (renderer == null)
            return;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) {
            MelonLogger.Warning("[FCS] Can't find URP shader. Use default material color instead.");
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

    public GameObject AddText(string text, float fontSize = 4f) {
        var go = new GameObject("FcsText");
        destroyOnShutdown.Add(go);
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

    public static void BeginPhysicalClick(LookAtTarget button) {
        button.OnClickDown();
        if (!heldPhysicalClicks.Contains(button))
            heldPhysicalClicks.Add(button);
    }

    public static void EndPhysicalClick(LookAtTarget button) {
        try {
            button.OnClickUp();
        }
        finally {
            heldPhysicalClicks.Remove(button);
        }
    }

    public static void ReleaseHeldPhysicalClicks(string reason) {
        if (heldPhysicalClicks.Count == 0)
            return;

        var held = heldPhysicalClicks.ToArray();
        heldPhysicalClicks.Clear();
        foreach (var button in held) {
            try {
                button.OnClickUp();
                MelonLogger.Warning(
                    $"[FCS] Released interrupted physical click during {reason}: {button.gameObject.name}");
            }
            catch (Exception ex) {
                MelonLogger.Warning(
                    $"[FCS] Failed to release interrupted physical click during {reason}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 等待游戏按钮可点击并模拟一次完整点击。失焦期间既不点击，也不消耗 FCS watchdog 时间。
    /// </summary>
    public static IEnumerator WaitAndClick(LookAtTarget? button, float timeoutSeconds = 10f) {
        if (button == null) {
            MelonLogger.Error("[FCS] WaitAndClick: button is null");
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + Mathf.Max(0.1f, timeoutSeconds);
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();

            if (button.isActive && button.nextAllowedClickTime <= Time.realtimeSinceStartup)
                break;

            if (FcsRuntimeClock.Now >= deadline) {
                MelonLogger.Error($"[FCS] WaitAndClick timeout: {button.gameObject.name}");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        }

        yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        yield return FcsRuntimeClock.WaitUntilFocused();
        BeginPhysicalClick(button);

        // Finish an already-started click even if focus changes between down and up. F9/Shutdown has an
        // additional tracked-release fallback in case the coroutine itself is stopped during this hold.
        yield return new WaitForSeconds(0.1f);
        EndPhysicalClick(button);
    }
    
    public static IEnumerator InvokeDelay(Action action, float delay) {
        yield return FcsRuntimeClock.WaitForSeconds(delay);
        yield return FcsRuntimeClock.WaitUntilFocused();
        action();
    }
    
}