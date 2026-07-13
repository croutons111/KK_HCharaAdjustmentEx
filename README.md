# KK_HCharaAdjustmentEx

**English** | [日本語](README.ja.md)

> A BepInEx plugin for Koikatsu H-scenes that **automatically aligns** characters whose body size is outside the normal range, and lets you **save your own manual position tweaks** per character + pose.

---

## Overview

In H-scenes, characters can end up misaligned in certain poses — especially bodies made with height-slider-unlock mods (very short or very tall). KK_HCharaAdjustmentEx does two things:

- **Automatic adjustment** — characters whose body scale falls outside the normal maker range are aligned to the pose automatically. Characters within the normal range are left untouched (the game already handles those correctly).
- **Manual adjustment** — you can fine-tune any character's position yourself and save it. The saved position is re-applied automatically for that same character + pose.

Adjustments are applied **smoothly** (no sudden teleporting).

> This plugin is an **add-on** to **KK_HCharaAdjustment** (by deathweasel), which provides the on-screen position guides. This plugin adds the automatic alignment and the saving / re-applying of your manual tweaks. See [License](#license).

---

## Requirements

| Item | Requirement |
|---|---|
| Game | Koikatsu (HF Patch) |
| Executable | `Koikatu.exe` (full) / `KoikatuVR.exe` (apply-only) |
| Framework | BepInEx 5.4.x |
| **Required dependency** | **KK_HCharaAdjustment** (provides the guides) |

> KK_HCharaAdjustment provides the guides (Female 1 = `O` / Female 2 = `P` / Male = `I`). Manual editing (saving) works only in the non-VR build; VR is apply-only.

---

## Installation

1. Make sure **KK_HCharaAdjustment** (the base plugin) is installed.
2. Download the latest `KK_HCharaAdjustmentEx.dll` from [Releases](../../releases).
3. Place it in your `BepInEx/plugins/` folder.
4. Start the game.

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

---

## Configuration

Open the BepInEx **ConfigurationManager** (default `F1`).

| Section | Setting | Default | Description |
|---|---|---|---|
| General | Enabled | ON | Enable/disable the whole plugin (OFF = vanilla) |
| Auto Adjust | Enabled | ON | Automatic position adjustment |
| Auto Adjust | Shift Cap | OFF | Limit over-correction (prevents floating / separation) |
| Auto Adjust | Mouth Shift Scale | 0.8 | Strength of mouth alignment (0–1, lower = subtler) |
| Manual Adjust | Buttons Show | ON | Show on-screen Save/Reset buttons |
| Manual Adjust | Position Auto Save | ON | Auto-save a moved position when the pose changes |
| Manual Adjust | Position Save | RCtrl+S | Key to save the manual adjustment |
| Manual Adjust | Position Reset | RCtrl+RShift+S | Key to reset the manual adjustment |

---

## Notes

- Automatic adjustment assumes the male's body size is within the normal (vanilla) range.
- Automatic adjustment is not perfect — some service acts (oral, handjob, etc.) in particular may still be misaligned. Use manual adjustment in those cases.
- Saved data is a plain text file under `BepInEx/config/` and can be edited or deleted by hand (restart the game to reflect changes).

---

## License

This plugin is an extension of **KK_HCharaAdjustment**, included in [KK_Plugins_CN](https://github.com/PopChicken/KK_Plugins_CN) (author: PopChicken).

Distributed under the **GNU General Public License v3.0**, same as the base plugin.

- Modification and redistribution must follow the terms of GPL v3.0.
- Source code must be provided when distributing.
- Keep the copyright and license notices intact.

[GNU General Public License v3.0](LICENSE)

---

## Disclaimer

This mod is adult (R18) content for H-scenes. Use at your own risk.
