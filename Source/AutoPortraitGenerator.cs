using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using HarmonyLib;

namespace Avatar
{
    public class AutoPortraitGenerator : MapComponent
    {
        // Two queues: colony pawns (player faction) are processed first.
        // ComfyUI has its own internal queue, so we don't need to pace our
        // submissions — we fire as fast as the update loop allows.
        private Queue<Pawn> colonyQueue = new Queue<Pawn>();
        private Queue<Pawn> otherQueue = new Queue<Pawn>();
        // Realtime throttling (was tick-based). Tick-based throttling froze
        // the queue whenever the game was paused — a real problem because
        // users dev-spawn pawns while paused, watch portraits generate while
        // paused, etc. Spinners use Time.realtimeSinceStartup so they animate
        // through pause, and now the dispatch and safety scan do too.
        private float nextProcessRealtime = 0f;
        private float nextSafetyScanRealtime = 0f;
        private const float DelayRealSeconds = 0.05f;        // ~3 frames at 60 FPS
        private const float SafetyScanIntervalSeconds = 20f;
        private const float FirstScanDelaySeconds = 5f;

        // === Queue persistence note ===
        // We do NOT Scribe `generationQueue` or `AvatarMod.pendingPortraitPawnIds`.
        // We don't have to: the source of truth for "this pawn needs a portrait" is
        // "is there a .png on disk under Application.persistentDataPath/avatar/<name>.png?".
        // On every save load (and on fresh maps), Map.FinalizeInit → our FinalizeInit
        // → EnqueueExistingPawns walks every pawn and EnqueuePawn re-checks the file.
        // Pawns whose file already exists are silently skipped (autoGenTriggered flagged).
        // Pawns whose file is missing are re-queued and re-spinner'd. The 20s safety
        // scan then keeps catching anything spawned between FinalizeInit and load completion.
        // Net effect: if the user saves at pawn 12 of 50 and quits, on next load the
        // remaining 38 are re-enqueued automatically. No Scribe needed.

        public AutoPortraitGenerator(Map map) : base(map) {}

        // Shared attach helper (review #8). Map_FinalizeInit_Patch,
        // Pawn_SpawnSetup_Patch, and UIPatch's on-demand path all call this
        // instead of inlining `new + components.Add`, so FinalizeInit is
        // called consistently and EnqueueExistingPawns runs whenever the
        // generator is created. Idempotent — returns the existing component
        // if one is already attached.
        public static AutoPortraitGenerator EnsureOnMap(Map map)
        {
            if (map == null) return null;
            AutoPortraitGenerator existing = map.GetComponent<AutoPortraitGenerator>();
            if (existing != null) return existing;
            AutoPortraitGenerator generator = new AutoPortraitGenerator(map);
            map.components.Add(generator);
            // FinalizeInit isn't called automatically when we add a component
            // post-MapFillComponents — we have to fire it ourselves so the
            // initial scan + first-scan-delay both happen.
            generator.FinalizeInit();
            Log.Message("Avatar: attached AutoPortraitGenerator to map " + map.uniqueID);
            return generator;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            EnqueueExistingPawns();
            // Fire the first safety scan 5s after map ready, not 20s.
            nextSafetyScanRealtime = Time.realtimeSinceStartup + FirstScanDelaySeconds;
        }

