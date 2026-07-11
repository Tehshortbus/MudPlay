using System.ComponentModel;
using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Conditions;

// Outbound ailment-sync: when the local character catches a curable ailment
// (poison / blindness / confusion / disease) or is held (movement-prevented),
// this engine (1) announces it on the say channel — the curable four as a paired
// toggle '.@poisoned on' … '.@poisoned off', held as a bare '.@held' — so other
// clients in the room can mirror our state on their party window (and a member
// with a cure-holds spell can free us), and (2) for the four curable ailments
// telepaths an @wait to the party leader so the party pauses while we're
// afflicted. On clear it (a) says the matching '.@X off' for the curable four —
// the authoritative chip-clear the receiver keys on, so a natural wear-off with
// no cure line still clears — and (b) telepaths @ok (only when the last wait
// reason releases — see PartyRestSync), which releases held's say-driven pause.
// Held has no off-signal; its release rides @ok alone.
//
// Transitions are read off ConditionTracker.ActiveFlags directly — we diff the
// added / removed bits per change rather than subscribing to
// ConditionTracker.ConditionApplied / ConditionEnded. A single inbound line that
// toggles two ailments at once still produces one decision per flag, and the
// engine stays decoupled from individual MessageRecords.
//
// The say-announce only fires when we're in a party AND we have no cure spell
// configured for that ailment — if we can self-cure we just clear our own
// condition silently, and out of a party there's no one to tell. On top of that
// the per-ailment DoNotAnnounce<X> gate (SpellsSettings, Char tier) suppresses
// the curable four; the Ignore<X> gate independently suppresses their @wait.
// Held has no settings gate — only the in-party / no-cure rule applies.
//
// Held is special: it never telepaths an @wait. The leader is paused by the
// inbound .@held say (which doubles as a "cure my hold" identifier), so the
// affliction registers a silent WaitReason.Held purely so the balanced @ok on
// last-clear releases the leader once every reason clears.
//
// The say wire format prefixes the token with a period — MajorMUD's say-channel
// prefix — so .@poisoned is what lands on the wire.
public sealed class AilmentSyncEngine : IDisposable
{
    // LogService category — appears as [Ailment] rows.
    public const string LogCategory = "Ailment";

    // The ailments we sync, with their say token, the WaitReason they hold on the
    // leader, and whether they telepath an @wait on top of the say-announce.
    // Confusion is included even though no realm cure exists for it (stock /
    // paramud) — the announce still lets the party react. Held (TelepathWait
    // false) never sends @wait: its leader-pause rides the .@held say.
    private static readonly (MessageFlags Flag, string SayToken, WaitReason Reason, bool TelepathWait)[] Ailments =
    {
        (MessageFlags.Poisoned, "@poisoned", WaitReason.Poison,    true),
        (MessageFlags.Blinded,  "@blind",    WaitReason.Blindness, true),
        (MessageFlags.Confused, "@confused", WaitReason.Confusion, true),
        (MessageFlags.Diseased, "@diseased", WaitReason.Disease,   true),
        (MessageFlags.MovementPrevented, "@held", WaitReason.Held, false),
    };

    private readonly ConditionTracker _conditions;
    private readonly PartyRestSync _restSync;
    private readonly Func<SpellsSettings> _readSpells;
    private readonly Func<bool> _isInParty;
    private readonly Func<MessageFlags, bool> _hasCureConfigured;
    private readonly LogService? _log;

    private MessageFlags _lastFlags;
    // Flags we've actually announced an ON for (say-gated by ShouldAnnounce). Used
    // to emit a BALANCED '.@X off' on clear only when the room heard the '.@X on' —
    // a self-cured / DoNotAnnounce'd ailment set no chip, so it needs no off.
    private MessageFlags _announcedFlags;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public AilmentSyncEngine(
        ConditionTracker conditions,
        PartyRestSync restSync,
        Func<SpellsSettings> readSpells,
        Func<bool> isInParty,
        Func<MessageFlags, bool> hasCureConfigured,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(restSync);
        ArgumentNullException.ThrowIfNull(readSpells);
        ArgumentNullException.ThrowIfNull(isInParty);
        ArgumentNullException.ThrowIfNull(hasCureConfigured);
        _conditions = conditions;
        _restSync = restSync;
        _readSpells = readSpells;
        _isInParty = isInParty;
        _hasCureConfigured = hasCureConfigured;
        _log = log;

        _lastFlags = conditions.ActiveFlags;
        _conditions.PropertyChanged += OnConditionsChanged;
    }

    // Bind the say wire-sender. Without it the say-announce is a silent no-op (the
    // @wait still routes through PartyRestSync's own sender). MainWindowViewModel
    // supplies the wrapped engine sender alongside the other engine hookups.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    private void OnConditionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConditionTracker.ActiveFlags)) return;

        MessageFlags now = _conditions.ActiveFlags;
        MessageFlags added   = now & ~_lastFlags;
        MessageFlags removed = _lastFlags & ~now;
        _lastFlags = now;
        if (added == MessageFlags.None && removed == MessageFlags.None) return;

