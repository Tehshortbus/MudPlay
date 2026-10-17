using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;
using static MudPlay.Terminal.LineExtractor;

namespace MudPlay.Game.Combat;

// Generic, monster-agnostic combat-line recognizer. Subscribes to every dispatched
// line and, for lines inside a *Combat Engaged* … *Combat Off* window (the gating
// the realm itself emits), classifies the outcome from LINE COLOUR + wording +
// target — with no per-monster message data, so it works for any monster including
// the 15-20k a custom game can add.
//
// Colour signals, read from the SGR the game sent (the stored foreground index and
// bold flag), never from the palette the terminal paints with, so a colour scheme
// can't shift them: dark cyan (6, NON-bold) = a swing that missed or was dodged;
// dark-red (1, NON-bold) = a swing your armor deflected. The player's own hits
// render bright-red (1 + bold) and always carry a "for N damage" tail, so they
// classify by wording, not colour. Target is split us-vs-others by whether the
// line addresses "you".
//
// Bright cyan (6 + bold, or 14) is no miss: the game prints misses and dodges in
// dark cyan (user, 2026-10-10: always, as far as they know), and bright cyan is
// the room title's colour (GAME_MECHANICS "Room display parsing — the room title
// is positional, not just bright cyan"). Read as a miss, every room title shown
// while a fight was on was tagged a monster's miss, and one that leads with a
// monster's name ("Dire Wolf Lair") passed for that monster's attack (report
// paradigm-20261010-215619).
public sealed class CombatLineClassifier : IDisposable
{
    private readonly MessageRouter _router;
    private bool _inCombatWindow;
    private bool _disposed;

    // Recent combat-window lines + how each was classified, for the Wire Inspector's
    // classified view and the bug-report capture. Only lines inside a combat window
    // (plus the *Combat Engaged*/*Combat Off* markers) are kept, so the log is a
    // focused combat trace rather than the whole terminal stream — except a damage
    // line the round ledger read, which is kept wherever it falls. Bounded ring.
    private const int LogCap = 1000;
    private readonly object _logLock = new();
    private readonly LinkedList<ClassifiedLine> _log = new();

    // The round ledger's reading of a damage line reaches NoteLedger from a separate
    // LineDispatched handler, before or after OnLine sees the same line. The last
    // line OnLine saw (and whether it kept it) lets a late tag find its entry; an
    // early tag waits here for OnLine.
    private string? _lastSeenText;
    private bool _lastSeenRecorded;
    private (string Text, string Tag)? _pendingLedger;

    public CombatLineClassifier(MessageRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _router.LineDispatched += OnLine;
    }

    private void OnLine(EmittedLine line)
    {
        // Track the combat window off the server's own markers — these lines are not
        // combat outcomes themselves. LineDispatched fires per line, so the window is
        // already set by the time the round's outcome lines arrive.
        if (line.Text.StartsWith("*Combat Engaged*", StringComparison.Ordinal))
        {
            _inCombatWindow = true;
            Record(line.Text, CombatLineKind.None);
            Seen(line.Text, recorded: true);
            return;
        }
        if (line.Text.StartsWith("*Combat Off*", StringComparison.Ordinal))
        {
            Record(line.Text, CombatLineKind.None);   // record before closing the window
            Seen(line.Text, recorded: true);
            _inCombatWindow = false;
            return;
        }

        (TerminalColor fg, bool bold) = DominantForeground(line);
        CombatLineKind kind = Classify(line.Text, fg, bold, _inCombatWindow);
        string? ledger = TakePendingLedger(line.Text);
        bool recorded = _inCombatWindow || ledger is not null;
        if (recorded) Record(line.Text, kind, ledger);
        Seen(line.Text, recorded);
    }

