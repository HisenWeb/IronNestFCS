# IronNestFCS Enhanced

[English](README.md) | **简体中文**

这是一个面向 **Iron Nest: Heavy Turret Simulator** 的自动化火控系统增强 Mod。

你只需要在 Tactical Map 上放好目标标记，IronNestFCS Enhanced 就可以接管大部分火控流程：弹道解算、左右炮分配、自动购买和装填炮弹/药包、调整仰角、旋转炮塔、准备 Review / Arm，并可选择自动完成最终击发。

[下载最新版](https://github.com/HisenWeb/IronNestFCS/releases/latest) · [原作者 Demo Video](https://www.bilibili.com/video/BV1xc7F6WEET/) · [IRON NEST Steam 页面](https://store.steampowered.com/app/4300500/) · [MelonLoader](https://melonwiki.xyz/)

> 原作者 Demo Video 可以用来了解基本操作方式。Enhanced 版本的内部调度、状态处理和部分 UI 已经发生变化。

---

## 这个 Mod 是做什么的

一次典型炮击任务会变成：

```text
放置 T1～T4 目标标记
        ↓
选择弹药
        ↓
提交目标
        ↓
自动弹道解算
        ↓
自动选择左炮 / 右炮
        ↓
自动装弹 + 装药
        ↓
自动调整仰角 + 炮塔方位
        ↓
Review / Arm
        ↓
手动击发或 Auto Fire
```

Mod 直接读取游戏里的真实对象、炮膛和控制器状态，不使用 OCR，也不依赖屏幕识别。

---

## 主要亮点

- **放好目标，后面的重复操作基本都可以交给 FCS**：算弹道、选左右炮、买弹药、装填、调仰角、转炮塔、准备击发。
- **两门炮可以同时忙**：左炮和右炮不用排队做同一件事，一门正在装填或瞄准时，另一门也可以继续自己的准备。
- **连续打多个目标更顺**：一门炮打完并恢复后，可以马上接下一个目标，不用等另一门炮也一起结束。
- **炮塔不用每次打完都回到 0°**：下一发会从炮塔当前真实朝向继续计算，不会假设它已经归零。
- **按 F9 不会把正在装的弹弄没**：火控逻辑重新加载时，已经开始执行的装弹 / 装药会继续完成。
- **弹道结果更稳**：会等游戏里的弹道计算结果稳定后再使用，降低误读上一发旧结果的概率。
- **同一个目标不会没必要地重复按两次 Calculate**：如果左右炮算出来需要的是同一个方案，会直接复用已经得到的结果。
- **缺炮弹或药包时可以自动购买**。
- **Auto Fire** 可以自动完成最后的击发；不想自动开火也可以保持手动。
- **Max Charge** 可以优先尝试可用的最高装药量。
- **提供简体中文和英文 UI**。

---

## 和原版 IronNestFCS 有什么区别

本项目基于 [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS) 继续开发，核心思路没有变：**把重炮火控里大量重复操作自动化。**

Enhanced 主要是在这个基础上，把正式版里连续使用时容易遇到的问题继续处理掉。普通玩家最容易感受到的区别是：

| 实际使用场景 | IronNestFCS Enhanced 的变化 |
| --- | --- |
| 同时使用两门炮 | 左右炮尽量各做各的，不必把两门炮当成一整批一起等 |
| 连续提交多个目标 | 一门炮恢复后就能马上接下一目标，另一门炮可以继续忙自己的任务 |
| 上一发之后炮塔没有归零 | 下一发直接从炮塔当前真实方向开始规划，不要求先转回 `0°` |
| 按 F9 重载火控 | 已经开始执行的装弹 / 装药不会跟着当前任务一起被清掉 |
| 弹道计算器反应有延迟 | 会等结果稳定后再使用，而不是只等固定几秒就直接读取 |
| 同一目标左右炮得到相同方案 | 已经算过一次就直接复用，不再重复操作一次计算器 |
| 安装 Mod | 提供已经打包好的中英文 ZIP，普通用户不需要自己编译源码 |
| 游戏内界面 | 提供简体中文 / English 两套玩家界面 |

简单说：**原版提供了自动火控的基础和思路；Enhanced 更侧重正式版下双炮连续作业时的流畅度、稳定性和直接可用性。**

---

## 前置要求

需要：

1. **Iron Nest: Heavy Turret Simulator 游戏本体**
2. **适用于 IL2CPP 的 MelonLoader**
3. 一份 IronNestFCS Enhanced 安装包

建议先安装好 MelonLoader，并至少正常启动一次游戏，再安装本 Mod。

---

## 下载与安装

打开 [GitHub 最新 Release](https://github.com/HisenWeb/IronNestFCS/releases/latest)，按语言下载 **其中一个**：

```text
IronNestFCS-Enhanced_v*_zh-CN.zip   简体中文 UI
IronNestFCS-Enhanced_v*_en-US.zip   English UI
```

两个安装包使用完全相同的 Mod DLL，只是默认 UI 语言不同。

### 安装步骤

1. 退出游戏。
2. 打开下载好的 ZIP。
3. 把压缩包里的**全部内容直接解压到游戏根目录**。
4. 如果 Windows 提示合并 `Mods`、`UserLibs`、`UserData` 文件夹，允许合并。
5. 正常启动游戏。

安装后应存在：

```text
<GameDir>/Mods/IronNestFCS.dll
<GameDir>/UserLibs/IronNestFCS.Abstractions.dll
<GameDir>/UserData/IronNestFCS/IronNestFCS.Logic.dll
<GameDir>/UserData/IronNestFCS/language.txt
```

不要把整个 ZIP 直接丢进 `Mods` 文件夹。

---

## 游戏内怎么用

### 1. 进入重炮场景

进入包含重炮和 Tactical Map 的场景。场景加载后，FCS 会自动绑定需要的游戏控制对象。

### 2. 放置目标标记

在 Tactical Map 上移动：

```text
T1 / T2 / T3 / T4
```

把对应标记放到你想炮击的位置。

### 3. 选择弹药

在 FCS 面板中选择这个任务要使用的弹种。

可选功能：

- **Auto Fire**：条件满足后由 FCS 自动完成最终击发。
- **Max Charge**：优先使用可用的最高装药量。

### 4. 提交任务

点击 FCS 中对应的 `T1`、`T2`、`T3` 或 `T4`。

之后 FCS 会自动完成：

```text
读取目标
→ 读取当前炮膛 / 炮塔真实状态
→ 弹道解算
→ 分配左炮或右炮
→ 自动装弹 / 装药
→ 调整仰角
→ 旋转炮塔
→ 准备 Review / Arm
```

### 5. 击发

- **Auto Fire 开启**：炮管和炮塔实际就绪后，FCS 自动完成最终击发。
- **Auto Fire 关闭**：等 FCS 把射击准备完成后，由你手动完成最终击发。

可以连续提交多个目标。左右炮会独立调度，一门炮恢复后可以马上接下一任务。

---

## F9 热重载

按 **F9** 可以重新加载 TaskSystem 逻辑。

F9 会重新建立当前任务规划状态，但已经被装填系统接受的实际装填工作会继续运行，不会因为 TaskSystem 重载而被强制取消。

如果只是想重载 FCS 逻辑，通常不需要退出整个游戏。

---

## 切换 UI 语言

语言配置文件：

```text
<GameDir>/UserData/IronNestFCS/language.txt
```

填写：

```text
zh-CN
```

或：

```text
en-US
```

保存后按 **F9**，或者重启游戏即可切换。

---

## 常见问题排查

如果 FCS 没出现或者没有正常绑定：

1. 确认 MelonLoader 已按 IL2CPP 方式安装。
2. 确认三个 DLL 都在上面写明的准确路径。
3. 如果更换过 Host / Abstractions DLL，完整重启一次游戏。
4. 等重炮场景完全加载后，按一次 **F9**。
5. 查看诊断日志。

日志位置：

```text
<GameDir>/UserData/IronNestFCS/Logs/
```

优先查看：

```text
problems.log
→ 对应分类日志
→ all.log
```

反馈 Bug 时，最好附上对应那一轮的日志文件夹，并说明出问题时炮塔正在做什么。

---

## 开发者

源码仍然保留 Stable Host、Shared Abstractions 和可热重载 Logic 的结构。详细设计记录见 [docs/FSC_MODULARIZATION_PLAN.md](docs/FSC_MODULARIZATION_PLAN.md)。

仓库内还提供：

- `tools/Deploy.ps1`：开发环境构建和部署
- `tools/Build-ReleasePackages.ps1`：生成中英双语言 Release ZIP

---

## Credits

IronNestFCS Enhanced 基于原项目 [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS) 继续开发。原始实现及其贡献归原作者和原项目贡献者所有。

## License

本项目沿用仓库中的 [MIT License](LICENSE)。
