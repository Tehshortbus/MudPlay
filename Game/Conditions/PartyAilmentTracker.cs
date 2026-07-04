using FujinTerm.Game.Remote;
using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Conditions;

// Inbound counterpart to AilmentSyncEngine. Mirrors a party member's
// curable-ailment state onto their PartyMember chip so the PartyWindow shows it
// and CastingDirector can party-cure them.
//
// Set — when a member running the same client catches a curable ailment (or is
// held), the outbound AilmentSyncEngine announces .@poisoned / .@blind /
// .@confused / .@diseased / .@held on say. The leading period is the
// say-shortcut, so other clients observe the bare token (Forged says
// "@poisoned"). We match that on the ChatChannel.Local channel and set the
// speaker's chip via PartyManager.SetMemberAilment. @held additionally pauses the
// leader through PartyEssentialHandlers.NotePause — a held member can't move, so
// the party waits for them. Our own announce echoes as You say "@poisoned" with a
// null speaker, so it's ignored here (our state is owned by ConditionTracker).
//
// Clear — there is no clear-side say broadcast (the outbound engine only
// telepaths @ok to the leader). Instead we clear a chip when we observe a cure
// land on the member: each configured cure spell's CasterMessage (OUR cast) and
// WitnessMessage (a cast by another member, seen in the room) templates are
// compiled to CasterMessageMatchers; a server line naming BOTH the cure spell AND
// the member (CasterMessageMatcher.ConfirmsSpellTarget) clears that member's chip
// — requiring the spell name too keeps an unrelated cast on the same member (a
// buff on a poisoned ally) from clearing the wrong chip. The witness path means a
// third-party observer clears the chip too, regardless of which member cast the
// cure. This catches both the CastingDirector auto-cure and a manual cast.
// Confusion has no cure spell in stock / ParaMUD, so a @confused chip has no
// cure-side clear path — it lingers until the member leaves the party (documented
// gap; confusion is short-lived server-side).
public sealed class PartyAilmentTracker : IDisposable
{
    // LogService category — appears as [PartyAilment] rows.
    public const string LogCategory = "PartyAilment";

    // Inbound say tokens (period already stripped by the say-shortcut). Mirrors
    // AilmentSyncEngine's outbound table minus the leading '.'. Held
    // (MovementPrevented) additionally pauses the leader — see OnChat.
    private static readonly (string Token, MessageFlags Flag)[] Tokens =
    {
        ("@poisoned", MessageFlags.Poisoned),
        ("@blind",    MessageFlags.Blinded),
        ("@confused", MessageFlags.Confused),
        ("@diseased", MessageFlags.Diseased),
        ("@held",     MessageFlags.MovementPrevented),
    };

    // The @-tokens this tracker consumes as inbound ailment announces. Exposed so
    // the remote-command engine can mark them reserved — they ride the say
    // channel as party-sync signals, not @-commands, so the engine must swallow
    // them silently instead of bouncing a "{command invalid}" reply back at the
    // announcing member.
    public static IReadOnlyList<string> AnnounceTokens { get; } =
        Array.ConvertAll(Tokens, t => t.Token);

    private readonly ChatRouter _chat;
    private readonly PartyManager _party;
    private readonly PartyEssentialHandlers _essentials;
    private readonly Func<IReadOnlyList<CureCastMatcher>> _readCureMatchers;
    private readonly LogService? _log;
    private LineExtractor? _lines;
    private bool _disposed;

    public PartyAilmentTracker(
        ChatRouter chat,
        PartyManager party,
        PartyEssentialHandlers essentials,
        Func<IReadOnlyList<CureCastMatcher>> readCureMatchers,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(essentials);
        ArgumentNullException.ThrowIfNull(readCureMatchers);
        _chat = chat;
        _party = party;
        _essentials = essentials;
        _readCureMatchers = readCureMatchers;
        _log = log;
        _chat.EntryClassified += OnChat;
    }

    // Subscribe to server lines for the cure-confirmation clear path. The
    // LineExtractor is swapped on reconnect, so this re-binds rather than taking
    // the extractor at construction (same shape as
    // CastingDirector.AttachLineExtractor).
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    private void OnChat(ChatLogEntry entry)
    {
        // Say only — the announce travels on the local/room channel.
        if (entry.Channel != ChatChannel.Local) return;
        // Null speaker = our own "You say" echo; our state is owned elsewhere.
        if (string.IsNullOrEmpty(entry.Speaker)) return;

        string msg = entry.Message.Trim();
        foreach ((string token, MessageFlags flag) in Tokens)
        {
            if (!msg.Equals(token, StringComparison.OrdinalIgnoreCase)) continue;
            _party.SetMemberAilment(entry.Speaker, flag, true);
            // Held also pauses the leader: a held member can't move, so the
            // party must wait for them exactly as an explicit @wait would.
            // The held member's own @ok (sent on cure / last-clear) releases
            // it via PartyEssentialHandlers.OnOk.
            if (flag == MessageFlags.MovementPrevented)
                _essentials.NotePause(entry.Speaker);
            _log?.Info(LogCategory, $"inbound {token} from {entry.Speaker}");
            return;
        }
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        IReadOnlyList<CureCastMatcher> matchers = _readCureMatchers();
        if (matchers.Count == 0) return;

        foreach (PartyMember m in _party.State.Members)
        {
            if (m.IsSelf) continue;
            // The game may print the cure target as either the full "Given
            // Family" name or just the given name — confirm against both so a
            // family-name member still clears.
            string given = GivenName(m.Name);
            foreach (CureCastMatcher cm in matchers)
            {
                // Require BOTH the cure spell's name and the member's name to
                // appear — a different spell landing on the same member (a buff
                // on a poisoned ally) must not clear the wrong chip. Match OUR
                // cast ("You cast cure on X!") or a cure another member casts
                // that we witness in the room ("Y casts cure on X!"); the
                // caster of a witnessed cure doesn't matter, only the spell and
                // the target do.
                bool hit =
                    cm.Caster.ConfirmsSpellTarget(line.Text, cm.SpellName, m.Name)
                 || cm.Caster.ConfirmsSpellTarget(line.Text, cm.SpellName, given)
                 || (cm.Witness is { } w
                     && (w.ConfirmsSpellTarget(line.Text, cm.SpellName, m.Name)
                      || w.ConfirmsSpellTarget(line.Text, cm.SpellName, given)));
                if (!hit) continue;
                _party.SetMemberAilment(m.Name, cm.Ailment, false);
                _log?.Info(LogCategory, $"cure confirmed ailment={cm.Ailment} target={m.Name}");
            }
        }
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= OnChat;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
    }
}

// One compiled cure-spell confirmation: the ailment it removes, the spell's name
// (so the spell slot is confirmed, not just the target), and matchers built from
// the spell's CasterMessage (OUR cast) and WitnessMessage (a cast by another
// member we see in the room — clears the chip for third-party observers). The
// witness matcher is null when the record has no witness template. Provided by
// AppServices from the live Spells settings + spellbook so re-configuring a cure
// spell takes effect without rebuilding the tracker.
public readonly record struct CureCastMatcher(
    MessageFlags Ailment, string SpellName,
    CasterMessageMatcher Caster, CasterMessageMatcher? Witness = null);
