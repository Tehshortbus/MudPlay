using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using static MudPlay.Terminal.LineExtractor;

namespace MudPlay.Game.Combat;

// Decides which finished terminal rows are left off the screen (Settings → Combat,
// "Hide combat lines"). Display only: the emulator asks after every reader of the line
// has had it, so combat, triggers, the logs and the Wire Inspector see exactly what
// they would have.
//
// CombatLineClassifier splits a line by outcome and by whether it addresses us, but
// reads every other fighter's swing as a monster's. These boxes are by who swung, so
// the leading name is read here against the party and the known players.
public sealed class CombatLineDisplayFilter : IDisposable
{
    private readonly MessageRouter _router;
    private readonly Func<IReadOnlyCollection<string>> _partyNames;
    private readonly Func<string, bool> _isPlayer;
    private readonly Func<bool> _lastLineUnrecognized;
    private readonly LogService? _log;

    private HashSet<CombatLineCategory> _hidden = new();
    private readonly Dictionary<CombatLineCategory, int> _hiddenCounts = new();
    private bool _inCombatWindow;

    // The row being finished: what its line read as, and whether a prompt shares it.
    private CombatLineCategory? _rowCategory;
    private bool _rowHasPrompt;

    public CombatLineDisplayFilter(
        MessageRouter router,
        Func<IReadOnlyCollection<string>> partyNames,
        Func<string, bool> isPlayer,
        Func<bool> lastLineUnrecognized,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(partyNames);
        ArgumentNullException.ThrowIfNull(isPlayer);
        ArgumentNullException.ThrowIfNull(lastLineUnrecognized);
        _router = router;
        _partyNames = partyNames;
        _isPlayer = isPlayer;
        _lastLineUnrecognized = lastLineUnrecognized;
        _log = log;
        _router.LineDispatched += OnLine;
    }

    public bool HidesUnknown => _hidden.Contains(CombatLineCategory.Unknown);

    // The client is printing to the terminal itself: nothing it says is the game's.
    public bool ClientIsWriting { get; set; }

    public void SetHidden(IEnumerable<CombatLineCategory>? categories)
    {
        HashSet<CombatLineCategory> now = categories is null ? new() : new(categories);
        if (now.SetEquals(_hidden)) return;
        _hidden = now;
        _log?.Info("CombatLines", now.Count == 0
            ? "Every combat line is shown."
            : $"Hiding combat lines from the terminal: {string.Join(", ", now.Order())}.");
    }

    // Asked by the emulator as a row is finished; answers for that row only.
    public bool TakeRowHidden()
    {
        CombatLineCategory? category = _rowCategory;
        bool hasPrompt = _rowHasPrompt;
        _rowCategory = null;
        _rowHasPrompt = false;
        // The statline and what was typed after it are never a fight's output.
        if (ClientIsWriting || hasPrompt || category is not { } read || !_hidden.Contains(read)) return false;
        if (read == CombatLineCategory.Unknown && !_lastLineUnrecognized()) return false;
        _hiddenCounts[read] = _hiddenCounts.GetValueOrDefault(read) + 1;
        return true;
    }

