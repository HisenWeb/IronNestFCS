# IronNestFCS

> **Iron Nest: Heavy Turret Simulator 自动化火控系统增强版**  
> 基于 [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS) 继续开发，面向游戏正式版强化了双炮调度、物理状态恢复、弹道可靠性与 F9 热重载。

[原版 Demo Video](https://www.bilibili.com/video/BV1xc7F6WEET/)

这是 [Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/4300500/) 的 [MelonLoader](https://melonwiki.xyz/) Mod。

在地图上放置 T1~T4 炮击标记并提交任务后，FCS 直接读取游戏对象与控制器状态，自动完成：

**目标定位 → 弹道解算 → FirePlan 规划 → 双炮装填/仰角准备 → 共享炮塔方位 → Review Console → Arm → 自动或手动击发 → 击发后恢复。**

不使用 OCR，也不依赖屏幕识别。

---

## 当前架构

当前 FCS 只有两个顶层业务系统：

### TaskSystem

位于可热重载的 `IronNestFCS.Logic.dll` 中，负责：

- T1~T4 任务队列
- 当前规划轮状态快照
- 弹道解算
- FirePlan 生成
- 双炮槽位与一次性首发顺序
- 炮管仰角
- 共享炮塔方位
- Trigger Console / Arm / Fire
- UI 与任务历史

按 **F9** 时，TaskSystem 会被卸载并重新创建。

### Persistent LoadingSystem

位于稳定 Host `IronNestFCS.dll` 中，负责：

- 左右炮独立的实际装弹 / 装药流程
- 已接受的 `Gun + Shell + Charge` 装填事务
- 装填阶段的物理状态跟踪
- F9 期间继续推进尚未完成的装填

它的生命周期独立于 TaskSystem，因此 **F9 不会取消已经接受的装填事务**。

核心原则：

> **游戏中的真实物理状态是最高真相；TaskSystem 表达射击意图，Persistent LoadingSystem 持有已接受的装填事务。**

---

## FirePlan

`FirePlan` 是当前调度和执行的核心单元。

一个 FirePlan 固定绑定：

- Task
- Gun（Left / Right）
- Shell
- Charge
- Elevation
- Target Azimuth
- ETA / planning metadata

**FirePlan 一旦生成，任务与炮的绑定不会动态改写。** 如果必须换炮，则放弃当前 Plan，之后重新读取真实状态并重新规划。

### 规划快照

任务进入当前规划轮时，FCS 会读取一次：

- 左炮物理状态
- 右炮物理状态
- Persistent LoadingSystem 当前事务
- 左右炮实际仰角
- 炮塔当前真实方位角

然后基于这个快照生成左右候选。

FirePlan 不会持续动态追踪炮塔当前方位并反复重排；下一次重新规划时再读取新的真实方位。

这很重要，因为游戏中：

- **炮塔方位开炮后不会自动归零**
- **炮管仰角在击发恢复过程中会回零**

因此后续任务的方位 ETA 会从炮塔当时真实停留位置计算，而不是假定从 0° 开始。

---

## 双炮调度

两门炮各有一个执行槽位，可独立进行本地准备；炮塔方位是两门炮共享的资源。

### 一次性首发比较

两个尚未比较过的 FirePlan 同时存在时，只比较一次预计完成时间：

```text
[A 未比较, B 未比较]
        ↓
比较一次
        ↓
First / Second 固定
```

之后不会持续动态抢占。

例如：

```text
A / B 已比较
A 击发
C 进入空出来的炮槽
```

此时：

```text
B 已比较
C 未比较
→ 不重新比较
→ B 仍然是下一发
```

等 B 完成，D 进入后：

```text
C 未比较
D 未比较
→ C / D 比较一次
```

如果当前只有一个 FirePlan，且队列中没有等待或正在规划的任务，则该 Plan 直接取得执行顺序，不等待未来可能出现的新任务。

### 滚动双炮槽

无需等待两门炮整轮一起清空。

一门炮完成并恢复到可重新使用状态后，它的槽位可以立即接收下一任务；另一门炮可以继续完成自己的旧 Plan。

---

## 并行准备与共享方位

每个 FirePlan 的本地准备流程相互独立：

```text
Left : Loading → 实际 LoadedReady → Elevation
Right: Loading → 实际 LoadedReady → Elevation
```

仰角调整以 **真实 `LoadedReady`** 为门槛，不依赖 ETA 推算。

共享炮塔方位不依赖装填完成。一旦首发顺序确定：

```text
First committed
      ↓
立即开始 Azimuth
```

同时左右炮仍可继续各自装填和调整仰角。

真正进入 Review / Arm / Fire 前要求：

```text
Azimuth Ready + Elevation Ready
```

### ETA

ETA 只用于规划和首发比较，不作为真实完成条件。

概念上：

```text
Local ETA = Loading ETA + Elevation ETA
Fire-ready ETA = max(Local ETA, Azimuth ETA)
```

对于已经进行中的 Persistent Loading transaction，会考虑它在规划期间真实经过的时间；Fresh Load 则不会把规划耗时误算成“已经开始装填”。

正式版实测参考：

```text
炮塔方位速度 ≈ 4°/s
炮管仰角速度 ≈ 2°/s
Fresh Load 到 LoadedReady ≈ 32s（仅作规划基线）
```

---

## 弹道计算

FCS 驱动游戏原生 Ballistic Calculator：

- 自动设置距离与方向
- 自动选择弹种与装药
- 支持 `Max Charge`
- 使用完整 Calculate Down/Up 交互
- 等待结果稳定后读取仰角
- 无法可靠确认结果时让当前规划失败，而不是使用疑似旧结果

### 同一规划轮的弹道缓存

同一个 Task 在比较 Left / Right 候选时，如果两边最终使用相同的 `Shell + Charge`，只会执行一次实际 Calculate：

```text
T1 Left  = HE C2 → Calculate → E=30.08°
T1 Right = HE C2 → cache hit → 复用 E=30.08°
```

因此不会因为“一个任务要比较两门炮”而生成两张完全相同的计算贴纸。

如果左右候选确实使用不同装药方案，例如 `HE C2` 与 `HE C3`，则仍会分别计算。

---

## Persistent Loading 与 F9

装填系统接受的事务只有：

```text
Gun + Shell + Charge
```

一旦接受，新的 TaskSystem 不能覆盖仍在进行的装填事务。

F9 的语义是：

```text
TaskSystem FirePlans / queue / order
→ 清空并重新创建

Persistent LoadingSystem accepted transaction
→ 保留并继续执行

游戏真实炮膛 / reload state
→ 始终作为事实来源
```

已经实测覆盖：

```text
Left  : FinalSequence
Right : CloseShellGuide
        ↓
       F9
        ↓
新 TaskSystem 启动
        ↓
两笔装填继续推进
        ↓
Left / Right → LoadedReady
```

F9 后新任务会重新读取当时的真实炮膛、装药、reload state、仰角和炮塔方位，再生成新的 FirePlan。

---

## Trigger Console

正式版中部分控制台对象的逻辑状态并不能稳定代表物理姿态，因此 FCS 会读取开关与保险杆 Transform 位置进行确认。

执行顺序为：

```text
Local Ready + Azimuth Ready
        ↓
Review Console
        ↓
Arm Left / Right
        ↓
手动击发或 Auto Fire
        ↓
观察真实炮膛变化确认已经击发
```

---

## 基础功能

- T1~T4 一键提交火力任务
- 双炮滚动执行队列
- 自动读取目标距离与方向
- 自动弹道解算
- 自动购买炮弹与药包
- 自动装弹 / 装药
- 左右炮独立仰角
- 共享炮塔方位自动控制
- 一次性 FirePlan 首发比较
- 手动 / Auto Fire
- Max Charge
- 物理状态恢复
- Alt-Tab 失焦保护
- F9 TaskSystem 热重载
- 分类诊断日志

### 弹种

当前逻辑支持游戏中的多种弹药，包括：

`AP / HCHE / HE / STAR / SMK / PCLM ...`

内部游戏枚举仍可能使用 `PLCM`，UI 使用正式拼写 `PCLM`。

---

## 典型工作流程

```text
移动地图炮击标记
        ↓
点击 T1 / T2 / T3 / T4
        ↓
标记稳定采样
        ↓
任务进入 planning round
        ↓
读取一次真实状态快照
        ↓
对 Left / Right 构建候选
        ↓
Ballistic Calculator
（相同 Shell+Charge 本轮只算一次）
        ↓
生成固定 Task + Gun FirePlan
        ↓
左右 FirePlan 各自开始本地准备
        ↓
两个未比较 Plan → 一次性决定 First / Second
        ↓
First 立即开始共享炮塔方位
        │
        ├──────────────┐
        ↓              ↓
Persistent Loading   Azimuth
        ↓              ↓
实际 LoadedReady   Azimuth Ready
        ↓              │
Elevation             │
        ↓              │
Local Ready ──────────┘
        ↓
Review / Arm
        ↓
手动击发 / Auto Fire
        ↓
确认真实击发
        ↓
该炮恢复后释放槽位
```

---

## 工程结构

| 项目 | 角色 | 说明 |
| --- | --- | --- |
| `IronNestFCS` | **稳定 Host** | MelonLoader 入口、Persistent LoadingSystem、Logic 生命周期、F9 |
| `IronNestFCS.Abstractions` | **共享契约** | Host / Logic 共用接口与装填事务类型 |
| `IronNestFCS.Logic` | **TaskSystem** | 地图、FirePlanner、FirePlanExecutor、炮塔/仰角、Trigger、UI |
| `IronNestFCS.CustomRecords` | **独立 Mod** | 自定义唱片机，与 FCS 火控无直接依赖 |

关键代码：

- [PersistentLoadingSystem.cs](IronNestFCS/PersistentLoadingSystem.cs)
- [LogicReloader.cs](IronNestFCS/LogicReloader.cs)
- [FirePlan.cs](IronNestFCS.Logic/Scheduling/FirePlan.cs)
- [FirePlanner.cs](IronNestFCS.Logic/Scheduling/FirePlanner.cs)
- [TaskDispatcher.cs](IronNestFCS.Logic/Scheduling/TaskDispatcher.cs)
- [FirePlanExecutor.cs](IronNestFCS.Logic/Execution/FirePlanExecutor.cs)
- [FSC.cs](IronNestFCS.Logic/FSC.cs)

更详细的重构设计记录见 [docs/FSC_MODULARIZATION_PLAN.md](docs/FSC_MODULARIZATION_PLAN.md)。

---

## 构建与部署

### 前置条件

- 游戏本体
- MelonLoader（IL2CPP）
- .NET SDK（版本约束见 [global.json](global.json)）

### 完整部署

当 Host / Abstractions 有变化，或首次安装当前架构时，退出游戏后运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Deploy.ps1
```

默认游戏目录：

```text
D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator
```

也可显式指定：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Deploy.ps1 `
  -GameDir "D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator" `
  -Configuration Release
```

脚本会构建完整 solution，并部署：

| 文件 | 位置 |
| --- | --- |
| `IronNestFCS.dll` | `<GameDir>\Mods\` |
| `IronNestFCS.Abstractions.dll` | `<GameDir>\UserLibs\` |
| `IronNestFCS.Logic.dll` | `<GameDir>\UserData\IronNestFCS\` |
| `IronNestFCS.CustomRecords.dll` | `Mods/`（如单独构建/安装） |

完整部署后需要重启游戏一次。

### 日常 Logic 开发

如果只修改 `IronNestFCS.Logic`，可以直接：

```powershell
dotnet build .\IronNestFCS.Logic\IronNestFCS.Logic.csproj -c Debug `
  -p:GameDir="D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator"
```

然后回到游戏按 **F9**，无需重启进程。

当前 Host 启动提示应包含：

```text
IronNestFCS v1.1.1
Press F9 to hot reload TaskSystem.
```

---

## 使用

1. 安装 MelonLoader 与本 Mod。
2. 进入包含重炮炮塔和 Tactical Map 的场景。
3. 将地图上的编号炮击标记 **1~4** 移动到目标位置。
4. 在 FCS 面板选择弹种，并按需设置 `Auto Fire` / `Max Charge`。
5. 点击 `T1` ~ `T4` 提交对应目标。
6. FCS 自动完成规划、装填、瞄准、共享方位和击发控制。
7. 手动模式下由玩家完成最终击发；Auto Fire 模式由 FCS 自动击发。

---

## 调试日志

日志位于：

```text
<GameDir>\UserData\IronNestFCS\Logs\yyyy-MM-dd\run-HHmmss-pidNNNN\
```

主要文件：

| 文件 | 内容 |
| --- | --- |
| `all.log` | 全部事件 |
| `dispatch.log` | Task queue / planning / FirePlan |
| `ballistic.log` | Ballistic Calculator 输入、稳定结果、planning cache |
| `reload.log` | Persistent Loading、物理炮膛与 reload state |
| `order.log` | 一次性 First / Second 顺序与 promotion |
| `turret.log` | 标记与共享炮塔方位 |
| `trigger.log` | Review / Arm / Fire 与物理开关姿态 |
| `problems.log` | 真正需要关注的 warning / failure |
| `arbitration.log` | 旧日志分类兼容保留；当前架构正常情况下基本为空 |

排障建议优先看：

```text
problems.log
→ 对应分类日志
→ all.log
```

---

## 当前已验证行为

当前重构版本已经在游戏中验证：

- 同一个 Task 左右候选相同 `Shell + Charge` 时只生成一次 Ballistic Calculate
- 两门炮独立装填
- 实际 `LoadedReady` 后立即开始各自仰角
- 首发确定后共享炮塔立即开始转向，不等待装填
- 两个未比较 FirePlan 只比较一次
- 已比较的 Second 不被后来新任务重新挑战
- 炮槽可滚动复用
- 开炮后新 Plan 使用炮塔真实当前方位，而不是假定归零
- F9 清空 TaskSystem，但不清空已经装入炮膛的真实弹药
- **F9 发生在 `CloseShellGuide / FinalSequence` 等装填中间状态时，Persistent Loading transaction 仍会继续推进到 `LoadedReady`**

---

## License

本项目沿用仓库中的 [MIT License](LICENSE)。

感谢原项目 [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS) 提供基础实现。
