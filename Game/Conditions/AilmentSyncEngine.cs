using System.ComponentModel;
using System.Text;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Conditions;

// Outbound ailment-sync: when the local character catches a curable ailment
// (poison / blindness / confusion / disease) or is held (movement-prevented),
// this engine (1) announces the VERBOSE ones on the say channel as a BARE token —
// '.@blind' / '.@confused' / '.@diseased' / '.@held', no 'on'/'off' suffix
// (MegaMUD parity) — so other clients in the room mirror our state on their party
// window, and (2) telepaths an @wait to the party leader so the party pauses
// while we're afflicted. POISON is NOT announced (Verbose:false) — an observer
// reads it from the par `P` flag (PartyManager) — but it still telepaths its
// @wait. HELD is the same as the others here: it announces '.@held' on say AND
// telepaths @wait (MegaMUD parity — an observed hold both lights the member's
// chip and pauses the leader). On CLEAR the engine sends NOTHING on say (MegaMUD
// sends no 'off'); the receiver clears the chip via a witnessed cure, the
// spell-data duration timing out, the par `P` drop, or a @status reconcile
// (PartyAilmentTracker). It only telepaths @ok when the last wait reason releases
// (see PartyRestSync).
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
// the per-ailment Ignore<X> gate (SpellsSettings, Char tier) is the single "I
// don't care about this ailment" toggle: it suppresses BOTH the say announce AND
// the @wait telepath for the curable four. Held has no settings gate — only the
// in-party / no-cure rule applies to its say, and its @wait is never suppressible.
//
// The say wire format prefixes the token with a period — MajorMUD's say-channel
// prefix — so .@blind is what lands on the wire.
//
// Following a leader while still afflicted says the tokens again and re-asks the
// @wait (NoteFollowingLeader). A held follower drops out of the party when the
// leader moves, and the leader's party loses our chip with us; once the leader
// comes back and we follow again, nobody in the party knows we are held unless we
// say so, and they sit out a hold a cure would end (report
// paradigm-20261014-214037).
//
// The '.@held' say waits HoldSettle before it goes out, and goes out only if the
// hold is still on. Some holds end in the round they land: a yeti's roar is
// cancelled by any yeti's death spell, which prints nothing the rest of the party
// can see, and with no 'off' and our @ok held back by another wait reason their
// chips stayed lit and they spent cures on a hold that was already over (report
// paradigm-20261103-092609). The @wait is not delayed: a held follower can't move,
// so the leader must stop at once, and an @ok balances it the moment the hold ends.
public sealed class AilmentSyncEngine : IDisposable
{
    // LogService category — appears as [Ailment] rows.
    public const string LogCategory = "Ailment";

    // The ailments we sync, with their say token, the WaitReason they hold on the
    // leader, and whether they announce on say (Verbose). Every ailment telepaths
    // @wait (gated per-ailment by Ignore<X>, except held which has no gate). Poison
    // is NOT verbose — an observer reads it from the par `P` flag (PartyManager),
    // the cross-client source; it still telepaths @wait. Confusion IS verbose even
    // though no realm cure exists for it (stock / paradigm) — the announce still
    // lets the party react. Held announces '.@held' AND telepaths @wait, like the
    // curable four (MegaMUD parity).
    private static readonly (MessageFlags Flag, string SayToken, WaitReason Reason, bool Verbose)[] Ailments =
    {
        (MessageFlags.Poisoned, "@poisoned", WaitReason.Poison,    false),
        (MessageFlags.Blinded,  "@blind",    WaitReason.Blindness, true),
        (MessageFlags.Confused, "@confused", WaitReason.Confusion, true),
        (MessageFlags.Diseased, "@diseased", WaitReason.Disease,   true),
        (MessageFlags.MovementPrevented, "@held", WaitReason.Held, true),
    };

    // Long enough for the rest of the round a hold landed in to arrive, the death
    // that cancels it among them; short against the five-second round a party
    // member needs before they could cast a cure anyway.
    public static readonly TimeSpan HoldSettle = TimeSpan.FromSeconds(1);

