using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Spells;

/// <summary>
/// Phase 9 PR 9.C — low-level spell-send layer. Builds the
/// <c>c &lt;spell&gt; [target]</c> wire command, gates on a recent-
/// cast cooldown + a "block until next combat tick" latch (set by
/// server failure messages), and emits
/// <see cref="CastSent"/> / <see cref="CastFailed"/> events so
/// <c>CastingDirector</c> (PR 9.D) can sequence decisions on top.
/// </summary>
/// <remarks>
/// <para>
/// Three gates compose the "can I cast right now?" check:
/// </para>
/// <list type="bullet">
/// <item><b>Recent-cast cooldown</b> — one cast per combat round
/// (5.5s default, matches MajorMUD's between-round cap). Cleared by
/// <see cref="OnCombatTick"/> so the very next round can cast
/// immediately.</item>
/// <item><b>Cast-blocked latch</b> — set on server failure lines
/// (<see cref="KnownPatterns.CastFizzled"/>,
/// <see cref="KnownPatterns.CastNoMana"/>,
/// <see cref="KnownPatterns.CastAlreadyThisRound"/>,
/// <see cref="KnownPatterns.CastInterrupted"/>). Cleared by
/// <see cref="OnCombatTick"/> OR by the
/// <see cref="CastBlockExpiry"/> timeout (safety net: out of combat
/// no tick will fire, so we'd be stuck otherwise).</item>
/// <item><b>Min recast interval</b> — short sub-cooldown between
/// two consecutive <see cref="TryCast"/> attempts (500ms) to absorb
/// burst calls when CastingDirector evaluates multiple candidates in
/// the same frame.</item>
/// </list>
/// <para>
/// This is a foundation layer — it does NOT decide what to cast;
/// CastingDirector picks the spell and target and calls
/// <see cref="TryCast"/>. External engines that cast spells outside
/// the director (CombatManager's pre-attack chain, for example) must
/// call <see cref="NotifyExternalCastSent"/> so the cooldown is
/// honoured.
/// </para>
/// </remarks>
public sealed class CastCoordinator : IDisposable
{
    /// <summary>LogService category — appears as <c>[Cast]</c> rows per
    /// send + failure detection + block-expire.</summary>
    public const string LogCategory = "Cast";

    /// <summary>One cast per combat round (5.5s — slightly longer than
    /// the 5s tick to account for server-side rounding). Reset by
    /// <see cref="OnCombatTick"/>.</summary>
    public static readonly TimeSpan CastCommandCooldown = TimeSpan.FromMilliseconds(5500);

    /// <summary>Burst-absorb gap between two <see cref="TryCast"/>
    /// attempts. Keeps a single decision frame from queuing two casts
    /// when only the first should land.</summary>
    public static readonly TimeSpan MinRecastInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the cast-blocked latch lives without a combat
    /// tick clearing it. Out of combat the tick never fires, so the
    /// latch must auto-expire or buffs would never recast.</summary>
    public static readonly TimeSpan CastBlockExpiry = TimeSpan.FromSeconds(3);

    private readonly LogService? _log;
    private readonly IDisposable _fizzleSub;
    private readonly IDisposable _noManaSub;
    private readonly IDisposable _alreadySub;
    private readonly IDisposable _interruptSub;

    private Action<byte[]>? _wireSender;
    private DateTimeOffset _lastCastSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _castBlockedSince = DateTimeOffset.MinValue;
    private bool _castBlocked;
    private bool _disposed;

    /// <summary>Fires after a cast command was successfully written to
    /// the wire. Carries the literal command line (no trailing CR).</summary>
    public event Action<string>? CastSent;

    /// <summary>Fires whenever a cast attempt was rejected — either by
    /// our local gates or by a server failure line. Reason carries the
    /// classification; the second arg is a free-text detail (spell name
    /// from the fizzle regex, "cooldown" for local gating, etc.).</summary>
    public event Action<CastFailureReason, string>? CastFailed;

    public CastCoordinator(MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _log = log;
        _fizzleSub    = router.Subscribe(KnownPatterns.CastFizzled,          OnFizzle);
        _noManaSub    = router.Subscribe(KnownPatterns.CastNoMana,           OnNoMana);
        _alreadySub   = router.Subscribe(KnownPatterns.CastAlreadyThisRound, OnAlreadyThisRound);
        _interruptSub = router.Subscribe(KnownPatterns.CastInterrupted,      OnInterrupted);
    }

