# IronNestFCS Smart — Project History & Design Sources

> 状态快照：2026-08-13（UTC+8）  
> 当前正式版本：**v1.2.0**  
> 默认分支：`master`  
> 发布基线：`8197223ced619525d78d4b7bc24f7a30aacc28e7`（`release: v1.2.0`）  
> 本组文档由原单文件 `IronNestFCS-Smart_项目背景与设计来源.md` 拆分而来。

> 定位：坐标实验、EntityLocation、upstream PR、关键提交、历史分支与设计来源。

## 文档导航

- `PROJECT_CONTEXT.md` — 先读；长期原则与总体模型。
- `ARCHITECTURE_PLANNING.md` — Planning / Matching / Admission。
- `ARCHITECTURE_EXECUTION.md` — Execution / Review / Arm / Physical Settlement。
- `KNOWN_ISSUES_AND_ROADMAP.md` — 已知问题与后续优化。
- `PROJECT_HISTORY.md` — 历史决策与设计来源。
- `RELEASE_AND_OPERATIONS.md` — 构建、日志、发布与维护规则。

**权威顺序：当前仓库代码与实机证据 > `PROJECT_CONTEXT.md` > 当前架构文档 > Roadmap > History。**

---

## 坐标链路的历史变化

上游旧链路曾把：

```text
Player Turret Piece
```

作为火控炮位来源。

运行时确认该对象本质上是 Tactical Map 上可拖动的炮位标记 / UI 表示，因此不适合作为真实物理炮位权威源。

Smart 后来把权威炮位改为：

```text
TurretLocation.position
```

再通过：

```text
mapSurface.InverseTransformPoint(...)
```

转换到 Tactical Map local coordinate。

当前实现细节统一维护在：

```text
ARCHITECTURE_PLANNING.md
→ 当前坐标模型
```

本 History 只保留为什么旧链路被替换、哪些实验被放弃。

---

## 坐标实验中明确放弃的方案

曾经尝试过：

- Z flatten；
- movement-delta calibration；
- 围绕 map plane 的实验修正。

这些方案后来被认为无效 / 不可靠，并从稳定修复中移除。

当前坐标原则：

```text
TurretLocation world position
→ mapSurface.InverseTransformPoint
→ Tactical Map local coordinate
```

不要无证据恢复 flatten-Z 等旧实验。

---

## EntityLocation / 战场实体真实坐标

研究其他 fork / PR 时确认过：

```text
Fire Mission Root
→ child
→ EntityLocation
→ GameObject / Transform
→ transform.position
```

游戏中的敌方 / 友军 / FDC / IronNest 等战场实体，可以通过实体 GameObject 的 Transform 取得世界位置。

### gxpppp 雷达方案

`gxpppp/IronNestFCS` 的雷达会：

```text
遍历 Fire Mission Root
→ GetComponent<EntityLocation>()
→ child.position
```

获取单位真实世界坐标。

### 上游 PR #22

上游 PR #22：

https://github.com/svr2kos2/IronNestFCS/pull/22

主题是雷达 / 自动追踪单位。

其中也使用了 `TurretLocation`，但仍通过更新 `Player Turret Piece` 让旧火控链继续工作。

Smart 的结构则更直接：

```text
TurretLocation
→ fire control
```

### Smart 当前边界

虽然 Smart 能枚举：

```text
Fire Mission Root → EntityLocation
```

但：

> **EntityLocation 当前不作为 T1～T4 自动目标来源。**

目标仍来自玩家放置的地图标记。

---

## PR #22 对战术自动化边界的启示

PR #22 实现过：

- 定时扫描敌军 / 友军 / 基地；
- 自动识别；
- 自动优先级；
- 自动选择前 4 个目标；
- 自动放置地图标记。

上游维护者关闭该方向的核心理由是：

> FCS 的目标是让玩家从重复炮台操作中解放出来，而不是替玩家完成测绘和射击规划。

这与 Smart 的核心理念一致。

因此未来利用 EntityLocation：

可以考虑：

- 调试；
- 真实状态读取；
- 位置同步；
- 玩家明确选择实体后的跟踪；
- 辅助验证。

不建议直接进入核心：

- 自动找敌人；
- 自动威胁评估；
- 自动战术排序；
- 自动任务生成。

---

## 上游装填可靠性 PR #21

Smart 中发现并验证的一部分共性装填问题，被拆成了最小 upstream fix：

https://github.com/svr2kos2/IronNestFCS/pull/21

标题：

```text
fix: 修复连续射击时装填机构未复位导致的卡死
```

状态：

```text
Merged
```

合并时间：

```text
2026-08-10
```

该 PR 没有把 Smart 架构带进上游，只修共性机械状态问题。

