# IronNestFCS Smart

**English** | [简体中文](README.zh-CN.md)

A smart automated fire-control-system mod for **Iron Nest: Heavy Turret Simulator**.

After you place a target marker on the Tactical Map, IronNestFCS Smart can handle most of the firing workflow for you: ballistic calculation, gun assignment, ammunition loading, elevation, turret azimuth, trigger preparation, and optional automatic firing.

[Download the latest release](https://github.com/HisenWeb/IronNestFCS/releases/latest) · [Original author's demo video](https://www.bilibili.com/video/BV1xc7F6WEET/) · [IRON NEST on Steam](https://store.steampowered.com/app/4300500/) · [MelonLoader](https://melonwiki.xyz/)

> The original demo video is useful for understanding the basic interaction flow. The Smart fork has a different scheduler, state handling, and additional player-facing UI features.

---

## Design philosophy

**Automate the work, not the tactics.**

You choose the targets, ammunition, and what goes into the task queue. Smart executes the plan.

Submit T1–T4 missions in sequence to build a task queue. If the plan or queue is wrong, press **F9** to clear the current tasks, waiting queue, and firing order, then submit them again. Physical loading already in progress keeps going, and the new plan uses the guns' real current state.

---

## What this mod does

The basic idea is simple: **you decide where to shoot and which ammunition to use; the FCS handles most of the repetitive heavy-turret work.**

A typical fire mission becomes:

```text
Move red map marker 1–4 to the target position
        ↓
Select ammunition
        ↓
Click the matching T1–T4 button on the right to submit
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

**F9 is the replan key.** If you placed the wrong target, submitted the wrong mission, or simply want to change the current queue or firing order:

```text
Press F9
  ↓
clear current tasks / waiting queue / firing order
  ↓
any physical loading sequence already in progress keeps going
  ↓
move red markers 1–4 again and resubmit the matching T1 / T2 / T3 / T4
  ↓
the FCS replans from the gun and turret state that really exists now
```

Use F9 to **abandon the current plan and issue a new one**. A shell/powder load already accepted by the loading system is not forcibly undone; the new missions plan around the real chamber and turret state.

The mod reads the game's real objects and physical state directly. It does **not** use OCR or screen recognition.

---

## Highlights

- **Build a task queue with T1–T4** — submit several missions in the order you want, and the FCS keeps both guns working through them.
- **Press F9 to replan** — clear the current tasks, waiting queue, and firing order, then submit a new plan without pretending an already-started physical load never happened.
- **Place a target and let the FCS do the repetitive work** — calculate the shot, choose a gun, load ammunition, aim, and prepare the trigger for you.
- **Both guns can stay busy at the same time** — one gun can be loading or aiming while the other is doing its own preparation.
- **It uses where the turret really is now** — after a shot, the turret does not have to return to zero before the next target is planned.
- **Ballistic calculation is less likely to use an old result** — the mod waits for the calculator output to settle before trusting it.
- **One target will not needlessly press Calculate twice for the same solution** — identical left/right possibilities reuse the result already obtained.
- **Can buy missing shells and powder automatically** when the selected task needs them.
- **Auto Fire** can complete the final firing action for you, or you can leave it off and fire manually.
- **Max Charge** can prefer the highest usable powder charge.
- **English and Simplified Chinese UI** are both included.

---

## What is different from the original IronNestFCS?

This project continues directly from the source of [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS).

First, the important part: **the original already automates most of the fire-control workflow.** T1–T4 queueing, assigning tasks to a free gun, both guns preparing at the same time, automatic shell/powder purchasing, Auto Fire, Max Charge, and an F9 Logic reload are already present in v1.0.6.

**Smart is mainly about making that existing automation behave better when several things are happening at once: repeated missions, both guns being busy, F9 replanning, or a gun being halfway through loading.**

| What happens in game | Original IronNestFCS | IronNestFCS Smart |
| --- | --- | --- |
| **Both guns can take a new mission** | Gives the mission to the first free gun slot; when both are free, Left is chosen first | Looks at what each gun currently contains, its elevation, and where the turret is pointing, then chooses the gun that better fits the shot |
| **Both guns are preparing their next shots** | Each task waits for access to the shared turret; whichever task gets it first continues first | Estimates which shot can become ready sooner and tries to fire that one first; once the order is chosen, later tasks do not casually reshuffle it |
| **You submitted the wrong task and press F9** | The current tasks, waiting queue, and the coroutines running those tasks are cleared when Logic reloads | The task list and firing order reset, but an actual shell/powder loading sequence that has already started can keep going; you can then submit T1–T4 again |
| **After F9, a gun is already loaded or only half loaded** | The new Logic instance has no separate in-progress loading transaction to inherit | Reads what is physically there now — empty, shell loaded, fully loaded, or still loading — and plans the new task around that real state |
| **The ballistic calculator is slow to update** | Waits fixed short delays and then reads the elevation display | Waits until Calculate is actually usable and watches the result until it has settled; if the result cannot be trusted, it will not use a suspicious old value |
| **Review / Arm controls were already moved** | The task flow directly clicks those controls | Checks the real physical position first and changes only what is needed, reducing the chance of toggling an already-correct control the wrong way |
| **You press F9 while the turret is still turning toward an old target** | The old task coroutine stops, but the original wrapper does not explicitly clear the old game-side rotation target | Cancels the stale target, holds the turret at its current real direction, and lets newly submitted tasks plan from there |
| **You want a Chinese UI** | No separate English/Chinese localization layer | Player-facing UI can switch between Simplified Chinese and English |

In short: **the original already knows how to “auto-fire the turret.” Smart focuses on keeping that automation sensible when missions overlap, you replan with F9, or the guns are already in the middle of doing something.**

---

## Requirements

You need:

1. **Iron Nest: Heavy Turret Simulator**
2. **MelonLoader for IL2CPP**
3. One IronNestFCS Smart release package

Install and run the game with MelonLoader at least once before installing the mod.

---

## Download and installation

Open the [latest GitHub Release](https://github.com/HisenWeb/IronNestFCS/releases/latest) and download **one** language package:

```text
IronNestFCS-Smart_v*_en-US.zip   English UI
IronNestFCS-Smart_v*_zh-CN.zip   Simplified Chinese UI
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

On the **left side** of the Tactical Map, use one of the red numbered markers:

```text
1 / 2 / 3 / 4
```

Drag it to the map position you want to attack. Red marker `1` corresponds to task `T1`, `2` to `T2`, and so on.

![Red target markers 1–4 on the left side of the Tactical Map](docs/images/ironnest_usage-target-markers.jpg)

### 3. Select ammunition

Use the FCS controls on the right side of the map to choose the shell type for this task.

Optional controls:

- **Auto Fire** — the FCS performs the final firing action automatically.
- **Max Charge** — prefers the highest usable powder charge.

### 4. Submit the fire mission

Click the task button on the right that matches the marker you placed:

![T1–T4 mission submit buttons on the right side of the Tactical Map](docs/images/ironnest_usage-submit-buttons.jpg)

```text
red 1 → T1
red 2 → T2
red 3 → T3
red 4 → T4
```

The mission is submitted immediately. You can move another red marker and click its matching T1–T4 button; missions submitted in succession enter the task queue.

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

### 5. Check FCS status

An `IronNest Fire Control System` status panel appears in the **top-left corner** of the game screen. It is mainly for monitoring what the FCS is doing; you do not need to operate the FCS from this panel.

It shows:

- the physical state of the left and right guns, or their current T mission and progress;
- current mission azimuth, range, charge, elevation, and elapsed time;
- current firing-order / priority status;
- whether **Auto Fire** and **Max Charge** are enabled;
- the pending queue and the missions waiting in it;
- session completed / success / failed counts and recent mission history; failed missions also show the failure reason.

The status panel is visible in the top-left corner of the target-marker screenshot above.

### 6. Fire

- With **Auto Fire ON**, the FCS completes the final firing action when the gun and turret are physically ready.
- With **Auto Fire OFF**, wait until the FCS has prepared the shot, then perform the final fire action manually.

The two guns are scheduled independently and a recovered gun can immediately accept another task.

### 7. Replan with F9

Press **F9** to clear the current tasks, waiting queue, and firing order. Then reposition red markers 1–4 and submit a new T1–T4 plan.

A physical shell/powder load that has already started is not forcibly cancelled by F9. New missions read the real chamber, elevation, and turret position and plan from that state.

---

## F9: replan missions

Press **F9** whenever you want to abandon the current task plan and build a new one.

This is useful when:

- you placed the wrong target marker;
- you submitted the wrong T1–T4 mission;
- you want to change the waiting queue or firing order;
- you want the FCS to plan again from the current real gun and turret state.

After F9, reposition the red markers you need and submit the matching T1–T4 missions again.

Important: **F9 resets the plan, not physical reality.** If a gun is already loading shell/powder, that sequence continues; the new plan uses the resulting real physical state.

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

By default, only the compact `problems.log` is written. Normal play no longer continuously generates the full categorized diagnostic set.

To reproduce a bug with full diagnostics, open:

```text
<GameDir>/UserData/IronNestFCS/diagnostics.txt
```

change it to:

```text
on
```

save the file, then press **F9**. Full logging will resume with `all.log`, `dispatch.log`, `ballistic.log`, `reload.log`, `order.log`, `arbitration.log`, `turret.log`, `trigger.log`, and `problems.log`. When troubleshooting is finished, change `diagnostics.txt` back to `off` and press F9 again.

If `diagnostics.txt` does not exist, the mod creates it automatically with `off` as the default.

When reporting a bug, include the relevant log folder and describe what the turret was doing when the problem happened.

---

## For developers

The source code remains split into a stable Host, shared Abstractions, and hot-reloadable Logic assembly. The player-facing **F9 replan** is implemented by reloading the TaskSystem/Logic while Host-owned physical loading remains alive. Development notes are available in [docs/FSC_MODULARIZATION_PLAN.md](docs/FSC_MODULARIZATION_PLAN.md).

The repository also includes:

- `tools/Deploy.ps1` — build and deploy a development copy
- `tools/Build-ReleasePackages.ps1` — generate the bilingual release ZIPs

---

## Credits

IronNestFCS Smart is based on the original [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS). Credit for the original implementation belongs to its original author and contributors.

## License

Released under the repository's [MIT License](LICENSE).