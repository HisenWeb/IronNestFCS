# IronNestFCS Enhanced

**English** | [简体中文](README.zh-CN.md)

An enhanced automated fire-control-system mod for **Iron Nest: Heavy Turret Simulator**.

After you place a target marker on the Tactical Map, IronNestFCS Enhanced can handle most of the firing workflow for you: ballistic calculation, gun assignment, ammunition loading, elevation, turret azimuth, trigger preparation, and optional automatic firing.

[Download the latest release](https://github.com/HisenWeb/IronNestFCS/releases/latest) · [Original author's demo video](https://www.bilibili.com/video/BV1xc7F6WEET/) · [IRON NEST on Steam](https://store.steampowered.com/app/4300500/) · [MelonLoader](https://melonwiki.xyz/)

> The original demo video is useful for understanding the basic interaction flow. The Enhanced fork has a different internal scheduler and additional UI/features.

---

## What this mod does

The basic idea is simple: **you decide where to shoot and which ammunition to use; the FCS handles most of the repetitive heavy-turret work.**

A typical fire mission becomes:

```text
Place T1–T4 target marker
        ↓
Select ammunition
        ↓
Submit target
        ↓
Ballistic calculation
        ↓
Choose left/right gun
        ↓
Buy missing ammunition if needed
        ↓
Load shell + powder
        ↓
Set elevation + turret azimuth
        ↓
Review / Arm
        ↓
Manual fire or Auto Fire
```

You can also submit several T1–T4 missions in succession. The FCS distributes work between the two guns, and a gun that recovers can continue with later targets.

**F9 can also be used as a practical “reset current fire missions” key.** If you placed the wrong target, submitted the wrong mission, or simply want the current tasks to be planned again:

```text
Press F9
  ↓
clear and recreate current tasks / waiting queue / firing order
  ↓
any physical loading sequence already in progress keeps going
  ↓
place the targets again and resubmit T1 / T2 / T3 / T4
  ↓
the FCS replans from the gun and turret state that really exists now
```

So F9 is not only a development hot-reload key. In normal play, you can use it to **abandon the current task arrangement and issue the missions again**. A shell/powder load already accepted by the loading system is not forcibly undone; the new missions plan around the real chamber and turret state after the reset.

The mod reads the game's real objects and physical state directly. It does **not** use OCR or screen recognition.

---

## Highlights

- **Place a target and let the FCS do the repetitive work** — calculate the shot, choose a gun, load ammunition, aim, and prepare the trigger for you.
- **Both guns can stay busy at the same time** — one gun can be loading or aiming while the other is doing its own preparation.
- **Better for several targets in a row** — when one gun finishes and becomes usable again, it can immediately start the next queued target instead of waiting for both guns to finish together.
- **It uses where the turret really is now** — after a shot, the turret does not have to return to zero before the next target is planned.
- **F9 can reset the current missions and let you submit them again** — useful if a target or task was wrong; a physical ammunition load already in progress is not forcibly interrupted.
- **Ballistic calculation is less likely to use an old result** — the mod waits for the calculator output to settle before trusting it.
- **One target will not needlessly press Calculate twice for the same solution** — identical left/right possibilities reuse the result already obtained.
- **Can buy missing shells and powder automatically** when the selected task needs them.
- **Auto Fire** can complete the final firing action for you, or you can leave it off and fire manually.
- **Max Charge** can prefer the highest usable powder charge.
- **English and Simplified Chinese UI** are both included.

---

## What is different from the original IronNestFCS?

This fork is built directly on [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS), so the comparison matters.

The original v1.0.6 already has many of the features people may associate with this fork: **T1–T4 task queueing, automatic assignment to a free gun, both guns preparing in parallel, a gun taking another queued task after it recovers, automatic shell/powder purchasing, Auto Fire, Max Charge, and F9 hot reload.** Enhanced does not claim those as new features.

The main source-level changes are in **how a task is planned, how two pending shots are ordered, and how the system recovers from the real game state after F9 or an interrupted sequence**:

| Area | Original IronNestFCS | IronNestFCS Enhanced |
| --- | --- | --- |
| Choosing a gun for a new task | The queued task is assigned immediately to the first free slot: Left if free, otherwise Right | Queueing stores only the mission. When planning starts, the FCS reads both guns' current chamber/loading/elevation state plus the current turret azimuth, evaluates the viable left/right choices, and chooses from their expected readiness/alignment |
| Deciding which of two shots fires first | Each task reserves the shared turret through a lock; there is no comparison of the two tasks' predicted fire-ready times | Two unpaired FirePlans are compared once. The plan expected to become fire-ready first is committed first; once the order is decided, later arrivals do not reshuffle that pair |
| What F9 resets | F9 reloads the Logic assembly; shutdown stops the Logic-owned coroutines and clears the task queue and left/right task slots | F9 reloads the TaskSystem, but accepted `Gun + Shell + Charge` loading transactions live in the stable Host and continue across the reload |
| Replanning with ammunition already in the gun | The normal task routine proceeds through its own selected-shell loading flow; there is no separate persistent loading transaction for the next Logic instance to inherit | Planning recognizes an active loading transaction, a fully loaded round, a shell-loaded gun, or an empty ready gun, and plans around the physical state that actually exists |
| Ballistic calculator result | The original sets the dials, waits fixed short delays, presses Calculate, waits again, then reads the elevation display | Enhanced waits for the Calculate control to become usable, completes the physical down/up click, watches the output until it is stable, and fails the solve if a trustworthy result cannot be confirmed |
| Review / Arm controls | The task routine directly clicks the review switches and arming lever | Enhanced reads the actual physical switch/lever poses, changes only controls that are not already in the required state, and reconciles those controls after F9 |
| Turret state after a task reset | The Logic coroutine is stopped on reload, but the original turret wrapper does not explicitly cancel the old game-side `DesiredRotation` | On rebind, Enhanced holds the turret at its current physical azimuth to cancel stale task intent; new plans then start from the live turret position and rotation commands have cancellation/timeout handling |
| Player UI | No separate localization layer | Player-facing UI can be switched between `en-US` and `zh-CN` |

In short: **the original already automates the heavy turret and already supports two-gun queued operation. Enhanced mainly replaces the original “assign a free gun and run the task coroutine” model with physical-state-aware planning, one-time fire-order comparison, and F9-persistent loading/recovery.**

---

## Requirements

You need:

1. **Iron Nest: Heavy Turret Simulator**
2. **MelonLoader for IL2CPP**
3. One IronNestFCS Enhanced release package

Install and run the game with MelonLoader at least once before installing the mod.

---

## Download and installation

Open the [latest GitHub Release](https://github.com/HisenWeb/IronNestFCS/releases/latest) and download **one** language package:

```text
IronNestFCS-Enhanced_v*_en-US.zip   English UI
IronNestFCS-Enhanced_v*_zh-CN.zip   简体中文 UI
```

Both packages contain the same mod binaries. Only the default UI language is different.

### Install

1. Close the game.
2. Open the downloaded ZIP.
3. Extract **everything directly into the game root directory**.
4. Allow Windows to merge the `Mods`, `UserLibs`, and `UserData` folders if prompted.
5. Start the game normally through MelonLoader.

After extraction, these files should exist:

```text
<GameDir>/Mods/IronNestFCS.dll
<GameDir>/UserLibs/IronNestFCS.Abstractions.dll
<GameDir>/UserData/IronNestFCS/IronNestFCS.Logic.dll
<GameDir>/UserData/IronNestFCS/language.txt
```

Do **not** place the whole ZIP inside the `Mods` folder.

---

## How to use it in game

### 1. Enter a heavy-turret scene

Enter a scene that contains the heavy artillery turret and Tactical Map. The FCS will bind to the required game controls after the scene loads.

### 2. Place a target marker

On the Tactical Map, move one of the numbered markers:

```text
T1 / T2 / T3 / T4
```

to the target you want to fire at.

### 3. Select ammunition

Use the FCS ammunition selector to choose the shell type for the task.

Optional controls:

- **Auto Fire** — the FCS performs the final firing action automatically.
- **Max Charge** — prefers the highest usable powder charge.

### 4. Submit the fire mission

Click the matching `T1`, `T2`, `T3`, or `T4` button in the FCS controls.

The FCS will then automatically:

```text
read target
→ read current gun/turret state
→ solve ballistics
→ assign a gun
→ load ammunition
→ set elevation
→ rotate turret
→ prepare Review / Arm
```

### 5. Fire

- With **Auto Fire ON**, the FCS completes the final firing action when the gun and turret are physically ready.
- With **Auto Fire OFF**, wait until the FCS has prepared the shot, then perform the final fire action manually.

You can submit multiple targets in succession. The two guns are scheduled independently and a recovered gun can immediately accept another task.

### 6. Reset and issue the missions again

Press **F9** to reset the current task system. The waiting queue, current planning state, and firing order are recreated; after the reload, you can place targets again and click `T1`, `T2`, `T3`, or `T4` again.

A physical shell/powder load that has already started is not forcibly cancelled by F9. New missions read the real chamber, elevation, and turret position after the reset and plan from that state.

---

## F9: reset missions / hot reload

Press **F9** to reload the current task system and reset the current mission arrangement.

This is useful when:

- you placed the wrong target marker;
- you submitted the wrong T1–T4 mission;
- you want to abandon the current waiting order;
- you want the FCS to plan again from the current real gun and turret state.

After F9 finishes, you can immediately submit new T1–T4 missions again.

Important: **F9 resets task intent, but it does not forcibly cancel a physical loading operation that the loading system has already accepted.** If a gun is already loading shell/powder, that sequence continues; newly submitted missions plan around the resulting real physical state.

---

## Change UI language

The selected language is stored in:

```text
<GameDir>/UserData/IronNestFCS/language.txt
```

Use either:

```text
en-US
```

or:

```text
zh-CN
```

Save the file and press **F9**, or restart the game.

---

## Troubleshooting

If the FCS does not appear or does not bind correctly:

1. Confirm MelonLoader is installed for the IL2CPP game build.
2. Confirm the three DLLs are in the exact paths shown above.
3. Restart the game after changing the Host or Abstractions DLLs.
4. After the turret scene is fully loaded, press **F9** once.
5. Check the diagnostic logs.

Logs are stored under:

```text
<GameDir>/UserData/IronNestFCS/Logs/
```

Start with `problems.log`, then check the relevant category log or `all.log`.

When reporting a bug, include the relevant log folder and describe what the turret was doing when the problem happened.

---

## For developers

The source code remains split into a stable Host, shared Abstractions, and hot-reloadable Logic assembly. Development notes are available in [docs/FSC_MODULARIZATION_PLAN.md](docs/FSC_MODULARIZATION_PLAN.md).

The repository also includes:

- `tools/Deploy.ps1` — build and deploy a development copy
- `tools/Build-ReleasePackages.ps1` — generate the bilingual release ZIPs

---

## Credits

IronNestFCS Enhanced is based on the original [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS). Credit for the original implementation belongs to its original author and contributors.

## License

Released under the repository's [MIT License](LICENSE).
