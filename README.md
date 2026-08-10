# IronNestFCS Enhanced

**English** | [简体中文](README.zh-CN.md)

An enhanced automated fire-control-system mod for **Iron Nest: Heavy Turret Simulator**.

After you place a target marker on the Tactical Map, IronNestFCS Enhanced can handle most of the firing workflow for you: ballistic calculation, gun assignment, ammunition loading, elevation, turret azimuth, trigger preparation, and optional automatic firing.

[Download the latest release](https://github.com/HisenWeb/IronNestFCS/releases/latest) · [Original author's demo video](https://www.bilibili.com/video/BV1xc7F6WEET/) · [IRON NEST on Steam](https://store.steampowered.com/app/4300500/) · [MelonLoader](https://melonwiki.xyz/)

> The original demo video is useful for understanding the basic interaction flow. The Enhanced fork has a different internal scheduler and additional UI/features.

---

## What this mod does

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
Load shell + powder
        ↓
Set elevation + turret azimuth
        ↓
Review / Arm
        ↓
Manual fire or Auto Fire
```

The mod reads the game's real objects and physical state directly. It does **not** use OCR or screen recognition.

---

## Highlights

- **Place a target and let the FCS do the repetitive work** — calculate the shot, choose a gun, load ammunition, aim, and prepare the trigger for you.
- **Both guns can stay busy at the same time** — one gun can be loading or aiming while the other is doing its own preparation.
- **Better for several targets in a row** — when one gun finishes and becomes usable again, it can immediately start the next queued target instead of waiting for both guns to finish together.
- **It uses where the turret really is now** — after a shot, the turret does not have to return to zero before the next target is planned.
- **Pressing F9 does not throw away a load already in progress** — the task logic can reload while an accepted shell/powder loading sequence keeps going.
- **Ballistic calculation is less likely to use an old result** — the mod waits for the calculator output to settle before trusting it.
- **One target will not needlessly press Calculate twice for the same solution** — identical left/right possibilities reuse the result already obtained.
- **Can buy missing shells and powder automatically** when the selected task needs them.
- **Auto Fire** can complete the final firing action for you, or you can leave it off and fire manually.
- **Max Charge** can prefer the highest usable powder charge.
- **English and Simplified Chinese UI** are both included.

---

## What is different from the original IronNestFCS?

This project is based on [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS) and keeps the same basic idea: make the heavy-turret fire-control workflow much less manual.

The Enhanced fork mainly changes what happens when you start sending several real missions through the system. The differences a normal player is most likely to notice are:

| In normal play | IronNestFCS Enhanced |
| --- | --- |
| Using both guns | Tries to keep the left and right guns working independently instead of treating them as one batch |
| Several targets in a row | A gun that has recovered can immediately take the next target while the other gun is still busy |
| After the turret has already rotated | Plans the next shot from the turret's actual current direction; it does not assume the turret returned to `0°` |
| Reloading the FCS with F9 | An ammunition load that was already accepted keeps running instead of being discarded with the task logic |
| Ballistic calculator timing | Waits for a stable calculator result instead of relying only on a fixed delay |
| Repeated calculation | Reuses the same ballistic answer when one target produces identical left/right solutions |
| Installing the mod | Provides ready-to-install English and Chinese ZIP packages instead of requiring users to build the project themselves |
| UI | Includes both English and Simplified Chinese player-facing text |

In short: **the original project provides the automation idea and foundation; Enhanced focuses on making that automation behave more smoothly during continuous dual-gun use on the current game release.**

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

---

## F9 hot reload

Press **F9** to reload the TaskSystem logic.

This clears/recreates the current task-planning state, but an already accepted physical loading operation is owned separately and continues running.

This is useful if you want to reload the FCS logic without restarting the whole game.

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
