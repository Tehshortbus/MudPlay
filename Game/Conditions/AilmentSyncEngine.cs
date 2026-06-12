using System.ComponentModel;
using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Conditions;

/// <summary>
/// Outbound ailment-sync: when the local character catches a curable
/// ailment (poison / blindness / confusion / disease) or is held
/// (movement-prevented), this engine (1) announces it on the say channel —
/// <c>.@poisoned</c> / <c>.@blind</c> / <c>.@confused</c> /
/// <c>.@diseased</c> / <c>.@held</c> — so other FujinTerm clients in the
/// room can mirror our state on their party window (and a member with a
/// cure-holds spell can free us), and (2) for the four curable ailments
/// telepaths an <c>@wait</c> to the party leader so the party pauses while
/// we're afflicted. On clear it telepaths the matching <c>@ok</c> (only
/// when the last wait reason releases — see <see cref="PartyRestSync"/>).
/// No clear-side say announce.
/// </summary>
/// <remarks>
/// <para>
/// Transitions are read off <see cref="ConditionTracker.ActiveFlags"/>
/// directly — we diff the added / removed bits per change rather than
/// subscribing to <see cref="ConditionTracker.ConditionApplied"/> /
/// <c>ConditionEnded</c>. A single inbound line that toggles two
/// ailments at once still produces one decision per flag, and the engine
/// stays decoupled from individual <see cref="MessageRecord"/>s.
/// </para>
/// <para>
/// The say-announce only fires when we're <b>in a party</b> AND we have
/// <b>no cure spell configured</b> for that ailment — if we can self-cure
/// we just clear our own condition silently, and out of a party there's no
/// one to tell. On top of that the per-ailment <c>DoNotAnnounce&lt;X&gt;</c>
/// gate (<see cref="OtherSettings"/>, Char tier) suppresses the curable
/// four; the <c>Ignore&lt;X&gt;</c> gate independently suppresses their
/// <c>@wait</c>. Held has no settings gate — only the in-party / no-cure
/// rule applies.
/// </para>
/// <para>
/// Held is special: it never telepaths an <c>@wait</c>. The leader is
/// paused by the inbound <c>.@held</c> say (which doubles as a "cure my
/// hold" identifier), so the affliction registers a silent
/// <see cref="WaitReason.Held"/> purely so the balanced <c>@ok</c> on
/// last-clear releases the leader once every reason clears.
/// </para>
/// <para>
/// The say wire format prefixes the token with a period — MajorMUD's
/// say-channel prefix — so <c>.@poisoned</c> is what lands on the wire.
/// </para>
/// </remarks>
public sealed class AilmentSyncEngine : IDisposable
{
    /// <summary>LogService category — appears as <c>[Ailment]</c> rows.</summary>
    public const string LogCategory = "Ailment";

    /// <summary>
    /// The ailments we sync, with their say token, the
    /// <see cref="WaitReason"/> they hold on the leader, and whether they
    /// telepath an <c>@wait</c> on top of the say-announce. Confusion is
    /// included even though no realm cure exists for it (stock / paramud) —
    /// the announce still lets the party react. Held (<c>TelepathWait</c>
    /// false) never sends <c>@wait</c>: its leader-pause rides the
    /// <c>.@held</c> say.
    /// </summary>
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
    private readonly Func<OtherSettings> _readOther;
    private readonly Func<bool> _isInParty;
    private readonly Func<MessageFlags, bool> _hasCureConfigured;
    private readonly LogService? _log;

    private MessageFlags _lastFlags;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public AilmentSyncEngine(
        ConditionTracker conditions,
        PartyRestSync restSync,
        Func<OtherSettings> readOther,
        Func<bool> isInParty,
        Func<MessageFlags, bool> hasCureConfigured,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(restSync);
        ArgumentNullException.ThrowIfNull(readOther);
        ArgumentNullException.ThrowIfNull(isInParty);
        ArgumentNullException.ThrowIfNull(hasCureConfigured);
        _conditions = conditions;
        _restSync = restSync;
        _readOther = readOther;
        _isInParty = isInParty;
        _hasCureConfigured = hasCureConfigured;
        _log = log;

        _lastFlags = conditions.ActiveFlags;
        _conditions.PropertyChanged += OnConditionsChanged;
    }

    /// <summary>
    /// Bind the say wire-sender. Without it the say-announce is a silent
    /// no-op (the @wait still routes through <see cref="PartyRestSync"/>'s
    /// own sender). MainWindowViewModel supplies the wrapped engine
    /// sender alongside the other Phase 6 hookups.
    /// </summary>
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

        OtherSettings other = _readOther();
        bool inParty = _isInParty();

        foreach ((MessageFlags flag, string token, WaitReason reason, bool telepathWait) in Ailments)
        {
            if (added.HasFlag(flag))
            {
                bool announced = ShouldAnnounce(flag, other, inParty);
                if (announced)
                    Say(token);

                if (telepathWait)
                {
                    if (!IsWaitSuppressed(flag, other))
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
                // Balance any wait we placed for this ailment. RequestOk
                // is a no-op when no matching reason is held, so calling
                // it unconditionally (even when the wait was suppressed)
                // is safe.
                _restSync.RequestOk(reason);
            }
        }
    }

    /// <summary>
    /// Whether to say-announce <paramref name="flag"/>. Two cross-cutting
    /// gates apply to every ailment: we must be <b>in a party</b> (no one to
    /// tell otherwise) and have <b>no cure spell configured</b> for it (if we
    /// can self-cure, we clear it silently). The per-ailment
    /// <c>DoNotAnnounce&lt;X&gt;</c> setting suppresses the curable four on top
    /// of that; held has no such setting.
    /// </summary>
    private bool ShouldAnnounce(MessageFlags flag, OtherSettings o, bool inParty)
    {
        if (!inParty) return false;
        if (_hasCureConfigured(flag)) return false;
        return !IsAnnounceSuppressed(flag, o);
    }

    private static bool IsAnnounceSuppressed(MessageFlags flag, OtherSettings o) => flag switch
    {
        MessageFlags.Poisoned => o.DoNotAnnouncePoison,
        MessageFlags.Blinded  => o.DoNotAnnounceBlindness,
        MessageFlags.Confused => o.DoNotAnnounceConfusion,
        MessageFlags.Diseased => o.DoNotAnnounceDiseased,
        _ => false,
    };

    private static bool IsWaitSuppressed(MessageFlags flag, OtherSettings o) => flag switch
    {
        MessageFlags.Poisoned => o.IgnorePoison,
        MessageFlags.Blinded  => o.IgnoreBlindness,
        MessageFlags.Confused => o.IgnoreConfusion,
        MessageFlags.Diseased => o.IgnoreDiseased,
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
