using FujinTerm.Game.Remote;
using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Conditions;

/// <summary>
/// Inbound counterpart to <see cref="AilmentSyncEngine"/>. Mirrors a party
/// member's curable-ailment state onto their <see cref="PartyMember"/> chip so
/// the PartyWindow shows it and <see cref="CastingDirector"/> can party-cure
/// them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Set</b> — when a member running FujinTerm catches a curable ailment (or is
/// held), the outbound <see cref="AilmentSyncEngine"/> announces
/// <c>.@poisoned</c> / <c>.@blind</c> / <c>.@confused</c> / <c>.@diseased</c> /
/// <c>.@held</c> on say. The leading period is the say-shortcut, so other clients
/// observe the bare token (<c>Forged says "@poisoned"</c>). We match that on the
/// <see cref="ChatChannel.Local"/> channel and set the speaker's chip via
/// <see cref="PartyManager.SetMemberAilment"/>. <c>@held</c> additionally pauses
/// the leader through <see cref="PartyEssentialHandlers.NotePause"/> — a held
/// member can't move, so the party waits for them. Our own announce echoes as
/// <c>You say "@poisoned"</c> with a null speaker, so it's ignored here (our
/// state is owned by <see cref="ConditionTracker"/>).
/// </para>
/// <para>
/// <b>Clear</b> — there is no clear-side say broadcast (the outbound engine only
/// telepaths <c>@ok</c> to the leader). Instead we clear a chip when we observe a
/// cure land on the member: each configured cure spell's
/// <see cref="MessageRecord.CasterMessage"/> (OUR cast) and
/// <see cref="MessageRecord.WitnessMessage"/> (a cast by another member, seen in
/// the room) templates are compiled to <see cref="CasterMessageMatcher"/>s; a
/// server line naming BOTH the cure spell AND the member
/// (<see cref="CasterMessageMatcher.ConfirmsSpellTarget"/>) clears that member's
/// chip — requiring the spell name too keeps an unrelated cast on the same member
/// (a buff on a poisoned ally) from clearing the wrong chip. The witness path
/// means a third-party observer clears the chip too, regardless of which member
/// cast the cure. This catches both the
/// <see cref="CastingDirector"/> auto-cure and a manual cast. Confusion has no
/// cure spell in stock / ParaMUD, so a <c>@confused</c> chip has no cure-side
/// clear path — it lingers until the member leaves the party (documented gap;
/// confusion is short-lived server-side).
/// </para>
/// </remarks>
public sealed class PartyAilmentTracker : IDisposable
{
    /// <summary>LogService category — appears as <c>[PartyAilment]</c> rows.</summary>
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

    /// <summary>
    /// Subscribe to server lines for the cure-confirmation clear path. The
    /// LineExtractor is swapped on reconnect, so this re-binds rather than
    /// taking the extractor at construction (same shape as
    /// <see cref="CastingDirector.AttachLineExtractor"/>).
    /// </summary>
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

/// <summary>
/// One compiled cure-spell confirmation: the ailment it removes, the spell's
/// name (so the spell slot is confirmed, not just the target), and matchers
/// built from the spell's <see cref="MessageRecord.CasterMessage"/> (OUR cast)
/// and <see cref="MessageRecord.WitnessMessage"/> (a cast by another member we
/// see in the room — clears the chip for third-party observers). The witness
/// matcher is <c>null</c> when the record has no witness template. Provided by
/// <see cref="Services.AppServices"/> from the live Spells settings + spellbook
/// so re-configuring a cure spell takes effect without rebuilding the tracker.
/// </summary>
public readonly record struct CureCastMatcher(
    MessageFlags Ailment, string SpellName,
    CasterMessageMatcher Caster, CasterMessageMatcher? Witness = null);
