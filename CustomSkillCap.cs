/*
 * Custom Skill Cap - Reloaded  v2.4.1
 * Rewrite based on original by Aerin_the_Lion
 * GUID: me.Aerin_the_Lion.Mad_Games_Tycoon_2.plugins.CustomSkillCap  (compatible)
 *
 * Fixes the bug: Secondary skills jump to the maximum value (e.g. 500)
 * when they exceed approximately 90.
 *
 * Patches:
 *   1. characterScript.GetSkillCap()       - Postfix:     correct secondary cap
 *   2. characterScript.GetSkillCap_Skill() - Postfix:     correct primary cap
 *   3. characterScript.Learn()             - Transpiler:   100 -> MajorSkillCap in hard limit
 *                                            Safety-Postfix: clamps secondary skills after each tick
 *   4. GUI_Main.SetBalkenEmployee()        - Transpiler:   bar proportions
 *   5. GUI_Main.SetBalkenArbeitsmarkt()    - Transpiler:   bar proportions (job market)
 *   6. GUI_Main.GetValColorEmployee()      - Prefix:       normalize color thresholds
 *   6b.14x GetValColor() in employee UIs  - Prefix:       normalize color thresholds (overview, tooltip, etc.)
 *   7. Menu_NewGameCEO.GetSkillCap()       - Postfix:      CEO creation screen
 *   8. Menu_NewGameCEO.SetBalken()         - Transpiler:   CEO skill bars
 *   9. charArbeitsmarkt.Create()          - Postfix:      job market skill boost (power-curve rarity)
 */

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace CustomSkillCapReloaded
{
    // =========================================================================
    // Plugin entry point
    // =========================================================================
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInProcess("Mad Games Tycoon 2.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID    = "me.Aerin_the_Lion.Mad_Games_Tycoon_2.plugins.CustomSkillCap";
        public const string NAME    = "Custom Skill Cap";
        public const string VERSION = "2.4.1";

        internal static new ManualLogSource Logger;

        // ---- Config (same section/key names as original for compatibility) ---
        public static ConfigEntry<bool>  CFG_IS_ENABLED;
        public static ConfigEntry<float> CFG_MajorSkillCap;
        public static ConfigEntry<float> CFG_MinorSkillCap;
        public static ConfigEntry<float> CFG_TalentMinorSkillCap;
        public static ConfigEntry<int>   CFG_MarketBoostChance;

        // Cached derived values (recomputed on config change)
        private static float _fillFactor;
        private static float _minorFillMax;
        private static float _talentMinorFillMax;

        private Harmony harmony;

        private void Awake()
        {
            Logger = base.Logger;

            CFG_IS_ENABLED = Config.Bind(
                "General", "Enabled", true,
                "Enable or disable the mod entirely.");

            CFG_MajorSkillCap = Config.Bind(
                "Skill Caps", "Major Skill Cap", 500f,
                new ConfigDescription(
                    "Maximum for the employee's primary skill (Vanilla: 100)",
                    new AcceptableValueRange<float>(100f, 9999f)));

            CFG_MinorSkillCap = Config.Bind(
                "Skill Caps", "Minor Skill Cap", 350f,
                new ConfigDescription(
                    "Maximum for secondary skills without the Talent perk (Vanilla: 50)",
                    new AcceptableValueRange<float>(10f, 9999f)));

            CFG_TalentMinorSkillCap = Config.Bind(
                "Skill Caps", "Talent + Minor Skill Cap", 450f,
                new ConfigDescription(
                    "Maximum for secondary skills with the Talent perk (Vanilla: 60)",
                    new AcceptableValueRange<float>(10f, 9999f)));

            CFG_MarketBoostChance = Config.Bind(
                "Job Market", "High Skill Chance", 10,
                new ConfigDescription(
                    "Chance (%) that a job market applicant receives boosted skills above vanilla range. "
                    + "Uses a power-curve: high skills are exponentially rarer. 0 = disabled.",
                    new AcceptableValueRange<int>(0, 100)));

            RecomputeCachedValues();

            CFG_MajorSkillCap.SettingChanged      += (_, __) => RecomputeCachedValues();
            CFG_MinorSkillCap.SettingChanged       += (_, __) => RecomputeCachedValues();
            CFG_TalentMinorSkillCap.SettingChanged += (_, __) => RecomputeCachedValues();

            harmony = new Harmony(GUID);
            try
            {
                harmony.PatchAll();
                Logger.LogInfo($"{NAME} v{VERSION} loaded!");
                Logger.LogInfo($"  Major: {CFG_MajorSkillCap.Value} | Minor: {CFG_MinorSkillCap.Value} | Talent+Minor: {CFG_TalentMinorSkillCap.Value} | MarketBoost: {CFG_MarketBoostChance.Value}%");
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during patching: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private static void RecomputeCachedValues()
        {
            float major  = CFG_MajorSkillCap.Value;
            float minor  = Mathf.Min(CFG_MinorSkillCap.Value, major);
            float talent = Mathf.Clamp(CFG_TalentMinorSkillCap.Value, minor, major);
            _fillFactor         = 1f / major;
            _minorFillMax       = minor / major;
            _talentMinorFillMax = talent / major;
        }

        private void OnDestroy() => harmony?.UnpatchSelf();

        // ---- Effective caps (clamped: Minor <= TalentMinor <= Major) --------
        public static float EffectiveMinorCap
            => Mathf.Min(CFG_MinorSkillCap.Value, CFG_MajorSkillCap.Value);

        public static float EffectiveTalentMinorCap
            => Mathf.Clamp(CFG_TalentMinorSkillCap.Value, EffectiveMinorCap, CFG_MajorSkillCap.Value);

        // ---- Static helpers for transpilers (CallVirt-compatible) ----------
        public static float GetMajorCap()           => CFG_MajorSkillCap.Value;
        public static float GetFillFactor()         => _fillFactor;
        public static float GetMinorFillMax()       => _minorFillMax;
        public static float GetTalentMinorFillMax() => _talentMinorFillMax;
    }

    // =========================================================================
    // PATCH 1: characterScript.GetSkillCap()
    //
    // Vanilla returns:
    //   100f -> Sandbox mode
    //    60f -> Employee has Talent perk (slot 15)
    //    50f -> No Talent perk
    //
    // With patch:
    //   100f -> CFG_MajorSkillCap        (Sandbox: all skills share MajorCap)
    //    60f -> EffectiveTalentMinorCap   (clamped: Minor <= Talent <= Major)
    //    50f -> EffectiveMinorCap         (clamped: <= Major)
    // =========================================================================
    [HarmonyPatch(typeof(characterScript), "GetSkillCap")]
    class Patch_GetSkillCap
    {
        [HarmonyPostfix]
        static void Postfix(ref float __result)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;

                if (__result >= 100f)
                    __result = Plugin.CFG_MajorSkillCap.Value;       // Sandbox: all → MajorCap
                else if (__result > 50f)
                    __result = Plugin.EffectiveTalentMinorCap;       // 60 = Talent perk
                else
                    __result = Plugin.EffectiveMinorCap;             // 50 = No Talent
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"GetSkillCap Postfix: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // PATCH 2: characterScript.GetSkillCap_Skill(int i)
    //
    // Vanilla returns:
    //   100f           -> Primary skill (beruf == i)
    //   GetSkillCap()  -> Secondary skill (50f or 60f, already patched by Patch 1)
    //
    // We only patch the primary case (beruf match -> MajorSkillCap).
    // Uses __instance.beruf instead of fragile float comparison.
    // The secondary case is already handled correctly by Patch 1.
    // =========================================================================
    [HarmonyPatch(typeof(characterScript), "GetSkillCap_Skill")]
    class Patch_GetSkillCap_Skill
    {
        [HarmonyPostfix]
        static void Postfix(ref float __result, characterScript __instance, int i)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;
                if (i == __instance.beruf)                       // Primary skill: beruf match
                    __result = Plugin.CFG_MajorSkillCap.Value;
                // Secondary case: already correct via Patch 1 (GetSkillCap)
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"GetSkillCap_Skill Postfix: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // PATCH 3: characterScript.Learn(...)
    //
    // TRANSPILER: Replaces the absolute hard limit (100f) with MajorSkillCap.
    //
    // Vanilla pattern for each skill block (8 blocks):
    //   ldfld  s_skill
    //   ldc.r4 100          <- Comparison: if (skill > 100) ...
    //   ble.un.s SKIP       <- Branch if skill <= 100
    //   ldarg.0
    //   ldc.r4 100          <- Assignment: skill = 100
    //   stfld  s_skill
    //
    // We only replace ldc.r4 100f followed by ble.un.s/ble.un OR stfld.
    // Other constants (0.5, 2.0, 0.001 ...) remain untouched.
    //
    // SAFETY-POSTFIX: Hard-clamps secondary skills after every tick.
    // This is the direct fix for the "secondary skill jumps to 500" bug.
    // =========================================================================
    [HarmonyPatch(typeof(characterScript), "Learn")]
    class Patch_Learn
    {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes   = new List<CodeInstruction>(instructions);
            int patched = 0;
            var getMajor = typeof(Plugin).GetMethod(nameof(Plugin.GetMajorCap));

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode != OpCodes.Ldc_R4) continue;
                if (codes[i].operand is not float f)    continue;
                if (Math.Abs(f - 100f) > 0.001f)        continue;
                if (i + 1 >= codes.Count)               continue;

                var next = codes[i + 1].opcode;

                // Only patch hard limit sites: comparison (ble.un.s) or assignment (stfld)
                if (next == OpCodes.Ble_Un_S || next == OpCodes.Ble_Un || next == OpCodes.Stfld)
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, getMajor);
                    patched++;
                }
            }

            if (patched > 0)
                Plugin.Logger.LogInfo($"Learn Transpiler: {patched} patches applied (expected: 16 for 8 skills x 2).");
            else
                Plugin.Logger.LogWarning("Learn Transpiler: No patches applied! Game update? Module still active via Safety-Postfix.");

            return codes;
        }

        // ---- Safety-Postfix: direct bug fix ------------------------------------
        // Skills are clamped to their per-skill cap (via GetSkillCap_Skill) after
        // every Learn() tick. This fixes the jump bug regardless of root cause.
        // Uses the game's own cap method → correctly handles Sandbox mode.

        private static readonly MethodInfo _mGetSkillCapSkill =
            AccessTools.Method(typeof(characterScript), "GetSkillCap_Skill");
        private static readonly object[] _capArg = new object[1];

        [HarmonyPostfix]
        static void SafetyClamp(characterScript __instance)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;

                // Get per-skill caps using the game's own patched method.
                // Returns MajorCap for primary, Minor/TalentMinor for secondary,
                // or MajorCap for ALL skills in Sandbox mode (via Patch 1+2).
                float cap0 = GetCap(__instance, 0);
                float cap1 = GetCap(__instance, 1);
                float cap2 = GetCap(__instance, 2);
                float cap3 = GetCap(__instance, 3);
                float cap4 = GetCap(__instance, 4);
                float cap5 = GetCap(__instance, 5);
                float cap6 = GetCap(__instance, 6);
                float cap7 = GetCap(__instance, 7);

                if (__instance.s_gamedesign    > cap0) __instance.s_gamedesign    = cap0;
                if (__instance.s_programmieren > cap1) __instance.s_programmieren = cap1;
                if (__instance.s_grafik        > cap2) __instance.s_grafik        = cap2;
                if (__instance.s_sound         > cap3) __instance.s_sound         = cap3;
                if (__instance.s_pr            > cap4) __instance.s_pr            = cap4;
                if (__instance.s_gametests     > cap5) __instance.s_gametests     = cap5;
                if (__instance.s_technik       > cap6) __instance.s_technik       = cap6;
                if (__instance.s_forschen      > cap7) __instance.s_forschen      = cap7;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"SafetyClamp: {ex.Message}");
            }
        }

        private static float GetCap(characterScript inst, int skillIndex)
        {
            _capArg[0] = skillIndex;
            return (float)_mGetSkillCapSkill.Invoke(inst, _capArg);
        }
    }

    // =========================================================================
    // PATCH 4 + 5: GUI_Main.SetBalkenEmployee / SetBalkenArbeitsmarkt
    //
    // Vanilla calculates bar fill:
    //   fillAmount = val * 0.01f              (val / 100 = 0..1)
    //   FillMax (secondary no Talent) = 0.5f  (50/100)
    //   FillMax (secondary + Talent)  = 0.6f  (60/100)
    //   FillMax (primary)             = 1.0f  (100/100, unchanged)
    //
    // With patch (example: MajorCap=500, MinorCap=350, TalentMinorCap=450):
    //   fillAmount = val * (1/500)            (val / 500 = 0..1)
    //   FillMax (secondary no Talent) = 350/500 = 0.7
    //   FillMax (secondary + Talent)  = 450/500 = 0.9
    //
    // Strategy: Replace specific float literals with calls to helper methods.
    //   0.01f -> Plugin.GetFillFactor()         = 1/MajorCap
    //   0.5f  -> Plugin.GetMinorFillMax()       = MinorCap/MajorCap
    //   0.6f  -> Plugin.GetTalentMinorFillMax() = TalentMinorCap/MajorCap
    //
    // No hardcoded instruction indices (old mod bug fixed).
    // =========================================================================
    [HarmonyPatch(typeof(GUI_Main), "SetBalkenEmployee")]
    class Patch_SetBalkenEmployee
    {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => BarPatchHelper.Apply(instructions, "SetBalkenEmployee");
    }

    [HarmonyPatch(typeof(GUI_Main), "SetBalkenArbeitsmarkt")]
    class Patch_SetBalkenArbeitsmarkt
    {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => BarPatchHelper.Apply(instructions, "SetBalkenArbeitsmarkt");
    }

    // =========================================================================
    // PATCH 6: GUI_Main.GetValColorEmployee(float val)
    //
    // Vanilla thresholds: val < 30 -> red | 30 <= val < 70 -> yellow | val >= 70 -> green
    // These are designed for val in 0-100. With MajorCap=500, val would be 0-500,
    // so ALL skills would always appear green.
    //
    // Fix: Normalize val to 0-100 BEFORE the vanilla method runs.
    //   normalizedVal = val / MajorCap * 100
    // This keeps the vanilla thresholds (30 / 70) working correctly.
    // =========================================================================
    [HarmonyPatch(typeof(GUI_Main), "GetValColorEmployee")]
    class Patch_GetValColorEmployee
    {
        [HarmonyPrefix]
        static void Prefix(ref float val)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;
                val = val / Plugin.CFG_MajorSkillCap.Value * 100f;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"GetValColorEmployee Prefix: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // PATCH 6b: GetValColor() in ALL employee-related UI classes
    //
    // Same problem as Patch 6: vanilla thresholds (30/70) assume val in 0-100.
    // 14 classes each have their own private GetValColor(float val) method:
    //   Employee views, tooltips, overview, job market, CEO, salary, etc.
    //
    // Uses Harmony TargetMethods() to patch all at once with a single prefix.
    // =========================================================================
    [HarmonyPatch]
    class Patch_AllGetValColor
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;

            // All employee/character-related classes with their own GetValColor
            var targets = new[]
            {
                typeof(Menu_MitarbeiterUebersicht),     // Employee overview (the reported bug)
                typeof(Menu_PersonalView),              // Personnel detail view
                typeof(Menu_PersonalViewArbeitsmarkt),  // Staff view: job market
                typeof(Menu_Personal_InRoom),           // Staff in room menu
                typeof(Menu_PersonalLohnverhandlung),   // Salary negotiation
                typeof(Menu_TooltipCharacter),          // Character tooltip
                typeof(Menu_PickCharacter),             // Pick character
                typeof(Menu_Mitarbeitersuche),          // Employee search
                typeof(Menu_MitarbeitersucheResult),    // Employee search results
                typeof(Menu_NewGameCEO),                // CEO creation
                typeof(Item_PersonalGroup),             // Staff group item
                typeof(Item_Personal_InRoom),           // Staff in room item
                typeof(Item_LeitenderDesigner),         // Lead designer item
                typeof(Item_Arbeitsmarkt),              // Job market item
            };

            int found = 0;
            foreach (var t in targets)
            {
                var m = t.GetMethod("GetValColor", flags, null, new[] { typeof(float) }, null);
                if (m != null)
                {
                    found++;
                    yield return m;
                }
            }

            Plugin.Logger.LogInfo($"GetValColor batch patch: {found}/{targets.Length} methods found.");
        }

        [HarmonyPrefix]
        static void Prefix(ref float val)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;
                val = val / Plugin.CFG_MajorSkillCap.Value * 100f;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"GetValColor Prefix: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // PATCH 7: Menu_NewGameCEO.GetSkillCap()
    //
    // CEO creation screen - same logic as characterScript.GetSkillCap().
    // =========================================================================
    [HarmonyPatch(typeof(Menu_NewGameCEO), "GetSkillCap")]
    class Patch_CEOGetSkillCap
    {
        [HarmonyPostfix]
        static void Postfix(ref float __result)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;

                if (__result >= 100f)
                    __result = Plugin.CFG_MajorSkillCap.Value;       // Sandbox
                else if (__result > 50f)
                    __result = Plugin.EffectiveTalentMinorCap;       // Talent
                else
                    __result = Plugin.EffectiveMinorCap;             // No Talent
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"CEOGetSkillCap Postfix: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // PATCH 8: Menu_NewGameCEO.SetBalken(GameObject, float val, int beruf_)
    //
    // CEO creation screen - same bar proportions as SetBalkenEmployee.
    // =========================================================================
    [HarmonyPatch(typeof(Menu_NewGameCEO), "SetBalken")]
    class Patch_CEOSetBalken
    {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => BarPatchHelper.Apply(instructions, "Menu_NewGameCEO.SetBalken");
    }

    // =========================================================================
    // PATCH 9: charArbeitsmarkt.Create() – Job Market Skill Boost
    //
    // By default, job market applicants have skills in vanilla range (~10-95).
    // This Postfix gives a configurable chance to boost skills above vanilla,
    // using a power-curve (roll^3) that makes high values exponentially rare.
    //
    // Compatible with Job Market Tweaker: JMT replaces Create with a Prefix
    // that returns false, but Harmony Postfixes still run afterward.
    //
    // Distribution (default 30% chance, MajorCap=500):
    //   ~70% normal (0-95) | ~24% slightly boosted (95-200) |
    //   ~4.5% moderate (200-350) | ~1.2% high (350-450) | ~0.3% exceptional (450-500)
    // =========================================================================
    [HarmonyPatch(typeof(charArbeitsmarkt), "Create")]
    class Patch_ArbeitsmarktSkillBoost
    {
        [HarmonyPostfix]
        static void Postfix(charArbeitsmarkt __instance)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;

                int chance = Plugin.CFG_MarketBoostChance.Value;
                if (chance <= 0) return;

                // Roll: skip boost for most applicants
                if (UnityEngine.Random.Range(0, 100) >= chance) return;

                float majorCap = Plugin.CFG_MajorSkillCap.Value;

                // Power-curve: cubic distribution makes high values exponentially rare
                // roll is uniform [0..1], strength = roll^3 skews heavily toward low values
                float roll     = UnityEngine.Random.value;
                float strength = Mathf.Pow(roll, 3f);

                int beruf = __instance.beruf;

                // Only boost the primary skill (beruf) toward majorCap
                switch (beruf)
                {
                    case 0: __instance.s_gamedesign    = Boost(__instance.s_gamedesign,    majorCap, strength); break;
                    case 1: __instance.s_programmieren = Boost(__instance.s_programmieren, majorCap, strength); break;
                    case 2: __instance.s_grafik        = Boost(__instance.s_grafik,        majorCap, strength); break;
                    case 3: __instance.s_sound         = Boost(__instance.s_sound,         majorCap, strength); break;
                    case 4: __instance.s_pr            = Boost(__instance.s_pr,            majorCap, strength); break;
                    case 5: __instance.s_gametests     = Boost(__instance.s_gametests,     majorCap, strength); break;
                    case 6: __instance.s_technik       = Boost(__instance.s_technik,       majorCap, strength); break;
                    case 7: __instance.s_forschen      = Boost(__instance.s_forschen,      majorCap, strength); break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"ArbeitsmarktSkillBoost: {ex.Message}");
            }
        }

        /// <summary>
        /// Boost a skill value toward its cap using the given strength factor.
        /// Never decreases the current value, never exceeds cap.
        /// </summary>
        private static float Boost(float current, float cap, float strength)
        {
            if (current >= cap) return cap;
            float boosted = current + (cap - current) * strength;
            return Mathf.Clamp(boosted, current, cap);
        }
    }

    // =========================================================================
    // Helper: Shared transpiler logic for skill bar patches
    // =========================================================================
    internal static class BarPatchHelper
    {
        internal static IEnumerable<CodeInstruction> Apply(
            IEnumerable<CodeInstruction> instructions, string callerName)
        {
            var codes   = new List<CodeInstruction>(instructions);
            int patched = 0;

            var getFill      = typeof(Plugin).GetMethod(nameof(Plugin.GetFillFactor));
            var getMinorMax  = typeof(Plugin).GetMethod(nameof(Plugin.GetMinorFillMax));
            var getTalentMax = typeof(Plugin).GetMethod(nameof(Plugin.GetTalentMinorFillMax));

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode != OpCodes.Ldc_R4) continue;
                if (codes[i].operand is not float f)    continue;

                if (Math.Abs(f - 0.01f) < 0.0001f)
                {
                    // val * 0.01f = val / 100  ->  val * (1/MajorCap) = val / MajorCap
                    codes[i] = new CodeInstruction(OpCodes.Call, getFill);
                    patched++;
                }
                else if (Math.Abs(f - 0.5f) < 0.0001f)
                {
                    // Context check: skip if used in arithmetic (not a threshold)
                    if (i + 1 < codes.Count && IsArithmeticOp(codes[i + 1].opcode))
                        continue;

                    // Secondary cap (no Talent): 50/100 = 0.5  ->  MinorCap/MajorCap
                    codes[i] = new CodeInstruction(OpCodes.Call, getMinorMax);
                    patched++;
                }
                else if (Math.Abs(f - 0.6f) < 0.0001f)
                {
                    // Context check: skip if used in arithmetic (not a threshold)
                    if (i + 1 < codes.Count && IsArithmeticOp(codes[i + 1].opcode))
                        continue;

                    // Secondary cap (Talent): 60/100 = 0.6  ->  TalentMinorCap/MajorCap
                    codes[i] = new CodeInstruction(OpCodes.Call, getTalentMax);
                    patched++;
                }
            }

            if (patched > 0)
                Plugin.Logger.LogInfo($"{callerName} Transpiler: {patched} patches applied.");
            else
                Plugin.Logger.LogWarning($"{callerName} Transpiler: No patches applied! Game update? Bar display may be incorrect.");

            return codes;
        }

        private static bool IsArithmeticOp(OpCode op)
        {
            return op == OpCodes.Mul || op == OpCodes.Div
                || op == OpCodes.Add || op == OpCodes.Sub
                || op == OpCodes.Rem;
        }
    }
}