核心修复包括：

- 绑定 `ArtilleryReloadController`；
- 不再把 `CurrentStateIndex` 简单等价为“装填机构空闲”；
- 下一次装填前检查真实机制状态；
- `WaitBackToIdle()` 等待真实恢复；
- 选弹 / 装弹前等待 reload ready。

本地稳定性压测时，双炮连续自动射击约 40 发，原有卡死未再次复现。

---

## 上游贡献原则

Smart 已经发生明显架构分叉。

以后：

> **不要把 Smart 整套架构直接往上游塞。**

正确方式：

```text
在 Smart 发现 / 验证共性 Bug
↓
回到 upstream 当前架构
↓
重新做最小修复
↓
只提交与 Smart 架构无关的必要改动
```

PR #21 是理想模板。

适合 upstream 的通常是：

- 明确的机械 Bug；
- 通用的状态判定修复；
- 与 Smart 调度架构无关的小修。

不适合直接 upstream 的通常是：

- PersistentLoadingSystem 整体架构；
- TaskGunMatcher / Smart Dispatcher；
- physical settlement 整套执行模型；
- Smart 的 F9 语义重构；
- 大规模模块化搬迁。

---

## 重要 v1.2.0 开发提交

### ready / physical settlement 基线

```text
b18fca608c89d3ab0c27ebfeab8533970da48c50
refactor: rebuild ready-arm handshake from master
```

### follower arm 资格集中

```text
dc594e6f802558f9395a800c48af504aed278823
refactor: centralize follower arm eligibility
```

### Review lead time

```text
ad5aac52a8cdbae28c4c10251e4080b64e23f168
tune: increase review lead before arm
```

### same-azimuth tolerance

```text
9bd6a8779313deaaa816e5e9ad1b0507c4e7a02c
tune: widen same-azimuth follower tolerance
```

### loadout-aware pending matching

```text
aac01bcc54bb197005aae18d1fa22e6d3d6ffdeb
fix: match pending tasks against gun loadout
```

### selected-only ballistic materialization

```text
8806b9ce21b0b65fb9ae1296a8e6afd1d69f7927
fix: defer ballistic sticker generation until after matching
```

### recovering gun redispatch

```text
7410afd29cf26058216fc03bfaa962dcc018e90b
fix: retry pending tasks when recovering gun becomes free
```

### v1.2.0 release preparation

```text
477942f6948f9a375170a78838a8ff88297dbb50
chore: prepare v1.2.0 release
```

该提交包括：

- 单 plan 文案修正；
- 删除 AimingSpeedProbe；
- 删除 PhysicalStateProbe；
- 删除 TriggerConsoleProbe；
- v1.2.0 release notes。

### 合入 master

```text
PR #17
release: prepare v1.2.0 scheduling and physical fire settlement
```

merge 后 master：

```text
a905dd909930baf230084b0cde908694a2dc6265
```

### 正式 release commit

```text
8197223ced619525d78d4b7bc24f7a30aacc28e7
release: v1.2.0
```

---

## 更早的重要 Smart / upstream 历史

### Smart：权威炮位改为 TurretLocation

```text
d9df1754c6b020be501a773f6dc387b59937cd5f
fix: use TurretLocation as firing origin
```

### Smart：Host teardown 清理

```text
PR #14
fix: clean host runtime before Unity teardown
```

squash merge：

```text
5523cf8adbd3eef7c08492e4699cc13f7e29d0ec
```

### Smart：v1.1.8 pending queue 可靠性

v1.1.8 主要建立了：

- blocked Pending 不消失；
- 队首 blocked 不阻塞后续兼容任务；
- FirePlan accepted 后才移除 Pending；
- 调度触发在 planning 期间不会丢失；
- 快速 shell / charge-range 筛选；
- Pending HUD 原因。

### upstream：共性装填修复

```text
PR #21
https://github.com/svr2kos2/IronNestFCS/pull/21
```

upstream merge commit：

```text
7c4c2ed2f4c228c47cc0c7a75d023ad31e3c34bb
```

### upstream：雷达边界案例

```text
PR #22
https://github.com/svr2kos2/IronNestFCS/pull/22
```

---

## 实验分支与不要误合的历史

### `feature/physical-fire-settlement`

曾存在独立实验分支用于 physical settlement 探索。

它不是当前 master 的继续开发基线。

当前 v1.2.0 已经在 `refactor/prearm-ready-handshake` → PR #17 → master 中形成正式实现。

不要因为旧实验分支还有提交就直接重新 merge。

### `fix/elevation-control-mechanism`

曾作为机制层诊断 / 实验分支。

结论：

> 额外机制层改动没有必要。

除非出现新的实机证据，不要把旧实验方案重新带回 master。
