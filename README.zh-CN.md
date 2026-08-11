# IronNestFCS Smart

[English](README.md) | **简体中文**

这是一个面向 **Iron Nest: Heavy Turret Simulator** 的智能自动火控 Mod。

你负责在 Tactical Map 上放置目标、选择弹种和提交任务，IronNestFCS Smart 负责执行大部分重复火控流程：弹道解算、左右炮分配、弹药购买与装填、仰角、炮塔方位、Review / Arm，以及可选的自动击发。

[Nexus Mods](https://www.nexusmods.com/ironnest/mods/32) · [GitHub Release](https://github.com/HisenWeb/IronNestFCS-Smart/releases/latest) · [IRON NEST Steam 页面](https://store.steampowered.com/app/4300500/) · [MelonLoader](https://melonwiki.xyz/)

## 设计理念

**自动化操作，不自动化战术。**

你决定目标、弹药和任务顺序，Smart 负责执行计划。

需要放弃当前计划时按 **F9**。F9 重置的是 TaskSystem 的任务计划和执行状态，不会假装已经被实际装填系统接受的动作从未发生。重新提交任务后，Smart 会从两门炮此刻真实存在的物理状态继续规划。

## 主要功能

- T1～T4 可以形成任务队列，并让左右两门炮持续处理任务。
- 按 **F9** 可以重新规划，不会抹掉已经开始的实际装填。
- 尽量读取真实炮膛、装填机构、炮塔和控制器状态后再决定下一步。
- 通过游戏自己的弹道计算器取得射击解算结果。
- 缺少炮弹或药包时可以自动购买。
- 自动调整仰角和炮塔方位。
- **Auto Fire** 可自动完成最终击发。
- **Max Charge** 可优先使用可用的最高装药量。
- 左上角 FCS 面板会显示当前任务、进度、距离、装药、仰角，以及**预计炮弹飞行时间**。
- UI 自动跟随游戏中英文状态；无法明确识别为中文时统一使用英文。
- 所有玩家共用一个通用发布包。

## 下载与安装

下载最新版通用安装包：

```text
IronNestFCS-Smart_vX.X.X.zip
```

安装步骤：

1. 安装适用于 IL2CPP 的 MelonLoader，并至少正常启动一次游戏。
2. 退出游戏。
3. 将 ZIP 中的全部内容直接解压到游戏根目录。
4. Windows 提示合并 `Mods`、`UserLibs`、`UserData` 时允许合并。
5. 通过 MelonLoader 正常启动游戏。

安装后应存在：

```text
<GameDir>/Mods/IronNestFCS.dll
<GameDir>/UserLibs/IronNestFCS.Abstractions.dll
<GameDir>/UserData/IronNestFCS/IronNestFCS.Logic.dll
```

不要把整个 ZIP 直接放进 `Mods` 文件夹。

## UI 自动语言识别

现在不再维护单独的中文包、英文包，也不再使用 `language.txt`。

Smart 直接读取游戏左炮 Time-To-Impact 表盘上的本地化标签：

- 标签严格等于 `左` → FCS 使用简体中文；
- 其他任何文字、识别不到对象、或游戏使用其他语言 → FCS 使用英文。

这个游戏 UI 对象会被缓存并定期重新读取，因此游戏运行中切换语言时，FCS 也可以自动跟随，而不需要额外维护 Mod 语言配置。

## 游戏内使用方法

### 1. 放置目标标记

在 Tactical Map 上拖动左侧红色数字标记器 `1～4` 到你要攻击的位置。

![Tactical Map 左侧红色 1～4 目标标记器](docs/images/ironnest_usage-target-markers.jpg)

### 2. 选择弹种并提交任务

选择弹种，然后点击与红色标记器编号对应的 T1～T4：

![地图右侧 T1～T4 任务提交按钮](docs/images/ironnest_usage-submit-buttons.jpg)

```text
红色 1 → T1
红色 2 → T2
红色 3 → T3
红色 4 → T4
```

可以连续提交多个任务，Smart 会根据两门炮当前状态进行分配和执行。

### 3. 让 FCS 执行准备流程

一次正常任务大致会经过：

```text
读取目标
→ 读取当前真实物理状态
→ 弹道解算
→ 选择左右炮
→ 必要时购买弹药
→ 装弹 + 装药
→ 调整仰角
→ 旋转炮塔
→ 准备 Review / Arm
→ 手动击发或 Auto Fire
```

### 4. 查看左上角状态面板

`IronNest 火控系统` 面板会显示：

- 左炮 / 右炮当前物理状态或正在执行的 T 任务；
- 任务进度与已用时间；
- 方位与距离；
- 装药与仰角；
- 游戏 Time-To-Impact 预设可用后显示的**预计炮弹飞行时间**；
- 射击顺序 / 优先级状态；
- Auto Fire 与 Max Charge 状态；
- 等待队列；
- 本轮成功 / 失败统计和近期任务记录。

预计飞行时间直接读取游戏自己的 Time-To-Impact 表盘。该值会在射击方案准备完成后写入当前 FirePlan，击发后不会跟着倒计时继续减少。

### 5. 击发

- **Auto Fire 开启**：炮和炮塔实际就绪后，Smart 自动完成最终击发。
- **Auto Fire 关闭**：Smart 完成射击准备后等待玩家手动击发。

### 6. 用 F9 重新规划

当前目标、队列或射击顺序不满意时，直接按 **F9**，重新放置标记并提交新的 T1～T4。

需要注意：**F9 重置的是计划，不是物理现实。** 已经被装填系统接受的炮弹 / 药包装填会继续；新计划会读取最后真实存在的炮膛、仰角和炮塔状态。

## 诊断日志

正常游戏只写精简的 `problems.log`。

需要完整排查时，编辑：

```text
<GameDir>/UserData/IronNestFCS/diagnostics.txt
```

改为：

```text
on
```

然后按 **F9**。完整分类日志会写入：

```text
<GameDir>/UserData/IronNestFCS/Logs/
```

排查结束后把 `diagnostics.txt` 改回 `off`，再按一次 F9。

开发阶段用于研究炮弹 Time-To-Impact 的临时轨迹探针不会进入正式版本，也不会再生成单独的 `flight.log`。

## Smart 架构

Smart 将稳定 Host / 持久物理装填与可热重载的 TaskSystem/Logic 分开。这样 F9 可以放弃并重建任务计划，同时已经被 Host 接受的实际装填仍然继续存在。

本项目继续基于 [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS) 开发。Smart 的自动化重点是执行既有火控工作流，而不是替玩家选择战术目标。

## 开发者工具

- `tools/Deploy.ps1`：构建并部署开发版本；
- `tools/Build-ReleasePackages.ps1`：生成单一通用发布 ZIP；
- `tools/Release.ps1`：在 `master` 上完成版本号、构建、tag 和 GitHub Release 发布。

开发说明见 [docs/FSC_MODULARIZATION_PLAN.md](docs/FSC_MODULARIZATION_PLAN.md)。

## 致谢

IronNestFCS Smart 基于 [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS) 开发。上游代码版权与贡献归原作者及贡献者所有。

## 许可证

本项目使用仓库中的 [MIT License](LICENSE)。
