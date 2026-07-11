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
// held), the outbound AilmentSyncEngine announces on say: the curable four as a
// paired toggle '.@poisoned on' … '.@poisoned off', held as a bare '.@held'. The
// leading period is the say-shortcut, so other clients observe the bare token
// (Forged says "@poisoned on"). We match that on the ChatChannel.Local channel
// and set the speaker's chip via PartyManager.SetMemberAilment (a bare token or
// the 'on' suffix sets it, 'off' clears it). @held additionally pauses the leader
// through PartyEssentialHandlers.NotePause — a held member can't move, so the
// party waits for them. Our own announce echoes as You say "@poisoned on" with a
// null speaker, so it's ignored here (our state is owned by ConditionTracker).
//
// Clear — the authoritative clear is the paired '.@X off' say above: it fires on
// every curable-ailment clear (including a natural wear-off with no cure cast),
// so a chip never sticks. Held has no off-signal — its chip and leader-pause
// release ride the member's @ok on last-clear (PartyEssentialHandlers.OnOk).
// A second, faster clear path also runs: when we witness a cure land on the
// member we drop the chip immediately, before their off-signal round-trips. Each
// configured cure spell's CasterMessage (OUR cast) and WitnessMessage (a cast by
// another member, seen in the room) templates are compiled to
// CasterMessageMatchers; a server line naming BOTH the cure spell AND the member
// (CasterMessageMatcher.ConfirmsSpellTarget) clears that member's chip —
// requiring the spell name too keeps an unrelated cast on the same member (a buff
// on a poisoned ally) from clearing the wrong chip. Both paths are idempotent.
//
// Reconcile — a third path repairs a chip the push signals ever missed (a dropped
// 'off', a cure witnessed out of the room). A member's @status reply carries an
// ailment clause ("no ailments" / "ailments: blind, poisoned") on whatever channel
// the @status rode; HandleStatusReply reads it and sets the member's four curable
// chips to exactly the reported set — present set, absent cleared. Pull-based, so
// asking @status resyncs a member whose state drifted. Held is excluded (rides @ok).
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
        // Null speaker = our own "You say" / telepath echo; our state is owned
        // elsewhere (ConditionTracker).
        if (string.IsNullOrEmpty(entry.Speaker)) return;
        string speaker = entry.Speaker;

        // Say-channel ailment toggle: '.@poisoned on' / '.@poisoned off' / '.@held'.
        if (entry.Channel == ChatChannel.Local)
            HandleAilmentToggle(entry, speaker);

        // @status reply reconcile — the reply rides whichever channel the @status
        // arrived on (telepath / gangpath / say), so watch all three.
        if (entry.Channel is ChatChannel.TelepathIncoming
                           or ChatChannel.Gangpath
                           or ChatChannel.Local)
            HandleStatusReply(entry, speaker);
    }

    private void HandleAilmentToggle(ChatLogEntry entry, string speaker)
    {
        string msg = entry.Message.Trim();
        foreach ((string token, MessageFlags flag) in Tokens)
        {
            if (!msg.StartsWith(token, StringComparison.OrdinalIgnoreCase)) continue;
            // Optional on/off suffix: '.@blind on' sets the chip, '.@blind off'
            // clears it. A bare '.@blind' (held, or an older client) reads as on.
            // The tail must be empty / on / off — anything else means the spoken
            // word merely starts with our token and isn't the sync signal.
            string suffix = msg[token.Length..].Trim();
            bool off = suffix.Equals("off", StringComparison.OrdinalIgnoreCase);
            if (suffix.Length != 0 && !off
                && !suffix.Equals("on", StringComparison.OrdinalIgnoreCase))
                continue;

            _party.SetMemberAilment(speaker, flag, !off);
            // Held also pauses the leader on set: a held member can't move, so
            // the party must wait for them exactly as an explicit @wait would.
            // Held sends no 'off' — its release rides the member's @ok (sent on
            // cure / last-clear) via PartyEssentialHandlers.OnOk.
            if (flag == MessageFlags.MovementPrevented && !off)
                _essentials.NotePause(speaker);
            _log?.Info(LogCategory, $"inbound {token} {(off ? "off" : "on")} from {speaker}");
            return;
        }
    }

    // Reconcile a member's curable-ailment chips against a @status reply — the
    // pull-based counterpart to the push-based .@X on/off say. The reply body
    // (wrapped in { } by RemoteCommandManager.SendReply) ends with an ailment
    // clause: "no ailments" or "ailments: blind, poisoned", spelled with the same
    // plain words PartyEssentialHandlers.OnStatus emits (our say tokens minus the
    // '@'). We set present ailments and clear absent ones, so a chip a dropped
    // 'off' left stuck still clears the moment anyone asks @status. Held is never
    // in the clause — its chip rides the @ok lifecycle — so it's left untouched.
    // A non-status telepath has no ailment clause and no-ops here.
    private void HandleStatusReply(ChatLogEntry entry, string speaker)
    {
        string msg = entry.Message.Trim();
        if (msg.StartsWith('{') && msg.EndsWith('}')) msg = msg[1..^1].Trim();

        int listAt = msg.IndexOf("ailments:", StringComparison.OrdinalIgnoreCase);
        bool none = msg.Contains("no ailments", StringComparison.OrdinalIgnoreCase);
        if (listAt < 0 && !none) return; // no ailment clause → not a @status reply

        // Exact-match the comma list after "ailments:" so a word can't be matched
        // as a substring of another (none of the four overlap today, but split
        // keeps it robust against future words).
        string tail = listAt >= 0 ? msg[(listAt + "ailments:".Length)..] : string.Empty;
        HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in tail.Split(',',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            reported.Add(part);

        foreach ((string token, MessageFlags flag) in Tokens)
        {
            if (flag == MessageFlags.MovementPrevented) continue; // held rides @ok
            _party.SetMemberAilment(speaker, flag, reported.Contains(token[1..]));
        }
        _log?.Info(LogCategory,
            $"@status reconcile from {speaker}: {(reported.Count == 0 ? "no ailments" : string.Join(",", reported))}");
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
