using MudPlay.Game.Remote;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Conditions;

// Inbound counterpart to AilmentSyncEngine. Mirrors a party member's
// curable-ailment state onto their PartyMember chip so the PartyWindow shows it
// and CastingDirector can party-cure them.
//
// Set — when a member running the same client catches a VERBOSE ailment (blind /
// confused / diseased / held), the outbound AilmentSyncEngine announces a BARE
// token on say — '.@blind', no 'on'/'off' suffix (MegaMUD parity). The leading
// period is the say-shortcut, so other clients observe the bare token (Forged says
// "@blind"). We match that on the ChatChannel.Local channel and set the speaker's
// chip via PartyManager.SetMemberAilment. The inbound parse still accepts an
// optional 'on'/'off' suffix for back-compat with an older client that paired the
// toggle, but the current client never emits 'off'. @held additionally pauses the
// leader through PartyEssentialHandlers.NotePause — a held member can't move, so
// the party waits for them. POISON is never announced on say — it's par-owned
// (PartyManager reads the par `P` flag). Our own announce echoes as You say
// "@blind" with a null speaker, so it's ignored here (our state is ConditionTracker's).
//
// Clear — MegaMUD sends no 'off', so a chip clears on whatever is observed first,
// none of which is an outbound say: (1) a witnessed cure landing on the member —
// the fastest path; (2) the spell-data duration timing out (SweepExpiredChips,
// armed from the apply-cast's cast level or a generous fallback cap); (3) the par
// `P` flag dropping (poison only, PartyManager); (4) a @status reconcile; (5) the
// member's @ok telepath — they hold @ok until their last non-ignored ailment
// clears, so it clears every VERBOSE chip (blind / confused / diseased / held) at
// once (ClearVerboseChipsOnOk). For the cure path, each configured cure spell's
// CasterMessage (OUR cast) and
// WitnessMessage (a cast by another member, seen in the room) templates are
// compiled to CasterMessageMatchers; a server line naming BOTH the cure spell AND
// the member (CasterMessageMatcher.ConfirmsSpellTarget) clears that member's chip —
// requiring the spell name too keeps an unrelated cast on the same member (a buff
// on a poisoned ally) from clearing the wrong chip. All clear paths are idempotent.
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

    // A generous cap applied when we set a chip we can't arm a real duration for
    // (a bare say announce with no witnessed cast, or a witnessed cast whose
    // duration couldn't be resolved). Not a duration claim — a backstop so a chip
    // can never stick forever when no cure / par / @status clear ever arrives.
    private const double FallbackDurationSeconds = 180;

    private readonly ChatRouter _chat;
    private readonly PartyManager _party;
    private readonly PartyEssentialHandlers _essentials;
    private readonly Func<IReadOnlyList<CureCastMatcher>> _readCureMatchers;
    // Apply-cast matchers (ailment-inflicting monster spells) + the spell-data
    // duration resolver. Null when apply-witnessing is disabled (only the cure/say
    // paths clear chips). See AppServices for the production bindings.
    private readonly Func<IReadOnlyList<ApplyCastMatcher>>? _readApplyMatchers;
    // (spell number, the caster the witness line named or null) → seconds.
    private readonly Func<int, string?, double?>? _resolveDurationSeconds;
    private readonly Func<long> _now;
    // Chips with an armed expiry, keyed by member given-name + flag → absolute
    // expiry (monotonic ms). The "duration timed out" clear: SweepExpiredChips
    // drops a chip whose window elapsed. Every non-timeout clear (cure / par /
    // @status / inbound off) removes the entry first, so the sweep only fires when
    // nothing else cleared. Poison is never armed here — it's par-owned.
    private readonly Dictionary<(string Given, MessageFlags Flag), long> _expiryAtMs = new();
    private readonly LogService? _log;
    private LineExtractor? _lines;
    private bool _disposed;

    public PartyAilmentTracker(
        ChatRouter chat,
        PartyManager party,
        PartyEssentialHandlers essentials,
        Func<IReadOnlyList<CureCastMatcher>> readCureMatchers,
        Func<IReadOnlyList<ApplyCastMatcher>>? readApplyMatchers = null,
        Func<int, string?, double?>? resolveDurationSeconds = null,
        Func<long>? nowMs = null,
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
        _readApplyMatchers = readApplyMatchers;
        _resolveDurationSeconds = resolveDurationSeconds;
        _now = nowMs ?? (static () => Environment.TickCount64);
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

        // Say-channel ailment announce: a bare '.@blind' / '.@confused' / '.@diseased' /
        // '.@held' (an older client's 'on' / 'off' suffix is still read).
        if (entry.Channel == ChatChannel.Local)
            HandleAilmentToggle(entry, speaker);

        // @ok (telepath → leader): the member's afflictions have all cleared. The
        // sender holds @ok until its LAST non-ignored ailment clears (PartyRestSync),
        // so an inbound @ok means every VERBOSE ailment they flagged is gone — drop
        // their say-driven chips. Poison is par-owned (its own P-flag drop clears it),
        // so it's left untouched here.
        if (entry.Channel == ChatChannel.TelepathIncoming
            && entry.Message.Trim().Equals("@ok", StringComparison.OrdinalIgnoreCase))
            ClearVerboseChipsOnOk(speaker);

        // @status reply reconcile — the reply rides whichever channel the @status
        // arrived on (telepath / gangpath / say), so watch all three.
        if (entry.Channel is ChatChannel.TelepathIncoming
                           or ChatChannel.Gangpath
                           or ChatChannel.Local)
            HandleStatusReply(entry, speaker);
    }

    // The verbose say-flagged ailments — the ones a member announces on say (so the
    // leader's chip for them is set by the member's broadcast, and an @ok means they
    // cleared). Poison is excluded: it's never said (par-owned).
    private static readonly MessageFlags[] VerboseAilments =
    {
        MessageFlags.Blinded, MessageFlags.Confused,
        MessageFlags.Diseased, MessageFlags.MovementPrevented,
    };

    // Clear a member's verbose ailment chips on their @ok (all non-ignored
    // afflictions cleared on their side). Drops any armed expiry too, so the sweep
    // can't re-fire on a stale window. Idempotent — SetMemberAilment no-ops a chip
    // that's already clear or a non-member speaker.
    private void ClearVerboseChipsOnOk(string speaker)
    {
        string given = GivenName(speaker);
        foreach (MessageFlags flag in VerboseAilments)
        {
            _party.SetMemberAilment(speaker, flag, false);
            _expiryAtMs.Remove((given, flag));
        }
        _log?.Info(LogCategory, $"@ok from {speaker} — cleared verbose ailment chips");
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
            if (off)
            {
                // An old client's explicit '.@X off' clears the chip — drop any
                // armed expiry so it doesn't re-fire on a stale window.
                _expiryAtMs.Remove((GivenName(speaker), flag));
            }
            else
            {
                // Held also pauses the leader on set: a held member can't move, so
                // the party must wait for them exactly as an explicit @wait would.
                // Its release rides the member's @ok via PartyEssentialHandlers.OnOk.
                if (flag == MessageFlags.MovementPrevented)
                    _essentials.NotePause(speaker);
                // A bare say announce carries no spell → no real duration. Arm the
                // fallback cap only when a (more precise) witnessed-apply expiry
                // isn't already set (TryAdd), so a say-only chip can't stick forever.
                // Poison is par-owned — never capped here.
                if (flag != MessageFlags.Poisoned)
                    _expiryAtMs.TryAdd((GivenName(speaker), flag),
                        _now() + (long)(FallbackDurationSeconds * 1000));
            }
            _log?.Info(LogCategory, $"inbound {token} {(off ? "off" : "on")} from {speaker}");
            return;
        }
    }

    // A follower our move left behind (they couldn't move with us) has rejoined.
    // Nothing announced a hold — they fell out of the party with it — so infer one:
    // light their Held chip and pause for their @ok exactly as an inbound @held
    // would, over a fresh wait window. ignoreOk: their last @ok came just before
    // they were left behind, so this wait runs its full window regardless.
    public void NoteInferredHold(string member, bool ignoreOk = false)
    {
        if (string.IsNullOrWhiteSpace(member)) return;
        _party.SetMemberAilment(member, MessageFlags.MovementPrevented, true);
        _essentials.NotePause(member, ignoreOk);
        _expiryAtMs.TryAdd((GivenName(member), MessageFlags.MovementPrevented),
            _now() + (long)(FallbackDurationSeconds * 1000));
        _log?.Info(LogCategory, ignoreOk
            ? $"{member} was left behind right after their @ok — holding the full wait window, ignoring @ok"
            : $"{member} was left behind by our move — treating them as held until their @ok");
    }

    // Reconcile a member's curable-ailment chips against a @status reply — the
    // pull-based counterpart to the push-based say announce. The reply body
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
        // A `@loop send` line names a loop and an @events one an event, and either
        // can be named "no ailments".
        if (UserNamedReply.IsLine(msg)) return;

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
            bool present = reported.Contains(token[1..]);
            _party.SetMemberAilment(speaker, flag, present);
            // Keep the armed-expiry bookkeeping consistent with the reconciled
            // truth: a present ailment gets the fallback cap (unless a precise
            // witnessed expiry is already armed), an absent one drops any entry.
            if (!present)
                _expiryAtMs.Remove((GivenName(speaker), flag));
            else if (flag != MessageFlags.Poisoned)
                _expiryAtMs.TryAdd((GivenName(speaker), flag),
                    _now() + (long)(FallbackDurationSeconds * 1000));
        }
        _log?.Info(LogCategory,
            $"@status reconcile from {speaker}: {(reported.Count == 0 ? "no ailments" : string.Join(",", reported))}");
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        // Drop any chip whose armed duration elapsed (the "duration timed out"
        // clear), then witness fresh applies (set + arm duration) and cures (clear
        // + drop the armed expiry). Sweeping on each line keeps this timer-free — a
        // quiet stream just clears on the next line, and the 5s par poll keeps lines
        // flowing — so a chip never outlives its duration by more than the gap to
        // the next line.
        SweepExpiredChips();
        WitnessApplies(line);
        WitnessCures(line);
    }

    // Witness a monster's ailment-apply line naming a party member → set that
    // member's chip and arm its spell-data duration (the authoritative clear for a
    // landed effect; a "resist" prints no apply line, so no chip). Poison is
    // excluded (par-owned); self is excluded (ConditionTracker owns it).
    // No-op when no apply-matchers are wired.
    private void WitnessApplies(LineExtractor.EmittedLine line)
    {
        if (_readApplyMatchers is null) return;
        IReadOnlyList<ApplyCastMatcher> matchers = _readApplyMatchers();
        if (matchers.Count == 0) return;

        foreach (PartyMember m in _party.State.Members)
        {
            if (m.IsSelf) continue;
            string given = GivenName(m.Name);
            // Cheap pre-filter: the matcher also pins the target, but skipping the
            // regex sweep when the member isn't even named keeps a busy combat
            // stream cheap against a large apply-matcher set.
            if (!line.Text.Contains(given, StringComparison.OrdinalIgnoreCase)
                && !line.Text.Contains(m.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            // Several records can share one witness line (two knockdown spells both
            // print "{target} is knocked flat!"), and only the one a monster in the
            // room carries resolves a duration. Keep the longest resolved per ailment
            // so an unresolved twin can't overwrite it with the fallback cap.
            Dictionary<MessageFlags, (double? Secs, string Spell)>? hits = null;
            foreach (ApplyCastMatcher am in matchers)
            {
                if (!NamesMember(am, line.Text, m.Name, given, out string? source)) continue;
                double? secs = _resolveDurationSeconds?.Invoke(am.SpellNumber, source);
                if (secs is <= 0) secs = null;
                hits ??= new();
                if (!hits.TryGetValue(am.Ailment, out (double? Secs, string Spell) best)
                    || (secs ?? 0) > (best.Secs ?? 0))
                    hits[am.Ailment] = (secs, am.SpellName);
            }
            if (hits is null) continue;
            foreach ((MessageFlags ailment, (double? resolved, string spell)) in hits)
            {
                _party.SetMemberAilment(m.Name, ailment, true);
                double secs = resolved ?? FallbackDurationSeconds;
                _expiryAtMs[(given, ailment)] = _now() + (long)(secs * 1000);
                _log?.Info(LogCategory,
                    $"witnessed {ailment} on {m.Name} (spell '{spell}') — chip set, clears in ~{secs:0}s"
                    + (resolved is null ? " (no caster in the room resolved a duration, fallback cap)" : string.Empty));
            }
        }
    }

    // Whether the witness line is this apply landing on the member, and who cast it
    // when the template names a caster. A template that pins {target} is matched on
    // its own slots: the spell name is checked only when the template carries one,
    // because an on-hit effect's line names nobody but the victim ("{target} is
    // knocked flat!"). Legacy {s}-only templates still need spell + target captures.
    private static bool NamesMember(
        ApplyCastMatcher am, string text, string name, string given, out string? source)
    {
        source = null;
        if (!am.Witness.PinsTarget)
            return am.Witness.ConfirmsSpellTarget(text, am.SpellName, name)
                || am.Witness.ConfirmsSpellTarget(text, am.SpellName, given);

        if (!am.Witness.TryMatchCaptures(text, out MessageCaptures caps)) return false;
        if (!Same(caps.Target, name) && !Same(caps.Target, given)) return false;
        if (caps.Spell is not null && !Same(caps.Spell, am.SpellName)) return false;
        source = caps.Source;
        return true;
    }

    private static bool Same(string? a, string b) =>
        string.Equals(a?.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // Clear a member's chip when we witness a cure land on them — the fastest clear
    // path, before any par/@status reconcile. Requires BOTH the cure spell's name
    // and the member's name so a different spell on the same member can't clear the
    // wrong chip. Matches OUR cast or a cure another member casts that we see in the
    // room; the caster of a witnessed cure doesn't matter, only spell + target.
    private void WitnessCures(LineExtractor.EmittedLine line)
    {
        IReadOnlyList<CureCastMatcher> matchers = _readCureMatchers();
        if (matchers.Count == 0) return;

        foreach (PartyMember m in _party.State.Members)
        {
            if (m.IsSelf) continue;
            string given = GivenName(m.Name);
            foreach (CureCastMatcher cm in matchers)
            {
                bool hit =
                    cm.Caster.ConfirmsSpellTarget(line.Text, cm.SpellName, m.Name)
                 || cm.Caster.ConfirmsSpellTarget(line.Text, cm.SpellName, given)
                 || (cm.Witness is { } w
                     && (w.ConfirmsSpellTarget(line.Text, cm.SpellName, m.Name)
                      || w.ConfirmsSpellTarget(line.Text, cm.SpellName, given)));
                if (!hit) continue;
                _party.SetMemberAilment(m.Name, cm.Ailment, false);
                _expiryAtMs.Remove((given, cm.Ailment));
                _log?.Info(LogCategory, $"cure confirmed ailment={cm.Ailment} target={m.Name}");
            }
        }
    }

    // Clear any chip whose armed duration has elapsed — the spell-data "duration
    // timed out" clear. Every non-timeout clear (cure / par / @status / inbound
    // off) removes the entry first, so this only fires when nothing else cleared.
    internal void SweepExpiredChips()
    {
        if (_expiryAtMs.Count == 0) return;
        long now = _now();
        List<(string Given, MessageFlags Flag)>? expired = null;
        foreach (KeyValuePair<(string Given, MessageFlags Flag), long> kv in _expiryAtMs)
            if (kv.Value <= now) (expired ??= new()).Add(kv.Key);
        if (expired is null) return;
        foreach ((string given, MessageFlags flag) in expired)
        {
            _expiryAtMs.Remove((given, flag));
            _party.SetMemberAilment(given, flag, false);
            _log?.Info(LogCategory, $"ailment {flag} on {given} timed out — chip cleared");
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
        _expiryAtMs.Clear();
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

// One compiled ailment-APPLY confirmation: the ailment it inflicts, the spell's
// name + number (number → spell-data duration at the in-room caster's cast level),
// and the WitnessMessage matcher (a monster casting it on a member, seen in the
// room — "The kobold shaman blinds Forged!"). Built by AppServices from every
// Messages record carrying an ailment Flags bit, a Spells link, and a witness
// template; re-read live so game-data edits take effect. Poison is excluded (par-
// owned). A "resist" prints no apply line, so a resisted cast sets no chip.
public readonly record struct ApplyCastMatcher(
    MessageFlags Ailment, string SpellName, int SpellNumber,
    CasterMessageMatcher Witness);
