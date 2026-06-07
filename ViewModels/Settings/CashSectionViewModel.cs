using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Cash" tab — per-currency Collect / Ignore / Discard policy plus the
/// auto-deposit threshold + bank-room key. Persists as the
/// <c>"Cash"</c> entry in <see cref="CharacterProfile.Settings"/>; read
/// at runtime by <see cref="Game.Cash.CashManager"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stash-room rules ship in a separate editor (their own list-of-rooms
/// shape doesn't fit a flat-fields tab). Encumbrance gates, cascade
/// drop-smaller-for-larger, and the walker-driven auto-deposit reroute
/// stay deferred — the stub fields they used to occupy aren't surfaced
/// because clicking them today does nothing.
/// </para>
/// </remarks>
public sealed partial class CashSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Cash";

    private readonly ProfileService _profile;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "cash";
    public override string Title => "Cash";
    public override bool IsDirty => _dirty;

    /// <summary>True when a profile is loaded — editor is hidden otherwise.</summary>
    public bool HasProfile => _profile.Current is not null;

    public override Control View => _view ??= new CashSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Cash", "Coin", "Currency",
        "Copper", "Silver", "Gold", "Platinum", "Runic",
        "Collect", "Ignore", "Discard",
        "Auto-deposit", "Bank", "Minimum cash on hand", "Wealth threshold",
    };

    // ----- Per-currency policy --------------------------------------

    [ObservableProperty] private CashPolicy _copperPolicy   = CashPolicy.Ignore;
    [ObservableProperty] private CashPolicy _silverPolicy   = CashPolicy.Collect;
    [ObservableProperty] private CashPolicy _goldPolicy     = CashPolicy.Collect;
    [ObservableProperty] private CashPolicy _platinumPolicy = CashPolicy.Collect;
    [ObservableProperty] private CashPolicy _runicPolicy    = CashPolicy.Collect;

    // ----- Auto-deposit ---------------------------------------------

    [ObservableProperty] private long _autoDepositIfWealthExceeds;
    [ObservableProperty] private long _minimumCashOnHand;
    [ObservableProperty] private string _bankRoomKey = string.Empty;

    // ----- Per-currency keep-on-hand for stash rooms ---------------

    [ObservableProperty] private long _keepCopperOnHand;
    [ObservableProperty] private long _keepSilverOnHand;
    [ObservableProperty] private long _keepGoldOnHand;
    [ObservableProperty] private long _keepPlatinumOnHand;
    [ObservableProperty] private long _keepRunicOnHand;

    // ----- Encumbrance + cascade (persisted; engines deferred) -----

    [ObservableProperty] private bool _skipCollectIfMakesLight;
    [ObservableProperty] private bool _skipCollectIfMakesMedium;
    [ObservableProperty] private bool _skipCollectIfMakesHeavy;
    [ObservableProperty] private bool _collectAfterCombatFinished;
    [ObservableProperty] private bool _dropSmallerForLarger;

    /// <summary>Static list of policy choices for the per-currency
    /// ComboBoxes. The view binds ItemsSource to this.</summary>
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
            MinimumCashOnHand          = ClampNonNeg(MinimumCashOnHand),
            BankRoomKey                = BankRoomKey ?? string.Empty,

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
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

        // Re-evaluate auto-deposit trigger immediately so a tighter
        // threshold fires now instead of waiting for the next coin
        // event. Mirrors MudProxy's OnSettingsChanged reapply pattern.
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
        MinimumCashOnHand          = dto.MinimumCashOnHand;
        BankRoomKey                = dto.BankRoomKey ?? string.Empty;

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
    partial void OnMinimumCashOnHandChanged(long value)               => MarkDirty();
    partial void OnBankRoomKeyChanged(string value)                   => MarkDirty();
    partial void OnKeepCopperOnHandChanged(long value)                => MarkDirty();
    partial void OnKeepSilverOnHandChanged(long value)                => MarkDirty();
    partial void OnKeepGoldOnHandChanged(long value)                  => MarkDirty();
    partial void OnKeepPlatinumOnHandChanged(long value)              => MarkDirty();
    partial void OnKeepRunicOnHandChanged(long value)                 => MarkDirty();
    partial void OnSkipCollectIfMakesLightChanged(bool value)         => MarkDirty();
    partial void OnSkipCollectIfMakesMediumChanged(bool value)        => MarkDirty();
    partial void OnSkipCollectIfMakesHeavyChanged(bool value)         => MarkDirty();
    partial void OnCollectAfterCombatFinishedChanged(bool value)      => MarkDirty();
    partial void OnDropSmallerForLargerChanged(bool value)            => MarkDirty();
}
