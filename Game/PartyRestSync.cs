using System.Text;
using MudPlay.Services;

namespace MudPlay.Game;

// Emit side of the @wait / @ok protocol. Engines call RequestWait / RequestOk
// when their own logic decides the party leader should pause / resume — e.g.
// the HealthManager auto-rest path, message-engine flag triggers (a paralyzed /
// held / confused ailment fires a "send @wait if following" message-flag
// handler), etc.
//
// Receive side lives in PartyEssentialHandlers (OnWait / OnOk handlers populate
// the WaitingMembers set the pause-gate consumer reads). The two halves talk
// past each other — leaders never emit (they have no one to wait for),
// followers never receive (only leaders care).
//
// We don't send @wait when solo or when we're the party leader. Leader has no
// one to ping; solo means there's no party context at all.
//
// We deliberately do NOT hook PlayerState.Position changes. A user typing a
// manual `rest` shouldn't trigger an automatic @wait — only engine decisions
// should. Engines call into this service when their own conditions fire.
public sealed class PartyRestSync : IDisposable
{
    private const string LogCategory = "Party";

    private readonly PartyState _party;
    private readonly HashSet<WaitReason> _waitReasons = new();
    private readonly LogService? _log;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public PartyRestSync(PartyState party, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(party);
        _party  = party;
        _log    = log;
        _party.PropertyChanged += OnPartyChanged;
        _namedLeader = InPartyBehindALeader() ? _party.LeaderName : null;
    }

    // The reasons holding the wait. An @ok goes out only when this empties, so one
    // listed here long after it should have cleared is what keeps a party stopped;
    // the bug report shows it.
    public IReadOnlyCollection<WaitReason> HeldReasons => _waitReasons;

    // Bind the wire-sender. Without it, RequestWait / RequestOk calls are silent
    // no-ops (no telepath). MainWindowViewModel supplies SendUserInput alongside
    // the other party hookups.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Every @wait names its reason after the token, so a leader sees why the party
    // stopped ("@wait (HP's too low)"): MegaMUD's wording where it has one, our own
    // where it doesn't (GAME_MECHANICS "`@wait` / `@ok` party pause"). A receiver
    // keys on the token alone (PartyEssentialHandlers.OnWait), so the note changes
    // nothing it does. ConditionNote is what MegaMUD sends for poison and disease.
    public const string HpNote        = "(HP's too low)";
    public const string ConditionNote = "(waiting on message condition)";
    public const string BlindNote     = "(blinded)";
    public const string ConfusedNote  = "(confused)";
    public const string HeldNote      = "(can't move)";
    public const string TooHeavyNote  = "(too heavy to move)";

    // Our own wording, in the same shape: MegaMUD sends nothing for a mana wait.
    // (TooHeavyNote is ours too.)
    public const string ManaNote      = "(mana's too low)";

    // The note a reason carries unless the caller passes one. Health has none here:
    // it covers both pools, and only HealthManager knows which one tripped.
    internal static string? DefaultNote(WaitReason reason) => reason switch
    {
        WaitReason.Poison    => ConditionNote,
        WaitReason.Disease   => ConditionNote,
        WaitReason.Blindness => BlindNote,
        WaitReason.Confusion => ConfusedNote,
        WaitReason.Held      => HeldNote,
        WaitReason.TooHeavy  => TooHeavyNote,
        _                    => null,
    };

