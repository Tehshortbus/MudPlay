using System.ComponentModel;
using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Conditions;

/// <summary>
/// Outbound ailment-sync: when the local character catches a curable
/// ailment (poison / blindness / confusion / disease), this engine
/// (1) announces it on the say channel — <c>.@poisoned</c> /
/// <c>.@blind</c> / <c>.@confused</c> / <c>.@diseased</c> — so other
/// FujinTerm clients in the room can mirror our state on their party
/// window, and (2) telepaths an <c>@wait</c> to the party leader so the
/// party pauses while we're afflicted. On clear it telepaths the
/// matching <c>@ok</c> (only when the last wait reason releases — see
/// <see cref="PartyRestSync"/>). No clear-side say announce.
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
/// Two independent settings gates (both <see cref="OtherSettings"/>,
/// Char tier):
/// <list type="bullet">
/// <item><c>DoNotAnnounce&lt;X&gt;</c> suppresses the say-announce.</item>
/// <item><c>Ignore&lt;X&gt;</c> suppresses the <c>@wait</c>.</item>
/// </list>
/// They're separate decisions — a party may pause the leader without
/// broadcasting, or broadcast without pausing.
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
    /// The four curable ailments we sync, with their say token and the
    /// <see cref="WaitReason"/> they hold on the leader. Confusion is
    /// included for the say-announce + @wait even though no realm cure
    /// exists for it (stock / paramud) — the announce still lets the
    /// party react.
    /// </summary>
    private static readonly (MessageFlags Flag, string SayToken, WaitReason Reason)[] Ailments =
    {
        (MessageFlags.Poisoned, "@poisoned", WaitReason.Poison),
        (MessageFlags.Blinded,  "@blind",    WaitReason.Blindness),
        (MessageFlags.Confused, "@confused", WaitReason.Confusion),
        (MessageFlags.Diseased, "@diseased", WaitReason.Disease),
    };

    private readonly ConditionTracker _conditions;
    private readonly PartyRestSync _restSync;
    private readonly Func<OtherSettings> _readOther;
    private readonly LogService? _log;

    private MessageFlags _lastFlags;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public AilmentSyncEngine(
        ConditionTracker conditions,
        PartyRestSync restSync,
        Func<OtherSettings> readOther,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(restSync);
        ArgumentNullException.ThrowIfNull(readOther);
        _conditions = conditions;
        _restSync = restSync;
        _readOther = readOther;
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

        foreach ((MessageFlags flag, string token, WaitReason reason) in Ailments)
        {
            if (added.HasFlag(flag))
            {
                if (!IsAnnounceSuppressed(flag, other))
                    Say(token);
                if (!IsWaitSuppressed(flag, other))
                    _restSync.RequestWait(reason);
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
