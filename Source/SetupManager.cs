using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using Verse;
using RimWorld;
using HarmonyLib;

namespace Avatar
{
    public enum SetupStage
    {
        Idle,
        DownloadComfy,
        ExtractComfy,
        InstallLora,
        DownloadCheckpoint,
        DownloadModel,
        InstallDeps,
        Done,
        Failed
    }

    // ============================================================================
    // Zero-touch ComfyUI bootstrap.
    //
    // Five-stage background pipeline that takes a machine with NOTHING installed
    // to a fully working AI-portrait setup:
    //   1. Download ComfyUI_windows_portable_nvidia.7z (~2.5 GB) from GitHub.
    //   2. Extract it with the bundled 7zr.exe (.7z is not a format .NET can
    //      read natively, so we shell out to a 705 KB redistributable extractor
    //      shipped in Assets/Setup/).
    //   3. Copy the bundled RimWorld LoRA into ComfyUI/models/loras/.
    //   4. Download sd_xl_base_1.0.safetensors (~6.9 GB) from HuggingFace into
    //      ComfyUI/models/checkpoints/.
    //   5. pip-install Python deps + the InSPyReNet custom node, then prewarm.
    //
    // All progress is published to volatile fields read by SetupOverlay, which
    // draws a small progress widget in the bottom-left of the screen.
    // ============================================================================
    public static class SetupManager
    {
        // GitHub "latest" redirect always points at the current portable build.
        private const string ComfyUrl =
            "https://github.com/comfyanonymous/ComfyUI/releases/latest/download/ComfyUI_windows_portable_nvidia.7z";
        private const string CheckpointUrl =
            "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/sd_xl_base_1.0.safetensors";
        private const string ComfyArchiveName = "ComfyUI_windows_portable_nvidia.7z";
        private const string PortableFolderName = "ComfyUI_windows_portable";
        private const string LoraFileName = "RimWorld_1.1-000001.safetensors";
        private const string CheckpointFileName = "sd_xl_base_1.0.safetensors";

        // ---- Live state, read from the UI thread (SetupOverlay) ----
        public static volatile bool Active = false;
        public static volatile bool CancelRequested = false;
        public static void Cancel() { CancelRequested = true; }
        private static volatile SetupStage stage = SetupStage.Idle;
        public static SetupStage Stage => stage;
        public static volatile string StageLabel = "";
        public static volatile string Detail = "";
        public static volatile string Error = null;
        // 0..1 progress for the current stage; < 0 means indeterminate.
        private static double fraction = 0.0;
        public static double Fraction { get { return fraction; } private set { fraction = value; } }
        // Realtime stamp when we entered Done/Failed, so the overlay can linger.
        public static float StageDoneRealtime = 0f;
        // Live download telemetry (read by the overlay for the metrics line).
        public static long DlDone = 0;
        public static long DlTotal = 0;
        public static double DlSpeed = 0.0; // bytes/sec, smoothed

        // Human-readable "Step N of 5" for the current stage.
        public static string StepText
        {
            get
            {
                switch (stage)
                {
                    case SetupStage.DownloadComfy:      return "Step 1 of 5";
                    case SetupStage.ExtractComfy:        return "Step 2 of 5";
                    case SetupStage.InstallLora:         return "Step 3 of 5";
                    case SetupStage.DownloadCheckpoint:  return "Step 4 of 5";
                    case SetupStage.DownloadModel:       return "HD model";
                    case SetupStage.InstallDeps:         return "Step 5 of 5";
                    case SetupStage.Done:                return "Complete";
                    default:                             return "";
                }
            }
        }

        private static readonly object gate = new object();

        // True when a complete, usable setup is already on disk: a valid portable
        // folder AND the SDXL checkpoint present. The LoRA / node / deps are
        // best-effort and don't block this check (they self-heal on first use).
        public static bool IsFullySetUp()
        {
            try
            {
                string p = AIGen.GetPortablePath();
                if (string.IsNullOrEmpty(p) || !AIGen.IsValidPortableFolder(p)) return false;
                string ckpt = Path.Combine(p, "ComfyUI", "models", "checkpoints", CheckpointFileName);
                return File.Exists(ckpt);
            }
            catch { return false; }
        }

        public static bool ShouldAutoSetup()
        {
            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                if (m == null || !m.settings.autoSetup) return false;
            }
            catch { return false; }
            if (IsFullySetUp()) return false;
            // Failsafe: don't even start on a machine the AI stack can't run on —
            // pixel-art avatars work everywhere and need no setup.
            if (PreflightReasonFast(ResolveInstallDir()) != null) return false;
            return true;
        }