        SpellsSettings spells = _readSpells();
        bool inParty = _isInParty();

        foreach ((MessageFlags flag, string token, WaitReason reason, bool telepathWait) in Ailments)
        {
            // Held is the lone non-toggle: it announces bare '.@held' (on only)
            // and its leader-pause release rides @ok. The curable four ride a
            // paired '.@X on' / '.@X off' toggle so the receiver can clear the
            // chip on the matching 'off' without witnessing a cure — the case
            // that broke the reported stuck-blind chip (blindness wore off with
            // no cure line, and no @ok because its @wait was ignored).
            bool paired = flag != MessageFlags.MovementPrevented;
            if (added.HasFlag(flag))
            {
                bool announced = ShouldAnnounce(flag, spells, inParty);
                if (announced)
                {
                    Say(paired ? token + " on" : token);
                    _announcedFlags |= flag;
                }

                if (telepathWait)
                {
                    if (!IsWaitSuppressed(flag, spells))
                        _restSync.RequestWait(reason);
                }
                else if (announced)
                {
                    // Held: no @wait telepath — the leader is paused by the
                    // inbound .@held say. Register the reason silently (only
                    // when we actually announced) so the balanced @ok on
                    // last-clear releases that say-driven pause.
                    _restSync.RequestWait(reason, announce: false);
                }
            }
            else if (removed.HasFlag(flag))
            {
                // Paired off-signal — only when we announced the on (so the
                // room actually set a chip to clear), and never for held.
                if (paired && _announcedFlags.HasFlag(flag))
                    Say(token + " off");
                _announcedFlags &= ~flag;

                // Balance any wait we placed for this ailment. RequestOk
                // is a no-op when no matching reason is held, so calling
                // it unconditionally (even when the wait was suppressed)
                // is safe.
                _restSync.RequestOk(reason);
            }
        }
    }

    // Reconcile the @wait state of the curable ailments against the CURRENT
    // settings. Called when the user toggles an Ignore<X> gate mid-affliction:
    // the onset-time decision is latched (OnConditionsChanged only fires on a
    // flag transition), so without this a "turn IgnorePoison on while poisoned"
    // leaves the @wait we already telepathed standing and the party never
    // resumes. Flipping the gate ON releases the wait (@ok); flipping it OFF
    // while still afflicted (re)places it (@wait). Idempotent — PartyRestSync
    // dedupes reasons, so an unchanged reason is a no-op on the wire. Held is
    // excluded (no Ignore setting; its wait rides the .@held say lifecycle).
    public void ReevaluateWaits()
    {
        MessageFlags active = _conditions.ActiveFlags;
        SpellsSettings spells = _readSpells();
        foreach ((MessageFlags flag, _, WaitReason reason, bool telepathWait) in Ailments)
        {
            if (!telepathWait) continue;
            if (active.HasFlag(flag) && !IsWaitSuppressed(flag, spells))
                _restSync.RequestWait(reason);
            else
                _restSync.RequestOk(reason);
        }
    }

    // Whether to say-announce flag. Two cross-cutting gates apply to every
    // ailment: we must be in a party (no one to tell otherwise) and have no cure
    // spell configured for it (if we can self-cure, we clear it silently). The
    // per-ailment DoNotAnnounce<X> setting suppresses the curable four on top of
    // that; held has no such setting.
    private bool ShouldAnnounce(MessageFlags flag, SpellsSettings s, bool inParty)
    {
        if (!inParty) return false;
        if (_hasCureConfigured(flag)) return false;
        return !IsAnnounceSuppressed(flag, s);
    }

    private static bool IsAnnounceSuppressed(MessageFlags flag, SpellsSettings s) => flag switch
    {
        MessageFlags.Poisoned => s.DoNotAnnouncePoison,
        MessageFlags.Blinded  => s.DoNotAnnounceBlindness,
        MessageFlags.Confused => s.DoNotAnnounceConfusion,
        MessageFlags.Diseased => s.DoNotAnnounceDiseased,
        _ => false,
    };

    private static bool IsWaitSuppressed(MessageFlags flag, SpellsSettings s) => flag switch
    {
        MessageFlags.Poisoned => s.IgnorePoison,
        MessageFlags.Blinded  => s.IgnoreBlindness,
        MessageFlags.Confused => s.IgnoreConfusion,
        MessageFlags.Diseased => s.IgnoreDiseased,
        _ => false,
    };

    private void Say(string token)
    {
        if (_wireSender is null) return;
        // MajorMUD say channel — a line prefixed with '.' is spoken to
        // the room. ".@poisoned" lets other FujinTerm clients mirror us.
        byte[] bytes = Encoding.Latin1.GetBytes("." + token + "\r");
        _wireSender(bytes);
        _log?.Info(LogCategory, $"announced '{token}' on say");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conditions.PropertyChanged -= OnConditionsChanged;
    }
}
