using System.Text.RegularExpressions;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Inventory;

/// <summary>
/// Tracks the player's currency and carry-weight by watching the terminal
/// line stream: a full <c>i</c> dump re-bases the snapshot, and incremental
/// coin pickups / drops / bank deposits / withdrawals patch it between dumps.
/// Publishes an immutable <see cref="InventorySnapshot"/> the cash engine
/// reads instead of tracking coin lines itself.
/// </summary>
/// <remarks>
/// <para>
/// This is the cash-hardening slice of Phase 9 PR 9.1 — it models currency
/// (per-denomination counts + consolidated wealth) and the numeric
/// encumbrance reading (current / max / percentage / bracket). The item and
/// equipment-slot model is a follow-up slice; the cash engine doesn't need
/// it.
/// </para>
/// <para>
/// <b>Currency ratios</b> are MajorMUD-faithful in copper farthings:
/// 1 silver = 10, 1 gold = 100, 1 platinum = 10000, 1 runic = 1000000 — the
/// same ladder MudProxy's InventoryManager uses so wealth + encumbrance math
/// stays in step with the game.
/// </para>
/// <para>
/// <b>Coin weight</b> follows the game's rule: 3 coins of any denomination =
/// 1 encumbrance unit, integer-truncated. Picking up 1–2 coins when the prior
/// total was a multiple of 3 moves weight by 0, matching the stat readout.
/// </para>
/// <para>
/// <b>Realm</b>: FujinTerm has no RealmType setting yet (Phase 12), so the
/// derived encumbrance bracket between full parses uses Stock boundaries
/// (None ≤ 16%). A full <c>i</c> parse always overrides the derived bracket
/// with the game's literal word, so any drift self-corrects on the next dump.
/// When RealmType lands, thread it into <see cref="DeriveCategory"/>.
/// </para>
/// <para>
/// <b>Single-writer</b>: this manager keeps its own snapshot and never writes
/// <see cref="PlayerState.Encumbrance"/> — that field's sole writer is
/// <see cref="EncumbranceParser"/>. The two observe the same line
/// independently.
/// </para>
/// </remarks>
public sealed partial class InventoryManager : IDisposable
{
    /// <summary>LogService category — <c>[Inventory]</c> rows per parse / patch.</summary>
    public const string LogCategory = "Inventory";

    // Stock None/Light encumbrance boundary (percent). ParaMUD puts it at 15;
    // wire RealmType through DeriveCategory when Phase 12 adds the setting.
    private const int StockNoneCeiling = 16;

    private const int MaxCaptureLines = 50;

    private readonly LogService? _log;
    private readonly object _lock = new();

    private LineExtractor? _lines;
    private bool _disposed;

    // ----- snapshot state (guarded by _lock) ---------------------------
    private int _copper, _silver, _gold, _platinum, _runic;
    private long _wealthCopper;
    private int _curWeight, _maxWeight, _percentage;
    private EncumbranceLevel _category = EncumbranceLevel.Unknown;
    private bool _loaded;
    private DateTimeOffset _lastUpdated = DateTimeOffset.MinValue;

    // ----- full-'i' capture FSM (single-threaded — OnLine only) --------
    private bool _capturing;
    private readonly List<string> _captureBuffer = new();

    // Wrap-merge: the MUD wraps long lines (~78 cols), so a multi-currency
    // "You deposit 1 platinum piece, 93 gold crowns, ... copper farthin" +
    // "gs." splits across two emitted rows. Hold a non-'.'-terminated
    // transaction-start line and prepend it to the next row for one retry.
    private string _pendingMergeLine = "";

    public InventoryManager(LogService? log = null)
    {
        _log = log;
    }

    /// <summary>Fired (outside the lock) whenever the snapshot changes.</summary>
    public event Action? Changed;

    /// <summary>True after at least one successful full <c>i</c> parse.</summary>
    public bool IsLoaded
    {
        get { lock (_lock) return _loaded; }
    }

