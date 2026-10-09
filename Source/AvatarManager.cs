#if !v1_3
#define BIOTECH
#endif
#if !(v1_3 || v1_4)
#define ANOMALY
#endif
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using HarmonyLib;

namespace Avatar
{
    public class AvatarManager
    {
        public static AvatarMod mod;
        public Pawn pawn;
        private Feature feature;
        private Texture2D canvas;
        private Texture2D avatar;
        private bool _drawHeadgear = true;
        private bool _drawClothes = true;
        public bool drawHeadgear { get => this._drawHeadgear;
            set
            {
                if (this._drawHeadgear != value)
                {
                    this._drawHeadgear = value;
                    ClearCachedAvatar();
                }
            }
        }
        public bool drawClothes { get => this._drawClothes;
            set
            {
                if (this._drawClothes != value)
                {
                    this._drawClothes = value;
                    ClearCachedAvatar();
                }
            }
        }
        private bool checkDowned = false;
        private Color bgColor = new Color(.5f,.5f,.6f,.5f);
        private int lastUpdateTime;
        private bool updateQueued = false;
        public Texture2D staticTexture;
        private DateTime? staticTextureLastModified;
        private int staticTextureLastCheck;
        public virtual void ClearCachedAvatar()
        {
            if (avatar != null)
            {
                // cap the update frequency
                if (Time.frameCount > lastUpdateTime + 5)
                { // destroy old texture
                    UnityEngine.Object.Destroy(avatar);
                    avatar = null;
                    feature = null;
                    updateQueued = false;
                }
                else
                {
                    updateQueued = true;
                }
            }
        }
        public void SetPawn(Pawn pawn)
        {
            if (this.pawn != pawn)
            {
                this.pawn = pawn;
                drawHeadgear = mod.settings.defaultDrawHeadgear;
                ClearCachedAvatar();
                // Also clear the static portrait texture so we load the correct one for this pawn
                if (staticTexture != null)
                {
                    UnityEngine.Object.Destroy(staticTexture);
                    staticTexture = null;
                    staticTextureLastModified = null;
                }
            }
        }
        public void SetBGColor(Color color)
        {
            if (bgColor != color)
            {
                bgColor = color;
                ClearCachedAvatar();
            }
        }
        public void SetCheckDowned(bool checkDowned)
        {
            if (this.checkDowned != checkDowned)
            {
                this.checkDowned = checkDowned;
                ClearCachedAvatar();
            }
        }
        private int Seed()
        {
            #if ANOMALY
            if (pawn.IsDuplicate && Find.PawnDuplicator.duplicates.ContainsKey(pawn.duplicate.duplicateOf))
            {
                Pawn original = Find.PawnDuplicator.duplicates[pawn.duplicate.duplicateOf].First();
                return 2632*original.ageTracker.BirthDayOfYear+3341*original.ageTracker.BirthYear;
            }
            #endif
            return 2632*pawn.ageTracker.BirthDayOfYear+3341*pawn.ageTracker.BirthYear;
        }
        private Feature GetFeature()
        {
            if (feature == null)
            {
                int v = Seed();
                feature = new ((v%450)/90+1, (v%90)/15+1, (v%15)/3+1, (v%3)+1);
            }
            return feature;
        }
        private string GetStardardHead()
        {
            switch ((Seed() % 2700)/450)
            {
                case 1: return "AverageWide";
                case 2: return "AveragePointy";
                case 3: return "NarrowNormal";
                case 4: return "NarrowWide";
                case 5: return "NarrowPointy";
                default: return "AverageNormal";
            }
        }
        private string GetPath<T>(string gender, string lifeStage, string typeName, string fallbackPath) where T: AvatarDef
        {
            string result = null;
            foreach (T def in DefDatabase<T>.AllDefs)
                if (def.typeName == typeName)
                    result = def.GetPath(gender, lifeStage);
            return result ?? fallbackPath;
        }
        private string GetPathByDefName<T>(string gender, string lifeStage, string defName, string fallbackPath) where T: AvatarDef
        {
            return DefDatabase<T>.GetNamedSilentFail(defName)?.GetPath(gender, lifeStage) ?? fallbackPath;
        }
        private T GetApparelDef<T>(Apparel apparel) where T: AvatarApparelDef
        {
            T def = null;
            CompStyleable comp = apparel.GetComp<CompStyleable>();
            if (comp != null && comp.styleDef != null)
                def = DefDatabase<T>.GetNamedSilentFail(comp.styleDef.defName);
            return def ?? DefDatabase<T>.GetNamedSilentFail(apparel.def.defName);
        }
        #if !(v1_3 || v1_4)
        private List<AvatarLayer> HandleRenderNode(List<PawnRenderNodeProperties> props, Hediff hediff, string path, Color skinColor, Color hairColor)
        {
            List<AvatarLayer> layers = new ();
            foreach (PawnRenderNodeProperties prop in props)
            {
                AvatarLayer attachment = new (path);
                if (prop.flipGraphic == true)
                    attachment.flipGraphic = true;
                if (prop.texPaths != null)
                {
                    PawnRenderNode node = new (pawn, prop, null);
                    node.hediff = hediff;
                    string variant = node.TexPathFor(pawn); // should end with A, B, C
                    attachment.texPath += variant[variant.Length-1];
                }
                if (prop.colorType == PawnRenderNodeProperties.AttachmentColorType.Skin)
                    attachment.color = skinColor;
                if (prop.colorType == PawnRenderNodeProperties.AttachmentColorType.Hair)
                    attachment.color = hairColor;
                layers.Add(attachment);
            }
            return layers;
        }
        #endif
        private bool ShouldShowWrinkles()
        {
            float lifeExpectancy = pawn.RaceProps.lifeExpectancy;
            #if BIOTECH
            foreach (Gene gene in pawn.genes.GenesListForReading.Where(g => g.Active))
            {
                if (gene.def.defName == "Ageless")
                    lifeExpectancy = float.PositiveInfinity;
                else if (!gene.def.statFactors.NullOrEmpty())
                {
                    foreach (StatModifier statModifier in gene.def.statFactors)
                    {
                        if (statModifier.stat == StatDefOf.LifespanFactor)
                            lifeExpectancy *= statModifier.value;
                    }
                }
            }
            #endif
            return pawn.ageTracker.AgeBiologicalYears >= 0.7 * lifeExpectancy;
        }
        #if BIOTECH
        private int GeneUseColor(Gene gene)
        {
            #if v1_4
            return gene.def.HasGraphic ? (int) gene.def.graphicData.colorType : -1;
            #else
            return gene.def.renderNodeProperties?.Count >= 1 ? (int) gene.def.renderNodeProperties[0].colorType : -1;
            #endif
        }
        #endif
        private Texture2D RenderAvatar()
        {
            lastUpdateTime = Time.frameCount;
            int width = 40;
            int height = 48;
            int halfWidthHeightDiff = (height-width)/2;
            if (canvas == null)
                canvas = new (width, height);
            TextureUtil.ClearTexture(canvas, bgColor);
            string gender = (pawn.gender == Gender.Female) ? "Female" : "Male";
            string lifeStage = "";
            int yOffset = 0;
            int eyeLevel = 0;
            string raceName = pawn.RaceProps.AnyPawnKind?.race.defName ?? "Human";
            raceName = (raceName == "Human") ? "" : ("_" + raceName);
            if (pawn.ageTracker.CurLifeStage.defName == "HumanlikeBaby"
                || pawn.ageTracker.CurLifeStage.defName == "HumanlikeToddler") // from Toddlers mod
            {
                lifeStage = "Newborn";
                yOffset = 3;
                eyeLevel = -2;
            }
            else if (pawn.ageTracker.CurLifeStage.defName == "HumanlikeChild" || pawn.ageTracker.CurLifeStage.defName == "HumanlikePreTeenager")
            {
                lifeStage = "Child";
                yOffset = 2;
                eyeLevel = -1;
            }
            // babies are always downed, no need to draw them this way unless dead
            bool downed = checkDowned && (lifeStage != "Newborn" && pawn.Downed);
            int downedOffset = 10;
            Color skinColor = pawn.story.SkinColor;
            Color hairColor = pawn.story.hairColor;
            #if ANOMALY
            if (pawn.IsShambler) hairColor = MutantUtility.GetShamblerColor(hairColor);
            #endif
            List<AvatarLayer> layers = new ();
            AvatarLayer coversAll = null;
            #if BIOTECH
            List<Gene> activeGenes = pawn.genes.GenesListForReading.Where(g => g.Active).ToList();
            // collect all cosmetic genes not handled by the defined defs
            List<Gene> cosmeticGenes = activeGenes.Where(g =>
                !DefDatabase<AvatarGeneDef>.AllDefsListForReading.Exists(def => def.geneName == g.def.defName && def.replaceModdedTexture)
                #if v1_4
                && g.def.HasGraphic
                && g.def.graphicData.drawLoc != GeneDrawLoc.Tailbone
                && !g.def.graphicData.drawOnEyes // eye textures cause most issues, easier to just ignore them
                #else
                && g.def.renderNodeProperties?.Count == 1
                && g.def.renderNodeProperties[0].parentTagDef?.defName != "Body"
                #endif
                ).ToList();
            #endif
            #if v1_3
            string headTypeName = pawn.story.HeadGraphicPath.Split('/').Last();
            headTypeName = headTypeName.Remove(headTypeName.LastIndexOf('_'), 1);
            #else
            string headTypeName = pawn.story.headType.defName;
            #endif
            if (headTypeName.StartsWith("Female_"))
                headTypeName = headTypeName.Substring(7);
            if (headTypeName.EndsWith("_Female"))
                headTypeName = headTypeName.Substring(0, headTypeName.Length-7);
            if (headTypeName.StartsWith("Male_"))
                headTypeName = headTypeName.Substring(5);
            if (headTypeName.EndsWith("_Male"))
                headTypeName = headTypeName.Substring(0, headTypeName.Length-5);
            if (pawn.Drawer.renderer.CurRotDrawMode == RotDrawMode.Dessicated)
            {
                headTypeName = "Skeleton";
                skinColor = new (0.8f, 0.7f, 0.6f);
            }
            bool hideTattoo = false;
            bool hideWrinkles = false;
            bool hideEyes = false;
            bool hideEars = false;
            bool hideNose = false;
            bool hideMouth = false;
            bool hideHair = false;
            bool hideBeard = false;
            bool specialNoJaw = false;
            int hairHideTop = 0;
            int headHideTop = 0;
            int headAttachmentOffset = 0;
            string bodyTypeName = "";
            List<EyePos> eyesPos = new List<EyePos> {new EyePos (14,27,15,27), new EyePos (24,27,23,27)};
            AvatarHeadDef headTypeDef = DefDatabase<AvatarHeadDef>.GetNamedSilentFail("Head_" + headTypeName + raceName);
            string facePaint = null;
            Color? facePaintColor = null;
            if (headTypeDef != null)
            {
                hideTattoo = headTypeDef.hideTattoo;
                hideHair = headTypeDef.hideHair;
                hideBeard = headTypeDef.hideBeard;
                hideWrinkles = headTypeDef.hideWrinkles;
                hideEyes = headTypeDef.hideEyes;
                hideEars = headTypeDef.hideEars;
                hideNose = headTypeDef.hideNose;
                hideMouth = headTypeDef.hideMouth;
                bodyTypeName = headTypeDef.forceBodyType;
                specialNoJaw = headTypeDef.specialNoJaw;
                facePaint = headTypeDef.facePaint;
                facePaintColor = headTypeDef.facePaintColor;
                headAttachmentOffset = headTypeDef.headAttachmentOffset;
                if (headTypeDef.eyesPos != null)
                    eyesPos = headTypeDef.eyesPos;
                if (headTypeDef.reassignStandard)
                    headTypeName = GetStardardHead();
            }
            List<(Apparel, AvatarBodygearDef)> bodygears = new ();
            List<(Apparel, AvatarBackgearDef)> backgears = new ();
            List<(Apparel, AvatarFacegearDef)> facegears = new ();
            List<(Apparel, AvatarHeadgearDef)> headgears = new ();
            if (drawClothes)
            {
                foreach (Apparel apparel in pawn.apparel.WornApparel)
                {
                    if (GetApparelDef<AvatarBodygearDef>(apparel) is AvatarBodygearDef bodygearDef)
                        bodygears.Add((apparel, bodygearDef));
                    else if (GetApparelDef<AvatarBackgearDef>(apparel) is AvatarBackgearDef backgearDef)
                        backgears.Add((apparel, backgearDef));
                    else if (GetApparelDef<AvatarFacegearDef>(apparel) is AvatarFacegearDef facegearDef)
                    {
                        if (drawHeadgear)
                        {
                            facegears.Add((apparel, facegearDef));
                            if (mod.settings.showHairWithHeadgear)
                                hideHair |= facegearDef.hideHair;
                            else
                                hideHair |= apparel.def.apparel.bodyPartGroups.Exists(p => p.defName == "UpperHead" || p.defName == "FullHead");
                            hairHideTop = Math.Max(hairHideTop, facegearDef.hideTop);
                            headHideTop = Math.Max(headHideTop, facegearDef.hideTop);
                            hideBeard |= facegearDef.hideBeard;
                        }
                    }
                    else if (GetApparelDef<AvatarHeadgearDef>(apparel) is AvatarHeadgearDef headgearDef)
                    {
                        if (drawHeadgear)
                        {
                            #if v1_3 || v1_4
                            if (apparel.def.apparel.shellCoversHead)
                            #else
                            if (apparel.def.apparel.renderSkipFlags?.FirstOrDefault()?.defName == "Head")
                            #endif
                                coversAll = new (headgearDef.GetPath(gender, lifeStage), apparel.DrawColor, headAttachmentOffset);
                            else
                                headgears.Add((apparel, headgearDef));
                            if (mod.settings.showHairWithHeadgear)
                                hideHair |= headgearDef.hideHair;
                            else
                                hideHair |= apparel.def.apparel.bodyPartGroups.Exists(p => p.defName == "UpperHead" || p.defName == "FullHead");
                            hairHideTop = Math.Max(hairHideTop, headgearDef.hideTop);
                            headHideTop = Math.Max(headHideTop, headgearDef.hideTop);
                            hideBeard |= headgearDef.hideBeard;
                        }
                    }
                    else if (apparel.def.apparel.bodyPartGroups.Exists(p => p.defName == "Torso")
                        && apparel.def.thingCategories != null) // warcaskets don't have this...
                    {
                        if (apparel.def.thingCategories.Exists(p => p.defName == "ApparelArmor"))
                            bodygears.Add((apparel, DefDatabase<AvatarBodygearDef>.GetNamedSilentFail("Avatar_GenericArmor")));
                        else if (!apparel.def.thingCategories.Exists(p => p.defName == "ApparelUtility"))
                            bodygears.Add((apparel, DefDatabase<AvatarBodygearDef>.GetNamedSilentFail("Avatar_Generic")));
                    }
                }
            }
            // sorting
            // Vanilla apparels are already sorted.
            // However the offset can be set manually which changes rendering order.
            #if v1_3 || v1_4
            if (ModCompatibility.VanillaFactionsExpanded_Loaded)
            {
                bodygears = bodygears.OrderBy(a => ModCompatibility.GetVEOffset(a.Item1.def)).ToList();
            }
            #else
            bodygears = bodygears.OrderBy(a => a.Item1.def.apparel?.drawData?.dataSouth?.offset?.y ?? 0f).ToList();
            #endif
            // building layers
            #if BIOTECH
            foreach (Gene gene in activeGenes)
            {
                foreach (AvatarBackDef def in DefDatabase<AvatarBackDef>.AllDefs)
                    if (gene.def.defName == def.geneName)
                        layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage)));
            }
            #endif
            foreach ((Apparel apparel, AvatarBackgearDef def) in backgears)
            {
                layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage), apparel.DrawColor, 8));
            }
            List<AvatarLayer> body_layers = new ();
            string neckPath = GetPathByDefName<AvatarBodyDef>(gender, lifeStage, "Body_" + bodyTypeName, "Core/"+gender+lifeStage+"/Neck");
            // asimov colored robot support
            AvatarLayer neck = new (neckPath, skinColor, 8);
            if (ModCompatibility.Asimov_Loaded)
            {
                if (ModCompatibility.GetAsimovSkinColor(pawn) is (Color skinFirst, Color skinSecond))
                {
                    neck.color = skinFirst;
                    neck.colorB = skinSecond;
                }
            }
            body_layers.Add(neck);
            if (!hideTattoo)
            {
                string bodyTattooPath = GetPath<AvatarBodyTattooDef>(gender, lifeStage, pawn.style.BodyTattoo?.defName, null);
                body_layers.Add(new AvatarLayer(bodyTattooPath, new Color(1f,1f,1f,0.8f), 8));
            }
            #if ANOMALY
            if (pawn.IsShambler && !(headTypeName == "Skeleton") && !mod.settings.noCorpseGore)
            {
                body_layers.Add(new AvatarLayer("Core/Unisex/Corpse/BodyScar" + ((Seed()%125)/25+1).ToString(), skinColor, 8));
            }
            #endif
            if (pawn.Drawer.renderer.CurRotDrawMode == RotDrawMode.Rotting && !mod.settings.noCorpseGore)
            {
                layers.Add(new ("Core/Unisex/Corpse/Skeleton" + lifeStage, new (0.8f, 0.7f, 0.6f), 8));
                foreach (AvatarLayer layer in body_layers)
                {
                    layer.alphaMaskPath = "Core/Unisex/Corpse/BodyMask" + ((Seed()%625)/125+1).ToString();
                }
            }
            foreach (AvatarLayer layer in body_layers)
                layers.Add(layer);
            foreach ((Apparel apparel, AvatarBodygearDef def) in bodygears)
            {
                layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage), apparel.DrawColor, 8));
            }
            foreach (Hediff h in pawn.health.hediffSet.hediffs.Where(h => h.Part != null))
            {
                foreach (AvatarBodyHediffDef def in DefDatabase<AvatarBodyHediffDef>.AllDefs)
                {
                    if (h.def.defName == def.typeName)
                    {
                        AvatarLayer prosthetic = new (def.GetPath(gender, lifeStage));
                        prosthetic.offset = 8;
                        if (h.Part.def.defName == "Shoulder")
                        {
                            prosthetic.color = skinColor;
                            if (h.Part.woundAnchorTag == "LeftShoulder")
                                prosthetic.drawDexter = false;
                            else
                                prosthetic.drawSinister = false;
                            layers.Add(prosthetic);
                        }
                        else
                        {
                            #if v1_3 || v1_4
                            layers.Add(prosthetic);
                            #else
                            if (h.def.renderNodeProperties != null)
                            {
                                foreach (AvatarLayer layer in HandleRenderNode(h.def.renderNodeProperties, h, def.GetPath(gender, lifeStage), skinColor, hairColor))
                                {
                                    layer.offset = 8;
                                    layers.Add(layer);
                                }
                            }
                            else
                                layers.Add(prosthetic);
                            #endif
                        }
                    }
                }
            }
            // draw head
            if (!pawn.health.hediffSet.hediffs.Exists(h => h.def.defName == "MissingBodyPart" && h.Part != null && h.Part.def.defName == "Head"))
            {
                string headPath = GetPathByDefName<AvatarHeadDef>(gender, lifeStage, "Head_" + headTypeName + raceName, "Core/"+gender+lifeStage+"/Head/AverageNormal");
                string faceTattooPath = GetPath<AvatarFaceTattooDef>(gender, lifeStage, pawn.style.FaceTattoo?.defName, null);
                string beardPath = GetPathByDefName<AvatarBeardDef>(gender, lifeStage, "Beard_" + (pawn.style.beardDef?.defName ?? "NoBeard"), "BEARD");
                string hairPath = GetPathByDefName<AvatarHairDef>(gender, lifeStage, "Hair_" + (pawn.story.hairDef?.defName ?? "Bald"), "HAIR");
                string earsPath = "Core/Unisex/Ears/Ears_Human";
                string nosePath = "Core/"+gender+lifeStage+"/Nose/Nose"+GetFeature().nose.ToString();
                string eyesPath = "Core/"+gender+lifeStage+"/Eyes/Eyes"+GetFeature().eyes.ToString();
                string mouthPath = "Core/"+(mod.settings.noFemaleLips ? "Male" : gender)+lifeStage+"/Mouth/Mouth"+GetFeature().mouth.ToString();
                string browsPath = "Core/"+gender+lifeStage+"/Brows/Brows"+GetFeature().brows.ToString();
                (Color, Color) eyeColor = (new Color(.6f,.6f,.6f,1), new Color(.1f,.1f,.1f,1));
                Color earsColor = skinColor;
                Color noseColor = skinColor;
                List<AvatarLayer> head_layers = new ();
                #if BIOTECH
                foreach (Gene gene in activeGenes)
                {
                    foreach (AvatarEarsDef def in DefDatabase<AvatarEarsDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                        {
                            earsPath = def.GetPath(gender, lifeStage);
                            switch (GeneUseColor(gene))
                            {
                                case 0: // custom
                                    earsColor = new (1,1,1,1);
                                    break;
                                case 1: // hair
                                    earsColor = hairColor;
                                    break;
                            }
                        }
                    foreach (AvatarNoseDef def in DefDatabase<AvatarNoseDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                        {
                            nosePath = def.GetPath(gender, lifeStage);
                            switch (GeneUseColor(gene))
                            {
                                case 0: // custom
                                    noseColor = new (1,1,1,1);
                                    break;
                                case 1: // hair
                                    noseColor = hairColor;
                                    break;
                            }
                        }
                    foreach (AvatarMouthDef def in DefDatabase<AvatarMouthDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                            mouthPath = def.GetPath(gender, lifeStage);
                    foreach (AvatarBrowsDef def in DefDatabase<AvatarBrowsDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                            browsPath = def.GetPath(gender, lifeStage);
                    foreach (AvatarEyesDef def in DefDatabase<AvatarEyesDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                        {
                            eyesPath = def.GetPath(gender, lifeStage) ?? eyesPath;
                            eyeColor = (def.color1 ?? eyeColor.Item1, def.color2 ?? eyeColor.Item2);
                            eyesPos = def.eyesPos ?? eyesPos;
                        }
                }
                #endif
                if (pawn.story.traits.allTraits.Exists(t => t.def.defName == "BodyMastery")
                    || pawn.health.hediffSet.hediffs.Exists(t => t.def.defName == "VoidTouched"))
                    eyeColor = (new Color(0.7f, 0.7f, 0.7f), new Color(1f, 1f, 1f));
                AvatarLayer ears = new (earsPath, earsColor);
                AvatarLayer nose = new (nosePath, noseColor);
                #if BIOTECH
                foreach (Gene gene in cosmeticGenes)
                {
                    if (gene.def.endogeneCategory == EndogeneCategory.Ears)
                    {
                        ears = AvatarLayer.FromGene(gene, pawn);
                        cosmeticGenes.Remove(gene);
                        break;
                    }
                    else if (gene.def.endogeneCategory == EndogeneCategory.Nose)
                    {
                        nose = AvatarLayer.FromGene(gene, pawn);
                        cosmeticGenes.Remove(gene);
                        break;
                    }
                }
                #endif
                if (pawn.Drawer.renderer.CurRotDrawMode == RotDrawMode.Rotting
                    #if ANOMALY
                    || pawn.IsShambler
                    #endif
                )
                {
                    // try to make the eyes look more lifeless
                    Color avg = new ((eyeColor.Item1.r + eyeColor.Item2.r*2)/3, (eyeColor.Item1.g + eyeColor.Item2.g*2)/3, (eyeColor.Item1.b + eyeColor.Item2.b*2)/3);
                    eyeColor.Item1 = avg;
                    eyeColor.Item2 = avg;
                }
                AvatarLayer eyes = new (eyesPath, skinColor);
                eyes.eyeColor = eyeColor;
                AvatarLayer mouth = new (mouthPath, skinColor);
                if (mod.settings.noFemaleLips && gender == "Female" && lifeStage != "Newborn") mouth.offset = -1; // shift female lips
                AvatarLayer head = new (headPath, skinColor);
                head.hideTop = headHideTop + headAttachmentOffset;
                // asimov colored robot support
                if (ModCompatibility.Asimov_Loaded)
                {
                    if (ModCompatibility.GetAsimovSkinColor(pawn) is (Color skinFirst, Color skinSecond))
                    {
                        head.color = skinFirst;
                        head.colorB = skinSecond;
                    }
                }
                if (!hideEars && (!mod.settings.earsOnTop || ears.texPath == "Core/Unisex/Ears/Ears_Human")) layers.Add(ears);
                // start to build head layers
                head_layers.Add(head);
                if (!hideWrinkles && !mod.settings.noWrinkles)
                {
                    if (ShouldShowWrinkles())
                        head_layers.Add(new AvatarLayer("Core/Unisex/Facial/Wrinkles", skinColor));
                }
                #if BIOTECH
                foreach (Gene gene in cosmeticGenes)
                {
                    if (gene.def.endogeneCategory == EndogeneCategory.Jaw
                        #if v1_4
                        || gene.def.graphicData.layer == GeneDrawLayer.PostSkin
                        #endif
                        )
                    {
                        head_layers.Add(AvatarLayer.FromGene(gene, pawn));
                        cosmeticGenes.Remove(gene);
                        break;
                    }
                }
                #endif
                if (!hideMouth) head_layers.Add(mouth);
                if (!hideNose) head_layers.Add(nose);
                if (!hideEyes) head_layers.Add(eyes);
                #if BIOTECH
                foreach (AvatarFacialDef def in DefDatabase<AvatarFacialDef>.AllDefs)
                {
                    foreach (Gene gene in activeGenes)
                    {
                        // handle variants
                        if (gene.def.defName == def.geneName)
                        {
                            string path = def.GetPath(gender, lifeStage);
                            #if v1_4
                            if (gene.def.graphicData != null && gene.def.graphicData.graphicPaths != null)
                            {
                                string variant = gene.def.graphicData.GraphicPathFor(pawn); // should end with A, B, C
                            #else
                            if (gene.def.renderNodeProperties?.Count == 1 && gene.def.renderNodeProperties[0].texPaths != null)
                            {
                                PawnRenderNode node = new (pawn, gene.def.renderNodeProperties[0], null);
                                node.gene = gene;
                                string variant = node.TexPathFor(pawn); // should end with A, B, C
                            #endif
                                path += variant[variant.Length-1];
                            }
                            Color color = skinColor;
                            switch (GeneUseColor(gene))
                            {
                                case 0: // custom
                                    color = new (1,1,1,1);
                                    break;
                                case 1: // hair
                                    color = hairColor;
                                    break;
                            }
                            head_layers.Add(new AvatarLayer(path, color));
                        }
                    }
                }
                #endif
                if (!string.IsNullOrEmpty(facePaint))
                {
                    string facePaintPath = GetPathByDefName<AvatarFacePaintDef>(gender, lifeStage, facePaint, null);
                    foreach (AvatarLayer layer in head_layers)
                    {
                        layer.maskPath = facePaintPath;
                        layer.colorB = facePaintColor;
                    }
                }
                if (!hideTattoo)
                    head_layers.Add(new AvatarLayer(faceTattooPath, new Color(1f,1f,1f,0.8f), headAttachmentOffset));
                #if ANOMALY
                if (pawn.IsShambler && !(headTypeName == "Skeleton") && !mod.settings.noCorpseGore)
                {
                    head_layers.Add(new AvatarLayer("Core/Unisex/Corpse/FaceScar" + ((Seed()%25)/5+1).ToString(), skinColor));
                }
                #endif
                if (pawn.Drawer.renderer.CurRotDrawMode == RotDrawMode.Rotting && !mod.settings.noCorpseGore)
                {
                    layers.Add(new ("Core/Unisex/Corpse/Skull" + lifeStage, new (0.8f, 0.7f, 0.6f)));
                    foreach (AvatarLayer layer in head_layers)
                    {
                        layer.alphaMaskPath = "Core/Unisex/Corpse/FaceMask" + ((Seed()%5)+1).ToString();
                    }
                }
                foreach (AvatarLayer layer in head_layers)
                    layers.Add(layer);
                foreach (Hediff h in pawn.health.hediffSet.hediffs.Where(h => h.Part != null))
                {
                    if (h is Hediff_MissingPart _)
                    {
                        if (h.Part.def.defName == "Nose")
                        {
                            nose.texPath = "Core/Unisex/Nose/Missing" + lifeStage;
                        }
                        else if (h.Part.def.defName == "Jaw")
                        {
                            if (specialNoJaw)
                                head.texPath += "NoJaw";
                            else
                                layers.Add(new AvatarLayer("Core/Unisex/Jaw/Missing" + lifeStage, skinColor));
                        }
                        else if (h.Part.def.defName == "Eye")
                        {
                            AvatarLayer missingEyes = new ("Core/Unisex/Eyes/Missing", skinColor);
                            if (h.Part.woundAnchorTag == "LeftEye") // this means it's left...
                                missingEyes.drawDexter = false;
                            else
                                missingEyes.drawSinister = false;
                            layers.Add(missingEyes);
                        }
                        else if (h.Part.def.defName == "Ear")
                        {
                            #if v1_3 || v1_4
                            if (h.Part.customLabel == "left ear")
                            #else
                            if (h.Part.flipGraphic)
                            #endif
                                ears.drawSinister = false;
                            else
                                ears.drawDexter = false;
                        }
                    }
                    else if (h is Hediff_AddedPart _)
                    {
                        foreach (AvatarHeadHediffDef def in DefDatabase<AvatarHeadHediffDef>.AllDefs)
                        {
                            if (h.def.defName == def.typeName)
                            {
                                if (h.Part.def.defName == "Nose")
                                {
                                    nose.texPath = def.GetPath(gender, lifeStage);
                                    nose.color = null;
                                }
                                else
                                {
                                    AvatarLayer prosthetic = new (def.GetPath(gender, lifeStage));
                                    if (h.Part.def.defName == "Eye")
                                    {
                                        if (h.Part.woundAnchorTag == "LeftEye")
                                            prosthetic.drawDexter = false;
                                        else
                                            prosthetic.drawSinister = false;
                                        if (lifeStage != "") prosthetic.offset = -1;
                                        layers.Add(prosthetic);
                                    }
                                    else if (h.Part.def.defName == "Ear")
                                    {
                                        #if v1_3 || v1_4
                                        if (h.Part.customLabel == "left ear")
                                        #else
                                        if (h.Part.flipGraphic)
                                        #endif
                                        {
                                            ears.drawSinister = false;
                                            prosthetic.drawDexter = false;
                                        }
                                        else
                                        {
                                            ears.drawDexter = false;
                                            prosthetic.drawSinister = false;
                                        }
                                        layers.Add(prosthetic);
                                    }
                                    else
                                    {
                                        #if v1_3 || v1_4
                                        layers.Add(prosthetic);
                                        #else
                                        if (h.def.renderNodeProperties != null)
                                            foreach (AvatarLayer layer in HandleRenderNode(h.def.renderNodeProperties, h, def.GetPath(gender, lifeStage), skinColor, hairColor))
                                                layers.Add(layer);
                                        else
                                            layers.Add(prosthetic);
                                        #endif
                                    }
                                }
                            }
                        }
                    }
                    else if (h is Hediff_Injury injury && injury.IsPermanent() && pawn.Drawer.renderer.CurRotDrawMode != RotDrawMode.Dessicated)
                    {
                        string scarName = h.Part.def.defName + "_" + h.def.defName;
                        foreach (AvatarHeadHediffDef def in DefDatabase<AvatarHeadHediffDef>.AllDefs)
                        {
                            if (scarName == def.typeName)
                            {
                                AvatarLayer scar = new (def.GetPath(gender, lifeStage), skinColor, headAttachmentOffset);
                                if (lifeStage != "") scar.offset = -1;
                                if (h.Part.def.defName == "Eye")
                                {
                                    if (h.Part.woundAnchorTag == "LeftEye")
                                        scar.drawDexter = false;
                                    else
                                        scar.drawSinister = false;
                                }
                                layers.Add(scar);
                            }
                        }
                    }
                }
                #if v1_4
                foreach (Gene gene in cosmeticGenes)
                {
                    if (gene.def.graphicData.layer == GeneDrawLayer.PostTattoo)
                    {
                        layers.Add(AvatarLayer.FromGene(gene, pawn));
                        cosmeticGenes.Remove(gene);
                        break;
                    }
                }
                #endif
                AvatarLayer beard = new (beardPath, hairColor, headAttachmentOffset);
                if (beardPath == "BEARD")
                    beard.fallback = new VanillaTexOption(pawn.style.beardDef.texPath + "_south", 8, RecolorOption.Yes);
                AvatarLayer hair = new (hairPath, hairColor, headAttachmentOffset);
                hair.hideTop = hairHideTop + headAttachmentOffset;
                if (hairPath == "HAIR")
                    hair.fallback = new VanillaTexOption(pawn.story.hairDef.texPath + "_south", 4, RecolorOption.Yes, true);
                // gradient hair mod support
                if (ModCompatibility.GradientHair_Loaded)
                {
                    if (ModCompatibility.GetGradientHair(pawn) is (String mask, Color color))
                    {
                        hair.gradientMask = mask;
                        hair.colorB = color;
                    }
                }
                AvatarLayer brows = new (browsPath, hairColor);
                if (!hideBeard && lifeStage != "Newborn")
                    layers.Add(beard);
                if (!hideEyes && lifeStage != "Newborn")
                    layers.Add(brows);
                if (!drawHeadgear)
                {
                    if (!hideHair && lifeStage != "Newborn")
                        layers.Add(hair);
                }
                else
                {
                    // facegear goes under hair
                    foreach ((Apparel apparel, AvatarFacegearDef def) in facegears)
                    {
                        layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage), apparel.DrawColor, headAttachmentOffset + (lifeStage == "" ? 0 : -1)));
                    }
                    // hair and headgear
                    if (!hideHair && lifeStage != "Newborn")
                        layers.Add(hair);
                    if (coversAll == null)
                        foreach ((Apparel apparel, AvatarHeadgearDef def) in headgears)
                        {
                            layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage), apparel.DrawColor, headAttachmentOffset));
                        }
                }
                if (!hideEars && (mod.settings.earsOnTop && ears.texPath != "Core/Unisex/Ears/Ears_Human")) layers.Add(ears);
                #if BIOTECH
                foreach (Gene gene in activeGenes)
                {
                    foreach (AvatarHeadboneDef def in DefDatabase<AvatarHeadboneDef>.AllDefs)
                        if (gene.def.defName == def.geneName)
                        {
                            Color color = new (1,1,1,1);
                            switch (GeneUseColor(gene))
                            {
                                case 1: // hair
                                    color = hairColor;
                                    break;
                                case 2: // skin
                                    color = skinColor;
                                    break;
                            }
                            layers.Add(new AvatarLayer(def.GetPath(gender, lifeStage), color, headAttachmentOffset));
                        }
                }
                // dump all remaining cosmetic genes here
                foreach (Gene gene in cosmeticGenes)
                {
                    AvatarLayer layer = AvatarLayer.FromGene(gene, pawn);
                    layer.offset += headAttachmentOffset;
                    layers.Add(layer);
                }
                #endif
                if (drawHeadgear && coversAll != null)
                    layers.Add(coversAll);
            }
            // end of head drawing


            // render the texture
            foreach (AvatarLayer layer in layers)
            {
                if (layer.texPath != null)
                {
                    Texture2D texture = null;
                    Texture2D mask = null;
                    Texture2D alphaMask = null;
                    if (layer.fallback != null)
                    {
                        // fallback to vanilla texture
                        if (ContentFinder<Texture2D>.Get(layer.fallback.texPath, false) != null)
                            texture = TextureUtil.ProcessVanillaTexture(layer.fallback, (width, height), (62,68));
                    }
                    else
                    {
                        Texture2D unreadableTexture = mod.GetTexture(layer.texPath);
                        // the path is defined in the def so the texture should exist
                        if (unreadableTexture != null)
                            texture = TextureUtil.MakeReadableCopy(unreadableTexture);
                    }
                    if (texture != null)
                    {
                        string maskPath = string.IsNullOrEmpty(layer.maskPath) ? layer.texPath + "m" : layer.maskPath;
                        Texture2D maskTexture = mod.GetTexture(maskPath, false);
                        if (maskTexture != null)
                            mask = TextureUtil.MakeReadableCopy(maskTexture);
                        if (layer.alphaMaskPath != null)
                        {
                            Texture2D alphaMaskTexture = mod.GetTexture(layer.alphaMaskPath, false);
                            if (alphaMaskTexture != null)
                                alphaMask = TextureUtil.MakeReadableCopy(alphaMaskTexture);
                        }
                        if (mod.settings.avatarCompression)
                            texture.Compress(true);

                        // ad hoc stuff for gradient hair
                        if (!string.IsNullOrEmpty(layer.gradientMask))
                        {
                            VanillaTexOption opt = new (layer.gradientMask, 4, RecolorOption.No);
                            mask = TextureUtil.ProcessVanillaTexture(opt, (width, height), (62,68));
                        }

                        for (int y = Math.Max(height-texture.height-layer.offset, 0);
                            y < Math.Min(height-layer.hideTop-yOffset-layer.offset, height); y++)
                        {
                            for (int x = (layer.drawDexter ? 0 : width/2); x < (layer.drawSinister ? width : width/2); x++)
                            {
                                Color oldColor = downed ? canvas.GetPixel(y-halfWidthHeightDiff, x) : canvas.GetPixel(x, y);
                                Color newColor = texture.GetPixel(layer.flipGraphic ? (width-1 - x) : x, y-(height-texture.height-layer.offset)+yOffset);
                                float alpha = newColor.a;
                                if (alphaMask != null)
                                    alpha *= alphaMask.GetPixel(x, y-(height-texture.height-layer.offset)+yOffset).r;
                                if (alpha > 0)
                                {
                                    Color color = new ();
                                    if (layer.color is Color tint)
                                    {
                                        alpha *= tint.a;
                                        if (mask != null)
                                        {
                                            if (layer.colorB is Color tint2)
                                            {
                                                Color maskPixel = mask.GetPixel(x, y-(height-texture.height-layer.offset)+yOffset);
                                                float r = maskPixel.r;
                                                float g = maskPixel.g;
                                                color.r = oldColor.r*(1f-alpha) + newColor.r*(tint.r*r + tint2.r*g + (1-r)*(1-g))*alpha;
                                                color.g = oldColor.g*(1f-alpha) + newColor.g*(tint.g*r + tint2.g*g + (1-r)*(1-g))*alpha;
                                                color.b = oldColor.b*(1f-alpha) + newColor.b*(tint.b*r + tint2.b*g + (1-r)*(1-g))*alpha;
                                                color.a = 1f;
                                            }
                                            else
                                            {
                                                Color maskPixel = mask.GetPixel(x, y-(height-texture.height-layer.offset)+yOffset);
                                                float r = maskPixel.r;
                                                color.r = oldColor.r*(1f-alpha) + newColor.r*(tint.r*r + 1-r)*alpha;
                                                color.g = oldColor.g*(1f-alpha) + newColor.g*(tint.g*r + 1-r)*alpha;
                                                color.b = oldColor.b*(1f-alpha) + newColor.b*(tint.b*r + 1-r)*alpha;
                                                color.a = 1f;
                                            }
                                        }
                                        else
                                        {
                                            color.r = oldColor.r*(1f-alpha) + newColor.r*tint.r*alpha;
                                            color.g = oldColor.g*(1f-alpha) + newColor.g*tint.g*alpha;
                                            color.b = oldColor.b*(1f-alpha) + newColor.b*tint.b*alpha;
                                            color.a = 1f;
                                        }
                                    }
                                    else
                                    {
                                        color.r = oldColor.r*(1f-alpha) + newColor.r*alpha;
                                        color.g = oldColor.g*(1f-alpha) + newColor.g*alpha;
                                        color.b = oldColor.b*(1f-alpha) + newColor.b*alpha;
                                        color.a = 1f;
                                    }
                                    if (downed)
                                    {
                                        if (y >= halfWidthHeightDiff && y < height-halfWidthHeightDiff
                                            && x <= width-downedOffset)
                                            canvas.SetPixel(y-halfWidthHeightDiff, width-x-downedOffset, color);
                                    }
                                    else
                                        canvas.SetPixel(x, y, color);
                                }
                            }
                        }
                        if (layer.eyeColor is (Color, Color) eyeColor)
                        { // draw eye colors manually
                            foreach (EyePos eye in eyesPos)
                            {
                                if (downed)
                                {
                                    foreach (IntVec2 pos1 in eye.pos1)
                                        canvas.SetPixel(pos1.z+eyeLevel-halfWidthHeightDiff, width-pos1.x-downedOffset, eyeColor.Item1);
                                    foreach (IntVec2 pos2 in eye.pos2)
                                        canvas.SetPixel(pos2.z+eyeLevel-halfWidthHeightDiff, width-pos2.x-downedOffset, eyeColor.Item2);
                                }
                                else
                                {
                                    foreach (IntVec2 pos1 in eye.pos1)
                                        canvas.SetPixel(pos1.x, pos1.z+eyeLevel, eyeColor.Item1);
                                    foreach (IntVec2 pos2 in eye.pos2)
                                        canvas.SetPixel(pos2.x, pos2.z+eyeLevel, eyeColor.Item2);
                                }
                            }
                        }
                        if (alphaMask != null) UnityEngine.Object.Destroy(alphaMask);
                        if (mask != null) UnityEngine.Object.Destroy(mask);
                        UnityEngine.Object.Destroy(texture);
                    }
                }
            }
            if (pawn.Dead)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        Color oldColor = canvas.GetPixel(x, y);
                        float gray = (oldColor.r + oldColor.g + oldColor.b-0.1f)/3f;
                        canvas.SetPixel(x, y, new Color(gray,gray,gray*1.2f,oldColor.a));
                    }
            canvas.Apply();
            if (avatar != null)
            { // destroy old texture
                UnityEngine.Object.Destroy(avatar);
            }
            if (mod.settings.avatarScaling)
                avatar = TextureUtil.ScaleX2(canvas);
            else
            {
                avatar = TextureUtil.MakeReadableCopy(canvas);
                avatar.Apply();
            }
            avatar.filterMode = FilterMode.Point;
            return avatar;
        }
        public virtual Texture2D GetAvatar(bool allowStatic = true)
        {
            if (allowStatic)
            {
                if (avatar == null || Time.frameCount > staticTextureLastCheck + 10) // don't check every frame
                    TryGetStaticTexture();
                if (staticTexture != null) return staticTexture;
            }
            if (updateQueued) ClearCachedAvatar();
            return avatar ?? RenderAvatar();
        }
        // GetPawnNameStatic still exists for backward-compat in case any caller
        // needs the legacy display-name path. New code should use
        // GetPortraitFileBase(pawn) for storage and pawn.LabelShortCap for UI.
        public static string GetPawnNameStatic(Pawn pawn)
        {
            if (pawn == null || pawn.Name == null) return pawn?.thingIDNumber.ToString() ?? "unknown";
            string name = pawn.Name.ToStringFull.Replace("'", "").Replace(" ", "_");
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '-');
            return name + "_" + pawn.thingIDNumber.ToString();
        }

        // === Per-world rename-stable portrait file naming ===
        // Portraits live in <persistentDataPath>/avatar/ which is a USER-wide
        // directory shared across every save. Storing files as `<id>.png` (old
        // single-game scheme) caused cross-game contamination: thingIDNumber
        // resets to low values in each new game, so a new pawn with id=500
        // would inherit an OLD pawn id=500's portrait from a previous world.
        // Symptom users notice: "a new pawn spawned with the same name as one
        // from before and got that pawn's portrait".
        //
        // Current scheme: `<worldId>_<thingIDNumber>.png` where worldId is
        // `Find.World.info.persistentRandomValue` (long, stable per world,
        // randomly initialized on world creation). Two different worlds can't
        // collide. Two saves of the same world correctly share portraits.
        //
        // Migration handled in three places:
        //   1) AvatarGameComponent.LoadedGame() bulk-renames very old
        //      `<name>_<id>.png` → `<id>.png` (only useful for users coming
        //      from a pre-id-naming version).
        //   2) GetPortraitPath() lazy per-pawn migration: copies legacy
        //      `<id>.png` to `<world>_<id>.png` on first lookup (COPY not move
        //      — the file might still belong to another world's pawn with the
        //      same id, and we can't know without scanning every world save).
        //   3) Same per-pawn fallback also looks for very old `<name>_<id>.png`
        //      and moves it (since those predate any world-aware storage).
        public static string GetPortraitFileBase(Pawn pawn)
        {
            if (pawn == null) return "unknown";
            long worldId = GetStableWorldId(pawn);
            if (worldId == 0L)
            {
                // Sidecar didn't exist and world unavailable; fall back to legacy
                // scheme. This is rare and temporary — world will be up next tick.
                return pawn.thingIDNumber.ToString();
            }
            return worldId.ToString() + "_" + pawn.thingIDNumber.ToString();
        }
        // Get the stable world ID for a pawn. This NEVER returns 0 — it will
        // cache the world ID in a sidecar file so even if Find.World becomes
        // null, we can still reliably load portraits from the same file.
        // The sidecar is per-pawn and lives in <persistentDataPath>/avatar/.
        // In-memory cache of resolved world IDs, keyed by pawn thingIDNumber.
        // A pawn's world ID is immutable for the session, but GetStableWorldId
        // is called from extremely hot paths (UIPatch.Postfix runs it several
        // times per frame for the selected pawn via GetPortraitPath +
        // TryGetStaticTexture; the 20s safety scan runs it per spawned pawn).
        // Without this cache every one of those calls did a File.Exists +
        // File.ReadAllText of the .worldid sidecar — ~100+ synchronous disk
        // reads/sec for a constant value. ConcurrentDictionary because the
        // background generation thread also resolves portrait paths.
        // We only cache positive resolutions (> 0); a 0 result means the
        // world wasn't available yet, which must stay re-resolvable.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> worldIdCache
            = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
        private static long GetStableWorldId(Pawn pawn)
        {
            if (pawn == null) return 0L;
            int pawnId = pawn.thingIDNumber;
            // Fast path: resolved earlier this session.
            if (worldIdCache.TryGetValue(pawnId, out long memo))
                return memo;
            string dir = System.IO.Path.Combine(Application.persistentDataPath, "avatar");
            string sidecarPath = System.IO.Path.Combine(dir, pawnId + ".worldid");
            
            // Try to read cached world ID from sidecar
            try
            {
                if (System.IO.File.Exists(sidecarPath))
                {
                    string text = System.IO.File.ReadAllText(sidecarPath).Trim();
                    if (long.TryParse(text, out long cached))
                    {
                        worldIdCache[pawnId] = cached;
                        return cached;
                    }
                }
            }
            catch { }
            
            // Sidecar missing or invalid — try to get the live world ID
            long worldId = TryGetWorldId();
            if (worldId > 0L)
            {
                // Write it to the sidecar for next time
                try
                {
                    if (!System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(sidecarPath, worldId.ToString());
                }
                catch { }
                worldIdCache[pawnId] = worldId;
                return worldId;
            }
            
            // World unavailable and no cached sidecar — fall back to legacy <id>.png
            // This is temporary and will be fixed once world is up.
            return 0L;
        }
        // Defensive fetch — Find.World can be null during early load, between
        // saves, or during world generation. We return 0 as the sentinel; the
        // caller treats that as "world unavailable".
        private static long TryGetWorldId()
        {
            try { return Find.World?.info?.persistentRandomValue ?? 0L; }
            catch { return 0L; }
        }
        // Style-scoped portrait directory. RimWorld style keeps the historical
        // root (<persistentDataPath>/avatar) so existing portraits keep working
        // and nothing has to migrate; Ultra-realistic portraits live in a
        // sibling "realistic" subfolder. Every read / write / existence-check
        // routes through here, so switching styles makes the "missing portrait"
        // checks look in the OTHER folder — the previous style's images are never
        // shown, and the auto-generator repaints into the active folder on the
        // next safety scan / inspect-pane click.
        //
        // Deliberately NOT style-scoped (shared root): the ComfyUI install dir,
        // the managed-PID sidecar, piptmp, the per-pawn .worldid sidecars, and
        // the bulk legacy migration — all of those are style-independent.
        public static string GetPortraitDir()
        {
            string root = System.IO.Path.Combine(Application.persistentDataPath, "avatar");
            PortraitStyleSpec spec = CurrentStyleSpec();
            return (spec != null && !string.IsNullOrEmpty(spec.folderSubdir))
                ? System.IO.Path.Combine(root, spec.folderSubdir) : root;
        }
        private static PortraitStyleSpec CurrentStyleSpec()
        {
            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                return m == null ? null : PortraitStyles.Get(m.settings.portraitStyle);
            }
            catch { return null; }
        }
        private static bool CurrentStyleIsRimWorld()
        {
            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                return m == null || m.settings.portraitStyle == PortraitStyle.RimWorld;
            }
            catch { return true; }
        }
        public static string GetPortraitPath(Pawn pawn)
        {
            string dir = GetPortraitDir();
            string newPath = System.IO.Path.Combine(dir, GetPortraitFileBase(pawn) + ".png");
            // Legacy on-disk filename formats only ever existed in the historical
            // root dir (RimWorld style). The realistic subfolder is new, so skip
            // migration there — and skip MARKING this pawn migration-checked,
            // which would otherwise block a later RimWorld-style lookup from
            // migrating its real legacy file (legacyMigrationChecked is keyed by
            // pawn id, not by directory).
            if (CurrentStyleIsRimWorld())
                MigrateLegacyPortraitForPawn(pawn, dir, newPath);
            return newPath;
        }
        // Lazy per-pawn migration. Two formats to handle:
        //   (a) <id>.png — previous single-game scheme. COPY to <world>_<id>.png
        //       so the same source file can serve multiple worlds without each
        //       world stealing it from the others on lookup.
        //   (b) <name>_<id>.png — very old scheme from pre-1.0 of this mod.
        //       Safe to MOVE; nothing else points at these files.
        // Bounded to one scan per pawn per session via a static set.
        private static readonly HashSet<int> legacyMigrationChecked = new HashSet<int>();
        // Match files in the world-namespaced format: <digits>_<digits>.png.
        // We use this to EXCLUDE our own new-format files from the legacy
        // `*_<id>.png` glob — otherwise we'd accidentally move our own
        // freshly-written portraits during migration.
        private static readonly System.Text.RegularExpressions.Regex WorldNamespacedFile =
            new System.Text.RegularExpressions.Regex(@"^\d+_\d+\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static void MigrateLegacyPortraitForPawn(Pawn pawn, string dir, string newPath)
        {
            if (pawn == null) return;
            int id = pawn.thingIDNumber;
            if (legacyMigrationChecked.Contains(id)) return;
            legacyMigrationChecked.Add(id);
            try
            {
                if (System.IO.File.Exists(newPath)) return;
                if (!System.IO.Directory.Exists(dir)) return;
                // (a) Single-game legacy: <id>.png. COPY to the world-namespaced
                // path so other worlds' lookups still find this file too.
                string legacyIdPath = System.IO.Path.Combine(dir, id + ".png");
                if (System.IO.File.Exists(legacyIdPath))
                {
                    try
                    {
                        System.IO.File.Copy(legacyIdPath, newPath);
                        Log.Message("Avatar: migrated single-game portrait " + id + ".png → " + System.IO.Path.GetFileName(newPath) + " for " + pawn.LabelShortCap);
                        return;
                    }
                    catch (Exception e)
                    {
                        Log.Warning("Avatar: <id>.png → <world>_<id>.png copy failed: " + e.Message);
                    }
                }
                // (b) Very old legacy: <anything>_<id>.png. MOVE — these predate
                // world-aware storage. Filter out our OWN <digits>_<digits>.png
                // format which also matches the *_<id>.png glob.
                string suffix = "_" + id + ".png";
                string[] candidates = System.IO.Directory.GetFiles(dir, "*" + suffix);
                foreach (string legacy in candidates)
                {
                    string filename = System.IO.Path.GetFileName(legacy);
                    if (WorldNamespacedFile.IsMatch(filename)) continue;
                    try
                    {
                        System.IO.File.Move(legacy, newPath);
                        Log.Message("Avatar: migrated legacy portrait " + filename + " → " + System.IO.Path.GetFileName(newPath));
                        return;
                    }
                    catch (Exception e)
                    {
                        Log.Warning("Avatar: legacy portrait migration failed for " + legacy + ": " + e.Message);
                    }
                }
            }
            catch (Exception e) { Log.Warning("Avatar: legacy migration scan failed: " + e.Message); }
        }
        // One-shot bulk migration. Called from AvatarGameComponent.LoadedGame.
        // Renames very-old `<name>_<id>.png` files to `<id>.png` (the lazy
        // per-pawn migration then copies those to `<world>_<id>.png` on first
        // lookup). Conflicts (both old and new exist) prefer the newer file
        // by mtime.
        //
        // Skips our world-namespaced `<digits>_<digits>.png` format — those
        // ALSO match the `_<digits>.png` glob, and stripping the world prefix
        // would re-introduce the cross-game contamination this exists to prevent.
        public static void BulkMigrateLegacyPortraits()
        {
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "avatar");
                if (!System.IO.Directory.Exists(dir)) return;
                System.Text.RegularExpressions.Regex re =
                    new System.Text.RegularExpressions.Regex(@"_(\d+)\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                int migrated = 0;
                foreach (string file in System.IO.Directory.GetFiles(dir, "*.png"))
                {
                    string name = System.IO.Path.GetFileName(file);
                    // Skip files already in single-game `<digits>.png` format.
                    if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d+\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        continue;
                    // Skip world-namespaced `<digits>_<digits>.png` — these are
                    // the new format we're trying to MIGRATE TO, not from.
                    if (WorldNamespacedFile.IsMatch(name))
                        continue;
                    var m = re.Match(name);
                    if (!m.Success) continue;
                    string id = m.Groups[1].Value;
                    string target = System.IO.Path.Combine(dir, id + ".png");
                    try
                    {
                        if (System.IO.File.Exists(target))
                        {
                            // Both exist — keep the more recently modified one.
                            DateTime oldMtime = System.IO.File.GetLastWriteTimeUtc(file);
                            DateTime newMtime = System.IO.File.GetLastWriteTimeUtc(target);
                            if (oldMtime > newMtime)
                            {
                                System.IO.File.Delete(target);
                                System.IO.File.Move(file, target);
                                migrated++;
                            }
                            else
                            {
                                System.IO.File.Delete(file); // legacy is stale; drop it
                            }
                        }
                        else
                        {
                            System.IO.File.Move(file, target);
                            migrated++;
                        }
                    }
                    catch (Exception e) { Log.Warning("Avatar: bulk migrate failed for " + name + ": " + e.Message); }
                }
                if (migrated > 0)
                    Log.Message("Avatar: bulk-migrated " + migrated + " legacy portrait file(s) to the new <id>.png scheme.");
            }
            catch (Exception e) { Log.Warning("Avatar: BulkMigrateLegacyPortraits threw: " + e.Message); }
        }

        public string GetPawnName()
        {
            return GetPawnNameStatic(pawn);
        }
        public void TryGetStaticTexture()
        {
            staticTextureLastCheck = Time.frameCount;
            string path = GetPortraitPath(pawn);
            if (System.IO.File.Exists(path))
            {
                if (staticTexture == null)
                    staticTexture = new (1, 1);
                DateTime lastModified = System.IO.File.GetLastWriteTime(path);
                if (lastModified != staticTextureLastModified)
                {
                    try
                    {
                        staticTexture.LoadImage(System.IO.File.ReadAllBytes(path));
                        staticTextureLastModified = lastModified;
                    }
                    catch (System.IO.IOException)
                    {
                        // read failed: probably the image is still being written
                    }
                }
            }
            else if (staticTexture != null)
            {
                staticTextureLastModified = null;
                UnityEngine.Object.Destroy(staticTexture);
                staticTexture = null;
            }
        }
        private static void SavePng(string filename, Texture2D texture)
        {
            string dir = GetPortraitDir();
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            TextureUtil.SavePng(System.IO.Path.Combine(dir, filename), texture);
        }
        public void SaveAsPng()
        {
            SavePng("avatar-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") + ".png", avatar);
        }
        public void UpscaleSaveAsPng()
        {
            Texture2D upscaled = TextureUtil.MakeReadableCopy(avatar, 480, 576);
            SavePng("avatar-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") + "-upscaled.png", upscaled);
            UnityEngine.Object.Destroy(upscaled);
        }
        public void EnableStatic()
        {
            string dir = GetPortraitDir();
            string fileBase = GetPortraitFileBase(pawn);
            string path = System.IO.Path.Combine(dir, fileBase + ".png");
            string backup = System.IO.Path.Combine(dir, fileBase + "-backup.png");
            if (System.IO.File.Exists(backup))
            {
                System.IO.File.Move(backup, path);
            }
            else
            {
                Texture2D upscaled = TextureUtil.MakeReadableCopy(avatar, 480, 576);
                SavePng(fileBase + ".png", upscaled);
                UnityEngine.Object.Destroy(upscaled);
            }
            TryGetStaticTexture();
        }
        public void DisableStatic()
        {
            string dir = GetPortraitDir();
            string fileBase = GetPortraitFileBase(pawn);
            string path = System.IO.Path.Combine(dir, fileBase + ".png");
            if (System.IO.File.Exists(path))
            {
                string backup = System.IO.Path.Combine(dir, fileBase + "-backup.png");
                if (System.IO.File.Exists(backup))
                    System.IO.File.Delete(backup);
                System.IO.File.Move(path, backup);
            }
        }
        public string GetPrompts()
        {
            string prompts_joind = mod.settings.aiGenPreamble
                .Replace("{age}", pawn.ageTracker.AgeBiologicalYears.ToString())
                .Replace("{gender}", (pawn.gender == Gender.Female) ? "female" : "male")
                .Replace("{race}", XenotypeDescriptionGenerator.GetRaceDescription(pawn))
                .Replace("{lifestage}", pawn.ageTracker.CurLifeStage.defName.Substring(9).ToLower());
            HashSet<string> prompts = new ();
            if (ShouldShowWrinkles())
                prompts.Add(DefDatabase<AIGenPromptDef>.GetNamedSilentFail("Wrinkles")?.prompt ?? "");
            HashSet<string> overridden = new ();
            {
                AIGenPromptDef def = DefDatabase<AIGenPromptDef>.GetNamedSilentFail(pawn.story.hairDef.defName);
                if (def != null)
                {
                    prompts.Add(def.prompt);
                }
            }
            if (pawn.style.beardDef?.defName != "NoBeard")
            {
                AIGenPromptDef def = DefDatabase<AIGenPromptDef>.GetNamedSilentFail(pawn.style.beardDef.defName);
                prompts.Add(def?.prompt ?? "beard");
            }
            if (pawn.style.faceTattoo?.defName != "NoTattoo_Face")
            {
                AIGenPromptDef def = DefDatabase<AIGenPromptDef>.GetNamedSilentFail(pawn.style.faceTattoo.defName);
                prompts.Add(def?.prompt ?? "facial tattoo");
            }
            if (pawn.style.bodyTattoo?.defName != "NoTattoo_Body")
            {
                AIGenPromptDef def = DefDatabase<AIGenPromptDef>.GetNamedSilentFail(pawn.style.bodyTattoo.defName);
                prompts.Add(def?.prompt ?? "body tattoo");
            }
            #if BIOTECH
            foreach (Gene gene in pawn.genes.GenesListForReading.Where(g => g.Active))
            {
                AIGenPromptDef def = DefDatabase<AIGenPromptDef>.GetNamedSilentFail(gene.def.defName);
                if (def != null)
                {
                    prompts.Add(def.prompt);
                    foreach (string p in def.overrides.Split(',').Select(p => p.Trim()))
                    {
                        overridden.Add(p);
                    }
                }
            }
            #endif
            if (drawClothes)
            {
                foreach (Apparel apparel in pawn.apparel.WornApparel)
                {
                    if (apparel.def.apparel.layers.Exists(p => p.defName == "Belt"))
                        continue;
                    if (!apparel.def.apparel.layers.Exists(p => p.defName == "Overhead" || p.defName == "EyeCover")
                        || drawHeadgear)
                    {
                        AIGenPromptDef def = DefDatabase<AIGenPromptDef>.GetNamedSilentFail(apparel.def.defName);
                        if (def != null)
                        {
                            prompts.Add(def.prompt);
                            foreach (string p in def.overrides.Split(',').Select(p => p.Trim()))
                            {
                                overridden.Add(p);
                            }
                        }
                        else
                            prompts.Add(apparel.def.label);
                    }
                }
            }
            foreach (string p in overridden)
                prompts.Remove(p);
            prompts_joind += string.Join(", ", prompts);
            return prompts_joind;
        }
        public void OpenPromptsWindow()
        {
            Find.WindowStack.Add(new Prompts_Window(pawn, drawHeadgear, drawClothes));
        }

        public void GeneratePortraitImmediate()
        {
            string prompts = GetPrompts();
            if (string.IsNullOrEmpty(prompts))
            {
                Messages.Message("Prompts cannot be empty", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            // Realistic style requires the RealVisXL model; bail with a clear
            // toast (SelectedStyleBackendReady messages it) instead of launching
            // a 30-90s job ComfyUI will reject for a missing checkpoint.
            if (!AIGen.SelectedStyleBackendReady()) return;
            // Capture pawn fields up front — the .Exited handler runs on a
            // ThreadPool thread, after potentially long delays. Don't deref
            // `pawn` from inside the closure (could be destroyed by then).
            Pawn capturedPawn = pawn;
            int pawnId = capturedPawn.thingIDNumber;
            string pawnLabel = capturedPawn.LabelShortCap;
            string expectedPath = AvatarManager.GetPortraitPath(capturedPawn);
            DateTime startedUtc = DateTime.UtcNow;
            try
            {
                System.Diagnostics.Process process = AIGen.NewProcess(SaveToStaticPortrait(), prompts);
                // Spinner integration: mark pending BEFORE the .Start so the
                // overlay shows immediately. Right-click → Regenerate used to
                // launch silently with no visual feedback for 30-90s.
                AvatarMod.MarkPending(pawnId);
                AvatarMod.ClearFailedAttempts(pawnId);
                process.Exited += (s, ev) =>
                {
                    try
                    {
                        var proc = (System.Diagnostics.Process)s;
                        bool succeeded = proc.ExitCode == 0
                            && System.IO.File.Exists(expectedPath);
                        if (succeeded)
                        {
                            double elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
                            AIGen.RecordGenerationSuccess(pawnLabel, elapsed);
                            // Mark autoGen so the safety scan treats this pawn
                            // as resolved (otherwise it might re-enqueue them
                            // a few seconds later when the file finishes flushing).
                            AvatarMod.MarkAutoGen(pawnId);
                        }
                        else
                        {
                            // Manual regen failed — record the attempt but don't
                            // give up; the user explicitly asked, so the safety
                            // scan should still retry within the budget.
                            AvatarMod.RecordFailedAttempt(pawnId);
                            AvatarMod.UnmarkAutoGen(pawnId);
                        }
                        AvatarMod.UnmarkPending(pawnId);
                    }
                    catch { /* never throw out of an event handler */ }
                };
                process.Start();
                Messages.Message("AI-gen process launched", MessageTypeDefOf.TaskCompletion, historical: false);
                process.BeginErrorReadLine();
            }
            catch (System.InvalidOperationException e)
            {
                // NewProcess threw before we could MarkPending — nothing to clean up.
                Messages.Message(e.Message, MessageTypeDefOf.RejectInput, historical: false);
                if (e.Message.Contains("ComfyUI"))
                    AIGen.NotifyComfyUIMissing();
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                // Process.Start threw — MarkPending already ran, undo it.
                AvatarMod.UnmarkPending(pawnId);
                Messages.Message("AI-gen process failed to start", MessageTypeDefOf.RejectInput, historical: false);
                Log.Error("AI-gen process failed to start:\n  " + e.Message);
            }
        }

        public void GeneratePortraitImmediateSilent()
        {
            string prompts = GetPrompts();
            if (string.IsNullOrEmpty(prompts)) return;
            try
            {
                System.Diagnostics.Process process = AIGen.NewProcess(SaveToStaticPortrait(), prompts);
                process.Start();
                process.BeginErrorReadLine();
            }
            catch (System.InvalidOperationException e)
            {
                Log.Warning("Avatar: Silent generation failed: " + e.Message);
                if (e.Message.Contains("ComfyUI"))
                    AIGen.NotifyComfyUIMissing();
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Log.Warning("Avatar: Silent generation failed to start: " + e.Message);
                if (e.Message.Contains("ComfyUI"))
                    AIGen.NotifyComfyUIMissing();
            }
        }
        public string SaveToStaticPortrait()
        {
            string dir = GetPortraitDir();
            string fileBase = GetPortraitFileBase(pawn);
            string path = System.IO.Path.Combine(dir, fileBase + ".png");
            Texture2D upscaled = TextureUtil.MakeReadableCopy(GetAvatar(false), 480, 576);
            SavePng(fileBase + ".png", upscaled);
            UnityEngine.Object.Destroy(upscaled);
            ClearCachedAvatar();
            return path;
        }
        public void ToggleAvatarVisibility()
        {
            if (AvatarMod.hiddenPawns.Contains(pawn.thingIDNumber))
            {
                AvatarMod.hiddenPawns.Remove(pawn.thingIDNumber);
                Messages.Message("Avatar shown", MessageTypeDefOf.TaskCompletion, historical: false);
            }
            else
            {
                AvatarMod.hiddenPawns.Add(pawn.thingIDNumber);
                Messages.Message("Avatar hidden", MessageTypeDefOf.TaskCompletion, historical: false);
            }
        }

        public FloatMenu GetFloatMenu()
        {
            List<FloatMenuOption> options = new ();
            bool isHidden = AvatarMod.hiddenPawns.Contains(pawn.thingIDNumber);
            if (staticTexture == null)
            {
                options.Add(new ("Generate portrait", GeneratePortraitImmediate));
            }
            else
            {
                options.Add(new ("Regenerate portrait", GeneratePortraitImmediate));
            }
            // Manual prompt-editor variant: opens Prompts_Window so the user
            // can tweak the auto-generated prompt before kicking off the run.
            options.Add(new ("Regenerate portrait (Adjust prompt)", OpenPromptsWindow));
            options.Add(new (isHidden ? "Show this image" : "Hide this image", ToggleAvatarVisibility));
            return new FloatMenu(options);
        }
        public bool CheckCursor(Vector2 pos)
        {
            Texture2D displayed = GetAvatar();
            int x = (int) (pos.x * (float) displayed.width);
            int y = (int) (pos.y * (float) displayed.height);
            return displayed.GetPixel(x, y).a > 0;
        }
    }

    public static class AIGen
    {
        private static string cachedPortable = null;
        private static string scriptPath = null;
        private static bool? depsChecked = null;
        private static volatile bool depsCheckInProgress = false;
        private static volatile bool depsInstallInProgress = false;

        public static bool DepsInstalled => depsChecked == true;
        public static bool DepsChecked => depsChecked.HasValue;
        public static bool DepsBusy => depsCheckInProgress || depsInstallInProgress;
        private static bool comfyUINotified = false;
        private static bool comfyLaunchAttempted = false;

        // === ComfyUI readiness cache + lifecycle ownership ===
        // The queue gate (AutoPortraitGenerator.MapComponentTick) calls
        // TryConfirmComfyReadyForQueue() once per ~3s while ComfyUI is starting.
        // Once /system_stats returns 200 OK we record the timestamp and let the
        // queue drain freely. The cache self-decays after AliveCacheSeconds so a
        // ComfyUI that dies mid-session re-gates the queue.
        private static DateTime lastConfirmedAlive = DateTime.MinValue;
        private static volatile bool preflightInFlight = false;
        private static DateTime nextPreflightAllowed = DateTime.MinValue;
        private const double AliveCacheSeconds = 30.0;
        private const double PreflightCooldownSeconds = 3.0;
        // PID of the ComfyUI .bat we launched ourselves. 0 = we did not launch it
        // (either it was already running when we first probed, or the user started
        // it manually). Only set in LaunchComfyUI when CheckComfyUIAlive was false
        // immediately before the launch, so we never kill someone else's process.
        private static int managedComfyPid = 0;
        // Set true once we've subscribed Application.quitting → KillManagedComfyUI.
        // HarmonyInit hooks this on game load.
        private static bool quitHookInstalled = false;

        // Test-generate gate: serialize so the user can't fire 5 of them in a row.
        private static volatile bool testGenInProgress = false;
        public static bool TestGenInProgress => testGenInProgress;

        // Warm-up gate: one throwaway generation per session to preload the
        // SDXL checkpoint into VRAM (and download InSPyReNet weights) before
        // the first real portrait is requested. See PrewarmComfyUI.
        private static volatile bool warmupDone = false;
        private static volatile bool warmupInProgress = false;
        private const string WarmupPrompt = "front portrait, 30-year-old human, adult, brown hair, brown eyes, simple gray shirt";

        // === Auto-detect (background-threaded so the UI doesn't stall) ===
        private static volatile bool autoDetectInProgress = false;
        public static bool AutoDetectInProgress => autoDetectInProgress;

        // === Cached model-presence check (refreshed by the settings button) ===
        public static string LastModelCheckResult = null;
        public static bool LastModelCheckOk = false;

        // === Last-generation telemetry for the status panel ===
        // Updated from any code path that produces a portrait — auto-gen's
        // Exited handler and manual right-click → Regenerate — so the user
        // can see what the last successful gen looked like at a glance.
        public static string LastGenerationLog = null;
        public static int GenerationsThisSession = 0;
        private static double totalGenerationSeconds = 0.0;
        public static double AverageGenerationSeconds =>
            GenerationsThisSession == 0 ? 0.0 : totalGenerationSeconds / GenerationsThisSession;
        public static void RecordGenerationSuccess(string pawnLabel, double elapsedSeconds)
        {
            GenerationsThisSession++;
            totalGenerationSeconds += elapsedSeconds;
            LastGenerationLog = string.Format("{0:F1}s for {1}", elapsedSeconds, pawnLabel);
        }

        public static bool ComfyLaunchAttempted => comfyLaunchAttempted;

        public static bool IsConfirmedReady =>
            (DateTime.UtcNow - lastConfirmedAlive).TotalSeconds < AliveCacheSeconds;

        // Public-friendly alias: same behavior as TryConfirmComfyReadyForQueue,
        // but the name makes more sense when called from non-queue paths (e.g.,
        // the settings panel needs to keep the readiness cache fresh while
        // the mod-options window is open).
        public static bool TryConfirmComfyReady() => TryConfirmComfyReadyForQueue();

        public static void ResetPortableCache()
        {
            cachedPortable = null;
            depsChecked = null;
            depsCheckInProgress = false;
            depsInstallInProgress = false;
            comfyLaunchAttempted = false;
            lastConfirmedAlive = DateTime.MinValue;
            preflightInFlight = false;
            nextPreflightAllowed = DateTime.MinValue;
            LastModelCheckResult = null;
            LastModelCheckOk = false;
            inspyrenetNodeInstalled = null;
            inspyrenetProbeState = 0;
            inspyrenetProbeInFlight = false;
            inspyrenetNextProbeUtc = DateTime.MinValue;
            // Portable folder changed → any prior warm-up no longer applies; the
            // new server's checkpoint will be cold, so allow a fresh warm-up.
            warmupDone = false;
            warmupInProgress = false;
        }

        // Synchronously adopt a portable folder as the active one by writing the
        // in-memory cache. Lets GetPortablePath / GetEmbeddedPython resolve to it
        // immediately on ANY thread, independent of the (async, main-thread)
        // ModSettings persist. The setup pipeline calls this right after it
        // downloads/detects a portable so the deps/node stages don't have to wait
        // on a settings write that may be queued behind the loading screen.
        public static void AdoptPortablePath(string path)
        {
            cachedPortable = path;
        }

        // Opens the avatar folder in Explorer (or the OS default file manager).
        // The avatar/ subfolder is created if missing so the launch never lands
        // on an Explorer error dialog.
        public static void OpenAvatarFolder()
        {
            try
            {
                string dir = AvatarManager.GetPortraitDir();
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
                Messages.Message("Opened avatar folder: " + dir, MessageTypeDefOf.TaskCompletion, historical: false);
            }
            catch (Exception e)
            {
                Messages.Message("Could not open avatar folder: " + e.Message, MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        // Kicks off auto-detect on a background thread so the main UI doesn't
        // freeze for 5-15 seconds while we walk every fixed drive root +
        // one level deep. The optional onDetected callback fires on the MAIN
        // thread (via LongEventHandler.ExecuteWhenFinished) with the found
        // path, so the settings panel can write it into `comfyPortablePath`
        // safely. Idempotent — concurrent clicks are silently coalesced.
        public static void StartAutoDetectAsync(Action<string> onDetected = null)
        {
            if (autoDetectInProgress) return;
            autoDetectInProgress = true;
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                string detected = null;
                try { detected = AutoDetectPortable(); }
                catch (Exception e) { Log.Warning("Avatar: AutoDetectPortable threw: " + e.Message); }
                finally { autoDetectInProgress = false; }

                string captured = detected;
                // Bounce back to the main thread for the Messages.Message call
                // (which is not thread-safe) AND for the callback that writes
                // into ModSettings (settings writes from a background thread
                // can race with the settings window's render pass).
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    try { onDetected?.Invoke(captured); } catch { }
                    if (!string.IsNullOrEmpty(captured))
                        Messages.Message("Detected ComfyUI portable: " + captured, MessageTypeDefOf.TaskCompletion, historical: false);
                    else
                        Messages.Message("Could not auto-detect ComfyUI portable. Use Browse to pick the folder manually.", MessageTypeDefOf.RejectInput, historical: false);
                });
            });
            t.IsBackground = true;
            t.Name = "AvatarAutoDetect";
            t.Start();
        }

        // Subscribe ourselves to Application.quitting so any ComfyUI we launched
        // dies with RimWorld. Idempotent — safe to call multiple times. Wired
        // from HarmonyInit's static ctor so it runs once on game load.
        public static void InstallQuitHook()
        {
            if (quitHookInstalled) return;
            quitHookInstalled = true;
            try
            {
                UnityEngine.Application.quitting += KillManagedComfyUI;
                Log.Message("Avatar: Application.quitting hook installed for ComfyUI cleanup.");
            }
            catch (Exception e) { Log.Warning("Avatar: InstallQuitHook failed: " + e.Message); }
        }

        // === PID sidecar persistence (review #9) ===
        // Application.quitting only fires on a clean exit. If the game crashes,
        // is killed via Task Manager, or the OS goes down, our managed ComfyUI
        // python.exe survives with full VRAM held. We persist the managed PID
        // to a tiny sidecar file at <persistentDataPath>/avatar/_managed_comfy.pid
        // whenever it changes; on the next game load, RecoverOrphanedComfyUI
        // reads the sidecar, verifies the PID still corresponds to a cmd.exe
        // process, and adopts it so the existing quit hook can clean it up.
        private static string PidSidecarPath
        {
            get
            {
                return System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "avatar", "_managed_comfy.pid");
            }
        }
        private static void WritePidSidecar(int pid)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(PidSidecarPath);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(PidSidecarPath, pid.ToString());
            }
            catch (Exception e) { Log.Warning("Avatar: WritePidSidecar failed: " + e.Message); }
        }
        private static void DeletePidSidecar()
        {
            try { if (System.IO.File.Exists(PidSidecarPath)) System.IO.File.Delete(PidSidecarPath); }
            catch { }
        }
        public static void RecoverOrphanedComfyUI()
        {
            try
            {
                if (!System.IO.File.Exists(PidSidecarPath)) return;
                string s = System.IO.File.ReadAllText(PidSidecarPath).Trim();
                if (!int.TryParse(s, out int pid) || pid <= 0) { DeletePidSidecar(); return; }
                try
                {
                    using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid))
                    {
                        if (p == null || p.HasExited) { DeletePidSidecar(); return; }
                        // Verify it's a cmd.exe (what LaunchComfyUI starts).
                        // If the PID got recycled to some unrelated process, we'd
                        // kill the wrong thing on quit — skip adoption in that case.
                        string procName = null;
                        try { procName = p.ProcessName; } catch { }
                        if (!string.Equals(procName, "cmd", StringComparison.OrdinalIgnoreCase))
                        {
                            DeletePidSidecar();
                            return;
                        }
                        managedComfyPid = pid;
                        // Don't reset comfyLaunchAttempted — the queue gate will
                        // re-probe /system_stats and confirm liveness organically.
                        Log.Message("Avatar: adopted orphaned ComfyUI process from previous session (PID " + pid + "). It will be cleaned up on quit.");
                    }
                }
                catch (ArgumentException) { DeletePidSidecar(); } // PID is gone
            }
            catch (Exception e) { Log.Warning("Avatar: RecoverOrphanedComfyUI failed: " + e.Message); }
        }

        // Non-blocking queue gate. Returns true if we've confirmed ComfyUI is
        // alive within the last 30s. Otherwise kicks off a background HTTP probe
        // (throttled to one per 3s) and, if the probe finds it down, also kicks
        // off auto-launch — so the user's tick-loop never blocks on the 2.5s HTTP
        // timeout during cold start. Used only by AutoPortraitGenerator's queue
        // gate; user-clicked actions (GeneratePortraitImmediate, RunTestGeneration)
        // still use the synchronous CheckComfyUIAlive because blocking briefly on
        // a user click is fine.
        public static bool TryConfirmComfyReadyForQueue()
        {
            if (IsConfirmedReady) return true;
            if (preflightInFlight) return false;
            if (DateTime.UtcNow < nextPreflightAllowed) return false;
            preflightInFlight = true;
            nextPreflightAllowed = DateTime.UtcNow.AddSeconds(PreflightCooldownSeconds);
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                try
                {
                    if (CheckComfyUIAlive())
                    {
                        // 2-second grace period: ComfyUI may respond to /system_stats
                        // before it's fully ready to accept generation jobs. Hold the
                        // queue for 2s after the first successful probe to avoid failed
                        // early generations.
                        System.Threading.Thread.Sleep(2000);
                        lastConfirmedAlive = DateTime.UtcNow;
                        comfyLaunchAttempted = false; // ready again — next downtime can re-launch
                        return;
                    }
                    // Not alive. Try auto-launch (only fires once per session via comfyLaunchAttempted).
                    bool autoLaunch = false;
                    try
                    {
                        AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                        autoLaunch = m != null && m.settings.autoLaunchComfyUI;
                    }
                    catch { }
                    if (autoLaunch && !comfyLaunchAttempted)
                        LaunchComfyUI();
                }
                catch { }
                finally { preflightInFlight = false; }
            });
            t.IsBackground = true;
            t.Name = "AvatarComfyPreflight";
            t.Start();
            return false;
        }

        // Returns true once Python deps (pillow, requests) are confirmed installed in the
        // embedded interpreter. On the first call where they aren't yet confirmed, kicks off
        // a background thread that runs the import test and — if it fails — follows up with
        // a pip install. Until that thread finishes, this method keeps returning false. The
        // tick loop and NewProcess both gate on this, so portrait generation pauses briefly
        // once on first ever use, then proceeds normally.
        //
        // NOTE: rembg + onnxruntime used to be installed here too — they were used by the
        // old CPU-side background removal that has since moved entirely into the ComfyUI
        // workflow (via ComfyUI-RMBG / InSPyReNet). Dropping them saves every fresh user
        // ~80MB download and ~60s of install time (review #7).
        public static bool EnsurePythonDepsInstalled()
        {
            if (depsChecked == true) return true;
            if (depsCheckInProgress || depsInstallInProgress) return false;
            if (string.IsNullOrEmpty(GetEmbeddedPython())) return false;

            depsCheckInProgress = true;
            try
            {
                // Surface once to the user. Safe to call from main thread (callers are MapComponentTick / NewProcess).
                Messages.Message(
                    "Avatar - Personas: checking Python dependencies for portrait generation...",
                    MessageTypeDefOf.NeutralEvent, historical: false);
            }
            catch { }

            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                try
                {
                    bool present = CheckPythonDeps();
                    if (present)
                    {
                        Log.Message("Avatar: Python dependencies already installed in embedded Python.");
                        return;
                    }
                    depsInstallInProgress = true;
                    Log.Message("Avatar: installing Python dependencies (pillow, requests) into embedded Python.");
                    bool ok = InstallPythonDeps();
                    if (ok)
                        Log.Message("Avatar: Python dependencies installed successfully. Portrait generation will resume.");
                    else
                        Log.Warning("Avatar: pip install failed. Run manually: \"" + GetEmbeddedPython() + "\" -m pip install pillow requests");
                }
                catch (Exception e)
                {
                    Log.Warning("Avatar: background dependency install threw: " + e.Message);
                }
                finally
                {
                    depsCheckInProgress = false;
                    depsInstallInProgress = false;
                }
            });
            t.IsBackground = true;
            t.Name = "AvatarPythonDepsInstall";
            t.Start();
            return false;
        }

        // ComfyUI portable has historically shipped its interpreter in
        // "python_embeded" (upstream's typo, single d). If upstream ever fixes
        // the spelling — or the user has a repackaged portable that already did —
        // every hardcoded path breaks: validation fails, the bootstrap
        // re-downloads 2.5 GB it doesn't need, and deps never install. Accept
        // BOTH spellings everywhere via this single resolver.
        public static string GetEmbeddedPythonDir(string portable)
        {
            if (string.IsNullOrEmpty(portable)) return null;
            try
            {
                string a = System.IO.Path.Combine(portable, "python_embeded");
                if (System.IO.File.Exists(System.IO.Path.Combine(a, "python.exe"))) return a;
                string b = System.IO.Path.Combine(portable, "python_embedded");
                if (System.IO.File.Exists(System.IO.Path.Combine(b, "python.exe"))) return b;
            }
            catch { }
            return null;
        }

        public static bool IsValidPortableFolder(string path)
        {
            return GetEmbeddedPythonDir(path) != null;
        }

        // Returns the configured portable folder if valid, else attempts auto-detect.
        // Returns null if no valid folder is found.
        public static string GetPortablePath()
        {
            if (!string.IsNullOrEmpty(cachedPortable) && IsValidPortableFolder(cachedPortable))
                return cachedPortable;

            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                if (m != null && !string.IsNullOrEmpty(m.settings.comfyPortablePath))
                {
                    string configured = m.settings.comfyPortablePath.Trim();
                    if (IsValidPortableFolder(configured))
                    {
                        cachedPortable = configured;
                        return cachedPortable;
                    }
                }
            }
            catch { }

            return null;
        }

        public static string GetEmbeddedPython()
        {
            string dir = GetEmbeddedPythonDir(GetPortablePath());
            if (string.IsNullOrEmpty(dir)) return null;
            return System.IO.Path.Combine(dir, "python.exe");
        }

        // RimWorld is launched by Unity/Steam with a TMP/TEMP environment the
        // embedded Python can't write to — child pip processes inherit it and die
        // before downloading anything with:
        //   FileNotFoundError: [Errno 2] No usable temporary directory found in [...]
        // We sidestep Unity's broken inherited env entirely by pointing every pip
        // subprocess at a temp dir we create under the (definitely-writable)
        // python_embeded folder, and giving pip its own cache dir there too.
        // Call AFTER setting psi.UseShellExecute = false (required to mutate env).
        private static void ApplyPipEnv(System.Diagnostics.ProcessStartInfo psi, string python)
        {
            // The temp dir MUST end up pointed at a writable location or pip dies
            // before doing anything with:
            //   FileNotFoundError: [Errno 2] No usable temporary directory found in [...]
            // (Unity launches RimWorld with a TMP/TEMP the embedded Python can't
            // use.) The old code created python_embeded\avatar_tmp inside a try and,
            // if that creation FAILED (ComfyUI under Program Files / a read-only
            // path), left the env vars UNSET — so pip inherited Unity's broken env
            // and crashed. That was the #1 real-world install failure. Now we try a
            // chain of candidate dirs and always set the env to the first writable
            // one; only if EVERY candidate fails do we leave the env alone.
            string tmp = ResolveWritableTempDir(python);
            if (string.IsNullOrEmpty(tmp))
            {
                Log.Warning("Avatar: could not create ANY writable pip temp dir — pip may fail with 'No usable temporary directory found'.");
                return;
            }
            psi.EnvironmentVariables["TMP"] = tmp;
            psi.EnvironmentVariables["TEMP"] = tmp;
            psi.EnvironmentVariables["TMPDIR"] = tmp;
            psi.EnvironmentVariables["PIP_CACHE_DIR"] = System.IO.Path.Combine(tmp, "pipcache");
        }

        // Returns the first temp dir we can actually create + write a probe file
        // into, trying (1) the embedded python folder, (2) the always-writable
        // RimWorld data folder, (3) the OS temp. Returns null only if all fail.
        private static string ResolveWritableTempDir(string python)
        {
            var candidates = new System.Collections.Generic.List<string>();
            try { candidates.Add(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(python), "avatar_tmp")); } catch { }
            try { candidates.Add(System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "avatar", "piptmp")); } catch { }
            try { candidates.Add(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "avatar_piptmp")); } catch { }
            foreach (string dir in candidates)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                    string probe = System.IO.Path.Combine(dir, ".write_probe_" + Guid.NewGuid().ToString("N"));
                    System.IO.File.WriteAllText(probe, "ok");
                    try { System.IO.File.Delete(probe); } catch { }
                    return dir;
                }
                catch { /* try next candidate */ }
            }
            return null;
        }

        // Scans common Windows locations for a folder that looks like
        // ComfyUI_windows_portable. Returns the first match or null.
        // Folder names we never descend into (huge / system / irrelevant) so a
        // detection pass can't accidentally walk an entire Windows install.
        private static readonly string[] DetectSkipNames = {
            "windows", "$recycle.bin", "system volume information", "winsxs",
            "node_modules", ".git", "__pycache__", "appdata\\locallow",
            "programdata\\microsoft", "perflogs", "recovery", "msocache"
        };
        // Folder-name fragments that mark a branch as worth recursing into past
        // the first level. Keeps the search bounded: every immediate child of a
        // seed root is checked, but we only go deeper down AI/ComfyUI-ish paths.
        private static readonly string[] DetectRelevantFragments = {
            "comfy", "portable", "stable", "diffus", "automatic", "webui",
            "ai", "sd", "tools", "apps", "games", "programs", "models"
        };

        // Robust scan for an existing ComfyUI portable so we never install a
        // second copy. Seeds a broad set of common locations (user folders,
        // Program Files, AppData\Local\Programs, ProgramData, every fixed drive
        // root + common parent folder names), then does a bounded recursive
        // descent (max depth 3) that only follows AI/ComfyUI-named branches.
        // Returns the first valid portable folder, or null.
        public static string AutoDetectPortable()
        {
            List<string> seeds = new List<string>();
            Action<string> add = p => { if (!string.IsNullOrEmpty(p)) seeds.Add(p); };
            try { add(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop)); } catch { }
            try { add(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments)); } catch { }
            try { add(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles)); } catch { }
            try { add(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86)); } catch { }
            try { add(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData)); } catch { }
            try
            {
                string lad = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                add(lad);
                add(System.IO.Path.Combine(lad, "Programs"));
            }
            catch { }
            try { add(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData)); } catch { }
            try
            {
                string up = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
                add(up);
                add(System.IO.Path.Combine(up, "Downloads"));
            }
            catch { }
            string[] commonNames = {
                "ComfyUI_windows_portable", "ComfyUI", "ComfyUI-portable", "Comfy",
                "AI", "AITools", "Tools", "Apps", "Programs", "Games",
                "StableDiffusion", "stable-diffusion", "SD"
            };
            try
            {
                foreach (System.IO.DriveInfo drive in System.IO.DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || drive.DriveType != System.IO.DriveType.Fixed) continue;
                    string r = drive.RootDirectory.FullName;
                    add(r);
                    foreach (string n in commonNames) add(System.IO.Path.Combine(r, n));
                }
            }
            catch { }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in seeds)
            {
                string hit = SearchForPortable(s, 0, 3, seen);
                if (!string.IsNullOrEmpty(hit))
                {
                    Log.Message("Avatar: auto-detect found ComfyUI portable at " + hit);
                    return hit;
                }
            }
            return null;
        }

        // Bounded DFS. Always checks the dir itself; recurses into children up to
        // maxDepth but only follows AI/ComfyUI-relevant branches past depth 1, so
        // it never walks a whole drive. Cycle/duplicate-safe via `seen`.
        private static string SearchForPortable(string dir, int depth, int maxDepth, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                if (!System.IO.Directory.Exists(dir)) return null;
                if (!seen.Add(dir)) return null;
                if (IsValidPortableFolder(dir)) return dir;
                if (depth >= maxDepth) return null;

                string[] children;
                try { children = System.IO.Directory.GetDirectories(dir); }
                catch { return null; } // access denied / IO — skip quietly

                foreach (string child in children)
                {
                    string name = System.IO.Path.GetFileName(child);
                    if (string.IsNullOrEmpty(name)) continue;
                    string lower = name.ToLowerInvariant();

                    bool skip = false;
                    foreach (string sk in DetectSkipNames) { if (lower == sk) { skip = true; break; } }
                    if (skip) continue;

                    // Direct child is always checked (nextMax == depth+1 stops it
                    // recursing further); relevant-named branches recurse deeper.
                    bool relevant = false;
                    foreach (string frag in DetectRelevantFragments)
                    {
                        if (lower == frag || lower.Contains(frag)) { relevant = true; break; }
                    }
                    int nextMax = relevant ? maxDepth : depth + 1;
                    string hit = SearchForPortable(child, depth + 1, nextMax, seen);
                    if (!string.IsNullOrEmpty(hit)) return hit;
                }
            }
            catch { }
            return null;
        }

        // Opens a native Windows folder picker via PowerShell so we don't need a
        // System.Windows.Forms reference. Blocks until the user dismisses the
        // dialog. Returns the selected path or null if cancelled.
        public static string BrowseForPortableFolder()
        {
            try
            {
                string psScript =
                    "Add-Type -AssemblyName System.Windows.Forms; " +
                    "$f = New-Object System.Windows.Forms.FolderBrowserDialog; " +
                    "$f.Description = 'Select your ComfyUI_windows_portable folder (must contain python_embeded and ComfyUI subfolders)'; " +
                    "$f.ShowNewFolderButton = $false; " +
                    "$r = $f.ShowDialog(); " +
                    "if ($r -eq 'OK') { Write-Output $f.SelectedPath }";
                // Resolve the FULL path to powershell.exe. Under Unity's Mono,
                // Process.Start with UseShellExecute=false does NOT reliably
                // search PATH, so a bare "powershell.exe" throws Win32 error 2
                // ("The system cannot find the file specified."). Build the
                // canonical System32 path and fall back to the bare name only if
                // that file is somehow absent.
                string sysDir = "";
                try { sysDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System); } catch { }
                string pwsh = string.IsNullOrEmpty(sysDir)
                    ? "powershell.exe"
                    : System.IO.Path.Combine(sysDir, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!System.IO.File.Exists(pwsh)) pwsh = "powershell.exe";
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = pwsh;
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -Command \"" + psScript + "\"";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                // An empty CurrentDirectory was part of the failure signature;
                // give the child process a guaranteed-valid working directory.
                if (!string.IsNullOrEmpty(sysDir)) psi.WorkingDirectory = sysDir;
                System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(300000); // up to 5 minutes for user input
                if (string.IsNullOrEmpty(output)) return null;
                string picked = output.Trim();
                return string.IsNullOrEmpty(picked) ? null : picked;
            }
            catch (Exception e)
            {
                Log.Warning("Avatar: Folder picker failed: " + e.Message);
                return null;
            }
        }

        // Returns true if our managed ComfyUI process (the cmd.exe we launched)
        // is still running. We need this to avoid spawning a second cmd window
        // while the first one is still mid-bootup — the launcher was previously
        // happy to fire repeatedly during the 30-60s cold start because
        // CheckComfyUIAlive returns false during that window.
        private static bool IsManagedComfyAlive()
        {
            int pid = managedComfyPid;
            if (pid == 0) return false;
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid))
                {
                    return p != null && !p.HasExited;
                }
            }
            catch (ArgumentException) { return false; } // PID is gone
            catch { return false; }
        }

        // Fires up ComfyUI via its bundled .bat file. Non-blocking: returns true if
        // a launch was kicked off successfully, not whether ComfyUI is fully ready.
        // Ownership rule: we only claim managedComfyPid when ComfyUI was NOT alive
        // immediately before launch. If the user had already started it manually,
        // we leave managedComfyPid=0 and won't kill it on quit.
        public static bool LaunchComfyUI()
        {
            // Duplicate-launch guard #1: if a cmd window we already spawned is
            // still running (ComfyUI cold-booting), do nothing. Stops the user
            // from clicking "Launch ComfyUI now" twice and getting two cmd
            // windows racing for port 8188.
            if (IsManagedComfyAlive())
            {
                Log.Message("Avatar: ComfyUI launch already in progress (PID " + managedComfyPid + ") — ignoring duplicate request.");
                return true;
            }
            // Race-safe ownership: probe alive *first*, only claim if we actually
            // start a new process. If ComfyUI is already up, mark the session as
            // launch-attempted (so the queue gate stops trying) but don't claim.
            if (CheckComfyUIAlive())
            {
                comfyLaunchAttempted = true;
                managedComfyPid = 0; // user (or a previous launch we lost track of) owns it
                lastConfirmedAlive = DateTime.UtcNow; // immediate — no grace period for direct launch check
                Log.Message("Avatar: ComfyUI already running — not launching, not claiming ownership.");
                return true;
            }
            string portable = GetPortablePath();
            if (string.IsNullOrEmpty(portable)) return false;
            string gpuBat = System.IO.Path.Combine(portable, "run_nvidia_gpu.bat");
            string cpuBat = System.IO.Path.Combine(portable, "run_cpu.bat");
            string batPath = System.IO.File.Exists(gpuBat) ? gpuBat
                : (System.IO.File.Exists(cpuBat) ? cpuBat : null);
            if (batPath == null)
            {
                Log.Warning("Avatar: No run_nvidia_gpu.bat or run_cpu.bat in " + portable);
                return false;
            }
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                // Launch via cmd.exe directly so we can suppress the console window
                // (CreateNoWindow prevents the alt-tab flash). cmd.exe stays alive as
                // long as the batch file's python process runs, so taskkill /T still
                // walks the tree and kills the grandchild correctly.
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c \"" + batPath + "\"";
                psi.WorkingDirectory = portable;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
                managedComfyPid = (proc != null) ? proc.Id : 0;
                if (managedComfyPid != 0) WritePidSidecar(managedComfyPid);
                comfyLaunchAttempted = true;
                InstallQuitHook(); // belt-and-suspenders — usually already wired
                Log.Message("Avatar: Launched ComfyUI: " + batPath + " (managed PID " + managedComfyPid + ")");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("Avatar: Failed to launch ComfyUI: " + e.Message);
                return false;
            }
        }

        // Force-kills the ComfyUI process tree we launched. No-op if we didn't
        // launch it (managedComfyPid=0) or if the PID has already exited. Uses
        // `taskkill /F /T /PID <pid>` because Process.Kill on the cmd.exe parent
        // does NOT cascade to the python.exe grandchild on Windows — the python
        // process keeps running and keeps eating VRAM. /T forces the whole tree.
        public static void KillManagedComfyUI()
        {
            int pid = managedComfyPid;
            managedComfyPid = 0;
            DeletePidSidecar();
            if (pid == 0) return;
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    "taskkill", "/F /T /PID " + pid);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                if (p != null) p.WaitForExit(5000);
                Log.Message("Avatar: Killed managed ComfyUI process tree (PID " + pid + ").");
            }
            catch (Exception e)
            {
                Log.Warning("Avatar: KillManagedComfyUI threw: " + e.Message);
            }
        }

        // Returns true if ComfyUI is alive. If not alive and auto-launch is enabled
        // and we haven't tried yet this session, kicks off a background launch.
        public static bool EnsureComfyUIRunning()
        {
            if (CheckComfyUIAlive())
            {
                comfyLaunchAttempted = false; // ready again — next downtime can re-launch
                lastConfirmedAlive = DateTime.UtcNow; // immediate — no grace period for direct check
                return true;
            }
            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                if (m == null || !m.settings.autoLaunchComfyUI) return false;
            }
            catch { return false; }
            if (comfyLaunchAttempted) return false;
            LaunchComfyUI();
            return false;
        }

        public static void NotifyComfyUIMissing()
        {
            if (comfyUINotified) return;
            comfyUINotified = true;
            string portable = GetPortablePath();
            string body;
            if (string.IsNullOrEmpty(portable))
            {
                body = "Avatar - Personas tried to generate AI portraits in the background, "
                    + "but no ComfyUI Portable folder is configured.\n\n"
                    + "To enable AI portrait generation:\n"
                    + "1. Download ComfyUI portable for Windows from github.com/comfyanonymous/ComfyUI\n"
                    + "   (look for ComfyUI_windows_portable_nvidia.7z or _cpu.7z under Releases)\n"
                    + "2. Extract the .7z anywhere on your PC (e.g. C:\\ComfyUI_windows_portable)\n"
                    + "3. Open Mod Options → Avatar - Personas and click Auto-detect or Browse\n"
                    + "4. Click Install dependencies, then Launch ComfyUI now\n"
                    + "5. Place sd_xl_base_1.0.safetensors and RimWorld_1.1-000001.safetensors\n"
                    + "   into <portable>\\ComfyUI\\models\\checkpoints and \\loras respectively\n\n"
                    + "Until then, avatars will still appear in pixel-art style.";
            }
            else
            {
                body = "Avatar - Personas tried to generate AI portraits, but could not reach "
                    + "ComfyUI at http://127.0.0.1:8188.\n\n"
                    + "If auto-launch is enabled, ComfyUI should be starting up in a console window now — "
                    + "wait ~30-60 seconds for it to finish loading, then try Generate Portrait again.\n\n"
                    + "Portable folder: " + portable;
            }
            Find.LetterStack.ReceiveLetter(
                "ComfyUI Not Ready",
                body,
                LetterDefOf.NeutralEvent
            );
        }

        // True when the backend for the CURRENTLY SELECTED portrait style is
        // ready to generate. RimWorld style only needs ComfyUI up (the SDXL base
        // checkpoint ships with the bootstrap). Ultra-realistic ALSO needs the
        // lazily-downloaded RealVisXL checkpoint present — until it lands we must
        // NOT enqueue/generate, because a "checkpoint not found" failure would
        // burn the pawn's retry budget and permanently cap them before the model
        // even arrives. Notifies once per miss-streak (the flag resets the moment
        // the model is present). Returns true on any error so we never wedge.
        private static bool styleMissingNotified = false;
        public static bool SelectedStyleBackendReady()
        {
            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                if (m == null) return true;
                PortraitStyleSpec spec = PortraitStyles.Get(m.settings.portraitStyle);
                if (spec == null) return true; // RimWorld — base checkpoint ships with the bootstrap
                if (SetupManager.StyleCheckpointInstalled(spec))
                {
                    styleMissingNotified = false;
                    return true;
                }
                if (!styleMissingNotified)
                {
                    styleMissingNotified = true;
                    string msg = SetupManager.Active
                        ? "Avatar - Personas: the " + spec.displayName + " model is still downloading — portraits will start once it finishes."
                        : "Avatar - Personas: " + spec.displayName + " is selected but its model isn't installed. Open Mod Options to download it.";
                    try { Messages.Message(msg, MessageTypeDefOf.NeutralEvent, historical: false); } catch { }
                }
                return false;
            }
            catch { return true; }
        }

        public static bool CheckComfyUIAlive()
        {
            try
            {
                System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:8188/system_stats");
                // 2.5s is plenty for a localhost HTTP probe; the old 5s was overkill
                // and visibly stalled the settings panel while ComfyUI was cold-booting.
                req.Timeout = 2500;
                using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    // Pure probe — callers decide whether (and when) to stamp the cache.
                    return resp.StatusCode == System.Net.HttpStatusCode.OK;
                }
            }
            catch { }
            return false;
        }

        // ============================================================
        // Test Generate: fires a known-good prompt against ComfyUI and
        // toasts the outcome. Used by the settings panel button so the
        // user can confirm their full pipeline works (portable folder,
        // python deps, ComfyUI server, models, embedded watcher script,
        // file writeback) without waiting for a pawn to spawn.
        // ============================================================
        public static void RunTestGeneration()
        {
            if (testGenInProgress)
            {
                Messages.Message("A test generation is already running. Wait for it to finish.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            string python = GetEmbeddedPython();
            if (string.IsNullOrEmpty(python))
            {
                Messages.Message("Set ComfyUI Portable folder first (Auto-detect or Browse above).", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            // Review #15: use the cached IsConfirmedReady flag instead of
            // a synchronous 2.5s HTTP probe on the main thread. If the cache
            // is stale, TryConfirmComfyReady kicks off a background probe and
            // the user retries in a moment.
            if (!IsConfirmedReady)
            {
                TryConfirmComfyReady();
                EnsureComfyUIRunning();
                Messages.Message("Checking ComfyUI in background — try Test Generate again in a moment (or wait 30-60s if it just launched).", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (!EnsurePythonDepsInstalled())
            {
                Messages.Message("Python dependencies are installing in the background. Try again in 1-3 minutes.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            // Realistic style needs RealVisXL present; SelectedStyleBackendReady
            // toasts the reason if it's still downloading / not installed.
            if (!SelectedStyleBackendReady()) return;
            string testDir = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "avatar");
            try
            {
                if (!System.IO.Directory.Exists(testDir))
                    System.IO.Directory.CreateDirectory(testDir);
            }
            catch (Exception e)
            {
                Messages.Message("Test generate: cannot create avatar folder: " + e.Message, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            string testPath = System.IO.Path.Combine(testDir, "_test_generate.png");
            try
            {
                WriteNeutralTestPng(testPath);
            }
            catch (Exception e)
            {
                Messages.Message("Test generate: writing test input failed: " + e.Message, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            DateTime startTime = DateTime.UtcNow;
            DateTime inputMtimeBefore = System.IO.File.GetLastWriteTimeUtc(testPath);
            const string testPrompt = "front portrait, 30-year-old female human, adult, brown hair, brown eyes, simple gray shirt";
            System.Diagnostics.Process proc;
            try
            {
                proc = NewProcess(testPath, testPrompt);
            }
            catch (Exception e)
            {
                Messages.Message("Test generate launch failed: " + e.Message, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            testGenInProgress = true;
            string capturedPath = testPath;
            DateTime capturedMtime = inputMtimeBefore;
            DateTime capturedStart = startTime;
            proc.Exited += (s, ev) =>
            {
                try
                {
                    System.Diagnostics.Process p = (System.Diagnostics.Process)s;
                    double elapsed = (DateTime.UtcNow - capturedStart).TotalSeconds;
                    int exitCode = p.ExitCode;
                    DateTime mtimeAfter = System.IO.File.Exists(capturedPath)
                        ? System.IO.File.GetLastWriteTimeUtc(capturedPath)
                        : DateTime.MinValue;
                    bool succeeded = exitCode == 0 && mtimeAfter > capturedMtime;
                    // Messages.Message must run on the main thread; LongEventHandler
                    // is the standard cross-thread bounce for showing user feedback.
                    LongEventHandler.ExecuteWhenFinished(() =>
                    {
                        if (succeeded)
                        {
                            Messages.Message(
                                string.Format("Test generation succeeded in {0:F1}s — pipeline is healthy. Output: {1}", elapsed, capturedPath),
                                MessageTypeDefOf.TaskCompletion, historical: false);
                        }
                        else
                        {
                            Messages.Message(
                                string.Format("Test generation FAILED (exit={0}, elapsed={1:F1}s). Check RimWorld log for details.", exitCode, elapsed),
                                MessageTypeDefOf.RejectInput, historical: false);
                        }
                    });
                }
                catch { }
                finally { testGenInProgress = false; }
            };
            try
            {
                proc.Start();
                proc.BeginErrorReadLine();
                Messages.Message("Test generate launched — result will appear in 30-90 seconds.", MessageTypeDefOf.NeutralEvent, historical: false);
            }
            catch (Exception e)
            {
                testGenInProgress = false;
                Messages.Message("Test generate: process.Start threw: " + e.Message, MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        private static void WriteNeutralTestPng(string path)
        {
            // 480x576 matches SaveToStaticPortrait's upscale dimensions so the
            // ComfyUI workflow handles it identically to a real pawn input.
            const int w = 480, h = 576;
            UnityEngine.Texture2D tex = new UnityEngine.Texture2D(w, h);
            UnityEngine.Color fill = new UnityEngine.Color(0.5f, 0.5f, 0.5f, 1f);
            UnityEngine.Color[] pixels = new UnityEngine.Color[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
            tex.SetPixels(pixels);
            tex.Apply();
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        // ============================================================
        // Eager pre-warm at game load. Called once from HarmonyInit's static
        // ctor (main thread). Instead of waiting for the first portrait enqueue
        // to trigger the ~30-60s torch/CUDA cold boot, we launch ComfyUI now so
        // that boot overlaps with the player loading their save / playing. Once
        // the server is up we fire one throwaway generation so the SDXL
        // checkpoint is hot in VRAM (and InSPyReNet weights are downloaded)
        // before the first real portrait is requested.
        //
        // Gated on autoLaunchComfyUI. No-op if the portable folder isn't set.
        // LaunchComfyUI's own guards handle the already-running / already-managed
        // cases, so this is safe even after RecoverOrphanedComfyUI adopted a PID.
        // ============================================================
        public static void PrewarmComfyUI()
        {
            bool autoLaunch;
            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                autoLaunch = m != null && m.settings.autoLaunchComfyUI;
            }
            catch { return; }
            if (!autoLaunch) return;
            string portable = GetPortablePath();
            if (string.IsNullOrEmpty(portable) || !IsValidPortableFolder(portable)) return;

            // Build the warm-up input PNG NOW, on the main thread — Unity's
            // Texture2D / EncodeToPNG API is main-thread-only and the background
            // worker below cannot touch it. HarmonyInit runs during
            // StaticConstructorOnStartup, which is the main thread.
            string warmPath = null;
            try
            {
                string warmDir = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "avatar");
                if (!System.IO.Directory.Exists(warmDir)) System.IO.Directory.CreateDirectory(warmDir);
                warmPath = System.IO.Path.Combine(warmDir, "_warmup.png");
                WriteNeutralTestPng(warmPath);
            }
            catch (Exception e)
            {
                Log.Warning("Avatar: prewarm could not write warm-up input: " + e.Message);
                warmPath = null;
            }

            // Launch + wait-for-ready + warm-up all happen off the main thread so
            // game load is never blocked on ComfyUI's boot.
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    LaunchComfyUI();
                    WarmUpGeneration(warmPath);
                }
                catch (Exception e) { Log.Warning("Avatar: PrewarmComfyUI worker failed: " + e.Message); }
            });
        }

        // Fires one throwaway generation so the SDXL checkpoint loads into VRAM
        // before the first real portrait. MUST be called from a background thread
        // (it polls for ComfyUI readiness with Thread.Sleep). warmPath must be a
        // valid pre-written PNG (see PrewarmComfyUI — written on the main thread).
        private static void WarmUpGeneration(string warmPath)
        {
            if (warmupDone || warmupInProgress) return;
            if (string.IsNullOrEmpty(warmPath) || !System.IO.File.Exists(warmPath)) return;
            warmupInProgress = true;
            try
            {
                // Cold torch/CUDA boot can take well over a minute on slow disks;
                // poll generously (~4 min) at the same 2s cadence the queue uses.
                bool ready = false;
                for (int i = 0; i < 120; i++)
                {
                    if (CheckComfyUIAlive()) { ready = true; break; }
                    System.Threading.Thread.Sleep(2000);
                }
                if (!ready) return;
                // Don't fire a warm-up we know will fail prompt-validation because
                // the rembg node isn't installed yet — the first real portrait
                // installs it and warms the checkpoint naturally afterwards.
                if (CheckInspyrenetNodeInstalled() != "installed") return;
                if (!EnsurePythonDepsInstalled()) return;
                // Skip the warm-up when the selected style's model isn't downloaded
                // yet — it would only fail "ckpt_name not in list" and log an error.
                // RimWorld base (spec == null) always passes. warmupDone stays
                // false so a later prewarm warms up once the model lands.
                try
                {
                    AvatarMod wm = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                    if (wm != null)
                    {
                        PortraitStyleSpec wspec = PortraitStyles.Get(wm.settings.portraitStyle);
                        if (wspec != null && !SetupManager.StyleCheckpointInstalled(wspec)) return;
                    }
                }
                catch { }
                System.Diagnostics.Process proc = NewProcess(warmPath, WarmupPrompt);
                proc.Start();
                proc.BeginErrorReadLine();
                warmupDone = true;
                Log.Message("Avatar: warm-up generation started — preloading SDXL checkpoint into VRAM before the first portrait.");
            }
            catch (Exception e) { Log.Warning("Avatar: warm-up generation failed: " + e.Message); }
            finally { warmupInProgress = false; }
        }

        private static string ExtractScript()
        {
            if (scriptPath != null && System.IO.File.Exists(scriptPath)) return scriptPath;
            try
            {
                // NOT Path.GetTempPath() directly: Unity can launch RimWorld with a
                // TMP/TEMP pointing at a dir that doesn't exist or isn't writable
                // (the same env bug ApplyPipEnv works around for child pip
                // processes). Probe for a writable dir first.
                string tempDir = ResolveWritableTempDir(GetEmbeddedPython());
                if (string.IsNullOrEmpty(tempDir)) tempDir = System.IO.Path.GetTempPath();
                string tempScript = System.IO.Path.Combine(tempDir, "avatar_comfy_watcher.py");
                System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string resourceName = "Avatar.comfy_watcher.py";
                using (System.IO.Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Log.Error("Avatar: Could not find embedded resource " + resourceName);
                        return null;
                    }
                    using (System.IO.StreamReader reader = new System.IO.StreamReader(stream))
                    {
                        System.IO.File.WriteAllText(tempScript, reader.ReadToEnd());
                    }
                }
                scriptPath = tempScript;
                return scriptPath;
            }
            catch (Exception e)
            {
                Log.Error("Avatar: Failed to extract embedded script: " + e.Message);
                return null;
            }
        }

        public static bool CheckPythonDeps()
        {
            if (depsChecked.HasValue) return depsChecked.Value;
            string python = GetEmbeddedPython();
            if (string.IsNullOrEmpty(python))
            {
                depsChecked = false;
                return false;
            }
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    python, "-s -c \"import PIL, requests\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
                proc.WaitForExit(15000);
                depsChecked = proc.ExitCode == 0;
                return depsChecked.Value;
            }
            catch
            {
                depsChecked = false;
                return false;
            }
        }

        public static bool InstallPythonDeps()
        {
            depsChecked = null;
            string python = GetEmbeddedPython();
            if (string.IsNullOrEmpty(python)) return false;
            // IMPORTANT: pip emits hundreds of KB to stdout (download progress, wheel
            // resolution noise). Synchronous ReadToEnd on stdout+stderr deadlocks the
            // moment one pipe buffer fills while we're blocked reading the other.
            // We drain both pipes asynchronously and only block on WaitForExit.
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    python, "-s -m pip install --disable-pip-version-check --no-input pillow requests");
                psi.UseShellExecute = false;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                ApplyPipEnv(psi, python);
                System.Text.StringBuilder stdoutSb = new System.Text.StringBuilder();
                System.Text.StringBuilder stderrSb = new System.Text.StringBuilder();
                System.Diagnostics.Process proc = new System.Diagnostics.Process();
                proc.StartInfo = psi;
                proc.OutputDataReceived += (s, ev) => { if (ev.Data != null) lock (stdoutSb) stdoutSb.AppendLine(ev.Data); };
                proc.ErrorDataReceived += (s, ev) => { if (ev.Data != null) lock (stderrSb) stderrSb.AppendLine(ev.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                bool exited = proc.WaitForExit(600000); // 10 minutes hard cap — slow connections + large wheels
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    Log.Warning("Avatar: pip install exceeded 10 minute hard cap. Killed. Try manually: \"" + python + "\" -m pip install pillow requests");
                    depsChecked = false;
                    return false;
                }
                // Give the async readers a moment to drain post-exit
                proc.WaitForExit();
                bool success = proc.ExitCode == 0;
                string stdout, stderr;
                lock (stdoutSb) stdout = stdoutSb.ToString();
                lock (stderrSb) stderr = stderrSb.ToString();
                if (!success)
                {
                    Log.Warning("Avatar: pip install failed (exit=" + proc.ExitCode + "):\n"
                        + (string.IsNullOrEmpty(stdout) ? "" : ("stdout (tail):\n" + Tail(stdout, 4000) + "\n"))
                        + (string.IsNullOrEmpty(stderr) ? "" : ("stderr (tail):\n" + Tail(stderr, 4000))));
                }
                depsChecked = success;
                return success;
            }
            catch (Exception e)
            {
                Log.Warning("Avatar: pip install threw: " + e.Message);
                depsChecked = false;
                return false;
            }
        }

        private static string Tail(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
            return "...(truncated)...\n" + s.Substring(s.Length - maxChars);
        }

        // ============================================================
        // ComfyUI-Inspyrenet-Rembg custom node auto-installer
        // ============================================================
        // The mod's portrait pipeline emits an `InspyrenetRembg` workflow node
        // (from john-mnz/ComfyUI-Inspyrenet-Rembg). This is a dedicated single-
        // model wrapper around the `transparent_background` PyPI package — the
        // entire pip install is one package, takes ~10s, vs the multi-model
        // ComfyUI-RMBG node which pulls ~30 packages (opencv, matplotlib,
        // groundingdino, segment-anything, etc.) and routinely failed mid-install
        // for our users.
        //
        // Install flow: download zip from GitHub, extract into custom_nodes/,
        // pip install requirements.txt (just `transparent_background`), restart
        // ComfyUI if we own its PID so the new node loads. All background-threaded
        // and gated by EnsureInspyrenetNodeInstalled so the portrait queue holds
        // until the node is fully usable.
        //
        // Lesson learned (from earlier ComfyUI-RMBG attempt): a kitchen-sink
        // requirements.txt that includes deps for models we don't use will
        // routinely time out or break on package conflicts. Always prefer
        // single-purpose ComfyUI nodes for production-grade auto-install.
        // ============================================================
        private const string InspyrenetNodeZipUrl = "https://github.com/john-mnz/ComfyUI-Inspyrenet-Rembg/archive/refs/heads/main.zip";
        private const string InspyrenetNodeFolderName = "ComfyUI-Inspyrenet-Rembg";
        private const string InspyrenetZipRootName = "ComfyUI-Inspyrenet-Rembg-main";

        private static bool? inspyrenetNodeInstalled = null;
        private static volatile bool inspyrenetInstallInProgress = false;
        private static volatile bool inspyrenetRestartInProgress = false;
        // Anti-spam: a persistently-failing pip install used to loop forever —
        // each failure deletes the node folder, so the very next queue tick
        // re-triggered a fresh download + pip attempt + red error message.
        // We now cap auto-retries and back off between them. The manual
        // "Install ... node automatically" button bypasses the cap (it calls
        // InstallInspyrenetNodeAsync directly and resets the counter).
        private const int InspyrenetMaxAutoAttempts = 3;
        private static volatile int inspyrenetFailCount = 0;
        private static DateTime inspyrenetNextRetryUtc = DateTime.MinValue;
        // Tail of the last failed pip run (stdout+stderr) so the failure
        // message can name the real cause instead of "see RimWorld log".
        private static volatile string lastPipFailTail = null;
        // Folder-on-disk is NOT proof the node loaded: if ComfyUI started before
        // the node was added, or transparent_background (or a dep like torchvision)
        // fails to import, ComfyUI silently skips the node and every portrait
        // prompt 400s with "Node 'InspyrenetRembgAdvanced' not found". We confirm
        // the node is actually registered in the RUNNING server before treating
        // it as ready, restart ComfyUI once to load an on-disk node, and surface
        // a clear message if the import is the problem.
        private const string InspyrenetWorkflowNodeName = "InspyrenetRembgAdvanced";
        private static volatile bool inspyrenetNodeRegisteredConfirmed = false;
        private static volatile bool inspyrenetRestartForNodeAttempted = false;
        private static volatile bool inspyrenetImportFailureReported = false;
        // Async, throttled cache for the registration probe. ProbeInspyrenetNodeRegistered
        // does a BLOCKING HTTP GET with a 3s timeout; calling it from the per-frame
        // dispatch gate (EnsureInspyrenetNodeInstalled, runs on the main thread every
        // DelayRealSeconds) froze the game for up to 3s while ComfyUI was busy loading
        // the checkpoint. We poll it on a background thread instead and read the cached
        // tri-state on the main thread — mirrors the off-main /system_stats gate.
        //   inspyrenetProbeState: 1 = registered, -1 = server up but node absent, 0 = unknown
        private static volatile int inspyrenetProbeState = 0;
        private static volatile bool inspyrenetProbeInFlight = false;
        private static DateTime inspyrenetNextProbeUtc = DateTime.MinValue;
        private const double InspyrenetProbeCooldownSeconds = 2.0;
        public static bool InspyrenetInstallInProgress => inspyrenetInstallInProgress || inspyrenetRestartInProgress;
        public static bool InspyrenetNodeChecked => inspyrenetNodeInstalled.HasValue;
        public static bool InspyrenetNodeReady => inspyrenetNodeInstalled == true;
        // True only once the node is confirmed registered in the running ComfyUI.
        public static bool InspyrenetNodeRegistered => inspyrenetNodeRegisteredConfirmed;
        // True when the node is on disk but ComfyUI failed to load it (needs restart
        // or has a Python dependency conflict blocking the import).
        public static bool InspyrenetNodeLoadFailed => inspyrenetImportFailureReported;
        // The verbatim tail of the last failed pip invocation (stdout/stderr).
        // Surfaced in the Mod Options panel so a player can read/report the real
        // cause without opening Player.log. Null once a pip install succeeds.
        public static string LastPipFailTail => lastPipFailTail;

        // Returns one of:
        //   "installed"            — folder exists + has __init__.py
        //   "not installed"        — folder is missing entirely
        //   "ComfyUI portable not set"
        //   "folder exists but missing __init__.py — delete + retry"
        public static string CheckInspyrenetNodeInstalled()
        {
            string portable = GetPortablePath();
            if (string.IsNullOrEmpty(portable))
            {
                inspyrenetNodeInstalled = false;
                return "ComfyUI portable not set";
            }
            string nodeDir = System.IO.Path.Combine(portable, "ComfyUI", "custom_nodes", InspyrenetNodeFolderName);
            if (!System.IO.Directory.Exists(nodeDir))
            {
                inspyrenetNodeInstalled = false;
                return "not installed";
            }
            string init = System.IO.Path.Combine(nodeDir, "__init__.py");
            if (!System.IO.File.Exists(init))
            {
                inspyrenetNodeInstalled = false;
                return "folder exists but missing __init__.py — delete + retry";
            }
            inspyrenetNodeInstalled = true;
            return "installed";
        }

        // Mirrors EnsurePythonDepsInstalled in shape: returns true once the
        // node is in place; on first call where it's missing, kicks off a
        // background install and returns false until that finishes.
        //
        // CRITICAL ordering (fix for an earlier race): in-progress flags are
        // checked BEFORE the cached `inspyrenetNodeInstalled` state. During the
        // ComfyUI restart window after install, the cache reads true but ComfyUI
        // is mid-restart — if we returned true here, the queue would fire a
        // portrait against a dead server. Gating on in-progress first keeps the
        // queue holding through the whole install+restart sequence.
        public static bool EnsureInspyrenetNodeInstalled()
        {
            if (inspyrenetInstallInProgress || inspyrenetRestartInProgress) return false;

            // Step 1: is the node folder on disk? (Sets inspyrenetNodeInstalled.)
            string state = CheckInspyrenetNodeInstalled();
            if (state != "installed")
            {
                if (string.IsNullOrEmpty(GetPortablePath())) return false;
                // Anti-spam guard: stop hammering a persistently-failing pip install.
                // After InspyrenetMaxAutoAttempts failures we give up auto-retrying
                // for the session (the user can still trigger it from Mod Options);
                // between attempts we wait out a backoff window so a transient
                // failure isn't retried on the very next queue tick.
                if (inspyrenetFailCount >= InspyrenetMaxAutoAttempts) return false;
                if (DateTime.UtcNow < inspyrenetNextRetryUtc) return false;
                InstallInspyrenetNodeAsync(null);
                return false;
            }

            // Step 2: folder is on disk, but that does NOT mean ComfyUI loaded the
            // node. Confirm it's actually registered in the running server before
            // letting the queue fire a prompt that would 400. The probe is a blocking
            // HTTP round-trip, so it runs on a BACKGROUND thread (throttled, cached) —
            // reading it synchronously here used to freeze the main thread for up to
            // the 3s HTTP timeout every dispatch tick while ComfyUI was loading the
            // checkpoint. Mirrors the off-main /system_stats gate above.
            if (inspyrenetNodeRegisteredConfirmed) return true;

            int registered = PollInspyrenetRegistrationCached();
            if (registered > 0)
            {
                inspyrenetNodeRegisteredConfirmed = true;
                inspyrenetImportFailureReported = false;
                return true;
            }
            if (registered == 0)
            {
                // Unknown yet: probe in flight / throttled, or ComfyUI unreachable
                // (still cold-booting). The /system_stats gate upstream already holds
                // the queue; just wait for the next cached result.
                return false;
            }

            // registered == false: ComfyUI is up, the node folder is present, but
            // the node isn't registered. Two causes:
            //   (a) ComfyUI started BEFORE the node was added — a restart loads it.
            //   (b) the node's Python import failed (e.g. transparent_background /
            //       torchvision dependency conflict) — a restart won't help.
            if (managedComfyPid != 0 && IsManagedComfyAlive() && !inspyrenetRestartForNodeAttempted)
            {
                inspyrenetRestartForNodeAttempted = true;
                Log.Warning("Avatar: " + InspyrenetWorkflowNodeName + " is on disk but not registered in the running ComfyUI — restarting ComfyUI once to load it.");
                RestartManagedComfyForNode();
                return false;
            }

            // Already restarted (or we don't own the ComfyUI process) and the node
            // still isn't there — its import is failing. Surface once, then hold the
            // queue so we don't spam HTTP 400s for every queued pawn.
            if (!inspyrenetImportFailureReported)
            {
                inspyrenetImportFailureReported = true;
                Log.Warning("Avatar: " + InspyrenetWorkflowNodeName + " is installed on disk but ComfyUI did not load it. "
                    + "If you launched ComfyUI yourself, restart it. If it persists, transparent_background failed to import "
                    + "(a Python dependency conflict) — check the ComfyUI console for the import traceback.");
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    Messages.Message("Avatar - Personas: background-removal node is installed but ComfyUI didn't load it. "
                        + "Restart ComfyUI; if it persists, a Python dependency conflict is blocking it (see ComfyUI console).",
                        MessageTypeDefOf.RejectInput, historical: false);
                });
            }
            return false;
        }

        // Non-blocking accessor for the node-registration state, used by the
        // per-frame dispatch gate. Returns the last cached probe result immediately
        // (1 = registered, -1 = server up but node absent, 0 = unknown) and kicks off
        // a throttled background probe to refresh it. NEVER blocks the calling thread
        // on the HTTP round-trip — that's the whole point (see field comments).
        private static int PollInspyrenetRegistrationCached()
        {
            if (!inspyrenetProbeInFlight && DateTime.UtcNow >= inspyrenetNextProbeUtc)
            {
                inspyrenetProbeInFlight = true;
                inspyrenetNextProbeUtc = DateTime.UtcNow.AddSeconds(InspyrenetProbeCooldownSeconds);
                System.Threading.Thread t = new System.Threading.Thread(() =>
                {
                    try
                    {
                        bool? r = ProbeInspyrenetNodeRegistered();
                        inspyrenetProbeState = (r == true) ? 1 : (r == false ? -1 : 0);
                    }
                    catch { }
                    finally { inspyrenetProbeInFlight = false; }
                });
                t.IsBackground = true;
                t.Name = "AvatarInspyrenetProbe";
                t.Start();
            }
            return inspyrenetProbeState;
        }

        // Probes the RUNNING ComfyUI to confirm InspyrenetRembgAdvanced actually
        // loaded. Returns true (registered), false (server up but node absent),
        // or null (server unreachable — unknown, caller should wait/retry).
        // Synchronous/blocking — call only from a background thread (see
        // PollInspyrenetRegistrationCached, which is what the main-thread gate uses).
        public static bool? ProbeInspyrenetNodeRegistered()
        {
            try
            {
                System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                    "http://127.0.0.1:8188/object_info/" + InspyrenetWorkflowNodeName);
                req.Timeout = 3000;
                using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK) return false;
                    using (System.IO.StreamReader sr = new System.IO.StreamReader(resp.GetResponseStream()))
                    {
                        string body = sr.ReadToEnd();
                        // /object_info/<class> returns {"<class>": {...}} when present,
                        // or {} when the class isn't registered.
                        return !string.IsNullOrEmpty(body) && body.Contains(InspyrenetWorkflowNodeName);
                    }
                }
            }
            catch (System.Net.WebException we)
            {
                // A 404 means the server answered but has no such node => not registered.
                // Anything else (connection refused / timeout) => server unreachable.
                if (we.Response is System.Net.HttpWebResponse) return false;
                return null;
            }
            catch { return null; }
        }

        // Background restart of a ComfyUI instance we own, to load a node that was
        // added to custom_nodes after the server had already started. Sets
        // inspyrenetRestartInProgress so the queue holds through the restart.
        private static void RestartManagedComfyForNode()
        {
            inspyrenetRestartInProgress = true;
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                try
                {
                    KillManagedComfyUI();
                    lastConfirmedAlive = DateTime.MinValue;
                    comfyLaunchAttempted = false;
                    // Drop the stale "node absent" probe result so the gate re-probes
                    // the freshly-restarted server rather than re-reading -1.
                    inspyrenetProbeState = 0;
                    inspyrenetNextProbeUtc = DateTime.MinValue;
                    System.Threading.Thread.Sleep(1500);
                    LaunchComfyUI();
                }
                catch (Exception e) { Log.Warning("Avatar: ComfyUI restart-for-node failed: " + e.Message); }
                finally { inspyrenetRestartInProgress = false; }
            });
            t.IsBackground = true;
            t.Name = "AvatarComfyRestartForNode";
            t.Start();
        }

        // Restart a ComfyUI instance we own so it rescans models/checkpoints +
        // models/vae and registers files added AFTER startup (a lazily-downloaded
        // style checkpoint/VAE). ComfyUI caches its model lists at launch, so
        // without this the first gen on a new model fails validation
        // ("ckpt_name ... not in list"). Synchronous: kills, relaunches, waits up
        // to waitMs for /system_stats. Returns false (no-op) when we don't own the
        // process — a later fresh launch scans the new files anyway.
        public static bool RestartManagedComfyForModelSync(int waitMs)
        {
            if (managedComfyPid == 0 || !IsManagedComfyAlive()) return false;
            try
            {
                Log.Message("Avatar: restarting managed ComfyUI so it registers the newly-downloaded model...");
                KillManagedComfyUI();
                lastConfirmedAlive = DateTime.MinValue;
                comfyLaunchAttempted = false;
                System.Threading.Thread.Sleep(1500);
                LaunchComfyUI();
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
                while (DateTime.UtcNow < deadline)
                {
                    if (CheckComfyUIAlive()) break;
                    System.Threading.Thread.Sleep(2000);
                }
            }
            catch (Exception e) { Log.Warning("Avatar: ComfyUI restart-for-model failed: " + e.Message); }
            return true;
        }

        // Directory.Move throws "identical roots" when source and destination are
        // on different Windows drives. We extract to %TEMP% (usually C:) but the
        // user may have ComfyUI on D:, so we copy recursively and delete the source.
        private static void CopyDirectoryRecursive(string source, string dest)
        {
            System.IO.Directory.CreateDirectory(dest);
            foreach (string file in System.IO.Directory.GetFiles(source))
            {
                string destFile = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(file));
                System.IO.File.Copy(file, destFile, true);
            }
            foreach (string subdir in System.IO.Directory.GetDirectories(source))
            {
                string destSub = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(subdir));
                CopyDirectoryRecursive(subdir, destSub);
            }
        }

        // Background-thread install. Optional onComplete fires on the main
        // thread with (success, message). Idempotent — concurrent calls coalesce
        // via inspyrenetInstallInProgress.
        public static void InstallInspyrenetNodeAsync(Action<bool, string> onComplete, bool isManualRetry = false)
        {
            if (inspyrenetInstallInProgress)
            {
                LongEventHandler.ExecuteWhenFinished(() => onComplete?.Invoke(false, "install already in progress"));
                return;
            }
            // A manual retry from Mod Options clears the auto-retry give-up gate
            // so the user can try again after fixing connectivity / disk space.
            if (isManualRetry)
            {
                inspyrenetFailCount = 0;
                inspyrenetNextRetryUtc = DateTime.MinValue;
            }
            // A (re)install invalidates any prior registration confirmation — force
            // the gate to re-probe the running server once the new node is in place.
            inspyrenetNodeRegisteredConfirmed = false;
            inspyrenetRestartForNodeAttempted = false;
            inspyrenetImportFailureReported = false;
            inspyrenetProbeState = 0;
            inspyrenetNextProbeUtc = DateTime.MinValue;
            string portable = GetPortablePath();
            if (string.IsNullOrEmpty(portable))
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    Messages.Message("Set ComfyUI Portable folder first (Auto-detect or Browse).", MessageTypeDefOf.RejectInput, historical: false);
                    onComplete?.Invoke(false, "ComfyUI portable not set");
                });
                return;
            }
            inspyrenetInstallInProgress = true;
            try
            {
                Messages.Message("Avatar - Personas: installing ComfyUI-Inspyrenet-Rembg node in the background (~10s download + pip install)...",
                    MessageTypeDefOf.NeutralEvent, historical: false);
            }
            catch { }

            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                bool ok = false;
                string msg = "";
                string nodeDir = null;
                try
                {
                    string customNodesDir = System.IO.Path.Combine(portable, "ComfyUI", "custom_nodes");
                    nodeDir = System.IO.Path.Combine(customNodesDir, InspyrenetNodeFolderName);
                    if (System.IO.Directory.Exists(nodeDir) &&
                        System.IO.File.Exists(System.IO.Path.Combine(nodeDir, "__init__.py")))
                    {
                        // Already there — still need to verify pip dep is present.
                        // Be safe: re-run pip; transparent_background install is idempotent
                        // and only a few seconds if already satisfied.
                        Log.Message("Avatar: ComfyUI-Inspyrenet-Rembg folder already present — verifying transparent_background dep...");
                    }
                    else
                    {
                        System.IO.Directory.CreateDirectory(customNodesDir);
                        // Writable-temp probe, not raw GetTempPath — Unity's inherited
                        // TMP/TEMP can point at an unusable dir (see ApplyPipEnv).
                        string tmpBase = ResolveWritableTempDir(GetEmbeddedPython());
                        if (string.IsNullOrEmpty(tmpBase)) tmpBase = System.IO.Path.GetTempPath();
                        string tempZip = System.IO.Path.Combine(tmpBase, "avatar_inspyrenet_" + Guid.NewGuid().ToString("N") + ".zip");
                        string tempExtract = System.IO.Path.Combine(tmpBase, "avatar_inspyrenet_x_" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            Log.Message("Avatar: downloading ComfyUI-Inspyrenet-Rembg from " + InspyrenetNodeZipUrl);
                            // Force TLS 1.2 — Unity Mono defaults to TLS 1.0, which GitHub now refuses.
                            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
                            using (System.Net.WebClient wc = new System.Net.WebClient())
                            {
                                wc.Headers.Add("User-Agent", "Avatar-Personas-Mod/1.0 (RimWorld; +https://github.com)");
                                wc.DownloadFile(InspyrenetNodeZipUrl, tempZip);
                            }
                            long zipSize = new System.IO.FileInfo(tempZip).Length;
                            Log.Message("Avatar: downloaded ComfyUI-Inspyrenet-Rembg zip (" + (zipSize / 1024) + " KB). Extracting...");

                            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract);
                            string extractedRoot = System.IO.Path.Combine(tempExtract, InspyrenetZipRootName);
                            if (!System.IO.Directory.Exists(extractedRoot))
                            {
                                string[] dirs = System.IO.Directory.GetDirectories(tempExtract);
                                if (dirs.Length == 1) extractedRoot = dirs[0];
                                else throw new Exception("ZIP layout unexpected: found " + dirs.Length + " subdirs in extract root");
                            }
                            if (System.IO.Directory.Exists(nodeDir))
                            {
                                try { System.IO.Directory.Delete(nodeDir, true); } catch { }
                            }
                            CopyDirectoryRecursive(extractedRoot, nodeDir);
                            try { System.IO.Directory.Delete(extractedRoot, true); } catch { }
                            Log.Message("Avatar: ComfyUI-Inspyrenet-Rembg extracted to " + nodeDir);
                        }
                        finally
                        {
                            try { if (System.IO.File.Exists(tempZip)) System.IO.File.Delete(tempZip); } catch { }
                            try { if (System.IO.Directory.Exists(tempExtract)) System.IO.Directory.Delete(tempExtract, true); } catch { }
                        }
                    }

                    // pip install the node's requirements (just `transparent_background`
                    // for this node — single line, ~10s vs 30+ packages with the
                    // multi-model ComfyUI-RMBG). Async-drained, 10-min cap.
                    string reqFile = System.IO.Path.Combine(nodeDir, "requirements.txt");
                    lastPipFailTail = null;
                    bool pipOk;
                    if (System.IO.File.Exists(reqFile))
                    {
                        Log.Message("Avatar: pip installing ComfyUI-Inspyrenet-Rembg requirements (~10s)...");
                        pipOk = InstallNodeRequirements(reqFile);
                    }
                    else
                    {
                        // Fallback: install the dep directly if the repo doesn't ship a
                        // requirements.txt (some forks don't).
                        Log.Message("Avatar: no requirements.txt found — installing transparent_background directly.");
                        pipOk = InstallSinglePipPackage("transparent_background");
                    }

                    // Self-heal: if pip died building the legacy `wget` dependency
                    // (Python 3.13 + modern setuptools wheel-build bug), drop in a
                    // stdlib-backed wget shim so pip sees wget==3.2 already satisfied
                    // and retry once. transparent_background only calls
                    // wget.download(url, out=path), which the shim implements.
                    if (!pipOk && !string.IsNullOrEmpty(lastPipFailTail) &&
                        lastPipFailTail.IndexOf("wget", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        lastPipFailTail.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Log.Warning("Avatar: pip failed building the legacy `wget` dependency. Installing a stdlib-backed wget shim and retrying the InSPyReNet pip install...");
                        if (EnsureWgetShim(portable))
                        {
                            lastPipFailTail = null;
                            pipOk = System.IO.File.Exists(reqFile)
                                ? InstallNodeRequirements(reqFile)
                                : InstallSinglePipPackage("transparent_background");
                            if (pipOk)
                                Log.Message("Avatar: InSPyReNet deps installed after applying the wget shim.");
                        }
                    }

                    if (!pipOk)
                    {
                        // Don't mark the node as installed if pip failed — the node
                        // folder is there but its Python deps aren't, so ComfyUI's
                        // node loader will crash on import and the node won't register.
                        // Delete the folder so the next retry starts clean.
                        try { if (System.IO.Directory.Exists(nodeDir)) System.IO.Directory.Delete(nodeDir, true); } catch { }
                        string tail = lastPipFailTail;
                        string detail = string.IsNullOrEmpty(tail)
                            ? "see RimWorld log for full pip output"
                            : ("pip said: " + Tail(tail, 600));
                        throw new Exception("pip install of transparent_background failed — " + detail);
                    }

                    inspyrenetNodeInstalled = true;
                    ok = true;
                    msg = "installed";
                    // Success clears the auto-retry give-up gate. The gate will
                    // re-probe /object_info after the ComfyUI restart below to
                    // confirm the node actually registered (not just landed on disk).
                    inspyrenetFailCount = 0;
                    inspyrenetNextRetryUtc = DateTime.MinValue;
                    inspyrenetNodeRegisteredConfirmed = false;
                    inspyrenetProbeState = 0;
                    inspyrenetNextProbeUtc = DateTime.MinValue;
                    inspyrenetRestartForNodeAttempted = false;
                    inspyrenetImportFailureReported = false;

                    // Restart ComfyUI so it discovers the new custom node. Only for
                    // processes we manage; user-launched ComfyUI gets a friendly toast.
                    if (managedComfyPid != 0 && IsManagedComfyAlive())
                    {
                        inspyrenetRestartInProgress = true;
                        try
                        {
                            Log.Message("Avatar: restarting managed ComfyUI to load ComfyUI-Inspyrenet-Rembg node...");
                            KillManagedComfyUI();
                            // Reset readiness cache so the queue gate re-probes fresh.
                            lastConfirmedAlive = DateTime.MinValue;
                            comfyLaunchAttempted = false;
                            System.Threading.Thread.Sleep(1500);
                            LaunchComfyUI();
                        }
                        finally { inspyrenetRestartInProgress = false; }
                    }
                    else if (!SetupManager.Active)
                    {
                        // During zero-touch bootstrap ComfyUI hasn't been launched yet
                        // (PrewarmComfyUI starts it right after this stage and loads the
                        // node fresh), so don't tell the user to restart it manually —
                        // that contradicts the setup overlay and the success toast below.
                        LongEventHandler.ExecuteWhenFinished(() =>
                        {
                            Messages.Message("ComfyUI-Inspyrenet-Rembg installed. Restart ComfyUI manually so the new node loads.",
                                MessageTypeDefOf.NeutralEvent, historical: false);
                        });
                    }
                }
                catch (Exception e)
                {
                    msg = e.Message;
                    inspyrenetNodeInstalled = false;
                    // Back off and eventually give up so a persistent failure
                    // stops spamming the queue + the in-game message feed.
                    inspyrenetFailCount++;
                    inspyrenetNextRetryUtc = DateTime.UtcNow + TimeSpan.FromSeconds(60 * inspyrenetFailCount);
                    if (inspyrenetFailCount >= InspyrenetMaxAutoAttempts)
                        Log.Warning("Avatar: ComfyUI-Inspyrenet-Rembg install failed " + inspyrenetFailCount
                            + "x — giving up auto-retry for this session. Use the Mod Options button to retry manually. Last error: " + e);
                    else
                        Log.Warning("Avatar: ComfyUI-Inspyrenet-Rembg install failed (attempt " + inspyrenetFailCount
                            + "/" + InspyrenetMaxAutoAttempts + ", retrying after backoff): " + e);
                }
                finally
                {
                    inspyrenetInstallInProgress = false;
                    bool capturedOk = ok;
                    string capturedMsg = msg;
                    LongEventHandler.ExecuteWhenFinished(() =>
                    {
                        if (capturedOk)
                            Messages.Message("ComfyUI-Inspyrenet-Rembg installed successfully. Portrait generation will resume once ComfyUI finishes restarting.",
                                MessageTypeDefOf.TaskCompletion, historical: false);
                        else
                            Messages.Message("InSPyReNet node install failed: " + capturedMsg + ". See RimWorld log for details.",
                                MessageTypeDefOf.RejectInput, historical: false);
                        onComplete?.Invoke(capturedOk, capturedMsg);
                    });
                }
            });
            t.IsBackground = true;
            t.Name = "AvatarInspyrenetInstall";
            t.Start();
        }

        // The InSPyReNet dep `transparent_background` requires `wget` (3.2), an
        // sdist-only PyPI package whose wheel build fails on the embedded Python
        // 3.13 + modern setuptools (the egg-info `dependency_links.txt` bug). When
        // even the build-tool constraints can't rescue that build, we drop in a
        // tiny stdlib-backed `wget` module + a matching .dist-info so pip sees
        // `wget==3.2` already satisfied and never tries to build it. The shim is
        // API-compatible with the only call transparent_background makes:
        // wget.download(url, out=path).
        private static bool EnsureWgetShim(string portable)
        {
            try
            {
                if (string.IsNullOrEmpty(portable)) return false;
                string embDir = GetEmbeddedPythonDir(portable);
                if (string.IsNullOrEmpty(embDir))
                {
                    Log.Warning("Avatar: wget shim — embedded python dir not found under " + portable);
                    return false;
                }
                string sp = System.IO.Path.Combine(embDir, "Lib", "site-packages");
                if (!System.IO.Directory.Exists(sp))
                {
                    Log.Warning("Avatar: wget shim — embedded site-packages not found at " + sp);
                    return false;
                }
                string py =
                    "import os, urllib.request\n" +
                    "__version__ = '3.2'\n" +
                    "def _name_from_url(url):\n" +
                    "    n = url.split('/')[-1].split('?')[0]\n" +
                    "    return n or 'download'\n" +
                    "def detect_filename(url=None, out=None, headers=None, default='download'):\n" +
                    "    if out and not os.path.isdir(out):\n" +
                    "        return out\n" +
                    "    name = _name_from_url(url) if url else default\n" +
                    "    return os.path.join(out, name) if out else name\n" +
                    "def download(url, out=None, bar=None):\n" +
                    "    dest = detect_filename(url, out)\n" +
                    "    d = os.path.dirname(dest)\n" +
                    "    if d and not os.path.isdir(d):\n" +
                    "        os.makedirs(d, exist_ok=True)\n" +
                    "    req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'})\n" +
                    "    with urllib.request.urlopen(req) as r, open(dest, 'wb') as f:\n" +
                    "        while True:\n" +
                    "            chunk = r.read(65536)\n" +
                    "            if not chunk:\n" +
                    "                break\n" +
                    "            f.write(chunk)\n" +
                    "    return dest\n" +
                    "def bar_adaptive(*a, **k):\n" +
                    "    return ''\n" +
                    "def bar_thermometer(*a, **k):\n" +
                    "    return ''\n";
                System.IO.File.WriteAllText(System.IO.Path.Combine(sp, "wget.py"), py);

                string distInfo = System.IO.Path.Combine(sp, "wget-3.2.dist-info");
                System.IO.Directory.CreateDirectory(distInfo);
                System.IO.File.WriteAllText(System.IO.Path.Combine(distInfo, "METADATA"),
                    "Metadata-Version: 2.1\nName: wget\nVersion: 3.2\nSummary: pure python download utility (Avatar stdlib shim)\n");
                System.IO.File.WriteAllText(System.IO.Path.Combine(distInfo, "INSTALLER"), "avatar-mod\n");
                System.IO.File.WriteAllText(System.IO.Path.Combine(distInfo, "top_level.txt"), "wget\n");
                // RECORD lets pip enumerate/uninstall the dist; hashes may be blank.
                System.IO.File.WriteAllText(System.IO.Path.Combine(distInfo, "RECORD"),
                    "wget.py,,\nwget-3.2.dist-info/METADATA,,\nwget-3.2.dist-info/INSTALLER,,\nwget-3.2.dist-info/top_level.txt,,\nwget-3.2.dist-info/RECORD,,\n");
                Log.Message("Avatar: installed stdlib-backed wget shim (wget==3.2) into " + sp + " so transparent_background's deps can finish installing.");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("Avatar: failed to install wget shim: " + e.Message);
                return false;
            }
        }

        // ============================================================
        // Torch-stack protection for custom-node pip installs.
        // ============================================================
        // `transparent_background` (the package the InSPyReNet node wraps) lists
        // torch / torchvision among its deps WITHOUT pinning them. pip's resolver
        // is therefore free to "upgrade" ComfyUI portable's CUDA torch to a
        // different (frequently CPU-only) build, or bump numpy to 2.x. Either
        // breaks the ABI: torchvision then fails to import
        //   (RuntimeError: operator torchvision::nms does not exist)
        // or numpy throws
        //   (A module compiled using NumPy 1.x cannot be run in NumPy 2.x).
        // ComfyUI silently skips a custom node whose import throws, so pip exits 0,
        // the folder + __init__.py are present ("installed"), yet the node never
        // registers and every portrait 400s with
        //   "Node 'InspyrenetRembgAdvanced' not found".
        // This was the #1 real-world node-install failure.
        //
        // Fix (same approach ComfyUI-Manager uses): before installing a node's
        // requirements, snapshot the currently-installed torch family with
        // `pip freeze` and write a pip CONSTRAINTS file pinning them to those exact
        // versions (incl. the +cuXXX local tag). Passed via PIP_CONSTRAINT, it
        // forces pip to keep the existing torch/torchvision/torchaudio/xformers and
        // only install the *other* deps. numpy is held below 2.0 when the install
        // is still on 1.x so a stray transitive bump can't trigger the ABI break.
        // Returns the constraints file path, or null if we couldn't read the
        // versions (callers then install unconstrained, as before).
        private static string BuildTorchConstraintsFile(string python)
        {
            if (string.IsNullOrEmpty(python)) return null;
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    python, "-s -m pip freeze --disable-pip-version-check");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                ApplyPipEnv(psi, python); // ensure pip has a writable temp/cache
                System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
                string outp = proc.StandardOutput.ReadToEnd();
                try { proc.StandardError.ReadToEnd(); } catch { }
                if (!proc.WaitForExit(30000)) { try { proc.Kill(); } catch { } }

                List<string> pins = new List<string>();
                // Always clamp the BUILD toolchain. The InSPyReNet dep chain pulls
                // in `wget` 3.2 — an sdist-only PyPI package whose wheel build fails
                // on the embedded Python 3.13 + modern setuptools (77+):
                //   error: [Errno 2] No such file or directory:
                //     '...\\wget-3.2-py3.13.egg-info\\dependency_links.txt'
                //   ERROR: Failed building wheel for wget
                // pip applies PIP_CONSTRAINT to the PEP-517 build-isolation env too,
                // so pinning setuptools/wheel down lets the legacy egg-info build
                // succeed again. Harmless for the torch/other deps.
                pins.Add("setuptools<77");
                pins.Add("wheel<0.45");
                if (!string.IsNullOrEmpty(outp))
                {
                    foreach (string raw in outp.Replace("\r", "").Split('\n'))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0) continue;
                        string lower = line.ToLowerInvariant();
                        // Exact-pin the torch family (these carry the CUDA ABI).
                        if (lower.StartsWith("torch==") || lower.StartsWith("torchvision==") ||
                            lower.StartsWith("torchaudio==") || lower.StartsWith("xformers=="))
                        {
                            pins.Add(line);
                        }
                        // Keep numpy on the 1.x line if that's where ComfyUI is, so a
                        // transitive dep can't drag it to 2.x and break the torch ABI.
                        else if (lower.StartsWith("numpy==1."))
                        {
                            pins.Add("numpy<2");
                        }
                    }
                }

                string dir = ResolveWritableTempDir(python);
                if (string.IsNullOrEmpty(dir)) dir = System.IO.Path.GetTempPath();
                string consPath = System.IO.Path.Combine(dir, "avatar_torch_constraints.txt");
                System.IO.File.WriteAllText(consPath, string.Join("\n", pins) + "\n");
                Log.Message("Avatar: constraining node pip install (clamp build toolchain + pin existing torch stack) so pip can't break ComfyUI or fail wget's wheel build — " + string.Join("  ", pins));
                return consPath;
            }
            catch (Exception e)
            {
                Log.Warning("Avatar: could not build torch constraints (continuing unconstrained): " + e.Message);
                return null;
            }
        }

        // Returns a ' -c "<file>"' pip argument for the constraints file (see
        // BuildTorchConstraintsFile), or "" if the constraints couldn't be built.
        // MUST be passed as a quoted command-line argument, NOT via the
        // PIP_CONSTRAINT environment variable: PIP_CONSTRAINT maps to a
        // multi-value pip option and pip splits its value on WHITESPACE, so any
        // path containing spaces (e.g. the default install under
        // "...\\RimWorld by Ludeon Studios\\avatar\\...") gets cut at the first
        // space and pip dies with "Could not open requirements file" before
        // installing anything. That made the whole node install fail before
        // either protection layer could run.
        private static string TorchConstraintArg(string python)
        {
            string cons = BuildTorchConstraintsFile(python);
            return string.IsNullOrEmpty(cons) ? "" : " -c \"" + cons + "\"";
        }

        // Single-package pip install used as a fallback when a node ships no
        // requirements.txt. Same async-drain pattern as InstallNodeRequirements.
        private static bool InstallSinglePipPackage(string package)
        {
            string python = GetEmbeddedPython();
            if (string.IsNullOrEmpty(python)) return false;
            try
            {
                // never let a node dep clobber ComfyUI's torch (quoted -c arg; see TorchConstraintArg)
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    python, "-s -m pip install --disable-pip-version-check --no-input" + TorchConstraintArg(python) + " " + package);
                psi.UseShellExecute = false;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                ApplyPipEnv(psi, python);
                System.Text.StringBuilder buf = new System.Text.StringBuilder();
                System.Diagnostics.Process proc = new System.Diagnostics.Process { StartInfo = psi };
                proc.OutputDataReceived += (s, ev) => { if (ev.Data != null) lock (buf) buf.AppendLine(ev.Data); };
                proc.ErrorDataReceived += (s, ev) => { if (ev.Data != null) lock (buf) buf.AppendLine(ev.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                bool exited = proc.WaitForExit(600000);
                if (!exited) { try { proc.Kill(); } catch { } return false; }
                proc.WaitForExit();
                bool success = proc.ExitCode == 0;
                if (!success)
                {
                    string output;
                    lock (buf) output = buf.ToString();
                    lastPipFailTail = output;
                    Log.Warning("Avatar: pip install " + package + " failed (exit=" + proc.ExitCode + "):\n" + Tail(output, 4000));
                }
                return success;
            }
            catch (Exception e)
            {
                Log.Warning("Avatar: pip install " + package + " threw: " + e.Message);
                return false;
            }
        }

        // pip install -r <reqFile> via the embedded Python, async-drained,
        // 10-minute hard cap (same shape as InstallPythonDeps for consistency).
        private static bool InstallNodeRequirements(string reqFile)
        {
            string python = GetEmbeddedPython();
            if (string.IsNullOrEmpty(python)) return false;
            try
            {
                // never let a node dep clobber ComfyUI's torch (quoted -c arg; see TorchConstraintArg)
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    python, "-s -m pip install --disable-pip-version-check --no-input" + TorchConstraintArg(python) + " -r \"" + reqFile + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                ApplyPipEnv(psi, python);
                System.Text.StringBuilder stdoutSb = new System.Text.StringBuilder();
                System.Text.StringBuilder stderrSb = new System.Text.StringBuilder();
                System.Diagnostics.Process proc = new System.Diagnostics.Process();
                proc.StartInfo = psi;
                proc.OutputDataReceived += (s, ev) => { if (ev.Data != null) lock (stdoutSb) stdoutSb.AppendLine(ev.Data); };
                proc.ErrorDataReceived += (s, ev) => { if (ev.Data != null) lock (stderrSb) stderrSb.AppendLine(ev.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                bool exited = proc.WaitForExit(600000);
                if (!exited) { try { proc.Kill(); } catch { } return false; }
                proc.WaitForExit();
                bool success = proc.ExitCode == 0;
                if (!success)
                {
                    string stdout, stderr;
                    lock (stdoutSb) stdout = stdoutSb.ToString();
                    lock (stderrSb) stderr = stderrSb.ToString();
                    lastPipFailTail = (string.IsNullOrEmpty(stderr) ? stdout : stderr);
                    Log.Warning("Avatar: ComfyUI-RMBG pip install failed (exit=" + proc.ExitCode + "):\n"
                        + (string.IsNullOrEmpty(stdout) ? "" : ("stdout (tail):\n" + Tail(stdout, 4000) + "\n"))
                        + (string.IsNullOrEmpty(stderr) ? "" : ("stderr (tail):\n" + Tail(stderr, 4000))));
                }
                return success;
            }
            catch (Exception e)
            {
                Log.Warning("Avatar: ComfyUI-RMBG pip install threw: " + e.Message);
                return false;
            }
        }

        // Filesystem-only check for the InSPyReNet background-removal model.
        //
        // NOTE: the mod switched from ComfyUI-RMBG (1038lab) to
        // ComfyUI-Inspyrenet-Rembg (john-mnz). The new node is a thin wrapper
        // around the `transparent_background` PyPI package — it does NOT store
        // weights in ComfyUI/models/RMBG/INSPYRENET/. Instead,
        // transparent_background downloads its checkpoint on first use into a
        // package-specific cache (typically ~/.transparent-background/ or the
        // torch hub cache under the user's profile).
        //
        // We check the most common Windows cache paths so the status panel
        // accurately reflects whether the first generation will trigger a
        // ~360 MB download (which can time out behind firewalls / in regions
        // where HuggingFace/GitHub are slow or blocked).
        //
        // Returns one of:
        //   "downloaded"       — cache folder exists and contains a >100MB file
        //   "not yet downloaded (~360MB, downloads on first portrait — requires internet)"
        //   "ComfyUI portable not set"
        // Cheap (a few stat calls), safe to call every frame.
        public static string CheckInSPyReNetModel()
        {
            string portable = GetPortablePath();
            if (string.IsNullOrEmpty(portable))
                return "ComfyUI portable not set";

            // The transparent_background package typically caches under the
            // user's home directory. On Windows that's C:\Users\<name>\.
            string userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            string[] candidateDirs = new string[]
            {
                System.IO.Path.Combine(userProfile, ".transparent-background"),
                System.IO.Path.Combine(userProfile, ".cache", "transparent-background"),
                System.IO.Path.Combine(userProfile, "AppData", "Local", "transparent-background"),
                // Fallback: inside the embedded python environment's site-packages
                // (spelling-tolerant; GetEmbeddedPythonDir may return null — the
                // loop below skips non-existent entries safely).
                System.IO.Path.Combine(GetEmbeddedPythonDir(portable) ?? System.IO.Path.Combine(portable, "python_embeded"), "Lib", "site-packages", "transparent_background"),
                // Legacy path from the old ComfyUI-RMBG node — if the user already
                // placed something here, report it so they know it's the wrong spot.
                System.IO.Path.Combine(portable, "ComfyUI", "models", "RMBG", "INSPYRENET"),
            };

            foreach (string dir in candidateDirs)
            {
                if (!System.IO.Directory.Exists(dir))
                    continue;
                try
                {
                    string[] files = System.IO.Directory.GetFiles(dir, "*", System.IO.SearchOption.AllDirectories);
                    foreach (var f in files)
                    {
                        try
                        {
                            if (new System.IO.FileInfo(f).Length > 50L * 1024 * 1024)
                                return "downloaded";
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return "not yet downloaded (~360MB, downloads on first portrait — requires internet)";
        }

        public static string CheckComfyUIModels()
        {
            // Checkpoint + LoRA are now configurable in ModSettings (review #10).
            // Read from settings instead of hardcoded names.
            string checkpointFile = AvatarSettings.DefaultComfyCheckpoint;
            string loraFile = AvatarSettings.DefaultComfyLora;
            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                if (m != null)
                {
                    if (!string.IsNullOrEmpty(m.settings.comfyCheckpoint)) checkpointFile = m.settings.comfyCheckpoint;
                    if (!string.IsNullOrEmpty(m.settings.comfyLora)) loraFile = m.settings.comfyLora;
                }
            }
            catch { }
            string portable = GetPortablePath();
            if (string.IsNullOrEmpty(portable))
                return "ComfyUI portable folder is not set — set it in Mod Options first.";
            string comfyRoot = System.IO.Path.Combine(portable, "ComfyUI");
            string cp = System.IO.Path.Combine(comfyRoot, "models", "checkpoints", checkpointFile);
            string lp = string.IsNullOrEmpty(loraFile) ? null : System.IO.Path.Combine(comfyRoot, "models", "loras", loraFile);
            bool checkpointFound = System.IO.File.Exists(cp);
            bool loraFound = string.IsNullOrEmpty(loraFile) || System.IO.File.Exists(lp);
            Log.Message("Avatar: Checking checkpoint: " + cp + " exists=" + checkpointFound);
            if (lp != null) Log.Message("Avatar: Checking lora: " + lp + " exists=" + System.IO.File.Exists(lp));
            if (checkpointFound && loraFound)
                return "All models found";
            List<string> missing = new List<string>();
            if (!checkpointFound) missing.Add(checkpointFile);
            if (!loraFound) missing.Add(loraFile);
            return "Missing in " + comfyRoot + "\\models: " + string.Join(", ", missing);
        }

        // Last watcher subprocess stderr (review #13). When InSPyReNet / RMBG / a
        // checkpoint isn't right, the workflow throws inside ComfyUI — the watcher
        // sees the error in /history and dumps it to stderr, which lands here.
        // Surfaced in the status panel so the user doesn't have to alt-tab to the
        // RimWorld log to see what went wrong. Cleared on next successful gen.
        public static string LastWatcherErrorTail = null;

        // Builds the JSON config blob that gets passed to comfy_watcher.py as its
        // 4th CLI argument. Lets users swap checkpoint / lora / denoise via
        // ModSettings without editing the embedded Python script. Returns "{}"
        // if settings aren't yet loaded — the script treats an empty/missing blob
        // as "use the hardcoded defaults".
        private static string BuildWatcherConfigJson()
        {
            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                if (m == null) return "{}";
                AvatarSettings s = m.settings;
                System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                // Resolve the active style. RimWorld style (spec == null) keeps
                // using the existing user-tunable comfy* fields verbatim (output
                // unchanged). Every other style is an "advanced" SDXL bundle from
                // PortraitStyles: its own checkpoint (+ optional VAE), LoRA OFF,
                // its own prompts + sampler/cfg, input upscale, and the cleaner
                // black-bg cutout. denoise + resolution come from the shared
                // sliders (realisticDenoise / realisticGenHeight).
                PortraitStyleSpec spec = PortraitStyles.Get(s.portraitStyle);
                bool advanced = spec != null;
                string checkpoint = advanced ? spec.checkpointFile : s.comfyCheckpoint;
                string loraName   = advanced ? "" : s.comfyLora;                  // "" => skip the LoraLoader
                float  denoise    = advanced ? s.realisticDenoise : s.comfyDenoise;
                string negative   = advanced ? spec.negativePrompt : s.comfyNegativePrompt;
                string positive   = advanced ? spec.positivePrompt : null;        // null => keep watcher default
                // Advanced styles use a slightly firmer matte threshold to shed
                // the faintest dark hair wisps; stylized keeps the user's value.
                float  bgThreshold = advanced ? 0.2f : s.comfyBgThreshold;

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("{");
                bool first = true;
                if (!string.IsNullOrEmpty(checkpoint))
                {
                    sb.Append("\\\"checkpoint\\\":\\\"").Append(JsonEscape(checkpoint)).Append("\\\"");
                    first = false;
                }
                if (loraName != null) // empty string = disable lora
                {
                    if (!first) sb.Append(",");
                    sb.Append("\\\"lora_name\\\":\\\"").Append(JsonEscape(loraName)).Append("\\\"");
                    first = false;
                }
                if (denoise > 0f)
                {
                    if (!first) sb.Append(",");
                    sb.Append("\\\"denoise\\\":").Append(denoise.ToString(inv));
                    first = false;
                }
                // Background-removal threshold (InspyrenetRembgAdvanced). 0.0
                // is a valid value (keep all soft alpha) so we always send it
                // — no "> 0" gate.
                if (!first) sb.Append(",");
                sb.Append("\\\"rmbg_threshold\\\":").Append(bgThreshold.ToString(inv));
                first = false;
                // SDXL negative prompt. Skip the send if it's empty so the
                // watcher's hardcoded default kicks in instead.
                if (!string.IsNullOrEmpty(negative))
                {
                    sb.Append(",\\\"negative_prompt\\\":\\\"").Append(JsonEscape(negative)).Append("\\\"");
                }
                // Ultra-realistic only: override the trailing positive style
                // block + sampler/cfg tuned for RealVisXL. RimWorld style leaves
                // these untouched so the watcher keeps its stylized defaults.
                if (advanced)
                {
                    if (!string.IsNullOrEmpty(positive))
                        sb.Append(",\\\"positive_prompt\\\":\\\"").Append(JsonEscape(positive)).Append("\\\"");
                    // Optional external VAE (e.g. Pony V6's baked VAE is washed
                    // out, so it ships sdxl_vae.safetensors). The watcher adds a
                    // VAELoader and routes encode/decode through it.
                    if (!string.IsNullOrEmpty(spec.vaeFile))
                        sb.Append(",\\\"vae_name\\\":\\\"").Append(JsonEscape(spec.vaeFile)).Append("\\\"");
                    if (spec.prependPositive)
                        sb.Append(",\\\"prepend_positive\\\":true");
                    sb.Append(",\\\"cfg\\\":").Append(spec.cfg.ToString(inv));
                    sb.Append(",\\\"sampler\\\":\\\"").Append(spec.sampler).Append("\\\"");
                    sb.Append(",\\\"scheduler\\\":\\\"").Append(spec.scheduler).Append("\\\"");
                    // Upscale the img2img INPUT so SDXL runs near native
                    // resolution (much sharper faces). Height drives it; width is
                    // derived from the 480x576 (5:6) source aspect, snapped to a
                    // multiple of 8 (SDXL VAE requirement). 576 = native, no upscale.
                    int targetH = s.realisticGenHeight;
                    if (targetH < 576) targetH = 576;
                    if (targetH > 1152) targetH = 1152;
                    targetH = (targetH / 64) * 64;
                    if (targetH > 576)
                    {
                        int targetW = ((int)System.Math.Round(targetH * 480.0 / 576.0 / 8.0)) * 8;
                        sb.Append(",\\\"upscale_input\\\":true");
                        sb.Append(",\\\"upscale_width\\\":").Append(targetW);
                        sb.Append(",\\\"upscale_height\\\":").Append(targetH);
                    }
                }
                // Alpha post-processing. hole_fill is shared. For realistic we
                // tune the cutout to kill the dark matting halo seen on photoreal
                // hair: no edge dilation (don't expand the matte into the black
                // backdrop), no source-silhouette floor (it forces the AI's dark
                // hair-on-black edge opaque = hard fringe), and decontaminate the
                // soft edge colors against black (defringe).
                sb.Append(",\\\"hole_fill\\\":").Append(s.comfyHoleFill ? "true" : "false");
                sb.Append(",\\\"edge_dilate_px\\\":").Append(advanced ? 0 : s.comfyEdgeDilation);
                if (advanced)
                {
                    sb.Append(",\\\"source_alpha_floor\\\":false");
                    sb.Append(",\\\"decontaminate_bg\\\":true");
                }
                sb.Append("}");
                return sb.ToString();
            }
            catch { return "{}"; }
        }
        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public static System.Diagnostics.Process NewProcess(string image, string prompts)
        {
            string python = GetEmbeddedPython();
            if (string.IsNullOrEmpty(python))
                throw new System.InvalidOperationException(
                    "ComfyUI Portable folder is not configured.\n"
                    + "Open Mod Options → Avatar - Personas and click Auto-detect or Browse "
                    + "to point the mod at your ComfyUI_windows_portable folder."
                );

            bool alive = CheckComfyUIAlive();
            if (!alive)
            {
                // Kick off auto-launch if enabled, then surface a friendly retry message
                // instead of blocking the UI thread waiting for ComfyUI to come up.
                EnsureComfyUIRunning();
                if (comfyLaunchAttempted)
                    throw new System.InvalidOperationException(
                        "ComfyUI is starting up in a console window. "
                        + "Please wait ~30-60 seconds for it to finish loading, then try again."
                    );
                throw new System.InvalidOperationException(
                    "Cannot connect to ComfyUI at http://127.0.0.1:8188\n"
                    + "Enable 'Auto-launch ComfyUI when generating portraits' in Mod Options, "
                    + "or run ComfyUI manually."
                );
            }

            if (!EnsurePythonDepsInstalled())
                throw new System.InvalidOperationException(
                    "Avatar - Personas is installing Python dependencies (pillow, requests) "
                    + "in the background. Portrait generation will resume automatically once it finishes."
                );

            string script = ExtractScript();
            // 4th positional CLI arg: JSON config override blob. comfy_watcher.py
            // parses this and overrides its hardcoded CONFIG entries (checkpoint,
            // lora_name, denoise). Wrapped in \"...\" because we're going through
            // cmd.exe argument parsing.
            string configJson = BuildWatcherConfigJson();
            System.Diagnostics.Process process = new ();
            process.StartInfo.FileName = python;
            process.StartInfo.Arguments = "-s " + string.Format("\"{0}\" \"{1}\" \"{2}\" \"{3}\"", script, image, prompts, configJson);
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.EnableRaisingEvents = true;
            // Dumps stderr to RimWorld log AND keeps a rolling tail in LastWatcherErrorTail
            // for the status panel (review #13). The tail is cleared on successful exit.
            System.Text.StringBuilder errBuf = new System.Text.StringBuilder("AI-gen process failed with the following error:");
            process.ErrorDataReceived += new System.Diagnostics.DataReceivedEventHandler((sender, e) =>
            {
                if (e.Data != null) lock (errBuf) errBuf.AppendLine("  " + e.Data);
            });
            process.Exited += new EventHandler((sender, e) =>
            {
                string captured;
                lock (errBuf) captured = errBuf.ToString();
                if (process.ExitCode != 0)
                {
                    Log.Error(captured);
                    LastWatcherErrorTail = Tail(captured, 800);
                }
                else
                {
                    LastWatcherErrorTail = null;
                }
            });
            return process;
        }
    }

    public class AIGenPromptDef : Def
    {
        public string prompt;
        public string overrides = "";
    }

    public class AIAvatarManager : AvatarManager
    {
        public bool useVanillaPortrait = false;
        private Texture2D vanillaPortrait;
        public override Texture2D GetAvatar(bool allowStatic = true)
        {
            if (useVanillaPortrait)
            {
                if (vanillaPortrait == null)
                {
                    RenderTexture active = RenderTexture.active;
                    RenderTexture.active = PortraitsCache.Get(pawn, new Vector2(160, 160), Rot4.South, renderHeadgear: drawHeadgear, renderClothes: drawClothes);
                    float offset = 160 * mod.settings.aiGenVanillaPortraitOffset;
                    vanillaPortrait = new (80, 96);
                    vanillaPortrait.SetPixels(new Color[80*96]);
                    vanillaPortrait.ReadPixels(new Rect(60, offset, 80, 96), 0, 0);
                    vanillaPortrait.Apply();
                    RenderTexture.active = active;
                }
                return vanillaPortrait;
            }
            return base.GetAvatar(allowStatic);
        }
        public override void ClearCachedAvatar()
        {
            if (vanillaPortrait != null)
            {
                UnityEngine.Object.Destroy(vanillaPortrait);
                vanillaPortrait = null;
            }
            base.ClearCachedAvatar();
        }
    }

    public class Prompts_Window : Window
    {
        private AIAvatarManager manager;
        protected string curPrompts;
        public override Vector2 InitialSize => new Vector2(800f, 240f);
        public Prompts_Window(Pawn pawn, bool drawHeadgear = false, bool drawClothes = true)
        {
            manager = new ();
            manager.SetPawn(pawn);
            manager.SetBGColor(new Color(0,0,0,0));
            manager.drawHeadgear = drawHeadgear;
            manager.drawClothes = drawClothes;
            manager.SetCheckDowned(false);
            curPrompts = manager.GetPrompts();
            doCloseX = true;
            draggable = true;
            forcePause = true;
        }
        public override void DoWindowContents(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, rect.width, 35f), "Prompts");
            Text.Font = GameFont.Small;
            GUI.DrawTexture(new Rect(0f, 35f, 100f, 120f), manager.GetAvatar(false));
            #if !v1_3
            if (manager.useVanillaPortrait)
            {
                float oldOffset = AvatarManager.mod.settings.aiGenVanillaPortraitOffset;
                Widgets.HorizontalSlider(new Rect(0f, 170f, 100f, 20f), ref AvatarManager.mod.settings.aiGenVanillaPortraitOffset, new FloatRange(0, 1), "Offset");
                if (AvatarManager.mod.settings.aiGenVanillaPortraitOffset != oldOffset)
                {
                    AvatarManager.mod.settings.Write();
                    manager.ClearCachedAvatar();
                }
            }
            #endif
            curPrompts = Widgets.TextArea(new Rect(120f, 35f, rect.width / 2f + 60f, InitialSize.y - 95f), curPrompts);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(120f, InitialSize.y - 60f, rect.width / 2f + 60f, 20f), "The base prompts (\"front portrait\" etc.) can be set in the mod settings.");
            Text.Font = GameFont.Small;
            bool enterPressed = false;
            if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                enterPressed = true;
                Event.current.Use();
            }
            bool drawHeadgear = manager.drawHeadgear;
            bool drawClothes = manager.drawClothes;
            bool useVanillaPortrait = manager.useVanillaPortrait;
            bool toggled = false;
            Widgets.CheckboxLabeled(new Rect(rect.width / 2f + 200f, 35f, rect.width / 2f - 200f, 30f), "Use vanilla portrait", ref useVanillaPortrait);
            Widgets.CheckboxLabeled(new Rect(rect.width / 2f + 200f, 65f, rect.width / 2f - 200f, 30f), "Draw headgear", ref drawHeadgear, !drawClothes);
            Widgets.CheckboxLabeled(new Rect(rect.width / 2f + 200f, 95f, rect.width / 2f - 200f, 30f), "Draw clothes", ref drawClothes);
            manager.useVanillaPortrait = useVanillaPortrait;
            if (drawHeadgear != manager.drawHeadgear)
            {
                manager.drawHeadgear = drawHeadgear;
                toggled = true;
            }
            if (drawClothes != manager.drawClothes)
            {
                manager.drawClothes = drawClothes;
                toggled = true;
            }
            if (toggled)
            {
                curPrompts = manager.GetPrompts();
            }
            if (Widgets.ButtonText(new Rect(rect.width / 2f + 200f, 135f, rect.width / 2f - 200f, 35f), "Accept") || enterPressed)
            {
                if (curPrompts.Length > 0)
                {
                    try
                    {
                        System.Diagnostics.Process process = AIGen.NewProcess(manager.SaveToStaticPortrait(), curPrompts);
                        process.Start();
                        Messages.Message("AI-gen process launched", MessageTypeDefOf.TaskCompletion, historical: false);
                        Find.WindowStack.TryRemove(this);
                        process.BeginErrorReadLine();
                    }
                    catch (System.InvalidOperationException e)
                    {
                        Messages.Message(e.Message, MessageTypeDefOf.RejectInput, historical: false);
                    }
                    // Handles the case where the process failed to start
                    catch (System.ComponentModel.Win32Exception e)
                    {
                        Messages.Message("AI-gen process failed to start", MessageTypeDefOf.RejectInput, historical: false);
                        Log.Error("AI-gen process failed to start:\n  " + e.Message);
                    }
                }
                else
                {
                    Messages.Message("Prompts cannot be empty", MessageTypeDefOf.RejectInput, historical: false);
                }
                Event.current.Use();
            }
        }
        public override void PostClose()
        {
            manager.ClearCachedAvatar();
        }
    }
}