    private readonly ConditionTracker _conditions;
    private readonly PartyRestSync _restSync;
    private readonly Func<SpellsSettings> _readSpells;
    private readonly Func<bool> _isInParty;
    private readonly Func<MessageFlags, bool> _hasCureConfigured;
    private readonly LogService? _log;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    private MessageFlags _lastFlags;
    private IDisposable? _heldSay;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public AilmentSyncEngine(
        ConditionTracker conditions,
        PartyRestSync restSync,
        Func<SpellsSettings> readSpells,
        Func<bool> isInParty,
        Func<MessageFlags, bool> hasCureConfigured,
        LogService? log = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
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
        _schedule = schedule ?? DispatcherTicks.Once;

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

        foreach ((MessageFlags flag, string token, WaitReason reason, bool verbose) in Ailments)
        {
            if (added.HasFlag(flag))
            {
                // Bare announce (MegaMUD parity): '.@blind', no 'on'/'off'
                // suffix. The receiver clears the chip via a witnessed cure, the
                // spell-data duration timing out, the par `P` flag (poison), or a
                // @status reconcile (PartyAilmentTracker) — never an 'off' say.
                // Non-verbose ailments (poison) never say anything — par carries them.
                if (verbose && ShouldAnnounce(flag, spells, inParty))
                {
                    if (flag == MessageFlags.MovementPrevented) SayHeldOnceSettled();
                    else Say(token);
                }

                // Telepath @wait unless this ailment's Ignore<X> gate suppresses it.
                // Held has no gate (never suppressible) but still @waits like the
                // others — its '.@held' say drives the chip / cure on the receiver,
                // the @wait drives the leader-pause, and the balanced @ok releases it.
                if (!IsWaitSuppressed(flag, spells))
                    _restSync.RequestWait(reason);
            }
            else if (removed.HasFlag(flag))
            {
                // No say on clear — MegaMUD sends no 'off'. Only the @ok telepath goes
                // out, releasing the leader's wait. RequestOk is a no-op when no
                // matching reason is held, so calling it unconditionally is safe.
                if (flag == MessageFlags.MovementPrevented && _heldSay is not null)
                {
                    CancelHeldSay();
                    _log?.Info(LogCategory, "hold ended before the party was told — '@held' not said");
                }
                _restSync.RequestOk(reason);
            }
        }
    }

    // We have just started following leader (PartyManager.FollowingLeader). With an
    // ailment still on us the say goes out under the apply-time rules
    // (ShouldAnnounce), so the leader lights our chip again (another member does
    // once its roster has us back), and one @wait goes out, a hold's note first
    // since that's what keeps us standing: PartyRestSync telepaths
    // only on a fresh hold, and this leader may never have been told. With the
    // master switch off nothing is sent; PartyRestSync asks the @wait again when it
    // comes back on.
    public void NoteFollowingLeader(string leader)
    {
        MessageFlags active = _conditions.ActiveFlags;
        if (active == MessageFlags.None || MasterSwitchOff?.Invoke() == true) return;
        SpellsSettings spells = _readSpells();
        bool inParty = _isInParty();
        List<string>? said = null;
        WaitReason? wait = null;
        foreach ((MessageFlags flag, string token, WaitReason reason, bool verbose) in Ailments)
        {
            if (!active.HasFlag(flag) || IsWaitSuppressed(flag, spells)) continue;
            if (flag == MessageFlags.MovementPrevented) wait = reason;
            else wait ??= reason;
            if (verbose && ShouldAnnounce(flag, spells, inParty))
            {
                if (flag == MessageFlags.MovementPrevented) CancelHeldSay();
                Say(token);
                (said ??= new()).Add(token);
            }
        }
        if (wait is not { } first) return;
        _restSync.RequestWait(first, resend: true);
        string what = (said is null ? "nothing said" : "said " + string.Join(" ", said))
            + $", @wait for {first}";
        LastFollowAnnounce = $"{DateTime.Now:HH:mm:ss} on following {leader}: {what}";
        _log?.Info(LogCategory, $"following {leader} while still afflicted — {what}");
    }