    /// <summary>Immutable point-in-time copy of the currency + encumbrance state.</summary>
    public InventorySnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return new InventorySnapshot(
                    new CurrencyHoldings(_copper, _silver, _gold, _platinum, _runic, _wealthCopper),
                    new EncumbranceReading(_curWeight, _maxWeight, _percentage, _category),
                    _lastUpdated);
            }
        }
    }

    /// <summary>
    /// Bind the per-session <see cref="LineExtractor"/> so the manager can
    /// watch the line stream. Idempotent for the same instance; swapping
    /// extractors detaches the old one first.
    /// </summary>
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    /// <summary>
    /// Mark the snapshot stale (e.g. after death / disconnect) without
    /// clearing the data. Drops any half-captured wrap-merge fragment so a
    /// mid-transaction cut doesn't poison the next session's first line.
    /// </summary>
    public void MarkStale()
    {
        lock (_lock) _loaded = false;
        _pendingMergeLine = "";
    }

    // ----- line processing ---------------------------------------------

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string text = line.Text.TrimEnd();

        if (_capturing)
        {
            _captureBuffer.Add(text);

            if (text.TrimStart().StartsWith("Encumbrance:", StringComparison.Ordinal))
            {
                ParseFullInventory();
                _capturing = false;
                _captureBuffer.Clear();
                return;
            }

            if (_captureBuffer.Count >= MaxCaptureLines)
            {
                _log?.Debug(LogCategory, "capture aborted: no Encumbrance line within buffer limit");
                _capturing = false;
                _captureBuffer.Clear();
            }
            return;
        }

        if (text.StartsWith("You are carrying ", StringComparison.Ordinal)
            || text == "You are carrying nothing.")
        {
            _captureBuffer.Clear();
            _captureBuffer.Add(text);
            _capturing = true;
            return;
        }

        // Incremental: apply a pending wrap-merge first, then detect a fresh
        // wrapped transaction-start to stash for the next row.
        string toProcess = text;
        if (_pendingMergeLine.Length > 0)
        {
            toProcess = _pendingMergeLine + text;
            _pendingMergeLine = "";
        }

        ProcessIncremental(toProcess);

        if (LooksLikeWrappedTransactionStart(text))
            _pendingMergeLine = text;
    }

    private static bool LooksLikeWrappedTransactionStart(string line)
    {
        if (line.EndsWith('.')) return false;
        return line.StartsWith("You deposit ", StringComparison.Ordinal)
            || line.StartsWith("You withdrew ", StringComparison.Ordinal)
            || line.StartsWith("you withdrew ", StringComparison.Ordinal);
    }

    private void ProcessIncremental(string line)
    {
        Match pickedUp = PickedUpCurrencyRegex().Match(line);
        if (pickedUp.Success)
        {
            if (int.TryParse(pickedUp.Groups[1].Value, out int amount))
            {
                lock (_lock) AdjustCurrency(pickedUp.Groups[2].Value, amount);
                Changed?.Invoke();
            }
            return;
        }

        Match dropped = DroppedCurrencyRegex().Match(line);
        if (dropped.Success)
        {
            if (int.TryParse(dropped.Groups[1].Value, out int amount))
            {
                lock (_lock) AdjustCurrency(dropped.Groups[2].Value, -amount);
                Changed?.Invoke();
            }
            return;
        }

        Match deposit = DepositCurrencyRegex().Match(line);
        if (deposit.Success)
        {
            long amount = ParsePriceToCopper(deposit.Groups[1].Value);
            lock (_lock) ApplyTransaction(-amount);
            Changed?.Invoke();
            return;
        }

        Match withdraw = WithdrawCurrencyRegex().Match(line);
        if (withdraw.Success)
        {
            long amount = ParsePriceToCopper(withdraw.Groups[1].Value);
            lock (_lock) ApplyTransaction(amount);
            Changed?.Invoke();
        }
    }

    // ----- full parse --------------------------------------------------

    private void ParseFullInventory()
    {
        string? wealthLine = null;
        string? encumbranceLine = null;
        var currencyTokens = new List<string>();

        foreach (string raw in _captureBuffer)
        {
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("Wealth:", StringComparison.Ordinal))
                wealthLine = trimmed;
            else if (trimmed.StartsWith("Encumbrance:", StringComparison.Ordinal))
                encumbranceLine = trimmed;
        }

        // Reconstruct the word-wrapped items text and pull currency tokens out
        // of it. Item tokens are ignored in this slice — only coins matter.
        string itemsText = string.Join(" ", CollectItemLines());
        const string prefix = "You are carrying ";
        if (itemsText.StartsWith(prefix, StringComparison.Ordinal))
            itemsText = itemsText[prefix.Length..];
        else if (itemsText.StartsWith("You are carrying nothing", StringComparison.Ordinal))
            itemsText = string.Empty;

        // The dump's items sentence ends with a period ("... 5 copper
        // farthings."). Drop it so the final ", "-split token is the bare
        // currency entry the anchored CurrencyTokenRegex expects.
        itemsText = itemsText.TrimEnd('.', ' ');

        int copper = 0, silver = 0, gold = 0, platinum = 0, runic = 0;
        if (itemsText.Length > 0)
        {
            foreach (string token in itemsText.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParseCurrency(token, out int count, out string denom))
                {
                    switch (denom)
                    {
                        case "runic": runic = count; break;
                        case "platinum": platinum = count; break;
                        case "gold": gold = count; break;
                        case "silver": silver = count; break;
                        case "copper": copper = count; break;
                    }
                    currencyTokens.Add(token);
                }
            }
        }

        long wealthCopper = ComputeWealth(copper, silver, gold, platinum, runic);
        if (wealthLine is not null)
        {
            Match wm = WealthRegex().Match(wealthLine);
            if (wm.Success && long.TryParse(wm.Groups[1].Value, out long w))
                wealthCopper = w;   // authoritative — game's own consolidated total
        }

        int curWeight = 0, maxWeight = 0, percentage = 0;
        EncumbranceLevel category = EncumbranceLevel.Unknown;
        if (encumbranceLine is not null)
        {
            Match em = EncumbranceRegex().Match(encumbranceLine);
            if (em.Success)
            {
                int.TryParse(em.Groups[1].Value, out curWeight);
                int.TryParse(em.Groups[2].Value, out maxWeight);
                int.TryParse(em.Groups[4].Value, out percentage);
                category = EncumbranceParser.ParseLevel(encumbranceLine);
            }
        }

        lock (_lock)
        {
            _copper = copper;
            _silver = silver;
            _gold = gold;
            _platinum = platinum;
            _runic = runic;
            _wealthCopper = wealthCopper;
            _curWeight = curWeight;
            _maxWeight = maxWeight;
            _percentage = percentage;
            _category = category;
            _loaded = true;
            _lastUpdated = DateTimeOffset.Now;
        }

        _log?.Debug(LogCategory,
            $"parsed: wealth={wealthCopper} copper, enc={curWeight}/{maxWeight} {category} [{percentage}%]");
        Changed?.Invoke();
    }

    // The capture buffer holds the "You are carrying ..." rows plus the Keys /
    // Wealth / Encumbrance trailers. Item rows are everything that isn't one of
    // those trailers; we join them to undo the ~80-col word wrap before
    // splitting on ", ".
    private IEnumerable<string> CollectItemLines()
    {
        foreach (string raw in _captureBuffer)
        {
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("Wealth:", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("Encumbrance:", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("You have no keys", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("You have the following keys", StringComparison.Ordinal)) continue;
            yield return trimmed;
        }
    }

    // ----- currency math (all under _lock) -----------------------------

    private void AdjustCurrency(string coinType, int amount)
    {
        long oldCoins = TotalCoins();

        string normalized = coinType.TrimEnd('s');
        if (normalized.StartsWith("runic coin", StringComparison.OrdinalIgnoreCase))
            _runic = Math.Max(0, _runic + amount);
        else if (normalized.StartsWith("platinum piece", StringComparison.OrdinalIgnoreCase))
            _platinum = Math.Max(0, _platinum + amount);
        else if (normalized.StartsWith("gold crown", StringComparison.OrdinalIgnoreCase))
            _gold = Math.Max(0, _gold + amount);
        else if (normalized.StartsWith("silver noble", StringComparison.OrdinalIgnoreCase))
            _silver = Math.Max(0, _silver + amount);
        else if (normalized.StartsWith("copper farthing", StringComparison.OrdinalIgnoreCase))
            _copper = Math.Max(0, _copper + amount);

        _wealthCopper = ComputeWealth(_copper, _silver, _gold, _platinum, _runic);
        ApplyWeightDelta(oldCoins);
    }

    private void ApplyTransaction(long copperDelta)
    {
        long oldCoins = TotalCoins();

        long wealth = Math.Max(0, _wealthCopper + copperDelta);
        _wealthCopper = wealth;

        _runic = (int)Math.Min(int.MaxValue, wealth / 1_000_000L); wealth %= 1_000_000L;
        _platinum = (int)Math.Min(int.MaxValue, wealth / 10_000L); wealth %= 10_000L;
        _gold = (int)Math.Min(int.MaxValue, wealth / 100L); wealth %= 100L;
        _silver = (int)Math.Min(int.MaxValue, wealth / 10L); wealth %= 10L;
        _copper = (int)Math.Min(int.MaxValue, wealth);

        ApplyWeightDelta(oldCoins);
    }

    private long TotalCoins() => (long)_copper + _silver + _gold + _platinum + _runic;

    private void ApplyWeightDelta(long oldCoins)
    {
        long weightDelta = TotalCoins() / 3 - oldCoins / 3;
        _curWeight = (int)Math.Max(0, _curWeight + weightDelta);
        _percentage = _maxWeight > 0 ? (int)((long)_curWeight * 100 / _maxWeight) : 0;
        _category = DeriveCategory(_percentage);
    }

    private static long ComputeWealth(int copper, int silver, int gold, int platinum, int runic)
    {
        try
        {
            return checked(
                copper +
                silver * 10L +
                gold * 100L +
                platinum * 10_000L +
                runic * 1_000_000L);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    // Stock boundaries (None ≤ 16%). Thread RealmType through here in Phase 12.
    private static EncumbranceLevel DeriveCategory(int percentage)
    {
        if (percentage <= StockNoneCeiling) return EncumbranceLevel.None;
        if (percentage <= 33) return EncumbranceLevel.Light;
        if (percentage <= 66) return EncumbranceLevel.Medium;
        return EncumbranceLevel.Heavy;
    }

    /// <summary>
    /// True when <paramref name="token"/> is exactly one currency entry
    /// (e.g. "30 platinum pieces"). The match must span the whole token so an
    /// item name that merely contains a coin word doesn't read as currency.
    /// </summary>
    private static bool TryParseCurrency(string token, out int count, out string denom)
    {
        count = 0;
        denom = string.Empty;
        Match m = CurrencyTokenRegex().Match(token);
        if (!m.Success || m.Index != 0 || m.Length != token.Length) return false;
        if (!int.TryParse(m.Groups[1].Value, out count)) return false;
        denom = m.Groups[2].Value switch
        {
            string s when s.StartsWith("runic", StringComparison.Ordinal) => "runic",
            string s when s.StartsWith("platinum", StringComparison.Ordinal) => "platinum",
            string s when s.StartsWith("gold", StringComparison.Ordinal) => "gold",
            string s when s.StartsWith("silver", StringComparison.Ordinal) => "silver",
            _ => "copper",
        };
        return true;
    }

    /// <summary>
    /// Sum a price tail ("26 gold crowns, 1 silver noble, 16 copper farthings"
    /// or "nothing") into copper farthings using the standard ratios.
    /// </summary>
    private static long ParsePriceToCopper(string priceTail)
    {
        if (string.IsNullOrEmpty(priceTail)) return 0;
        long total = 0;
        foreach (Match m in PriceSegmentRegex().Matches(priceTail))
        {
            if (!long.TryParse(m.Groups[1].Value, out long count)) continue;
            string denom = m.Groups[2].Value.TrimEnd('s');
            total += denom switch
            {
                "runic coin" => count * 1_000_000L,
                "platinum piece" => count * 10_000L,
                "gold crown" => count * 100L,
                "silver noble" => count * 10L,
                "copper farthing" => count,
                _ => 0,
            };
        }
        return total;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }

    // ----- regexes -----------------------------------------------------

    [GeneratedRegex(@"^(\d+) (runic coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)$")]
    private static partial Regex CurrencyTokenRegex();

    [GeneratedRegex(@"^Wealth:\s+(\d+)\s+copper farthings?$")]
    private static partial Regex WealthRegex();

    [GeneratedRegex(@"^Encumbrance:\s+(\d+)/(\d+)\s+-\s+(\w+)\s+\[(\d+)%\]$")]
    private static partial Regex EncumbranceRegex();

    [GeneratedRegex(@"^You picked up (\d+) (runic coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)\.?$")]
    private static partial Regex PickedUpCurrencyRegex();

    [GeneratedRegex(@"^You dropped (\d+) (runic coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)\.?$")]
    private static partial Regex DroppedCurrencyRegex();

    [GeneratedRegex(@"^You deposit (\d.+)\.$")]
    private static partial Regex DepositCurrencyRegex();

    [GeneratedRegex(@"^[Yy]ou withdrew (\d.+)\.$")]
    private static partial Regex WithdrawCurrencyRegex();

    [GeneratedRegex(@"(\d+)\s+(runic coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)")]
    private static partial Regex PriceSegmentRegex();
}