        // Auto-enqueue: called from SpawnSetup, FinalizeInit, safety scan, and
        // SetFaction. Only colonists (player faction) are auto-queued. Non-
        // colonists must be explicitly selected by the player to get a portrait.
        public void EnqueuePawn(Pawn pawn)
        {
            if (pawn == null) { return; }
            if (!pawn.RaceProps.Humanlike) { return; }
            if (pawn.Faction != Faction.OfPlayerSilentFail) { return; }
            int pawnId = pawn.thingIDNumber;
            // Retry-budget gate: if this pawn has hit MaxRetryAttempts hard
            // failures this session, give up auto-retrying. The user can lift
            // the cap from settings ("Reset failed pawns") or right-click the
            // pawn → Regenerate, which both clear failedAttempts.
            if (AvatarMod.GetFailedAttempts(pawnId) >= AvatarMod.MaxRetryAttempts)
            {
                // Make sure we don't keep spinner-rendering this pawn or trying
                // to re-enqueue it. autoGenTriggered stays set so the safety
                // scan won't re-add. pendingPortraitPawnIds gets cleared so the
                // spinner doesn't keep rotating on a permanently-failed pawn.
                AvatarMod.UnmarkPending(pawnId);
                AvatarMod.MarkAutoGen(pawnId);
                return;
            }
            if (AvatarMod.IsAutoGenMarked(pawnId))
            {
                // Verify the marker is still valid: portrait file exists OR
                // currently in-flight. A stale marker (no file, not pending)
                // means a previous generation dropped between MarkPending and
                // process.Exited — e.g. ComfyUI was killed mid-gen by the
                // install/restart flow, or a save-load happened mid-flight.
                // Without this self-heal, the safety scan silently skips the
                // pawn forever and they show up as a "missed colonist".
                string verifyPath = AvatarManager.GetPortraitPath(pawn);
                if (System.IO.File.Exists(verifyPath)) return;
                if (AvatarMod.IsPending(pawnId)) return;
                Log.Message("Avatar: stale autoGen marker for " + pawn.LabelShortCap
                    + " (id=" + pawnId + ", no file, not pending) — clearing and re-enqueueing.");
                AvatarMod.UnmarkAutoGen(pawnId);
                // fall through to the file-exists check + enqueue path below
            }

            // If the ComfyUI Portable folder isn't configured, silently skip auto-generation
            // entirely — don't queue, don't warn per-pawn, don't mark autoGenTriggered (so
            // pawns get picked up once the user configures the folder mid-session and a new
            // pawn spawns). Surface a one-time letter so the user knows portraits are off.
            if (string.IsNullOrEmpty(AIGen.GetPortablePath()))
            {
                AIGen.NotifyComfyUIMissing();
                return;
            }
            // Ultra-realistic needs its RealVisXL checkpoint. If the one-time
            // download hasn't finished, skip WITHOUT marking/enqueuing so the
            // retry budget isn't burned before the model arrives — the periodic
            // scan re-checks and enqueues once it lands.
            if (!AIGen.SelectedStyleBackendReady()) return;

            // Use the new rename-stable path. GetPortraitPath also performs
            // a one-shot legacy migration if a <name>_<id>.png file is the
            // only existing portrait for this pawn.
            string portraitPath = AvatarManager.GetPortraitPath(pawn);
            if (System.IO.File.Exists(portraitPath))
            {
                AvatarMod.MarkAutoGen(pawnId); // mark so we don't re-check every scan
                return;
            }

            AvatarMod.MarkAutoGen(pawnId);
            // Spinner-state set: ColonistBarPatch's Postfix draws an animated
            // spinner over any pawn whose ID is in this set. Stays until the
            // python subprocess exits in ProcessPawn's Exited handler.
            AvatarMod.MarkPending(pawnId);
            // Colony pawns go to the priority queue so they never wait behind
            // a caravan of 20 raiders that all SpawnSetup'd at once.
            if (pawn.Faction == Faction.OfPlayerSilentFail)
                colonyQueue.Enqueue(pawn);
            else
                otherQueue.Enqueue(pawn);
            int q = colonyQueue.Count + otherQueue.Count;
            Log.Message("Avatar: enqueued portrait for colonist " + pawn.LabelShortCap + " (queue=" + q + ")");
        }

