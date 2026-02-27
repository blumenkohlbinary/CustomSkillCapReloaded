# CustomSkillCap Reloaded

A rewrite of the original [Custom Skill Cap](https://www.nexusmods.com/madgamestycoon2/mods/12?file_id=30) mod by **Aerin_the_Lion** for **Mad Games Tycoon 2**.

This version is fully compatible with the current game build, fixes a critical bug present in the original, and uses more robust IL patching techniques that survive game updates.

## What's Different from the Original

| | Original (v1.0.1) | Reloaded |
|---|---|---|
| Secondary skill jump bug | Present — skills snap to max value above ~90 | Fixed via Safety-Postfix clamp |
| IL transpiler approach | Blindly replaces all `ldc.r4 100f` constants | Context-aware: only patches comparison and assignment sites |
| UI bar patching | Hardcoded instruction indices (breaks on game updates) | Value-based matching (update-resistant) |
| Bar color thresholds | Incorrect at high skill caps (all skills appear green) | Normalized correctly across all 15 UI classes |
| Compatibility | Written for 3-year-old game version | Verified against current Assembly-CSharp.dll |
| Talent perk detection | `perks` read as `byte[]` (wrong type, always ignored) | Correctly read as `bool[]` |
| Sandbox mode | Not handled | Fully supported (all skills share MajorCap) |
| Job Market | Not affected | Configurable high-skill applicants with power-curve rarity |

## Features

- Configurable major skill cap (primary discipline of each employee)
- Configurable minor skill cap (all other disciplines)
- Separate cap for employees with the Talent perk
- Skill bars and color thresholds scale correctly with custom caps in all UI screens
- CEO character creation screen reflects configured caps
- Job Market skill boost: rare high-skill applicants with exponential rarity curve
- Sandbox mode: all skills correctly share MajorCap (no primary/secondary distinction)
- Cross-config validation: MinorCap is always clamped to MajorCap
- Compatible with [Job Market Tweaker](https://www.nexusmods.com/madgamestycoon2/mods/10) by Aerin_the_Lion
- Drop-in replacement: reads existing config file from the original mod

## Requirements

| Dependency | Version |
|---|---|
| Mad Games Tycoon 2 | tested on current Steam build |
| BepInEx | 5.4.x (Mono) |
| BepInEx.ConfigurationManager | any recent version (optional, for in-game UI) |

## Installation

> **Note:** If you have the original `CustomSkillCap.dll` installed, remove it first. Both plugins share the same GUID and cannot run simultaneously.

1. Install [BepInEx 5.x (Mono)](https://github.com/BepInEx/BepInEx/releases) into your Mad Games Tycoon 2 game folder.
2. Remove `BepInEx/plugins/CustomSkillCap.dll` if present.
3. Download `CustomSkillCapReloaded.dll` from the [Releases](../../releases) page.
4. Place the DLL into `BepInEx/plugins/CustomSkillCapReloaded/`.
5. Launch the game. Settings appear in the ConfigurationManager (`F1` by default).

Existing settings from the original mod are automatically picked up — no reconfiguration needed.

## Configuration

All options are available in `BepInEx/config/me.Aerin_the_Lion.Mad_Games_Tycoon_2.plugins.CustomSkillCap.cfg` or via the in-game ConfigurationManager.

| Section | Key | Default | Vanilla | Description |
|---|---|---|---|---|
| General | `Enabled` | true | — | Enable or disable the mod entirely |
| Skill Caps | `Major Skill Cap` | 500 | 100 | Maximum value for an employee's primary skill |
| Skill Caps | `Minor Skill Cap` | 350 | 50 | Maximum value for secondary skills (no Talent perk) |
| Skill Caps | `Talent + Minor Skill Cap` | 450 | 60 | Maximum value for secondary skills with the Talent perk |
| Job Market | `High Skill Chance` | 10 | — | Chance (%) that a job market applicant gets boosted skills. Uses a power-curve: high skills are exponentially rarer. 0 = disabled |

### Job Market Skill Boost

When enabled, a percentage of job market applicants receive a boosted primary skill using a cubic power-curve (`strength = roll^3`). This makes high values exponentially rare:

| Skill Range | Approximate Share |
|---|---|
| 0–95 (normal) | ~90% |
| 95–200 | ~8% |
| 200–350 | ~1.5% |
| 350–450 | ~0.4% |
| 450–500 | ~0.1% |

Only the primary skill (matching the employee's profession) is boosted. Secondary skills remain at vanilla values.

**Job Market Tweaker compatibility:** This mod's Postfix runs after JMT's Prefix, so both work together without conflict.

## Building from Source

### Prerequisites

- .NET SDK (any version supporting `net46` target)
- The following DLLs copied into the `lib/` folder:
  - `Assembly-CSharp.dll` — from `Mad Games Tycoon 2_Data/Managed/`
  - `netstandard.dll` — from `Mad Games Tycoon 2_Data/Managed/`

```
lib/
  Assembly-CSharp.dll
  netstandard.dll
```

### Build

```bash
dotnet build -c Release
```

The compiled DLL is automatically copied to `BepInEx/plugins/CustomSkillCapReloaded/` after a successful build.

## Technical Notes

### The Secondary Skill Jump Bug

The original mod's transpiler replaced every occurrence of `ldc.r4 100f` in `characterScript.Learn()` without checking the surrounding context. This caused certain internal calculations — not related to the hard cap — to use the configured cap value instead of the vanilla constant, resulting in secondary skills jumping directly to the maximum value when they exceeded approximately 90.

This rewrite uses context-aware instruction matching: a `ldc.r4 100f` is only replaced when followed by a branch instruction (`ble.un.s` / `ble.un`) or a field store (`stfld`), which are the actual hard cap sites. A Safety-Postfix additionally clamps all skills after every `Learn()` call as a secondary guarantee.

### Color Normalization

Vanilla uses fixed thresholds (30/70 on a 0–100 scale) to color skill values red/yellow/green. With custom caps, all skills would always appear green. This mod normalizes skill values to 0–100 before the vanilla color method runs. This is applied across all 15 UI classes that have their own `GetValColor()` method.

### Update Resistance

The original UI bar patches used hardcoded instruction indices. Any change to the method body — even adding a single local variable — would shift all indices and silently produce incorrect bar fill values. This rewrite matches instructions by their float operand values (`0.01f`, `0.5f`, `0.6f`), which are semantically tied to the vanilla 0–100 scale and are unlikely to change.

## Credits

- **Aerin_the_Lion** — original [Custom Skill Cap](https://www.nexusmods.com/madgamestycoon2/mods/12?file_id=30) mod concept and implementation
- **Tobias Feddersen** — rewrite, bug fix, compatibility update

## License

This project is released under the [MIT License](LICENSE).
