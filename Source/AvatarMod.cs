using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Verse;
#if !(v1_3 || v1_4)
using LudeonTK;
#endif
using RimWorld;
using HarmonyLib;

namespace Avatar
{
    public class AvatarMod : Mod
    {
        public AvatarSettings settings;

        public static Dictionary<string, Texture2D> cachedTextures = new ();

        public static AvatarManager mainManager = new ();
        public static Dictionary<Pawn, AvatarManager> colonistBarManagers = new ();
        public static Dictionary<Pawn, AvatarManager> questTabManagers = new ();
        // ============================================================
        // Cross-thread shared state — backed by ConcurrentDictionary so
        // Process.Exited callbacks (ThreadPool threads) can mutate these
        // while the main UI thread reads them every frame. Using a plain
        // HashSet<int> here was a latent bug: HashSet is NOT thread-safe
        // and concurrent Add/Remove during an internal resize can throw
        // or return torn results. byte is a one-byte sentinel value;
        // we only care about key presence.
        // ============================================================

        // autoGenTriggered: pawns we've already tried to auto-generate for
        // (either succeeded → portrait file on disk, OR in-flight). Stops
        // the periodic safety scan from re-enqueueing the same pawn forever.
        public static ConcurrentDictionary<int, byte> autoGenTriggered = new ConcurrentDictionary<int, byte>();

        // pendingPortraitPawnIds: pawns currently in the queue OR mid-process.
        // ColonistBarPatch's spinner Postfix reads this every frame; the
        // ConcurrentDictionary tolerates Exited-handler mutations on a
        // ThreadPool thread without throwing.
        public static ConcurrentDictionary<int, byte> pendingPortraitPawnIds = new ConcurrentDictionary<int, byte>();

        // failedAttempts: per-pawn retry counter. After MaxRetryAttempts hard
        // failures (subprocess exit != 0 OR exit 0 with no output file), we
        // STOP auto-retrying that pawn — a permanently-failing pawn would
        // otherwise cycle through the 20s safety scan forever. Cleared on
        // manual right-click → Regenerate so the user can override.
        public static ConcurrentDictionary<int, int> failedAttempts = new ConcurrentDictionary<int, int>();
        public const int MaxRetryAttempts = 3;

        // hiddenPawns: "don't draw the main inspect-pane avatar for this pawn".
        // Main-thread only (toggled via right-click float menu, read by UIPatch),
        // so HashSet is fine. Persisted across save/load via AvatarGameComponent.
        public static HashSet<int> hiddenPawns = new HashSet<int>();

        // === Convenience accessors for the concurrent sets ===
        // Use these from new code instead of touching the dicts directly —
        // they document intent ("mark this pawn pending") and centralize the
        // byte-sentinel boilerplate.
        // pendingCount mirrors pendingPortraitPawnIds.Count but as a plain
        // volatile int. ConcurrentDictionary.Count acquires ALL internal
        // segment locks on every read, and the spinner Postfix reads the
        // count once per colonist per IMGUI pass as its hot-path early-out.
        // Keeping a manually-maintained counter turns that into a single
        // field read. Only MarkPending/UnmarkPending mutate the dict, so
        // bumping the counter exactly when TryAdd/TryRemove succeed keeps
        // them in lockstep. Interlocked guards against the Process.Exited
        // ThreadPool callbacks racing the main thread.
        private static int pendingCount = 0;
        public static bool MarkPending(int id)
        {
            if (pendingPortraitPawnIds.TryAdd(id, 0))
            {
                System.Threading.Interlocked.Increment(ref pendingCount);
                return true;
            }
            return false;
        }
        public static bool UnmarkPending(int id)
        {
            byte _;
            if (pendingPortraitPawnIds.TryRemove(id, out _))
            {
                System.Threading.Interlocked.Decrement(ref pendingCount);
                return true;
            }
            return false;
        }
        public static bool IsPending(int id) => pendingPortraitPawnIds.ContainsKey(id);
        public static int PendingCount => pendingCount;

        public static bool MarkAutoGen(int id) => autoGenTriggered.TryAdd(id, 0);
        public static bool UnmarkAutoGen(int id) { byte _; return autoGenTriggered.TryRemove(id, out _); }
        public static bool IsAutoGenMarked(int id) => autoGenTriggered.ContainsKey(id);

        // RecordFailedAttempt increments the per-pawn failure counter and
        // returns the NEW count. Callers compare against MaxRetryAttempts to
        // decide whether to give up on the pawn entirely.
        public static int RecordFailedAttempt(int id) =>
            failedAttempts.AddOrUpdate(id, 1, (k, v) => v + 1);
        public static int GetFailedAttempts(int id)
        {
            int v;
            return failedAttempts.TryGetValue(id, out v) ? v : 0;
        }
        public static void ClearFailedAttempts(int id) { int _; failedAttempts.TryRemove(id, out _); }
        public static int FailedPermanentlyCount
        {
            get
            {
                int n = 0;
                foreach (var kv in failedAttempts) if (kv.Value >= MaxRetryAttempts) n++;
                return n;
            }
        }
        public static void ResetAllFailedPawns()
        {
            // Lift each permanently-failed pawn out of all three tracking
            // sets so the next safety scan re-enqueues them from scratch.
            List<int> ids = new List<int>();
            foreach (var kv in failedAttempts) if (kv.Value >= MaxRetryAttempts) ids.Add(kv.Key);
            foreach (int id in ids)
            {
                ClearFailedAttempts(id);
                UnmarkAutoGen(id);
                UnmarkPending(id);
            }
        }

        private Vector2 scrollPosition = Vector2.zero;
        // each manager stores a pawn, if any, and the avatar texture

        [DebugAction("Avatar", "Reload Textures")]
        public static void ClearCachedTextures()
        {
            foreach (KeyValuePair<string, Texture2D> kvp in cachedTextures)
                UnityEngine.Object.Destroy(kvp.Value);
            cachedTextures.Clear();
            ClearCachedAvatars();
        }

        public static void ClearCachedAvatars()
        {
            mainManager.ClearCachedAvatar();
            foreach (KeyValuePair<Pawn, AvatarManager> kvp in colonistBarManagers)
                kvp.Value.ClearCachedAvatar();
            colonistBarManagers.Clear();
            foreach (KeyValuePair<Pawn, AvatarManager> kvp in questTabManagers)
                kvp.Value.ClearCachedAvatar();
            questTabManagers.Clear();
        }

        // Review #3: cached AvatarManagers used to leak forever — every raider
        // / visitor / trader pinned a Pawn reference and held a Texture2D
        // canvas. Called every 20s from AutoPortraitGenerator's safety scan.
        // Eviction criteria: the pawn is null, destroyed, or discarded — i.e.
        // there's no in-game reason to keep rendering them. A still-alive
        // off-map pawn (caravan / quest holding pen) stays cached so re-opening
        // the inspect pane on them doesn't re-render from scratch.
        public static void SweepDeadPawnManagers()
        {
            int colonistEvicted = SweepOneDict(colonistBarManagers);
            int questEvicted = SweepOneDict(questTabManagers);
            if (colonistEvicted + questEvicted > 0)
            {
                Log.Message("Avatar: swept " + (colonistEvicted + questEvicted)
                    + " dead-pawn avatar manager(s) (" + colonistEvicted + " colonist-bar, "
                    + questEvicted + " quest-tab).");
            }
        }
        private static int SweepOneDict(Dictionary<Pawn, AvatarManager> dict)
        {
            List<Pawn> dead = null;
            foreach (KeyValuePair<Pawn, AvatarManager> kvp in dict)
            {
                Pawn p = kvp.Key;
                if (p == null || p.Destroyed || p.Discarded)
                {
                    if (dead == null) dead = new List<Pawn>();
                    dead.Add(p);
                }
            }
            if (dead == null) return 0;
            foreach (Pawn p in dead)
            {
                try { dict[p].ClearCachedAvatar(); } catch { }
                dict.Remove(p);
            }
            return dead.Count;
        }

