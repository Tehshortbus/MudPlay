using System.Collections.Generic;
using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// Sole writer of <see cref="PlayerState.Encumbrance"/>. Subscribes to
/// <see cref="KnownPatterns.UserEncumbrance"/> via the
/// <see cref="MessageRouter"/> and extracts the bracket from the trailing
/// label, e.g. <c>"Encumbrance:    0/2880  -  None  [0%]"</c> →
/// <see cref="EncumbranceLevel.None"/>.
/// </summary>
public sealed partial class EncumbranceParser : IDisposable
{
    private readonly PlayerState _state;
    private readonly LogService? _log;
    private readonly IDisposable _sub;

    public EncumbranceParser(MessageRouter router, PlayerState state, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _log = log;
        _sub = router.Subscribe(KnownPatterns.UserEncumbrance, OnMatch);
    }

    public void Dispose() => _sub.Dispose();

    private void OnMatch(MatchResult match)
    {
        EncumbranceLevel level = ParseLevel(match.Text);
        if (level == EncumbranceLevel.Unknown) return;
        if (_state.Encumbrance == level) return;
        _state.Encumbrance = level;
        _log?.Debug("Encumbrance", $"observed {level} from '{match.Text}'");
    }

    /// <summary>
    /// Pulls the encumbrance bracket from an <c>Encumbrance:</c> line.
    /// Returns <see cref="EncumbranceLevel.Unknown"/> when the bracket
    /// token isn't recognisable — the server uses a small fixed set
    /// (None / Light / Medium / Heavy / Encumbered) but the parser is
    /// case-insensitive in case a custom realm tweaks the casing.
    /// </summary>
    public static EncumbranceLevel ParseLevel(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return EncumbranceLevel.Unknown;
        Match m = BracketRegex().Match(line);
        if (!m.Success) return EncumbranceLevel.Unknown;
        return s_brackets.TryGetValue(m.Groups[1].Value, out EncumbranceLevel lvl)
            ? lvl
            : EncumbranceLevel.Unknown;
    }

    private static readonly Dictionary<string, EncumbranceLevel> s_brackets =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["None"]       = EncumbranceLevel.None,
        ["Light"]      = EncumbranceLevel.Light,
        ["Medium"]     = EncumbranceLevel.Medium,
        ["Heavy"]      = EncumbranceLevel.Heavy,
        ["Encumbered"] = EncumbranceLevel.Encumbered,
    };

    [GeneratedRegex(@"-\s*(None|Light|Medium|Heavy|Encumbered)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BracketRegex();
}
