# IronNestFCS Smart — Development, Release & Operations

> 状态快照：2026-08-13（UTC+8）  
> 当前正式版本：**v1.2.0**  
> 默认分支：`master`  
> 发布基线：`8197223ced619525d78d4b7bc24f7a30aacc28e7`（`release: v1.2.0`）  
> 本组文档由原单文件 `IronNestFCS-Smart_项目背景与设计来源.md` 拆分而来。

> 定位：日志、语言、发布、Nexus/README、License、构建流程以及未来 ChatGPT/Codex 工作规则。

## 文档导航

- `PROJECT_CONTEXT.md` — 先读；长期原则与总体模型。
- `ARCHITECTURE_PLANNING.md` — Planning / Matching / Admission。
- `ARCHITECTURE_EXECUTION.md` — Execution / Review / Arm / Physical Settlement。
- `KNOWN_ISSUES_AND_ROADMAP.md` — 已知问题与后续优化。
- `PROJECT_HISTORY.md` — 历史决策与设计来源。
- `RELEASE_AND_OPERATIONS.md` — 构建、日志、发布与维护规则。

**权威顺序：当前仓库代码与实机证据 > `PROJECT_CONTEXT.md` > 当前架构文档 > Roadmap > History。**

---

## 日志策略

Smart 的日志系统目标是：

> **正常玩家默认低噪音；出现问题时可以打开完整分类诊断。**

配置文件：

```text
<GameDir>/UserData/IronNestFCS/diagnostics.txt
```

默认：

```text
off
```

正常模式主要保留：

```text
problems.log
```

开启完整诊断：

```text
on
```

然后按 F9 重载 Logic，或重启游戏。

支持开启值：

```text
on
true
1
yes
full
```

详细模式分类包括：

```text
all.log
dispatch.log
ballistic.log
reload.log
order.log
arbitration.log
turret.log
trigger.log
problems.log
```

### v1.2.0 发版清理

以下临时开发探针已经删除：

```text
AimingSpeedProbe
PhysicalStateProbe
TriggerConsoleProbe
```

它们曾用于：

- 实测方位 / 仰角速度；
- 记录装填状态时间线；
- 观察 Review / Arm 控件物理姿态。

这些发现已经进入稳定代码后，不应继续让探针污染 release build。

注意：

> diagnostics 开关只控制 Smart 自己的分类 / 镜像诊断；MelonLoader 自己的 Latest.log 仍可能包含直接 MelonLogger 输出。

---

## UI 语言策略

当前 release 已经不再使用独立中英文安装包，也不再依赖 `language.txt`。

公开 README：

```text
README.md         English
README.zh-CN.md   简体中文
```

游戏内 Smart UI：

```text
zh-CN
或 English fallback
```

当前语言检测思路：

- 读取游戏自身一个已本地化的 TTI label；
- 精确检测中文 `左` → 选择简体中文 UI；
- 其他语言 / 找不到 probe / 识别失败 → English fallback；
- 已缓存的游戏 label 会周期性重新检查。

因此现在：

> **一个 universal release package 服务所有玩家。**

不要重新恢复：

- `language.txt`；
- en-US / zh-CN 两套 ZIP；
- 让用户手工维护 Mod 语言文件。

除非未来语言系统整体重新设计。

---

## 当前正式发布状态

当前最新正式 Release：

```text
IronNestFCS Smart v1.2.0
```

GitHub：

https://github.com/HisenWeb/IronNestFCS-Smart/releases/tag/v1.2.0

当前 release commit：

```text
8197223ced619525d78d4b7bc24f7a30aacc28e7
release: v1.2.0
```

当前 `master` 与 v1.2.0 release commit 对齐。

Release 文件：

```text
IronNestFCS-Smart_v1.2.0.zip
SHA256SUMS.txt
```

ZIP 是 universal package。

### v1.2.0 主要能力变化

- physical fire settlement；
- Review / Arm handshake 重构；
- side-effect-free Task × Gun eligibility；
- 双炮全局 matching；
- selected-only ballistic materialization；
- 避免正常成功路径的重复 firing-solution stickers；
- recovery-aware Pending redispatch；
- 单炮继续执行、另一炮恢复后自动继续 Pending；
- 发版前清理临时 probes。

---

## Nexus Mods 分发定位

Nexus 页面：

https://www.nexusmods.com/ironnest/mods/32

对普通玩家的定位建议保持：

> **Overbuilt for the easy missions. Built for the messy ones.**

Smart 在单任务干净场景下可能与 upstream 感觉非常接近。

它真正体现价值的是：

- full queue；
- mixed ammunition；
- two guns in different states；
- recovering gun；
- F9 replan；
- simultaneous / partial physical fire；
- Pending recovery。

Nexus 页面优先回答：

1. 这个 Mod 能做什么？
2. 为什么复杂连续任务下更可靠？
3. 怎么安装？
4. F9 到底做什么？
5. Smart 与 upstream 的核心区别是什么？
6. 源码 / 上游来源在哪里？

不要把 Nexus Description 写成内部架构文档。

---

## README 的定位

README 应同时满足普通玩家和开发者。

### 普通玩家需要快速知道

- Mod 能干什么；
- 怎么安装；
- 怎么使用；
- F9 是什么；
- Pending 是什么；
- Smart 与 upstream 根本区别是什么。

### 开发者可以在后半部分看到

- Stable Host / reloadable Logic；
- PersistentLoadingSystem；
- TaskDispatcher / Planner / Matcher / Executor；
- 构建和 release 脚本；
- 设计文档。

