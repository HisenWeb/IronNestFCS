# IronNestFCS Smart — Planning Architecture

> 状态快照：2026-08-13（UTC+8）  
> 当前正式版本：**v1.2.0**  
> 默认分支：`master`  
> 发布基线：`8197223ced619525d78d4b7bc24f7a30aacc28e7`（`release: v1.2.0`）  
> 本组文档由原单文件 `IronNestFCS-Smart_项目背景与设计来源.md` 拆分而来。

> 定位：Stable Host / Logic、PersistentLoading、Dispatcher、Planner、Matcher、Materialization、FirePlan 与 Priority。

## 文档导航

- `PROJECT_CONTEXT.md` — 先读；长期原则与总体模型。
- `ARCHITECTURE_PLANNING.md` — Planning / Matching / Admission。
- `ARCHITECTURE_EXECUTION.md` — Execution / Review / Arm / Physical Settlement。
- `KNOWN_ISSUES_AND_ROADMAP.md` — 已知问题与后续优化。
- `PROJECT_HISTORY.md` — 历史决策与设计来源。
- `RELEASE_AND_OPERATIONS.md` — 构建、日志、发布与维护规则。

**权威顺序：当前仓库代码与实机证据 > `PROJECT_CONTEXT.md` > 当前架构文档 > Roadmap > History。**

---

## Stable Host / TaskSystem 的架构边界

上游项目本来已经存在 Host / Logic 分离和 F9 Logic 热重载机制。

**Smart 并不是发明了 Host / Logic 分层。**

Smart 的真正改动是重新定义：

> 哪些状态必须留在 Stable Host，哪些状态可以随 TaskSystem 被 F9 推翻。

### Stable Host

Host 必须持有不应该因为 F9 消失的物理过程。

最重要的是：

```text
PersistentLoadingSystem
```

Host 的核心语义：

```text
Stable Host
F9 reloads TaskSystem only
PersistentLoadingSystem stays alive
```

也就是说：

- 已经被接受的物理装填事务属于 Host；
- F9 不应该打断它；
- 任务系统销毁，不意味着现实里的炮弹“没装过”；
- LoadingSnapshot 是 Logic 重新接入现实的重要边界对象。

### TaskSystem / Logic

Logic 属于可重规划层。

它负责：

- 接收任务意图；
- Pending 队列；
- 规划；
- Task × Gun 匹配；
- FirePlan；
- 调度顺序；
- Review / Arm / Fire 自动化；
- 任务失败 / 完成；
- current / next；
- 当前 FCS UI 状态。

所以：

```text
Host = 现实持续存在
Logic = 计划可以推翻
```

---

## PersistentLoadingSystem 为什么必须独立

项目重构的一个直接动因是：

> 连续任务 + F9 + 双炮并行时，任务生命周期与实际装填生命周期不能绑定在一起。

典型错误模式：

```text
任务 A 开始装填
↓
F9 / 任务对象结束
↓
游戏里的物理装填实际上还在进行
↓
新任务 B 认为装填流程已被“重置”
↓
任务状态与真实机械状态错位
↓
后续卡死 / 错装 / 无法恢复
```

Smart 的解决方式不是在 F9 时强行把一切锁住，而是：

> **装填系统和任务系统拆开。**

### PersistentLoadingSystem 负责

- 已被接受的装弹 / 装药事务；
- 装填事务状态；
- 装填机构状态；
- 真实炮膛状态；
- LoadedReady / Recovering / PostShotRecovery 等物理阶段；
- F9 之后继续收敛已有事务。

### TaskSystem 负责

- 玩家想打什么；
- 哪些任务 Pending；
- 哪门炮接哪个任务；
- 当前执行顺序；
- 在当前物理状态下是否能继续；
- 如何生成新的 FirePlan。

两边只通过明确接口交换必要信息，不共享完整生命周期。

---

## 当前坐标模型

坐标属于任务输入与弹道规划边界，不属于 Executor 生命周期。

Smart 当前使用：

```text
TurretLocation.position
↓
mapSurface.InverseTransformPoint(...)
↓
Tactical Map local coordinate
↓
与 T1～T4 marker localPosition 相减
↓
relative firing vector
↓
distance + azimuth
```

权威炮位来源是：

```text
TurretLocation.position
```

当前 Smart 不再退回 Tactical Map 上的 `Player Turret Piece` 作为物理炮位 fallback。

如果找不到 `TurretLocation`，应绑定失败，而不是偷偷使用 UI 坐标代替真实炮位。

关于旧 `Player Turret Piece` 链路、flatten-Z、movement-delta calibration 等历史实验，统一见：

