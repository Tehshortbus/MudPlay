using System.Collections.Generic;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.Settings;

namespace MudPlay.ViewModels.Settings;

// "Party" tab — bespoke layout. Knobs that map onto live services (what sends
// par, auto-invite reconnecting member, reset statistics on loop start); the
// party-heal pickers + thresholds + AOE-member count feed CastingDirector's
// party-cast path. Party buffs, and when each may cast, live in the Buff Watchdog
// (CharacterProfile.PartyBuffs), not here. Persists per character as the "Party"
// entry in CharacterProfile.Settings.
public sealed partial class PartySectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Party";

    private readonly ProfileService _profile;
    private readonly Game.Spells.SpellbookState _spellbook;
    private bool _suppressDirty;
    private bool _dirty;

    public override string Id => "party";
    public override string Title => "Party";
    public override bool IsDirty => _dirty;

    // True when a profile is loaded — editor is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    // Known-spell suggestions for the party heal / bless typeahead boxes — the
    // current class's learnable list (level gate ignored), ordered by name +
    // distinct by cast-code, from SpellbookState.AvailablePicks. Each box commits
    // the 4-letter SpellPick.Short cast-code. Refreshes when the spellbook
    // rebuilds (class swap / reroll).
    public IReadOnlyList<Game.Spells.SpellPick> SpellSuggestions => _spellbook.AvailablePicks;

    protected override Control BuildView() => new PartySectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Party", "Rank", "Front", "Mid", "Back",
        "Party heal", "Minor heal", "Major heal", "Single-target", "Party AOE",
        "Use AOE", "Request healing", "Request healing channel", "@heal",
        "Help leader open doors",
        "Auto-invite", "Auto-Exp-Reset", "par frequency", "par poll", "Send par",
        "after each combat round", "combat I only witness", "unknown damage",
        "Wait for members", "Max monsters",
        "@panic", "panic", "Use @panic while leading", "Ignore @panics",
    };

    // ----- Wired knobs -----

    // What sends par — any mix of the three boxes, or none.
    [ObservableProperty] private bool _parPollOnTimer = true;
    // The timed par's cadence in seconds; range 1..60. Default 5.
    [ObservableProperty] private int _parPollFrequencySec = 5;
    [ObservableProperty] private bool _parPollAfterCombatRound;
    // Sub-option of the box above: rounds we only witnessed count too.
    [ObservableProperty] private bool _parPollIncludeWitnessedRounds;
    [ObservableProperty] private bool _parPollOnUnknownDamage;

    [ObservableProperty] private bool _autoInviteReconnecting = true;

    [ObservableProperty] private bool _resetStatisticsOnLoopStart = true;

    // Rank radio bound as three mutually-exclusive booleans (matches the
    // existing AXAML RadioButton pattern).
    [ObservableProperty] private bool _rankFront;
    [ObservableProperty] private bool _rankMid = true;
    [ObservableProperty] private bool _rankBack;

    // ----- @join nag escalation -----
    // Delay after the initial invite before the first @join. Range 1..60.
    [ObservableProperty] private int _joinNagInitialDelaySec = 5;
    // Cadence for subsequent @join resends. Range 1..60.
    [ObservableProperty] private int _joinNagFrequencySec = 10;
    // Hard cap on the total nag window. Range 5..600.
    [ObservableProperty] private int _joinNagMaxTotalSec = 55;
    // Master enable for the @join follow-up nag. Default on.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NagInitialDelayEditable))]
    private bool _sendJoinToInvited = true;
    // Master enable for the on-join @health round-trip nag. Default on.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NagInitialDelayEditable))]
    private bool _sendHealthToMembers = true;
    // Master enable for the once-a-day @level + @version party stats probe. Default on.
    [ObservableProperty] private bool _probeStatsOnPartyJoin = true;

    // The shared initial-delay spinner (in Options, next to the @join toggle)
    // edits the value both nag flows use, so it's editable whenever either nag
    // is enabled.
    public bool NagInitialDelayEditable => SendJoinToInvited || SendHealthToMembers;

    // ----- "If leading, wait only" — disconnect grace window in seconds.
    //       Single field; UI uses one NumericUpDown with Increment=10
    //       and free-text entry for non-multiples of 10. Default 90.
    [ObservableProperty] private int _ifLeadingWaitTotalSec = 90;

    // ----- "Return distance" — leader-side recovery reach in BFS room-hops.
    //       The farthest we'll walk to re-collect a returning member before
    //       declining via @forget. 1..500; default 30.
    [ObservableProperty] private int _returnDistanceRooms = 30;

    // ----- "If leading, accept @comeback for up to" (minutes). 0..60; default 2.
    [ObservableProperty] private int _acceptComebackMinutes = 2;

    // ----- Party-cast heal pickers (consumed by CastingDirector) -----
    // Each Minor / Major slot owns a single-target spell AND an AOE / party
    // spell sharing one threshold; CastingDirector picks single vs AOE at
    // cast time based on how many members are below it (AoeMinMembers).
    [ObservableProperty] private string? _minorPartyHealSpell;
    [ObservableProperty] private string? _minorPartyHealAoeSpell;
    [ObservableProperty] private string? _majorPartyHealSpell;
    [ObservableProperty] private string? _majorPartyHealAoeSpell;
    [ObservableProperty] private int _minorHealMemberThresholdPercent = 70;
    [ObservableProperty] private int _majorHealMemberThresholdPercent = 40;
    [ObservableProperty] private int _aoeMinMembers = 2;

    // The channel a follower's @heal goes out on.
    [ObservableProperty] private HealRequestChannel _requestHealChannel = HealRequestChannel.Say;
    public IReadOnlyList<HealRequestChannel> RequestHealChannels { get; } = Enum.GetValues<HealRequestChannel>();

    // ----- Capacity (consumed by CombatManager) ----------------------
    // Party-scoped max-monsters cap; overrides the Combat-tab upper bound while
    // in an active party. 1..20.
    [ObservableProperty] private int _maxMonstersWhenPartying = 20;

    // ----- Vitals gate (consumed by PartyVitalsWatcher) --------------
    // Hold the party action loop while any other member's HP% is below this
    // value. 0 disables. 0..100.
    [ObservableProperty] private int _waitIfMemberBelowPercent;

    // ----- Leader behaviour (consumed by PartyEssentialHandlers) -----
    // When leading, drop incoming @wait broadcasts so the leader's automation
    // keeps running.
    [ObservableProperty] private bool _ignoreWaitWhenLeading;

    // Pitch in when the party leader fails to bash a door we can see — bashes /
    // picks the same door per the Other-tab preference. Consumed by
    // LeaderDoorAssistManager.
    [ObservableProperty] private bool _helpLeaderOpenDoors;

    // ----- @panic (MegaMUD parity) -----------------------------------
    // Leader-side send: broadcast "@panic" on say when our emergency-hangup floor
    // is crossed while leading. Read by HealthManager. Default off.
    [ObservableProperty] private bool _usePanicWhileLeading;
    // Receive-side: ignore partymates' "@panic" instead of bailing with them.
    // Read by PanicResponder. Default off (we bail by default, MegaMUD parity).
    [ObservableProperty] private bool _ignorePanics;

    // "Include in combat profile" for the party healing group —
    // staged in the Settings window's combat-profile session with the other tabs'.
    // Null when the tab is built without a session.
    private readonly CombatProfileStagingSession? _session;
    public CombatProfileGroupToggle? PartyHealingInProfile { get; }
    public CombatProfileChipBar? ChipBar { get; }

    // The border / label for the groups above follow the staged active profile, the
    // same as the Combat / Health / Spells tabs.
    public string ActiveProfileLabel => _session is null ? string.Empty
        : $"Combat profile: {(string.IsNullOrWhiteSpace(_session.Active.Name) ? $"Profile {_session.ActiveIndex + 1}" : _session.Active.Name.Trim())}";
    public Avalonia.Media.IBrush ActiveProfileAccentBrush =>
        CombatProfilePalette.SolidBrush((_session?.ActiveIndex ?? 0) + 1);
    public Avalonia.Media.IBrush ActiveProfileAccentSoftBrush =>
        CombatProfilePalette.SoftBrush((_session?.ActiveIndex ?? 0) + 1);

    public PartySectionViewModel(CombatProfileStagingSession? session = null)
        : this(AppServices.Current.Profile, session) { }

    public PartySectionViewModel(ProfileService profile, CombatProfileStagingSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _session = session;
        _spellbook = AppServices.Current.Spellbook;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _spellbook.Changed += OnSpellbookChanged;
        if (session is not null)
        {
            PartyHealingInProfile = new CombatProfileGroupToggle(session, CombatProfileGroup.PartyHealing, MarkDirty);
            ChipBar = new CombatProfileChipBar(session);
            session.Committing += OnSessionCommitting;
            session.Committed += OnSessionCommitted;
            session.CaptureRequested += CapturePartyBoxesToActive;
            session.LoadRequested += LoadPartyBoxesFromActive;
            session.ChipsChanged += OnChipsChanged;
        }
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _spellbook.Changed -= OnSpellbookChanged;
            PartyHealingInProfile?.Dispose();
            ChipBar?.Dispose();
            if (_session is not null)
            {
                _session.Committing -= OnSessionCommitting;
                _session.Committed -= OnSessionCommitted;
                _session.CaptureRequested -= CapturePartyBoxesToActive;
                _session.LoadRequested -= LoadPartyBoxesFromActive;
                _session.ChipsChanged -= OnChipsChanged;
            }
        });
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    private void OnSpellbookChanged() => OnPropertyChanged(nameof(SpellSuggestions));

    public override void Apply()
    {
        // With the window's combat-profile session, save through it: it writes this
        // tab first (OnSessionCommitting), folds the party healing boxes into
        // the active profile, and reloads the tab afterwards.
        if (_session is not null)
        {
            _session.MarkDirty();
            _session.CommitIfDirty();
            return;
        }
        Write();
        ClearDirty();
    }

    // The combat-profile commit folds Settings["Party"] into the profile the tab was
    // showing, so this tab's edits must land there before it runs — the Health tab's
    // Apply can trigger the commit before this tab's own Apply comes round.
    private void OnSessionCommitting()
    {
        if (IsDirty) Write();
    }

    // The commit made the active profile's party healing live; reload so the
    // tab reads back exactly what was saved.
    private void OnSessionCommitted()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    // Chip switch: fold this tab's party healing boxes into the outgoing
    // profile (CaptureRequested), then load the incoming one's (LoadRequested).
    private void CapturePartyBoxesToActive() => _session?.Active.Party.CaptureFrom(BuildDto());

    private void LoadPartyBoxesFromActive()
    {
        if (_session is null) return;
        CombatProfileParty p = _session.Active.Party;
        _suppressDirty = true;
        MinorPartyHealSpell    = p.MinorPartyHealSpell;
        MinorPartyHealAoeSpell = p.MinorPartyHealAoeSpell;
        MajorPartyHealSpell    = p.MajorPartyHealSpell;
        MajorPartyHealAoeSpell = p.MajorPartyHealAoeSpell;
        MinorHealMemberThresholdPercent = p.MinorHealMemberThresholdPercent;
        MajorHealMemberThresholdPercent = p.MajorHealMemberThresholdPercent;
        AoeMinMembers          = p.AoeMinMembers;
        _suppressDirty = false;
    }

    private void OnChipsChanged()
    {
        OnPropertyChanged(nameof(ActiveProfileLabel));
        OnPropertyChanged(nameof(ActiveProfileAccentBrush));
        OnPropertyChanged(nameof(ActiveProfileAccentSoftBrush));
    }

    // The Party section from the current boxes.
    private PartySettings BuildDto() => new()
    {
        ParPollOnTimer           = ParPollOnTimer,
        ParPollFrequencySec      = Math.Clamp(ParPollFrequencySec, 1, 60),
        ParPollAfterCombatRound  = ParPollAfterCombatRound,
        ParPollIncludeWitnessedRounds = ParPollIncludeWitnessedRounds,
        ParPollOnUnknownDamage   = ParPollOnUnknownDamage,
        AutoInviteReconnecting   = AutoInviteReconnecting,
        ResetStatisticsOnLoopStart = ResetStatisticsOnLoopStart,
        Rank = RankFront ? PartyRank.Front
             : RankBack  ? PartyRank.Back
             : PartyRank.Mid,
        JoinNagInitialDelaySec   = Math.Clamp(JoinNagInitialDelaySec, 1, 60),
        JoinNagFrequencySec      = Math.Clamp(JoinNagFrequencySec,    1, 60),
        JoinNagMaxTotalSec       = Math.Clamp(JoinNagMaxTotalSec,     5, 600),
        SendJoinToInvited        = SendJoinToInvited,
        SendHealthToMembers      = SendHealthToMembers,
        ProbeStatsOnPartyJoin    = ProbeStatsOnPartyJoin,
        IfLeadingWaitTotalSec    = Math.Clamp(IfLeadingWaitTotalSec,  0, 3600),
        ReturnDistanceRooms      = Math.Clamp(ReturnDistanceRooms,    1, 500),
        AcceptComebackMinutes    = Math.Clamp(AcceptComebackMinutes,  0, 60),

        MinorPartyHealSpell    = NullIfBlank(MinorPartyHealSpell),
        MinorPartyHealAoeSpell = NullIfBlank(MinorPartyHealAoeSpell),
        MajorPartyHealSpell    = NullIfBlank(MajorPartyHealSpell),
        MajorPartyHealAoeSpell = NullIfBlank(MajorPartyHealAoeSpell),
        MinorHealMemberThresholdPercent = Math.Clamp(MinorHealMemberThresholdPercent, 0, 100),
        MajorHealMemberThresholdPercent = Math.Clamp(MajorHealMemberThresholdPercent, 0, 100),
        AoeMinMembers          = Math.Clamp(AoeMinMembers, 2, 6),
        RequestHealChannel     = RequestHealChannel,
        MaxMonstersWhenPartying = Math.Clamp(MaxMonstersWhenPartying, 1, 20),
        WaitIfMemberBelowPercent = Math.Clamp(WaitIfMemberBelowPercent, 0, 100),
        IgnoreWaitWhenLeading  = IgnoreWaitWhenLeading,
        HelpLeaderOpenDoors    = HelpLeaderOpenDoors,
        UsePanicWhileLeading   = UsePanicWhileLeading,
        IgnorePanics           = IgnorePanics,
    };

    private void Write()
    {
        if (_profile.Current is not { } profile) return;

        PartySettings dto = BuildDto();

        profile.Settings ??= new();
        profile.Settings[TabKey] = SettingsSection.ToElement(dto);
        _profile.Save();
        _profile.NotifyMutated();

        // Push to live services so the user's edit takes effect without
        // requiring a profile-reload.
        ApplyToServices(dto);
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
        ParPollOnTimer             = dto.ParPollOnTimer;
        ParPollFrequencySec        = dto.ParPollFrequencySec;
        ParPollAfterCombatRound    = dto.ParPollAfterCombatRound;
        ParPollIncludeWitnessedRounds = dto.ParPollIncludeWitnessedRounds;
        ParPollOnUnknownDamage     = dto.ParPollOnUnknownDamage;
        AutoInviteReconnecting     = dto.AutoInviteReconnecting;
        ResetStatisticsOnLoopStart = dto.ResetStatisticsOnLoopStart;
        RankFront = dto.Rank == PartyRank.Front;
        RankMid   = dto.Rank == PartyRank.Mid;
        RankBack  = dto.Rank == PartyRank.Back;
        JoinNagInitialDelaySec     = dto.JoinNagInitialDelaySec;
        JoinNagFrequencySec        = dto.JoinNagFrequencySec;
        JoinNagMaxTotalSec         = dto.JoinNagMaxTotalSec;
        SendJoinToInvited          = dto.SendJoinToInvited;
        SendHealthToMembers        = dto.SendHealthToMembers;
        ProbeStatsOnPartyJoin      = dto.ProbeStatsOnPartyJoin;
        IfLeadingWaitTotalSec      = dto.IfLeadingWaitTotalSec;
        ReturnDistanceRooms        = dto.ReturnDistanceRooms;
        AcceptComebackMinutes      = dto.AcceptComebackMinutes;

        MinorPartyHealSpell    = dto.MinorPartyHealSpell;
        MinorPartyHealAoeSpell = dto.MinorPartyHealAoeSpell;
        MajorPartyHealSpell    = dto.MajorPartyHealSpell;
        MajorPartyHealAoeSpell = dto.MajorPartyHealAoeSpell;
        MinorHealMemberThresholdPercent = dto.MinorHealMemberThresholdPercent;
        MajorHealMemberThresholdPercent = dto.MajorHealMemberThresholdPercent;
        AoeMinMembers          = dto.AoeMinMembers;
        RequestHealChannel     = dto.RequestHealChannel;
        MaxMonstersWhenPartying = dto.MaxMonstersWhenPartying;
        WaitIfMemberBelowPercent = dto.WaitIfMemberBelowPercent;
        IgnoreWaitWhenLeading  = dto.IgnoreWaitWhenLeading;
        HelpLeaderOpenDoors    = dto.HelpLeaderOpenDoors;
        UsePanicWhileLeading   = dto.UsePanicWhileLeading;
        IgnorePanics           = dto.IgnorePanics;

        // Mirror loaded settings into the live services so they reflect
        // the profile from first connection, not just after the user
        // visits this tab and clicks Apply.
        ApplyToServices(dto);
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private PartySettings ReadOrDefault() => SettingsSection.Read<PartySettings>(_profile.Current, TabKey);

    private static void ApplyToServices(PartySettings dto)
    {
        AppServices svcs = AppServices.Current;
        svcs.PartyPoller.ApplyParSettings(dto);
        svcs.Party.AutoInviteEnabled = dto.AutoInviteReconnecting;
        svcs.Party.LocalRankPreference = dto.Rank;
        svcs.PartyBroadcaster.AutoExpResetEnabled = dto.ResetStatisticsOnLoopStart;
        // Shared nag cadence feeds both the @join loop (AutoPartyManager)
        // and the on-join @health retry (PartyPoller). Mirror the canonical
        // push in AppServices.ApplyPartyFromActiveProfile so a Settings
        // Save takes effect live without waiting for a profile reload.
        TimeSpan nagInitial = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagInitialDelaySec, 1, 60));
        TimeSpan nagFreq    = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagFrequencySec,    1, 60));
        TimeSpan nagMax     = TimeSpan.FromSeconds(Math.Clamp(dto.JoinNagMaxTotalSec,     5, 600));
        svcs.AutoParty.JoinNagInitialDelay = nagInitial;
        svcs.AutoParty.JoinNagFrequency    = nagFreq;
        svcs.AutoParty.JoinNagMaxTotal     = nagMax;
        svcs.AutoParty.JoinNagEnabled      = dto.SendJoinToInvited;
        svcs.PartyPoller.HealthNagInitialDelay = nagInitial;
        svcs.PartyPoller.HealthNagFrequency    = nagFreq;
        svcs.PartyPoller.HealthNagMaxTotal     = nagMax;
        svcs.PartyPoller.HealthNagEnabled      = dto.SendHealthToMembers;
        svcs.PartyProbe.Enabled                = dto.ProbeStatsOnPartyJoin;
        svcs.Party.DisconnectGraceWindow   = TimeSpan.FromSeconds(Math.Clamp(dto.IfLeadingWaitTotalSec,  0, 3600));
        svcs.PartyComeback.ReturnDistanceRooms = Math.Clamp(dto.ReturnDistanceRooms, 1, 500);
        svcs.ApplyComebackWindow(dto.AcceptComebackMinutes);
    }

    // ----- IsDirty plumbing -----

    private void ClearDirty()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnParPollOnTimerChanged(bool value)            => MarkDirty();
    partial void OnParPollFrequencySecChanged(int value)        => MarkDirty();
    partial void OnParPollAfterCombatRoundChanged(bool value)   => MarkDirty();
    partial void OnParPollIncludeWitnessedRoundsChanged(bool value) => MarkDirty();
    partial void OnParPollOnUnknownDamageChanged(bool value)    => MarkDirty();
    partial void OnAutoInviteReconnectingChanged(bool value)    => MarkDirty();
    partial void OnResetStatisticsOnLoopStartChanged(bool value)=> MarkDirty();
    partial void OnRankFrontChanged(bool value)                 => MarkDirty();
    partial void OnRankMidChanged(bool value)                   => MarkDirty();
    partial void OnRankBackChanged(bool value)                  => MarkDirty();
    partial void OnJoinNagInitialDelaySecChanged(int value)     => MarkDirty();
    partial void OnJoinNagFrequencySecChanged(int value)        => MarkDirty();
    partial void OnJoinNagMaxTotalSecChanged(int value)         => MarkDirty();
    partial void OnSendJoinToInvitedChanged(bool value)         => MarkDirty();
    partial void OnSendHealthToMembersChanged(bool value)       => MarkDirty();
    partial void OnProbeStatsOnPartyJoinChanged(bool value)     => MarkDirty();
    partial void OnIfLeadingWaitTotalSecChanged(int value)      => MarkDirty();
    partial void OnReturnDistanceRoomsChanged(int value)        => MarkDirty();
    partial void OnAcceptComebackMinutesChanged(int value)      => MarkDirty();
    partial void OnMinorPartyHealSpellChanged(string? value)    => MarkDirty();
    partial void OnMinorPartyHealAoeSpellChanged(string? value) => MarkDirty();
    partial void OnMajorPartyHealSpellChanged(string? value)    => MarkDirty();
    partial void OnMajorPartyHealAoeSpellChanged(string? value) => MarkDirty();
    partial void OnMinorHealMemberThresholdPercentChanged(int value) => MarkDirty();
    partial void OnMajorHealMemberThresholdPercentChanged(int value) => MarkDirty();
    partial void OnAoeMinMembersChanged(int value)              => MarkDirty();
    partial void OnRequestHealChannelChanged(HealRequestChannel value) => MarkDirty();
    partial void OnMaxMonstersWhenPartyingChanged(int value)    => MarkDirty();
    partial void OnWaitIfMemberBelowPercentChanged(int value)   => MarkDirty();
    partial void OnIgnoreWaitWhenLeadingChanged(bool value)     => MarkDirty();
    partial void OnHelpLeaderOpenDoorsChanged(bool value)       => MarkDirty();
    partial void OnUsePanicWhileLeadingChanged(bool value)      => MarkDirty();
    partial void OnIgnorePanicsChanged(bool value)              => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }
}