        public Texture2D GetTexture(string texPath, bool fallback=true)
        {
            if (string.IsNullOrEmpty(texPath)) return null;
            if (!cachedTextures.ContainsKey(texPath))
            {
                string path = Content.RootDir+"/Assets/"+texPath+".png";
                if (!System.IO.File.Exists(path))
                { // fallback to RW texture manager
                    return fallback ? ContentFinder<Texture2D>.Get(texPath) : null;
                }
                Texture2D newTexture = new (1, 1);
                newTexture.LoadImage(System.IO.File.ReadAllBytes(path));
                cachedTextures[texPath] = newTexture;
            }
            return cachedTextures[texPath];
        }

        public AvatarMod(ModContentPack content) : base(content)
        {
            AvatarManager.mod = this;
            settings = GetSettings<AvatarSettings>();
        }

        public override string SettingsCategory() => "Avatar - Personas";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect viewRect = inRect;
            // Bumped to 1600 to accommodate the status panel, 4 primary action
            // buttons, model-install guide, and utility buttons. Over-estimating
            // is harmless; the scroll view just shows blank space past the actual
            // content.
            Rect contentRect = new Rect(0f, 0f, viewRect.width - 16f, 1700f);

            Widgets.BeginScrollView(viewRect, ref scrollPosition, contentRect);

            Listing_Standard listingStandard = new Listing_Standard()
            {
                boundingRect = contentRect,
                ColumnWidth = contentRect.width / 2 - 17,
            };
            listingStandard.Begin(contentRect);
            #if v1_3
            listingStandard.Label("Avatar size");
            settings.avatarWidth = (float)Math.Round(
                listingStandard.Slider(settings.avatarWidth, 80f, 400f));
            #else
            settings.avatarWidth = (float)Math.Round(
                listingStandard.SliderLabeled("Avatar size", settings.avatarWidth, 80f, 400f));
            #endif
            string samplePath = "UI/AvatarSampleS";
            Texture2D avatar = GetTexture(samplePath);
            avatar.filterMode = FilterMode.Point;
            float width = settings.avatarWidth;
            float height = width*avatar.height/avatar.width;
            listingStandard.ButtonImage(avatar, width, height);
            listingStandard.NewColumn();
            // Hoisted up here so the status panel and action buttons can show
            // the resolved portable folder path without re-resolving mid-render.
            string resolvedPortable = AIGen.GetPortablePath();

            // ============================================================
            // STATUS PANEL — single-glance health view of the AI pipeline.
            // ============================================================
            Text.Font = GameFont.Medium;
            listingStandard.Label((TaggedString)"AI portrait pipeline status");
            Text.Font = GameFont.Small;
            // 1) Portable folder
            string sPortable = (!string.IsNullOrEmpty(resolvedPortable) && AIGen.IsValidPortableFolder(resolvedPortable))
                ? "[OK] ComfyUI Portable: " + resolvedPortable
                : "[X]  ComfyUI Portable: not configured";
            listingStandard.Label((TaggedString)sPortable);
            // 2) Python dependencies
            string sDeps;
            if (AIGen.DepsBusy) sDeps = "[...] Python deps: checking / installing";
            else if (AIGen.DepsInstalled) sDeps = "[OK] Python deps: pillow, requests";
            else if (AIGen.DepsChecked) sDeps = "[X]  Python deps: missing — first generation will install them";
            else sDeps = "[?]  Python deps: not yet checked (will check on first portrait)";
            listingStandard.Label((TaggedString)sDeps);
            // 3) ComfyUI server (cached — NEVER probe here every frame)
            string sServer = AIGen.IsConfirmedReady
                ? "[OK] ComfyUI server: running on 127.0.0.1:8188"
                : (AIGen.ComfyLaunchAttempted
                    ? "[...] ComfyUI server: starting (wait 30-60s after launch)"
                    : "[X]  ComfyUI server: not running");
            listingStandard.Label((TaggedString)sServer);
            // 4) Required models — cheap filesystem check (no logging, safe per-render).
            string sModels = "[?]  Required models: ComfyUI not set up yet";
            if (!string.IsNullOrEmpty(resolvedPortable) && AIGen.IsValidPortableFolder(resolvedPortable))
            {
                string mroot = System.IO.Path.Combine(resolvedPortable, "ComfyUI", "models");
                bool ckptOk = System.IO.File.Exists(System.IO.Path.Combine(mroot, "checkpoints", settings.comfyCheckpoint));
                bool loraOk = string.IsNullOrEmpty(settings.comfyLora) || System.IO.File.Exists(System.IO.Path.Combine(mroot, "loras", settings.comfyLora));
                sModels = (ckptOk && loraOk)
                    ? "[OK] Required models: SDXL checkpoint + RimWorld LoRA present"
                    : (SetupManager.Active ? "[...] Required models: downloading..." : "[X]  Required models: missing");
            }
            listingStandard.Label((TaggedString)sModels);
            // 4b) ComfyUI-Inspyrenet-Rembg custom node (auto-installed by the mod on first portrait).
            string sNode = AIGen.CheckInspyrenetNodeInstalled();
            // "installed" on disk is not the same as "loaded in ComfyUI" — reflect
            // the running-server registration state so the panel can't claim ready
            // while portrait prompts are 400ing with "node not found".
            string sNodeIcon;
            string sNodeLabel;
            if (AIGen.InspyrenetInstallInProgress)
            {
                sNodeIcon = "...";
                sNodeLabel = "installing / restarting ComfyUI...";
            }
            else if (sNode != "installed")
            {
                sNodeIcon = "X ";
                sNodeLabel = sNode;
            }
            else if (AIGen.InspyrenetNodeLoadFailed)
            {
                sNodeIcon = "X ";
                sNodeLabel = "on disk, but ComfyUI did NOT load it — restart ComfyUI; if it persists a Python dependency conflict is blocking it (see ComfyUI console)";
            }
            else if (AIGen.InspyrenetNodeRegistered)
            {
                sNodeIcon = "OK";
                sNodeLabel = "installed and loaded in ComfyUI";
            }
            else
            {
                sNodeIcon = "...";
                sNodeLabel = "on disk — verifying ComfyUI loaded it...";
            }
            listingStandard.Label((TaggedString)("[" + sNodeIcon + "] ComfyUI-Inspyrenet-Rembg custom node: " + sNodeLabel));
            // When the node isn't installed because pip failed, show the actual
            // pip output here (not just "see RimWorld log") so the real cause is
            // visible/copyable in-game. The node folder gets deleted on pip
            // failure, so sNode == "not installed" + a non-null tail == pip failed.
            if (sNode != "installed" && !AIGen.InspyrenetInstallInProgress && !string.IsNullOrEmpty(AIGen.LastPipFailTail))
            {
                string tail = AIGen.LastPipFailTail;
                if (tail.Length > 1200) tail = "..." + tail.Substring(tail.Length - 1200);
                listingStandard.Label((TaggedString)("      pip output (the real cause \u2014 copy this if asking for help):\n" + tail));
            }
            // 4c) InSPyReNet background-removal model (auto-downloads via transparent_background on first use).
            // Filesystem-only check, no HTTP — cheap to call every render.
            string sBg = AIGen.CheckInSPyReNetModel();
            string sBgIcon = sBg == "downloaded" ? "OK" : (sBg.StartsWith("not yet") ? "..." : "X ");
            listingStandard.Label((TaggedString)("[" + sBgIcon + "] Background-removal model (InSPyReNet): " + sBg));
            // 5) Live counters: queue + permanent failures + last generation
            int pendingCount = AvatarMod.PendingCount;
            int permaFailedShown = AvatarMod.FailedPermanentlyCount;
            string sQueue = "Queue: " + pendingCount + " pending";
            if (permaFailedShown > 0) sQueue += ", " + permaFailedShown + " gave up after " + AvatarMod.MaxRetryAttempts + " retries";
            listingStandard.Label((TaggedString)sQueue);
            if (!string.IsNullOrEmpty(AIGen.LastGenerationLog))
                listingStandard.Label((TaggedString)("Last generation: " + AIGen.LastGenerationLog));
            if (AIGen.GenerationsThisSession > 0)
                listingStandard.Label((TaggedString)("Session: " + AIGen.GenerationsThisSession + " portraits, avg " + AIGen.AverageGenerationSeconds.ToString("F1") + "s each"));
            // 6) Last watcher subprocess error tail (review #13).
            // Surfaced here so the user can diagnose ComfyUI workflow failures
            // (missing models, wrong RMBG model name, etc.) without alt-tabbing
            // to the RimWorld log. Cleared on the next successful generation.
            if (!string.IsNullOrEmpty(AIGen.LastWatcherErrorTail))
            {
                listingStandard.Label((TaggedString)("[!]  Last subprocess error:\n" + AIGen.LastWatcherErrorTail));
            }
            listingStandard.GapLine();
            // ============================================================
            // END STATUS PANEL
            // ============================================================