    // Whether a '.@held' is waiting out HoldSettle, for the bug report.
    public bool HeldSayPending => _heldSay is not null;

    // The last follow that told the leader of an ailment again, for the bug report.
    public string? LastFollowAnnounce { get; private set; }

    // Reconcile the @wait state of every ailment against the CURRENT settings.
    // Called when the user toggles an Ignore<X> gate mid-affliction: the onset-time
    // decision is latched (OnConditionsChanged only fires on a flag transition), so
    // without this a "turn IgnorePoison on while poisoned" leaves the @wait we
    // already telepathed standing and the party never resumes. Flipping the gate ON
    // releases the wait (@ok); flipping it OFF while still afflicted (re)places it
    // (@wait). Idempotent — PartyRestSync dedupes reasons, so an unchanged reason is
    // a no-op on the wire. Held carries no Ignore gate (IsWaitSuppressed is always
    // false for it), so it's simply kept asserted while the hold is active.
    public void ReevaluateWaits()
    {
        MessageFlags active = _conditions.ActiveFlags;
        SpellsSettings spells = _readSpells();
        foreach ((MessageFlags flag, _, WaitReason reason, _) in Ailments)
        {
            if (active.HasFlag(flag) && !IsWaitSuppressed(flag, spells))
                _restSync.RequestWait(reason);
            else
                _restSync.RequestOk(reason);
        }
    }

    // The master switch (true = off): off, no ailment is announced on say. One
    // skipped is said later only if we follow a leader again while it lasts.
    public Func<bool>? MasterSwitchOff { get; set; }

    // Whether to say-announce flag. Three gates apply to every ailment: we must be
    // in a party (no one to tell otherwise), have no cure spell configured for it
    // (if we can self-cure, we clear it silently), and the per-ailment Ignore<X>
    // gate must be off. Ignore<X> is the single "I don't care about this ailment"
    // toggle — it suppresses BOTH the say announce and the @wait telepath. Held has
    // no Ignore gate, so it always announces (in-party / no-cure permitting).
    private bool ShouldAnnounce(MessageFlags flag, SpellsSettings s, bool inParty)
    {
        if (!inParty) return false;
        if (MasterSwitchOff?.Invoke() == true) return false;
        if (_hasCureConfigured(flag)) return false;
        return !IsWaitSuppressed(flag, s);
    }

    private static bool IsWaitSuppressed(MessageFlags flag, SpellsSettings s) => flag switch
    {
        MessageFlags.Poisoned => s.IgnorePoison,
        MessageFlags.Blinded  => s.IgnoreBlindness,
        MessageFlags.Confused => s.IgnoreConfusion,
        MessageFlags.Diseased => s.IgnoreDiseased,
        _ => false,
    };

    private void SayHeldOnceSettled()
    {
        if (_heldSay is not null) return;
        _heldSay = _schedule(HoldSettle, () =>
        {
            _heldSay = null;
            // The hold ending cancels this; the say's gates are asked again, as we
            // may have left the party within the second.
            if (_disposed || !ShouldAnnounce(MessageFlags.MovementPrevented, _readSpells(), _isInParty())) return;
            Say("@held");
        });
    }

    private void CancelHeldSay()
    {
        _heldSay?.Dispose();
        _heldSay = null;
    }

    private void Say(string token)
    {
        if (_wireSender is null) return;
        // MajorMUD say channel — a line prefixed with '.' is spoken to
        // the room. ".@blind" lets other MudPlay clients mirror us.
        byte[] bytes = Encoding.Latin1.GetBytes("." + token + "\r");
        _wireSender(bytes);
        _log?.Info(LogCategory, $"announced '{token}' on say");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelHeldSay();
        _conditions.PropertyChanged -= OnConditionsChanged;
    }
}
