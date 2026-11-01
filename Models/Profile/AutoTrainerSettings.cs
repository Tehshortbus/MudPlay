using System.Collections.Generic;
using MudPlay.Services;

namespace MudPlay.Models.Profile;

// Per-character Auto-Trainer settings — the "AutoTrainer" entry in
// CharacterProfile.Settings. Surfaced by the Settings → Auto-Trainer tab.
public sealed class AutoTrainerSettings
{
    // Auto-level toggle: when running a loop / auto-lair and a level-up is
    // available, detour to the appropriate trainer and train the level(s).
    public bool AutoTrain { get; set; }

    // Auto-apply-CP toggle — INDEPENDENT of AutoTrain (decoupled): whenever a train
    // flow runs, drive the train-stats screen to apply the CP plan's row. On with
    // AutoTrain off simply means no auto-trigger fires it, so it then only affects
    // the manual Train-now / @train / "Apply this level" paths. Still requires a
    // saved CP plan to have anything to apply.
    public bool AutoTrainStats { get; set; }

    // How many trainable levels must stack up before auto-train fires at all.
    // Trips on the way OUT of the grind, not on every level: with this at 3 the
    // character keeps killing until three levels are banked and then makes ONE
    // trip that trains them together, instead of three separate trainer detours.
    // 0 / 1 (the default) fires as soon as a single level is trainable.
    //
    // Pairs with LevelsToKeep, which decides how many of those stay banked once
    // the trip happens: a threshold at or below the keep reserve can never train
    // anything, so the trigger treats it as "fire as soon as there's something
    // above the reserve to train".
    [SettingRange(0, 60)]
    public int FireAtBankedLevels { get; set; }

    // Buffer of trainable-but-untrained levels to always keep banked. Auto-train
    // (and the manual Train Now) stops once this many levels are still reachable
    // from banked exp, so the character always carries a reserve. 0 (the
    // default) trains every banked level.
    [SettingRange(0, 60)]
    public int LevelsToKeep { get; set; }

    // Level ceiling: auto-train will train UP TO this level but never above it
    // (reach N, then stop) — a guard against over-levelling / "OP"-ing a character
    // once the exp + level-buffer gates would otherwise keep training. 0 (the
    // default) = no ceiling.
    [SettingRange(0, 200)]
    public int DoNotTrainAbove { get; set; }

    // When on, broadcast "I can now train to level: N" on AnnounceChannel each
    // time a live experience gain makes a new level trainable — i.e. a
    // Level-Projection row's "Exp to level" reaches 0. Banked levels already
    // trainable on login / at the next stat poll are seeded silently, so this
    // only fires on transitions that happen during play.
    public bool AnnounceLevelUps { get; set; }

    // Chat channel the level-up announce is sent on. Defaults to Gangpath
    // (most useful to a party).
    public AnnounceChannel AnnounceChannel { get; set; } = AnnounceChannel.Gangpath;

    // Party-friendly auto-train. In a party, auto-train can't just walk off: a
    // follower's `train` drops only that follower, but the leader's disbands the
    // whole party. With this on, a member reports its readiness (per the thresholds
    // above) to the leader instead of training alone, and the leader — when its own
    // copy is on — takes everyone who's ready to the trainer(s) together. Off, a
    // character in a party never auto-trains (the solo run is solo-only).
    public bool AutoTrainParty { get; set; }

    // How many party members (the leader counting as one) must be ready before the
    // leader goes; whoever isn't ready then sits the trip out. A party where everyone
    // who counts is ready goes even when that's fewer than this.
    [SettingRange(1, 6)]
    public int PartyMinReady { get; set; } = 2;

    // A member who isn't ready and sits more than this many levels above the rest
    // of the party (a power-leveler) is never waited for. 0 = no gap rule.
    [SettingRange(0, 200)]
    public int PartyLevelGap { get; set; } = 5;

    // Never party-train from level 10 to 11: that trainer is a solo effort, so a
    // character stops at 10 in party mode and takes the step to 11 solo.
    public bool PartySkipLevel11 { get; set; } = true;

    // Stock only: how long a train run standing at the trainer waits for an
    // altered stat to clear before it leaves the CP plan for later. Stock refuses
    // `train stats` while a spell or item is on a stat; Paradigm trains through
    // one, so nothing there waits. 0 = don't wait.
    [SettingRange(0, 600)]
    public int AlteredStatsWaitSeconds { get; set; } = 120;
    public const int MaxAlteredStatsWaitSeconds = 600;

    // Trainer rows the user has switched OFF for auto-train, keyed by
    // Game.GameData.TrainerShop.RowKey (shop/map/room) so a multi-room shop's
    // rooms toggle independently. Storing the disabled set (rather than the
    // allowed set) keeps the JSON small and means newly-discovered trainers
    // default to allowed. null / empty = every discovered trainer is allowed.
    public List<string>? DisabledTrainers { get; set; }

    // Which trainer a run goes to when several can teach the level: the closest, or
    // the cheapest (lowest markup). `train stats` alone always takes the closest:
    // applying CP costs nothing.
    public TrainerChoice TrainerChoice { get; set; }

    // How a party trip picks the trainer its members train at next: the one that
    // trains the most of them, or the one TrainerChoice picks.
    public PartyStopChoice PartyStopChoice { get; set; }

    // Where a run that's short on cash may fetch the difference from: stashes, a
    // bank, both, or nowhere (keep looping until the purse covers it).
    public TrainFundingMode FundingMode { get; set; }

    // Only withdraw at this bank room. A bank can have several branches (Bank of
    // Godfrey in Silvermere and Khazarad) sharing one balance; this picks the one
    // to walk to. null = any bank room whose bank holds enough.
    public RoomRef? FundingBankRoom { get; set; }

    // Only collect from this flagged stash room. null = any stash room.
    public RoomRef? FundingStash { get; set; }

    // After a train trip, go on to the shops that sell the scrolls for spells the
    // character can now learn and hasn't, buy them and read them. The trip's
    // funding errand fetches the scroll money along with the training fee.
    public bool AutoObtainShopSpells { get; set; }

    // Spells the user has switched OFF for that shop trip, by Spells.Name. Names
    // rather than numbers because a data-set swap renumbers the rows; the skipped
    // set (rather than the wanted one) so a newly listed spell defaults to wanted.
    public List<string>? SkippedShopSpells { get; set; }
}
