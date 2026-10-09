#if !v1_3
#define BIOTECH
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Verse;
using RimWorld;

namespace Avatar
{
    public static class XenotypeDescriptionGenerator
    {
        /// <summary>
        /// Returns a concise visual description suitable for the {race} tag in AI image prompts.
        /// </summary>
        public static string GetRaceDescription(Pawn pawn)
        {
            // Non-human races: use their race label directly
            string raceDefName = pawn.RaceProps?.AnyPawnKind?.race?.defName ?? "Human";
            if (raceDefName != "Human")
            {
                return pawn.RaceProps.AnyPawnKind.race.label.ToLower();
            }

            // Without Biotech, everyone is just human
            #if !BIOTECH
            return "human";
            #else
            if (!ModsConfig.BiotechActive || pawn.genes == null)
                return "human";

            XenotypeDef xenotype = pawn.genes.Xenotype;
            if (xenotype == null || xenotype == XenotypeDefOf.Baseliner)
                return "human";

            // 1. Curated prompt lookup — mod authors can add AIGenPromptDef entries
            //    with defName matching their XenotypeDef.defName
            AIGenPromptDef curated = DefDatabase<AIGenPromptDef>.GetNamedSilentFail(xenotype.defName);
            if (curated != null && !curated.prompt.NullOrEmpty())
                return curated.prompt;

            // 2. For vanilla/DLC xenotypes with good descriptionShort, extract first sentence
            if (!xenotype.descriptionShort.NullOrEmpty())
            {
                string sanitized = SanitizeDescription(xenotype.descriptionShort);
                if (!sanitized.NullOrEmpty())
                    return sanitized;
            }

            // 3. For mod-added xenotypes without curated prompts, generate from genes
            return GenerateFromGenes(pawn, xenotype);
            #endif
        }

        #if BIOTECH
        private static string GenerateFromGenes(Pawn pawn, XenotypeDef xenotype)
        {
            List<GeneDef> genes = xenotype.AllGenes;
            if (genes.NullOrEmpty())
                return xenotype.label.ToLower();

            List<string> descriptors = new List<string>();

            // Body type
            GeneticBodyType? bodyType = null;
            foreach (GeneDef gene in genes)
            {
                if (gene.bodyType.HasValue)
                {
                    bodyType = gene.bodyType.Value;
                    break;
                }
            }
            switch (bodyType)
            {
                case GeneticBodyType.Thin:
                    descriptors.Add("thin");
                    break;
                case GeneticBodyType.Hulk:
                    descriptors.Add("muscular");
                    break;
                case GeneticBodyType.Fat:
                    descriptors.Add("heavyset");
                    break;
            }

            // Fur
            bool hasFur = genes.Any(g => g.fur != null);
            if (hasFur)
                descriptors.Add("furry");

            // Skin color
            string skinDesc = GetSkinColorDescriptor(genes);
            if (!skinDesc.NullOrEmpty())
                descriptors.Add(skinDesc);

            // Head features (horns, heavy jaw, brow, pig nose, pig ears, gaunt)
            bool hasHorns = genes.Any(g => g.defName.Contains("Horns") || g.defName.Contains("horn"));
            if (hasHorns)
                descriptors.Add("horned");

            bool heavyJaw = genes.Any(g => g.defName.Contains("Jaw_Heavy"));
            if (heavyJaw)
                descriptors.Add("heavy jaw");

            bool heavyBrow = genes.Any(g => g.defName.Contains("Brow_Heavy"));
            if (heavyBrow)
                descriptors.Add("heavy brow");

            bool gauntHead = genes.Any(g => g.defName.Contains("Head_Gaunt"));
            if (gauntHead)
                descriptors.Add("gaunt");

            bool pigNose = genes.Any(g => g.defName.Contains("Nose_Pig"));
            bool pigEars = genes.Any(g => g.defName.Contains("Ears_Pig"));
            if (pigNose || pigEars)
                descriptors.Add("pig-like features");

            // Tail
            bool hasTail = genes.Any(g => g.defName.Contains("Tail_"));
            if (hasTail)
                descriptors.Add("tail");

            // Eyes
            string eyeDesc = GetEyeColorDescriptor(genes);
            if (!eyeDesc.NullOrEmpty())
                descriptors.Add(eyeDesc);

            // Hair color constraint
            string hairDesc = GetHairColorDescriptor(genes);
            if (!hairDesc.NullOrEmpty())
                descriptors.Add(hairDesc);

            // Bald / no beard
            bool baldOnly = genes.Any(g => g.defName.Contains("Hair_BaldOnly"));
            if (baldOnly)
                descriptors.Add("bald");

            // Special visual abilities
            bool fireSpew = genes.Any(g => g.defName.Contains("FireSpew"));
            if (fireSpew)
                descriptors.Add("demon-like");

            bool hemogenic = genes.Any(g => g.defName.Contains("Hemogenic"));
            if (hemogenic)
                descriptors.Add("pale vampire-like");

            // Build the final string
            string baseType = hasFur ? "humanoid" : "human";
            if (descriptors.Count == 0)
                return xenotype.label.ToLower();

            // Limit to most visually distinctive descriptors (max 4)
            var prioritized = PrioritizeDescriptors(descriptors);
            return $"{baseType} with {string.Join(", ", prioritized)}";
        }

