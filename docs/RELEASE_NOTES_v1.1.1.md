# IronNestFCS Smart v1.1.1

## Downloads / 下载

> v1.1.1 was packaged before the project was renamed to **IronNestFCS Smart**, so the published ZIP filenames still use the previous `Enhanced` name.
>
> v1.1.1 的安装包在项目更名为 **IronNestFCS Smart** 之前已经生成，因此已发布 ZIP 文件名仍保留旧的 `Enhanced` 名称。

- `IronNestFCS-Enhanced_v1.1.1_en-US.zip` — English UI
- `IronNestFCS-Enhanced_v1.1.1_zh-CN.zip` — 简体中文 UI

Both packages contain the **same binaries**. Only `UserData/IronNestFCS/language.txt` is different.

两个安装包包含**完全相同的 DLL**，仅 `UserData/IronNestFCS/language.txt` 的默认语言不同。

Extract the selected archive directly into the **Iron Nest: Heavy Turret Simulator** game directory.

将所选压缩包直接解压到 **Iron Nest: Heavy Turret Simulator** 游戏根目录。

---

## Highlights

- Dual-gun FirePlan scheduling with independent loading/elevation and shared turret azimuth.
- Persistent loading transactions survive F9 TaskSystem hot reloads.
- One-time First/Second comparison with rolling gun-slot reuse.
- Planning reads the real chamber, reload state, elevation, and current turret azimuth.
- Reliable ballistic calculation with stable-result checking and per-task duplicate-solve cache.
- Manual fire and Auto Fire support.
- Max Charge and automatic ammunition/powder purchasing.
- English and Simplified Chinese player-facing UI packages.
- Canonical `PCLM` naming inside the FCS domain while retaining compatibility with the game's legacy `PLCM` identifier.
- Categorized diagnostics for planning, loading, firing order, turret, trigger, and failures.

## 主要更新

- 双炮 FirePlan 调度：左右炮独立装填/仰角，共享炮塔方位。
- F9 热重载 TaskSystem 时，已经接受的 Persistent Loading 装填事务继续执行。
- First/Second 只进行一次比较，并支持双炮槽位滚动复用。
- 规划时读取真实炮膛、reload state、仰角和当前炮塔方位。
- 弹道计算等待稳定结果，并对同一任务相同方案进行缓存，避免重复生成计算贴纸。
- 支持手动击发与 Auto Fire。
- 支持 Max Charge、自动购买炮弹和药包。
- 提供英文与简体中文玩家 UI 安装包。
- FCS 内部统一使用正式拼写 `PCLM`，同时兼容游戏遗留的 `PLCM` 标识。
- 规划、装填、射击顺序、炮塔、Trigger 与异常均有分类诊断日志。

---

## Installation / 安装

1. Install MelonLoader for IL2CPP. / 安装适用于 IL2CPP 的 MelonLoader。
2. Choose one language package. / 选择一个语言包。
3. Extract it directly into the game directory. / 直接解压到游戏根目录。
4. Start the game. / 启动游戏。

Expected files:

```text
Mods/IronNestFCS.dll
UserLibs/IronNestFCS.Abstractions.dll
UserData/IronNestFCS/IronNestFCS.Logic.dll
UserData/IronNestFCS/language.txt
```

To switch language manually, set `language.txt` to either:

```text
en-US
```

or:

```text
zh-CN
```

Then press **F9** or restart the game.

手动切换语言时，将 `language.txt` 改为 `en-US` 或 `zh-CN`，随后按 **F9** 或重启游戏。

---

## Credits

Based on the original [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS). Released under the repository MIT License.

基于原项目 [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS) 继续开发，并沿用仓库 MIT License。
