/*
 * Custom Skill Cap – Reloaded  v2.0.0
 * Autor: Rewrite basierend auf Original von Aerin_the_Lion
 * GUID : me.Aerin_the_Lion.Mad_Games_Tycoon_2.plugins.CustomSkillCap  (kompatibel)
 *
 * Behebt den Bug: Sekundaerskills springen auf den Maximalwert (z.B. 500)
 * wenn sie ca. 90 ueberschreiten.
 *
 * Patches:
 *   1. characterScript.GetSkillCap()       – Postfix:     korrekte Sekundaer-Cap
 *   2. characterScript.GetSkillCap_Skill() – Postfix:     korrekte Primaer-Cap
 *   3. characterScript.Learn()             – Transpiler:  100 → MajorSkillCap in absoluter Grenze
 *                                            Safety-Postfix: klemmt Sekundaerskills nach jedem Tick
 *   4. GUI_Main.SetBalkenEmployee()        – Transpiler:  Balken-Proportionen
 *   5. GUI_Main.SetBalkenArbeitsmarkt()    – Transpiler:  Balken-Proportionen (Jobmarkt)
 *   6. GUI_Main.GetValColorEmployee()      – Prefix:      Farbschwellen normalisieren
 *   7. Menu_NewGameCEO.GetSkillCap()       – Postfix:     CEO-Erstellungsbildschirm
 *   8. Menu_NewGameCEO.SetBalken()         – Transpiler:  CEO-Balken
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
    // Plugin-Einstiegspunkt
    // =========================================================================
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID    = "me.Aerin_the_Lion.Mad_Games_Tycoon_2.plugins.CustomSkillCap";
        public const string NAME    = "Custom Skill Cap";
        public const string VERSION = "2.0.0";

        internal static new ManualLogSource Logger;

        // ---- Konfiguration (gleiche Abschnitt-/Schluesselnamen wie Original) ---
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
                "Mod aktivieren / deaktivieren.");

            CFG_MajorSkillCap = Config.Bind(
                "Skill Caps", "Major Skill Cap", 500f,
                new ConfigDescription(
                    "Maximum fuer den Primaerskill des Mitarbeiters (Vanilla: 100)",
                    new AcceptableValueRange<float>(100f, 9999f)));

            CFG_MinorSkillCap = Config.Bind(
                "Skill Caps", "Minor Skill Cap", 350f,
                new ConfigDescription(
                    "Maximum fuer Sekundaerskills OHNE Talent-Perk (Vanilla: 50)",
                    new AcceptableValueRange<float>(10f, 9999f)));

            CFG_TalentMinorSkillCap = Config.Bind(
                "Skill Caps", "Talent + Minor Skill Cap", 450f,
                new ConfigDescription(
                    "Maximum fuer Sekundaerskills MIT Talent-Perk (Vanilla: 60)",
                    new AcceptableValueRange<float>(10f, 9999f)));

            harmony = new Harmony(GUID);
            try
            {
                harmony.PatchAll();
                Logger.LogInfo($"{NAME} v{VERSION} geladen!");
                Logger.LogInfo($"  Primaer: {CFG_MajorSkillCap.Value} | Sekundaer: {CFG_MinorSkillCap.Value} | Talent+Sekundaer: {CFG_TalentMinorSkillCap.Value}");
            }
            catch (Exception ex)
            {
                Logger.LogError("Fehler beim Patchen: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private void OnDestroy() => harmony?.UnpatchSelf();

        // ---- Statische Helfer fuer Transpiler (CallVirt-kompatibel) ----------
        public static float GetMajorCap()           => CFG_MajorSkillCap.Value;
        public static float GetFillFactor()         => 1f / CFG_MajorSkillCap.Value;
        public static float GetMinorFillMax()       => CFG_MinorSkillCap.Value / CFG_MajorSkillCap.Value;
        public static float GetTalentMinorFillMax() => CFG_TalentMinorSkillCap.Value / CFG_MajorSkillCap.Value;
    }

    // =========================================================================
    // PATCH 1: characterScript.GetSkillCap()
    //
    // Vanilla gibt zurueck:
    //   100f  → Sandbox-Modus (unveraendert lassen)
    //    60f  → Mitarbeiter hat Talent-Perk (Slot 15)
    //    50f  → ohne Talent-Perk
    //
    // Mit Patch:
    //   100f → unveraendert (Sandbox)
    //    60f → CFG_TalentMinorSkillCap
    //    50f → CFG_MinorSkillCap
    // =========================================================================
    [HarmonyPatch(typeof(characterScript), "GetSkillCap")]
    class Patch_GetSkillCap
    {
        [HarmonyPostfix]
        static void Postfix(ref float __result)
        {
            if (!Plugin.CFG_IS_ENABLED.Value) return;
            if (__result >= 100f) return;                    // Sandbox: unveraendert

            __result = (__result > 50f)                      // 60 = Talent-Perk vorhanden
                ? Plugin.CFG_TalentMinorSkillCap.Value
                : Plugin.CFG_MinorSkillCap.Value;
        }
    }

    // =========================================================================
    // PATCH 2: characterScript.GetSkillCap_Skill(int i)
    //
    // Vanilla gibt zurueck:
    //   100f                 → Primaerskill (beruf == i)
    //   GetSkillCap()        → Sekundaerskill  (50f oder 60f, per Patch 1 schon geaendert)
    //
    // Wir patchen nur den Primaer-Fall (100f → MajorSkillCap).
    // Der Sekundaer-Fall wird durch Patch 1 automatisch korrekt behandelt.
    // =========================================================================
    [HarmonyPatch(typeof(characterScript), "GetSkillCap_Skill")]
    class Patch_GetSkillCap_Skill
    {
        [HarmonyPostfix]
        static void Postfix(ref float __result)
        {
            if (!Plugin.CFG_IS_ENABLED.Value) return;
            if (__result == 100f)                            // Primaerskill-Rueckgabe
                __result = Plugin.CFG_MajorSkillCap.Value;
            // Sekundaer-Fall: von Patch 1 (GetSkillCap) schon korrekt
        }
    }

    // =========================================================================
    // PATCH 3: characterScript.Learn(...)
    //
    // TRANSPILER: Ersetzt die absolute Haertegrenze (100f) durch MajorSkillCap.
    //
    // Vanilla-Muster fuer jeden Skill-Block (8 Bloecke):
    //   ldfld  s_skill
    //   ldc.r4 100          ← Vergleich: if (skill > 100) ...
    //   ble.un.s SKIP       ← Sprung wenn skill <= 100
    //   ldarg.0
    //   ldc.r4 100          ← Zuweisung: skill = 100
    //   stfld  s_skill
    //
    // Wir ersetzen NUR ldc.r4 100f, die von ble.un.s/ble.un ODER stfld gefolgt werden.
    // Andere Konstanten (0.5, 2.0, 0.001 ...) bleiben unveraendert.
    //
    // SAFETY-POSTFIX: Klemmt Sekundaerskills nach jedem Tick hart.
    // Das ist der direkte Fix fuer den Bug "Sekundaerskill springt auf 500".
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

                // Nur Haertegrenze patchen: Vergleich (ble.un.s) oder Zuweisung (stfld)
                if (next == OpCodes.Ble_Un_S || next == OpCodes.Ble_Un || next == OpCodes.Stfld)
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, getMajor);
                    patched++;
                }
            }

            if (patched > 0)
                Plugin.Logger.LogInfo($"Learn-Transpiler: {patched} Patches gesetzt (erwartet: 16 fuer 8 Skills × 2).");
            else
                Plugin.Logger.LogWarning("Learn-Transpiler: Kein Patch! Game-Update? Modul trotzdem aktiv via Safety-Postfix.");

            return codes;
        }

        // ---- Safety-Postfix: direkter Bug-Fix ------------------------------------
        // Sekundaerskills werden nach jedem Learn()-Tick auf ihren konfigurierten
        // Cap geklemmt. Das behebt den Sprung-Bug unabhaengig von der Ursache.
        [HarmonyPostfix]
        static void SafetyClamp(characterScript __instance)
        {
            if (!Plugin.CFG_IS_ENABLED.Value) return;

            // beruf und perks: beide public → direkter Zugriff (kein Traverse-Overhead)
            // perks ist bool[] (nicht byte[]) – wichtig fuer korrekte Talent-Erkennung
            int beruf      = __instance.beruf;
            bool[] perks   = __instance.perks;

            bool hasTalent = perks != null && perks.Length > 15 && perks[15];
            float minorCap = hasTalent
                ? Plugin.CFG_TalentMinorSkillCap.Value
                : Plugin.CFG_MinorSkillCap.Value;
            float majorCap = Plugin.CFG_MajorSkillCap.Value;

            // Sekundaerskills klemmen (alle ausser dem Primaerskill des Mitarbeiters)
            if (beruf != 0 && __instance.s_gamedesign    > minorCap) __instance.s_gamedesign    = minorCap;
            if (beruf != 1 && __instance.s_programmieren > minorCap) __instance.s_programmieren = minorCap;
            if (beruf != 2 && __instance.s_grafik        > minorCap) __instance.s_grafik        = minorCap;
            if (beruf != 3 && __instance.s_sound         > minorCap) __instance.s_sound         = minorCap;
            if (beruf != 4 && __instance.s_pr            > minorCap) __instance.s_pr            = minorCap;
            if (beruf != 5 && __instance.s_gametests     > minorCap) __instance.s_gametests     = minorCap;
            if (beruf != 6 && __instance.s_technik       > minorCap) __instance.s_technik       = minorCap;
            if (beruf != 7 && __instance.s_forschen      > minorCap) __instance.s_forschen      = minorCap;

            // Primaerskill klemmen (Safety gegen Edge-Cases, Transpiler macht das eigentlich)
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
    // Vanilla berechnet den Balken-Fuellstand:
    //   fillAmount = val * 0.01f              (val / 100 = 0..1)
    //   FillMax (Sekundaer ohne Talent) = 0.5f   (50/100)
    //   FillMax (Sekundaer mit Talent)  = 0.6f   (60/100)
    //   FillMax (Primaer)               = 1.0f   (100/100, unveraendert)
    //
    // Mit Patch (Beispiel: MajorCap=500, MinorCap=350, TalentMinorCap=450):
    //   fillAmount = val * (1/500)             (val / 500 = 0..1)
    //   FillMax (Sekundaer ohne Talent) = 350/500 = 0.7
    //   FillMax (Sekundaer mit Talent)  = 450/500 = 0.9
    //
    // Strategie: Ersetze spezifische float-Literale durch Call zu Helfer-Methode.
    //   0.01f → Plugin.GetFillFactor()         = 1/MajorCap
    //   0.5f  → Plugin.GetMinorFillMax()       = MinorCap/MajorCap
    //   0.6f  → Plugin.GetTalentMinorFillMax() = TalentMinorCap/MajorCap
    //
    // KEIN hardcodierter Instruction-Index mehr (alter Mod-Bug behoben).
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
    // Vanilla-Schwellen: val < 30 → rot | 30 <= val < 70 → gelb | val >= 70 → gruen
    // Diese sind fuer val in 0-100 gedacht. Mit MajorCap=500 waere val 0-500,
    // also wuerden ALLE Skills immer gruen angezeigt.
    //
    // Fix: Normalisiere val auf 0-100 BEVOR die Vanilla-Methode laeuft.
    //   normalizedVal = val / MajorCap * 100
    // Dadurch arbeiten die Vanilla-Schwellen (30 / 70) weiter korrekt.
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
    // CEO-Erstellungsbildschirm – identische Logik wie characterScript.GetSkillCap().
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
    // CEO-Erstellungsbildschirm – gleiche Balken-Proportionen wie SetBalkenEmployee.
    // =========================================================================
    [HarmonyPatch(typeof(Menu_NewGameCEO), "SetBalken")]
    class Patch_CEOSetBalken
    {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => BarPatchHelper.Apply(instructions, "Menu_NewGameCEO.SetBalken");
    }

    // =========================================================================
    // Hilfsklasse: Transpiler fuer Skill-Balken
    // =========================================================================
    internal static class BarPatchHelper
    {
        internal static IEnumerable<CodeInstruction> Apply(
            IEnumerable<CodeInstruction> instructions, string callerName)
        {
            var codes   = new List<CodeInstruction>(instructions);
            int patched = 0;

            var getFill        = typeof(Plugin).GetMethod(nameof(Plugin.GetFillFactor));
            var getMinorMax    = typeof(Plugin).GetMethod(nameof(Plugin.GetMinorFillMax));
            var getTalentMax   = typeof(Plugin).GetMethod(nameof(Plugin.GetTalentMinorFillMax));

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode != OpCodes.Ldc_R4) continue;
                if (codes[i].operand is not float f)    continue;

                if (Math.Abs(f - 0.01f) < 0.0001f)
                {
                    // val * 0.01f = val / 100  →  val * (1/MajorCap) = val / MajorCap
                    codes[i] = new CodeInstruction(OpCodes.Call, getFill);
                    patched++;
                }
                else if (Math.Abs(f - 0.5f) < 0.0001f)
                {
                    // Sekundaer-Cap ohne Talent: 50/100 = 0.5  →  MinorCap/MajorCap
                    codes[i] = new CodeInstruction(OpCodes.Call, getMinorMax);
                    patched++;
                }
                else if (Math.Abs(f - 0.6f) < 0.0001f)
                {
                    // Sekundaer-Cap mit Talent: 60/100 = 0.6  →  TalentMinorCap/MajorCap
                    codes[i] = new CodeInstruction(OpCodes.Call, getTalentMax);
                    patched++;
                }
            }

            if (patched > 0)
                Plugin.Logger.LogInfo($"{callerName} Transpiler: {patched} Patches gesetzt.");
            else
                Plugin.Logger.LogWarning($"{callerName} Transpiler: Keine Patches! Game-Update? Balkendarstellung moeglicherweise falsch.");

            return codes;
        }
    }
}