    // Engine-callable entry point — register a wait reason and telepath @wait to
    // the party leader on the 0→non-empty transition. If another reason already
    // holds the wait, this only records the new reason and sends nothing (the
    // leader is already paused). No-ops on the wire when solo, when we're the
    // leader, or when no wire-sender is bound — but the reason is still tracked so
    // a later RequestOk balances. Following a leader the client can't name, the
    // @wait is kept until it can (OweWait). Idempotent at the protocol level — the
    // receiving leader's PartyEssentialHandlers.OnWait dedupes via a HashSet so
    // repeat sends don't double-count.
    //
    // resend: telepath @wait even though a reason already holds the wait. The leader
    // gives up on a wait after its "If leading, wait only" window, so a wait we still
    // count as held may no longer be holding it; an engine that knows it needs the
    // leader to stop NOW (a fresh drop below a rest floor, or being dragged along
    // while still recovering) re-asks. A duplicate @wait is harmless — the leader
    // dedupes waiting members.
    //
    // note: the reason shown after the token, overriding the reason's DefaultNote.
    public void RequestWait(WaitReason reason, bool resend = false, string? note = null)
    {
        bool wasEmpty = _waitReasons.Count == 0;
        bool added = _waitReasons.Add(reason);
        string? why = note ?? DefaultNote(reason);
        _notes[reason] = why;
        _okOwed = false;
        if (!resend && (!added || !wasEmpty)) return;
        if (FollowingAnUnknownLeader())
        {
            OweWait(reason, why);
            return;
        }
        if (!CanSignal()) return;
        SendWait(reason, why);
    }

    private void SendWait(WaitReason reason, string? why)
    {
        Telepath(_party.LeaderName!, why is null ? "@wait" : $"@wait {why}");
        _leaderHolds = true;
        _owedWait = null;
        _log?.Info(LogCategory, $"sent @wait for {reason}" + (why is null ? "" : $" {why}"));
    }

    // A @wait asked for while we follow a leader whose name the client has lost:
    // there is nobody to telepath it to, so it is kept and sent the moment the
    // leader is known again (PartyManager reads it back off `par`;
    // TellRestoredLeader). Warned once until it goes out or is dropped, not per
    // ask: the health engine re-asks on every room it is dragged through.
    private (WaitReason Reason, string? Note)? _owedWait;

    // The last reason cleared while the leader couldn't be named, and that leader
    // had been told @wait before the loss: the @ok goes out once they are named
    // again, so they don't wait out their whole window. Moot, and not kept, when
    // they were never told.
    private bool _okOwed;

    // Whom the last party change left us following by name.
    private string? _namedLeader;

    // The leader whose name the client lost while we went on following (the name
    // went and the party didn't), and whether they had been told @wait then. Put
    // back (PartyManager.RestoreLeaderFromPar, or the game's follow line), they
    // are told what is still held. Forgotten with the party.
    private string? _lostLeader;
    private bool _lostLeaderHeld;

    // The reason of the @wait that is waiting on a leader to send it to, for the
    // bug report.
    public WaitReason? WaitOwedForUnknownLeader => _owedWait?.Reason;

    // An @ok kept for the leader whose name was lost, for the bug report.
    public bool OkOwedForUnknownLeader => _okOwed;

    private bool FollowingAnUnknownLeader() =>
        _party.IsInParty && !_party.SelfIsLeader && string.IsNullOrEmpty(_party.LeaderName)
        && MasterSwitchOff?.Invoke() != true;

    private void OweWait(WaitReason reason, string? why)
    {
        bool first = _owedWait is null;
        _owedWait = (reason, why);
        if (first)
            _log?.Warn(LogCategory,
                $"@wait for {reason} not sent: following, but the client doesn't know who leads; it goes out once a `par` names the leader");
    }

    // The leader we lost is named again. A reason still held is told to them: one
    // kept while they couldn't be named, or one from before (a hold, a blinding,
    // a poison: those ask only once), which a leader who went on without a name
    // to answer may have given up on; without it a restored leader walked on.
    // A hold's note goes first, as AilmentSyncEngine sends it, since that is what
    // keeps us standing. With nothing held, a kept @ok goes out instead. The
    // master switch off, ResyncAfterMasterSwitch asks once it is back on.
    //
    // Only for the leader we lost, or with something kept for whoever is named: a
    // follow the game reports (PartyManager.FollowingLeader) has AilmentSyncEngine
    // tell the new leader its ailments, and a second @wait would only repeat it.
    private void TellRestoredLeader()
    {
        if (!CanSignal()) return;
        if (_waitReasons.Count > 0)
        {
            WaitReason reason = _waitReasons.Contains(WaitReason.Held) ? WaitReason.Held
                : _owedWait is { } owed && _waitReasons.Contains(owed.Reason) ? owed.Reason
                : _waitReasons.First();
            _log?.Info(LogCategory,
                $"{_party.LeaderName} is named again, with {string.Join(", ", _waitReasons)} still held — telling them");
            SendWait(reason, _notes.GetValueOrDefault(reason));
            return;
        }
        if (!_okOwed) return;
        _okOwed = false;
        Telepath(_party.LeaderName!, "@ok");
        _log?.Info(LogCategory, $"sent {_party.LeaderName} the @ok kept while who leads wasn't known");
    }