    // Pure classification. Only lines inside a combat window
    // classify; outside, everything is None.
    public static CombatLineKind Classify(string text, TerminalColor fg, bool bold, bool inWindow)
    {
        if (!inWindow || string.IsNullOrEmpty(text)) return CombatLineKind.None;

        // Damage in any of the engine's wordings (DamageLineAttributor), colour-independent.
        bool hit = DamageLineAttributor.TryAttribute(text, Array.Empty<string>(), out DamageAttribution a);

        // Damage nobody dealt, on us: "You are poisoned for 2 damage!", "Your blood is
        // drained", the heavens' punishment bolt.
        if (hit && a.NoDealer && a.Target == DamageLineAttributor.Self) return CombatLineKind.DamageYou;

        // The smash penalty landing — a secondary effect of a smash (user, 2026-09-29).
        if (SmashedYouLine.IsMatch(text)) return CombatLineKind.SmashedYou;
        if (SmashedOtherLine.IsMatch(text)) return CombatLineKind.SmashedOther;

        // Damage the line itself credits to us without leading with "You": our room
        // spell on "your foes", or "Your <weapon> ...". Its "your" would otherwise
        // read as a monster hitting us (report paradigm-20261010-215619).
        if (hit && a.Source == DamageLineAttributor.Self) return CombatLineKind.PlayerHit;

        // The local player's own swing ("You hurl … for N damage!" / "You miss …!").
        if (text.StartsWith("You ", StringComparison.Ordinal))
        {
            if (hit) return CombatLineKind.PlayerHit;
            if (IsDarkCyan(fg, bold)) return CombatLineKind.PlayerMiss;
            return CombatLineKind.None;
        }

        // Incoming / third-party. Addressing "you"/"your" marks it as against us;
        // otherwise it landed on another player.
        bool vsYou = You.IsMatch(text);

        // Another fighter's outcome in the engine's "just …" wording, which carries no
        // colour cue of its own.
        if (text.Contains(" just glanced off of ", StringComparison.Ordinal))
            return vsYou ? CombatLineKind.ArmorBlockYou : CombatLineKind.ArmorBlockOther;
        if (text.Contains(" just dodged an attack from ", StringComparison.Ordinal))
            return vsYou ? CombatLineKind.DodgeYou : CombatLineKind.DodgeOther;
        if (text.Contains(" just missed an attack against ", StringComparison.Ordinal))
            return vsYou ? CombatLineKind.MonsterMissYou : CombatLineKind.MonsterMissOther;

        if (hit)
        {
            if (vsYou) return CombatLineKind.MonsterHitYou;
            // Not our own swing and not addressed to us. A monster's weapon hit on a
            // party member renders in the attack colour (red, indexed 1). A damage line
            // in the DEFAULT / white colour is instead a thorns/ShockShield reflect —
            // a worn item striking the attacker back ("The armour spikes stab <monster>
            // for N damage!"; the item wording varies: "collar spikes", …). There the
            // monster is the VICTIM, so it's retaliation, not a monster hit. Split by
            // colour: the reflect is white and follows the red hit that triggered it.
            return IsAttackRed(fg) ? CombatLineKind.MonsterHitOther : CombatLineKind.Reflect;
        }

        if (IsDarkCyan(fg, bold))
        {
            bool dodge = text.Contains("dodge", StringComparison.OrdinalIgnoreCase);
            if (dodge) return vsYou ? CombatLineKind.DodgeYou : CombatLineKind.DodgeOther;
            return vsYou ? CombatLineKind.MonsterMissYou : CombatLineKind.MonsterMissOther;
        }

        if (IsDarkRed(fg, bold))
            return vsYou ? CombatLineKind.ArmorBlockYou : CombatLineKind.ArmorBlockOther;

        return CombatLineKind.None;
    }

    // The same reading of one line taken on its own, as if a fight of ours were on.
    // For a reader that has to tell a monster's swing while we are not attacking
    // (our own combat window is shut then, though the room is still fighting).
    public static CombatLineKind ClassifyAsCombat(EmittedLine line)
    {
        (TerminalColor fg, bool bold) = DominantForeground(line);
        return Classify(line.Text, fg, bold, inWindow: true);
    }

