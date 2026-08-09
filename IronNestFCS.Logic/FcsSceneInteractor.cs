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

    // 当前选中的弹种（两管炮共享，由调度器决定任务派到哪管炮）。
    public BulletType selectedBulletType = BulletType.HE;

    private List<GameObject> bulletTypeBtns = new();

    // 每个地图目标对应一个按钮：targetId -> 按钮。点击=用当前弹种为该目标入队一个任务。
    private readonly Dictionary<int, GameObject> targetButtons = new();

    public bool AutoFire = false;
    public bool maxCharge = false;

    public FcsSceneInteractor(FSC fcs) {
        this.fcs = fcs;
    }

    public void Initialize() {
        InitializeBulletTypeButtons();
        InitializeTargetButtons();
    }

    private void InitializeBulletTypeButtons() {
        const float z = -18.4181f;
        var x = 0.8f;
        var y = -0.65f;
        foreach (BulletType type in Enum.GetValues(typeof(BulletType))) {
            BulletType captured = type;
            // 先声明再赋值：lambda 要捕获 button，不能在其声明表达式内部引用它。
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
            var text = AddText(type.ToString(), 14f);
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
                // Snapshot the selected shell at click time. Marker stabilization is asynchronous,
                // so a later shell-selection click must not alter the already requested mission.
                var bulletAtClick = selectedBulletType;
                SetColor(button, Color.gray);
                var collider = button.GetComponent<Collider>();
                if (collider != null) collider.enabled = false;
                MelonCoroutines.Start(QueueStableTarget(targetId, bulletAtClick, button));
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

    private IEnumerator QueueStableTarget(int targetId, BulletType bulletType, GameObject button) {
        var clickedAt = Time.realtimeSinceStartup;
        ArtilleryTask? task = null;
        yield return fcs.MapTable.GetStableMarkTarget(targetId, result => task = result);

        if (task != null) {
            task.targetId = targetId;
            task.bulletType = bulletType;
            fcs.EnqueueTask(task);
        }

        // Preserve the old one-second anti-double-click cooldown. Stable sampling normally consumes
        // about 0.2 s of it; only wait for the remainder.
        var remainingCooldown = 1f - (Time.realtimeSinceStartup - clickedAt);
        if (remainingCooldown > 0f) {
            yield return new WaitForSeconds(remainingCooldown);
        }

        if (button != null) {
            SetColor(button, Color.red);
            var collider = button.GetComponent<Collider>();
            if (collider != null) collider.enabled = true;
        }
    }

    /// <summary>任务完成回调</summary>
    public void TaskFinished(ArtilleryTask task) {
    }
    
    public void Update() {
        clicks.Update();
    }

    public void ShutDown() {
        clicks.Clear();
        foreach (var obj in destroyOnShutdown) {
            Object.Destroy(obj);
        }
    }
    
    public GameObject AddButton(Action onClick) {
        return AddButton(onClick, Color.white);
    }

    public GameObject AddButton(Action onClick, Color color) {
        // 用自带 BoxCollider 的 cube 当可点击目标，靠 ClickRaycaster 自己 raycast 检测点击，
        // 不依赖游戏的 LookAtTarget，也不注册新 IL2CPP 类型（保持可热重载）。
        var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
        destroyOnShutdown.Add(button);
        var collider = button.GetComponent<Collider>();
        clicks.Register(collider, onClick);
        SetColor(button, color);
        return button;
    }

    /// <summary>
    /// 给对象的 Renderer 换上当前渲染管线（URP）的材质并设颜色。
    /// CreatePrimitive 默认用内置管线的 Standard 材质，在 URP 下 shader 无效会渲染成紫色；
    /// 这里用 URP 的 Unlit shader 重建材质（不受光照影响，纯色所见即所得）。
    /// </summary>
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

    /// <summary>
    /// 在 3D 世界里创建一段文本（World Space 的 TextMeshPro，非 UGUI）。
    /// </summary>
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

    /// <summary>
    /// 等待游戏按钮可点击并模拟一次完整点击。以前这里可能无限等待，导致任务永远占住炮管；
    /// 现在默认 10 秒超时，后续任务流程会通过自己的状态 watchdog 判定失败并释放槽位。
    /// </summary>
    public static IEnumerator WaitAndClick(LookAtTarget? button, float timeoutSeconds = 10f) {
        if (button == null) {
            MelonLogger.Error("[FCS] WaitAndClick: button is null");
            yield break;
        }

        var deadline = Time.realtimeSinceStartup + Mathf.Max(0.1f, timeoutSeconds);
        while (button.isActive == false || button.nextAllowedClickTime > Time.realtimeSinceStartup) {
            if (Time.realtimeSinceStartup >= deadline) {
                MelonLogger.Error($"[FCS] WaitAndClick timeout: {button.gameObject.name}");
                yield break;
            }
            yield return new WaitForSeconds(0.1f);
        }
        yield return new WaitForSeconds(0.1f);
        button.OnClickDown();
        yield return new WaitForSeconds(0.1f);
        button.OnClickUp();
    }
    
    public static IEnumerator InvokeDelay(Action action, float delay) {
        yield return new WaitForSeconds(delay);
        action();
    }
    
}