            // ============================================================
            // ZERO-TOUCH SETUP — the recommended first-time path. Downloads
            // ComfyUI portable + the SDXL checkpoint, installs the bundled
            // RimWorld LoRA + Python deps + the InSPyReNet node. Progress is
            // drawn bottom-left of the screen (see SetupOverlay).
            // ============================================================
            if (SetupManager.Active)
            {
                listingStandard.Label((TaggedString)("Setting up ComfyUI: " + SetupManager.StageLabel +
                    (string.IsNullOrEmpty(SetupManager.Detail) ? "" : "  \u2014  " + SetupManager.Detail)));
                if (listingStandard.ButtonText("Cancel setup"))
                {
                    SetupManager.Cancel();
                    Messages.Message("Avatar - Personas: cancelling setup...", MessageTypeDefOf.NeutralEvent, historical: false);
                }
            }
            else
            {
                listingStandard.Label((TaggedString)(SetupManager.IsFullySetUp()
                    ? "ComfyUI is fully set up and ready."
                    : "ComfyUI is not set up yet — it installs automatically on game load, or download everything now:"));
                // Runs the exact same end-to-end pipeline the first game load runs:
                // find/install ComfyUI, copy the bundled LoRA, download the SDXL
                // checkpoint, install Python deps + the InSPyReNet node. Skips
                // whatever already exists; never re-downloads ComfyUI if present.
                if (listingStandard.ButtonText("Download all dependencies"))
                {
                    SetupManager.StartBootstrap();
                    Messages.Message("Avatar - Personas: downloading all dependencies. Watch the progress bar in the bottom-left of the screen.", MessageTypeDefOf.NeutralEvent, historical: false);
                }
            }
            listingStandard.CheckboxLabeled("Auto-download & set up ComfyUI on game load if missing", ref settings.autoSetup,
                "When on, the mod installs ComfyUI portable + the SDXL checkpoint automatically the first time you load a game with the mod active.");
            listingStandard.Label((TaggedString)"Install location (leave empty for the default RimWorld data folder)", -1, "Where ComfyUI portable (~7 GB) and the SDXL checkpoint (~6.9 GB) get installed. Change this BEFORE running setup if you want them on a different drive.");
            settings.comfyInstallDir = listingStandard.TextEntry(settings.comfyInstallDir);
            // Normalize a whitespace-only entry back to "" so it can't be
            // persisted and later Trim() to "" inside the setup pipeline
            // (which would throw "Path cannot be the empty string or all
            // whitespace."). Empty = use the default data folder.
            if (string.IsNullOrWhiteSpace(settings.comfyInstallDir))
                settings.comfyInstallDir = "";

            // --- Manual override: reuse an existing ComfyUI portable so it isn't
            // installed twice. The mod already auto-detects one on load; this is
            // for installs in non-standard locations auto-detect misses. ---
            listingStandard.Gap(4f);
            listingStandard.Label((TaggedString)"Already have ComfyUI portable? Point the mod at it (avoids a second copy):");
            string curPortable = AIGen.GetPortablePath();
            if (!string.IsNullOrEmpty(curPortable) && AIGen.IsValidPortableFolder(curPortable))
                listingStandard.Label((TaggedString)("Using: " + curPortable));
            else if (!string.IsNullOrEmpty(settings.comfyPortablePath))
                listingStandard.Label((TaggedString)("Set, but not a valid ComfyUI portable: " + settings.comfyPortablePath));
            else
                listingStandard.Label((TaggedString)"Not set — the mod will auto-detect an existing install on load, or install one if none is found.");
            if (listingStandard.ButtonText("Browse for existing ComfyUI portable folder"))
            {
                string picked = AIGen.BrowseForPortableFolder();
                if (!string.IsNullOrEmpty(picked))
                {
                    if (AIGen.IsValidPortableFolder(picked))
                    {
                        settings.comfyPortablePath = picked;
                        AIGen.ResetPortableCache();
                        Messages.Message("ComfyUI portable folder set: " + picked, MessageTypeDefOf.TaskCompletion, historical: false);
                        // Immediately fill in the RimWorld-specific bits the user's
                        // ComfyUI is missing: copy the bundled LoRA, download the
                        // SDXL checkpoint (only if absent), install Python deps +
                        // the InSPyReNet node. The pipeline adopts the chosen
                        // portable and NEVER re-downloads ComfyUI itself.
                        if (!SetupManager.Active && !SetupManager.IsFullySetUp())
                        {
                            SetupManager.StartBootstrap();
                            Messages.Message("Installing any missing AI model files into your ComfyUI — watch the progress bar in the bottom-left.", MessageTypeDefOf.NeutralEvent, historical: false);
                        }
                    }
                    else
                    {
                        Messages.Message("That folder isn't a valid ComfyUI portable (missing python_embeded\\python.exe).", MessageTypeDefOf.RejectInput, historical: false);
                    }
                }
            }
            if (!string.IsNullOrEmpty(settings.comfyPortablePath) && listingStandard.ButtonText("Clear (use auto-detect / auto-install instead)"))
            {
                settings.comfyPortablePath = "";
                AIGen.ResetPortableCache();
                Messages.Message("Cleared. The mod will auto-detect or auto-install ComfyUI on next load.", MessageTypeDefOf.NeutralEvent, historical: false);
            }
            listingStandard.GapLine();

            // ============================================================
            // PRIMARY ACTION BUTTONS — manual / advanced setup fallbacks.
            // ============================================================
            // 3b) Install ComfyUI-Inspyrenet-Rembg node — manual button alongside
            // the auto-trigger from the first portrait. Hidden once installed;
            // shows progress while busy.
            string rmbgState = AIGen.CheckInspyrenetNodeInstalled();
            if (AIGen.InspyrenetInstallInProgress)
            {
                listingStandard.Label((TaggedString)"Installing ComfyUI-Inspyrenet-Rembg node (~10s download + ~10s pip install)...");
            }
            else if (rmbgState != "installed")
            {
                if (listingStandard.ButtonText("Install ComfyUI-Inspyrenet-Rembg node automatically"))
                {
                    AIGen.InstallInspyrenetNodeAsync(null, isManualRetry: true);
                }
            }
            // 4) Test Generate: end-to-end pipeline test with a neutral image.
            string testButtonLabel = AIGen.TestGenInProgress
                ? "Test Generate... (running — check status panel above)"
                : "Test Generate...";
            if (!AIGen.TestGenInProgress && listingStandard.ButtonText(testButtonLabel))
            {
                AIGen.RunTestGeneration();
            }
            else if (AIGen.TestGenInProgress)
            {
                listingStandard.Label((TaggedString)testButtonLabel);
            }
            listingStandard.GapLine();
            // ============================================================
            // END PRIMARY ACTION BUTTONS
            // ============================================================

