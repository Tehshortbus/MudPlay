using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// "Cash" tab — per-currency Collect / Ignore / Discard policy plus the
// auto-deposit threshold + bank-room key. Persists as the "Cash" entry in
// CharacterProfile.Settings; read at runtime by Game.Cash.CashManager.
//
// Stash-room rules ship in a separate editor (their own list-of-rooms shape
// doesn't fit a flat-fields tab).
public sealed partial class CashSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Cash";

    private readonly ProfileService _profile;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "cash";
    public override string Title => "Cash + Items";
    public override bool IsDirty => _dirty;

    // True when a profile is loaded — editor is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    public override Control View => _view ??= new CashSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Cash", "Coin", "Currency", "Items",
        "Copper", "Silver", "Gold", "Platinum", "Runic",
        "Collect", "Ignore", "Discard",
        "Auto-deposit", "Stashing", "Bank", "Keep on hand", "Wealth threshold",
        "Coin count", "Coins exceed",
        "Encumbrance", "Weight", "Light", "Medium", "Heavy",
        "Auto-get", "Get item", "Collection rules",
    };

    // ----- Per-currency policy --------------------------------------

    [ObservableProperty] private CashPolicy _copperPolicy   = CashPolicy.Ignore;
    [ObservableProperty] private CashPolicy _silverPolicy   = CashPolicy.Collect;
    [ObservableProperty] private CashPolicy _goldPolicy     = CashPolicy.Collect;
    [ObservableProperty] private CashPolicy _platinumPolicy = CashPolicy.Collect;
    [ObservableProperty] private CashPolicy _runicPolicy    = CashPolicy.Collect;

    // ----- Auto-deposit ---------------------------------------------

    [ObservableProperty] private long _autoDepositIfWealthExceeds;
    [ObservableProperty] private long _autoDepositIfCoinsExceed;
    [ObservableProperty] private string _bankRoomKey = string.Empty;

    // Dropdown items for the Bank picker — banks from the active game-data set's
    // Shops.json (ShopType==7) plus the user's stash rooms from
    // CharacterProfile.StashRooms.
    public List<BankChoice> BankChoices { get; private set; } = new();

    [ObservableProperty] private BankChoice? _selectedBank;

    // ----- Per-currency keep-on-hand (banking + stashing) ----------
    // The floor kept after offloading coin, honoured by BOTH the
    // auto-deposit reroute and StashRoomManager's `hide N <coin>`.

    [ObservableProperty] private long _keepCopperOnHand;
    [ObservableProperty] private long _keepSilverOnHand;
    [ObservableProperty] private long _keepGoldOnHand;
    [ObservableProperty] private long _keepPlatinumOnHand;
    [ObservableProperty] private long _keepRunicOnHand;

    // ----- Coin encumbrance gate + cascade --------------------------

    [ObservableProperty] private bool _skipCollectIfMakesLight;
    [ObservableProperty] private bool _skipCollectIfMakesMedium;
    [ObservableProperty] private bool _skipCollectIfMakesHeavy;
    [ObservableProperty] private bool _collectAfterCombatFinished;
    [ObservableProperty] private bool _dropSmallerForLarger;

    // ----- Item encumbrance gate (independent of the coin gate) ------

    [ObservableProperty] private bool _skipGetItemIfMakesLight;
    [ObservableProperty] private bool _skipGetItemIfMakesMedium;
    [ObservableProperty] private bool _skipGetItemIfMakesHeavy;

    // Static list of policy choices for the per-currency ComboBoxes. The view
    // binds ItemsSource to this.
    public IReadOnlyList<CashPolicy> PolicyChoices { get; } = new[]
    {
        CashPolicy.Collect, CashPolicy.Ignore, CashPolicy.Discard,
    };

    public CashSectionViewModel() : this(AppServices.Current.Profile) { }

    public CashSectionViewModel(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
        });
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        CashSettings dto = new()
        {
            CopperPolicy   = CopperPolicy,
            SilverPolicy   = SilverPolicy,
            GoldPolicy     = GoldPolicy,
            PlatinumPolicy = PlatinumPolicy,
            RunicPolicy    = RunicPolicy,

            AutoDepositIfWealthExceeds = ClampNonNeg(AutoDepositIfWealthExceeds),
            AutoDepositIfCoinsExceed   = ClampNonNeg(AutoDepositIfCoinsExceed),
            BankRoomKey                = SelectedBank?.Value ?? BankRoomKey ?? string.Empty,

            KeepCopperOnHand   = ClampNonNeg(KeepCopperOnHand),
            KeepSilverOnHand   = ClampNonNeg(KeepSilverOnHand),
            KeepGoldOnHand     = ClampNonNeg(KeepGoldOnHand),
            KeepPlatinumOnHand = ClampNonNeg(KeepPlatinumOnHand),
            KeepRunicOnHand    = ClampNonNeg(KeepRunicOnHand),

            SkipCollectIfMakesLight    = SkipCollectIfMakesLight,
            SkipCollectIfMakesMedium   = SkipCollectIfMakesMedium,
            SkipCollectIfMakesHeavy    = SkipCollectIfMakesHeavy,
            CollectAfterCombatFinished = CollectAfterCombatFinished,
            DropSmallerForLarger       = DropSmallerForLarger,

            SkipGetItemIfMakesLight    = SkipGetItemIfMakesLight,
            SkipGetItemIfMakesMedium   = SkipGetItemIfMakesMedium,
            SkipGetItemIfMakesHeavy    = SkipGetItemIfMakesHeavy,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

        // Re-evaluate auto-deposit trigger immediately so a tighter threshold
        // fires now instead of waiting for the next coin event.
        try { AppServices.Current.Cash.OnSettingsChanged(); }
        catch { /* AppServices may not be initialized in design-time / tests */ }

        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    private static long ClampNonNeg(long value) => Math.Max(0, value);

    private void OnProfileChanged(CharacterProfile _) => ReloadAfterProfileSwap();
    private void OnProfileClosedExternally() => ReloadAfterProfileSwap();

    private void ReloadAfterProfileSwap()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private void LoadFromProfile()
    {
        CashSettings dto = ReadOrDefault();

        CopperPolicy   = dto.CopperPolicy;
        SilverPolicy   = dto.SilverPolicy;
        GoldPolicy     = dto.GoldPolicy;
        PlatinumPolicy = dto.PlatinumPolicy;
        RunicPolicy    = dto.RunicPolicy;

        AutoDepositIfWealthExceeds = dto.AutoDepositIfWealthExceeds;
        AutoDepositIfCoinsExceed   = dto.AutoDepositIfCoinsExceed;
        BankRoomKey                = dto.BankRoomKey ?? string.Empty;

        RebuildBankChoices();
        SelectedBank = BankChoices.FirstOrDefault(c =>
            string.Equals(c.Value, BankRoomKey, StringComparison.OrdinalIgnoreCase));

        KeepCopperOnHand   = dto.KeepCopperOnHand;
        KeepSilverOnHand   = dto.KeepSilverOnHand;
        KeepGoldOnHand     = dto.KeepGoldOnHand;
        KeepPlatinumOnHand = dto.KeepPlatinumOnHand;
        KeepRunicOnHand    = dto.KeepRunicOnHand;

        SkipCollectIfMakesLight    = dto.SkipCollectIfMakesLight;
        SkipCollectIfMakesMedium   = dto.SkipCollectIfMakesMedium;
        SkipCollectIfMakesHeavy    = dto.SkipCollectIfMakesHeavy;
        CollectAfterCombatFinished = dto.CollectAfterCombatFinished;
        DropSmallerForLarger       = dto.DropSmallerForLarger;

        SkipGetItemIfMakesLight    = dto.SkipGetItemIfMakesLight;
        SkipGetItemIfMakesMedium   = dto.SkipGetItemIfMakesMedium;
        SkipGetItemIfMakesHeavy    = dto.SkipGetItemIfMakesHeavy;
    }

    private CashSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new CashSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new CashSettings();
        try
        {
            return JsonSerializer.Deserialize<CashSettings>(json) ?? new CashSettings();
        }
        catch
        {
            // Malformed delta — fall back to defaults rather than throwing.
            return new CashSettings();
        }
    }

    // ----- IsDirty plumbing -----------------------------------------

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnCopperPolicyChanged(CashPolicy value)              => MarkDirty();
    partial void OnSilverPolicyChanged(CashPolicy value)              => MarkDirty();
    partial void OnGoldPolicyChanged(CashPolicy value)                => MarkDirty();
    partial void OnPlatinumPolicyChanged(CashPolicy value)            => MarkDirty();
    partial void OnRunicPolicyChanged(CashPolicy value)               => MarkDirty();
    partial void OnAutoDepositIfWealthExceedsChanged(long value)      => MarkDirty();
    partial void OnAutoDepositIfCoinsExceedChanged(long value)        => MarkDirty();
    partial void OnBankRoomKeyChanged(string value)                   => MarkDirty();
    partial void OnKeepCopperOnHandChanged(long value)                => MarkDirty();
    partial void OnKeepSilverOnHandChanged(long value)                => MarkDirty();
    partial void OnKeepGoldOnHandChanged(long value)                  => MarkDirty();
    partial void OnKeepPlatinumOnHandChanged(long value)              => MarkDirty();
    partial void OnKeepRunicOnHandChanged(long value)                 => MarkDirty();
    // ----- Encumbrance-gate cascade ---------------------------------
    // The three gates are nested by strictness: Light (strictest) ⊃
    // Medium ⊃ Heavy (loosest). Checking a stricter gate subsumes the
    // looser ones — refusing a pickup that would make you Light also
    // refuses ones that would make you Medium / Heavy — so the looser
    // gates are forced checked and disabled. Only the strictest checked
    // gate stays user-editable; unchecking it re-enables the next gate
    // down (which remains checked, now the new strictest).

    // Medium gate editable only while the stricter Light gate is unchecked;
    // otherwise it's locked checked + disabled.
    public bool SkipMediumEnabled => !SkipCollectIfMakesLight;

    // Heavy gate editable only while both stricter gates (Light, Medium) are unchecked.
    public bool SkipHeavyEnabled => !(SkipCollectIfMakesLight || SkipCollectIfMakesMedium);

    partial void OnSkipCollectIfMakesLightChanged(bool value)
    {
        if (value) SkipCollectIfMakesMedium = true;   // cascades Heavy on
        OnPropertyChanged(nameof(SkipMediumEnabled));
        OnPropertyChanged(nameof(SkipHeavyEnabled));
        MarkDirty();
    }

    partial void OnSkipCollectIfMakesMediumChanged(bool value)
    {
        if (value) SkipCollectIfMakesHeavy = true;
        OnPropertyChanged(nameof(SkipHeavyEnabled));
        MarkDirty();
    }

    partial void OnSkipCollectIfMakesHeavyChanged(bool value)          => MarkDirty();
    partial void OnCollectAfterCombatFinishedChanged(bool value)      => MarkDirty();
    partial void OnDropSmallerForLargerChanged(bool value)            => MarkDirty();

    // ----- Item encumbrance-gate cascade ----------------------------
    // Same strictness nesting as the coin gate: Light ⊃ Medium ⊃ Heavy. The
    // hard capacity cap (never exceed MaxWeight) is always on in the engine and
    // has no checkbox — these three only add the tighter bracket ceilings.

    // Item Medium gate editable only while the stricter Light gate is unchecked.
    public bool GetItemMediumEnabled => !SkipGetItemIfMakesLight;

    // Item Heavy gate editable only while both stricter item gates are unchecked.
    public bool GetItemHeavyEnabled => !(SkipGetItemIfMakesLight || SkipGetItemIfMakesMedium);

    partial void OnSkipGetItemIfMakesLightChanged(bool value)
    {
        if (value) SkipGetItemIfMakesMedium = true;   // cascades Heavy on
        OnPropertyChanged(nameof(GetItemMediumEnabled));
        OnPropertyChanged(nameof(GetItemHeavyEnabled));
        MarkDirty();
    }

    partial void OnSkipGetItemIfMakesMediumChanged(bool value)
    {
        if (value) SkipGetItemIfMakesHeavy = true;
        OnPropertyChanged(nameof(GetItemHeavyEnabled));
        MarkDirty();
    }

    partial void OnSkipGetItemIfMakesHeavyChanged(bool value)         => MarkDirty();
    partial void OnSelectedBankChanged(BankChoice? value)
    {
        if (value is not null) BankRoomKey = value.Value;
        MarkDirty();
    }

    // Build the Bank ComboBox items. Two sources, in order:
    // (1) Shops table rows where ShopType == 7 (Bank) — each entry becomes
    //     "(map/room) RoomName - ShopName".
    // (2) Per-character stash rooms — each becomes "(map/room) RoomName - Stash" so
    //     the user can pick a marked stash as the deposit destination (treating it
    //     as an offline bank for personal caches).
    // Returns an empty list when no game-data set is loaded.
    private void RebuildBankChoices()
    {
        List<BankChoice> choices = new();
        try
        {
            AppServices svc = AppServices.Current;
            AppendShopBanks(svc, choices);
            AppendStashRooms(svc, choices);
        }
        catch
        {
            // Design-time / test path with no AppServices initialised.
        }
        BankChoices = choices;
        OnPropertyChanged(nameof(BankChoices));
    }

    private static void AppendShopBanks(AppServices svc, List<BankChoice> choices)
    {
        // Same ShopType == 7 + Assigned-To parse the auto-deposit engine's
        // destination-validity check uses, so the picker offers exactly the
        // rooms a persisted key will validate against.
        foreach (BankShop bank in BankCatalog.Enumerate(svc.GameData))
        {
            string roomName = string.IsNullOrEmpty(bank.RoomName) ? "(unknown)" : bank.RoomName;
            choices.Add(new BankChoice(
                Value:   $"{bank.Map}/{bank.Room}",
                Display: $"({bank.Map}/{bank.Room}) {roomName} - {bank.Name}"));
        }
    }

    private static void AppendStashRooms(AppServices svc, List<BankChoice> choices)
    {
        if (svc.Profile.Current?.StashRooms is not { } stashes) return;
        foreach (RoomRef r in stashes)
        {
            string roomName = svc.RoomGraph.GetRoom(new RoomKey(r.Map, r.Room))?.Name ?? "(unknown)";
            string val = $"{r.Map}/{r.Room}";
            // De-dup: if a shop bank is already in the same room,
            // don't list it again as a stash entry.
            if (choices.Any(c => string.Equals(c.Value, val, StringComparison.OrdinalIgnoreCase))) continue;
            choices.Add(new BankChoice(
                Value:   val,
                Display: $"({val}) {roomName} - Stash"));
        }
    }

}

// One entry in the Bank picker — gold-equivalent location the auto-deposit flow
// will walk to. Value is the canonical "{map}/{room}" key persisted on
// CashSettings.BankRoomKey; Display is the human-readable label rendered in the
// ComboBox.
public sealed record BankChoice(string Value, string Display);
