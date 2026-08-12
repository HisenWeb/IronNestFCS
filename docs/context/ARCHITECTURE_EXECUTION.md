# IronNestFCS Smart — Execution Architecture

> 状态快照：2026-08-13（UTC+8）  
> 当前正式版本：**v1.2.0**  
> 默认分支：`master`  
> 发布基线：`8197223ced619525d78d4b7bc24f7a30aacc28e7`（`release: v1.2.0`）  
> 本组文档由原单文件 `IronNestFCS-Smart_项目背景与设计来源.md` 拆分而来。

> 定位：Executor、current/next、Review、Arm、follower、物理击发结算、Pending/玩家任务语义与坐标模型。

## 文档导航

- `PROJECT_CONTEXT.md` — 先读；长期原则与总体模型。
- `ARCHITECTURE_PLANNING.md` — Planning / Matching / Admission。
- `ARCHITECTURE_EXECUTION.md` — Execution / Review / Arm / Physical Settlement。
- `KNOWN_ISSUES_AND_ROADMAP.md` — 已知问题与后续优化。
- `PROJECT_HISTORY.md` — 历史决策与设计来源。
- `RELEASE_AND_OPERATIONS.md` — 构建、日志、发布与维护规则。

**权威顺序：当前仓库代码与实机证据 > `PROJECT_CONTEXT.md` > 当前架构文档 > Roadmap > History。**

---

## FirePlanExecutor：容量二滑动窗口

当前 Executor 是容量为 2 的执行器：

```text
_leftPlan
_rightPlan
_current
_next
```

可以概括为：

```text
[A current, B next]
A physical fire
→ consume A
→ B promoted
→ C fills free side
```

如果 B 在共享物理击发中先实际开火：

```text
consume B
A remains current
C fills free side
```

### 关键原则

> **Dispatcher 管谁进来 / 怎么匹配炮。**  
> **Priority 管计划上谁先执行。**  
> **Executor 管两个已接收计划现在怎么滑动。**  
> **物理击发结果决定实际消费谁。**

---

## 为什么 LocalReady 不能替代 current / next

共享炮塔方位角可以在单炮完成装填前提前移动。

例如：

```text
Plan order A → B

A = current
→ 炮塔立即旋转到 A
→ A 还没 LocalReady

B = next
→ B 可能更早 LocalReady
```

这时不能因为 B LocalReady 更早就让 B 变成 current。

否则会破坏已经开始执行的共享炮塔顺序。

所以：

```text
current / next
→ scheduling order / shared azimuth ownership

LocalReady
→ 单炮局部准备完成
```

二者不是同一个维度。

---

## PrepareLocal 与 RunShared 的并行关系

每个 FirePlan 有两个不同维度：

### PrepareLocal

单炮独立准备：

```text
弹药资源
→ PersistentLoading request
→ 等真实 LoadedReady
→ elevation
→ LocalReady
```

左右炮的 Local preparation 可以并行。

### RunShared

只由 current 占有共享执行权：

```text
current committed
→ turret azimuth starts immediately
→ wait current LocalReady + azimuth
→ Review / Arm / shared fire wait
```

这解释了为什么：

> **方位角可以提前动。**

也解释了为什么 LocalReady 不能反向改写调度顺序。

---

## Review Controller

TriggerConsole 现在有独立的 Review Controller。

它维护：

```text
_leftGunReady
_rightGunReady
_reviewControllerEnabled
```

聚合目标：

```text
desiredOn = leftReady || rightReady
```

然后 `ReviewStateLoop()` 持续把 5 个确认开关收敛到目标物理状态。

五个 Review 控件：

- Task；
- Bullet；
- Rotation；
- Elevation；
- Ready。

### 重要原则

```text
Review = 后台持续收敛
Arm    = 满足当前击发条件后可启动
```

两者可以重叠执行。

不要重新改成：

```text
Review desired ON
→ 等 5 个开关全部物理到位
→ 再开始 Arm
```

这个方案已经被认为过慢，也不是当前架构目标。

当前 Review lead time：

```text
1.5 s
```

它是“提前量”，不是“等待所有 Review 完成”的 barrier。

---

## 物理按钮操作与共享资源锁

FCS 对真实控制台按钮的操作必须尊重游戏自身的：

- `isActive`；
- `nextAllowedClickTime`；
- 物理 OnClickDown / OnClickUp；
- 动画和状态切换。

不要为了追求速度绕过这些锁。

否则容易出现：

- 按钮动画和状态不同步；
- 物理顺序错乱；
- UI 看似改变但底层状态没收敛；
- 多个共享操作互相踩踏。

### SharedConsoleCoordinator

当前至少有三条共享物理 lane：

```text
Ballistic
Requisition
Trigger
```

多个逻辑流程可以同时提出操作，但底层物理控制台必须串行通过对应 lane。

正确理解：

> **多个逻辑流程可以并行推进意图，底层共享按钮系统自己串行流转。**

---

## Arm / 保险与共享击发扳机

游戏击发扳机是共享控制。

因此两门炮如果同时 Arm ON，拉一次共享扳机可能让两门炮一起开火。

这使 Arm 不能简单等价为：

```text
current LocalReady
→ Arm current + next
```

当前原则：

> **当前炮只解除自己的保险；另一门符合当前击发机会条件时，由另一门自己的流程解除自己的保险。**

不要重新引入：

```text
ArmSelected(current.Side, peer.Side)
```

作为“current 顺便给 peer 解保险”的机制。

---

## Same-azimuth follower 的正确语义

当另一门炮已经 LocalReady，且目标方位和 current 几乎一致时，它可以利用当前这次共享击发机会。

当前 tolerance：

