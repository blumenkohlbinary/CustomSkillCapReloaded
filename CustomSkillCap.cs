/*
 * Custom Skill Cap - Reloaded  v2.5.1
 * Rewrite based on original by Aerin_the_Lion
 * GUID: me.Aerin_the_Lion.Mad_Games_Tycoon_2.plugins.CustomSkillCap  (compatible)
 *
 * Fixes the bug: Secondary skills jump to the maximum value (e.g. 500)
 * when they exceed approximately 90.
 *
 * Patches (8 classes + 1 helper):
 *   1.  Patch_RemapSkillCap          - Postfix (TargetMethods):
 *       characterScript.GetSkillCap()  + Menu_NewGameCEO.GetSkillCap()
 *   2.  Patch_GetSkillCap_Skill      - Postfix: primary cap override
 *   3.  Patch_Learn                  - Transpiler + Safety-Postfix: 100 -> MajorCap + clamp
 *   4.  Patch_AllSkillBars           - Transpiler (TargetMethods): 6 bar methods
 *   5.  Patch_GetValColorEmployee    - Prefix: unconditional normalization
 *   6.  Patch_AllGetValColor         - Prefix (TargetMethods): 13x conditional normalization
 *   7.  Patch_JobMarketSkillBoost    - Postfix: legends + recruitment + boost
 *   8.  Patch_EmployeeSearch         - Postfix/Prefix: scaled experience tiers
 *       BarPatchHelper               - shared transpiler logic
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
        public const string NAME    = "Custom Skill Cap Reloaded";
        public const string VERSION = "2.5.1";

        internal static new ManualLogSource Logger;

        // ---- Config (same section/key names as original for compatibility) ---
        public static ConfigEntry<bool>  CFG_IS_ENABLED;
        public static ConfigEntry<float> CFG_MajorSkillCap;
        public static ConfigEntry<float> CFG_MinorSkillCap;
        public static ConfigEntry<float> CFG_TalentMinorSkillCap;
        public static ConfigEntry<int>   CFG_MarketBoostChance;

        // Cached derived values — recomputed on config change
        private static float _fillFactor;
        private static float _minorFillMax;
        private static float _talentMinorFillMax;
        private static float _effectiveMinorCap;
        private static float _effectiveTalentMinorCap;

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
                Logger.LogError($"Error during patching: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void RecomputeCachedValues()
        {
            float major  = Mathf.Max(1f, CFG_MajorSkillCap.Value);
            float minor  = Mathf.Min(CFG_MinorSkillCap.Value, major);
            float talent = Mathf.Clamp(CFG_TalentMinorSkillCap.Value, minor, major);
            _fillFactor              = 1f / major;
            _minorFillMax            = minor / major;
            _talentMinorFillMax      = talent / major;
            _effectiveMinorCap       = minor;
            _effectiveTalentMinorCap = talent;
        }

        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
            Patch_EmployeeSearch.ResetState();
        }

        // ---- Effective caps (cached — Minor <= TalentMinor <= Major) --------
        public static float EffectiveMinorCap       => _effectiveMinorCap;
        public static float EffectiveTalentMinorCap  => _effectiveTalentMinorCap;

        // ---- Static helpers for transpilers (CallVirt-compatible) ----------
        public static float GetMajorCap()           => CFG_MajorSkillCap.Value;
        public static float GetFillFactor()         => _fillFactor;
        public static float GetMinorFillMax()       => _minorFillMax;
        public static float GetTalentMinorFillMax() => _talentMinorFillMax;

        /// <summary>
        /// Scales a skill value from [0..MajorCap] to [0..100] for vanilla color thresholds.
        /// Pure function — reuses cached _fillFactor. Used by both GetValColor patch variants.
        /// </summary>
        public static float NormalizeSkill(float val) => val * _fillFactor * 100f;

        /// <summary>
        /// Remaps vanilla GetSkillCap return values (100/60/50) to configured caps.
        /// </summary>
        public static void RemapSkillCap(ref float result)
        {
            if (!CFG_IS_ENABLED.Value) return;

            if (result >= 100f)
                result = CFG_MajorSkillCap.Value;
            else if (result > 50f)
                result = EffectiveTalentMinorCap;
            else
                result = EffectiveMinorCap;
        }
    }

    // =========================================================================
    // PATCH 1: characterScript.GetSkillCap() + Menu_NewGameCEO.GetSkillCap()
    //
    // Merged via TargetMethods — both remap vanilla caps (100/60/50) identically.
    // =========================================================================
    [HarmonyPatch]
    class Patch_RemapSkillCap
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(characterScript), "GetSkillCap");
            yield return AccessTools.Method(typeof(Menu_NewGameCEO), "GetSkillCap");
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        static void Postfix(ref float __result)
        {
            try   { Plugin.RemapSkillCap(ref __result); }
            catch (Exception ex) { Plugin.Logger.LogError($"RemapSkillCap Postfix: {ex.Message}"); }
        }
    }

    // =========================================================================
    // PATCH 2: characterScript.GetSkillCap_Skill(int i)
    //
    // Only patches the primary skill case (beruf == i -> MajorSkillCap).
    // Secondary skills are already handled by Patch 1 via GetSkillCap().
    // =========================================================================
    [HarmonyPatch(typeof(characterScript), "GetSkillCap_Skill")]
    class Patch_GetSkillCap_Skill
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        static void Postfix(ref float __result, characterScript __instance, int i)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;
                if (i == __instance.beruf)
                    __result = Plugin.CFG_MajorSkillCap.Value;
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
    // TRANSPILER: Replaces ldc.r4 100f with Call GetMajorCap() at hard-limit
    // sites (comparison ble.un.s/ble.un + assignment stfld), 8 skills x 2 = 16.
    //
    // SAFETY-POSTFIX: Clamps all skills to their per-skill cap after every tick.
    // Direct fix for the "secondary skill jumps to 500" bug.
    // =========================================================================
    [HarmonyPatch(typeof(characterScript), "Learn")]
    class Patch_Learn
    {
        private const int EXPECTED_PATCHES = 16; // 8 skills x 2 (comparison + assignment)

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
                if (next == OpCodes.Ble_Un_S || next == OpCodes.Ble_Un || next == OpCodes.Stfld)
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, getMajor);
                    patched++;
                }
            }

            if (patched == EXPECTED_PATCHES)
                Plugin.Logger.LogInfo($"Learn Transpiler: {patched} patches applied.");
            else if (patched > 0)
                Plugin.Logger.LogWarning($"Learn Transpiler: {patched} patches applied (expected {EXPECTED_PATCHES}). Game update?");
            else
                Plugin.Logger.LogWarning("Learn Transpiler: 0 patches applied! Game update? Safety-Postfix still active.");

            return codes;
        }

        // ---- Safety-Postfix: clamp skills to per-skill cap every tick --------

        private static readonly Func<characterScript, float>[] SkillGet =
        {
            c => c.s_gamedesign, c => c.s_programmieren, c => c.s_grafik, c => c.s_sound,
            c => c.s_pr,         c => c.s_gametests,     c => c.s_technik, c => c.s_forschen
        };
        private static readonly Action<characterScript, float>[] SkillSet =
        {
            (c,v) => c.s_gamedesign    = v, (c,v) => c.s_programmieren = v,
            (c,v) => c.s_grafik        = v, (c,v) => c.s_sound         = v,
            (c,v) => c.s_pr            = v, (c,v) => c.s_gametests     = v,
            (c,v) => c.s_technik       = v, (c,v) => c.s_forschen      = v
        };

        // Cached delegate for private GetSkillCap_Skill — zero-alloc direct call.
        // Harmony Postfix (Patch 2) still runs because MonoMod detours at IL level.
        private static readonly Func<characterScript, int, float> _getSkillCapSkill;

        static Patch_Learn()
        {
            var mi = AccessTools.Method(typeof(characterScript), "GetSkillCap_Skill");
            if (mi != null)
                _getSkillCapSkill = AccessTools.MethodDelegate<Func<characterScript, int, float>>(mi);
            else
                Plugin.Logger.LogWarning("GetSkillCap_Skill not found — SafetyClamp disabled. Game update?");
        }

        private static float GetCap(characterScript inst, int skillIndex)
            => _getSkillCapSkill != null
                ? _getSkillCapSkill(inst, skillIndex)
                : Plugin.CFG_MajorSkillCap.Value;

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        static void SafetyClamp(characterScript __instance)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;

                for (int i = 0; i < SkillGet.Length; i++)
                {
                    float cap = GetCap(__instance, i);
                    if (SkillGet[i](__instance) > cap)
                        SkillSet[i](__instance, cap);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"SafetyClamp: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // PATCH 4: Skill bar proportions — 6 methods via TargetMethods
    //
    // Replaces vanilla float literals with dynamic cap-relative values:
    //   0.01f -> GetFillFactor()         = 1/MajorCap
    //   0.5f  -> GetMinorFillMax()       = MinorCap/MajorCap
    //   0.6f  -> GetTalentMinorFillMax() = TalentMinorCap/MajorCap
    //
    // NOTE: Item_Personal_InRoom.SetData only — Update() has 0.01f for the
    // MOTIVATION bar (0-100 scale) which must NOT be patched.
    // =========================================================================
    [HarmonyPatch]
    class Patch_AllSkillBars
    {
        private static readonly Type[] _barTypes =
        {
            typeof(GUI_Main),                      typeof(GUI_Main),
            typeof(Menu_MitarbeiterUebersicht),    typeof(Item_Arbeitsmarkt),
            typeof(Item_Personal_InRoom),          typeof(Menu_NewGameCEO),
        };
        private static readonly string[] _barMethods =
        {
            "SetBalkenEmployee",  "SetBalkenArbeitsmarkt",
            "SetBalken",          "SetData",
            "SetData",            "SetBalken",
        };

        static IEnumerable<MethodBase> TargetMethods()
        {
            int found = 0;
            var missing = new List<string>();
            for (int i = 0; i < _barTypes.Length; i++)
            {
                var m = AccessTools.Method(_barTypes[i], _barMethods[i]);
                if (m != null) { found++; yield return m; }
                else missing.Add($"{_barTypes[i].Name}.{_barMethods[i]}");
            }

            Plugin.Logger.LogInfo($"SkillBars batch patch: {found}/{_barTypes.Length} methods found.");
            if (missing.Count > 0)
                Plugin.Logger.LogWarning($"SkillBars missing: {string.Join(", ", missing)}");
        }

        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
            => BarPatchHelper.Apply(instructions,
                $"{original.DeclaringType.Name}.{original.Name}");
    }

    // =========================================================================
    // PATCH 5: GUI_Main.GetValColorEmployee(float val)
    //
    // Unconditional normalization — this method exclusively receives skill values.
    // Scales [0..MajorCap] to [0..100] for vanilla color thresholds (30/70).
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
                val = Plugin.NormalizeSkill(val);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"GetValColorEmployee Prefix: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // PATCH 6: GetValColor() in 13 employee-related UI classes
    //
    // Conditional normalization — these methods receive BOTH skill values AND
    // percentage values (motivation etc.). Only values > 100 are skills from
    // our extended cap; percentages 0-100 are left as-is.
    //
    // Trade-off: Skills <= 100 (low-tier employees) show slightly too positive
    // colors. Acceptable vs. permanently red motivation indicators.
    //
    // Menu_Mitarbeitersuche intentionally EXCLUDED — its GetValColor receives
    // percentage/workpoint values, NOT skill values.
    // =========================================================================
    [HarmonyPatch]
    class Patch_AllGetValColor
    {
        private static readonly Type[] _targets =
        {
            typeof(Menu_MitarbeiterUebersicht),
            typeof(Menu_PersonalView),
            typeof(Menu_PersonalViewArbeitsmarkt),
            typeof(Menu_Personal_InRoom),
            typeof(Menu_PersonalLohnverhandlung),
            typeof(Menu_TooltipCharacter),
            typeof(Menu_PickCharacter),
            typeof(Menu_MitarbeitersucheResult),
            typeof(Menu_NewGameCEO),
            typeof(Item_PersonalGroup),
            typeof(Item_Personal_InRoom),
            typeof(Item_LeitenderDesigner),
            typeof(Item_Arbeitsmarkt),
        };

        static IEnumerable<MethodBase> TargetMethods()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;

            int found = 0;
            var missing = new List<string>();
            foreach (var t in _targets)
            {
                var m = t.GetMethod("GetValColor", flags, null, new[] { typeof(float) }, null);
                if (m != null) { found++; yield return m; }
                else missing.Add(t.Name);
            }

            Plugin.Logger.LogInfo($"GetValColor batch patch: {found}/{_targets.Length} methods found.");
            if (missing.Count > 0)
                Plugin.Logger.LogWarning($"GetValColor missing in: {string.Join(", ", missing)}");
        }

        [HarmonyPrefix]
        static void Prefix(ref float val)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;
                if (val > 100f)
                    val = Plugin.NormalizeSkill(val);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"GetValColor Prefix: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // PATCH 7: charArbeitsmarkt.Create() – Legends + Recruitment + Boost
    //
    // A) Developer Legends: scale to MajorCap (primary 80-95%, secondary 2-4%)
    // B) Recruitment tiers: override vanilla skill ranges with scaled values
    // C) Power-curve boost: configurable chance to boost random applicants
    //
    // Compatible with Job Market Tweaker (JMT Prefix returns false,
    // but Harmony Postfixes still run).
    // =========================================================================
    [HarmonyPatch(typeof(charArbeitsmarkt), "Create")]
    class Patch_JobMarketSkillBoost
    {
        // Recruitment tier definitions: 10 tiers covering 0-100% of MajorCap.
        internal static readonly float[] TierMin = { 0.02f, 0.10f, 0.20f, 0.30f, 0.40f, 0.50f, 0.60f, 0.70f, 0.80f, 0.90f };
        internal static readonly float[] TierMax = { 0.10f, 0.20f, 0.30f, 0.40f, 0.50f, 0.60f, 0.70f, 0.80f, 0.90f, 1.00f };
        internal static int TierCount => TierMin.Length;

        // Legend skill ranges (fraction of MajorCap)
        private const float LEGEND_SECONDARY_MIN = 0.02f;
        private const float LEGEND_SECONDARY_MAX = 0.04f;
        private const float LEGEND_PRIMARY_MIN   = 0.80f;
        private const float LEGEND_PRIMARY_MAX   = 0.95f;

        // Power-curve exponent — higher = rarer high values
        private const float POWER_CURVE_EXPONENT = 3f;

        private static readonly Func<charArbeitsmarkt, float>[] SkillGet =
        {
            c => c.s_gamedesign, c => c.s_programmieren, c => c.s_grafik, c => c.s_sound,
            c => c.s_pr,         c => c.s_gametests,     c => c.s_technik, c => c.s_forschen
        };
        private static readonly Action<charArbeitsmarkt, float>[] SkillSet =
        {
            (c,v) => c.s_gamedesign    = v, (c,v) => c.s_programmieren = v,
            (c,v) => c.s_grafik        = v, (c,v) => c.s_sound         = v,
            (c,v) => c.s_pr            = v, (c,v) => c.s_gametests     = v,
            (c,v) => c.s_technik       = v, (c,v) => c.s_forschen      = v
        };

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        static void Postfix(charArbeitsmarkt __instance, taskMitarbeitersuche task_)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;

                float majorCap = Plugin.CFG_MajorSkillCap.Value;
                int beruf = __instance.beruf;

                // --- A) Developer Legend ---
                if (__instance.legend != -1)
                {
                    for (int i = 0; i < SkillSet.Length; i++)
                        SkillSet[i](__instance, UnityEngine.Random.Range(
                            majorCap * LEGEND_SECONDARY_MIN, majorCap * LEGEND_SECONDARY_MAX));

                    SetPrimarySkill(__instance, beruf, UnityEngine.Random.Range(
                        majorCap * LEGEND_PRIMARY_MIN, majorCap * LEGEND_PRIMARY_MAX));
                    return;
                }

                // --- B) Recruitment Search ---
                if (task_ != null)
                {
                    int tier = task_.berufserfahrung;
                    if (tier >= 0 && tier < TierCount)
                        SetPrimarySkill(__instance, beruf,
                            UnityEngine.Random.Range(majorCap * TierMin[tier], majorCap * TierMax[tier]));
                    return;
                }

                // --- C) Random Power-Curve Boost (organic job market only) ---
                int chance = Plugin.CFG_MarketBoostChance.Value;
                if (chance <= 0) return;
                if (UnityEngine.Random.Range(0, 100) >= chance) return;

                float strength = Mathf.Pow(UnityEngine.Random.value, POWER_CURVE_EXPONENT);

                if (beruf >= 0 && beruf < SkillGet.Length)
                    SkillSet[beruf](__instance, Boost(SkillGet[beruf](__instance), majorCap, strength));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"JobMarketSkillBoost: {ex.Message}");
            }
        }

        private static void SetPrimarySkill(charArbeitsmarkt inst, int beruf, float value)
        {
            if (beruf >= 0 && beruf < SkillSet.Length)
                SkillSet[beruf](inst, value);
        }

        /// <summary>Lerp toward cap by strength. Never decreases, never exceeds cap.</summary>
        private static float Boost(float current, float cap, float strength)
        {
            if (current >= cap) return cap;
            return Mathf.Clamp(current + (cap - current) * strength, current, cap);
        }
    }

    // =========================================================================
    // PATCH 8: Menu_Mitarbeitersuche – Scaled Experience Tiers
    //
    // Extends vanilla's 3 tiers to 10 with fixed price/chance/workPoints tables
    // balanced against EnhancedTraining Reloaded.
    //
    // Arrays are ALWAYS extended regardless of CFG_IS_ENABLED to prevent
    // IndexOutOfRangeException when a saved task references tier >= 3.
    // =========================================================================
    [HarmonyPatch]
    class Patch_EmployeeSearch
    {
        private const int TOTAL_TIERS    = 10;
        private const int INJECTED_NAMES = 7;

        //   Tier | Price/search  | ~ total cost  | ET reference
        //   -----+---------------+---------------+-----------------
        //    0   |    $20,000    |     $25k      | (below ET I)
        //    1   |    $37,500    |     $50k      | (below ET I)
        //    2   |    $56,000    |     $80k      | (below ET I)
        //    3   |    $68,000    |    $105k      | ~ ET I   $150k
        //    4   |   $115,000    |    $192k      | ET I-II
        //    5   |   $154,000    |    $280k      | ~ ET II  $400k
        //    6   |   $315,000    |    $630k      | ~ ET III $900k
        //    7   |   $560,000    |   $1.19M      | ET III-IV
        //    8   |   $770,000    |   $1.75M      | ~ ET IV  $2.5M
        //    9   | $1,910,000    |   $4.55M      | ~ ET V   $6.5M
        private static readonly int[] TIER_PRICE =
        {
              20_000,   37_500,   56_000,   68_000,  115_000,
             154_000,  315_000,  560_000,  770_000, 1_910_000,
        };

        private static readonly float[] TIER_CHANCE =
        {
            80f, 75f, 70f, 65f, 60f, 55f, 50f, 47f, 44f, 42f
        };

        private static readonly float[] TIER_WORKPOINTS =
        {
            15f, 20f, 28f, 38f, 50f, 65f, 85f, 110f, 145f, 185f
        };

        // Tier name resolution: vanilla text ID (>0) or injected name offset (when 0)
        private const int TEXT_ID_YOUNG_PRO  = 1710;
        private const int TEXT_ID_SKILLED    = 1711;
        private const int TEXT_ID_SPECIALIST = 1712;

        private static readonly int[] TierVanillaTextId = { 0, TEXT_ID_YOUNG_PRO, 0, TEXT_ID_SKILLED, 0, TEXT_ID_SPECIALIST, 0, 0, 0, 0 };
        private static readonly int[] TierInjNameIdx    = { 0, -1, 1, -1, 2, -1, 3, 4, 5, 6 };

        private static int _textIdBase = -1;

        internal static void ResetState() => _textIdBase = -1;

        // ---- Text injection: 7 tier names x 20 languages --------------------

        private static void EnsureTextInjected(textScript tS)
        {
            if (_textIdBase >= 0 || tS == null) return;

            int baseIndex = tS.text_EN.Length;
            _textIdBase = baseIndex;
            int newLen = baseIndex + INJECTED_NAMES;

            InjectNames(ref tS.text_EN, newLen, "Newcomer", "Apprentice", "Journeyman", "Senior", "Expert", "Master", "Legend");
            InjectNames(ref tS.text_GE, newLen, "Neuling", "Lehrling", "Geselle", "Senior", "Experte", "Meister", "Legende");
            InjectNames(ref tS.text_TU, newLen, "Acemi", "\u00C7\u0131rak", "Kalfa", "K\u0131demli", "Usta", "\u00DCstat", "Efsane");
            InjectNames(ref tS.text_CH, newLen, "\u65B0\u624B", "\u5B66\u5F92", "\u719F\u7EC3\u5DE5", "\u8D44\u6DF1", "\u8D44\u6DF1\u4E13\u5BB6", "\u5927\u5E08", "\u4F20\u5947");
            InjectNames(ref tS.text_FR, newLen, "Novice", "Apprenti", "Compagnon", "Senior", "Expert", "Ma\u00EEtre", "L\u00E9gende");
            InjectNames(ref tS.text_ES, newLen, "Novato", "Aprendiz", "Oficial", "Senior", "Experto", "Maestro", "Leyenda");
            InjectNames(ref tS.text_KO, newLen, "\uC2E0\uC785", "\uACAC\uC2B5\uC0DD", "\uC219\uB828\uACF5", "\uC2DC\uB2C8\uC5B4", "\uB2EC\uC778", "\uC7A5\uC778", "\uC804\uC124");
            InjectNames(ref tS.text_PB, newLen, "Novato", "Aprendiz", "Oficial", "S\u00EAnior", "Veterano", "Mestre", "Lenda");
            InjectNames(ref tS.text_HU, newLen, "Newcomer", "Apprentice", "Journeyman", "Senior", "Expert", "Master", "Legend");
            InjectNames(ref tS.text_RU, newLen, "\u041D\u043E\u0432\u0438\u0447\u043E\u043A", "\u0423\u0447\u0435\u043D\u0438\u043A", "\u041F\u043E\u0434\u043C\u0430\u0441\u0442\u0435\u0440\u044C\u0435", "\u0421\u0442\u0430\u0440\u0448\u0438\u0439", "\u042D\u043A\u0441\u043F\u0435\u0440\u0442", "\u041C\u0430\u0441\u0442\u0435\u0440", "\u041B\u0435\u0433\u0435\u043D\u0434\u0430");
            InjectNames(ref tS.text_CT, newLen, "\u65B0\u624B", "\u5B78\u5F92", "\u719F\u7DF4\u5DE5", "\u8CC7\u6DF1", "\u8CC7\u6DF1\u5C08\u5BB6", "\u5927\u5E2B", "\u50B3\u5947");
            InjectNames(ref tS.text_PL, newLen, "Nowicjusz", "Ucze\u0144", "Czeladnik", "Senior", "Ekspert", "Mistrz", "Legenda");
            InjectNames(ref tS.text_CZ, newLen, "Nov\u00E1\u010Dek", "U\u010De\u0148", "Tovary\u0161", "Senior", "Expert", "Mistr", "Legenda");
            InjectNames(ref tS.text_AR, newLen, "Newcomer", "Apprentice", "Journeyman", "Senior", "Expert", "Master", "Legend");
            InjectNames(ref tS.text_IT, newLen, "Principiante", "Apprendista", "Operaio", "Senior", "Esperto", "Maestro", "Leggenda");
            InjectNames(ref tS.text_RO, newLen, "\u00CEncep\u0103tor", "Ucenic", "Calf\u0103", "Senior", "Expert", "Maestru", "Legend\u0103");
            InjectNames(ref tS.text_JA, newLen, "\u65B0\u4EBA", "\u898B\u7FD2\u3044", "\u8077\u4EBA", "\u30B7\u30CB\u30A2", "\u30A8\u30AD\u30B9\u30D1\u30FC\u30C8", "\u30DE\u30B9\u30BF\u30FC", "\u4F1D\u8AAC");
            InjectNames(ref tS.text_UA, newLen, "\u041D\u043E\u0432\u0430\u0447\u043E\u043A", "\u0423\u0447\u0435\u043D\u044C", "\u041F\u0456\u0434\u043C\u0430\u0439\u0441\u0442\u0435\u0440", "\u0421\u0442\u0430\u0440\u0448\u0438\u0439", "\u0415\u043A\u0441\u043F\u0435\u0440\u0442", "\u041C\u0430\u0439\u0441\u0442\u0435\u0440", "\u041B\u0435\u0433\u0435\u043D\u0434\u0430");
            InjectNames(ref tS.text_LA, newLen, "Novato", "Aprendiz", "Oficial", "Senior", "Experto", "Maestro", "Leyenda");
            InjectNames(ref tS.text_TH, newLen, "\u0E21\u0E37\u0E2D\u0E43\u0E2B\u0E21\u0E48", "\u0E1D\u0E36\u0E01\u0E2B\u0E31\u0E14", "\u0E0A\u0E48\u0E32\u0E07\u0E1D\u0E35\u0E21\u0E37\u0E2D", "\u0E2D\u0E32\u0E27\u0E38\u0E42\u0E2A", "\u0E1C\u0E39\u0E49\u0E40\u0E0A\u0E35\u0E48\u0E22\u0E27\u0E0A\u0E32\u0E0D", "\u0E1B\u0E23\u0E21\u0E32\u0E08\u0E32\u0E23\u0E22\u0E4C", "\u0E15\u0E33\u0E19\u0E32\u0E19");
        }

        private static void InjectNames(ref string[] arr, int newLen, params string[] names)
        {
            // Array.Resize handles null — no need for intermediate empty array.
            if (arr == null || arr.Length < newLen)
                Array.Resize(ref arr, newLen);
            int baseOffset = newLen - names.Length;
            for (int i = 0; i < names.Length; i++)
                arr[baseOffset + i] = names[i];
        }

        // ---- Array extension (savegame-safe) --------------------------------

        internal static void EnsureArraysExtended(Menu_Mitarbeitersuche inst)
        {
            if (inst == null) return;
            if (inst.price != null && inst.price.Length >= TOTAL_TIERS) return;

            inst.price      = new int[TOTAL_TIERS];
            inst.chance     = new float[TOTAL_TIERS];
            inst.workPoints = new float[TOTAL_TIERS];

            for (int i = 0; i < TOTAL_TIERS; i++)
            {
                inst.price[i]      = TIER_PRICE[i];
                inst.chance[i]     = TIER_CHANCE[i];
                inst.workPoints[i] = TIER_WORKPOINTS[i];
            }
        }

        // ---- Tier name resolution (pure function) ---------------------------

        private static string ResolveTierName(textScript tS, int tierIndex)
        {
            if (tS == null) return $"Tier {tierIndex + 1}";

            int vanillaId = TierVanillaTextId[tierIndex];
            if (vanillaId > 0) return tS.GetText(vanillaId);

            return _textIdBase >= 0
                ? tS.GetText(_textIdBase + TierInjNameIdx[tierIndex])
                : $"Tier {tierIndex + 1}";
        }

        private static textScript FindTextScript()
        {
            var main = GameObject.FindGameObjectWithTag("Main");
            return main != null ? main.GetComponent<textScript>() : null;
        }

        // ---- Dropdown rebuild (extracted from Init_Postfix for SRP) ---------

        private static void RebuildTierDropdown(Menu_Mitarbeitersuche inst)
        {
            if (inst.uiObjects == null || inst.uiObjects.Length < 2) return;

            var dropdown = inst.uiObjects[1].GetComponent<UnityEngine.UI.Dropdown>();
            if (dropdown == null) return;

            float cap = Plugin.CFG_MajorSkillCap.Value;
            int savedValue = dropdown.value;
            int tierCount  = Patch_JobMarketSkillBoost.TierCount;

            textScript tS = FindTextScript();
            EnsureTextInjected(tS);

            var options = new List<string>(tierCount);
            for (int i = 0; i < tierCount; i++)
            {
                int lo = Mathf.RoundToInt(cap * Patch_JobMarketSkillBoost.TierMin[i]);
                int hi = Mathf.RoundToInt(cap * Patch_JobMarketSkillBoost.TierMax[i]);
                options.Add($"<b>[{lo}-{hi}]</b> {ResolveTierName(tS, i)}");
            }

            dropdown.ClearOptions();
            dropdown.AddOptions(options);
            dropdown.value = Mathf.Clamp(savedValue, 0, tierCount - 1);
        }

        // ---- Harmony patches ------------------------------------------------

        [HarmonyPatch(typeof(Menu_Mitarbeitersuche), "Init")]
        [HarmonyPostfix]
        static void Init_Postfix(Menu_Mitarbeitersuche __instance)
        {
            try
            {
                if (!Plugin.CFG_IS_ENABLED.Value) return;
                EnsureArraysExtended(__instance);
                RebuildTierDropdown(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"EmployeeSearch Init: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(Menu_Mitarbeitersuche), "GetChance")]
        [HarmonyPrefix]
        static void GetChance_Prefix(Menu_Mitarbeitersuche __instance)
        {
            try   { EnsureArraysExtended(__instance); }
            catch (Exception ex) { Plugin.Logger.LogError($"GetChance Prefix: {ex.Message}"); }
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
                    codes[i] = new CodeInstruction(OpCodes.Call, getFill);
                    patched++;
                }
                else if (Math.Abs(f - 0.5f) < 0.0001f)
                {
                    if (i + 1 < codes.Count && IsArithmeticOp(codes[i + 1].opcode))
                        continue;
                    codes[i] = new CodeInstruction(OpCodes.Call, getMinorMax);
                    patched++;
                }
                else if (Math.Abs(f - 0.6f) < 0.0001f)
                {
                    if (i + 1 < codes.Count && IsArithmeticOp(codes[i + 1].opcode))
                        continue;
                    codes[i] = new CodeInstruction(OpCodes.Call, getTalentMax);
                    patched++;
                }
            }

            if (patched > 0)
                Plugin.Logger.LogInfo($"{callerName} Transpiler: {patched} patches applied.");
            else
                Plugin.Logger.LogWarning($"{callerName} Transpiler: No patches applied! Game update?");

            return codes;
        }

        private static bool IsArithmeticOp(OpCode op)
            => op == OpCodes.Mul || op == OpCodes.Div
            || op == OpCodes.Add || op == OpCodes.Sub
            || op == OpCodes.Rem;
    }
}
