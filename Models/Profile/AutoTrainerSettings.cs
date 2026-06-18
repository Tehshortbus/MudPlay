using System.Collections.Generic;

namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character Auto-Trainer settings — the <c>"AutoTrainer"</c> entry in
/// <see cref="CharacterProfile.Settings"/>. Surfaced by the Settings →
/// Auto-Trainer tab.
/// </summary>
public sealed class AutoTrainerSettings
{
    /// <summary>
    /// Master toggle: when running a loop / auto-lair and a level-up is
    /// available, detour to the appropriate trainer and <c>train</c>.
    /// </summary>
    public bool AutoTrain { get; set; }

    /// <summary>
    /// Cascading toggle (only meaningful when <see cref="AutoTrain"/> is on):
    /// after training a level, drive the <c>train stats</c> screen to apply the
    /// CP plan's row for the new level.
    /// </summary>
    public bool AutoTrainStats { get; set; }

    /// <summary>
    /// Buffer of trainable-but-untrained levels to always keep banked. Auto-train
    /// (and the manual Train Now) stops once this many levels are still reachable
    /// from banked exp, so the character always carries a reserve. <c>0</c> (the
    /// default) trains every banked level.
    /// </summary>
    public int LevelsToKeep { get; set; }

    /// <summary>
    /// When on, broadcast "I can now train to level: N" on
    /// <see cref="AnnounceChannel"/> each time a live experience gain makes a new
    /// level trainable — i.e. a Level-Projection row's "Exp to level" reaches 0.
    /// Banked levels already trainable on login / at the next stat poll are seeded
    /// silently, so this only fires on transitions that happen during play.
    /// </summary>
    public bool AnnounceLevelUps { get; set; }

    /// <summary>
    /// Chat channel the level-up announce is sent on. Defaults to
    /// <see cref="Profile.AnnounceChannel.Gangpath"/> (most useful to a party).
    /// </summary>
    public AnnounceChannel AnnounceChannel { get; set; } = AnnounceChannel.Gangpath;

    /// <summary>
    /// Trainer rows the user has switched OFF for auto-train, keyed by
    /// <see cref="FujinTerm.Game.GameData.TrainerShop.RowKey"/> (<c>shop/map/room</c>)
    /// so a multi-room shop's rooms toggle independently. Storing the disabled set
    /// (rather than the allowed set) keeps the JSON small and means
    /// newly-discovered trainers default to allowed. <c>null</c> / empty = every
    /// discovered trainer is allowed.
    /// </summary>
    public List<string>? DisabledTrainers { get; set; }
}
