namespace MudPlay.Models.Profile;

// Per-character setting for the route Details window: colour each monster's name by
// its live "Hits You %" — the same Monster Intel weighted incoming-hit figure —
// instead of by alignment. A low chance-to-hit-you reads green (safe), a high one
// red (dangerous), with a yellow middle band; the two boundaries are user-adjustable.
// Saved on the character profile so each character keeps its own toggle + band split
// and the Details window opens the way that character last left it. A null on the
// profile means off, with the factory 15 / 45 split.
public sealed class MonsterHitColorSettings
{
    // Colour monsters by Hits-You-% (default off → the prior alignment tint).
    public bool Enabled { get; set; }

    // Green→yellow boundary (%): a monster whose hit% is at or below this reads green.
    public int GreenMax { get; set; } = DefaultGreenMax;

    // Yellow→red boundary (%): above GreenMax and at or below this reads yellow; above
    // this reads red.
    public int YellowMax { get; set; } = DefaultYellowMax;

    public const int DefaultGreenMax = 15;
    public const int DefaultYellowMax = 45;
}