        // On-demand enqueue: called from the inspect pane when the player clicks
        // on ANY pawn (colonist or non-colonist) that lacks a portrait. Bypasses
        // the colonist-only gate so traders, raiders, quest pawns, etc. can be
        // generated on selection. Non-colonists go to otherQueue (lower priority).
        public void EnqueueOnDemand(Pawn pawn)
        {
            if (pawn == null) { return; }
            if (!pawn.RaceProps.Humanlike) { return; }
            int pawnId = pawn.thingIDNumber;

            if (AvatarMod.GetFailedAttempts(pawnId) >= AvatarMod.MaxRetryAttempts)
            {
                AvatarMod.UnmarkPending(pawnId);
                AvatarMod.MarkAutoGen(pawnId);
                return;
            }
            if (AvatarMod.IsAutoGenMarked(pawnId))
            {
                // Same stale-marker self-heal as EnqueuePawn: if the marker is
                // set but there's no file AND no pending generation, a previous
                // attempt was silently dropped (e.g. by the dispatch `pawn.Map
                // == map` check before that gate was removed, or ComfyUI killed
                // mid-gen). Clear the marker so we can re-attempt instead of
                // silently skipping forever — the original bug that left clicked
                // non-colonists never getting a portrait on a second click.
                string verifyPath = AvatarManager.GetPortraitPath(pawn);
                if (System.IO.File.Exists(verifyPath)) return;
                if (AvatarMod.IsPending(pawnId)) return;
                Log.Message("Avatar: stale autoGen marker for on-demand pawn " + pawn.LabelShortCap
                    + " (id=" + pawnId + ", no file, not pending) — clearing and re-enqueueing.");
                AvatarMod.UnmarkAutoGen(pawnId);
                // fall through
            }

            if (string.IsNullOrEmpty(AIGen.GetPortablePath()))
            {
                AIGen.NotifyComfyUIMissing();
                return;
            }
            // Ultra-realistic needs its RealVisXL checkpoint; skip WITHOUT
            // marking/enqueuing until the one-time download lands so we don't
            // burn this pawn's retry budget on a missing-checkpoint failure.
            if (!AIGen.SelectedStyleBackendReady()) return;

            string portraitPath = AvatarManager.GetPortraitPath(pawn);
            if (System.IO.File.Exists(portraitPath))
            {
                AvatarMod.MarkAutoGen(pawnId);
                return;
            }

            AvatarMod.MarkAutoGen(pawnId);
            AvatarMod.MarkPending(pawnId);
            if (pawn.Faction == Faction.OfPlayerSilentFail)
                colonyQueue.Enqueue(pawn);
            else
                otherQueue.Enqueue(pawn);
            int q = colonyQueue.Count + otherQueue.Count;
            Log.Message("Avatar: on-demand portrait for " + pawn.LabelShortCap + " (queue=" + q + ")");
        }

        private void EnqueueExistingPawns()
        {
            AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();
            if (mod == null || !mod.settings.autoGeneratePortraits) return;
            
            foreach (Pawn pawn in map.mapPawns.AllPawns)
            {
                EnqueuePawn(pawn);
            }
        }

        // Dispatch runs in MapComponentUpdate, not MapComponentTick, so it
        // continues to fire while the game is paused. Users routinely:
        //   - dev-spawn pawns while paused
        //   - watch the colonist bar / inspect pane while paused
        //   - admire their new colony before unpausing
        // Tick-based dispatch froze in all those cases (spinners spun forever,
        // queue never drained). Realtime throttling fixes it. The cost is
        // running this method 60x/sec instead of 60x/tick — but every code
        // path early-outs cheaply when conditions aren't met, so net overhead
        // is negligible.
        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();

            float now = Time.realtimeSinceStartup;

            // Periodic safety-net scan: walk every spawned humanlike on the map
            // and EnqueuePawn each one. EnqueuePawn dedups via autoGenTriggered
            // AND skips pawns whose portrait file already exists on disk, so
            // this is cheap (hashset lookup + File.Exists per pawn, once every
            // 20s realtime). Catches pawns that SpawnSetup missed for any
            // reason — mods using non-standard spawn paths, pawns whose
            // earlier generation failed and got un-marked, faction changes,
            // etc.
            if (now >= nextSafetyScanRealtime)
            {
                nextSafetyScanRealtime = now + SafetyScanIntervalSeconds;
                AvatarMod scanMod = LoadedModManager.GetMod<AvatarMod>();
                if (scanMod != null && scanMod.settings.autoGeneratePortraits)
                {
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                    {
                        EnqueuePawn(p);
                    }
                }
                // Evict cached AvatarManagers for pawns that have been destroyed
                // /discarded — prevents the Pawn+Texture2D leak in colonistBar
                // Managers / questTabManagers over long playthroughs.
                AvatarMod.SweepDeadPawnManagers();
            }

            int totalQueued = colonyQueue.Count + otherQueue.Count;
            if (totalQueued == 0) return;
            if (now < nextProcessRealtime) return;