        // Resolve where ComfyUI gets installed. Captured on the MAIN thread (this
        // touches Application.persistentDataPath and ModSettings) and passed into
        // the worker.
        private static string ResolveInstallDir()
        {
            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                // IsNullOrWhiteSpace (not IsNullOrEmpty): a box containing only
                // spaces would otherwise survive the guard, Trim() to "", and
                // make Directory.CreateDirectory("") throw "Path cannot be the
                // empty string or all whitespace." Fall back to the default in
                // both the empty AND whitespace cases.
                if (m != null && !string.IsNullOrWhiteSpace(m.settings.comfyInstallDir))
                {
                    string dir = m.settings.comfyInstallDir.Trim();
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch { }
            return Path.Combine(Application.persistentDataPath, "avatar");
        }

        // ---------------------------------------------------------------------
        // Failsafe preflight. The AI stack only realistically runs on
        // Windows + an NVIDIA GPU + enough free disk. Rather than try to support
        // every machine, we detect that one supported configuration and, when it
        // isn't met, cleanly fall back to the built-in pixel-art avatars instead
        // of crashing or half-installing.
        // ---------------------------------------------------------------------
        // Context-aware disk requirements. A FRESH install peaks at ~18 GB on
        // the install drive (2.5 GB archive + ~11 GB extracted portable, archive
        // deleted before the 6.9 GB checkpoint lands) — the old flat 12 GB check
        // let those installs run out of disk mid-checkpoint. ADOPTING an existing
        // portable only needs the checkpoint (+ LoRA + deps), so ~8 GB — and on
        // the PORTABLE's drive, which may differ from the install dir's drive.
        private const long RequiredFreeBytesFresh = 20L * 1024 * 1024 * 1024; // ~20 GB
        private const long RequiredFreeBytesAdopt = 8L * 1024 * 1024 * 1024;  // ~8 GB
        private static bool? cachedHasNvidia = null;

        // Disk check against the drive `path` lives on. Returns null when OK (or
        // unreadable — never block on a flaky probe), else a human-readable reason.
        private static string DiskReason(string path, long requiredBytes, string what)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(root))
                {
                    long free = new DriveInfo(root).AvailableFreeSpace;
                    if (free < requiredBytes)
                        return "needs ~" + (requiredBytes / (1024L * 1024 * 1024)) + " GB free disk space " + what
                            + " (only " + BytesText(free) + " free on " + root + ")";
                }
            }
            catch { /* can't read the drive → don't block, let the pipeline try */ }
            return null;
        }

        // Process-free checks (platform + disk). Safe on the main thread; used by
        // ShouldAutoSetup so a non-Windows / low-disk machine never even starts
        // the bootstrap. Returns null when OK, else a human-readable reason.
        private static string PreflightReasonFast(string installDir)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.WindowsEditor)
                return "AI portraits require Windows";
            // Adopt vs fresh: a configured, valid portable means we only download
            // the checkpoint — onto the PORTABLE's drive. Otherwise budget for a
            // full fresh install on the install dir's drive. (An existing portable
            // that only auto-detect would find is re-checked in RunPipeline after
            // the detect step — detection is too slow for the main thread.)
            string portable = null;
            try { portable = AIGen.GetPortablePath(); } catch { }
            if (!string.IsNullOrEmpty(portable))
                return DiskReason(portable, RequiredFreeBytesAdopt, "for the SDXL checkpoint");
            string probe = string.IsNullOrWhiteSpace(installDir)
                ? Application.persistentDataPath : installDir;
            return DiskReason(probe, RequiredFreeBytesFresh, "to install ComfyUI + models");
        }

        // Fast checks + a one-time NVIDIA GPU probe. Spawns PowerShell, so this
        // runs on the worker thread (from RunPipeline), never at game load.
        private static string PreflightReasonFull(string installDir)
        {
            string fast = PreflightReasonFast(installDir);
            if (fast != null) return fast;
            if (!HasNvidiaGpu()) return "no NVIDIA GPU detected";
            return null;
        }

        // Detects an NVIDIA GPU via Win32_VideoController. Cached for the session.
        // On any probe failure we ASSUME capable (true) so a flaky probe never
        // wrongly blocks a machine that could actually run the stack.
        private static bool HasNvidiaGpu()
        {
            if (cachedHasNvidia.HasValue) return cachedHasNvidia.Value;
            bool found = true;
            try
            {
                string sysDir = "";
                try { sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System); } catch { }
                string pwsh = string.IsNullOrEmpty(sysDir)
                    ? "powershell.exe"
                    : Path.Combine(sysDir, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(pwsh)) pwsh = "powershell.exe";
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = pwsh;
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"(Get-CimInstance Win32_VideoController).Name\"";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                if (!string.IsNullOrEmpty(sysDir)) psi.WorkingDirectory = sysDir;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                if (!string.IsNullOrEmpty(outp))
                    found = outp.ToLowerInvariant().Contains("nvidia");
            }
            catch (Exception e)
            {
                Log.Warning("Avatar: GPU probe failed (" + e.Message + ") — assuming capable.");
                found = true;
            }
            cachedHasNvidia = found;
            return found;
        }

        private static string ResolveModRoot()
        {
            try
            {
                AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                if (m != null && m.Content != null) return m.Content.RootDir;
            }
            catch { }
            return null;
        }

        // Entry point. Safe to call from the main thread (HarmonyInit) or a button.
        public static void StartBootstrap()
        {
            lock (gate)
            {
                if (Active) return;
                Active = true;
            }
            Error = null;
            CancelRequested = false;
            string modRoot = ResolveModRoot();
            string installDir = ResolveInstallDir();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { RunPipeline(modRoot, installDir); }
                catch (OperationCanceledException)
                {
                    Error = null;
                    SetStage(SetupStage.Failed, "Setup cancelled", 0);
                    Detail = "cancelled";
                    Log.Message("Avatar: setup cancelled by user.");
                }
                catch (Exception e)
                {
                    Error = e.Message;
                    SetStage(SetupStage.Failed, "Setup failed: " + e.Message, 0);
                    Log.Error("Avatar setup pipeline crashed: " + e);
                }
                finally { Active = false; }
            });
        }

        // ---------------------------------------------------------------------
        // Ultra-realistic style: lazy one-time download of the RealVisXL HD
        // checkpoint into the EXISTING ComfyUI portable. Reuses the same
        // background downloader + progress overlay + disk preflight as the main
        // bootstrap, but is a standalone single-file fetch — ComfyUI itself must
        // already be set up (via the RimWorld-style bootstrap). Shares
        // Active/gate so it never runs concurrently with the main bootstrap.
        // ---------------------------------------------------------------------
        // True when the style's checkpoint (and its external VAE, if any) are both
        // present in the configured portable. spec == null (RimWorld) is always
        // "installed" (the SDXL base ships with the bootstrap).
        public static bool StyleCheckpointInstalled(PortraitStyleSpec spec)
        {
            try
            {
                if (spec == null) return true;
                string p = AIGen.GetPortablePath();
                if (string.IsNullOrEmpty(p)) return false;
                string ckpt = Path.Combine(p, "ComfyUI", "models", "checkpoints", spec.checkpointFile);
                if (!File.Exists(ckpt)) return false;
                if (!string.IsNullOrEmpty(spec.vaeFile))
                {
                    string vae = Path.Combine(p, "ComfyUI", "models", "vae", spec.vaeFile);
                    if (!File.Exists(vae)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        public static void EnsureStyleCheckpoint(PortraitStyle style)
        {
            PortraitStyleSpec spec = PortraitStyles.Get(style);
            if (spec == null) return;
            lock (gate)
            {
                if (Active) return;
                Active = true;
            }
            Error = null;
            CancelRequested = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { RunStyleDownload(spec); }
                catch (OperationCanceledException)
                {
                    Error = null;
                    SetStage(SetupStage.Failed, "Download cancelled", 0);
                    Detail = "cancelled";
                    Log.Message("Avatar: " + spec.displayName + " download cancelled by user.");
                }
                catch (Exception e)
                {
                    Error = e.Message;
                    SetStage(SetupStage.Failed, spec.displayName + " download failed: " + e.Message, 0);
                    Log.Error("Avatar: " + spec.displayName + " download failed: " + e);
                }
                finally { Active = false; }
            });
        }

        private static void RunStyleDownload(PortraitStyleSpec spec)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            string portable = AIGen.GetPortablePath();
            if (string.IsNullOrEmpty(portable) || !AIGen.IsValidPortableFolder(portable))
            {
                Error = "ComfyUI is not installed yet";
                SetStage(SetupStage.Failed, "Set up ComfyUI first (let RimWorld style install once)", 0);
                return;
            }
            string ckptDest = Path.Combine(portable, "ComfyUI", "models", "checkpoints", spec.checkpointFile);
            string vaeDest = string.IsNullOrEmpty(spec.vaeFile)
                ? null : Path.Combine(portable, "ComfyUI", "models", "vae", spec.vaeFile);
            bool ckptThere = File.Exists(ckptDest);
            bool vaeThere = vaeDest == null || File.Exists(vaeDest);
            if (ckptThere && vaeThere)
            {
                SetStage(SetupStage.Done, spec.displayName + " ready", 1.0);
                return;
            }
            // ~checkpoint + vae + 1 GB headroom, on the drive the files land on.
            long required = spec.checkpointBytes + spec.vaeBytes + (1L * 1024 * 1024 * 1024);
            string disk = DiskReason(ckptDest, required, "for the " + spec.displayName + " model");
            if (disk != null) throw new Exception(disk);
            if (!ckptThere)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ckptDest));
                SetStage(SetupStage.DownloadModel, "Downloading " + spec.displayName + " (one-time, " + BytesText(spec.checkpointBytes) + ")", -1);
                DownloadWithProgress(spec.checkpointUrl, ckptDest);
            }
            if (vaeDest != null && !File.Exists(vaeDest))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(vaeDest));
                SetStage(SetupStage.DownloadModel, "Downloading " + spec.displayName + " VAE (" + BytesText(spec.vaeBytes) + ")", -1);
                DownloadWithProgress(spec.vaeUrl, vaeDest);
            }
            // ComfyUI caches its model lists at startup, so a file dropped in now
            // isn't visible until it rescans. Restart the instance we own so the
            // new checkpoint/VAE register (else the first gen fails validation:
            // "ckpt_name ... not in list"). No-op if we don't own the process.
            SetStage(SetupStage.DownloadModel, "Loading " + spec.displayName + " into ComfyUI", -1);
            DlTotal = 0; DlDone = 0; DlSpeed = 0.0;
            AIGen.RestartManagedComfyForModelSync(150000);
            SetStage(SetupStage.Done, spec.displayName + " ready — portraits enabled", 1.0);
            Log.Message("Avatar: " + spec.displayName + " installed (" + ckptDest + ")");
        }

        private static void RunPipeline(string modRoot, string installDir)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            // Failsafe gate: only the supported configuration (Windows + NVIDIA +
            // enough disk) proceeds; everything else cleanly falls back to the
            // built-in pixel-art avatars instead of crashing or half-installing.
            string skip = PreflightReasonFull(installDir);
            if (skip != null)
            {
                Log.Message("Avatar: skipping AI setup — " + skip + ". Using pixel-art avatars.");
                RunOnMain(() => Messages.Message(
                    "Avatar - Personas: " + skip + " — using pixel-art avatars instead.",
                    MessageTypeDefOf.NeutralEvent, historical: false));
                SetStage(SetupStage.Idle, "", 0);
                return;
            }

            if (string.IsNullOrEmpty(modRoot))
                throw new Exception("could not resolve mod content folder");
            // Defensive: never hand an empty/whitespace path to CreateDirectory
            // (it throws "Path cannot be the empty string or all whitespace.").
            // ResolveInstallDir should already guarantee this, but a blank
            // install-location box must degrade to the default, not crash setup.
            if (string.IsNullOrWhiteSpace(installDir))
                installDir = Path.Combine(Application.persistentDataPath, "avatar");
            Directory.CreateDirectory(installDir);
            ThrowIfCancelled();

            // ---- Stage 1 + 2: locate or download ComfyUI portable ----
            // NEVER re-download ComfyUI for someone who already has it. Prefer:
            //   (a) the configured path (settings.comfyPortablePath), then
            //   (b) auto-detect an existing portable anywhere on disk, and only
            //   (c) download a fresh copy if nothing is found.
            string portable = AIGen.GetPortablePath(); // configured + valid, or null
            if (string.IsNullOrEmpty(portable))
            {
                SetStage(SetupStage.DownloadComfy, "Looking for an existing ComfyUI install...", -1);
                try { portable = AIGen.AutoDetectPortable(); } catch { portable = null; }
            }
            ThrowIfCancelled();

            if (string.IsNullOrEmpty(portable) || !AIGen.IsValidPortableFolder(portable))
            {
                // Nothing on disk — download a fresh portable into the install dir.
                portable = Path.Combine(installDir, PortableFolderName);
                if (!AIGen.IsValidPortableFolder(portable))
                {
                    string archive = Path.Combine(installDir, ComfyArchiveName);
                    SetStage(SetupStage.DownloadComfy, "Downloading ComfyUI (one-time, ~2.5 GB)", -1);
                    DownloadWithProgress(ComfyUrl, archive);

                    SetStage(SetupStage.ExtractComfy, "Extracting ComfyUI", -1);
                    Extract7z(modRoot, archive, installDir);
                    try { File.Delete(archive); } catch { }

                    if (!AIGen.IsValidPortableFolder(portable))
                    {
                        string found = FindPortableUnder(installDir);
                        if (!string.IsNullOrEmpty(found)) portable = found;
                    }
                    if (!AIGen.IsValidPortableFolder(portable))
                        throw new Exception("extraction did not produce a valid ComfyUI portable folder under " + installDir);
                }
            }
            else
            {
                Log.Message("Avatar: found an existing ComfyUI portable — adopting it instead of downloading: " + portable);
            }

            ThrowIfCancelled();
            // Persist the path so every downstream helper (deps, node, prewarm) uses it.
            ApplyPortablePath(portable);

            string modelsRoot = Path.Combine(portable, "ComfyUI", "models");

            // ---- Stage 3: bundled RimWorld LoRA ----
            SetStage(SetupStage.InstallLora, "Installing RimWorld LoRA", -1);
            string loraDest = Path.Combine(modelsRoot, "loras", LoraFileName);
            if (!File.Exists(loraDest))
            {
                string loraSrc = Path.Combine(modRoot, "Assets", "Setup", LoraFileName);
                if (!File.Exists(loraSrc))
                    throw new Exception("bundled LoRA missing at " + loraSrc);
                Directory.CreateDirectory(Path.GetDirectoryName(loraDest));
                Detail = "copying " + LoraFileName;
                File.Copy(loraSrc, loraDest, true);
            }

            // ---- Stage 4: SDXL checkpoint ----
            ThrowIfCancelled();
            string ckptDest = Path.Combine(modelsRoot, "checkpoints", CheckpointFileName);
            if (!File.Exists(ckptDest))
            {
                // Re-check disk on the drive the checkpoint ACTUALLY lands on — an
                // adopted/auto-detected portable may sit on a different drive than
                // the one the main-thread preflight probed. Failing loudly here
                // beats dying at 94% of a 6.9 GB download.
                string disk = DiskReason(ckptDest, RequiredFreeBytesAdopt, "for the SDXL checkpoint");
                if (disk != null)
                    throw new Exception(disk);
                SetStage(SetupStage.DownloadCheckpoint, "Downloading SDXL checkpoint (one-time, ~6.9 GB)", -1);
                Directory.CreateDirectory(Path.GetDirectoryName(ckptDest));
                DownloadWithProgress(CheckpointUrl, ckptDest);
            }

            // ---- Stage 5: Python deps + InSPyReNet node (best-effort) ----
            SetStage(SetupStage.InstallDeps, "Installing Python dependencies", -1);
            try
            {
                RunOnMain(() => AIGen.EnsurePythonDepsInstalled());
                WaitWhile(() => !AIGen.DepsChecked || AIGen.DepsBusy, 600000);

                if (AIGen.CheckInspyrenetNodeInstalled() != "installed")
                {
                    SetStage(SetupStage.InstallDeps, "Installing InSPyReNet node", -1);
                    RunOnMain(() => AIGen.InstallInspyrenetNodeAsync(null));
                    Thread.Sleep(750);
                    WaitWhile(() => AIGen.InspyrenetInstallInProgress, 600000);
                }
            }
            catch (Exception e)
            {
                // Non-fatal: deps + node self-heal on first portrait if this slips.
                Log.Warning("Avatar setup: deps/node stage had a problem (will self-heal on first portrait): " + e.Message);
            }

            // ---- Done ----
            SetStage(SetupStage.Done, "ComfyUI ready — portraits enabled", 1.0);
            Log.Message("Avatar: zero-touch setup complete. Portable=" + portable);
            // Launch + warm the server so the first portrait isn't a cold start.
            RunOnMain(() => AIGen.PrewarmComfyUI());
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static void ThrowIfCancelled()
        {
            if (CancelRequested) throw new OperationCanceledException();
        }

        private static void SetStage(SetupStage s, string label, double frac)
        {
            stage = s;
            StageLabel = label;
            fraction = frac;
            Detail = "";
            // Reset download telemetry whenever we leave a download stage.
            if (s != SetupStage.DownloadComfy && s != SetupStage.DownloadCheckpoint && s != SetupStage.DownloadModel)
            {
                DlDone = 0; DlTotal = 0; DlSpeed = 0.0;
            }
            if (s == SetupStage.Done || s == SetupStage.Failed)
                StageDoneRealtime = Time.realtimeSinceStartup;
        }

        private static void ApplyPortablePath(string portable)
        {
            // Set the in-memory cache SYNCHRONOUSLY so GetPortablePath /
            // GetEmbeddedPython resolve on THIS worker thread immediately. The
            // deps/node stages can't wait on the main-thread settings write,
            // which during game-load may not run until the loading screen ends.
            AIGen.AdoptPortablePath(portable);
            RunOnMain(() =>
            {
                try
                {
                    AvatarMod m = LoadedModManager.GetMod<AvatarMod>() as AvatarMod;
                    if (m != null)
                    {
                        m.settings.comfyPortablePath = portable;
                        m.WriteSettings();
                    }
                    // Re-assert the cache after the write (defensive; the persist
                    // path does not invalidate it, but a concurrent ResetPortableCache
                    // from the settings UI could).
                    AIGen.AdoptPortablePath(portable);
                }
                catch (Exception e) { Log.Warning("Avatar setup: could not persist portable path: " + e.Message); }
            });
        }

        // Marshals an action onto the main (Unity) thread. We deliberately do
        // NOT use LongEventHandler.ExecuteWhenFinished: when no long event is in
        // progress (at the menu or in-game) it runs the action INLINE on the
        // CALLING thread. From our worker that means Unity calls (Texture2D,
        // Scribe) run off-thread and HARD-CRASH the game. Instead we queue and
        // drain from UIRoot.UIRootOnGUI, which is guaranteed main-thread.
        private static readonly System.Collections.Generic.Queue<Action> mainQueue =
            new System.Collections.Generic.Queue<Action>();

        private static void RunOnMain(Action a)
        {
            if (a == null) return;
            lock (mainQueue) mainQueue.Enqueue(a);
        }

        // Called once per frame from the UIRoot OnGUI postfixes (main thread).
        public static void DrainMainQueue()
        {
            while (true)
            {
                Action a = null;
                lock (mainQueue)
                {
                    if (mainQueue.Count > 0) a = mainQueue.Dequeue();
                }
                if (a == null) break;
                try { a(); }
                catch (Exception e) { Log.Warning("Avatar setup: queued main-thread action threw: " + e.Message); }
            }
        }

        private static bool WaitWhile(Func<bool> cond, int timeoutMs)
        {
            DateTime start = DateTime.UtcNow;
            while (cond())
            {
                if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs) return false;
                Thread.Sleep(500);
            }
            return true;
        }

        private static string FindPortableUnder(string root)
        {
            try
            {
                if (AIGen.IsValidPortableFolder(root)) return root;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (AIGen.IsValidPortableFolder(dir)) return dir;
                    try
                    {
                        foreach (string sub in Directory.GetDirectories(dir))
                            if (AIGen.IsValidPortableFolder(sub)) return sub;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // Streaming downloader with live byte progress, TLS 1.2, redirect-follow,
        // and resume-on-retry (Range header). Downloads to <dest>.part then moves
        // into place so a half-finished file never looks complete.
        private static void DownloadWithProgress(string url, string destPath)
        {
            if (File.Exists(destPath)) return;
            string tmp = destPath + ".part";
            int attempt = 0;
            while (true)
            {
                attempt++;
                long existing = 0;
                try { if (File.Exists(tmp)) existing = new FileInfo(tmp).Length; } catch { }
                try
                {
                    DownloadChunk(url, tmp, existing);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (System.Net.WebException we)
                {
                    // A stale/oversized .part makes the server reject our resume
                    // offset with 416 — the old loop re-sent the same bad offset
                    // forever. Throw the partial away and restart from byte 0.
                    bool range416 = (we.Response as HttpWebResponse)?.StatusCode
                        == HttpStatusCode.RequestedRangeNotSatisfiable;
                    if (range416) { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                    if (attempt >= 6) throw;
                    Detail = range416 ? "restarting download (stale partial)" : ("connection lost — retry " + attempt + "/6");
                    Log.Warning("Avatar setup: download attempt " + attempt + " failed (" + we.Message + "), retrying" + (range416 ? " from scratch." : "..."));
                    Thread.Sleep(range416 ? 1000 : 4000);
                }
                catch (Exception e)
                {
                    if (attempt >= 6) throw;
                    Detail = "connection lost — retry " + attempt + "/6";
                    Log.Warning("Avatar setup: download attempt " + attempt + " failed (" + e.Message + "), retrying...");
                    Thread.Sleep(4000);
                }
            }
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            File.Move(tmp, destPath);
        }

        private static void DownloadChunk(string url, string tmp, long resumeFrom)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Avatar-Personas-Mod/1.0 (RimWorld)";
            req.AllowAutoRedirect = true;
            req.Timeout = 60000;            // connect
            req.ReadWriteTimeout = 120000;  // stalled stream
            if (resumeFrom > 0) req.AddRange(resumeFrom);

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
                long startAt = (resumeFrom > 0 && partial) ? resumeFrom : 0;
                long total = resp.ContentLength + startAt; // ContentLength is the remaining bytes for a 206
                if (resp.ContentLength <= 0) total = -1;

                using (Stream net = resp.GetResponseStream())
                using (FileStream fs = new FileStream(
                    tmp, startAt > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 1 << 20))
                {
                    byte[] buf = new byte[1 << 20];
                    long read = startAt;
                    int n;
                    DateTime lastUi = DateTime.UtcNow;
                    long lastBytes = startAt;
                    DlTotal = total;
                    while ((n = net.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (CancelRequested) throw new OperationCanceledException();
                        fs.Write(buf, 0, n);
                        read += n;
                        double sinceMs = (DateTime.UtcNow - lastUi).TotalMilliseconds;
                        if (sinceMs > 200)
                        {
                            double inst = (read - lastBytes) / (sinceMs / 1000.0);
                            // Exponential smoothing so the readout doesn't jitter.
                            DlSpeed = DlSpeed <= 0 ? inst : (DlSpeed * 0.7 + inst * 0.3);
                            DlDone = read;
                            DlTotal = total;
                            fraction = total > 0 ? (double)read / total : -1;
                            lastUi = DateTime.UtcNow;
                            lastBytes = read;
                        }
                    }
                    DlDone = read;
                    DlTotal = total;
                    fraction = total > 0 ? (double)read / total : -1;
                }
            }
        }

        // Extracts a .7z using the bundled 7zr.exe. Parses 7zr's -bsp1 progress
        // output (lines like " 42%") to drive the progress bar.
        private static void Extract7z(string modRoot, string archive, string destDir)
        {
            string exe = Path.Combine(modRoot, "Assets", "Setup", "7zr.exe");
            if (!File.Exists(exe))
                throw new Exception("bundled 7zr.exe missing at " + exe);

            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe,
                "x -y -bsp1 -o\"" + destDir + "\" \"" + archive + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            Regex pctRx = new Regex(@"(\d{1,3})%");
            StringBuilder errBuf = new StringBuilder();
            System.Diagnostics.Process p = new System.Diagnostics.Process { StartInfo = psi };
            p.OutputDataReceived += (s, ev) =>
            {
                if (ev.Data == null) return;
                Match m = pctRx.Match(ev.Data);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int pct))
                {
                    fraction = pct / 100.0;
                    Detail = "extracting " + pct + "%";
                }
            };
            p.ErrorDataReceived += (s, ev) => { if (ev.Data != null) lock (errBuf) errBuf.AppendLine(ev.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            // Poll instead of a blocking WaitForExit(): honors Cancel and puts a
            // hard cap on a wedged 7zr (otherwise the pipeline hangs forever with
            // the overlay stuck at "Extracting").
            DateTime deadline = DateTime.UtcNow.AddMinutes(30);
            while (!p.WaitForExit(1000))
            {
                if (CancelRequested)
                {
                    try { p.Kill(); } catch { }
                    throw new OperationCanceledException();
                }
                if (DateTime.UtcNow > deadline)
                {
                    try { p.Kill(); } catch { }
                    throw new Exception("7zr extraction timed out after 30 minutes");
                }
            }
            p.WaitForExit(); // flush async output readers
            if (p.ExitCode != 0)
            {
                string tail;
                lock (errBuf) tail = errBuf.ToString();
                throw new Exception("7zr extraction failed (exit " + p.ExitCode + "): " + tail);
            }
        }

        internal static string BytesText(long b)
        {
            if (b < 0) return "";
            double mb = b / 1048576.0;
            if (mb >= 1024.0) return (mb / 1024.0).ToString("F2") + " GB";
            return mb.ToString("F0") + " MB";
        }

        internal static string SpeedText(double bytesPerSec)
        {
            if (bytesPerSec <= 0) return "";
            double mbps = bytesPerSec / 1048576.0;
            if (mbps >= 1.0) return mbps.ToString("F1") + " MB/s";
            return (bytesPerSec / 1024.0).ToString("F0") + " KB/s";
        }

        internal static string EtaText(long done, long total, double bytesPerSec)
        {
            if (total <= 0 || bytesPerSec <= 0 || done >= total) return "";
            double secs = (total - done) / bytesPerSec;
            if (secs >= 3600) return "ETA " + ((int)(secs / 3600)) + "h " + ((int)((secs % 3600) / 60)) + "m";
            if (secs >= 60) return "ETA " + ((int)(secs / 60)) + "m " + ((int)(secs % 60)) + "s";
            return "ETA " + ((int)secs) + "s";
        }
    }

    // ============================================================================
    // Bottom-left progress card. Drawn from BOTH UIRoot subclasses (menu + play)
    // so it shows everywhere, exactly where portraits otherwise live. Lingers a
    // few seconds after Done/Failed, then disappears.
    // ============================================================================
    public static class SetupOverlay
    {
        private const float LingerSeconds = 14f;
        // Property (not a cached static Texture2D field) so the type doesn't trip
        // RimWorld's "needs StaticConstructorOnStartup" asset-on-field warning.
        private static Texture2D WhiteTex => BaseContent.WhiteTex;

        public static void Draw()
        {
            bool failed = SetupManager.Stage == SetupStage.Failed;
            bool done = SetupManager.Stage == SetupStage.Done;
            bool show = SetupManager.Active;
            if (!show && (done || failed))
                show = (Time.realtimeSinceStartup - SetupManager.StageDoneRealtime) < LingerSeconds;
            if (!show) return;

            const float w = 440f, h = 114f, pad = 12f;
            Rect card = new Rect(pad, UI.screenHeight - h - pad, w, h);

            TextAnchor oldAnchor = Text.Anchor;
            GameFont oldFont = Text.Font;
            Color oldColor = GUI.color;

            // --- Card background: solid dark panel + subtle border. ---
            GUI.color = new Color(0.07f, 0.08f, 0.10f, 0.94f);
            GUI.DrawTexture(card, WhiteTex);
            GUI.color = new Color(1f, 1f, 1f, 0.10f);
            Widgets.DrawBox(card, 1);
            GUI.color = oldColor;

            Rect inner = card.ContractedBy(12f);

            // --- Header row: title (left) + step counter (right). ---
            Color accent = failed ? new Color(0.93f, 0.42f, 0.36f)
                          : done   ? new Color(0.45f, 0.85f, 0.55f)
                                   : new Color(0.40f, 0.70f, 1f);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = accent;
            Rect titleRect = new Rect(inner.x, inner.y, inner.width - 96f, 26f);
            Widgets.Label(titleRect, failed ? "Avatar — setup failed" : "Avatar — AI setup");
            Text.Anchor = TextAnchor.UpperRight;
            GUI.color = new Color(0.65f, 0.68f, 0.72f);
            Rect stepRect = new Rect(inner.xMax - 96f, inner.y, 96f, 26f);
            Widgets.Label(stepRect, SetupManager.StepText);
            GUI.color = oldColor;

            // --- Stage label. ---
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = new Color(0.86f, 0.88f, 0.90f);
            Rect stageRect = new Rect(inner.x, inner.y + 28f, inner.width, 20f);
            Widgets.Label(stageRect, SetupManager.StageLabel ?? "");
            GUI.color = oldColor;

            // --- Progress bar. ---
            Rect bar = new Rect(inner.x, inner.y + 50f, inner.width, 16f);
            // Track.
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(bar, WhiteTex);

            double frac = SetupManager.Fraction;
            if (failed)
            {
                GUI.color = new Color(0.45f, 0.13f, 0.12f);
                GUI.DrawTexture(bar, WhiteTex);
            }
            else if (frac >= 0.0)
            {
                float f = Mathf.Clamp01((float)frac);
                Rect fill = new Rect(bar.x, bar.y, bar.width * f, bar.height);
                GUI.color = accent;
                GUI.DrawTexture(fill, WhiteTex);
                // Highlight strip along the top of the fill for a glossy feel.
                GUI.color = new Color(1f, 1f, 1f, 0.18f);
                GUI.DrawTexture(new Rect(fill.x, fill.y, fill.width, fill.height * 0.4f), WhiteTex);
            }
            else
            {
                // Indeterminate: a soft block sweeping left-right.
                float t = (Mathf.Sin(Time.realtimeSinceStartup * 2.4f) + 1f) * 0.5f;
                float bw = bar.width * 0.28f;
                Rect blip = new Rect(bar.x + (bar.width - bw) * t, bar.y, bw, bar.height);
                GUI.color = accent;
                GUI.DrawTexture(blip, WhiteTex);
            }
            // Border around bar.
            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            Widgets.DrawBox(bar, 1);
            GUI.color = oldColor;

            // Percentage text centered on the bar (only when determinate).
            if (!failed && frac >= 0.0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = Color.white;
                Widgets.Label(bar, ((int)(Mathf.Clamp01((float)frac) * 100f)) + "%");
                GUI.color = oldColor;
            }

            // --- Metrics line: size · speed · ETA, or the stage detail text. ---
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = new Color(0.62f, 0.66f, 0.70f);
            Rect metaRect = new Rect(inner.x, inner.y + 70f, inner.width, 18f);
            string meta;
            if (failed)
            {
                meta = SetupManager.Error ?? "see RimWorld log";
            }
            else if (SetupManager.DlTotal > 0)
            {
                string size = SetupManager.BytesText(SetupManager.DlDone) + " / " + SetupManager.BytesText(SetupManager.DlTotal);
                string spd = SetupManager.SpeedText(SetupManager.DlSpeed);
                string eta = SetupManager.EtaText(SetupManager.DlDone, SetupManager.DlTotal, SetupManager.DlSpeed);
                meta = size;
                if (!string.IsNullOrEmpty(spd)) meta += "  ·  " + spd;
                if (!string.IsNullOrEmpty(eta)) meta += "  ·  " + eta;
            }
            else
            {
                meta = SetupManager.Detail ?? "";
            }
            Widgets.Label(metaRect, meta);
            GUI.color = oldColor;

            Text.Anchor = oldAnchor;
            Text.Font = oldFont;
            GUI.color = oldColor;
        }
    }

    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    public static class UIRootPlay_SetupOverlay_Patch
    {
        public static void Postfix() { SetupManager.DrainMainQueue(); SetupOverlay.Draw(); }
    }

    [HarmonyPatch(typeof(UIRoot_Entry), nameof(UIRoot_Entry.UIRootOnGUI))]
    public static class UIRootEntry_SetupOverlay_Patch
    {
        public static void Postfix() { SetupManager.DrainMainQueue(); SetupOverlay.Draw(); }
    }
}