    // For the bug report, whose scrollback is missing whatever this hid.
    public string Summary()
    {
        if (_hidden.Count == 0 && _hiddenCounts.Count == 0) return "Every combat line is shown.";
        string hidden = _hidden.Count == 0 ? "(nothing now)" : string.Join(", ", _hidden.Order());
        string counts = _hiddenCounts.Count == 0
            ? "none yet"
            : string.Join(", ", _hiddenCounts.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"));
        return $"Hidden from the terminal and scrollback (the wire sections still have them): {hidden}.\n\n"
            + $"Lines hidden this session: {counts}.";
    }

    private void OnLine(EmittedLine line)
    {
        bool engaged = line.Text.StartsWith("*Combat Engaged*", StringComparison.Ordinal);
        bool off = line.Text.StartsWith("*Combat Off*", StringComparison.Ordinal);
        if (line.IsPromptLine)
        {
            _rowHasPrompt = true;
            _rowCategory = null;
        }
        else if (_hidden.Count > 0)
        {
            _rowCategory = engaged || off ? CombatLineCategory.CombatMarkers : Read(line);
        }
        if (engaged) _inCombatWindow = true;
        if (off) _inCombatWindow = false;
    }

    private CombatLineCategory? Read(EmittedLine line)
    {
        string text = line.Text.Trim();
        if (text.Length == 0 || ClientNotice.IsNotice(text)) return null;
        if (Matches(KnownPatterns.UserGainExperience, line)) return CombatLineCategory.Experience;
        if (Matches(KnownPatterns.CashFromKill, line)) return CombatLineCategory.CoinDrops;
        if (Matches(KnownPatterns.PartyAttackAnnounce, line)
            || Matches(KnownPatterns.PlayerAttacksYou, line)
            || Matches(KnownPatterns.PartyCastAnnounce, line))
            return CombatLineCategory.AttackStarts;

        CombatLineKind kind = CombatLineClassifier.ClassifyAsCombat(line);
        // A monster can hit someone who isn't fighting back, so damage counts with no
        // window open. A colour alone doesn't: dark cyan text outside a fight is no miss.
        if (kind != CombatLineKind.None && (_inCombatWindow || LandedOutsideAFight(kind)))
            return ByWhoActed(text, kind);
        return _inCombatWindow ? CombatLineCategory.Unknown : null;
    }

    private static bool LandedOutsideAFight(CombatLineKind kind) => kind is
        CombatLineKind.PlayerHit or CombatLineKind.DamageYou or CombatLineKind.MonsterHitYou
        or CombatLineKind.MonsterHitOther or CombatLineKind.Reflect
        or CombatLineKind.SmashedYou or CombatLineKind.SmashedOther;

    private CombatLineCategory ByWhoActed(string text, CombatLineKind kind)
    {
        if (kind == CombatLineKind.DamageYou) return CombatLineCategory.DamageNoAttacker;
        if (kind == CombatLineKind.SmashedYou) return CombatLineCategory.MonsterHitsOnYou;

        bool landed = kind is CombatLineKind.PlayerHit or CombatLineKind.MonsterHitYou
            or CombatLineKind.MonsterHitOther or CombatLineKind.SmashedOther or CombatLineKind.Reflect;
        if (text.StartsWith("You ", StringComparison.Ordinal) || text.StartsWith("Your ", StringComparison.Ordinal))
            return landed ? CombatLineCategory.YourHits : CombatLineCategory.YourMisses;
        if (DamageLineAttributor.LeadingActor(text, _partyNames()) is not null)
            return landed ? CombatLineCategory.PartyHits : CombatLineCategory.PartyMisses;
        if (!text.StartsWith("The ", StringComparison.Ordinal) && _isPlayer(text.Split(' ')[0]))
            return landed ? CombatLineCategory.PlayerHits : CombatLineCategory.PlayerMisses;

        if (kind == CombatLineKind.Reflect) return CombatLineCategory.Reflects;
        bool onUs = kind is CombatLineKind.MonsterHitYou or CombatLineKind.MonsterMissYou
            or CombatLineKind.ArmorBlockYou or CombatLineKind.DodgeYou;
        return (onUs, landed) switch
        {
            (true, true)   => CombatLineCategory.MonsterHitsOnYou,
            (true, false)  => CombatLineCategory.MonsterMissesOnYou,
            (false, true)  => CombatLineCategory.MonsterHitsOnOthers,
            (false, false) => CombatLineCategory.MonsterMissesOnOthers,
        };
    }

    private bool Matches(string patternId, EmittedLine line) =>
        _router.TryGetPattern(patternId, out IMessagePattern pattern) && pattern.TryMatch(line, out _);

    public void Dispose() => _router.LineDispatched -= OnLine;
}
