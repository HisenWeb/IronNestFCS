# IronNestFCS Smart — Known Issues & Roadmap

> 状态快照：2026-08-14（UTC+8）  
> 当前正式版本：**v1.2.7**  
> 默认分支：`master`  
> 发布基线：`da756d0f288956c2e099fd2e4a8a65cd86b97715`（`release: v1.2.7`）  
> 本组文档由原单文件 `IronNestFCS-Smart_项目背景与设计来源.md` 拆分而来。

> 定位：当前明确记录的复杂度热点、已知行为检查项和后续优化清单。

## 文档导航

- `PROJECT_CONTEXT.md` — 先读；长期原则与总体模型。
- `ARCHITECTURE_PLANNING.md` — Planning / Matching / Admission。
- `ARCHITECTURE_EXECUTION.md` — Execution / Review / Arm / Physical Settlement。
- `KNOWN_ISSUES_AND_ROADMAP.md` — 已知问题与后续优化。
- `PROJECT_HISTORY.md` — 历史决策与设计来源。
- `RELEASE_AND_OPERATIONS.md` — 构建、日志、发布与维护规则。

**权威顺序：当前仓库代码与实机证据 > `PROJECT_CONTEXT.md` > 当前架构文档 > Roadmap > History。**

---

## Dispatcher 当前复杂度与后续拆分项

`TaskDispatcher` 目前是 v1.2.0 架构中**最明显的复杂度热点**。

它同时承担：

- 唤醒；
- coalescing；
- Snapshot；
- Eligibility round；
- Matcher；
- Materialize / rematch；
- admission；
- physical retry；
- 下一轮触发。

这不是立即阻塞发版的问题，但已经值得后续做一次**轻量职责拆分**。

推荐优化方向：

```text
TaskDispatcher
负责：
Pending queue
+ 什么时候启动一轮
+ recovery / slot / task 事件
+ 最终 admission / remove Pending

        ↓

DispatchPlanningRound
一次性的，无长期状态
负责：
Snapshot
→ Eligibility
→ Match
→ Materialize
→ edge failure / rematch
→ 返回本轮结果
```

重要限制：

> **这个拆分不应该新增第二套生命周期。**

`DispatchPlanningRound` 应该是一次性事务对象 / 方法：

- 不持有长期 `_planning`；
- 不拥有 watcher；
- 不保存自己的 retry 状态；
- 用完即销毁。

这是**优化项，不是 v1.2.0 必须修复的问题**。

---

## 当前 Review / follower 的已知后续检查项

v1.2.0 发版时，以下两点仍应作为后续 review 项记录，不要假装已经完全解决。

### 非 current LocalReady 的 Review ready publication

当前 `PrepareLocal()` 完成后会：

```text
LocalReady = true
→ WaitingForFire
→ follower arm eligibility check
```

但 `SetGunReady(side, true)` 主要在 current 的 `RunShared()` 进入 shared fire stage 后发布。

这意味着：

> 非 current 的 LocalReady 与 Review ready input 不是完全同步发布。

一个未来可讨论的方向是：

```text
PrepareLocal complete
→ LocalReady = true
→ SetGunReady(side, true)
```

因为：

```text
Review Ready ≠ firing authority
```

但这项在 v1.2.0 中**没有修改**。

### AutoFire 与 follower 的 Trigger lane 时序

当前 current 在 Trigger lane 内：

```text
current Arm
→ BeginFireWait
→ 启动 follower arm coroutine
→ AutoFire 可能立即 Fire
→ current 释放 Trigger lane
```

follower coroutine 如果此时还在等同一个 Trigger lane，AutoFire 可能先发出，导致 follower 来不及解除自己的保险。

这属于后续设计检查项。

不要在无实机证据时随意改动，但未来处理 same-azimuth AutoFire 时应重点检查这条时序。

---

## 当前主要后续优化清单

这些是**已记录的后续项**，不要和当前已完成能力混淆。

### A. Dispatcher 轻拆分

目标：

```text
TaskDispatcher
+ stateless DispatchPlanningRound
```

不新增生命周期。

### B. Matcher cost ordering

讨论：

```text
existing loaded gun / ETA
是否应该优先于 charge excess
```

原则：

> 已有装药是约束；空炮是可塑资源。

### C. Review ready publication

检查是否应在 `PrepareLocal → LocalReady` 时立即发布 `SetGunReady(side, true)`。

### D. AutoFire + follower 时序

检查 same-azimuth follower 是否会因为 Trigger lane 而来不及 Arm。

### E. 更长期连续压力测试

重点场景：

- 4 个 Pending 连续滑动；
- 左右炮不同装药；
- 一门 LoadedReady、一门 Recovering；
- F9 在 loading / aiming / fire wait 各阶段；
- 手动提前开火；
- 双炮近同时开火；
- AutoFire + same azimuth。

### F. Forced Sync / 强制同步模式（实现中）

实现分支：`agent/forced-sync-lr`

Forced Sync 是一个**提交模式**，不是新的执行生命周期，也不是取消 / 降级控制。

```text
Forced Sync OFF
→ 新提交任务按普通模式进入 Pending

Forced Sync ON
→ 新提交任务标记为 Forced Sync
→ HUD 显示：原任务名 [Forced Sync L+R]
```

切换按钮只影响之后新提交的任务，不追溯修改已经进入 Pending 的任务。