            // ============================================================
            // COMFYUI SERVER STATUS & MANUAL LAUNCH
            // ============================================================
            resolvedPortable = AIGen.GetPortablePath();
            // Cached server status read (no per-frame HTTP blocking).
            AIGen.TryConfirmComfyReady();
            bool comfyAlive = AIGen.IsConfirmedReady;
            listingStandard.Label((TaggedString)("ComfyUI server: " + (comfyAlive ? "running on http://127.0.0.1:8188" : "not running")));
            if (!comfyAlive && !string.IsNullOrEmpty(resolvedPortable) && AIGen.IsValidPortableFolder(resolvedPortable) && listingStandard.ButtonText("Launch ComfyUI now"))
            {
                if (AIGen.LaunchComfyUI())
                    Messages.Message("ComfyUI is launching — wait ~30-60 seconds for it to finish loading.", MessageTypeDefOf.NeutralEvent, historical: false);
                else
                    Messages.Message("Failed to launch ComfyUI. Check that run_nvidia_gpu.bat or run_cpu.bat exist in the portable folder.", MessageTypeDefOf.RejectInput, historical: false);
            }
            listingStandard.GapLine();
            // ============================================================
            // END COMFYUI SERVER STATUS & MANUAL LAUNCH
            // ============================================================

            // ============================================================
            // BEHAVIOUR CHECKBOXES
            // ============================================================
            listingStandard.CheckboxLabeled("Hide main avatar", ref settings.hideMainAvatar);
            listingStandard.CheckboxLabeled("Show avatars in colonist bar", ref settings.showInColonistBar);
            if (settings.showInColonistBar && !ModCompatibility.ColonyGroups_Loaded)
            {
                #if v1_3
                listingStandard.Label("Colonist bar size adjustment");
                settings.showInColonistBarSizeAdjust = (float)(
                    listingStandard.Slider(settings.showInColonistBarSizeAdjust, 0f, 10f));
                #else
                settings.showInColonistBarSizeAdjust = (float)(
                    listingStandard.SliderLabeled("Colonist bar size adjustment", settings.showInColonistBarSizeAdjust, 0f, 10f));
                #endif
            }
            if (ModCompatibility.CCMBar_Loaded && listingStandard.ButtonText("Refresh colonist bar"))
            {
                AccessTools.Method("ColoredMoodBar13.MoodPatch:CGMarkColonistsDirty").Invoke(null, new object[] {null});
            }
            listingStandard.CheckboxLabeled("Show avatars in quest tab (experimental)", ref settings.showInQuestTab);
            listingStandard.CheckboxLabeled("Auto-generate AI portraits for new pawns", ref settings.autoGeneratePortraits);
            listingStandard.CheckboxLabeled("Auto-launch ComfyUI when generating portraits", ref settings.autoLaunchComfyUI);
            listingStandard.GapLine();
            // ============================================================
            // END BEHAVIOUR CHECKBOXES
            // ============================================================

            // ============================================================
            // PORTRAIT STYLE
            // ============================================================
            listingStandard.Label((TaggedString)"Portrait style", -1,
                "Choose how AI portraits are rendered. Each style keeps its portraits in a "
                + "separate folder, so switching only ever shows the selected style's images; "
                + "pawns with no portrait in the selected style fall back to pixel-art until "
                + "regenerated (auto on the next scan, or via the buttons below). The non-RimWorld "
                + "styles each download a one-time SDXL model (~6.5–7 GB) on first selection.");
            if (listingStandard.RadioButton("RimWorld style (stylized pixel-art AI)", settings.portraitStyle == PortraitStyle.RimWorld))
                SelectPortraitStyle(PortraitStyle.RimWorld);
            if (listingStandard.RadioButton("Ultra realistic (HD photo — RealVisXL)", settings.portraitStyle == PortraitStyle.UltraRealistic))
                SelectPortraitStyle(PortraitStyle.UltraRealistic);
            if (listingStandard.RadioButton("Anime — Pony Diffusion V6 XL", settings.portraitStyle == PortraitStyle.PonyDiffusion))
                SelectPortraitStyle(PortraitStyle.PonyDiffusion);
            if (listingStandard.RadioButton("Anime — Animagine XL 4.0", settings.portraitStyle == PortraitStyle.AnimagineXL))
                SelectPortraitStyle(PortraitStyle.AnimagineXL);
            PortraitStyleSpec selSpec = PortraitStyles.Get(settings.portraitStyle);
            if (selSpec != null)
            {
                if (SetupManager.StyleCheckpointInstalled(selSpec))
                {
                    listingStandard.Label((TaggedString)("   " + selSpec.displayName + " model: installed."));
                }
                else if (SetupManager.Active && SetupManager.Stage == SetupStage.DownloadModel)
                {
                    listingStandard.Label((TaggedString)("   " + selSpec.displayName + " model: downloading — see the bottom-left progress card."));
                }
                else if (string.IsNullOrEmpty(AIGen.GetPortablePath()))
                {
                    listingStandard.Label((TaggedString)("   " + selSpec.displayName + " model: ComfyUI must finish installing first (let RimWorld style set up once)."));
                }
                else if (listingStandard.ButtonText("Download " + selSpec.displayName + " model (~" + SetupManager.BytesText(selSpec.TotalBytes) + ")"))
                {
                    if (!SetupManager.Active)
                    {
                        SetupManager.EnsureStyleCheckpoint(settings.portraitStyle);
                        Messages.Message("Downloading the " + selSpec.displayName + " model (one-time).",
                            MessageTypeDefOf.NeutralEvent, historical: false);
                    }
                }
                settings.realisticDenoise = listingStandard.SliderLabeled(
                    "AI transformation strength: " + settings.realisticDenoise.ToString("0.00"),
                    settings.realisticDenoise, 0.5f, 0.95f, 0.5f,
                    "Higher = more transformed and further from the pixel-art source (may drift from the pawn's exact hair/clothes). Lower = closer to the source.");
                float genH = listingStandard.SliderLabeled(
                    "AI generation resolution: " + settings.realisticGenHeight + "px (higher = sharper, slower)",
                    settings.realisticGenHeight, 576f, 1152f, 0.5f,
                    "SDXL models render much better faces near 1024px. 576 = native (fastest), 1024 = recommended, 1152 = max quality (slowest, most VRAM). Snaps to multiples of 64.");
                settings.realisticGenHeight = Mathf.Clamp(Mathf.RoundToInt(genH / 64f) * 64, 576, 1152);
            }
            listingStandard.GapLine();
            // ============================================================
            // END PORTRAIT STYLE
            // ============================================================

            // ============================================================
            // BASE PROMPTS
            // ============================================================
            listingStandard.Label((TaggedString)"Base prompts", -1, "Added before all other prompts.\n{age}, {gender}, {lifestage} will be replaced with their values.");
            settings.aiGenPreamble = listingStandard.TextEntry(settings.aiGenPreamble);
            if (listingStandard.ButtonText("Reset prompts to default"))
            {
                settings.aiGenPreamble = settings.aiGenPreambleDefault;
            }
            listingStandard.GapLine();
            // ============================================================
            // END BASE PROMPTS
            // ============================================================

            // ============================================================
            // UTILITY BUTTONS
            // ============================================================
            // One-click recovery for any colonist whose portrait somehow never
            // generated (autoGen marker leaked, ComfyUI was killed mid-gen,
            // save-load mid-flight, etc.). Walks every player-faction humanlike
            // on every map and re-enqueues anyone without a file on disk —
            // bypasses the retry-budget cap and the autoGenTriggered cache.
            if (listingStandard.ButtonText("Regenerate missing portraits for all colonists"))
            {
                int n = AutoPortraitGenerator.RegenerateMissingForAllColonists();
                Messages.Message(
                    n == 0
                        ? "All colonists already have portraits — nothing to enqueue."
                        : ("Enqueued " + n + " missing portrait" + (n == 1 ? "" : "s") + ". The queue will pick them up over the next few seconds."),
                    n == 0 ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.TaskCompletion,
                    historical: false);
            }
            int permaFailed = AvatarMod.FailedPermanentlyCount;
            if (permaFailed > 0 && listingStandard.ButtonText("Reset " + permaFailed + " failed pawn" + (permaFailed == 1 ? "" : "s") + " (retry from scratch)"))
            {
                AvatarMod.ResetAllFailedPawns();
                Messages.Message("Reset " + permaFailed + " failed pawn(s). The periodic scan will re-enqueue them within 20 seconds.", MessageTypeDefOf.TaskCompletion, historical: false);
            }
            listingStandard.GapLine();
            if (listingStandard.ButtonText("Open portraits folder"))
            {
                AIGen.OpenAvatarFolder();
            }
            listingStandard.End();
            Widgets.EndScrollView();
            base.DoSettingsWindowContents(inRect);
        }

