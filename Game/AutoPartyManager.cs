using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// Engine that consumes the per-player <see cref="PlayerCustomization"/>
/// auto-party flags. Two behaviours, both gated on the loaded character's
/// PlayerDatabase customizations:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Invite-on-seen</b> — when a player whose row carries
///         <see cref="PlayerCustomization.InviteToPartyIfSeen"/>
///         appears in our current room (via the
///         <see cref="KnownPatterns.RoomAlsoHere"/> "Also here: ..." line),
///         send <c>invite &lt;given&gt;</c> on the wire. TTL-suppressed at
///         <see cref="InviteCooldown"/> per recipient so subsequent room
///         re-renders don't re-spam. Skipped when the player is already
///         in <see cref="PartyState.Members"/>.</item>
///   <item><b>Accept-invite</b> — when another player sends us an in-game
///         party invite (<see cref="KnownPatterns.PartyInviteReceived"/>,
///         matching "X has invited you to follow him/her"), look up
///         their customization. If
///         <see cref="PlayerCustomization.JoinPartyIfInvited"/> is set,
///         send <c>follow &lt;given&gt;</c> (the MajorMUD accept
///         mechanism — joining someone's party is "follow them";
///         <see cref="PartyManager"/> already maps "You are now
///         following X" to "we joined X's party").</item>
/// </list>
/// <para>
/// Threading: handler invocation is on the dispatcher thread (the
/// MessageRouter marshals upstream). All state reads + writes happen
/// there, so the <c>_recentlyInvited</c> dictionary doesn't need its
/// own lock.
/// </para>
/// </remarks>
public sealed class AutoPartyManager : IDisposable
{
    private readonly MessageRouter _router;
    private readonly PlayerDatabase _players;
    private readonly PartyState _party;
    private readonly LogService? _log;
    private Action<byte[]>? _wireSender;
    private readonly IDisposable _alsoHereSub;
    private readonly IDisposable _partyInviteSub;
    private bool _disposed;

    /// <summary>
    /// Per-recipient TTL on auto-invites. Subsequent room renders within
    /// this window won't re-fire the invite — they either accepted
    /// (and would be in <see cref="PartyState.Members"/>, taking the
    /// already-in-party branch) or they declined, in which case we
    /// shouldn't keep nagging them once per move. Default 60 s; tunable
    /// at runtime if a feature surfaces a knob for it.
    /// </summary>
    public TimeSpan InviteCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Test seam — overrides <see cref="DateTime.UtcNow"/> for the TTL
    /// math so unit tests don't have to <c>Thread.Sleep</c>. Defaults
    /// to <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    /// <summary>Test seam — most recent bytes the engine asked to write to the wire.</summary>
    internal List<byte[]> LastSentForTests { get; } = new();

    /// <summary>Per-given-name TTL map suppressing rapid re-invites.</summary>
    private readonly Dictionary<string, DateTime> _recentlyInvited =
        new(StringComparer.OrdinalIgnoreCase);

    public AutoPartyManager(MessageRouter router, PlayerDatabase players, PartyState party, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(party);
        _router  = router;
        _players = players;
        _party   = party;
        _log     = log;

        _alsoHereSub    = _router.Subscribe(KnownPatterns.RoomAlsoHere,        OnRoomAlsoHere);
        _partyInviteSub = _router.Subscribe(KnownPatterns.PartyInviteReceived, OnPartyInviteReceived);
    }

