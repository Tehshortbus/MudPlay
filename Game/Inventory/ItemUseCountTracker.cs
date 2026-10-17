using System;
using System.Collections.Generic;
using MudPlay.Game.Map;          // BossTimerMath
using MudPlay.Models.Profile;
using MudPlay.Services;          // GameDataCache, ProfileService, LogService, BossCleanupConfig
using MudPlay.Terminal;          // LineExtractor

namespace MudPlay.Game.Inventory;

// Stock-realm charge tracking for limited-use items. Stock prints no "Uses remaining"
// on look (that's a Paradigm feature — handled by ItemChargeTracker), so the client
// counts SUCCESSFUL uses and derives remaining = max − used, with max from
// ItemChargeMeta (UseCount). Counts persist on the character profile
// (CharacterProfile.ItemUseCounts, keyed by item number):
//   • a RECHARGEABLE item (Retain After Uses) restocks to max at the BBS cleanup time,
//     so a used-count recorded before the last cleanup reads as 0 (reusing the boss-
//     timer NextCleanup math);
//   • a FINITE item's count is permanent.
//
// Only SUCCESSFUL uses count. A `use` that succeeds prints the item's use-spell caster
// message (the spell it casts via CastsSp); a `use` that fails prints the between-round
// "bonk" instead and burns no charge. So we don't count the outbound send — we arm on
// it and count only when the item's use-spell message lands within a round window. A
// bonked / blocked use never produces that line, so its pending simply lapses. (Items
// whose on-use message can't be resolved from game data fall back to counting on send —
// best-effort, since there's no line to confirm against.)
// Gated to stock so it never double-counts against the Paradigm look tracker.
public sealed class ItemUseCountTracker
{
    // A successful `use` casts on the round, so the message can land a beat later; a
    // bonked use gets nothing. Generous enough to cover a round, short enough that a
    // later unrelated cast of the same spell isn't mistaken for this use's confirmation.
    private const int ConfirmWindowMs = 5000;

    private readonly GameDataCache _gameData;
    private readonly Func<IReadOnlyList<string>> _held;   // carried pack + worn/wielded gear + key-ring
    private readonly Func<string, int> _itemNumberOf;
    private readonly Func<bool> _onStock;
    private readonly Func<BossCleanupConfig?> _cleanupConfig;
    private readonly ProfileService _profile;
    private readonly Func<int, Func<string, bool>?> _useConfirmLine;
    private readonly Action<int, Action> _schedule;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private LineExtractor? _lines;

    private int _pendingNumber;                  // item whose use we sent, awaiting its cast message
    private Func<string, bool>? _pendingConfirm;
    private int _pendingGen;

    // Fired after a use is counted (or a profile reload) so Character Info refreshes.
    public event Action? Changed;

    public ItemUseCountTracker(
        GameDataCache gameData,
        Func<IReadOnlyList<string>> heldItems,
        Func<string, int> itemNumberOf,
        Func<bool> onStock,
        Func<BossCleanupConfig?> cleanupConfig,
        ProfileService profile,
        Func<int, Func<string, bool>?> useConfirmLine,
        Action<int, Action> schedule,
        Func<DateTimeOffset>? now = null,
        LogService? log = null)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _held = heldItems ?? throw new ArgumentNullException(nameof(heldItems));
        _itemNumberOf = itemNumberOf ?? throw new ArgumentNullException(nameof(itemNumberOf));
        _onStock = onStock ?? throw new ArgumentNullException(nameof(onStock));
        _cleanupConfig = cleanupConfig ?? throw new ArgumentNullException(nameof(cleanupConfig));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _useConfirmLine = useConfirmLine ?? throw new ArgumentNullException(nameof(useConfirmLine));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Remaining charges for a held limited-use item, or null when it isn't a finite
    // limited-use item (infinite / non-charged). Recharge-adjusted.
    public int? RemainingFor(int itemNumber)
        => ItemChargeMeta.Read(_gameData, itemNumber) is { IsLimitedUse: true } meta
            ? Math.Max(0, meta.MaxUses - EffectiveUsed(itemNumber, meta))
            : null;