            // Cold-start gate: hold the entire queue until /system_stats has
            // returned 200 OK at least once within the last 30s. The check
            // itself runs on a background thread (throttled to one probe per
            // 3s) so the main thread never blocks on the 2.5s HTTP timeout
            // during ComfyUI's 30-60s cold boot. Auto-launch is kicked off
            // from inside the same probe thread when needed.
            //
            // Why this matters: without the gate, 8 colonists at game start
            // all get queued, the queue immediately dequeues one per 5s,
            // each fires NewProcess(), each python subprocess fails its own
            // preflight (or worse, queues a ComfyUI job that 503s on a
            // half-loaded server), each Exited handler unmarks the pawn,
            // and the safety-scan re-queues them — a thundering herd of
            // doomed retries against a server that isn't ready yet.
            if (!AIGen.TryConfirmComfyReadyForQueue())
            {
                // Check again in DelayRealSeconds. No dequeue, no warning — the pawn stays in line.
                nextProcessRealtime = now + DelayRealSeconds;
                return;
            }

            // Same idea for the one-time dependency install (pillow, requests
            // into ComfyUI portable's embedded Python). Background thread runs the install; this
            // returns false until it's complete, then true on every subsequent call.
            if (!AIGen.EnsurePythonDepsInstalled())
            {
                nextProcessRealtime = now + DelayRealSeconds;
                return;
            }

            // Same gate again for the ComfyUI-Inspyrenet-Rembg custom node —
            // the workflow emits an `InspyrenetRembg` node, and ComfyUI returns
            // HTTP 400 "Node 'InspyrenetRembg' not found" if it's not installed.
            // The auto-installer downloads the repo zip, extracts into custom_nodes/,
            // pip-installs `transparent_background` (single dep), and restarts
            // ComfyUI if we own its PID. Total first-run ~10s download + ~10s
            // pip + ~30-60s ComfyUI restart. The queue holds the whole time.
            if (!AIGen.EnsureInspyrenetNodeInstalled())
            {
                nextProcessRealtime = now + DelayRealSeconds;
                return;
            }

            // Hold the whole queue while the selected style's model is still
            // downloading (or ComfyUI is restarting to register it). Without this,
            // pawns enqueued under a previous style get dispatched against a
            // missing checkpoint, fail "ckpt_name not in list", and burn their
            // retry budget. No dequeue, spinners stay, no warning.
            if (!AIGen.SelectedStyleBackendReady())
            {
                nextProcessRealtime = now + DelayRealSeconds;
                return;
            }

            Pawn pawn = colonyQueue.Count > 0 ? colonyQueue.Dequeue() : otherQueue.Dequeue();
            // Dropped the old `pawn.Map == map` check (it falsely rejected
            // perfectly-valid pawns who had drifted off-map between enqueue
            // and dequeue, e.g. raiders who fled, traders who left, corpse
            // InnerPawns whose .Map is null). Portrait generation only needs
            // the Pawn object — it doesn't care which map they're currently on.
            if (pawn != null && !pawn.Destroyed && pawn.RaceProps.Humanlike)
            {
                ProcessPawn(pawn);
            }
            else if (pawn != null)
            {
                // Dropped pawn (destroyed or non-humanlike). Clear all tracking
                // state so the safety mechanisms / future on-demand clicks can
                // re-evaluate from scratch instead of silently no-op'ing on a
                // leaked marker.
                int pid = pawn.thingIDNumber;
                AvatarMod.UnmarkPending(pid);
                AvatarMod.UnmarkAutoGen(pid);
                Log.Message("Avatar: dropped dequeued pawn " + pawn.LabelShortCap
                    + " (destroyed=" + pawn.Destroyed + ", humanlike=" + pawn.RaceProps.Humanlike
                    + ") and cleared tracking state.");
            }

            nextProcessRealtime = now + DelayRealSeconds;
        }

        // One-click recovery: walks every player-faction humanlike on every map
        // and re-enqueues any without a portrait on disk, bypassing the
        // autoGenTriggered cache (so even pawns we've given up on get a fresh
        // attempt). Wired to a button in the settings panel. Returns the
        // number of pawns enqueued so the caller can toast it back.
        public static int RegenerateMissingForAllColonists()
        {
            int enqueued = 0;
            if (Find.Maps == null) return 0;
            foreach (Map map in Find.Maps)
            {
                if (map == null) continue;
                AutoPortraitGenerator gen = EnsureOnMap(map);
                if (gen == null) continue;
                // Snapshot the list so EnqueuePawn's downstream effects don't
                // mutate what we're iterating.
                List<Pawn> snapshot = new List<Pawn>(map.mapPawns.AllPawns);
                foreach (Pawn p in snapshot)
                {
                    if (p == null || p.Destroyed) continue;
                    if (!p.RaceProps.Humanlike) continue;
                    if (p.Faction != Faction.OfPlayerSilentFail) continue;
                    string path = AvatarManager.GetPortraitPath(p);
                    if (System.IO.File.Exists(path)) continue;
                    // Force re-enqueue: clear ALL tracking state so EnqueuePawn
                    // doesn't short-circuit on the retry-budget cap or the
                    // autoGen cache.
                    int pid = p.thingIDNumber;
                    AvatarMod.UnmarkAutoGen(pid);
                    AvatarMod.ClearFailedAttempts(pid);
                    gen.EnqueuePawn(p);
                    enqueued++;
                }
            }
            return enqueued;
        }