```text
0.09°
```

但这个“另一门炮”不能被重新定义成一个新的 scheduler `_next`。

准确语义：

> **不改变 FirePlan 的 current / next 顺序，只表示当前共享击发机会中，另一门已经物理就绪、目标方位兼容，可以考虑由自己的流程解除自己的保险。**

因此 follower：

- 不是新的调度状态；
- 不拥有 scheduling identity；
- 不做 promotion；
- 不拥有 shared azimuth；
- 只是一条瞬时 safety eligibility 关系；
- 每次使用前从 live state 重新计算。

### follower 基本资格

至少要求：

- current 仍然是 `_current`；
- current / follower 是不同 plan / side；
- 两者仍 active；
- current `LocalReady`；
- current `AzimuthReady`；
- current 正在拥有当前 shared fire wait；
- follower `LocalReady`；
- generation 有效；
- AutoFire 尚未发出；
- 两个目标方位差 <= tolerance。

---

## 物理击发结算

v1.2.0 一个核心变化是：

> **FirePlan 的完成由观察到的实际物理击发决定。**

不再采用：

```text
current 调用了 Fire
→ 就假定 current 已经开火
```

### 当前流程

```text
shared fire wait 开始
→ 同时建立 Left / Right physical fire watch
→ 观察真实炮状态
→ 任一侧出现 fire event
→ 再等待 3 frame settlement buffer
→ 确认 Left / Right 实际谁开火
→ 对应 FirePlan ShotObserved = true
→ 对应 Task Finished
→ RecordTaskResult
→ ReleaseGunSlot
```

当前：

```text
FireSettlementBufferFrames = 3
```

### 双炮同时击发

```text
Left observed + Right observed
→ consume Left plan
→ consume Right plan
```

### 只有一门炮击发

```text
只 consume 实际击发侧 FirePlan
另一门 plan 保留
原调度承诺继续
```

### 没有 active plan 的炮被人工击发

这是一个真实物理事件，但不能错误消费 current plan。

系统会重新建立 baseline 并继续等待当前 plan。

---

## FirePlan 结束点

当前 FirePlan 的结束点是：

> **observed physical shot**

而不是：

- WaitFire() 返回；
- post-shot mechanism 完全回到 EmptyReady；
- 炮闩全部复位；
- 装填机构下一轮可用。

所以：

```text
physical shot observed
→ FirePlan finished / slot released

post-shot recovery
→ PersistentLoadingSystem / physical runtime owns it
```

这使 Executor 的计划生命周期和 Host 的机械恢复生命周期保持分离。

---

## 双炮连续执行模型

当前设计不是“打完一整批再重新规划”，而是容量二滑动窗口。

例如：

```text
[A current, B next]
Pending: C, D

A fires
→ A slot released
→ B promoted
→ C can enter freed side

B fires
→ B slot released
→ C may become current
→ D can enter freed side
```

这要求：

- FirePlan completion 尽早以物理 shot 为界；
- PostShotRecovery 不能长期占用 FirePlan slot；
- Dispatcher 必须能在 recovering gun 真正恢复后重新规划；
- Priority commitment 不能被每个新 plan 反复打乱。

---

## 当前玩家任务模型

Tactical Map 上有红色数字目标标记：

```text
1 / 2 / 3 / 4
```

任务提交对应：

```text
1 → T1
2 → T2
3 → T3
4 → T4
```

玩家可以连续提交多个任务。

Smart 会根据：

- FirePlan slot；
- 左右炮物理状态；
- 当前炮膛弹种；
- 当前 / 正在进行的装填事务；
- 固定装药射程资格；
- 炮塔方位；
- 预计 readiness；
- 当前 current / next 承诺；

决定任务是否：

- 进入 FirePlan；
- 继续 Pending；
- 等待某一炮恢复；
- 因硬资格不匹配而暂时无法执行。

---

## Pending 的语义

Pending 不等于失败。

Pending 表示：

> **任务意图仍然有效，但在当前可用炮位 / 当前物理装填状态下还不能形成可执行 FirePlan。**

典型原因：

- 当前弹种不匹配；
- 固定装药射程不足；
- 可用炮位仍在 Recovering；
- 当前两个 FirePlan slot 都被占用；
- 另一项已提交计划拥有当前调度承诺。

重要行为：

- 队首 blocked task 不应该阻塞后面兼容任务；
- 任务只有在具体 FirePlan 被 Executor 接受后才真正离开 Pending；
- 炮位恢复可用后应该重新触发调度；
- F9 会主动放弃 Pending，因为 F9 的语义就是“放弃当前任务计划”。

---

## 单计划 commit 文案

内部 Executor 仍可能使用历史 reason key：

```text
等待队列为空
```

但 v1.2.0 对玩家 / 日志的实际语义已经修正为：

```text
当前没有可立即形成配对的计划
no immediately available partner FirePlan
```

因为单个 plan commit 时，Pending 队列不一定真的为空；另一项任务可能只是暂时无法在当前物理炮况下形成 partner。

未来若清理内部 reason key，可以改名，但不应因此改变当前调度行为。

---

## 人工干预的基本原则

项目不假设玩家永远不碰设备。

如果中途有人工干预，系统恢复原则是：

> **重新读取真实物理状态，而不是相信旧任务流程记忆。**

```text
旧计划认为 A
但现场实际已经变成 B
→ 以后续 B 为准
```

只要游戏暴露足够的权威对象 / Controller 状态，就应该优先读取，而不是在 Logic 内维护一个“现实应该是什么”的影子世界。

这也是为什么 physical settlement、PersistentLoadingSystem 和 TurretLocation 都属于 Smart 的关键方向。
