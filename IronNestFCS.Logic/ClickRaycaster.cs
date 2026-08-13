using System.Collections.Generic;
using UnityEngine.InputSystem;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

/// <summary>
/// 自己做的点击检测：每帧用新 Input System 读鼠标左键，从主相机发射线，
/// 命中已注册的 Collider 就触发对应回调。
///
/// 为什么不用其它方案：
///  - 不用 MonoBehaviour.OnMouseDown：需要注册新 IL2CPP 类型（破坏热重载），且走旧输入模块（本游戏已禁用）。
///  - 不用游戏的 LookAtTarget：它依赖游戏自己的管理器/基类，外部硬挂不可靠。
///  - 不用 IMGUI：MelonMod.OnGUI 单 pass，controlID 错位。
/// 本方案纯逻辑、可热重载、走新 Input、不依赖游戏交互组件。
/// </summary>
public class ClickRaycaster
{
    private readonly List<(Collider collider, System.Action onClick)> targets = new();

    /// <summary>注册一个可点击 Collider 及其点击回调。</summary>
    public void Register(Collider collider, System.Action onClick)
    {
        if (collider != null)
            targets.Add((collider, onClick));
    }

    /// <summary>移除一个动态销毁的 Collider，避免刷新 UI 时残留旧注册。</summary>
    public void Unregister(Collider collider)
    {
        if (collider == null)
            return;
        targets.RemoveAll(item => item.collider == null || item.collider.Equals(collider));
    }

    /// <summary>每帧调用。检测左键点击并派发。</summary>
    public void Update()
    {
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
            return;

        var cam = Camera.main;
        if (cam == null)
            return;

        var mousePos = mouse.position.ReadValue();
        var ray = cam.ScreenPointToRay(new Vector3(mousePos.x, mousePos.y, 0f));
        if (!Physics.Raycast(ray, out var hit, 1000f))
            return;

        var hitCollider = hit.collider;
        foreach (var (collider, onClick) in targets) {
            if (collider == null || !collider.Equals(hitCollider)) continue;
            try { onClick?.Invoke(); }
            catch (Exception ex) { MelonLogger.Error($"[Click] Callback exception: {ex}"); }
            break;
        }
    }

    /// <summary>清空注册表（重载/卸载时调用）。</summary>
    public void Clear() => targets.Clear();
}
