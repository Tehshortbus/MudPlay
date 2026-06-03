namespace FujinTerm.Models.Profile;

/// <summary>
/// Persisted snapshot of the most recent
/// <see cref="Game.PlayerStats"/> capture — written by
/// <see cref="Game.StatParser"/> each time it closes a successful
/// scan window, rehydrated into <see cref="Game.PlayerStats"/> on
/// <see cref="Services.ProfileService.ProfileLoaded"/>. The point is
/// that when the user closes the client and reconnects, the live
/// state surfaces start with the last-observed values instead of
/// zeros — the next <c>stat</c> / <c>exp</c> just verifies / updates
/// rather than establishing-from-scratch.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors every field on <see cref="Game.PlayerStats"/> 1:1.
/// Whenever a field is added there, this DTO and the snapshot /
/// hydrate paths in <see cref="Game.StatParser"/> need to grow with
/// it. (A test pins the field-count parity so a future addition
/// can't silently drift.)
/// </para>
/// <para>
/// Stored on <see cref="CharacterProfile.LastKnownStats"/>, which
/// the app's save-on-close hook
/// (<c>MainWindow.Closing</c>) flushes to disk for any loaded named
/// profile. A draft profile (no name) doesn't persist — same rule
/// as the rest of the profile DTO.
/// </para>
/// </remarks>
public sealed class LastKnownStats
{
    /// <summary>JSON schema version for forward-compat migrations.</summary>
    public int SchemaVersion { get; set; } = 1;

    // ----- Identity ------------------------------------------------------
    public string Name { get; set; } = string.Empty;
    public string Race { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;

    // ----- Progression ---------------------------------------------------
    public int Level { get; set; }
    public int Exp { get; set; }
    public int Lives { get; set; }
    public int Cp { get; set; }
    public int ExpToNext { get; set; }
    public int LevelExpSpan { get; set; }
    public int LevelPercent { get; set; }

    // ----- Vitals --------------------------------------------------------
    public int Hits { get; set; }
    public int MaxHits { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    public int Kai { get; set; }
    public int MaxKai { get; set; }
    public int ArmourClass { get; set; }
    public int MaxArmourClass { get; set; }

    // ----- Primary stats -------------------------------------------------
    public int Strength { get; set; }
    public int Intellect { get; set; }
    public int Willpower { get; set; }
    public int Agility { get; set; }
    public int Health { get; set; }
    public int Charm { get; set; }

    // ----- Secondary skills ----------------------------------------------
    public int Perception { get; set; }
    public int Stealth { get; set; }
    public int Thievery { get; set; }
    public int Traps { get; set; }
    public int Picklocks { get; set; }
    public int Tracking { get; set; }
    public int MartialArts { get; set; }
    public int MagicRes { get; set; }
    public int Spellcasting { get; set; }
}