```text
PROJECT_HISTORY.md
```

当前坐标方案有实机命中表现，但在没有新的充分证据前，不主动把整套 Smart 坐标方案作为 upstream PR 推送。

---

## 当前 Planning / Matching 管线

v1.2.0 后，规划流程不再是“拿到一个任务就立刻尝试某门炮并调用游戏弹道计算器”。

当前正确结构是：

```text
Pending Tasks
→ coalesce manual submissions
→ CaptureSnapshot once
→ BuildEligibility(Task × Gun)
→ side-effect-free candidate graph
→ TaskGunMatcher
→ selected non-conflicting assignments
→ Materialize selected edges only
→ CreatePlan
→ AddPlan
```

这个变化解决了两个核心问题：

1. 双炮任务不能只靠贪心串行 admission；
2. 游戏弹道计算器有物理副作用，不能在 Match 之前调用。

---

## TaskDispatcher 的职责

当前 `TaskDispatcher` 负责：

- Pending 队列；
- 新任务进入；
- 一轮 planning 的启动条件；
- 短时间任务 coalescing；
- 调用 FirePlanner 建立资格；
- 调用 TaskGunMatcher；
- 选中边的 materialization / rematch 协调；
- FirePlan admission；
- Pending 任务状态恢复；
- 炮位 Recovering 后的 retry；
- slot 释放后的下一轮调度。

### 当前重要语义

`TaskDispatcher` 不是每帧轮询 Pending。

主方向仍然是：

> **事件驱动调度。**

典型 Dispatch Opportunity：

```text
新任务提交
slot 释放
窗口恢复焦点
Recovering 空闲炮位变为可规划状态
```

### Recovering 炮位的 retry 规则

v1.2.0 修复了一个重要漏触发问题。

旧问题：

```text
Left 可以接 T1
Right 仍 Recovering
↓
这一轮认为“已经有可执行任务”
↓
没有登记 Right 的未来恢复机会
↓
Right 后来 EmptyReady
↓
Pending 没有新 TryDispatch
```

正确原则：

> **Retry ownership belongs to the free transient gun side, not to whether a particular task had zero candidates.**

也就是：

```text
空闲 FirePlan slot + 物理 Recovering gun
→ 本身就是未来 Dispatch Opportunity
```

如果该炮在另一项任务 Materialize 期间已经恢复，就立即请求下一轮；否则由一个临时 physical-retry waiter 等待恢复。

---

## FirePlanner：Eligibility 与 Materialization 分离

`FirePlanner` 当前最重要的边界是：

> **Eligibility 判断无游戏控制副作用；Ballistic Materialization 只对最终选中边发生。**

### CaptureSnapshot

一轮 planning 先读取同一份快照，包括：

- 当前炮塔方位；
- 左炮物理状态；
- 右炮物理状态；
- PersistentLoadingSystem snapshots；
- FirePlan slot availability。

### BuildEligibility

对每个 Task × Gun 建立资格边。

这里可以检查：

- 该侧 FirePlan slot 是否可用；
- 当前 Loading transaction 是否绑定；
- 弹种是否兼容；
- 固定装药是否能覆盖任务射程；
- 当前装填 / 已装状态；
- 预计装填剩余时间；
- 当前炮塔方位差；
- soft ETA / alignment score。

这里**不能**为了得到精确仰角而调用游戏 Ballistic Calculator。

### TaskGunCandidate

`TaskGunCandidate` 是：

> side-effect-free 的 Task × Gun 资格边。

它不是最终 FirePlan。

它可以携带：

- side；
- shell；
- charge；
- load estimate；
- azimuth estimate；
- pre-match ETA；
- alignment score。

但它不应该已经生成射击解算贴纸。

### MaterializeCandidate

只有 Matcher 选中后才进入 Materialize：

```text
selected Task × Gun
→ physical BallisticCalculator
→ game firing-solution sticker
→ elevation result
→ elevation validation
→ full timing
→ FirePlanCandidate
```

### 最重要的不变量

正常成功路径：

> **one final selected Task × Gun pairing → one physical ballistic calculation → one required sticker**

---

## 为什么 Ballistic Materialization 必须延后

游戏内 Ballistic Calculator 不是纯计算函数。

调用 `Calculate()` 会驱动物理游戏 UI，并生成 firing-solution sticker / 射击解算卡。

因此旧模式：

```text
T1 × Left → Calculate → sticker #1
T1 × Right → Calculate → sticker #2
Matcher 最后只选 Left
```

即使 Executor 最终只有一个 T1 FirePlan，也已经产生两张贴纸。

