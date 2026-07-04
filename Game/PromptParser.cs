using FujinTerm.Services;

namespace FujinTerm.Game;

// Writes observed status-line values from WirePromptScanner into
// PlayerState. Sole writer of HP / MA / mana-type / position fields (the
// IL-scan test enforces this).
//
// Subscribes to the wire stream rather than MessageRouter's StatusLine id
// because the server overwrites the statline in place (CR + erase-line + new
// content on the same row). By the time LineExtractor emits a row, only the
// last statline survives and intermediate HP / MA / position changes are
// lost. Scanning the wire catches every update.
//
// MaxHp / MaxMa ratchet upward on every observation. A custom statline may
// include the %H / %M max wildcards, but StatlinePromptRegexBuilder emits
// those as non-capturing digit runs — they render on the wire without
// feeding a second write path into the max fields, so this parser stays the
// sole writer. The high-water mark reads low until the character is first
// seen at full; the authoritative ceilings come from the stat screen via
// ApplyStatScreenMax (routed here to preserve sole ownership).
public sealed class PromptParser : IDisposable
{
    private readonly WirePromptScanner _scanner;
    private bool _disposed;

    public PlayerState State { get; }

    public PromptParser(WirePromptScanner scanner, PlayerState state)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        _scanner = scanner;
        _scanner.PromptObserved += OnPromptObserved;
    }

    private void OnPromptObserved(PromptObservation obs)
    {
        State.Hp = obs.Hp;
        if (obs.Hp > State.MaxHp) State.MaxHp = obs.Hp;

        State.ManaType = obs.ManaType;
        if (obs.ManaType == ManaType.None)
        {
            State.Ma = 0;
            State.MaxMa = 0;
        }
        else
        {
            State.Ma = obs.Mana;
            if (obs.Mana > State.MaxMa) State.MaxMa = obs.Mana;
        }

        State.Position = obs.Position;
        State.HasPromptData = true;
    }

    // Snap MaxHp / MaxMa to the authoritative ceilings read off the stat
    // screen. The standard status line carries only current HP / MA, so the
    // prompt path learns the maxima as a high-water mark that reads low until
    // the character is observed at full — the stat screen is the true source,
    // so this overrides even downward. Routed through the parser to keep it
    // the sole writer of the max fields. A non-positive value is ignored so a
    // failed / absent parse can't wipe an already-learned max.
    public void ApplyStatScreenMax(int maxHp, int maxMa)
    {
        if (maxHp > 0) State.MaxHp = maxHp;
        if (maxMa > 0) State.MaxMa = maxMa;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanner.PromptObserved -= OnPromptObserved;
    }
}
