using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// Reads the status-line prompt and writes the captured values into
/// <see cref="PlayerState"/>. Subscribes to
/// <see cref="KnownPatterns.StatusLine"/> via <see cref="MessageRouter"/>;
/// fan-out semantics let other consumers (the Phase 1 LineExtractor's
/// prompt detector, future statline editor preview) match the same line.
/// </summary>
/// <remarks>
/// <para>
/// Capture groups (from the default registry's port of Megamind's regex):
/// </para>
/// <list type="bullet">
///   <item><description><c>hp</c> — current HP (1–4 digits).</description></item>
///   <item><description><c>type</c> — <c>MA</c> or <c>KAI</c>. Absent for classes with no mana pool.</description></item>
///   <item><description><c>mana</c> — current MA / KAI value (1–3 digits).</description></item>
///   <item><description><c>statea</c> — <c>Resting</c> or <c>Meditating</c> when the parens come before the closing bracket.</description></item>
///   <item><description><c>stateb</c> — same set, when the parens come after.</description></item>
/// </list>
/// <para>
/// <see cref="PlayerState.MaxHp"/> / <see cref="PlayerState.MaxMa"/> ratchet
/// upward on every observed value. Phase 12 statline gains <c>%H</c>/<c>%M</c>
/// wildcards that emit explicit denominators; the parser will pick those up
/// when the regex carries the new captures.
/// </para>
/// </remarks>
public sealed class PromptParser : IDisposable
{
    private readonly IDisposable _statusLineSub;
    private bool _disposed;

    public PlayerState State { get; }

    public PromptParser(MessageRouter router, PlayerState state)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        _statusLineSub = router.Subscribe(KnownPatterns.StatusLine, OnStatusLine);
    }

    private void OnStatusLine(MatchResult result)
    {
        IReadOnlyList<string> g = result.Groups;
        // Group order from the StatusLine regex: hp / type / mana / statea / stateb.
        if (g.Count < 1) return;

        if (int.TryParse(g[0], out int hp))
        {
            State.Hp = hp;
            if (hp > State.MaxHp) State.MaxHp = hp;
        }

        string typeRaw = SafeGroup(g, 1);
        State.ManaType = typeRaw switch
        {
            "MA"  => ManaType.Mana,
            "KAI" => ManaType.Kai,
            _      => ManaType.None,
        };

        if (State.ManaType == ManaType.None)
        {
            State.Ma = 0;
            State.MaxMa = 0;
        }
        else if (int.TryParse(SafeGroup(g, 2), out int mana))
        {
            State.Ma = mana;
            if (mana > State.MaxMa) State.MaxMa = mana;
        }

        string state = !string.IsNullOrEmpty(SafeGroup(g, 3))
            ? g[3]
            : SafeGroup(g, 4);

        State.Position = state switch
        {
            "Resting"    => PlayerPosition.Resting,
            "Meditating" => PlayerPosition.Meditating,
            _            => PlayerPosition.Standing,
        };

        State.HasPromptData = true;
    }

    private static string SafeGroup(IReadOnlyList<string> groups, int index)
        => (uint)index < (uint)groups.Count ? groups[index] : string.Empty;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statusLineSub.Dispose();
    }
}
