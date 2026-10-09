using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using HarmonyLib;

namespace Avatar
{
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit ()
        {
            new Harmony("AvatarMod").PatchAll();
            // Install the Application.quitting hook ONCE on game load so any
            // ComfyUI process we launched gets force-killed when RimWorld exits.
            // Skipping this means the python.exe child of cmd.exe leaks and
            // keeps eating VRAM until the user reboots or kills it by hand.
            AIGen.InstallQuitHook();
            // Review #9: adopt any ComfyUI process orphaned by a previous
            // crashed / Task-Manager-killed session. The PID sidecar tells us
            // what to look for. Idempotent and safe — on a clean session this
            // is a no-op (no sidecar file present).
            AIGen.RecoverOrphanedComfyUI();
            // Zero-touch bootstrap: if ComfyUI + the SDXL checkpoint aren't on
            // disk yet (and autoSetup is on), kick off the download/install
            // pipeline — it ends by prewarming. Otherwise just eager pre-warm
            // the already-installed server so its ~30-60s cold boot overlaps
            // with the player loading their save instead of stalling the first
            // portrait. Both paths are background-threaded; game load is never
            // blocked. Gated on autoLaunchComfyUI inside PrewarmComfyUI.
            if (SetupManager.ShouldAutoSetup())
                SetupManager.StartBootstrap();
            else
                AIGen.PrewarmComfyUI();
        }
    }

    // ============================================================================
    // Animated spinner overlay for pawns whose portrait is queued or in-flight.
    // Fires on the vanilla colonist bar. The spinner draws on top of the avatar
    // (which is what mod.GetColonistBarAvatar returned via the GetPortrait
    // transpiler above) so the user gets immediate "yes, the mod is working on
    // it" feedback even before the first portrait file lands.
    //
    // 8 dots arranged in a circle, each at a different alpha, rotating once per
    // ~0.8s. Uses BaseContent.WhiteTex tinted via GUI.color — cheap and matches
    // the rest of RimWorld's GUI vocabulary.
    // ============================================================================
    [HarmonyPatch(typeof(ColonistBarColonistDrawer), nameof(ColonistBarColonistDrawer.DrawColonist))]
    public static class ColonistBar_SpinnerOverlay_Patch
    {
        // Note: deliberately NOT gated on `mod.settings.showInColonistBar`.
        // The spinner indicates "a portrait is being generated for this
        // specific pawn" — useful feedback whether or not the user has the
        // avatar replacing their vanilla colonist bar. Per-pawn check via
        // IsPending is the only thing that decides whether to draw.
        public static void Postfix(Rect rect, Pawn colonist)
        {
            if (colonist == null) return;
            // Hot-path early-out via the thread-safe accessor. PendingCount
            // is O(segments) on ConcurrentDictionary — still fast enough to
            // call every frame for every colonist; for typical colonies the
            // dict is empty most of the time and this returns immediately.
            if (AvatarMod.PendingCount == 0) return;
            if (!AvatarMod.IsPending(colonist.thingIDNumber)) return;
            // Draw spinner over the actual portrait rect with a dark scrim
            // so the dots pop on bright pixel-art. Sized to the rect.
            DrawPortraitSpinner(rect);
        }

        // Colonist-bar version: scrim + centered spinner sized to the rect.
        private static void DrawPortraitSpinner(Rect rect)
        {
            Color saved = GUI.color;
            try
            {
                // Dark scrim so the spinner pops on bright pixel-art
                GUI.color = new Color(0f, 0f, 0f, 0.40f);
                GUI.DrawTexture(rect, BaseContent.WhiteTex);

                DrawDotSpinner(
                    rect.center,
                    Mathf.Min(rect.width, rect.height) * 0.28f,
                    Mathf.Max(2f, Mathf.Min(rect.width, rect.height) * 0.10f));
            }
            finally { GUI.color = saved; }
        }

        // Reusable 8-dot rotating spinner. Caller controls position + scale.
        // Does NOT draw a scrim — if you want one, draw it yourself before
        // calling this so callers that draw over photos / non-darkened
        // content (e.g. the main inspect-pane avatar) can opt out.
        //
        // One full revolution per ~0.8s. Time.realtimeSinceStartup keeps
        // spinning even when the game is paused — important because pawns
        // routinely spawn during loading screens when game time is frozen.
        public static void DrawDotSpinner(Vector2 center, float radius, float dotSize)
        {
            float phase = (Time.realtimeSinceStartup * 1.25f) % 1f;
            float baseAngle = phase * 360f;
            const int dots = 8;
            for (int i = 0; i < dots; i++)
            {
                float deg = baseAngle - i * (360f / dots);
                float rad = deg * Mathf.Deg2Rad;
                float alpha = 0.15f + 0.85f * (1f - (float)i / dots);
                GUI.color = new Color(1f, 1f, 1f, alpha);
                Vector2 p = center + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radius;
                GUI.DrawTexture(
                    new Rect(p.x - dotSize * 0.5f, p.y - dotSize * 0.5f, dotSize, dotSize),
                    BaseContent.WhiteTex);
            }
        }
    }

    [HarmonyPatch(typeof(PrefsData), nameof(PrefsData.Apply))]
    public static class GlobalHatSetting_Patch
    {
        public static void Postfix()
        {
            AvatarMod.ClearCachedAvatars();
        }
    }

    // patch vanilla colonist bar drawing function to use the avatars
    [HarmonyPatch(typeof(ColonistBarColonistDrawer), nameof(ColonistBarColonistDrawer.DrawColonist))]
    public static class ColonistBar_Transpiler_Patch
    {
        private static AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();

        #if v1_3
        private static Texture GetPortrait(Pawn pawn, Vector2 size, Rot4 rotation, Vector3 cameraOffset = default(Vector3), float cameraZoom = 1f, bool supersample = true, bool compensateForUIScale = true, bool renderHeadgear = true, bool renderClothes = true, Dictionary<Apparel, Color> overrideApparelColors = null, Color? overrideHairColor = default(Color?), bool stylingStation = false)
        #else
        private static Texture GetPortrait(Pawn pawn, Vector2 size, Rot4 rotation, Vector3 cameraOffset = default(Vector3), float cameraZoom = 1f, bool supersample = true, bool compensateForUIScale = true, bool renderHeadgear = true, bool renderClothes = true, IReadOnlyDictionary<Apparel, Color> overrideApparelColors = null, Color? overrideHairColor = null, bool stylingStation = false, PawnHealthState? healthStateOverride = null)
        #endif
        {
            if (mod.settings.showInColonistBar)
            {
                // vanilla ignores the "renderHeadgear" parameter anyway...
                return mod.GetColonistBarAvatar(pawn, !Prefs.HatsOnlyOnMap, !ModCompatibility.ModdedNudity(pawn));
            }
            #if v1_3
            return PortraitsCache.Get(pawn, size, rotation, cameraOffset, cameraZoom, supersample, compensateForUIScale, renderHeadgear, renderClothes, overrideApparelColors, overrideHairColor, stylingStation);
            #else
            return PortraitsCache.Get(pawn, size, rotation, cameraOffset, cameraZoom, supersample, compensateForUIScale, renderHeadgear, renderClothes, overrideApparelColors, overrideHairColor, stylingStation, healthStateOverride);
            #endif
        }

        private static MethodInfo oldMethod = AccessTools.Method("PortraitsCache:Get");
        private static MethodInfo newMethod = AccessTools.Method("ColonistBar_Transpiler_Patch:GetPortrait");
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction.Calls(oldMethod) ? new CodeInstruction(OpCodes.Call, newMethod) : instruction;
            }
        }
    }

    // patch CCMBar drawing function to use the avatars
    [HarmonyPatch]
    public static class CCMBar_Transpiler_Patch
    {
        private static MethodInfo target;
        public static MethodBase TargetMethod() => target;
        public static bool Prepare()
        {
            if (ModCompatibility.CCMBar_Loaded)
            {
                target = AccessTools.Method("ColoredMoodBar13.MoodPatch:DrawColonist");
                return target != null;
            }
            return false;
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ColonistBar_Transpiler_Patch.Transpiler(instructions);
    }

    // patch ColonyGroups drawing function to use the avatars
    [HarmonyPatch]
    public static class ColonyGroups_Transpiler_Patch
    {
        private static MethodInfo target;
        public static MethodBase TargetMethod() => target;
        public static bool Prepare()
        {
            if (ModCompatibility.ColonyGroups_Loaded)
            {
                target = AccessTools.Method("TacticalGroups.TacticalGroups_ColonistBarColonistDrawer:DrawColonist");
                return target != null;
            }
            return false;
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ColonistBar_Transpiler_Patch.Transpiler(instructions);
    }

    // patch GetRect for vanilla colonist bar drawing function
    [HarmonyPatch(typeof(ColonistBarColonistDrawer), nameof(ColonistBarColonistDrawer.GetPawnTextureRect))]
    class ColonistBar_GetRect_Patch
    {
        private static AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();
        public static bool Prefix(ref Rect __result, Vector2 pos)
        {
            if (mod.settings.showInColonistBar)
            {
                float adjust = mod.settings.showInColonistBarSizeAdjust;
                float width = (ColonistBarColonistDrawer.PawnTextureSize.x+2f*adjust)*Find.ColonistBar.Scale;
                Vector2 vector = new (width, width*1.2f);
                __result = new Rect (pos.x-adjust, pos.y - (vector.y - Find.ColonistBar.Size.y) - 1f, vector.x, vector.y).ContractedBy (1f);
                return false;
            }
            return true;
        }
    }

    // patch GetRect for ColonyGroups drawing function
    [HarmonyPatch]
    class ColonyGroups_GetRect_Patch
    {
        private static AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();
        private static MethodInfo target;
        public static MethodBase TargetMethod() => target;
        public static bool Prepare()
        {
            if (ModCompatibility.ColonyGroups_Loaded)
            {
                target = AccessTools.Method("TacticalGroups.TacticalGroups_ColonistBarColonistDrawer:GetPawnTextureRect");
                return target != null;
            }
            return false;
        }
        public static bool Prefix(ref Rect __result, Vector2 pos)
        {
            if (mod.settings.showInColonistBar)
            {
                ModCompatibility.GetFieldInfo("TacticalGroups.TacticalGroups_ColonistBarColonistDrawer:PawnTextureSize").SetValue(null, new Vector2(40f, 48f));
                var bar = ModCompatibility.GetFieldInfo("TacticalGroups.TacticUtils:TacticalColonistBar").GetValue(null);
                float scale = (float) ModCompatibility.GetFieldInfo("TacticalGroups.TacticalGroupsSettings:PawnScale").GetValue(null);
                float size_y = ((Vector2) ModCompatibility.GetMethodInfo("TacticalGroups.TacticalColonistBar:get_Size").Invoke(bar, null)).y;
                float boxWidth = (float) ModCompatibility.GetFieldInfo("TacticalGroups.TacticalGroupsSettings:PawnBoxWidth").GetValue(null);
                float width = boxWidth+20*(scale-1f);
                Vector2 vector = new (width, width*1.2f);
                __result = new Rect (pos.x-10*(scale-1f), pos.y - (vector.y - size_y) - 1f, vector.x, vector.y).ContractedBy (1f);
                return false;
            }
            return true;
        }
    }

    // patch GetRect for CCMBar drawing function
    [HarmonyPatch]
    class CCMBar_GetRect_Patch
    {
        private static AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();
        private static MethodInfo target;
        public static MethodBase TargetMethod() => target;
        public static bool Prepare()
        {
            if (ModCompatibility.CCMBar_Loaded)
            {
                target = AccessTools.Method("ColoredMoodBar13.MoodCache:GetPawnTextureRect");
                return target != null;
            }
            return false;
        }
        static bool Prefix(ref object __instance, ref Rect __result, Vector2 pos)
        {
            if (mod.settings.showInColonistBar)
            {
                if ((bool) ModCompatibility.GetFieldInfo("ColoredMoodBar13.MoodCache:ScalePortrait").GetValue(__instance))
                {
                    // the smaller portraits
                    float scale = (float) ModCompatibility.GetFieldInfo("ColoredMoodBar13.MoodCache:Scale").GetValue(__instance);
                    float width = ColonistBarColonistDrawer.PawnTextureSize.x*Find.ColonistBar.Scale*scale;
                    Vector2 vector = new (width, width*1.2f);
                    __result = new Rect (pos.x+1f, pos.y - (vector.y - Find.ColonistBar.Size.y*scale) - 1f, vector.x, vector.y).ContractedBy (1f);
                    return false;
                }
                return ColonistBar_GetRect_Patch.Prefix(ref __result, pos);
            }
            return true;
        }
    }

    // patch GetRectCG for CCMBar drawing function
    [HarmonyPatch]
    class CCMBar_ColonyGroups_GetRect_Patch
    {
        private static MethodInfo target;
        public static MethodBase TargetMethod() => target;
        public static bool Prepare()
        {
            if (ModCompatibility.CCMBar_Loaded && ModCompatibility.ColonyGroups_Loaded)
            {
                target = AccessTools.Method("ColoredMoodBar13.MoodCache:GetPawnTextureRectCG");
                return target != null;
            }
            return false;
        }
        static bool Prefix(ref Rect __result, Vector2 pos) => ColonyGroups_GetRect_Patch.Prefix(ref __result, pos);
    }

    // patch FacialAnimation colonist bar update function
    [HarmonyPatch]
    class FA_UpdatePortrait_Patch
    {
        private static AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();
        private static MethodInfo target;
        public static MethodBase TargetMethod() => target;
        public static bool Prepare()
        {
            if (ModCompatibility.FacialAnimation_Loaded)
            {
                target = AccessTools.Method("FacialAnimation.FacialAnimationControllerComp:UpdatePortrait");
                return target != null;
            }
            return false;
        }
        public static void SetDirty(Pawn pawn)
        {
            if (!mod.settings.showInColonistBar)
            {
                PortraitsCache.SetDirty(pawn);
            }
        }
        private static MethodInfo oldMethod = AccessTools.Method("PortraitsCache:SetDirty");
        private static MethodInfo newMethod = AccessTools.Method("FA_UpdatePortrait_Patch:SetDirty");
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction.Calls(oldMethod) ? new CodeInstruction(OpCodes.Call, newMethod) : instruction;
            }
        }
    }
}
