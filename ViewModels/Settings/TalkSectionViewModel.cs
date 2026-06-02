using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Talk" tab — engine-level policy for the Phase 6
/// <see cref="Game.Remote.RemoteCommandManager"/>. Mirrors the
/// PartySectionViewModel pattern: Apply / Discard against an in-memory
/// edit set, persist as the <c>"Talk"</c> entry in
/// <see cref="CharacterProfile.Settings"/>, and push the resulting DTO
/// into the live engine via <see cref="ApplyToServices"/> so changes
/// take effect without a profile reload.
/// </summary>
/// <remarks>
/// AFK Mode rows (auto-AFK timer, AFK response message, etc.) belong on
/// this tab visually because they're chat-policy, but their consumer
/// lands in Phase 11. They render as disabled-stubs alongside the wired
/// rows below until that phase opens.
/// </remarks>
public sealed partial class TalkSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Talk";

    private readonly ProfileService _profile;
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "talk";
    public override string Title => "Talk";
    public override bool IsDirty => _dirty;

    /// <summary>True when a profile is loaded — editor is hidden otherwise.</summary>
    public bool HasProfile => _profile.Current is not null;

    public string PhaseTag => "Phase 6 (RemoteCommandManager) + Phase 11 (AFK Mode)";

    public string Description =>
        "Engine-level policy for inbound @-commands and (Phase 11) AFK Mode. " +
        "Per-channel disable rows below cover only the three channels the engine ever listens on — Gossip / Auction " +
        "/ Broadcast / Yell are hard-excluded engine-wide and don't need a toggle. " +
        "Per-player permissions live on the Game Data → Players tab; this tab is the master policy layer above them.";

    public override Control View => _view ??= new TalkSectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Talk", "Remote", "@-command", "Disallow", "AFK",
        "telepaths", "gangpaths", "say", "local",
        "failure message", "party commands", "kill switch", "greet",
    };

    // ----- Wired knobs (Phase 6) -----

    [ObservableProperty] private bool _disallowAllRemoteCommands;

    [ObservableProperty] private bool _disallowPartyCommandsFromLeader;

    [ObservableProperty] private bool _disallowRemoteFromTelepaths;

    [ObservableProperty] private bool _disallowRemoteFromGangpaths;

    [ObservableProperty] private bool _disallowRemoteFromLocal;

    [ObservableProperty] private bool _warnOnInvalidRemoteCommand = true;

    [ObservableProperty] private string _remoteCommandFailureMessage = "{command invalid or not allowed}";

    public TalkSectionViewModel() : this(AppServices.Current.Profile) { }

    public TalkSectionViewModel(ProfileService profile)
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

        TalkSettings dto = new()
        {
            DisallowAllRemoteCommands        = DisallowAllRemoteCommands,
            DisallowPartyCommandsFromLeader  = DisallowPartyCommandsFromLeader,
            DisallowRemoteFromTelepaths      = DisallowRemoteFromTelepaths,
            DisallowRemoteFromGangpaths      = DisallowRemoteFromGangpaths,
            DisallowRemoteFromLocal          = DisallowRemoteFromLocal,
            WarnOnInvalidRemoteCommand       = WarnOnInvalidRemoteCommand,
            RemoteCommandFailureMessage      = RemoteCommandFailureMessage ?? string.Empty,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

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
        TalkSettings dto = ReadOrDefault();
        DisallowAllRemoteCommands       = dto.DisallowAllRemoteCommands;
        DisallowPartyCommandsFromLeader = dto.DisallowPartyCommandsFromLeader;
        DisallowRemoteFromTelepaths     = dto.DisallowRemoteFromTelepaths;
        DisallowRemoteFromGangpaths     = dto.DisallowRemoteFromGangpaths;
        DisallowRemoteFromLocal         = dto.DisallowRemoteFromLocal;
        WarnOnInvalidRemoteCommand      = dto.WarnOnInvalidRemoteCommand;
        RemoteCommandFailureMessage     = dto.RemoteCommandFailureMessage;

        // Mirror loaded settings into the live engine so the user's
        // policy applies from first connection, not just after the
        // Settings window is visited.
        ApplyToServices(dto);
    }

    private TalkSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new TalkSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new TalkSettings();
        try
        {
            return JsonSerializer.Deserialize<TalkSettings>(json) ?? new TalkSettings();
        }
        catch
        {
            return new TalkSettings();
        }
    }

    private static void ApplyToServices(TalkSettings dto)
    {
        Game.Remote.RemoteCommandManager engine = AppServices.Current.RemoteCommands;
        engine.MasterDisable           = dto.DisallowAllRemoteCommands;
        engine.DisablePartyWhitelist   = dto.DisallowPartyCommandsFromLeader;
        engine.DisableTelepathChannel  = dto.DisallowRemoteFromTelepaths;
        engine.DisableGangpathChannel  = dto.DisallowRemoteFromGangpaths;
        engine.DisableLocalChannel     = dto.DisallowRemoteFromLocal;
        engine.WarnOnDenial            = dto.WarnOnInvalidRemoteCommand;
        engine.FailureMessage          = dto.RemoteCommandFailureMessage ?? string.Empty;
    }

    // ----- IsDirty plumbing -----

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnDisallowAllRemoteCommandsChanged(bool value)       => MarkDirty();
    partial void OnDisallowPartyCommandsFromLeaderChanged(bool value) => MarkDirty();
    partial void OnDisallowRemoteFromTelepathsChanged(bool value)     => MarkDirty();
    partial void OnDisallowRemoteFromGangpathsChanged(bool value)     => MarkDirty();
    partial void OnDisallowRemoteFromLocalChanged(bool value)         => MarkDirty();
    partial void OnWarnOnInvalidRemoteCommandChanged(bool value)      => MarkDirty();
    partial void OnRemoteCommandFailureMessageChanged(string value)   => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }
}