    // The master switch (true = off). Off, no hold is telepathed: no @wait and no
    // @heal (user, 2026-10-09: "no holds should be sent"). The reasons are still
    // tracked, so ResyncAfterMasterSwitch can ask again afterwards.
    public Func<bool>? MasterSwitchOff { get; set; }

    // The leader was last told @wait and has not been told @ok since. Forgotten
    // with the party or its leader: an @ok owed to one leader is not owed to the
    // next.
    private bool _leaderHolds;

    // The note each held reason was asked with, so a wait asked again later still
    // names its reason.
    private readonly Dictionary<WaitReason, string?> _notes = new();

    // The master switch is going off. A wait this client asked for is released
    // first: a release is not a hold, and a leader left waiting on a client that
    // has gone quiet would stand there for good when its own wait limit is 0. The
    // reasons stay tracked. After this nothing more is sent until the switch is
    // back on.
    public void ReleaseForMasterSwitch()
    {
        if (!_leaderHolds) return;
        _leaderHolds = false;
        if (!InPartyBehindALeader()) return;
        Telepath(_party.LeaderName!, "@ok");
        _log?.Info(LogCategory,
            $"master switch off — sent @ok to release our wait ({string.Join(", ", _waitReasons)}); nothing more is sent while it is off");
    }

    // The master switch came back on. A reason still held was either released as
    // the switch went off or arose while it was off and was never sent: ask once,
    // with that reason's own note. Nothing is sent when the leader has already
    // been asked since (the health engine re-asks for itself, with the pool's
    // note, as it is re-run), or when the only reason held has no note to give.
    public void ResyncAfterMasterSwitch()
    {
        if (_leaderHolds || _waitReasons.Count == 0 || !CanSignal()) return;
        foreach (WaitReason reason in _waitReasons)
        {
            if (!_notes.TryGetValue(reason, out string? why) || why is null) continue;
            Telepath(_party.LeaderName!, $"@wait {why}");
            _leaderHolds = true;
            _owedWait = null;
            _log?.Info(LogCategory, $"master switch back on — sent @wait {why}, still held by {string.Join(", ", _waitReasons)}");
            return;
        }
    }