        private void ProcessPawn(Pawn pawn)
        {
            try
            {
                AvatarManager manager = new AvatarManager();
                manager.SetPawn(pawn);
                manager.SetBGColor(new Color(0, 0, 0, 0));
                manager.SetCheckDowned(false);

                string prompts = manager.GetPrompts();
                if (string.IsNullOrEmpty(prompts))
                {
                    // Defensive cleanup: an empty prompt means we can't generate
                    // anything for this pawn, but if we leave them in pending /
                    // autoGen tracking the spinner spins forever and the safety
                    // scan never re-tries them. Unmark so the next pass treats
                    // them as fresh.
                    AvatarMod.UnmarkPending(pawn.thingIDNumber);
                    AvatarMod.UnmarkAutoGen(pawn.thingIDNumber);
                    return;
                }

                string imagePath = manager.SaveToStaticPortrait();
                System.Diagnostics.Process process = AIGen.NewProcess(imagePath, prompts);
                // Retry-on-failure: if the python subprocess exits non-zero (ComfyUI
                // glitched, model missing, network blip, etc.) unmark the pawn so the
                // next safety-net scan picks them up again. We also verify a portrait
                // file actually exists on disk — exit 0 with no file should also retry.
                int pawnId = pawn.thingIDNumber;
                string expectedPortraitPath = imagePath;
                // Timestamp the start so we can record duration on success.
                DateTime startedUtc = DateTime.UtcNow;
                string pawnLabel = pawn.LabelShortCap;
                process.Exited += (s, ev) =>
                {
                    try
                    {
                        var proc = (System.Diagnostics.Process)s;
                        bool succeeded = proc.ExitCode == 0
                            && !string.IsNullOrEmpty(expectedPortraitPath)
                            && System.IO.File.Exists(expectedPortraitPath);
                        if (succeeded)
                        {
                            // Telemetry for the settings status panel.
                            double elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
                            AIGen.RecordGenerationSuccess(pawnLabel, elapsed);
                            // Clear any previous failure count — the pawn is good.
                            AvatarMod.ClearFailedAttempts(pawnId);
                        }
                        else
                        {
                            // Record this failure. If we hit the retry cap, leave
                            // autoGenTriggered marked so the periodic scan stops
                            // re-enqueueing the pawn (and EnqueuePawn's own retry
                            // check will also reject it on the next pass).
                            int attempts = AvatarMod.RecordFailedAttempt(pawnId);
                            if (attempts < AvatarMod.MaxRetryAttempts)
                            {
                                AvatarMod.UnmarkAutoGen(pawnId);
                            }
                            else
                            {
                                Log.Warning("Avatar: " + pawnLabel + " hit " + AvatarMod.MaxRetryAttempts
                                    + " failed retries — giving up. Use Mod Options → Reset failed pawns to retry, or right-click → Regenerate.");
                            }
                        }
                        // Spinner stops either way — on a non-cap failure the
                        // next safety scan re-enqueues via EnqueuePawn, which
                        // re-adds the ID to pendingPortraitPawnIds and the
                        // spinner returns.
                        AvatarMod.UnmarkPending(pawnId);
                    }
                    catch { /* don't ever throw out of an event handler */ }
                };
                process.Start();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                // "Starting up" / "installing" paths are both handled by tick-loop gates now;
                // if we still see them here it's a race — swallow silently and re-enqueue.
                // Note: this re-enqueue does NOT count as a failed attempt against
                // the retry budget (ComfyUI not being ready isn't the pawn's fault).
                if (ex.Message.Contains("starting up") || ex.Message.Contains("installing Python dependencies"))
                {
                    int pid = pawn.thingIDNumber;
                    AvatarMod.UnmarkAutoGen(pid);
                    if (pawn.Faction == Faction.OfPlayerSilentFail)
                        colonyQueue.Enqueue(pawn);
                    else
                        otherQueue.Enqueue(pawn);
                    AvatarMod.MarkAutoGen(pid);
                    // Pawn is still pending — keep the spinner on.
                    AvatarMod.MarkPending(pid);
                    return;
                }
                Log.Warning("Avatar: Auto-generation failed for " + pawn.LabelShort + ": " + ex.Message);
                // Hard failure inside ProcessPawn itself (not the subprocess).
                // Count it against the retry budget so a chronically broken
                // pawn doesn't loop forever.
                int attemptsNow = AvatarMod.RecordFailedAttempt(pawn.thingIDNumber);
                if (attemptsNow < AvatarMod.MaxRetryAttempts)
                {
                    AvatarMod.UnmarkAutoGen(pawn.thingIDNumber);
                }
                AvatarMod.UnmarkPending(pawn.thingIDNumber);
                if (ex.Message.Contains("ComfyUI"))
                    AIGen.NotifyComfyUIMissing();
            }
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    public static class Map_FinalizeInit_Patch
    {
        public static void Postfix(Map __instance)
        {
            AutoPortraitGenerator.EnsureOnMap(__instance);
        }
    }

    // Pawn.SpawnSetup catches every direct map spawn: raids, traders, wanderers,
    // babies, quest pawns, slaves, pod crashers, dev-placed pawns, etc. We use
    // the full 3-arg signature so Harmony binds even if the user has another
    // mod that's already patched a different overload of SpawnSetup.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    public static class Pawn_SpawnSetup_Patch
    {
        public static void Postfix(Pawn __instance, Map map, bool respawningAfterLoad)
        {
            AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();
            if (mod == null || !mod.settings.autoGeneratePortraits) return;
            if (map == null) return;
            if (__instance == null) return;
            // Skip dead pawns — SpawnSetup early-returns for them and replaces them
            // with a corpse, so we'd be enqueueing a pawn that's about to become a
            // Thing we can't render.
            if (__instance.Dead) return;

            // Unified attach (review #8) — EnsureOnMap also calls FinalizeInit
            // when it creates a fresh generator, so the race where SpawnSetup
            // beats Map.FinalizeInit is handled cleanly.
            AutoPortraitGenerator generator = AutoPortraitGenerator.EnsureOnMap(map);
            generator.EnqueuePawn(__instance);
        }
    }

    // Faction-change catch-all: when a pawn that was already on the map becomes
    // ours via recruit / faction reassignment / quest joiner / etc., SpawnSetup
    // doesn't re-fire (the pawn is already spawned). EnqueuePawn will silently
    // skip them if a portrait already exists OR if they're in autoGenTriggered,
    // so this is safe to fire on every faction change — only the genuinely-new
    // ones cost anything. Catches: recruited prisoners, freed slaves, mod-spawned
    // pawns that bypass SpawnSetup, faction-reassign via dev tools.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction))]
    public static class Pawn_SetFaction_Patch
    {
        public static void Postfix(Pawn __instance, Faction newFaction)
        {
            if (__instance == null || newFaction == null) return;
            if (newFaction != Faction.OfPlayerSilentFail) return;
            AvatarMod mod = LoadedModManager.GetMod<AvatarMod>();
            if (mod == null || !mod.settings.autoGeneratePortraits) return;
            Map map = __instance.Map;
            if (map == null) return;
            AutoPortraitGenerator generator = map.GetComponent<AutoPortraitGenerator>();
            if (generator == null) return; // map not ready; periodic scan will catch them

            // If this pawn was previously attempted (e.g., as a raider) and
            // failed, autoGenTriggered is set and EnqueuePawn would silently
            // skip them forever. Recruited prisoners and rescued wanderers
            // deserve a fresh attempt. Unmark them if no portrait file exists
            // yet, then enqueue.
            int pid = __instance.thingIDNumber;
            if (AvatarMod.IsAutoGenMarked(pid))
            {
                string portraitPath = AvatarManager.GetPortraitPath(__instance);
                if (!System.IO.File.Exists(portraitPath))
                {
                    AvatarMod.UnmarkAutoGen(pid);
                    AvatarMod.ClearFailedAttempts(pid);
                }
            }
            generator.EnqueuePawn(__instance);
        }
    }
}