        // Switch portrait style from the Mod Options radios. Clears cached avatars
        // so the new style's folder resolves immediately, and kicks the one-time
        // model download for advanced styles (when ComfyUI is installed, the model
        // isn't present, and nothing else is downloading).
        private void SelectPortraitStyle(PortraitStyle style)
        {
            if (settings.portraitStyle == style) return;
            settings.portraitStyle = style;
            AvatarMod.ClearCachedAvatars();
            PortraitStyleSpec spec = PortraitStyles.Get(style);
            if (spec != null && !SetupManager.Active && !SetupManager.StyleCheckpointInstalled(spec)
                && !string.IsNullOrEmpty(AIGen.GetPortablePath()))
            {
                SetupManager.EnsureStyleCheckpoint(style);
                Messages.Message("Downloading the " + spec.displayName + " model (one-time, ~"
                    + SetupManager.BytesText(spec.TotalBytes) + "). Progress shows in the bottom-left card.",
                    MessageTypeDefOf.NeutralEvent, historical: false);
            }
        }

        public Texture2D GetColonistBarAvatar(Pawn pawn, bool drawHeadgear, bool drawClothes)
        {
            if (!colonistBarManagers.TryGetValue(pawn, out AvatarManager manager))
            {
                manager = new ();
                manager.SetPawn(pawn);
                manager.SetBGColor(new Color(0,0,0,0));
                manager.SetCheckDowned(true);
                colonistBarManagers[pawn] = manager;
            }
            manager.drawHeadgear = drawHeadgear;
            manager.drawClothes = drawClothes;
            return manager.GetAvatar();
        }