    /// <summary>Bind the wire sender — typically the <c>TelnetClient.SendAsync</c>
    /// wrapper exposed by <c>MainWindowViewModel</c>. Until set,
    /// <see cref="TryCast"/> short-circuits to false.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>True while any of the three gates would reject a cast
    /// (block latch, recent-cast cooldown, or min recast interval).
    /// Auto-clears the block latch on read once
    /// <see cref="CastBlockExpiry"/> elapses.</summary>
    public bool IsCastBlocked
    {
        get
        {
            DateTimeOffset now = DateTimeOffset.Now;
            if (_castBlocked && now - _castBlockedSince >= CastBlockExpiry)
            {
                _castBlocked = false;
                _log?.Debug(LogCategory, "cast-block latch expired (no combat tick received)");
            }
            if (_castBlocked) return true;
            return now - _lastCastSentAt < CastCommandCooldown;
        }
    }

    /// <summary>Timestamp of the most recent successful cast emit.
    /// <see cref="DateTimeOffset.MinValue"/> when no cast has been sent
    /// since the last tick reset.</summary>
    public DateTimeOffset LastCastSentAt => _lastCastSentAt;

    /// <summary>
    /// Attempt to send <c>c &lt;spell&gt; [target]</c>. Returns
    /// <c>true</c> only if the command actually went to the wire.
    /// Burst-absorbs back-to-back attempts via
    /// <see cref="MinRecastInterval"/> + checks
    /// <see cref="IsCastBlocked"/>.
    /// </summary>
    /// <param name="spellName">The spell command-name the user
    /// configured (e.g. "heal", "freedom"). Whitespace-only is a
    /// no-op false return.</param>
    /// <param name="target">Optional explicit target — "self", a party
    /// member's name, or a monster name. Omit for self-cast spells
    /// where the server defaults the target to the caster.</param>
    public bool TryCast(string spellName, string? target = null)
    {
        if (string.IsNullOrWhiteSpace(spellName)) return false;
        if (_wireSender is null) return false;

        DateTimeOffset now = DateTimeOffset.Now;
        if (IsCastBlocked)
        {
            CastFailed?.Invoke(CastFailureReason.Blocked, "cast-blocked");
            return false;
        }
        if (now - _lastCastSentAt < MinRecastInterval)
        {
            CastFailed?.Invoke(CastFailureReason.Blocked, "recast-interval");
            return false;
        }

        string spell = spellName.Trim();
        string line = string.IsNullOrWhiteSpace(target)
            ? $"c {spell}"
            : $"c {spell} {target.Trim()}";
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
        _lastCastSentAt = now;
        _log?.Info(LogCategory, $"cast spell={spell} target={target ?? "<self>"}");
        CastSent?.Invoke(line);
        return true;
    }

    /// <summary>
    /// External-cast notification. Engines that issue spell commands
    /// outside the coordinator (CombatManager's pre-attack debuff, a
    /// user-typed `c X` line, etc.) must call this so the cooldown
    /// blocks subsequent <see cref="TryCast"/> attempts for this round.
    /// </summary>
    public void NotifyExternalCastSent()
    {
        _lastCastSentAt = DateTimeOffset.Now;
        _log?.Debug(LogCategory, "external cast noted — cooldown started");
    }

    /// <summary>
    /// Hook the combat-tick boundary. Clears the block latch + resets
    /// the recent-cast cooldown so the next round can cast immediately.
    /// Subscribe by wiring <see cref="TickEngine.CombatTickElapsed"/>
    /// to this method in <c>AppServices</c>.
    /// </summary>
    public void OnCombatTick()
    {
        if (_castBlocked)
        {
            _castBlocked = false;
            _log?.Debug(LogCategory, "cast-block latch cleared on combat tick");
        }
        _lastCastSentAt = DateTimeOffset.MinValue;
    }

    // ----- failure handlers ------------------------------------------

    private void OnFizzle(MatchResult m)
    {
        string spell = m.Groups.Count > 0 ? m.Groups[0] : "<unknown>";
        BlockAndLog(CastFailureReason.Fizzled, $"spell={spell}");
    }

    private void OnNoMana(MatchResult _) =>
        BlockAndLog(CastFailureReason.NotEnoughMana, "insufficient-mana");

    private void OnAlreadyThisRound(MatchResult _) =>
        BlockAndLog(CastFailureReason.AlreadyCastThisRound, "already-cast-this-round");

    private void OnInterrupted(MatchResult _) =>
        BlockAndLog(CastFailureReason.Interrupted, "concentration-lost");

    private void BlockAndLog(CastFailureReason reason, string detail)
    {
        _castBlocked = true;
        _castBlockedSince = DateTimeOffset.Now;
        _log?.Info(LogCategory, $"cast failed reason={reason} {detail}");
        CastFailed?.Invoke(reason, detail);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fizzleSub.Dispose();
        _noManaSub.Dispose();
        _alreadySub.Dispose();
        _interruptSub.Dispose();
    }
}
