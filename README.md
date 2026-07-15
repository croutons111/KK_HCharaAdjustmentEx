# KK_HCharaAdjustmentEx

**English** | [日本語](README.ja.md)

> A BepInEx plugin for Koikatsu H-scenes that **automatically aligns** characters whose body size is outside the normal range, and lets you **save your own manual position tweaks** per character + pose.

---

## Overview

In H-scenes, characters can end up misaligned in certain poses — especially bodies made with height-slider-unlock mods (very short or very tall). KK_HCharaAdjustmentEx does two things:

- **Automatic adjustment** — characters whose body scale falls outside the normal maker range are aligned to the pose automatically. Characters within the normal range are left untouched (the game already handles those correctly).
- **Manual adjustment** — you can fine-tune any character's position yourself and save it. The saved position is re-applied automatically for that same character + pose.

Adjustments are applied **smoothly** (no sudden teleporting).

An optional companion DLL, **KK_HCharaAdjustmentEx.VR**, adds **VR controller fine-tuning** on top: in the VR H-scene you can nudge the girl's position with your right controller (formerly the separate KK_HCharaPosVR plugin). VR tweaks are intentionally **temporary** — a position that looks right in first person is often off in third person, so they are never saved and reset on pose change.

> This plugin is an **add-on** to **KK_HCharaAdjustment** (by DeathWeasel1337), which provides the on-screen position guides. This plugin adds the automatic alignment and the saving / re-applying of your manual tweaks. See [License](#license).

---

## Requirements

| Item | Requirement |
|---|---|
| Game | Koikatsu (HF Patch) |
| Executable | `Koikatu.exe` (full) / `KoikatuVR.exe` (apply + VR fine-tuning) |
| Framework | BepInEx 5.4.x |
| **Required dependency** | **KK_HCharaAdjustment** (provides the guides) |
| Optional (VR) | `KK_HCharaAdjustmentEx.VR.dll` — VR controller fine-tuning. Verified with Meta Quest 2 (Oculus Link / Air Link); other headsets untested |

> KK_HCharaAdjustment provides the guides (Female 1 = `O` / Female 2 = `P` / Male = `I`). Manual editing (saving) works only in the non-VR build; in VR the saved/automatic adjustments are applied, and the optional VR DLL adds temporary controller fine-tuning.

---

## Installation

1. Make sure **KK_HCharaAdjustment** (the base plugin) is installed.
2. Download the latest `KK_HCharaAdjustmentEx.dll` from [Releases](../../releases).
3. Place it in your `BepInEx/plugins/` folder.
4. **VR users:** also place `KK_HCharaAdjustmentEx.VR.dll` in `BepInEx/plugins/` if you want controller fine-tuning (it only loads in `KoikatuVR.exe`).
5. Start the game.

> If you previously used **KK_HCharaPosVR**, remove its DLL — it has been integrated into `KK_HCharaAdjustmentEx.VR.dll` and the two will fight over character positions if both are installed.

---

## How to Use

### Automatic adjustment
Nothing to do — it just works. Characters whose body size is outside the normal range are aligned automatically during H-scenes. You can turn it off with `Auto Adjust > Enabled` in the config.

### Manual adjustment
1. In an H-scene, show the guide for the character you want to move (Female 1 = `O` / Female 2 = `P` / Male = `I`).
2. Grab the guide and move the character.
3. **Save** it in any of these ways:
   - Press **Right Ctrl + S**, or
   - Click the **Save** button (shown at the top-center of the screen while a guide is up), or
   - Simply **change to another pose** — a character you moved is saved automatically (can be turned off).
4. To undo the saved tweak for the current pose: press **Right Ctrl + Right Shift + S**, or click **Reset**.

- Saved tweaks are stored **per character combination and per pose**, and take priority over the automatic adjustment for that pose.
- Saving requires the non-VR build.

### VR fine-tuning (optional `KK_HCharaAdjustmentEx.VR.dll`)

Works in the VR H-scene. All control is on the **right controller's A button**:

| Action | Result |
|---|---|
| **Hold** A (0.2 s or longer) | The selected girl follows your controller. Release to settle. |
| **Double-tap** A (within 0.4 s) | Switch between Female 1 and Female 2 (the controller vibrates to confirm; ignored without a Female 2). |

- The tweak is applied **on top of** the automatic / saved adjustment.
- It is **temporary by design**: never saved, and reset on pose change, position (spot) change, and scene end. The selection resets to Female 1 each H-scene.

---

## Configuration

Open the BepInEx **ConfigurationManager** (default `F1`).

| Section | Setting | Default | Description |
|---|---|---|---|
| General | Enabled | ON | Enable/disable the whole plugin (OFF = vanilla) |
| Auto Adjust | Enabled | ON | Automatic position adjustment (desktop / non-VR) |
| Auto Adjust | Enabled (VR) | ON | Automatic position adjustment in VR |
| Auto Adjust | Precise Sampling | ON | Refine the automatic adjustment by measuring a hidden reference body (desktop / non-VR). OFF = fast approximation only |
| Auto Adjust | Precise Sampling (VR) | OFF | Same, for VR. Default OFF — the reference body can cause frame drops in VR; the fast approximation is used instead |
| Auto Adjust | Shift Cap | OFF | Limit over-correction (prevents floating / separation) |
| Auto Adjust | Mouth Shift Scale | 0.8 | Strength of mouth alignment (0–1, lower = subtler) |
| Manual Adjust | Buttons Show | ON | Show on-screen Save/Reset buttons |
| Manual Adjust | Position Auto Save | ON | Auto-save a moved position when the pose changes |
| Manual Adjust | Position Save | RCtrl+S | Key to save the manual adjustment |
| Manual Adjust | Position Reset | RCtrl+RShift+S | Key to reset the manual adjustment |

`KK_HCharaAdjustmentEx.VR` has its own config file:

| Section | Setting | Default | Description |
|---|---|---|---|
| General | Enabled | ON | Enable/disable VR controller fine-tuning (OFF = vanilla) |
| Female 1 | Move Scale | 1.0 | Movement multiplier for Female 1 |
| Female 2 | Move Scale | 1.0 | Movement multiplier for Female 2 |

---

## Notes

- Automatic adjustment assumes the male's body size is within the normal (vanilla) range.
- Automatic adjustment is not perfect — some service acts (oral, handjob, etc.) in particular may still be misaligned. Use manual adjustment in those cases.
- **Lesbian scenes are not auto-adjusted.** The automatic adjustment aligns to the male as its reference, so girl-on-girl scenes have no reference to align to. Manual adjustment (and the VR fine-tuning) still works there.
- **3P scenes are supported but less reliable.** Both girls are auto-adjusted, but 3P has not been verified as thoroughly as 1-on-1 — the girl who is not currently engaged may be adjusted less sensibly. Use manual adjustment if something looks off.
- Saved data is a plain text file under `BepInEx/config/` and can be edited or deleted by hand (restart the game to reflect changes).
- VR fine-tuning: the X button (left controller) is not usable — on Meta Quest 2 its press cannot be detected through the SteamVR legacy input API, so only the right controller's A button is supported.

---

## License

This plugin is an extension of **KK_HCharaAdjustment**, included in [KK_Plugins](https://github.com/IllusionMods/KK_Plugins) (author: DeathWeasel1337).

Distributed under the **GNU General Public License v3.0**, same as the base plugin.

- Modification and redistribution must follow the terms of GPL v3.0.
- Source code must be provided when distributing.
- Keep the copyright and license notices intact.

[GNU General Public License v3.0](LICENSE)

---

## Disclaimer

This mod is adult (R18) content for H-scenes. Use at your own risk.
