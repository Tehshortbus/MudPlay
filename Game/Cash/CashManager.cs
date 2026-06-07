using System.Text;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Cash;

/// <summary>
/// Phase 9 PR 9.E — per-currency cash pickup / discard engine.
/// Subscribes to <see cref="KnownPatterns.CashOnGround"/>,
/// <see cref="KnownPatterns.CashPickedUp"/>, and
/// <see cref="KnownPatterns.CashDropped"/>. Dispatches based on
/// <see cref="CashSettings"/> per-currency
/// <see cref="CashPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// v1 scope:
/// </para>
/// <list type="bullet">
/// <item><b>CashOnGround → policy dispatch</b>. Collect →
/// <c>get all &lt;coin&gt;</c>. Discard / Ignore → no action.</item>
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

    private Action<byte[]>? _wireSender;
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
    }

    /// <summary>Bind the wire sender — typically the gate-wrapped
    /// engine pipeline from <c>MainWindowViewModel</c>.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
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
        _log?.Debug(LogCategory, "settings changed — re-evaluating auto-deposit");
        CheckAutoDeposit();
        // Future: when Discard auto-drop lands, walk _held here and
        // emit drops for any Discard-flagged currency we still hold.
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
                Send($"get all {currency}");
                break;
            case CashPolicy.Discard:
                // Don't pick up; don't react. The drop-held-discard
                // branch fires elsewhere (a held-cash audit).
                break;
            case CashPolicy.Ignore:
                break;
        }
    }

    private void OnCashPickedUp(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        AdjustHeld(currency, count);
        CheckAutoDeposit();
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
    }
}