        public Texture2D GetQuestTabAvatar(Pawn pawn)
        {
            if (!questTabManagers.ContainsKey(pawn))
            {
                AvatarManager manager = new ();
                manager.SetPawn(pawn);
                questTabManagers[pawn] = manager;
            }
            return questTabManagers[pawn].GetAvatar();
        }
    }

    [HarmonyPatch(typeof(InspectPaneUtility), nameof(InspectPaneUtility.DoTabs))]
    public static class UIPatch
    {
        static AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();

        // ============================================================
        // On-demand enqueue: fire immediately on first frame of a new
        // selection.
        //
        // Previous (broken) behavior: a 1.5s selection-stable timer
        // gated the enqueue. Failure mode the user hit: click pawn,
        // click off before 1.5s elapses → nothing ever enqueued. Real
        // problem because users routinely click pawns briefly to check
        // names / quest info / hediffs without dwelling.
        //
        // New behavior: enqueue the FIRST frame the inspect pane shows
        // a pawn we haven't already enqueued. The `lastEnqueuedPawnId`
        // per-session guard ensures we don't re-enqueue the same pawn
        // on re-selection. The downstream `pendingAutoGen` / `hasStatic`
        // checks ensure we don't double-queue or regenerate pawns that
        // already have portraits.
        //
        // The 30-pawn raid drop concern from the original gating is
        // mitigated by: (a) retry budget caps each pawn at 3 attempts,
        // (b) ComfyUI serializes generation internally so wall time
        // bounds at ~5s per pawn, (c) users physically can't click 30
        // pawns rapidly without intending to.
        // ============================================================
        private static int lastSeenPawnId = -1;
        private static int lastEnqueuedPawnId = -1;

        private static Vector2 relPos(Vector2 absPos, Rect rect)
        {
            return new((absPos.x-rect.x)/rect.width, 1f-(absPos.y-rect.y)/rect.height);
        }
        public static void Postfix(IInspectPane pane)
        {
            if (pane is not MainTabWindow_Inspect inspectPanel) return;
            Pawn pawn = null;
            if (inspectPanel.SelThing is Pawn selectedPawn)
                pawn = selectedPawn;
            else if (inspectPanel.SelThing is Corpse corpse)
                pawn = corpse.InnerPawn;
            if (pawn != null && pawn.RaceProps.Humanlike)
            {
                AvatarManager manager = AvatarMod.mainManager;
                manager.SetPawn(pawn);
                manager.drawClothes = !ModCompatibility.ModdedNudity(pawn);

                // Use the new rename-stable path (<thingIDNumber>.png). Lazy
                // legacy-file migration is handled inside GetPortraitPath, so
                // a colonist who got their portrait under the old <name>_<id>
                // naming will still resolve to the same image.
                string portraitPath = AvatarManager.GetPortraitPath(pawn);
                bool hasStatic = System.IO.File.Exists(portraitPath);
                bool pendingAutoGen = AvatarMod.IsAutoGenMarked(pawn.thingIDNumber);

                // Track which pawn the inspect pane is currently showing.
                // Updates lastSeenPawnId so a re-selection of the same pawn
                // after viewing someone else still passes the per-streak guard.
                if (pawn.thingIDNumber != lastSeenPawnId)
                {
                    lastSeenPawnId = pawn.thingIDNumber;
                }

                // On-demand generation: fire immediately on the first frame
                // we see a pawn with no portrait that isn't already queued.
                // No selection-hold delay — the previous 1.5s gate was
                // dropping enqueues when users clicked then clicked off
                // quickly. EnsureOnMap handles corpse InnerPawns (whose .Map
                // is null) by falling through; we'd lose those, so corpse
                // generation relies on the user double-checking via right-click.
                if (!hasStatic && !pendingAutoGen
                    && lastEnqueuedPawnId != pawn.thingIDNumber)
                {
                    Map pMap = pawn.Map;
                    if (pMap != null)
                    {
                        AutoPortraitGenerator generator = AutoPortraitGenerator.EnsureOnMap(pMap);
                        generator.EnqueueOnDemand(pawn);
                        lastEnqueuedPawnId = pawn.thingIDNumber;
                    }
                }

                if (!mod.settings.hideMainAvatar && inspectPanel.OpenTabType is null)
                {
                    bool isHidden = AvatarMod.hiddenPawns.Contains(pawn.thingIDNumber);
                    Texture2D avatar = manager.GetAvatar();
                    float width = mod.settings.avatarWidth;
                    float height = width*avatar.height/avatar.width;
                    float left = 0f;
                    if (ModCompatibility.Portraits_Loaded && pawn.ageTracker.AgeBiologicalYearsFloat >= 7 && !pawn.Dead)
                    {
                        // move avatar to the right of the portrait
                        left = 30f;
                        if (ModCompatibility.PortraitShown(pawn)) left += 185f;
                    }
                    Rect rect = new(left, inspectPanel.PaneTopY - InspectPaneUtility.TabHeight - height, width, height);
                    // Always draw the avatar (when not user-hidden). The manager
                    // returns the AI static portrait when one exists, else the
                    // pixel-art render — so during generation the pawn shows
                    // pixel-art with the corner spinner, then atomically swaps
                    // to the AI portrait when the file lands on disk. The old
                    // `hasStatic || !pendingAutoGen` gate blanked the inspect-pane
                    // avatar mid-generation, which the user wanted reverted.
                    if (!isHidden)
                    {
                        GUI.DrawTexture(rect, avatar);
                    }
                    // Bottom-left mini-spinner during portrait generation.
                    // Same 8-dot animation as the colonist-bar overlay, but:
                    //  - no scrim (don't tint the portrait)
                    //  - small fixed size (12px radius, 3px dots) regardless of
                    //    settings.avatarWidth so it stays unobtrusive on large
                    //    avatar settings
                    //  - anchored to the bottom-left with 8px inset padding
                    // Gated on per-pawn pending state AND the same isHidden
                    // guard as the avatar itself — if the user has hidden this
                    // pawn's avatar, don't draw spinner feedback either.
                    if (!isHidden && AvatarMod.IsPending(pawn.thingIDNumber))
                    {
                        const float spinnerRadius = 10f;
                        const float spinnerDotSize = 3f;
                        const float spinnerInset = 8f;
                        Color savedColor = GUI.color;
                        try
                        {
                            Vector2 spinnerCenter = new Vector2(
                                rect.xMin + spinnerInset + spinnerRadius,
                                rect.yMax - spinnerInset - spinnerRadius);
                            ColonistBar_SpinnerOverlay_Patch.DrawDotSpinner(
                                spinnerCenter, spinnerRadius, spinnerDotSize);
                        }
                        finally { GUI.color = savedColor; }
                    }
                    if (Event.current.type == EventType.MouseDown && Mouse.IsOver(rect)
                        && (isHidden || manager.CheckCursor(relPos(Event.current.mousePosition, rect))))
                    { // capture mouse click
                        if (Event.current.button == 0 && !isHidden) // leftbutton
                            manager.drawHeadgear = !manager.drawHeadgear;
                        else if (Event.current.button == 1) // rightbutton
                            Find.WindowStack.Add(manager.GetFloatMenu());
                        Event.current.Use();
                    }
                }
            }
        }
    }

    // basically borrrowed from Portraits of the Rim
    [HarmonyPatch(typeof(MainTabWindow_Quests), nameof(MainTabWindow_Quests.DoFactionInfo))]
    public static class QuestWindowPatch
    {
        static AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();
        public static void Prefix(ref MainTabWindow_Quests __instance, Rect rect, ref float curY)
        {
            if (mod.settings.showInQuestTab)
            {
                List<Pawn> pawns = new();
                foreach (var part in __instance.selected.PartsListForReading)
                {
                    List<Pawn> partPawns;
                    if (part is QuestPart_PawnsArrive pawnsArrive)
                        partPawns = pawnsArrive.pawns?.Where(p => p.RaceProps.Humanlike).ToList();
                    else if (part is QuestPart_ExtraFaction extraFaction)
                        partPawns = extraFaction.affectedPawns?.Where(p => p.RaceProps.Humanlike).ToList();
                    #if v1_3 || v1_4 || v1_5
                    else if (part is QuestPart_Hyperlinks hyperlinks)
                        partPawns = hyperlinks.pawns?.Where(p => p.RaceProps.Humanlike).ToList();
                    #endif
                    else
                        partPawns = part.QuestLookTargets.Where(x => x.Thing is Pawn p && p.RaceProps.Humanlike).Select(x => x.Thing).Cast<Pawn>().ToList();
                    foreach (Pawn pawn in partPawns)
                    {
                        if (!pawns.Contains(pawn))
                            pawns.Add(pawn);
                    }
                }
                if (pawns.Count > 0)
                {
                    float width = pawns.Count > 4 ? 80f : 120f;
                    float height = width*1.2f;
                    for (int i = 0; i < pawns.Count; i++)
                    {
                        Rect avatarRect = new(rect.width - (width+5)*(i%5+1), curY+15+(height+5)*(i/5), width, height);
                        GUI.DrawTexture(avatarRect, mod.GetQuestTabAvatar(pawns[i]));
                        if (Mouse.IsOver(avatarRect))
                        {
                            TooltipHandler.TipRegion(avatarRect, pawns[i].LabelCap);
                        }
                    }
                    curY += 10f+(height+5f)*(float)Math.Ceiling(pawns.Count/5f);
                }
            }
        }
    }

    // disable some of vanilla's portrait updating
    [HarmonyPatch(typeof(Verse.AI.JobDriver), nameof(Verse.AI.JobDriver.SetInitialPosture))]
    public static class AvatarJobDriverPatch
    {
        private static MethodInfo oldMethod = AccessTools.Method(typeof(PortraitsCache), "SetDirty");
        private static MethodInfo newMethod = AccessTools.Method("AvatarJobDriverPatch:SetDirty");
        public static void SetDirty(Pawn _)
        {
            // DO NOTHING!
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
                yield return (instruction.Calls(oldMethod)) ? new CodeInstruction(OpCodes.Call, newMethod) : instruction;
        }
    }

    // Heal function calls unnecessary updates, which becomes a problem for entities with regeneration
    // Removing them might not be the best solution, but what the hell
    [HarmonyPatch(typeof(Verse.Hediff_Injury), nameof(Verse.Hediff_Injury.Heal))]
    public static class AvatarHealPatch
    {
        private static MethodInfo oldMethod = AccessTools.Method(typeof(Verse.Pawn_HealthTracker), "Notify_HediffChanged");
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
                if (instruction.Calls(oldMethod))
                {
                    // clear the stack then do a nop
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Nop);
                }
                else
                    yield return instruction;
        }
    }

    // redraw avatar whenever ingame portrait got redrawn
    [HarmonyPatch(typeof(PortraitsCache), nameof(PortraitsCache.SetDirty))]
    public static class AvatarUpdateHookPatch
    {
        public static void Postfix(Pawn pawn)
        {
            if (pawn == AvatarMod.mainManager.pawn)
                AvatarMod.mainManager.ClearCachedAvatar();
            if (AvatarMod.colonistBarManagers.ContainsKey(pawn))
                AvatarMod.colonistBarManagers[pawn].ClearCachedAvatar();
            if (AvatarMod.questTabManagers.ContainsKey(pawn))
                AvatarMod.questTabManagers[pawn].ClearCachedAvatar();
        }
    }

    // redraw avatar when pawn ages (for wrinkles)
    [HarmonyPatch(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.BirthdayBiological))]
    public static class Pawn_AgeTracker_BirthdayBiological_Patch
    {
        public static void Postfix(ref Pawn_AgeTracker __instance)
        {
            if (__instance.pawn == AvatarMod.mainManager.pawn)
                AvatarMod.mainManager.ClearCachedAvatar();
            if (AvatarMod.colonistBarManagers.ContainsKey(__instance.pawn))
                AvatarMod.colonistBarManagers[__instance.pawn].ClearCachedAvatar();
            if (AvatarMod.questTabManagers.ContainsKey(__instance.pawn))
                AvatarMod.questTabManagers[__instance.pawn].ClearCachedAvatar();
        }
    }

    // Portrait art style. RimWorld = the original stylized pixel-art-AI pipeline
    // (SDXL base + RimWorld LoRA, the shared comfy* settings). Every other value
    // is an "advanced" SDXL bundle with its own checkpoint + prompts (see
    // PortraitStyles / BuildWatcherConfigJson). New values MUST be appended at
    // the end so Scribe'd int indices stay stable across saves.
    public enum PortraitStyle
    {
        RimWorld,
        UltraRealistic,
        PonyDiffusion,
        AnimagineXL
    }

    // Immutable per-style bundle for the non-RimWorld ("advanced") styles. Drives
    // the lazy model download (SetupManager), the per-style portrait folder
    // (AvatarManager.GetPortraitDir), and the watcher config (BuildWatcherConfigJson).
    public sealed class PortraitStyleSpec
    {
        public PortraitStyle style;
        public string displayName;
        public string folderSubdir;     // under <persistentDataPath>/avatar/
        public string checkpointFile;   // lands in ComfyUI/models/checkpoints/
        public string checkpointUrl;
        public long   checkpointBytes;
        public string vaeFile;          // null/empty = use the checkpoint's baked VAE
        public string vaeUrl;
        public long   vaeBytes;
        public float  cfg;
        public string sampler;
        public string scheduler;
        public string positivePrompt;   // appended after the per-pawn description
        public string negativePrompt;
        public bool   prependPositive;   // true => positivePrompt LEADS (Pony score tags want this)
        public long TotalBytes => checkpointBytes + vaeBytes;
    }

    public static class PortraitStyles
    {
        // Shared black-backdrop suffix: gives InSPyReNet a uniform background to
        // strip and keeps the black-bg decontamination math exact. (The realistic
        // prompt already bakes its own black-backdrop phrase in.)
        private const string BgSuffix =
            ", isolated on a solid black background, simple black background, plain black backdrop";

        public static readonly PortraitStyleSpec Realistic = new PortraitStyleSpec
        {
            style = PortraitStyle.UltraRealistic,
            displayName = "RealVisXL HD",
            folderSubdir = "realistic",
            checkpointFile = AvatarSettings.RealisticCheckpointFile,
            checkpointUrl = "https://huggingface.co/SG161222/RealVisXL_V5.0/resolve/main/RealVisXL_V5.0_fp16.safetensors",
            checkpointBytes = 6938065488L,
            cfg = AvatarSettings.RealisticCfg, sampler = AvatarSettings.RealisticSampler, scheduler = AvatarSettings.RealisticScheduler,
            positivePrompt = AvatarSettings.DefaultRealisticPositivePrompt,
            negativePrompt = AvatarSettings.DefaultRealisticNegativePrompt,
        };

        // Pony Diffusion V6 XL. Requires the score_* quality tags to produce good
        // output. Ships a washed-out baked VAE, so we fetch + use sdxl_vae.
        public static readonly PortraitStyleSpec Pony = new PortraitStyleSpec
        {
            style = PortraitStyle.PonyDiffusion,
            displayName = "Pony Diffusion V6 XL",
            folderSubdir = "pony",
            checkpointFile = "ponyDiffusionV6XL_v6StartWithThisOne.safetensors",
            checkpointUrl = "https://huggingface.co/LyliaEngine/Pony_Diffusion_V6_XL/resolve/main/ponyDiffusionV6XL_v6StartWithThisOne.safetensors",
            checkpointBytes = 6938041050L,
            vaeFile = "sdxl_vae.safetensors",
            vaeUrl = "https://huggingface.co/LyliaEngine/Pony_Diffusion_V6_XL/resolve/main/sdxl_vae.safetensors",
            vaeBytes = 334641162L,
            cfg = 7.0f, sampler = "euler_ancestral", scheduler = "karras",
            // Pony is trained with the score_* quality tags at the FRONT, so the
            // style block must lead the per-pawn description.
            prependPositive = true,
            positivePrompt =
                "score_9, score_8_up, score_7_up, score_6_up, rating_safe, source_anime, "
                + "anime, anime portrait, detailed face, vibrant colors, clean lineart, "
                + "masterpiece, best quality, very aesthetic" + BgSuffix,
            negativePrompt =
                "score_6, score_5, score_4, source_furry, source_pony, source_cartoon, "
                + "rating_explicit, rating_questionable, nsfw, nude, "
                + "worst quality, low quality, lowres, jpeg artifacts, "
                + "bad anatomy, bad hands, missing fingers, extra digits, fewer digits, "
                + "text, watermark, signature, logo, username, "
                + "busy background, cluttered background, scenery, landscape, outdoors, window, "
                + "skullcap, hat, cap, headwear, headband, "
                + "deformed, mutated, distorted, disfigured, extra limbs, multiple faces, bad proportions",
        };

        // Animagine XL 4.0 (opt). Uses Cagliostro's quality-tag format. Baked VAE
        // is good, so no external VAE.
        public static readonly PortraitStyleSpec Animagine = new PortraitStyleSpec
        {
            style = PortraitStyle.AnimagineXL,
            displayName = "Animagine XL 4.0",
            folderSubdir = "animagine",
            checkpointFile = "animagine-xl-4.0-opt.safetensors",
            checkpointUrl = "https://huggingface.co/cagliostrolab/animagine-xl-4.0/resolve/main/animagine-xl-4.0-opt.safetensors",
            checkpointBytes = 6938350040L,
            cfg = 5.0f, sampler = "euler_ancestral", scheduler = "karras",
            positivePrompt =
                "anime, anime portrait, detailed face, "
                + "masterpiece, high score, great score, absurdres, very aesthetic, newest" + BgSuffix,
            negativePrompt =
                "lowres, bad anatomy, bad hands, text, error, missing finger, "
                + "extra digits, fewer digits, cropped, worst quality, low quality, "
                + "low score, bad score, average score, signature, watermark, username, blurry, "
                + "nsfw, nude, "
                + "busy background, cluttered background, scenery, landscape, outdoors, window, "
                + "skullcap, hat, cap, headwear, headband, "
                + "deformed, mutated, distorted, disfigured, extra limbs, multiple faces, bad proportions",
        };

        // RimWorld style returns null (it uses the shared comfy* settings, not a spec).
        public static PortraitStyleSpec Get(PortraitStyle s)
        {
            switch (s)
            {
                case PortraitStyle.UltraRealistic: return Realistic;
                case PortraitStyle.PonyDiffusion:  return Pony;
                case PortraitStyle.AnimagineXL:    return Animagine;
                default:                           return null;
            }
        }
    }

    public class AvatarSettings : ModSettings
    {
        // Defaults for the new ComfyUI advanced settings (kept as constants so
        // the Reset button can restore them and the field defaults stay in sync).
        public const string DefaultComfyCheckpoint = "sd_xl_base_1.0.safetensors";
        public const string DefaultComfyLora = "RimWorld_1.1-000001.safetensors";
        public const float DefaultComfyDenoise = 0.65f;
        // 0.1 = preserve everything the model isn't certain is background.
        // Clothing and hair strands survive. See comfy_watcher.py CONFIG
        // comment for the full threshold scale.
        public const float DefaultComfyBgThreshold = 0.1f;
        // Hole-fill ON by default — directly fixes the "clothes go transparent"
        // failure mode where InSPyReNet correctly identifies an SDXL-painted
        // dark blob inside the body as background, leaving a void surrounded
        // by opaque foreground. Edge-flood post-processing identifies
        // unreachable transparent islands and forces them opaque.
        public const bool DefaultComfyHoleFill = true;
        // Edge dilation now defaults to 1px. Real-world test showed InSPyReNet
        // erodes shirt silhouettes far enough to cut off shoulders entirely on
        // some pawns (notably bearded faces / busy outlines confuse the matter).
        // 1px push-out recovers the silhouette without producing visible halos.
        // Users can crank to 2-3px if erosion is still visible, or back to 0
        // if they don't want any dilation. 4-5px starts to halo noticeably.
        public const int DefaultComfyEdgeDilation = 1;
        // Default SDXL negative prompt — fights the most common portrait
        // artifacts (yarmulke-halo on bald heads, speckled skin, deformed
        // faces, watermarks, low quality). The headwear list is the killer
        // for the bald-pawn skullcap hallucination. Users can edit this in
        // mod options; same string lives in comfy_watcher.py's CONFIG as the
        // fallback when no JSON override is sent.
        public const string DefaultComfyNegativePrompt =
            "text, watermark, signature, logo, "
            + "tech on face, soft edges, "
            + "skullcap, kippah, yarmulke, beanie, hat, cap, headwear, headband, "
            + "patchy skin, blemishes, skin spots, acne, freckles, "
            + "ugly, deformed, mutated, distorted, disfigured, "
            + "extra limbs, extra heads, multiple faces, extra eyes, "
            + "asymmetric face, bad anatomy, bad proportions, "
            + "low quality, blurry, jpeg artifacts, pixelated";

        // ---- Ultra-realistic (HD photo) style bundle ----------------------
        // Selected via portraitStyle == UltraRealistic. Uses a dedicated
        // photoreal SDXL checkpoint (RealVisXL V5.0), drops the RimWorld LoRA,
        // and swaps in photographic positive/negative prompts + a RealVisXL-
        // tuned sampler/cfg. None of this touches the RimWorld-style fields.
        public const string RealisticCheckpointFile = "RealVisXL_V5.0_fp16.safetensors";
        public const float DefaultRealisticDenoise = 0.75f;
        // Realistic-only img2img generation height (px). The pawn image is
        // uploaded at 480x576; the realistic style upscales the INPUT to this
        // height (width derived from the 5:6 aspect) before VAE-encode so
        // SDXL/RealVisXL paint sharp faces near native res. 576 = no upscale.
        public const int DefaultRealisticGenHeight = 1024;
        public const float RealisticCfg = 5.0f;
        public const string RealisticSampler = "dpmpp_2m";
        public const string RealisticScheduler = "karras";
        public const string DefaultRealisticPositivePrompt =
            "RAW photo, photorealistic, ultra realistic, ultra detailed, 8k uhd, "
            + "dslr, soft natural lighting, high quality skin texture with visible pores, "
            + "sharp focus, professional studio portrait photography, "
            + "shallow depth of field, film grain, Fujifilm XT3, "
            // Plain black studio backdrop keeps the background-removal +
            // source-alpha-floor pipeline sound: the AI paints near-black outside
            // the subject, InSPyReNet strips it cleanly, and the floor's near-
            // black "AI background" gate (source_alpha_ai_bg_rgb_max) still holds.
            + "isolated on a solid black background, plain black studio backdrop";
        public const string DefaultRealisticNegativePrompt =
            "illustration, painting, drawing, cartoon, anime, comic, sketch, "
            + "cgi, 3d render, video game, stylized, cel shading, pixar, concept art, "
            + "busy background, cluttered background, scenery, landscape, outdoors, "
            + "window, furniture, props, "
            + "text, watermark, signature, logo, "
            + "skullcap, kippah, yarmulke, beanie, hat, cap, headwear, headband, "
            + "deformed, mutated, distorted, disfigured, extra limbs, extra heads, "
            + "multiple faces, extra eyes, asymmetric face, bad anatomy, bad proportions, "
            + "plastic skin, doll, waxy, low quality, blurry, jpeg artifacts";

        public float avatarWidth = 200f;
        // The next 9 fields are still consumed by RenderAvatar / Prompts_Window
        // at runtime, but are no longer exposed in the settings UI. Their
        // Scribe_Values.Look calls were removed so the user's settings XML
        // stops accumulating dead keys (see review #11). Defaults are now the
        // only values these ever take.
        public bool avatarCompression = false;
        public bool avatarScaling = true;
        public bool defaultDrawHeadgear = true;
        public bool showHairWithHeadgear = true;
        public bool hideMainAvatar = false;
        public bool showInQuestTab = true;
        public bool showInColonistBar = true;
        public float showInColonistBarSizeAdjust = 0f;
        public bool noFemaleLips = false;
        public bool noWrinkles = false;
        public bool earsOnTop = true;
        public bool noCorpseGore = false;
        public bool autoGeneratePortraits = true;
        public bool autoLaunchComfyUI = true;
        // Zero-touch bootstrap: when true (default), the mod auto-downloads and
        // installs ComfyUI portable + the SDXL checkpoint on game load if they
        // aren't already present. comfyInstallDir overrides where ComfyUI lands
        // (empty = <persistentDataPath>/avatar).
        public bool autoSetup = true;
        public string comfyInstallDir = "";

        // aiGenExecutable removed — ComfyUI script is now embedded in the mod assembly
        // aiGenPythonPath removed — embedded python now comes from ComfyUI portable
        public string comfyPortablePath = "";
        public string aiGenPreamble = "front portrait, {age}-year-old {gender} {race}, {lifestage}, ";
        public string aiGenPreambleDefault = "front portrait, {age}-year-old {gender} {race}, {lifestage}, ";
        public float aiGenVanillaPortraitOffset = 0.5f;
        // Advanced ComfyUI settings — forwarded to comfy_watcher.py as a JSON
        // blob on each invocation. Users can swap SDXL base, LoRA, or img2img
        // denoise without editing the embedded Python script.
        public string comfyCheckpoint = DefaultComfyCheckpoint;
        public string comfyLora = DefaultComfyLora;
        public float comfyDenoise = DefaultComfyDenoise;
        // Background-removal mask threshold passed to InspyrenetRembgAdvanced.
        // Lower = more conservative (keeps clothing). See AvatarSettings
        // DefaultComfyBgThreshold for the rationale.
        public float comfyBgThreshold = DefaultComfyBgThreshold;
        // SDXL negative prompt. User-editable so artifact patterns can be
        // tuned without rebuilding the mod. See AvatarSettings
        // DefaultComfyNegativePrompt for the curated default.
        public string comfyNegativePrompt = DefaultComfyNegativePrompt;
        // Alpha post-processing knobs (forwarded to comfy_watcher.py).
        public bool comfyHoleFill = DefaultComfyHoleFill;
        public int comfyEdgeDilation = DefaultComfyEdgeDilation;
        // Portrait art style. RimWorld = the original stylized pipeline (all the
        // comfy* fields above). UltraRealistic = the RealVisXL photoreal bundle
        // (RealisticCheckpointFile + DefaultRealistic* consts). realisticDenoise
        // is the img2img strength used only by the realistic style.
        public PortraitStyle portraitStyle = PortraitStyle.RimWorld;
        public float realisticDenoise = DefaultRealisticDenoise;
        public int realisticGenHeight = DefaultRealisticGenHeight;

        public override void ExposeData()
        {
            base.ExposeData();
            // Persisted: only settings actually surfaced in the UI today.
            // Removed Scribe lines for avatarCompression / avatarScaling /
            // defaultDrawHeadgear / showHairWithHeadgear / noFemaleLips /
            // noWrinkles / earsOnTop / noCorpseGore / aiGenVanillaPortraitOffset
            // (see review #11). Users with hand-edited XML for those keys will
            // revert to defaults — acceptable for a rare power-user case.
            Scribe_Values.Look(ref avatarWidth, "avatarWidth");
            Scribe_Values.Look(ref hideMainAvatar, "hideMainAvatar");
            Scribe_Values.Look(ref showInQuestTab, "showInQuestTab");
            Scribe_Values.Look(ref showInColonistBar, "showInColonistBar");
            Scribe_Values.Look(ref showInColonistBarSizeAdjust, "showInColonistBarSizeAdjust");
            Scribe_Values.Look(ref autoGeneratePortraits, "autoGeneratePortraits", true);
            Scribe_Values.Look(ref autoLaunchComfyUI, "autoLaunchComfyUI", true);
            Scribe_Values.Look(ref autoSetup, "autoSetup", true);
            Scribe_Values.Look(ref comfyInstallDir, "comfyInstallDir");
            Scribe_Values.Look(ref comfyPortablePath, "comfyPortablePath");
            Scribe_Values.Look(ref aiGenPreamble, "aiGenPreamble");
            Scribe_Values.Look(ref comfyCheckpoint, "comfyCheckpoint", DefaultComfyCheckpoint);
            Scribe_Values.Look(ref comfyLora, "comfyLora", DefaultComfyLora);
            Scribe_Values.Look(ref comfyDenoise, "comfyDenoise", DefaultComfyDenoise);
            Scribe_Values.Look(ref comfyBgThreshold, "comfyBgThreshold", DefaultComfyBgThreshold);
            Scribe_Values.Look(ref comfyNegativePrompt, "comfyNegativePrompt", DefaultComfyNegativePrompt);
            Scribe_Values.Look(ref comfyHoleFill, "comfyHoleFill", DefaultComfyHoleFill);
            Scribe_Values.Look(ref comfyEdgeDilation, "comfyEdgeDilation", DefaultComfyEdgeDilation);
            Scribe_Values.Look(ref portraitStyle, "portraitStyle", PortraitStyle.RimWorld);
            Scribe_Values.Look(ref realisticDenoise, "realisticDenoise", DefaultRealisticDenoise);
            Scribe_Values.Look(ref realisticGenHeight, "realisticGenHeight", DefaultRealisticGenHeight);
            // Clearing the avatar cache on every Scribe pass (incl. saves
            // and resolve passes) caused a per-frame texture re-render storm.
            // Only clear after an actual load. See review #4.
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                AvatarMod.mainManager.SetBGColor(new Color(0,0,0,0));
                AvatarMod.ClearCachedAvatars();
            }
        }
    }
}
