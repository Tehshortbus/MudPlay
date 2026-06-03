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

    // ----- @join nag escalation (wired Phase 6) -----
    /// <summary>Delay after the initial <c>invite</c> before the first <c>@join</c>. Range 1..60, default 5.</summary>
    [ObservableProperty] private int _joinNagInitialDelaySec = 5;
    /// <summary>Cadence for subsequent <c>@join</c> resends. Range 1..60, default 10.</summary>
    [ObservableProperty] private int _joinNagFrequencySec = 10;
    /// <summary>Hard cap on the total nag window. Range 5..600, default 55.</summary>
    [ObservableProperty] private int _joinNagMaxTotalSec = 55;

    // ----- "If leading, wait only" — disconnect grace window in seconds.
    //       Single field; UI uses one NumericUpDown with Increment=10
    //       and free-text entry for non-multiples of 10. Default 90.
    [ObservableProperty] private int _ifLeadingWaitTotalSec = 90;

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
            JoinNagInitialDelaySec   = Math.Clamp(JoinNagInitialDelaySec, 1, 60),
            JoinNagFrequencySec      = Math.Clamp(JoinNagFrequencySec,    1, 60),
            JoinNagMaxTotalSec       = Math.Clamp(JoinNagMaxTotalSec,     5, 600),
            IfLeadingWaitTotalSec    = Math.Clamp(IfLeadingWaitTotalSec,  0, 3600),
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
        JoinNagInitialDelaySec     = dto.JoinNagInitialDelaySec;
        JoinNagFrequencySec        = dto.JoinNagFrequencySec;
        JoinNagMaxTotalSec         = dto.JoinNagMaxTotalSec;
        IfLeadingWaitTotalSec      = dto.IfLeadingWaitTotalSec;

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
        svcs.AutoParty.JoinNagInitialDelay = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagInitialDelaySec, 1, 60));
        svcs.AutoParty.JoinNagFrequency    = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagFrequencySec,    1, 60));
        svcs.AutoParty.JoinNagMaxTotal     = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagMaxTotalSec,     5, 600));
        svcs.Party.DisconnectGraceWindow   = TimeSpan.FromSeconds(Math.Clamp(dto.IfLeadingWaitTotalSec,  0, 3600));
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
    partial void OnJoinNagInitialDelaySecChanged(int value)     => MarkDirty();
    partial void OnJoinNagFrequencySecChanged(int value)        => MarkDirty();
    partial void OnJoinNagMaxTotalSecChanged(int value)         => MarkDirty();
    partial void OnIfLeadingWaitTotalSecChanged(int value)      => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }
}
