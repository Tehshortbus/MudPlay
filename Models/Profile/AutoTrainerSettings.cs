using System.Collections.Generic;

namespace FujinTerm.Models.Profile;

// Per-character Auto-Trainer settings — the "AutoTrainer" entry in
// CharacterProfile.Settings. Surfaced by the Settings → Auto-Trainer tab.
public sealed class AutoTrainerSettings
{
    // Master toggle: when running a loop / auto-lair and a level-up is
    // available, detour to the appropriate trainer and train.
    public bool AutoTrain { get; set; }

    // Cascading toggle (only meaningful when AutoTrain is on): after training a
    // level, drive the train stats screen to apply the CP plan's row for the
    // new level.
    public bool AutoTrainStats { get; set; }

    // Buffer of trainable-but-untrained levels to always keep banked. Auto-train
    // (and the manual Train Now) stops once this many levels are still reachable
    // from banked exp, so the character always carries a reserve. 0 (the
    // default) trains every banked level.
    public int LevelsToKeep { get; set; }

    // When on, broadcast "I can now train to level: N" on AnnounceChannel each
    // time a live experience gain makes a new level trainable — i.e. a
    // Level-Projection row's "Exp to level" reaches 0. Banked levels already
    // trainable on login / at the next stat poll are seeded silently, so this
    // only fires on transitions that happen during play.
    public bool AnnounceLevelUps { get; set; }

    // Chat channel the level-up announce is sent on. Defaults to Gangpath
    // (most useful to a party).
    public AnnounceChannel AnnounceChannel { get; set; } = AnnounceChannel.Gangpath;

    // Trainer rows the user has switched OFF for auto-train, keyed by
    // Game.GameData.TrainerShop.RowKey (shop/map/room) so a multi-room shop's
    // rooms toggle independently. Storing the disabled set (rather than the
    // allowed set) keeps the JSON small and means newly-discovered trainers
    // default to allowed. null / empty = every discovered trainer is allowed.
    public List<string>? DisabledTrainers { get; set; }
}
