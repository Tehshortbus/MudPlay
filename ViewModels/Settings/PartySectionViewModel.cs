using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Party" tab — bespoke layout. PR 6.9 wires the three knobs that map
/// onto live Phase 6 services (par poll cadence, auto-invite reconnecting
/// member, reset statistics on loop start), persists per character as
/// the <c>"Party"</c> entry in <see cref="CharacterProfile.Settings"/>.
/// The spell-picker / bless-slot / heal-threshold controls stay
/// disabled-stubs because their consumer (<c>CastingDirector</c> in
/// Phase 12) doesn't exist yet — locking the schema before that lands
/// would force an awkward migration.
/// </summary>
public sealed partial class PartySectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Party";

    private readonly ProfileService _profile;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "party";
    public override string Title => "Party";
    public override bool IsDirty => _dirty;

    /// <summary>True when a profile is loaded — editor is hidden otherwise.</summary>
    public bool HasProfile => _profile.Current is not null;

    public string PhaseTag => "Phase 6 (PartyManager) + Phase 12 PR 12.D (CastingDirector — party)";

    public string Description =>
        "Party-coordination knobs plus the party-cast spell picks. Heal rows put the spell and threshold side by " +
        "side; bless takes 10 slots without per-slot timeouts (the bless engine handles re-cast cadence on its " +
        "own). Cure / buff / heal priority is configured once on the Spells tab and applies to both self and party.";

    public override Control View => _view ??= new PartySectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Party", "Rank", "Front", "Mid", "Back",
        "Minor heal", "Major heal", "Request healing",
        "Bless", "Auto-share cash", "Help leader bash doors",
        "Auto-invite", "Auto-Exp-Reset", "par frequency",
        "Wait for members", "Max monsters", "Max monster experience",
        "Attack last in party", "Attack in reverse order",
        "Attack what other members attack",
        "Ignore party when following", "Auto-collect when following",
        "Say emote", "Go @panic when injured",
    };

    // ----- Wired knobs (PR 6.9) -----

    /// <summary>par poll cadence in seconds; range 1..60. Default 5.</summary>
    [ObservableProperty] private int _parPollFrequencySec = 5;

    [ObservableProperty] private bool _autoInviteReconnecting = true;

    [ObservableProperty] private bool _resetStatisticsOnLoopStart = true;

    /// <summary>Rank radio bound as three mutually-exclusive booleans (matches the existing AXAML RadioButton pattern).</summary>
    [ObservableProperty] private bool _rankFront;
    [ObservableProperty] private bool _rankMid = true;
    [ObservableProperty] private bool _rankBack;

    public PartySectionViewModel() : this(AppServices.Current.Profile) { }

    public PartySectionViewModel(ProfileService profile)
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

        PartySettings dto = new()
        {
            ParPollFrequencySec      = Math.Clamp(ParPollFrequencySec, 1, 60),
            AutoInviteReconnecting   = AutoInviteReconnecting,
            ResetStatisticsOnLoopStart = ResetStatisticsOnLoopStart,
            Rank = RankFront ? PartyRank.Front
                 : RankBack  ? PartyRank.Back
                 : PartyRank.Mid,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

        // Push to live services so the user's edit takes effect without
        // requiring a profile-reload.
        ApplyToServices(dto);

        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

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
        PartySettings dto = ReadOrDefault();
        ParPollFrequencySec        = dto.ParPollFrequencySec;
        AutoInviteReconnecting     = dto.AutoInviteReconnecting;
        ResetStatisticsOnLoopStart = dto.ResetStatisticsOnLoopStart;
        RankFront = dto.Rank == PartyRank.Front;
        RankMid   = dto.Rank == PartyRank.Mid;
        RankBack  = dto.Rank == PartyRank.Back;

        // Mirror loaded settings into the live services so they reflect
        // the profile from first connection, not just after the user
        // visits this tab and clicks Apply.
        ApplyToServices(dto);
    }

    private PartySettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new PartySettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new PartySettings();
        try
        {
            return JsonSerializer.Deserialize<PartySettings>(json) ?? new PartySettings();
        }
        catch
        {
            // Malformed delta — fall back to defaults rather than throwing.
            return new PartySettings();
        }
    }

    private static void ApplyToServices(PartySettings dto)
    {
        AppServices svcs = AppServices.Current;
        svcs.PartyPoller.SetParCadence(TimeSpan.FromSeconds(Math.Clamp(dto.ParPollFrequencySec, 1, 60)));
        svcs.Party.AutoInviteEnabled = dto.AutoInviteReconnecting;
        svcs.Party.LocalRankPreference = dto.Rank;
        svcs.PartyBroadcaster.AutoExpResetEnabled = dto.ResetStatisticsOnLoopStart;
    }

    // ----- IsDirty plumbing -----

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnParPollFrequencySecChanged(int value)        => MarkDirty();
    partial void OnAutoInviteReconnectingChanged(bool value)    => MarkDirty();
    partial void OnResetStatisticsOnLoopStartChanged(bool value)=> MarkDirty();
    partial void OnRankFrontChanged(bool value)                 => MarkDirty();
    partial void OnRankMidChanged(bool value)                   => MarkDirty();
    partial void OnRankBackChanged(bool value)                  => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }
}