    /// <summary>
    /// Bind the wire-sender — same shape as
    /// <see cref="Remote.PartyEssentialHandlers.SetWireSender"/>. The
    /// main-window VM supplies <c>SendUserInput</c>; pre-binding, the
    /// engine still processes events but produces no wire output (so
    /// tests can inspect <see cref="LastSentForTests"/> without
    /// configuring a real sender).
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _alsoHereSub.Dispose();
        _partyInviteSub.Dispose();
    }

    // ----- Handlers ------------------------------------------------------

    private void OnRoomAlsoHere(MatchResult match)
    {
        // Group 0 in the positional list = the (?<players>.+?) capture.
        if (match.Groups.Count == 0) return;
        string list = match.Groups[0];
        if (string.IsNullOrWhiteSpace(list)) return;

        foreach (string raw in SplitOccupantList(list))
        {
            string given = ExtractGiven(raw);
            if (string.IsNullOrEmpty(given)) continue;
            TryAutoInvite(given);
        }
    }

    private void OnPartyInviteReceived(MatchResult match)
    {
        // Group 0 in the positional list = the (?<player>\w+) capture.
        if (match.Groups.Count == 0) return;
        string sender = match.Groups[0];
        if (string.IsNullOrEmpty(sender)) return;
        TryAutoAccept(sender);
    }

    // ----- Behaviour ----------------------------------------------------

    private void TryAutoInvite(string given)
    {
        // Already in our party? Nothing to do.
        foreach (PartyMember m in _party.Members)
        {
            if (string.Equals(ExtractGiven(m.Name), given, StringComparison.OrdinalIgnoreCase))
                return;
        }

        if (!FindCustomization(given, out PlayerCustomization c)) return;
        if (!c.InviteToPartyIfSeen) return;

        // TTL suppression — skip if we've invited them in the cooldown
        // window. Lazy pruning happens here on read.
        DateTime now = NowProvider();
        if (_recentlyInvited.TryGetValue(given, out DateTime sentAt)
            && now - sentAt < InviteCooldown)
        {
            return;
        }
        _recentlyInvited[given] = now;

        SendWire($"invite {given}");
        _log?.Log(LogSeverity.Info, "AutoParty", $"Auto-invited {given} (InviteToPartyIfSeen).");
    }

    private void TryAutoAccept(string sender)
    {
        if (!FindCustomization(sender, out PlayerCustomization c)) return;
        if (!c.JoinPartyIfInvited) return;

        // Already in this player's party? The PartyManager would have
        // populated PartyState.Members on the follow-confirmation line,
        // so a duplicate follow command is a no-op — but skip it
        // anyway to keep the wire quiet.
        foreach (PartyMember m in _party.Members)
        {
            if (string.Equals(ExtractGiven(m.Name), sender, StringComparison.OrdinalIgnoreCase))
                return;
        }

        SendWire($"follow {sender}");
        _log?.Log(LogSeverity.Info, "AutoParty", $"Auto-accepted invite from {sender} (JoinPartyIfInvited).");
    }

    private bool FindCustomization(string given, out PlayerCustomization c)
    {
        foreach (PlayerRecord p in _players.Players)
        {
            if (string.Equals(p.GivenName, given, StringComparison.OrdinalIgnoreCase))
            {
                c = new PlayerCustomization(
                    RemoteControls:      p.RemoteControls,
                    InviteToPartyIfSeen: p.InviteToPartyIfSeen,
                    JoinPartyIfInvited:  p.JoinPartyIfInvited,
                    DontAutoDelete:      p.DontAutoDelete,
                    Notes:               p.Notes);
                return true;
            }
        }
        c = default;
        return false;
    }

    private void SendWire(string command)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        LastSentForTests.Add(bytes);
        _wireSender?.Invoke(bytes);
    }

    // ----- Parsing helpers ---------------------------------------------

    /// <summary>
    /// Split an "Also here:" list capture into individual names. Handles
    /// the three forms observed in MajorMUD: single ("Raijin"), comma
    /// ("Foo, Bar"), and Oxford-and ("Foo, Bar and Baz" / "Foo, Bar, and
    /// Baz"). The capture is already <c>.</c>-stripped by the regex.
    /// </summary>
    private static IEnumerable<string> SplitOccupantList(string list)
    {
        // Normalise " and " → ", " so the comma split handles both forms
        // uniformly. Word-boundary on either side so we don't mangle a
        // name that happens to contain "and" (e.g. "Brandon").
        string normalised = System.Text.RegularExpressions.Regex
            .Replace(list, @"\s+and\s+", ", ");
        foreach (string part in normalised.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    /// <summary>
    /// Extract the given name from a list entry. The "Also here:" list
    /// can include suffixes like "Raijin (sneaking)" or "Forged WuzHere"
    /// (full display name with family). MajorMUD's <c>invite</c> command
    /// only accepts the given name, so always take the first
    /// whitespace-delimited token, then strip any trailing punctuation
    /// or parenthetical.
    /// </summary>
    private static string ExtractGiven(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // First whitespace-delimited token.
        int space = raw.IndexOf(' ');
        string token = space >= 0 ? raw[..space] : raw;
        // Strip any non-letter trailing chars (paren, period, comma…).
        int cut = token.Length;
        while (cut > 0 && !char.IsLetter(token[cut - 1])) cut--;
        return cut <= 0 ? string.Empty : token[..cut];
    }
}
