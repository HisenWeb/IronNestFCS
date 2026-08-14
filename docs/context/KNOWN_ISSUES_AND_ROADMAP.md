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

### F. Salvo / 双炮齐射（候选，尚未实现）

当前结论：Smart **已经自然具备双炮同方位协同并同时 Arm / Fire 的后半段能力**。因此如果加入显式 `Salvo`，不应新增一套 Salvo executor、长期 pair state 或新的执行生命周期。

玩家侧语义：

```text
Salvo OFF
→ 普通任务，保持现有最大吞吐量调度

Salvo ON
→ 玩家点一次目标
→ 创建 1 个逻辑上的 SalvoTask
→ 明确接受等待两门炮同时可接任务的代价
→ 两门炮一起执行这个目标
```

这里的等待是显式模式本身的产品取舍：普通模式负责效率；Salvo 模式负责玩家选择的同步射击体验。不要为了同时追求 Salvo 与零等待而引入 half-salvo、提前占一门炮、长期等待另一门炮等额外状态。

#### Pending 表达一个任务，不放两个 linked task

队列中推荐只保存：

```text
[ SalvoTask A ]
```

而不是：

```text
[ A1 ][ A2 ]  // linked pending tasks
```

原因：Pending 应表达一次玩家意图。提前放两个 linked task 会额外制造相邻性、reorder、coalesce、cancel、F9/replan 和“只 admission 了一半”等问题。

#### Admission / Matcher 只增加一个很窄的双槽规则

`SalvoTask` 到达可派发位置后，只有当左右两门炮的执行槽都可接收新任务，并且当前 Snapshot 下两侧都满足既有 eligibility 时，才允许 admission。

```text
只有一门炮可接
→ SalvoTask 保持 Pending

两门炮都可接
→ atomic admission
→ 同一个 SalvoTask 同时 materialize 到 Left / Right
```

这可以实现为 Matcher / admission 前后的窄特判；不需要修改普通任务的 cardinality、scarcity、Pending order、ETA / azimuth 等比较规则，也不应在 Dispatcher 中另造一条绕开现有 eligibility / materialization 不变量的直接派发路径。

#### 进入执行栈时展开为两个普通执行实例

成功 admission 时，从同一个 `SalvoTask` 创建两份参数完全相同、仅 AssignedGun 不同的普通执行实例 / `FirePlan`：

```text
SalvoTask A
    ↓
Left  FirePlan(A)
Right FirePlan(A)
```

这里不需要真的复制成两个 Pending Task；更准确的语义是：

> **1 个玩家任务请求 → 2 个执行实例。**

一旦进入执行栈，`Salvo` 的特殊性即结束。

#### 后半段保持现有执行模型

进入执行层后：

- 两侧继续各自走现有 `PrepareLocal`；
- 现有 same-azimuth / follower 逻辑负责识别双炮共同击发机会；
- 两边条件满足后沿现有路径同时 Arm；
- Fire / physical shot settlement / recovery 全部按现有规则处理；
- 当前没有理由给 `FirePlan` 增加 `SalvoPairId`，除非未来实机证据证明后半段确实需要额外区分。

设计不变量：

> **Salvo 的特殊性只存在于 UI / Pending / admission；进入执行栈后立即退化为两个普通执行实例。**

未来实现前需要确认的边界：

- “两门炮清空”应精确定义为两侧执行槽均可接收新任务，而不是误解为物理炮膛必须为空；
- Salvo 是否作为持续模式开关（当前倾向：按钮激活期间，每次点目标都创建一个 SalvoTask）；
- F9 在 admission 前按 Pending 现有语义处理；admission 后则已经是两个普通执行实例，继续遵循现有 F9 / physical reality 规则；
- 实机验证现有 same-azimuth Arm 路径确实足以覆盖显式 Salvo 的最终同步体验。
