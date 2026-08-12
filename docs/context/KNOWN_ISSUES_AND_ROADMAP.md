# IronNestFCS Smart — Known Issues & Roadmap

> 状态快照：2026-08-13（UTC+8）  
> 当前正式版本：**v1.2.0**  
> 默认分支：`master`  
> 发布基线：`8197223ced619525d78d4b7bc24f7a30aacc28e7`（`release: v1.2.0`）  
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

这些是**已记录的后续项**，不要和 v1.2.0 已完成能力混淆。

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