    // Outbound `use <item>` / `use <item> <target>` for a held limited-use item (carried
    // or worn) → ARM a pending confirmation. Stock only — Paradigm reads the look reply.
    public void ObserveOutbound(byte[] data)
    {
        if (!_onStock() || data is null || data.Length == 0) return;
        string text = System.Text.Encoding.Latin1.GetString(data);
        foreach (string raw in text.Split('\r', '\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("use ", StringComparison.OrdinalIgnoreCase)) continue;
            if (ResolveHeld(line[4..].Trim()) is not { } name) continue;
            int number = _itemNumberOf(name);
            if (number <= 0 || ItemChargeMeta.Read(_gameData, number) is not { IsLimitedUse: true }) continue;

            if (_useConfirmLine(number) is { } confirm)
                ArmPending(number, confirm);
            else
                RecordUse(number);   // no resolvable use-spell line — best-effort count on send
        }
    }

    private void ArmPending(int number, Func<string, bool> confirm)
    {
        _pendingNumber = number;
        _pendingConfirm = confirm;
        int gen = ++_pendingGen;
        // A bonked / blocked use prints no cast message, so let the pending lapse.
        _schedule(ConfirmWindowMs, () => { if (gen == _pendingGen) ClearPending(); });
    }

    private void ClearPending()
    {
        _pendingNumber = 0;
        _pendingConfirm = null;
        _pendingGen++;
    }

    private void OnLine(LineExtractor.EmittedLine emitted) => HandleLine(emitted.Text);

    // The armed
    // item's use-spell caster message confirms the use actually fired → count it.
    internal void HandleLine(string text)
    {
        if (_pendingConfirm is not { } confirm || _pendingNumber <= 0) return;
        if (!confirm(text)) return;
        int number = _pendingNumber;
        ClearPending();
        RecordUse(number);
    }

    private void RecordUse(int number)
    {
        if (ItemChargeMeta.Read(_gameData, number) is not { IsLimitedUse: true } meta) return;
        if (_profile.Current is not { } prof) return;

        // A FINITE item poofs when its top copy empties; in a stack the next copy is then
        // on top, assumed full (a fresh drop = max charges). Not guaranteed — a partial
        // copy picked off the ground breaks it — but it's the best a realm with no charges
        // line can do, so wrap to 0 (a fresh full copy) rather than cap at spent. A
        // rechargeable item is retained empty and refills at cleanup, so it caps instead.
        int next = EffectiveUsed(number, meta) + 1;
        int used = !meta.Recharges && next >= meta.MaxUses ? 0 : Math.Min(meta.MaxUses, next);
        prof.ItemUseCounts ??= new Dictionary<int, ItemUseRecord>();
        prof.ItemUseCounts[number] = new ItemUseRecord(used, _now());
        _profile.Save();
        _log?.Info("Items", $"use counted: item #{number} → {used}/{meta.MaxUses} used");
        Changed?.Invoke();
    }

    // Used count as of now: a rechargeable item whose record predates the last cleanup
    // has restocked (reads 0); otherwise the stored count. Finite items ignore cleanup.
    private int EffectiveUsed(int number, ItemChargeMeta meta)
    {
        if (_profile.Current?.ItemUseCounts is not { } map
            || !map.TryGetValue(number, out ItemUseRecord? rec))
            return 0;
        if (meta.Recharges && _cleanupConfig() is { } cfg
            && _now() >= BossTimerMath.NextCleanup(rec.UpdatedUtc, cfg.TimeOfDay, cfg.Tz))
            return 0;
        return rec.Used;
    }

    // The held item (carried or worn) a `use` arg refers to: a held name that is a prefix
    // of the arg (item-then-target), else one whose name contains the arg's first word.
    // First match wins — the loose resolution the game does for a partial. Worn gear is
    // included because cast-on-use rechargeables (a wielded mace) are used while worn.
    private string? ResolveHeld(string arg)
    {
        if (arg.Length < 2) return null;
        string a = arg.ToLowerInvariant();
        foreach (string c in _held())
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            string cl = Singular(c).ToLowerInvariant();
            if (a == cl || a.StartsWith(cl + " ", StringComparison.Ordinal)) return Singular(c);
        }
        string first = a.Split(' ')[0];
        if (first.Length >= 3)
            foreach (string c in _held())
                if (!string.IsNullOrWhiteSpace(c) && Singular(c).ToLowerInvariant().Contains(first)) return Singular(c);
        return null;
    }

    // Strip a stacked carry entry's leading count ("2 gnarled wand" → "gnarled wand") so
    // it resolves to its item number. The name stays singular under the count.
    private static string Singular(string name) => CountedCommand.SplitLeadingCount(name).Name;
}
