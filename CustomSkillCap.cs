/*
 * Custom Skill Cap - Reloaded  v2.1.0
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
 *   7. Menu_NewGameCEO.GetSkillCap()       - Postfix:      CEO creation screen
 *   8. Menu_NewGameCEO.SetBalken()         - Transpiler:   CEO skill bars
 */

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

namespace CustomSkillCapReloaded
{
    // =========================================================================
    // Plugin entry point
    // =========================================================================
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID    = "me.Aerin_the_Lion.Mad_Games_Tycoon_2.plugins.CustomSkillCap";
        public const string NAME    = "Custom Skill Cap";
        public const string VERSION = "2.1.0";

        internal static new ManualLogSource Logger;

        // ---- Config (same section/key names as original for compatibility) ---
        public static ConfigEntry<bool>  CFG_IS_ENABLED;
        public static ConfigEntry<float> CFG_MajorSkillCap;
        public static ConfigEntry<float> CFG_MinorSkillCap;
        public static ConfigEntry<float> CFG_TalentMinorSkillCap;

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

            harmony = new Harmony(GUID);
            try
            {
                harmony.PatchAll();
                Logger.LogInfo($"{NAME} v{VERSION} loaded!");
                Logger.LogInfo($"  Major: {CFG_MajorSkillCap.Value} | Minor: {CFG_MinorSkillCap.Value} | Talent+Minor: {CFG_TalentMinorSkillCap.Value}");
            }
            catch (Exception ex)
            {
                Logger.LogError("Error during patching: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private void OnDestroy() => harmony?.UnpatchSelf();

        // ---- Static helpers for transpilers (CallVirt-compatible) ----------
        public static float GetMajorCap()           => CFG_MajorSkillCap.Value;
        public static float GetFillFactor()         => 1f / CFG_MajorSkillCap.Value;
        public static float GetMinorFillMax()       => CFG_MinorSkillCap.Value / CFG_MajorSkillCap.Value;
        public static float GetTalentMinorFillMax() => CFG_TalentMinorSkillCap.Value / CFG_MajorSkillCap.Value;
    }

    // =========================================================================
    // PATCH 1: characterScript.GetSkillCap()
    //
    // Vanilla returns:
    //   100f -> Sandbox mode (leave unchanged)
    //    60f -> Employee has Talent perk (slot 15)
    //    50f -> No Talent perk
    //
    // With patch:
    //   100f -> unchanged (Sandbox)
    //    60f -> CFG_TalentMinorSkillCap
    //    50f -> CFG_MinorSkillCap
    // =========================================================================
    [HarmonyPatch(typeof(characterScript), "GetSkillCap")]
    class Patch_GetSkillCap
    {
        [HarmonyPostfix]
        static void Postfix(ref float __result)
        {
            if (!Plugin.CFG_IS_ENABLED.Value) return;
            if (__result >= 100f) return;                    // Sandbox: unchanged

            __result = (__result > 50f)                      // 60 = Talent perk present
                ? Plugin.CFG_TalentMinorSkillCap.Value
                : Plugin.CFG_MinorSkillCap.Value;
        }
    }

    // =========================================================================
    // PATCH 2: characterScript.GetSkillCap_Skill(int i)
    //
    // Vanilla returns:
    //   100f           -> Primary skill (beruf == i)
    //   GetSkillCap()  -> Secondary skill (50f or 60f, already patched by Patch 1)
    //
    // We only patch the primary case (100f -> MajorSkillCap).
    // The secondary case is already handled correctly by Patch 1.
    // =========================================================================
    [HarmonyPatch(typeof(characterScript), "GetSkillCap_Skill")]
    class Patch_GetSkillCap_Skill
    {
        [HarmonyPostfix]
        static void Postfix(ref float __result)
        {
            if (!Plugin.CFG_IS_ENABLED.Value) return;
            if (__result == 100f)                            // Primary skill return value
                __result = Plugin.CFG_MajorSkillCap.Value;
            // Secondary case: already correct via Patch 1 (GetSkillCap)
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
        // Secondary skills are clamped to their configured cap after every
        // Learn() tick. This fixes the jump bug regardless of root cause.
        [HarmonyPostfix]
        static void SafetyClamp(characterScript __instance)
        {
            if (!Plugin.CFG_IS_ENABLED.Value) return;

            // beruf and perks are both public -> direct access (no Traverse overhead)
            // perks is bool[] (not byte[]) - critical for correct Talent detection
            int beruf      = __instance.beruf;
            bool[] perks   = __instance.perks;

            bool hasTalent = perks != null && perks.Length > 15 && perks[15];
            float minorCap = hasTalent
                ? Plugin.CFG_TalentMinorSkillCap.Value
                : Plugin.CFG_MinorSkillCap.Value;
            float majorCap = Plugin.CFG_MajorSkillCap.Value;

            // Clamp secondary skills (all except the employee's primary discipline)
            if (beruf != 0 && __instance.s_gamedesign    > minorCap) __instance.s_gamedesign    = minorCap;
            if (beruf != 1 && __instance.s_programmieren > minorCap) __instance.s_programmieren = minorCap;
            if (beruf != 2 && __instance.s_grafik        > minorCap) __instance.s_grafik        = minorCap;
            if (beruf != 3 && __instance.s_sound         > minorCap) __instance.s_sound         = minorCap;
            if (beruf != 4 && __instance.s_pr            > minorCap) __instance.s_pr            = minorCap;
            if (beruf != 5 && __instance.s_gametests     > minorCap) __instance.s_gametests     = minorCap;
            if (beruf != 6 && __instance.s_technik       > minorCap) __instance.s_technik       = minorCap;
            if (beruf != 7 && __instance.s_forschen      > minorCap) __instance.s_forschen      = minorCap;

            // Clamp primary skill (safety net for edge cases; transpiler handles this normally)
            switch (beruf)
            {
                case 0: if (__instance.s_gamedesign    > majorCap) __instance.s_gamedesign    = majorCap; break;
                case 1: if (__instance.s_programmieren > majorCap) __instance.s_programmieren = majorCap; break;
                case 2: if (__instance.s_grafik        > majorCap) __instance.s_grafik        = majorCap; break;
                case 3: if (__instance.s_sound         > majorCap) __instance.s_sound         = majorCap; break;
                case 4: if (__instance.s_pr            > majorCap) __instance.s_pr            = majorCap; break;
                case 5: if (__instance.s_gametests     > majorCap) __instance.s_gametests     = majorCap; break;
                case 6: if (__instance.s_technik       > majorCap) __instance.s_technik       = majorCap; break;
                case 7: if (__instance.s_forschen      > majorCap) __instance.s_forschen      = majorCap; break;
            }
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
            if (!Plugin.CFG_IS_ENABLED.Value) return;
            val = val / Plugin.CFG_MajorSkillCap.Value * 100f;
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
            if (!Plugin.CFG_IS_ENABLED.Value) return;
            if (__result >= 100f) return;                    // Sandbox

            __result = (__result > 50f)
                ? Plugin.CFG_TalentMinorSkillCap.Value
                : Plugin.CFG_MinorSkillCap.Value;
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
                    // Secondary cap (no Talent): 50/100 = 0.5  ->  MinorCap/MajorCap
                    codes[i] = new CodeInstruction(OpCodes.Call, getMinorMax);
                    patched++;
                }
                else if (Math.Abs(f - 0.6f) < 0.0001f)
                {
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
    }
}
