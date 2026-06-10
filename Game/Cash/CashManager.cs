using System.Text;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Cash;

/// <summary>
/// Phase 9 PR 9.E — per-currency cash pickup / discard engine.
/// Subscribes to <see cref="KnownPatterns.CashOnGround"/>,
/// <see cref="KnownPatterns.CashPickedUp"/>,
/// <see cref="KnownPatterns.CashDropped"/>, and
/// <see cref="KnownPatterns.CashFromKill"/> (corpse loot after a
/// monster dies). Dispatches based on <see cref="CashSettings"/>
/// per-currency <see cref="CashPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// v1 scope:
/// </para>
/// <list type="bullet">
/// <item><b>CashOnGround → policy dispatch</b>. Collect →
/// <c>get &lt;count&gt; &lt;coin&gt;</c> with the exact observed
/// count (specific amounts keep encumbrance / weight tracking
/// deterministic). Discard / Ignore → no action.</item>
/// <item><b>CashPickedUp / CashDropped → tally update</b>. Held
/// counts exposed via <see cref="HeldCoin"/> for the wealth-
/// threshold check.</item>
/// <item><b>AutoDeposit trigger</b>. When the gold-equivalent total
/// crosses <see cref="CashSettings.AutoDepositIfWealthExceeds"/>,
/// <see cref="AutoDepositRequested"/> fires once per crossing.
/// Subscribers (the future walker reroute) decide what to do —
/// v1 only signals.</item>
/// </list>
/// <para>
/// <b>Deferred to follow-ups</b>:
/// </para>
/// <list type="bullet">
/// <item>In-flight delta projection (60s timeout) — guards
/// against the server emitting a CashPickedUp line after our get
/// command but before our wealth display refreshes.</item>
/// <item>Encumbrance gates — skip pickup if would push into Light /
/// Medium / Heavy bracket.</item>
/// <item>Drop-smaller-for-larger cascade.</item>
/// <item>Walker-driven auto-deposit reroute (snapshot activity →
/// pause → walk to bank → deposit → walk back → resume).</item>
/// <item>Per-realm currency naming (runic in particular varies by
/// BBS — v1 hardcodes the stock set; per-realm renames live on
/// the Phase 4 Settings → BBS tab).</item>
/// </list>
/// <para>
/// Master switch: <see cref="AutoActionDefaults.AutoGetCash"/>
/// (shared with the Settings → General toggle and the toolbar
/// Toggle command).
/// </para>
/// </remarks>
public sealed class CashManager : IDisposable
{
    /// <summary>LogService category — appears as <c>[Cash]</c> rows
    /// per dispatch + threshold fire.</summary>
    public const string LogCategory = "Cash";

