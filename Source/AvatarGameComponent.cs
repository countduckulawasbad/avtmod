using System.Collections.Generic;
using Verse;
using RimWorld;

namespace Avatar
{
    // ============================================================================
    // AvatarGameComponent
    // ============================================================================
    // Per-save state holder. RimWorld discovers GameComponent subclasses via
    // reflection during Game initialization (Game.FillComponents \u2192 every
    // non-abstract GameComponent with a single Game-arg ctor) and Scribes their
    // ExposeData on every save/load. That's the only place we get a guaranteed
    // hook for "do this once per loaded save."
    //
    // Why a GameComponent and not a MapComponent:
    //   - hiddenPawns is per-pawn but global to the game (pawn IDs are stable
    //     across maps; a colonist hidden on map A should stay hidden if they
    //     caravan to map B).
    //   - The legacy portrait-file migration is a one-shot global operation
    //     on Application.persistentDataPath/avatar/ \u2014 not per-map.
    //
    // What we serialize:
    //   - AvatarMod.hiddenPawns: which pawns the user manually right-click \u2192
    //     Hide'd. Without this, a save+load resets every hidden flag and the
    //     user has to re-hide every pawn they care about.
    //
    // What we deliberately do NOT serialize (and why):
    //   - autoGenTriggered / pendingPortraitPawnIds: source of truth is the
    //     on-disk portrait file. On every load FinalizeInit re-walks all pawns
    //     and re-derives these sets via the file-exists check. See the
    //     "Queue persistence note" in AutoPortraitGenerator.cs.
    //   - failedAttempts: a fresh game launch should re-try every pawn (the
    //     user might have fixed whatever was broken by editing ComfyUI/models
    //     between sessions). Session-scoped is correct here.
    // ============================================================================
    public class AvatarGameComponent : GameComponent
    {
        public AvatarGameComponent(Game game) { }

        public override void ExposeData()
        {
            base.ExposeData();
            // Scribe_Collections handles HashSet<int> via Look<T> with a wrapped
            // list internally. The keyed name lives forever \u2014 don't rename it.
            Scribe_Collections.Look(ref AvatarMod.hiddenPawns, "avatarHiddenPawns", LookMode.Value);
            // Guard against null after load \u2014 Scribe leaves the field null
            // for saves that predate this GameComponent.
            if (Scribe.mode == LoadSaveMode.PostLoadInit && AvatarMod.hiddenPawns == null)
                AvatarMod.hiddenPawns = new HashSet<int>();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            // Bulk-migrate legacy <name>_<id>.png \u2192 <id>.png file names. Runs
            // once per loaded save, idempotent (no-op when the avatar folder is
            // already clean), and cheap (one Directory.GetFiles + a regex pass).
            AvatarManager.BulkMigrateLegacyPortraits();
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // FinalizeInit fires on fresh games too (where LoadedGame does NOT).
            // Run the bulk migration here as well so a fresh-game session with
            // legacy portrait files from a previous install also gets cleaned up.
            AvatarManager.BulkMigrateLegacyPortraits();
        }
    }
}
