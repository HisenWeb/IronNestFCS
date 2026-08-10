# IronNestFCS Enhanced

**English** | [简体中文](README.zh-CN.md)

> An enhanced automated fire-control-system mod for **Iron Nest: Heavy Turret Simulator**.
>
> Built on top of [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS), with full-release compatibility, dual-gun FirePlan scheduling, physical-state recovery, persistent loading, and F9 hot reload.

[Original Demo Video](https://www.bilibili.com/video/BV1xc7F6WEET/) · [IRON NEST on Steam](https://store.steampowered.com/app/4300500/) · [MelonLoader](https://melonwiki.xyz/)

IronNestFCS Enhanced reads the game's actual map, turret, gun, loading mechanism, ballistic calculator, and trigger-console state directly. It does **not** use OCR or screen recognition.

A typical fire mission looks like this:

```text
Target marker
    ↓
Ballistic solution
    ↓
FirePlan scheduling
    ↓
Independent left/right loading + elevation
    ↓
Shared turret azimuth
    ↓
Review Console → Arm
    ↓
Manual fire or Auto Fire
    ↓
Physical shot confirmation and recovery
```

<!-- Hero screenshot will be added here later. -->

---

## Highlights

- **Dual-gun FirePlan scheduling** — both guns can prepare in parallel while sharing one turret azimuth.
- **Persistent loading across F9** — accepted loading transactions survive TaskSystem hot reloads.
- **Physical-state-aware planning** — chamber contents, powder charge, reload state, elevation, and current turret azimuth are treated as the source of truth.
- **One-time firing-order comparison** — two unpaired FirePlans are compared once; an already-compared second plan is not displaced by later arrivals.
- **Rolling gun-slot reuse** — one gun can accept the next task as soon as it physically recovers, without waiting for the other gun.
- **Reliable ballistic calculation** — waits for a stable result instead of blindly reading the calculator after a fixed delay.
- **Per-task ballistic cache** — if left and right candidates use the same shell and charge, the calculator is operated only once, avoiding duplicate calculation stickers.
- **Manual or automatic firing** — supports both player-triggered fire and `Auto Fire`.
- **Max Charge mode** and automatic shell / powder purchasing.
- **F9 TaskSystem hot reload** for faster development and recovery.
- **Categorized diagnostic logs** for planning, ballistics, loading, firing order, turret control, trigger control, and failures.

---

## Quick Start

### Requirements

- Iron Nest: Heavy Turret Simulator
- MelonLoader for IL2CPP
- .NET SDK matching [global.json](global.json) when building from source

### Current installation method

A prebuilt public release package is not included yet. For now, install from source with the deployment script.

Clone or download this repository, close the game, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Deploy.ps1 -Configuration Release
```

The default game directory is:

```text
D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator
```

To use a different location:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Deploy.ps1 `
  -GameDir "D:\Your\Iron Nest Heavy Turret Simulator" `
  -Configuration Release
```

The script builds and deploys:

| File | Destination |
| --- | --- |
| `IronNestFCS.dll` | `<GameDir>\Mods\` |
| `IronNestFCS.Abstractions.dll` | `<GameDir>\UserLibs\` |
| `IronNestFCS.Logic.dll` | `<GameDir>\UserData\IronNestFCS\` |

Restart the game once after a full Host / Abstractions deployment.

The current Host banner should include:

```text
IronNestFCS v1.1.1
Press F9 to hot reload TaskSystem.
```

---

## How to Use

1. Enter a scene containing the heavy artillery turret and Tactical Map.
2. Move one of the numbered map markers **T1–T4** onto a target.
3. Choose the desired shell type in the FCS panel.
4. Optionally enable `Auto Fire` and/or `Max Charge`.
5. Click `T1`, `T2`, `T3`, or `T4` to submit the corresponding fire mission.
6. The FCS reads the physical state, solves the trajectory, assigns a gun, loads ammunition, sets elevation, rotates the turret, and prepares the trigger console.
7. In manual mode, fire when the system has completed Review + Arm. In `Auto Fire`, the FCS performs the final trigger action automatically.

Multiple tasks can be submitted in succession. The left and right guns are scheduled independently and reuse freed gun slots as soon as the physical gun state allows it.

---

## Core Architecture

The current FCS has two top-level runtime systems.

### TaskSystem

`IronNestFCS.Logic.dll` is hot-reloadable and owns the current firing intent:

- T1–T4 task queue
- one planning snapshot per planning round
- ballistic solving
- FirePlan creation
- left/right gun-slot assignment
- one-time First / Second ordering
- gun elevation
- shared turret azimuth
- Review Console / Arm / Fire
- UI and task history

Pressing **F9** destroys and recreates the TaskSystem.

### Persistent LoadingSystem

The stable Host `IronNestFCS.dll` owns accepted loading transactions:

- independent left/right shell loading
- independent left/right powder loading
- accepted `Gun + Shell + Charge` transactions
- physical loading-stage tracking
- continued loading while TaskSystem is being reloaded

Its lifecycle is independent from the TaskSystem.

The design rule is simple:

> **The physical game state is the highest source of truth. TaskSystem represents firing intent; Persistent LoadingSystem owns accepted loading transactions.**

---

## FirePlan

A `FirePlan` is the fixed scheduling and execution unit for one task on one gun.

It contains:

- Task
- Gun (`Left` / `Right`)
- Shell
- Charge
- Elevation
- Target Azimuth
- ETA / planning metadata

Once a FirePlan is created, its **Task + Gun** binding is not dynamically rewritten. If the assignment must change, the current plan is discarded and the task is planned again from a fresh physical snapshot.

### Planning snapshot

When a task enters the active planning round, the FCS reads the current state once:

- left gun physical state
- right gun physical state
- persistent loading transactions
- actual left/right elevation
- actual current turret azimuth

The target azimuth stored in the FirePlan remains fixed. The current turret azimuth is **not** continuously re-read to dynamically reshuffle plans.

This matters because the turret does not automatically return to zero after firing. A later task therefore plans from the turret's real current position instead of assuming `0°`.

---

## Dual-Gun Scheduling

Each gun has one execution slot. Loading and elevation are local to each gun; turret azimuth is shared.

### One-time firing-order comparison

When two FirePlans that have never been compared are present together:

```text
[A unpaired, B unpaired]
        ↓
compare once
        ↓
First / Second fixed
```

The system does not continuously re-arbitrate them.

Example:

```text
A / B were already compared
A fires
C enters the newly free gun slot
```

The state becomes:

```text
B = already compared
C = not yet compared
→ no B/C re-comparison
→ B remains next
```

After B finishes, another new plan can pair with C and be compared once.

If only one FirePlan exists and there is no queued or currently-planning task, that plan commits directly instead of waiting indefinitely for a future task.

### Rolling gun-slot reuse

The system does not require both guns to finish an entire pair before accepting more work.

As soon as one gun physically returns to a reusable state, its slot can accept the next task while the other gun continues its existing plan.

---

## Parallel Preparation and Shared Azimuth

Local preparation is independent:

```text
Left : Loading → physical LoadedReady → Elevation
Right: Loading → physical LoadedReady → Elevation
```

Elevation starts from the **real `LoadedReady` state**, not from an ETA prediction.

The shared turret does not wait for loading to finish. As soon as the firing order is committed:

```text
First committed
      ↓
start Azimuth immediately
```

Loading and elevation continue in parallel.

The final firing sequence requires both:

```text
Azimuth Ready + Elevation Ready
```

before Review / Arm / Fire.

### ETA

ETA is used for planning and First / Second comparison only. Physical state remains the final execution gate.

Conceptually:

```text
Local ETA      = Loading ETA + Elevation ETA
Fire-ready ETA = max(Local ETA, Azimuth ETA)
```

An already-running persistent loading transaction is allowed to make real progress while ballistic planning is happening. A fresh load does not receive "free" elapsed planning time before the FirePlan actually exists.

Measured planning baselines from the current game build are approximately:

```text
Turret azimuth speed ≈ 4°/s
Gun elevation speed  ≈ 2°/s
Fresh load to LoadedReady ≈ 32s
```

These values are estimates for planning; actual firing still waits for physical readiness.

---

## Ballistic Calculator

The FCS drives the game's native Ballistic Calculator directly:

- sets target distance and direction
- chooses shell and powder charge
- supports `Max Charge`
- performs the full Calculate Down / Up interaction
- waits for the output to stabilize
- rejects an unconfirmed result instead of firing with a suspected stale elevation

### Per-task ballistic cache

A single task may evaluate both guns before choosing one. If both candidates need the same shell and charge, the physical calculator is only operated once:

```text
T1 Left  = HE C2 → Calculate → E=30.08°
T1 Right = HE C2 → cache hit → reuse E=30.08°
```

This prevents one submitted task from generating two identical calculation stickers.

If the candidates genuinely require different solutions, such as `HE C2` versus `HE C3`, each unique solution is calculated separately.

---

## F9 Hot Reload and Persistent Loading

F9 intentionally separates **firing intent** from **accepted physical loading work**:

```text
TaskSystem FirePlans / queue / order
→ destroyed and recreated

Persistent LoadingSystem accepted transaction
→ preserved and continues

Physical chamber / reload state
→ always remains the factual source of truth
```

This has been tested while loading was already in progress:

```text
Left  : FinalSequence
Right : CloseShellGuide
        ↓
       F9
        ↓
new TaskSystem starts
        ↓
accepted loading transactions continue
        ↓
Left / Right → LoadedReady
```

After F9, newly submitted tasks read the current chamber, powder charge, reload state, elevation, and turret azimuth before creating new FirePlans.

---

## Trigger Console

Some full-release control objects do not expose a sufficiently reliable logical ON/OFF state, so the FCS also verifies the physical Transform positions of switches and arm levers.

The final sequence is:

```text
Local Ready + Azimuth Ready
        ↓
Review Console
        ↓
Arm Left / Right
        ↓
Manual Fire or Auto Fire
        ↓
confirm the real chamber transition
```

---

## Supported Features

- T1–T4 fire missions
- dual-gun rolling task execution
- target distance / direction reading
- automatic ballistic solving
- automatic ammunition and powder purchasing
- automatic shell / powder loading
- independent left/right elevation
- shared turret azimuth control
- one-time FirePlan order comparison
- manual fire / Auto Fire
- Max Charge
- physical-state recovery
- Alt-Tab focus protection
- F9 TaskSystem hot reload
- categorized diagnostics

Supported ammunition includes multiple in-game shell types such as:

`AP / HCHE / HE / STAR / SMK / PCLM ...`

The internal game enum may still use `PLCM`; the FCS UI uses the displayed spelling `PCLM`.

---

## Verified In-Game Behavior

The current architecture has been validated in the game for the following cases:

- one task with identical left/right `Shell + Charge` candidates performs only one physical ballistic Calculate
- left and right guns load independently
- each gun starts elevation after its own physical `LoadedReady`
- shared azimuth starts immediately after firing order commitment instead of waiting for loading
- two unpaired FirePlans are compared exactly once
- an already-compared Second plan is promoted without re-comparison
- gun slots are reused on a rolling basis
- new plans use the turret's real current azimuth after previous shots
- F9 destroys TaskSystem state without clearing an already-loaded physical round
- F9 during active `CloseShellGuide / FinalSequence` loading preserves the accepted transaction until `LoadedReady`

---

## Project Structure

| Project | Role | Description |
| --- | --- | --- |
| `IronNestFCS` | **Stable Host** | MelonLoader entry point, Persistent LoadingSystem, Logic lifecycle, F9 |
| `IronNestFCS.Abstractions` | **Shared contract** | Host / Logic interfaces and loading transaction types |
| `IronNestFCS.Logic` | **TaskSystem** | map, FirePlanner, FirePlanExecutor, turret/elevation, trigger, UI |
| `IronNestFCS.CustomRecords` | **Independent mod** | custom record-player functionality; no direct dependency on the FCS core |

Key files:

- [PersistentLoadingSystem.cs](IronNestFCS/PersistentLoadingSystem.cs)
- [LogicReloader.cs](IronNestFCS/LogicReloader.cs)
- [FirePlan.cs](IronNestFCS.Logic/Scheduling/FirePlan.cs)
- [FirePlanner.cs](IronNestFCS.Logic/Scheduling/FirePlanner.cs)
- [TaskDispatcher.cs](IronNestFCS.Logic/Scheduling/TaskDispatcher.cs)
- [FirePlanExecutor.cs](IronNestFCS.Logic/Execution/FirePlanExecutor.cs)
- [FSC.cs](IronNestFCS.Logic/FSC.cs)

The detailed refactor design record is available in [docs/FSC_MODULARIZATION_PLAN.md](docs/FSC_MODULARIZATION_PLAN.md).

---

## Development

For Logic-only changes:

```powershell
dotnet build .\IronNestFCS.Logic\IronNestFCS.Logic.csproj -c Debug `
  -p:GameDir="D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator"
```

Then return to the game and press **F9**. A full process restart is not required for normal `IronNestFCS.Logic` changes.

When the Host or Abstractions contract changes, run the full deployment script and restart the game once.

---

## Diagnostic Logs

Logs are written to:

```text
<GameDir>\UserData\IronNestFCS\Logs\yyyy-MM-dd\run-HHmmss-pidNNNN\
```

| File | Purpose |
| --- | --- |
| `all.log` | all events |
| `dispatch.log` | task queue, planning, FirePlan creation |
| `ballistic.log` | calculator input, stable result, planning cache |
| `reload.log` | persistent loading, chamber state, reload controller state |
| `order.log` | one-time First / Second ordering and promotion |
| `turret.log` | target markers and shared azimuth |
| `trigger.log` | Review / Arm / Fire and physical switch state |
| `problems.log` | warnings and failures that need attention |
| `arbitration.log` | legacy compatibility category; normally mostly empty in the current architecture |

For troubleshooting, start with:

```text
problems.log
→ relevant category log
→ all.log
```

---

## Credits

IronNestFCS Enhanced is based on the original [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS). Credit for the original implementation belongs to its original author and contributors.

This fork focuses on full-release compatibility, reliability, physical-state recovery, persistent loading, and the current dual-gun FirePlan architecture.

## License

Released under the repository's [MIT License](LICENSE).