问题本质不是：

- Matcher 选错；
- Executor 重复 AddPlan。

而是：

> **Eligibility 判断做得太晚，Ballistic materialization 做得太早。**

当前正确原则：

> **任何有游戏物理副作用的计算，都必须尽量推迟到 assignment 已经确定以后。**

---

## TaskGunMatcher：双炮全局匹配

当前 `TaskGunMatcher` 是无状态、纯匹配模块。

它只接收 FirePlanner 提供的 side-effect-free edges，不直接接触：

- Ballistic Calculator；
- Trigger；
- Loading 物理动作；
- Executor。

因为系统建筑上只有两门炮，所以不需要引入 Hungarian 等通用大规模算法。

当前做法是枚举合法的左右炮非冲突组合。

### 典型问题

```text
             Left C2     Right C3
T1 9.7km        ✓            ✓
T2 13km         ×            ✓
```

正确匹配：

```text
T1 → Left C2
T2 → Right C3
```

旧式贪心可能：

```text
T1 → Right C3
T2 → no match
```

因此匹配必须先建立完整 Eligibility 图，再求合法 assignment。

### 当前 Matcher 优先级

当前代码大体按：

```text
1. 最大化已匹配任务数
2. 最小化最大 charge excess
3. 最小化总 charge excess
4. pre-match ETA
5. azimuth alignment
6. stable tie
```

其中 pre-match ETA 只包含：

- loading；
- shared azimuth；

不包含 elevation，因为 elevation 需要调用有副作用的物理弹道计算器。

### 已知后续优化点

当前 v1.2.0 的 Matcher 排序仍然把 charge fit 放在 pre-match ETA 前面。

这项 cost ordering 已记录为后续优化，不属于当前架构事实。具体问题、示例和候选调整方向统一维护在：

```text
KNOWN_ISSUES_AND_ROADMAP.md
→ Matcher cost ordering
```

这里不要复制未来方案，避免 Roadmap 与当前架构文档产生两套描述。

---

## Materialization 失败与 rematch

即使某条边通过 Eligibility，也可能在真正调用游戏弹道计算器时失败，例如：

- 弹道解算失败；
- 仰角超出机械范围；
- 物理 UI 未能完成计算。

当前策略：

```text
selected assignments
→ Materialize into temporary results
→ 某一 Task×Gun edge 失败
→ 排除这一条 edge
→ rematch
→ 最终一组都 materialize 成功
→ 才 AddPlan
```

这样避免“半批次 admission”。

需要记住一个边界：

如果某条成功边已经调用过物理弹道计算器并产生贴纸，随后因为另一条失败导致全局 rematch 最终放弃这条成功边，理论上仍可能产生 orphan sticker。

因此准确表述应是：

> 正常成功路径不再为 rejected alternatives 产生多余贴纸；失败重匹配极端路径仍可能留下已 materialize 的物理副作用。

不要把当前实现描述成“绝对不可能有孤儿贴纸”。

---

## FirePlan 的角色

`FirePlan` 表示已经完成规划、可以进入 Executor 的执行决定。

它不是实时物理状态本身。

典型内容包括：

- Task；
- Side；
- Shell；
- Charge；
- Elevation；
- Azimuth；
- Loading request；
- readiness estimate；
- ExecutionBatchId；
- LocalReady；
- AzimuthReady；
- ShotObserved；
- Failed 等执行字段。

### FirePlan 的边界

```text
TaskGunCandidate
→ 资格边

FirePlanCandidate
→ 已 materialize 的弹道 / 仰角候选

FirePlan
→ 已提交给 Executor 的执行计划
```

不要把这三层重新混回一个对象。

---

## FirePriorityCoordinator 的职责

`FirePriorityCoordinator` 负责的是：

> **进入 Executor 的 FirePlan 之间的一次性执行顺序决策。**

不是 Task × Gun 匹配器。

当前主要规则：

- 两个 plan 都有 ETA：比较 `EstimatedReadyAt`；
- ETA 接近：使用 planning order / tie break；
- ETA 不可用：参考 `AlignmentScore`；
- 一旦 pair 正式比较，结果不会因为后续新任务动态重排。

### ExecutionBatchId

`ExecutionBatchId` 只用于：

- scheduling compare / commit；
- pair 身份；
- 日志和顺序状态。

不要把它用于：

- Review；
- follower；
- safety eligibility；
- physical result mapping。

### 一次比较原则

```text
A / B 已比较
→ A current
→ B next
→ A 被物理击发消费
→ B promotion
```

B promotion 不应该再与新来的 C 重比，除非未来明确改变整个调度语义。
