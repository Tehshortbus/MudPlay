using FujinTerm.Services;

namespace FujinTerm.Game;

/// <summary>
/// Writes observed status-line values from <see cref="WirePromptScanner"/>
/// into <see cref="PlayerState"/>. Sole writer of HP / MA / mana-type /
/// position fields (Phase 3 PR 3.5 enforces this via the IL-scan test).
/// </summary>
/// <remarks>
/// <para>
/// Subscribes to the wire stream rather than <see cref="MessageRouter"/>'s
/// <see cref="Services.Patterns.KnownPatterns.StatusLine"/> id because the
/// server overwrites the statline in place (CR + erase-line + new content
/// on the same row). By the time <see cref="Terminal.LineExtractor"/>
/// emits a row, only the last statline survives and intermediate HP / MA /
/// position changes are lost. Scanning the wire catches every update.
/// </para>
/// <para>
/// <see cref="PlayerState.MaxHp"/> / <see cref="PlayerState.MaxMa"/>
/// ratchet upward on every observation. A custom statline may include the
/// <c>%H</c>/<c>%M</c> max wildcards, but
/// <see cref="StatlinePromptRegexBuilder"/> emits those as <i>non-capturing</i>
/// digit runs — they render on the wire without feeding a second write path
/// into the max fields, so this parser stays the sole writer (Phase 3 PR 3.5).
/// </para>
/// </remarks>
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanner.PromptObserved -= OnPromptObserved;
    }
}