“与上游对比”部分继续做减法。

只保留根本差异：

- 计划与物理状态边界；
- 双炮协调；
- 状态判断 / 恢复；
- F9 语义。

不要把每个 Review / Arm 小修、日志修正或单独 Bug 都塞进主对比表。

---

## Attribution / License

当前 `LICENSE` 基本原则：

```text
MIT License

Copyright (c) 2026 svr2kos2
Smart fork modifications Copyright (c) 2026 HisenWeb
```

仓库另有：

```text
NOTICE.md
```

对外措辞：

可以：

```text
Based on:
https://github.com/svr2kos2/IronNestFCS
```

或：

```text
based on the upstream svr2kos2/IronNestFCS repository
```

不要：

```text
Original author: svr2kos2
```

除非未来有可靠历史资料证明。

---

## 内部命名不要跟品牌名一起改

公开品牌：

```text
IronNestFCS Smart
```

内部兼容名称继续保持：

```text
IronNestFCS.dll
IronNestFCS.Logic.dll
IronNestFCS.Abstractions.dll
UserData/IronNestFCS/
namespace IronNestFCS
```

不要为了品牌统一随意重命名内部路径 / assembly / namespace。

原因：

- 破坏安装兼容；
- 增加发布脚本维护成本；
- F9 Logic loader 依赖既有结构；
- 没有实际业务收益。

---

## 构建、部署与 Release

主要工具：

```text
tools/Version.ps1
tools/Release.ps1
tools/Deploy.ps1
tools/Build-ReleasePackages.ps1
打包上传.bat
```

开发机游戏目录历史上出现过两种路径：

```text
D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator
D:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator
```

因此如果直接 build 某个 csproj，没有正确传 `GameDir`，会出现大量级联缺引用错误。

开发部署优先使用：

```text
tools\Deploy.ps1
```

正式 release 工具会：

- 要求在 `master`；
- 检查 clean working tree；
- 检查 GitHub repo / login；
- 更新版本；
- build universal package；
- 生成 SHA256SUMS；
- 创建 `release: vX.Y.Z` commit；
- push master；
- tag；
- 发布 GitHub Release。

### 重要版本号规则

如果当前版本是 `1.1.8`，release 脚本留空会自动 patch +1 成 `1.1.9`。

因此像 v1.2.0 这种 minor bump 必须显式输入：

```text
1.2.0
```

---

## 给未来 ChatGPT / Codex 会话的工作规则

### 1. 当前代码优先

讨论“现在代码到底怎样”时，先读取：

```text
HisenWeb/IronNestFCS-Smart
master
```

不要只依赖本背景文件。

### 2. 不要 yes-man

对于架构建议：

- 客观判断是否真的需要改；
- 区分 bug、设计缺陷、优化和风格偏好；
- 不因为用户提出一个方向就默认它一定正确；
- 如果当前实现已经足够简单，不要为了拆分而拆分。

### 3. 上游状态实时查询

涉及 upstream：

- PR；
- commit；
- release；
- README；
- issue；

必须重新查询，不假设本快照永远有效。

### 4. 不要把 Smart 当成“更多自动化功能版”

Smart 的核心是：

```text
physical reality
planning
matching
execution scheduling
shared controls
UI
```

之间的边界。

### 5. F9 不得重新耦合装填

任何设计如果导致：

```text
F9
→ 已接受的 PersistentLoading transaction 被 TaskSystem 重置
```

都违背当前架构。

### 6. 优先读真实状态

如果游戏已经暴露：

- Transform；
- Controller；
- Chamber；
- Reload mechanism；
- Turret；
- physical control pose；

优先使用现实，不要在 Logic 里猜。

### 7. 不要让 Eligibility 有物理副作用

资格判断阶段不要驱动：

- Ballistic Calculate；
- Purchase；
- Loading；
- Trigger；
- Arm。

### 8. 不要用 LocalReady 重新排序 scheduler

`current / next` 和 follower 是不同概念。

不要把 follower 重新做成另一个 `_next`。

### 9. Review 和 Arm 不要串成硬 barrier

不要要求 5 个 Review 开关全部完成后才 Arm。

当前目标是并行逻辑意图 + 底层物理串行执行。

### 10. 对上游贡献保持最小化

Smart 架构性代码通常不适合直接 PR upstream。

共性 Bug 应重新基于 upstream 实现最小修复。

### 11. 不自动化战术

自动执行可以。

自动替玩家决定攻击谁原则上不进入核心 Smart。

### 12. 发版前清理探针

临时 Probe / 埋点只用于验证具体假设。

当结论进入稳定代码后，应评估：

- 是否删除 probe；
- 是否保留低成本 diagnostics；
- 是否会污染正常玩家日志。

---

## 重要链接

### Smart

GitHub：

https://github.com/HisenWeb/IronNestFCS-Smart

Latest Release：

https://github.com/HisenWeb/IronNestFCS-Smart/releases/latest

v1.2.0：

https://github.com/HisenWeb/IronNestFCS-Smart/releases/tag/v1.2.0

Nexus Mods：

https://www.nexusmods.com/ironnest/mods/32

### Upstream

https://github.com/svr2kos2/IronNestFCS

### 已合并共性装填修复

https://github.com/svr2kos2/IronNestFCS/pull/21

### 雷达 / 战术自动化边界案例

https://github.com/svr2kos2/IronNestFCS/pull/22

### Smart v1.2.0 合入 PR

https://github.com/HisenWeb/IronNestFCS-Smart/pull/17