    // "You are smashed to the ground!"
    private static readonly Regex SmashedYouLine =
        new(@"^You are smashed to the ground!", RegexOptions.Compiled);
    // "You smashed Bob to the ground!" / "Bob is smashed to the ground defenseless!" /
    // "The orc is smashed to the floor defenseless!"
    private static readonly Regex SmashedOtherLine =
        new(@"^(?:You smashed .+ to the ground!|.+ is smashed to the (?:ground|floor) defenseless!)", RegexOptions.Compiled);
    // "you" / "your" as an addressee — the us-vs-others split.
    private static readonly Regex You =
        new(@"\byou(?:r)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // dark cyan = indexed 6 without bold. Bright cyan (indexed 6 + bold, or 14) is
    // the room title's colour, NOT a miss — so bold disqualifies.
    private static bool IsDarkCyan(TerminalColor fg, bool bold) =>
        fg.Kind == ColorKind.Indexed && fg.Value == 6 && !bold;

    // dark red = indexed 1 without bold. Bright red (indexed 1 + bold) is the
    // player's own hit colour, NOT an armor block — so bold disqualifies.
    private static bool IsDarkRed(TerminalColor fg, bool bold) =>
        fg.Kind == ColorKind.Indexed && fg.Value == 1 && !bold;

    // The weapon-attack colour, bold-agnostic: any indexed-1 red. A monster's hit
    // (dark or bright red) uses it; a thorns/ShockShield reflect is white/default, so
    // this tells a real incoming hit apart from our retaliation among "for N damage"
    // lines that don't address "you".
    private static bool IsAttackRed(TerminalColor fg) =>
        fg.Kind == ColorKind.Indexed && fg.Value == 1;

    // The line's colour: the foreground of its first visible (non-space) glyph.
    // Combat lines are emitted in a single colour, so the first glyph is
    // representative; blanks fall back to Default.
    private static (TerminalColor Fg, bool Bold) DominantForeground(EmittedLine line)
    {
        CellAttributes[] attrs = line.Attributes;
        string text = line.Text;
        int n = Math.Min(text.Length, attrs.Length);
        for (int i = 0; i < n; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return (attrs[i].Foreground, (attrs[i].Flags & CellFlags.Bold) != 0);
        }
        return (TerminalColor.Default, false);
    }

    private void Record(string text, CombatLineKind kind, string? ledger = null)
    {
        lock (_logLock)
        {
            _log.AddLast(new ClassifiedLine(text, kind, ledger));
            while (_log.Count > LogCap) _log.RemoveFirst();
        }
    }

    private void Seen(string text, bool recorded)
    {
        _lastSeenText = text;
        _lastSeenRecorded = recorded;
    }

    private string? TakePendingLedger(string text)
    {
        if (_pendingLedger is not { } p || p.Text != text) return null;
        _pendingLedger = null;
        return p.Tag;
    }

    // The round ledger read text as tag. Attach it to that line's entry — recording
    // the line if OnLine skipped it outside a combat window — or hold it for OnLine
    // when the ledger saw the line first.
    public void NoteLedger(string text, string tag)
    {
        if (_lastSeenText != text)
        {
            _pendingLedger = (text, tag);
            return;
        }
        lock (_logLock)
        {
            if (_lastSeenRecorded && _log.Last is { } last && last.Value.Text == text)
            {
                last.Value = last.Value with { Ledger = tag };
                return;
            }
        }
        Record(text, CombatLineKind.None, tag);
        _lastSeenRecorded = true;
    }

    // The most recent up-to-`max` combat-window lines with their classification.
    public IReadOnlyList<ClassifiedLine> SnapshotLog(int max = LogCap)
    {
        lock (_logLock)
        {
            int skip = Math.Max(0, _log.Count - max);
            return _log.Skip(skip).ToArray();
        }
    }

    // Render the classified log as text: each line, with a "[Combat: <label>]" tag
    // appended to the lines that classified and a "[Ledger: <who> → <whom> <n>]" tag
    // to the damage lines the round ledger read. Shared by the Wire Inspector's
    // classified pane and the bug-report capture.
    public string RenderLog(int max = LogCap)
    {
        var sb = new System.Text.StringBuilder();
        foreach (ClassifiedLine e in SnapshotLog(max))
        {
            sb.Append(e.Text);
            string label = Label(e.Kind);
            if (label.Length != 0) sb.Append("    [Combat: ").Append(label).Append(']');
            if (e.Ledger is { } ledger) sb.Append("    [Ledger: ").Append(ledger).Append(']');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Note a monster death for the Wire Inspector's Classified view. Deaths are
    // recognized generically from the exp signal (a kill's "You gain N experience."
    // before its *Combat Off*), so this marks the kill with the exp gained. Recorded
    // regardless of the combat window — the death fires on the *Combat Off* that
    // closes it.
    public void NoteMonsterDeath(int? exp)
    {
        string tag = exp is { } e ? $"[Monster Death: +{e} exp]" : "[Monster Death]";
        Record(tag, CombatLineKind.None);
    }

    // Drop the classified-line log (Wire Inspector's Clear button).
    public void Clear()
    {
        lock (_logLock) _log.Clear();
        _pendingLedger = null;
        _lastSeenText = null;
    }

    // Human-readable tag for the classified view; empty for None.
    public static string Label(CombatLineKind kind) => kind switch
    {
        CombatLineKind.PlayerHit        => "You Hit",
        CombatLineKind.DamageYou        => "Damage (you)",
        CombatLineKind.SmashedYou       => "Smashed (you)",
        CombatLineKind.SmashedOther     => "Smashed (other)",
        CombatLineKind.PlayerMiss       => "You Miss",
        CombatLineKind.MonsterHitYou    => "Monster Hit (you)",
        CombatLineKind.MonsterHitOther  => "Monster Hit (other)",
        CombatLineKind.ArmorBlockYou    => "Armor Block (you)",
        CombatLineKind.ArmorBlockOther  => "Armor Block (other)",
        CombatLineKind.DodgeYou         => "You Dodge",
        CombatLineKind.DodgeOther       => "Other Dodge",
        CombatLineKind.MonsterMissYou   => "Monster Miss (you)",
        CombatLineKind.MonsterMissOther => "Monster Miss (other)",
        CombatLineKind.Reflect          => "Reflect",
        _ => "",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLine;
    }
}

// One classified combat-window line: the (de-ANSI'd) text, how it was read, and for a
// damage line how the round ledger read it (RoundTotalsFormatter.LedgerTag).
public readonly record struct ClassifiedLine(string Text, CombatLineKind Kind, string? Ledger = null);