    private void OnPartyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PartyState.IsInParty) or nameof(PartyState.LeaderName)
            or nameof(PartyState.SelfIsLeader)))
            return;
        bool toldWait = _leaderHolds;
        _leaderHolds = false;
        if (!_party.IsInParty || _party.SelfIsLeader)
        {
            _owedWait = null;
            _okOwed = false;
            _lostLeader = null;
            _lostLeaderHeld = false;
            _namedLeader = null;
            return;
        }
        if (string.IsNullOrEmpty(_party.LeaderName))
        {
            if (_namedLeader is { } lost)
            {
                _lostLeader = lost;
                _lostLeaderHeld = toldWait;
            }
            _namedLeader = null;
            return;
        }
        string named = _party.LeaderName;
        if (string.Equals(named, _namedLeader, StringComparison.OrdinalIgnoreCase)) return;
        _namedLeader = named;
        bool restored = string.Equals(named, _lostLeader, StringComparison.OrdinalIgnoreCase);
        _lostLeader = null;
        _lostLeaderHeld = false;
        // An @ok is owed only to the leader who was told @wait; a kept @wait goes
        // to whoever we follow now.
        if (!restored) _okOwed = false;
        if (restored || _owedWait is not null) TellRestoredLeader();
    }

    // A new character: nothing of the last one's is owed to anyone.
    public void ForgetLeaderHold()
    {
        _leaderHolds = false;
        _owedWait = null;
        _okOwed = false;
        _lostLeader = null;
    }

    // Engine-callable entry point — clear a wait reason and telepath @ok to the
    // party leader only on the non-empty→0 transition (the LAST reason
    // clearing). While other reasons still hold the wait, this records the
    // release and sends nothing. Same wire gates as RequestWait.
    //
    // A released reason that leaves others held is logged with what still holds: a
    // reason that never clears suppresses every later @ok, and without that line a
    // stopped party shows only an @wait and then nothing. Nothing auto-releases a
    // long-held reason — a timeout would send the leader on while a follower still
    // can't move. A release of a reason that wasn't held is routine (the ailment
    // engine releases unconditionally) and isn't logged.
    public void RequestOk(WaitReason reason)
    {
        if (!_waitReasons.Remove(reason)) return;
        _notes.Remove(reason);
        if (_waitReasons.Count > 0)
        {
            _log?.Info(LogCategory,
                $"{reason} released, no @ok sent — still held by {string.Join(", ", _waitReasons)}");
            return;
        }
        _owedWait = null;
        if (FollowingAnUnknownLeader())
        {
            _okOwed = _lostLeaderHeld;
            _log?.Info(LogCategory, _okOwed
                ? $"last wait reason ({reason}) cleared, but who leads isn't known: the @ok goes out once a `par` names the leader"
                : $"last wait reason ({reason}) cleared while who leads isn't known; the leader was never told @wait, so no @ok is owed");
            return;
        }
        if (!CanSignal()) return;
        Telepath(_party.LeaderName!, "@ok");
        _leaderHolds = false;
        _log?.Info(LogCategory, $"last wait reason ({reason}) cleared — sent @ok");
    }

    // True while any wait reason (a rest, a hold, a blinding…) still stands.
    public bool IsHoldingWait => _waitReasons.Count > 0;

    // The channel @heal goes out on (Settings → Party); Say until wired.
    public Func<Models.Profile.HealRequestChannel>? HealChannel { get; set; }

    // Ask the party for a heal with @heal — the flee-side signal a low-HP follower
    // emits instead of running. A follower that ran off alone would break party
    // formation and strand itself, so it asks the party healer(s) to top it up and
    // stays put; the leader owns the party's run decision. Said to the room rather
    // than telepathed to the leader (as RequestWait / RequestOk are) because the
    // healer may be any party member. It's a call for help now, so it goes out even
    // when it ends a sneak (SneakBreakingCommands' must-send relays). No-ops solo, as
    // leader, or without a wire-sender.
    public void RequestHeal()
    {
        if (!_party.IsInParty) return;
        if (_party.SelfIsLeader) return;
        if (_wireSender is null) return;
        if (MasterSwitchOff?.Invoke() == true) return;
        Models.Profile.HealRequestChannel channel = HealChannel?.Invoke() ?? Models.Profile.HealRequestChannel.Say;
        string wire = channel switch
        {
            Models.Profile.HealRequestChannel.Gangpath  => "bg @heal",   // gang speak verb is `bg`
            Models.Profile.HealRequestChannel.Broadcast => "br @heal",
            _                                           => ".@heal",
        };
        _wireSender(Encoding.Latin1.GetBytes(wire + "\r"));
        _log?.Info(LogCategory, $"asked the party for a heal on {channel}: {wire}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _party.PropertyChanged -= OnPartyChanged;
    }

    private bool CanSignal() => MasterSwitchOff?.Invoke() != true && InPartyBehindALeader();

    private bool InPartyBehindALeader() =>
        _party.IsInParty && !_party.SelfIsLeader && !string.IsNullOrEmpty(_party.LeaderName);

    private void Telepath(string recipient, string body)
    {
        if (_wireSender is null) return;
        // MajorMUD telepath syntax: `/<given> <body>` (slash + given name, no
        // space). `t` / `tel` / `tell` are all interpreted as `say`; full
        // "Given Family" recipients are rejected.
        string given = GivenName(recipient);
        byte[] bytes = Encoding.Latin1.GetBytes($"/{given} {body}\r");
        _wireSender(bytes);
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