设计原则：

> **完全同步的代价就是效率。**

普通模式继续优化吞吐量；Forced Sync 明确接受等待，以换取左右炮成对执行。

#### Pending 是单向 Head-of-Line Barrier

Pending 容器和 HUD 始终保留完整队列。Forced Sync 不锁住或修改队列本身，而是限制本轮 Dispatcher 能看到的候选范围。

例如：

```text
T1
S2 [Forced Sync L+R]
T3
```

正确语义：

```text
T1 → 完全按普通 Matcher / Dispatcher 规则执行
S2 → 当前屏障
T3 → 在 S2 完成前不可越过
```

也就是说：

> **Forced Sync 只挡后面，不挡前面。**

本轮普通任务扫描在遇到第一个 Forced Sync 时停止，Forced Sync 自身只有在真正到达 Pending 队首后才进入特殊处理。不要把它实现成全队列冻结，也不要让它抢占自己前面的普通任务。

多个 Forced Sync 不需要额外锁状态：永远只有 Pending 中第一个 Forced Sync 是当前屏障；前一个完成后再自然暴露后续任务和下一个 Forced Sync。

#### 到达队首后要求 L + R

Forced Sync 位于 Pending 队首时，只有满足以下条件才允许继续：

```text
Left executor slot  = free
Right executor slot = free
Left eligible       = true
Right eligible      = true
```

否则保持 Pending，且后面的任务仍不可越过。

这里不是给 Forced Sync 一个极高 Matcher 分数。它的语义是特殊的双槽 admission contract：左右两侧都必须可执行。

#### 火控解算保持现有串行模型

不修改 `BallisticCalculator`，也不新增同步解算。

Forced Sync 复用现有 `FirePlanner.BuildEligibility()` 和 `MaterializeCandidate()`：

```text
Left materialize / ballistic solve
→ Right materialize / ballistic solve
→ 两边都成功
→ 才进入执行
```

物理弹道计算器本来就由共享 Ballistic lane 串行保护；Left → Right 的固定顺序只提供确定性，不改变火控架构。

#### 进入执行时展开为 Left Task + Right Task

Pending 中的 Forced Sync 保持一个提交意图；真正准入后展开为两个独立的普通射击任务：

```text
S2 [Forced Sync L+R]
        ↓
Left Task  → Left FirePlan
Right Task → Right FirePlan
```

因此：

- 左右任务各自拥有 progress；
- 各自对应一次物理击发；
- 各自完成 / 失败结算；
- 统计上是 **2 个射击任务**；
- 不增加父任务完成聚合；
- 不增加长期 pair state machine。

#### 采购沿用现有逻辑，只在 TryRequest 前会合一次

左右两侧继续各自走当前 `PrepareLocal()` 的采购逻辑：

```text
检查装药 / 必要时采购
检查弹种 / 必要时采购
finally Requisition.Release()
```

Forced Sync 唯一新增的执行同步点位于：

```text
Requisition.Release()
→ 确认 FirePlan 仍 Active
→ Forced Sync L+R rendezvous
→ Loading.TryRequest(...)
```

两侧都完成采购并释放 Requisition 后才放行各自的 `TryRequest()`。

这个 rendezvous **必须位于 `Requisition.Release()` 之后**，避免一侧持有共享采购 lane 等待另一侧，从而形成死锁。

当前实现不修改共享装药采购 / reservation 语义；共享装药总量问题留作未来有实机证据后再单独处理。

#### TryRequest 后 Forced Sync 特殊性结束

两个加载请求放行后，后半段完全复用现有系统：

```text
PersistentLoadingSystem
→ 转弹架 / 入膛 / 装药
→ 各自 elevation
→ 现有 current / follower / Review / Arm
→ Fire
→ physical shot settlement
→ recovery
```

当前明确**不增加**：

- LoadedReady barrier；
- LocalReady gate；
- Arm barrier；
- `SalvoPairId` / 长期 GroupId；
- Host 侧 Forced Sync 状态；
- Forced Sync 专用 executor；
- 共享装药重构。

现有 same-azimuth follower / Arm / physical settlement 继续负责后半段行为。

#### F9 语义

Forced Sync 的 Pending、队列屏障和采购会合全部属于 reloadable Logic。

```text
F9 before Loading.TryRequest
→ Forced Sync 逻辑状态消失
→ 已经发生的物理采购仍留在游戏现实中

F9 after Loading.TryRequest accepted
→ Logic 任务状态消失
→ 已接受的 Host PersistentLoading transaction 按现有规则继续
```

原则保持不变：

> **F9 丢弃计划，不重置物理现实。**

#### 实机验证重点

实现完成后优先验证：

- `T1 → Forced Sync → T3`：T1 正常执行，T3 不越过；
- Forced Sync 到队首但仅一门炮空闲：空闲炮故意等待；
- 两门炮都空闲：Forced Sync 展开为 L + R；
- 一边需要采购、一边无需采购：无需采购的一边在 `TryRequest()` 前等待；
- 两边都需要采购：Requisition 串行采购后正确会合，无死锁；
- Forced Sync 两发分别计入任务统计；
- F9 分别发生在 Pending / 采购 / rendezvous / loading 阶段；
- AutoFire OFF / ON 下现有 same-azimuth Arm / Fire 行为未回归。