        private static string GetSkinColorDescriptor(List<GeneDef> genes)
        {
            foreach (GeneDef gene in genes)
            {
                string name = gene.defName;
                if (name.StartsWith("Skin_"))
                {
                    switch (name)
                    {
                        case "Skin_SheerWhite": return "sheer white skin";
                        case "Skin_LightGray": return "light gray skin";
                        case "Skin_SlateGray": return "slate gray skin";
                        case "Skin_InkBlack": return "ink black skin";
                        case "Skin_Blue": return "blue skin";
                        case "Skin_Purple": return "purple skin";
                        case "Skin_PaleRed": return "pale red skin";
                        case "Skin_DeepRed": return "deep red skin";
                        case "Skin_PaleYellow": return "pale yellow skin";
                        case "Skin_DeepYellow": return "deep yellow skin";
                        case "Skin_Orange": return "orange skin";
                        case "Skin_Green": return "green skin";
                        default:
                            // Try to extract color from defName like Skin_MyColor
                            string color = name.Substring(5).ToLower();
                            return $"{color} skin";
                    }
                }
                if (gene.skinColorBase.HasValue || gene.skinColorOverride.HasValue)
                {
                    Color c = gene.skinColorOverride ?? gene.skinColorBase.Value;
                    string colorName = ApproximateColorName(c);
                    if (!colorName.NullOrEmpty())
                        return $"{colorName} skin";
                }
            }
            return null;
        }

        private static string GetEyeColorDescriptor(List<GeneDef> genes)
        {
            foreach (GeneDef gene in genes)
            {
                string name = gene.defName;
                if (name.StartsWith("Eyes_"))
                {
                    switch (name)
                    {
                        case "Eyes_Red": return "red eyes";
                        case "Eyes_Gray": return "gray eyes";
                        default:
                            string color = name.Substring(5).ToLower();
                            return $"{color} eyes";
                    }
                }
            }
            return null;
        }

        private static string GetHairColorDescriptor(List<GeneDef> genes)
        {
            foreach (GeneDef gene in genes)
            {
                string name = gene.defName;
                if (name.StartsWith("Hair_"))
                {
                    switch (name)
                    {
                        case "Hair_SnowWhite": return "white hair";
                        case "Hair_Gray": return "gray hair";
                        case "Hair_LightOrange": return "light orange hair";
                        case "Hair_SandyBlonde": return "sandy blonde hair";
                        default: break;
                    }
                }
                if (gene.hairColorOverride.HasValue)
                {
                    string colorName = ApproximateColorName(gene.hairColorOverride.Value);
                    if (!colorName.NullOrEmpty())
                        return $"{colorName} hair";
                }
            }
            return null;
        }

        private static string ApproximateColorName(Color c)
        {
            // Simple hue-based approximation
            float h, s, v;
            Color.RGBToHSV(c, out h, out s, out v);

            if (s < 0.1f)
            {
                if (v > 0.9f) return "white";
                if (v < 0.2f) return "black";
                return "gray";
            }

            if (h < 0.03f || h > 0.97f) return "red";
            if (h < 0.08f) return s > 0.5f ? "orange" : "brown";
            if (h < 0.15f) return "yellow";
            if (h < 0.22f) return "yellow-green";
            if (h < 0.45f) return "green";
            if (h < 0.55f) return "cyan";
            if (h < 0.70f) return "blue";
            if (h < 0.82f) return "purple";
            if (h < 0.92f) return "magenta";
            return "red";
        }

        private static List<string> PrioritizeDescriptors(List<string> descriptors)
        {
            // Most visually distinctive first
            var priorityOrder = new List<string>
            {
                "furry", "horned", "demon-like", "pale vampire-like",
                "pig-like features", "tail",
                "heavy jaw", "heavy brow", "gaunt",
                "red eyes", "gray eyes",
                "blue skin", "green skin", "purple skin", "orange skin", "gray skin", "white skin", "black skin",
                "white hair", "gray hair",
                "bald",
                "muscular", "thin", "heavyset"
            };

            var sorted = descriptors.OrderBy(d =>
            {
                int idx = priorityOrder.IndexOf(d);
                return idx >= 0 ? idx : int.MaxValue;
            }).ToList();

            // Cap at 4 descriptors to keep prompt concise
            if (sorted.Count > 4)
                sorted = sorted.Take(4).ToList();

            return sorted;
        }
        #endif

        private static string SanitizeDescription(string desc)
        {
            if (desc.NullOrEmpty())
                return null;

            // Take only the first sentence (up to first period)
            int period = desc.IndexOf('.');
            string first = period > 0 ? desc.Substring(0, period) : desc;

            // Remove newlines
            first = first.Replace("\n", " ").Trim();

            // Remove lore-heavy phrases that don't help image generation
            first = Regex.Replace(first, @"Originally engineered.*", "", RegexOptions.IgnoreCase);
            first = Regex.Replace(first, @"They were created.*", "", RegexOptions.IgnoreCase);
            first = Regex.Replace(first, @"Today,.*", "", RegexOptions.IgnoreCase);
            first = Regex.Replace(first, @"\(.*?\)", ""); // remove parenthetical notes

            first = first.Trim();
            if (first.Length < 3)
                return null;

            // Lowercase for prompt consistency
            return first.ToLower();
        }
    }
}
