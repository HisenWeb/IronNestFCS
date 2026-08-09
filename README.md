# IronNestFCS

> **Iron Nest: Heavy Turret Simulator 自动化火控系统增强版**  
> 基于 [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS) 继续开发，面向游戏正式版强化了双炮调度、物理状态恢复、弹道可靠性与热重载能力。

[原版 Demo Video](https://www.bilibili.com/video/BV1xc7F6WEET/)

[Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/4300500/) 的 [MelonLoader](https://melonwiki.xyz/) Mod。

在地图上放置 T1~T4 炮击标记并提交任务后，FCS 会直接读取游戏中的地图、炮塔、装填机构和控制台状态，自动完成：

**目标定位 → 弹道解算 → 弹药准备 → 双炮任务调度 → 方位/仰角校准 → Review Console → Arm → 击发/等待手动击发 → 击发后恢复。**

不使用 OCR，也不依赖屏幕识别；核心状态直接来自游戏对象与控制器。

---

## 主要增强

相较原版，本 fork 已经从基础自动化扩展为更完整的双炮 FCS。

### 正式版兼容与可靠性

- **适配游戏正式版场景与控制器**：修复 Demo → Release 后的场景绑定、炮管仰角控制和若干对象结构变化。
- **完整 watchdog / timeout**：装填、弹道、仰角、炮塔旋转、击发等待等环节均有失败检测，避免协程永久卡死。
- **动态炮塔旋转超时**：根据实际待转角度估算 watchdog，大角度旋转不会再被固定 45 秒错误中断。
- **异常任务清理**：任务失败、重新分类、F9、协程取消等路径会清理对应仲裁状态和 stale turret waiter，避免旧任务污染后续调度。

### 物理状态感知调度

FCS 不再简单认为“逻辑任务结束 = 炮膛为空”，而是读取炮的真实状态：

- 当前膛内弹种
- 实际装药数 `PowderCharges`
- `CanFire`
- `IsReloading`
- `pendingReload`
- reload controller state
- 当前真实仰角

因此支持三种任务模式：

| 模式 | 场景 |
| --- | --- |
| `FreshLoad` | 炮膛为空，从头装弹/装药 |
| `CompleteShellLoaded` | 已经有炮弹但尚未完成装药，继续完成本轮装填 |
| `ReuseLoadedRound` | 已经有完整可击发弹药，直接重新解算并复用 |

这使得 **手动预装弹、F9 后恢复、只击发其中一门炮、已有弹药重新指向新目标** 等情况都可以继续工作，而不是强制清空逻辑状态重来。

### 双炮并行与首发仲裁

两门炮的本地流程可以并行执行，炮塔方位则作为共享资源协调。

当前首发顺序不再按“左炮优先”或简单角度大小决定，而是估算每门炮距离真正可击发状态还剩多少时间：

```text
仰角 ETA = 剩余仰角 / 约 2°/s
方位 ETA = 剩余方位 / 约 4°/s

Local ETA = 剩余装填 ETA + 仰角 ETA
Fire-ready ETA = max(Local ETA, 方位 ETA)
```

正式版实测：

```text
炮塔方位速度 ≈ 4°/s
炮管仰角速度 ≈ 2°/s
FreshLoad 从弹道注册到 LoadedReady ≈ 32.25s
```

仲裁会重新读取当前炮管与炮塔位置，所以已经完成的装填/瞄准进度不会被重复计算。

当上一轮 First 已击发时，Second 会立即接手共享炮塔开始转向，但在真正进入击发控制阶段前仍属于 **provisional winner**。如果另一门炮恢复后完成了一个更快的新任务，可以重新仲裁并抢占方位资源。

真正不可抢占的 **hard commit** 点位于：

```text
方位 Ready + 仰角 Ready
        ↓
Hard Commit
        ↓
Review Console
        ↓
Arm
        ↓
Fire
```

因此不会在已经上保险或准备击发后突然切换目标。

### 弹道计算可靠性

- 自动读取目标距离与方向并驱动游戏原生 Ballistic Calculator。
- 自动选择装药量，也可开启 `Max Charge` 强制最大装药。
- 支持多种弹药，并可在缺弹时自动采购。
- Calculate 使用完整的按钮 Down/Up 流程。
- 不再固定等待 0.5 秒就盲读结果，而是等待输出稳定。
- 如果计算后显示值疑似未刷新，会自动再次验证。
- 无法确认结果时宁可让任务失败，也不会拿疑似旧仰角继续击发。

### Trigger Console 物理状态识别

正式版中部分交互控件的 `GetActive()` / `isClicked` 并不能可靠表示真实状态，因此本 fork 直接根据开关/保险杆的实际 Transform 姿态判断：

- Review 开关：物理旋转姿态判断 ON/OFF
- Left / Right Arm：分别读取各自保险杆位置

FCS 只会在 **方位和仰角都已经物理到位** 后操作 Review Console 与 Arm。

### F9 热重载与状态恢复

火控核心逻辑位于独立可卸载程序集 `IronNestFCS.Logic.dll`。

修改代码重新编译后，在游戏中按 **F9** 即可重新加载逻辑，无需重启游戏。

F9 的语义是：

> **丢弃旧的逻辑任务状态，但不假设现实中的炮被清空。**

重新绑定后，FCS 会重新读取两门炮的实际膛内弹药、装药、reload 状态和仰角，再决定下一任务应该继续装填、复用现有弹药还是重新开始。

---

## 基础功能

- **T1~T4 一键下达火力任务**
- **双炮管并行任务队列**
- **自动目标定位、距离与方向角读取**
- **自动弹道解算**
- **自动购买炮弹与药包**
- **自动装弹 / 装药**
- **自动调整共享炮塔方位**
- **独立控制左右炮仰角**
- **手动 / 自动击发模式**
- **Max Charge 模式**
- **任务状态、队列、近期任务与失败原因显示**
- **首发仲裁状态与 ETA 详情显示**
- **失焦保护**：Alt-Tab 时暂停新的自动交互与有效 watchdog 计时，回到游戏后继续
- **F9 热重载**

### 弹种

当前火控逻辑支持游戏中的多种弹药，包括：

`AP / HCHE / HE / STAR / SMK / PCLM ...`

内部游戏枚举仍可能使用 `PLCM`，UI 使用正式拼写 `PCLM`。

---

## 工作流程

一个典型的 FreshLoad 任务：

```text
移动地图炮击标记
        ↓
点击 T1 / T2 / T3 / T4
        ↓
标记位置稳定采样
        ↓
读取距离 / 方位
        ↓
Ballistic Calculator 解算
        ↓
注册双炮仲裁候选
        ├───────────────┐
        ↓               ↓
装弹 / 装药         共享炮塔方位
        ↓               ↓
LoadedReady         Azimuth Ready
        ↓               │
调整本炮仰角             │
        ↓               │
Elevation Ready ────────┘
        ↓
Hard Commit
        ↓
Review Console
        ↓
Arm
        ↓
手动击发 / Auto Fire
        ↓
确认真实击发
        ↓
等待炮尾机构恢复到 EmptyReady
        ↓
释放炮管槽位并接取下一任务
```

---

## 状态面板

IMGUI 面板会显示：

- 左 / 右炮当前任务
- 目标编号
- 弹种
- 方位角
- 距离
- 装药数
- 仰角
- 当前任务阶段
- 等待时间
- 队列任务
- 本轮成功 / 失败统计
- 最近任务历史
- 中文失败原因
- 当前首发仲裁状态
- 两门炮预计 Fire-ready ETA

仲裁信息示例：

```text
首发仲裁：已完成 T1 → T2
左T1：预计14.1s（装0.0+仰14.1 / 方0.0）
右T2：预计49.3s（装32.2+仰17.1 / 方32.8）
```

---

## 架构

工程拆分为四个程序集，核心是宿主 / Logic 分离的热重载架构：

| 项目 | 角色 | 说明 |
| --- | --- | --- |
| `IronNestFCS` | **宿主 Mod** | 稳定加载，负责 Logic 生命周期、F9 热重载与回调转发 |
| `IronNestFCS.Abstractions` | **契约** | `IFcsModule` 等跨 `AssemblyLoadContext` 共享接口 |
| `IronNestFCS.Logic` | **火控核心** | 地图、弹道、双炮调度、物理状态、炮塔/炮管、Trigger Console、UI |
| `IronNestFCS.CustomRecords` | **独立 Mod** | 自定义唱片机，与 FCS 核心无直接关系 |

Logic 程序集从内存字节加载，不锁住磁盘 DLL，并运行于可回收 `AssemblyLoadContext`。

热重载时会：

```text
Shutdown 当前 Logic
→ 停止协程
→ 撤销 Harmony patch
→ 清理游戏对象引用
→ 卸载旧 ALC
→ 读取新的 IronNestFCS.Logic.dll
→ 创建新 Logic
→ 重新绑定当前场景
```

相关代码：

- [LogicReloader.cs](IronNestFCS/LogicReloader.cs)
- [FSC.cs](IronNestFCS.Logic/FSC.cs)

---

## 构建与安装

### 前置条件

- 游戏本体
- MelonLoader（IL2CPP）
- .NET SDK（项目版本约束见 [global.json](global.json)）

### 推荐：通过命令行指定游戏目录

无需为了本机路径修改源码，可以直接通过 `GameDir` MSBuild 属性构建。

例如：

```powershell
dotnet build .\IronNestFCS.Logic\IronNestFCS.Logic.csproj -c Release `
  -p:GameDir="D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator"
```

Logic 项目会直接把输出写入：

```text
<GameDir>\UserData\IronNestFCS\IronNestFCS.Logic.dll
```

因此开发时通常只需要：

```text
修改代码
→ dotnet build
→ 回到游戏
→ F9
```

### 完整构建

也可以构建整个解决方案：

```powershell
dotnet build .\IronNestFCS.sln -c Release `
  -p:GameDir="D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator"
```

### 运行时文件位置

| 文件 | 位置 |
| --- | --- |
| `IronNestFCS.dll` | `Mods/` |
| `IronNestFCS.Abstractions.dll` | `UserLibs/` |
| `IronNestFCS.Logic.dll` | `UserData/IronNestFCS/` |
| `IronNestFCS.CustomRecords.dll` | `Mods/` |

---

## 使用

1. 安装 MelonLoader 与本 Mod。
2. 进入包含重炮炮塔和 Tactical Map 的场景。
3. 将地图上的编号炮击标记 **1~4** 移动到目标位置。
4. 在 FCS 面板中选择弹种，并按需设置 `Auto Fire` / `Max Charge`。
5. 点击 `T1` ~ `T4` 提交对应目标。
6. FCS 自动完成定位、解算、调度、装填和瞄准。
7. 手动模式下等待系统完成 Review + Arm 后由玩家击发；自动模式则由 FCS 完成最后击发。
8. 左上角状态面板可观察双炮任务、队列和仲裁情况。

如果当前场景重新加载或开发中替换了 Logic DLL，可按 **F9** 重新绑定。

---

## 调试日志

开发版本目前保留了一些用于验证正式版物理行为的诊断信息：

- `[FCS BALLISTIC]`：弹道计算输入、输出和结果刷新验证
- `[FCS PrepProbe]`：仲裁后装填、仰角与 LocalReady 耗时
- `[FCS SpeedProbe]`：方位 / 左仰角 / 右仰角实际运动速度

这些日志主要用于继续校准 ETA 模型和定位极低概率的游戏交互状态问题。

---

## Custom Records

`IronNestFCS.CustomRecords` 是附带的独立 Mod，与火控逻辑无关。

把音频放入：

```text
UserData/CustomRecords/
```

支持：

- `.mp3`
- `.wav`
- `.flac`

封面可使用：

1. 与音频同名的 `.png / .jpg / .jpeg`
2. 音频文件内嵌封面

进入场景后会自动克隆 RecordDisk 并替换音轨与封面。

---

## 上游与贡献

本仓库基于：

- [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS)

继续开发。

如果改动具有通用价值，也可以整理为独立改动提交回上游。

本仓库欢迎 Issue / Pull Request：

- [Issues](../../issues)
- [Pull Requests](../../pulls)

修改 `IronNestFCS.Logic` 时需要特别注意热重载约束：

- Logic 中不要随意注册无法卸载的 IL2CPP 类型
- 长生命周期协程必须能够在 Shutdown 时停止
- 不要让旧 Logic 的游戏对象引用跨越 F9 reload
- F9 / 异常退出时必须同步清理共享锁与仲裁状态
- Trigger Console 与 reload state 应以已验证的物理状态为准，而不是依赖不可靠的 UI flag

---

## License

沿用项目许可证，详见 [LICENSE](LICENSE)。

## 免责声明

本项目为非官方第三方 Mod，与游戏开发商无关。仅供学习与单机娱乐使用，使用风险自负。
