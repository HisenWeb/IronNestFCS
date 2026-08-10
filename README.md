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

- **Automatic ballistic calculation** using the game's own ballistic calculator.
- **Dual-gun scheduling** — the left and right guns can load and prepare independently.
- **Rolling gun reuse** — a recovered gun can take the next task without waiting for the other gun to finish.
- **Physical-state-aware control** — the real chamber, powder charge, reload state, elevation, and turret position are treated as the source of truth.
- **Persistent loading across F9** — an accepted loading operation continues even when the TaskSystem is hot-reloaded.
- **Reliable ballistic result handling** — waits for a stable result instead of blindly reading an old calculator value.
- **Duplicate-solve avoidance** — identical candidates inside one task reuse the same ballistic result.
- **Automatic shell and powder purchasing** when needed.
- **Max Charge mode**.
- **Manual fire or Auto Fire**.
- **English and Simplified Chinese UI**.
- **Diagnostic logs** for troubleshooting.

---

## Compared with the original IronNestFCS

This project is based on [svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS). The Enhanced fork keeps the same basic goal — automating the heavy turret fire-control workflow — while substantially reworking the runtime behavior for the current game release.

The main additions/reworks are:

| Area | IronNestFCS Enhanced |
| --- | --- |
| Game compatibility | Updated for the current full-release game behavior |
| Dual-gun control | FirePlan-based scheduling with independent left/right preparation |
| Task flow | Rolling gun-slot reuse instead of waiting for both guns as one batch |
| Reload handling | Persistent loading transactions survive F9 TaskSystem reloads |
| State handling | Plans from the actual physical chamber/reload/elevation/turret state |
| Ballistics | Stable-result checking and per-task duplicate-solve cache |
| Hot reload | F9 reloads TaskSystem logic without discarding accepted loading work |
| UI | English / Simplified Chinese |
| Distribution | Ready-to-install release ZIPs |

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