    /// <summary>Gold-equivalent multipliers per currency unit. Stock
    /// MajorMUD economy:
    /// 1 platinum = 100 gold; 1 gold = 100 silver; 1 silver = 100
    /// copper; 1 runic = 1000 gold (varies per realm — overridable
    /// in a follow-up).</summary>
    private static readonly IReadOnlyDictionary<string, long> GoldEquivalent =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["copper"]   = 1,        // 1/100 gold but we track integer; cap to 1
            ["silver"]   = 1,        // ditto — for v1 we round low-denominations to 1g
            ["gold"]     = 1,
            ["platinum"] = 100,
            ["runic"]    = 1000,
        };

    private readonly Func<CashSettings> _readSettings;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;
    private readonly IDisposable _groundSub;
    private readonly IDisposable _pickedUpSub;
    private readonly IDisposable _droppedSub;
    private readonly IDisposable _hiddenSub;
    private readonly IDisposable _noticeSub;
    private readonly IDisposable _killDropSub;
    private Terminal.LineExtractor? _lines;
    private string? _noticeBuffer;       // multi-line continuation
    private string? _noticeRawFirst;     // raw first row that started the buffer

    /// <summary>Recognised cash denomination words (case-insensitive).
    /// User's screenshot showed "silver nobles" / "copper farthings";
    /// expand by adding more entries here when realms use unique
    /// denomination words.</summary>
    private static readonly HashSet<string> CashDenominations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "copper", "silver", "gold", "platinum", "runic",
        };

    private Action<byte[]>? _wireSender;
    private Game.Inventory.AcquisitionGate? _gate;
    private readonly Dictionary<string, long> _held = new(StringComparer.OrdinalIgnoreCase);
    private bool _autoDepositFiredThisCrossing;
    private bool _disposed;

    /// <summary>Fires whenever a CashOnGround line resolves the
    /// per-currency policy decision. Args: currency, count, decided
    /// action.</summary>
    public event Action<string, int, CashPolicy>? CashDispatched;

    /// <summary>Fires once when the gold-equivalent held wealth
    /// crosses <see cref="CashSettings.AutoDepositIfWealthExceeds"/>.
    /// Single-shot per crossing — re-arms when wealth drops back
    /// below the threshold.</summary>
    public event Action<long>? AutoDepositRequested;

    public CashManager(
        MessageRouter router,
        Func<CashSettings> readSettings,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _readSettings = readSettings;
        _isEnabled = isEnabled;
        _log = log;

        _groundSub   = router.Subscribe(KnownPatterns.CashOnGround,  OnCashOnGround);
        _pickedUpSub = router.Subscribe(KnownPatterns.CashPickedUp,  OnCashPickedUp);
        _droppedSub  = router.Subscribe(KnownPatterns.CashDropped,   OnCashDropped);
        // `hide N <coin>` is the stash-room verb — same tally semantics
        // as drop. Without this subscription, stashing decrements only
        // via item-hide (UserHides) which isn't currency-aware; the
        // held tally would go stale and AutoDeposit would misfire on
        // phantom wealth. Note for future inventory subsystem: its
        // UserHides handler must skip currency-shape item text so we
        // don't double-decrement here vs. there.
        _hiddenSub   = router.Subscribe(KnownPatterns.CashHidden,    OnCashHidden);
        _noticeSub   = router.Subscribe(KnownPatterns.YouNoticeRoom, OnYouNoticeRoom);
        _killDropSub = router.Subscribe(KnownPatterns.CashFromKill,  OnCashFromKill);
    }

    /// <summary>Bind the wire sender — typically the gate-wrapped
    /// engine pipeline from <c>MainWindowViewModel</c>.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>Bind the shared <see cref="Game.Inventory.AcquisitionGate"/>
    /// so collecting cash holds the walker until get-clear (the same gate
    /// the item engine feeds). Optional — when unbound the engine behaves
    /// exactly as v1 (no movement gate). Only the Collect path asserts;
    /// Discard/Ignore don't gate movement.</summary>
    public void SetAcquisitionGate(Game.Inventory.AcquisitionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    /// <summary>Current held count of <paramref name="currency"/> as
    /// observed via CashPickedUp / CashDropped lines since engine
    /// start. Resets on app close; not persisted (the wealth display
    /// is authoritative; we track here for the auto-deposit
    /// threshold).</summary>
    public long HeldCoin(string currency)
    {
        return _held.TryGetValue(currency, out long count) ? count : 0;
    }

    /// <summary>Gold-equivalent of all held coins via
    /// <see cref="GoldEquivalent"/>.</summary>
    public long HeldGoldEquivalent
    {
        get
        {
            long total = 0;
            foreach ((string c, long n) in _held)
            {
                if (GoldEquivalent.TryGetValue(c, out long mult))
                    total += n * mult;
            }
            return total;
        }
    }

    /// <summary>Reset held counts (called on profile load to drop
    /// the prior character's tallies).</summary>
    public void ResetTallies()
    {
        _held.Clear();
        _autoDepositFiredThisCrossing = false;
    }

    /// <summary>
    /// Re-evaluate state after a settings edit. Call this when the
    /// user changes a per-currency policy (e.g. flips Collect to
    /// Discard) or the auto-deposit threshold so the engine reacts
    /// immediately instead of waiting for the next CashPickedUp /
    /// CashOnGround line. Mirrors MudProxy's
    /// <c>CashManager.OnSettingsChanged</c> (CashManager.cs:479-497).
    /// </summary>
    public void OnSettingsChanged()
    {
        _log?.Debug(LogCategory, "settings changed — re-evaluating auto-deposit + discard");
        CheckAutoDeposit();
        AuditHeldForDiscard();
    }

    /// <summary>
    /// Walk held tallies; for any currency whose policy is Discard
    /// AND we hold &gt; 0, emit <c>drop &lt;amount&gt; &lt;type&gt;</c>
    /// (the confirmed MajorMUD syntax for currency drops). The
    /// CashDropped subscription decrements <c>_held</c> when the
    /// server confirms; we don't optimistically decrement so the
    /// audit retries on the next firing if the drop fails.
    /// </summary>
    private void AuditHeldForDiscard()
    {
        if (!_isEnabled()) return;
        CashSettings settings = _readSettings();
        foreach ((string currency, long count) in _held.ToList())
        {
            if (count <= 0) continue;
            if (ResolvePolicy(settings, currency) != CashPolicy.Discard) continue;
            _log?.Info(LogCategory, $"discard drop currency={currency} count={count}");
            Send($"drop {count} {currency}");
        }
    }

    // ----- handlers ----------------------------------------------------

    private void OnCashOnGround(MatchResult m)
    {
        if (!_isEnabled()) return;

        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        CashSettings settings = _readSettings();
        CashPolicy policy = ResolvePolicy(settings, currency);

        _log?.Info(LogCategory,
            $"on-ground currency={currency} count={count} policy={policy}");
        CashDispatched?.Invoke(currency, count, policy);

        switch (policy)
        {
            case CashPolicy.Collect:
                // Specific amount (not bare `get <currency>` which
                // would grab all available) so encumbrance / weight
                // tracking can do exact arithmetic instead of waiting
                // for the wealth display to refresh.
                CollectCoins(count, currency);
                break;
            case CashPolicy.Discard:
                // Don't pick up; don't react. The drop-held-discard
                // branch fires elsewhere (a held-cash audit).
                break;
            case CashPolicy.Ignore:
                break;
        }
    }

    /// <summary>
    /// Corpse-loot handler — "N &lt;currency&gt; drop to the ground."
    /// fires from <see cref="KnownPatterns.CashFromKill"/> after a
    /// monster dies. Funnels into the same per-currency policy
    /// dispatch as room-display cash so kill-loot honours the user's
    /// Collect / Discard / Ignore choices.
    /// </summary>
    private void OnCashFromKill(MatchResult m)
    {
        if (!_isEnabled()) return;

        // CashFromKill pattern uses 2 groups (count, currency) — the
        // shared ParseCashLine helper handles 3 groups (singular /
        // plural branch), so we parse inline here.
        if (m.Groups.Count < 2) return;
        if (!int.TryParse(m.Groups[0], out int count)) return;
        string currency = m.Groups[1].Trim();
        if (currency.Length == 0) return;
        if (!CashDenominations.Contains(currency)) return;
        currency = currency.ToLowerInvariant();

        CashSettings settings = _readSettings();
        CashPolicy policy = ResolvePolicy(settings, currency);

        _log?.Info(LogCategory,
            $"corpse-drop currency={currency} count={count} policy={policy}");
        CashDispatched?.Invoke(currency, count, policy);

        if (policy == CashPolicy.Collect)
            CollectCoins(count, currency);
    }

    private void OnCashPickedUp(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        AdjustHeld(currency, count);
        CheckAutoDeposit();
        // Picked up a currency the user marked Discard (or settings
        // changed since the last audit) — drop it.
        AuditHeldForDiscard();
    }

    private void OnCashDropped(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        AdjustHeld(currency, -count);
        CheckAutoDeposit();
    }

    /// <summary>
    /// Stash-room confirmation handler — tally identically to drop.
    /// The <c>hide</c> wire shape is what stash-room visits use to
    /// dump excess coin / items; without this the auto-deposit
    /// threshold drifts stale after a stash run.
    /// </summary>
    private void OnCashHidden(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        _log?.Debug(LogCategory, $"hidden currency={currency} count={count}");
        AdjustHeld(currency, -count);
        CheckAutoDeposit();
    }

    /// <summary>
    /// Single-line "You notice <list> here." — splits the list and
    /// dispatches each recognised cash entry through the same
    /// per-currency policy path as <see cref="OnCashOnGround"/>.
    /// The multi-line wrap variant joins through the LineExtractor
    /// buffer (see <see cref="OnLine"/>) and feeds the same parse.
    /// </summary>
    private void OnYouNoticeRoom(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        DispatchYouNoticeList(m.Groups[0]);
    }

    /// <summary>
    /// Bind the per-session <see cref="Terminal.LineExtractor"/> so
    /// the manager can stitch wrapped "You notice" lines back
    /// together — same shape as the
    /// <see cref="Game.Combat.RoomEntityClassifier"/> fix for
    /// "Also here:".
    /// </summary>
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    private void OnLine(Terminal.LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string text = line.Text.TrimEnd();
        if (text.Length == 0) return;

        if (_noticeBuffer is not null)
        {
            _noticeBuffer = _noticeBuffer + " " + text;
            if (text.EndsWith(".", StringComparison.Ordinal))
            {
                string complete = _noticeBuffer;
                _noticeBuffer = null;
                _noticeRawFirst = null;
                ProcessYouNoticeMultiLine(complete);
            }
            return;
        }

        if (text.StartsWith("You notice ", StringComparison.Ordinal))
        {
            if (text.EndsWith(".", StringComparison.Ordinal))
            {
                // Single-line case — pattern subscription already
                // handles it; skip to avoid double-processing.
                return;
            }
            _noticeBuffer = text;
            _noticeRawFirst = line.Text;
        }
    }

    private void ProcessYouNoticeMultiLine(string completeLine)
    {
        // Strip "You notice " prefix and " here." suffix.
        const string prefix = "You notice ";
        if (!completeLine.StartsWith(prefix, StringComparison.Ordinal)) return;
        string body = completeLine[prefix.Length..].TrimEnd();
        const string suffix = " here.";
        if (body.EndsWith(suffix, StringComparison.Ordinal))
            body = body[..^suffix.Length];
        else if (body.EndsWith(".", StringComparison.Ordinal))
            body = body[..^1];
        DispatchYouNoticeList(body);
    }

    /// <summary>
    /// Split "X gold sovereigns, Y silver nobles, an item, ..."
    /// into entries; for each, decide if it's cash (count +
    /// recognised denomination) and dispatch through the
    /// per-currency policy. Non-cash entries are item references —
    /// silently skipped until an items subsystem ships.
    /// </summary>
    private void DispatchYouNoticeList(string list)
    {
        if (!_isEnabled()) return;
        CashSettings settings = _readSettings();

        foreach (string raw in list.Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) continue;
            if (!TryParseCashEntry(raw, out string? currency, out int count)) continue;

            CashPolicy policy = ResolvePolicy(settings, currency!);
            _log?.Info(LogCategory,
                $"you-notice cash currency={currency} count={count} policy={policy}");
            CashDispatched?.Invoke(currency!, count, policy);

            if (policy == CashPolicy.Collect)
                CollectCoins(count, currency!);
        }
    }

    /// <summary>Dispatch a Collect <c>get</c> and hold the walker via the
    /// shared <see cref="Game.Inventory.AcquisitionGate"/> until get-clear.
    /// Single funnel for all three collect sites (room display, corpse
    /// drop, "You notice" list) so the gate note can't be missed.</summary>
    private void CollectCoins(int count, string currency)
    {
        _gate?.NoteGetSent();
        Send($"get {count} {currency}");
    }

    /// <summary>Recognise <c>"N {denomination} ..."</c> as cash —
    /// requires a leading integer + the second word being a
    /// <see cref="CashDenominations"/> entry. Singular form
    /// <c>"a gold piece"</c> is also tolerated (count = 1).</summary>
    private static bool TryParseCashEntry(string raw, out string? currency, out int count)
    {
        currency = null;
        count = 0;
        string[] words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return false;

        // "a <denomination> ..." singular variant
        if (string.Equals(words[0], "a", StringComparison.OrdinalIgnoreCase)
         && CashDenominations.Contains(words[1]))
        {
            currency = words[1].ToLowerInvariant();
            count = 1;
            return true;
        }

        // "N <denomination> ..." plural variant
        if (int.TryParse(words[0], out int n) && words.Length >= 2
         && CashDenominations.Contains(words[1]))
        {
            currency = words[1].ToLowerInvariant();
            count = n;
            return true;
        }

        return false;
    }

    /// <summary>Parse (count, currency) from a cash line match.
    /// Returns count=1 for singular form ("a gold piece") and the
    /// captured digit for plural form.</summary>
    private static (string? Currency, int Count) ParseCashLine(MatchResult m)
    {
        // Singular branch: groups[0]="currency" populated, groups[1]+ empty.
        // Plural branch: groups[1]="count", groups[2]="currency2".
        if (m.Groups.Count == 0) return (null, 0);
        string? currency;
        int count;
        if (!string.IsNullOrEmpty(m.Groups[0]))
        {
            currency = m.Groups[0].Trim();
            count = 1;
        }
        else if (m.Groups.Count >= 3
                 && int.TryParse(m.Groups[1], out int n))
        {
            currency = m.Groups[2].Trim();
            count = n;
        }
        else
        {
            return (null, 0);
        }
        return (string.IsNullOrEmpty(currency) ? null : currency, count);
    }

    private static CashPolicy ResolvePolicy(CashSettings s, string currency)
    {
        return currency.ToLowerInvariant() switch
        {
            "copper"   => s.CopperPolicy,
            "silver"   => s.SilverPolicy,
            "gold"     => s.GoldPolicy,
            "platinum" => s.PlatinumPolicy,
            "runic"    => s.RunicPolicy,
            _          => CashPolicy.Ignore,    // unknown currency name → don't touch
        };
    }

    private void AdjustHeld(string currency, int delta)
    {
        if (!_held.TryGetValue(currency, out long current)) current = 0;
        long next = current + delta;
        if (next < 0) next = 0;
        _held[currency] = next;
    }

    private void CheckAutoDeposit()
    {
        CashSettings settings = _readSettings();
        long threshold = settings.AutoDepositIfWealthExceeds;
        if (threshold <= 0) return;
        long held = HeldGoldEquivalent;

        if (held > threshold && !_autoDepositFiredThisCrossing)
        {
            _autoDepositFiredThisCrossing = true;
            _log?.Info(LogCategory,
                $"auto-deposit triggered held-gold-eq={held} threshold={threshold}");
            AutoDepositRequested?.Invoke(held);
        }
        else if (held <= threshold && _autoDepositFiredThisCrossing)
        {
            _autoDepositFiredThisCrossing = false;
            _log?.Debug(LogCategory,
                $"auto-deposit re-armed held-gold-eq={held}");
        }
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _groundSub.Dispose();
        _pickedUpSub.Dispose();
        _droppedSub.Dispose();
        _hiddenSub.Dispose();
        _noticeSub.Dispose();
        _killDropSub.Dispose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}
