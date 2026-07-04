using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// "Party" tab — bespoke layout. Knobs that map onto live services (par poll
// cadence, auto-invite reconnecting member, reset statistics on loop start);
// the party-heal pickers + thresholds + AOE-member count, and the 10
// party-bless slots, feed CastingDirector's party-cast path. Persists per
// character as the "Party" entry in CharacterProfile.Settings.
public sealed partial class PartySectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Party";

    private readonly ProfileService _profile;
    private readonly Game.Spells.SpellbookState _spellbook;
    private readonly GameDataCache _gameData;
    private Control? _view;
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

    public override Control View => _view ??= new PartySectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Party", "Rank", "Front", "Mid", "Back",
        "Party heal", "Minor heal", "Major heal", "Single-target", "Party AOE",
        "Use AOE", "Request healing",
        "Bless", "Bless while resting", "Bless during combat",
        "Help leader open doors",
        "Auto-invite", "Auto-Exp-Reset", "par frequency",
        "Wait for members", "Max monsters",
    };

    // ----- Wired knobs -----

    // par poll cadence in seconds; range 1..60. Default 5.
    [ObservableProperty] private int _parPollFrequencySec = 5;

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

    // The shared initial-delay spinner (in Options, next to the @join toggle)
    // edits the value both nag flows use, so it's editable whenever either nag
    // is enabled.
    public bool NagInitialDelayEditable => SendJoinToInvited || SendHealthToMembers;

    // ----- "If leading, wait only" — disconnect grace window in seconds.
    //       Single field; UI uses one NumericUpDown with Increment=10
    //       and free-text entry for non-multiples of 10. Default 90.
    [ObservableProperty] private int _ifLeadingWaitTotalSec = 90;

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

    // ----- Party bless gating (consumed by CastingDirector) ----------
    // Two coarse gates the party-bless path honors before casting a
    // beneficial spell on a member. Both default ON.
    [ObservableProperty] private bool _blessWhileResting = true;
    [ObservableProperty] private bool _blessDuringCombat = true;

    // ----- Party bless slots (consumed by CastingDirector) -----------
    // 10 fixed rows; each pairs a spell short-code with the set of class
    // numbers it targets. Rebuilt from the active set's Classes table on
    // load + ActiveSetChanged so the per-class checkboxes track whatever
    // classes the imported gamedata defines.

    // The 10 editable party-bless rows. Always 10 entries so the UI binds
    // one-to-one with the persisted slots.
    public ObservableCollection<PartyBlessSlotViewModel> BlessSlots { get; } = new();

    public PartySectionViewModel() : this(AppServices.Current.Profile) { }

    public PartySectionViewModel(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _spellbook = AppServices.Current.Spellbook;
        _gameData = AppServices.Current.GameData;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _spellbook.Changed += OnSpellbookChanged;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _spellbook.Changed -= OnSpellbookChanged;
            _gameData.ActiveSetChanged -= OnActiveSetChanged;
        });
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    private void OnSpellbookChanged() => OnPropertyChanged(nameof(SpellSuggestions));

    // Class roster changed (game-data set swap) — rebuild the bless rows
    // preserving the user's current spell + checked-class edits. Marshalled
    // because the cache fires this from its load path.
    private void OnActiveSetChanged(string? _) =>
        Dispatcher.UIThread.Post(() => RebuildBlessSlots(SnapshotBlessSlots()));

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
            SendJoinToInvited        = SendJoinToInvited,
            SendHealthToMembers      = SendHealthToMembers,
            IfLeadingWaitTotalSec    = Math.Clamp(IfLeadingWaitTotalSec,  0, 3600),

            MinorPartyHealSpell    = NullIfBlank(MinorPartyHealSpell),
            MinorPartyHealAoeSpell = NullIfBlank(MinorPartyHealAoeSpell),
            MajorPartyHealSpell    = NullIfBlank(MajorPartyHealSpell),
            MajorPartyHealAoeSpell = NullIfBlank(MajorPartyHealAoeSpell),
            MinorHealMemberThresholdPercent = Math.Clamp(MinorHealMemberThresholdPercent, 0, 100),
            MajorHealMemberThresholdPercent = Math.Clamp(MajorHealMemberThresholdPercent, 0, 100),
            AoeMinMembers          = Math.Clamp(AoeMinMembers, 2, 6),
            MaxMonstersWhenPartying = Math.Clamp(MaxMonstersWhenPartying, 1, 20),
            WaitIfMemberBelowPercent = Math.Clamp(WaitIfMemberBelowPercent, 0, 100),
            IgnoreWaitWhenLeading  = IgnoreWaitWhenLeading,
            HelpLeaderOpenDoors    = HelpLeaderOpenDoors,
            BlessWhileResting      = BlessWhileResting,
            BlessDuringCombat      = BlessDuringCombat,
            BlessSlots             = SnapshotBlessSlots(),
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
        SendJoinToInvited          = dto.SendJoinToInvited;
        SendHealthToMembers        = dto.SendHealthToMembers;
        IfLeadingWaitTotalSec      = dto.IfLeadingWaitTotalSec;

        MinorPartyHealSpell    = dto.MinorPartyHealSpell;
        MinorPartyHealAoeSpell = dto.MinorPartyHealAoeSpell;
        MajorPartyHealSpell    = dto.MajorPartyHealSpell;
        MajorPartyHealAoeSpell = dto.MajorPartyHealAoeSpell;
        MinorHealMemberThresholdPercent = dto.MinorHealMemberThresholdPercent;
        MajorHealMemberThresholdPercent = dto.MajorHealMemberThresholdPercent;
        AoeMinMembers          = dto.AoeMinMembers;
        MaxMonstersWhenPartying = dto.MaxMonstersWhenPartying;
        WaitIfMemberBelowPercent = dto.WaitIfMemberBelowPercent;
        IgnoreWaitWhenLeading  = dto.IgnoreWaitWhenLeading;
        HelpLeaderOpenDoors    = dto.HelpLeaderOpenDoors;
        BlessWhileResting      = dto.BlessWhileResting;
        BlessDuringCombat      = dto.BlessDuringCombat;
        RebuildBlessSlots(NormalizeSlots(dto.BlessSlots));

        // Mirror loaded settings into the live services so they reflect
        // the profile from first connection, not just after the user
        // visits this tab and clicks Apply.
        ApplyToServices(dto);
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ----- Bless-slot rebuild / snapshot -----------------------------

    // Snapshot the current bless rows to persistable DTOs. Always 10 entries —
    // falls back to fresh empties before any row VM has been built (e.g. when
    // Apply runs with no profile UI).
    private List<PartyBlessSlot> SnapshotBlessSlots() =>
        BlessSlots.Count == 0
            ? PartySettings.NewBlessSlots()
            : BlessSlots.Select(s => s.ToDto()).ToList();

    // Pad / trim a deserialized slot list to exactly 10 entries so the UI always
    // renders the full set even if an older profile stored fewer.
    private static List<PartyBlessSlot> NormalizeSlots(List<PartyBlessSlot>? slots)
    {
        List<PartyBlessSlot> result = new(PartySettings.PartyBlessSlotCount);
        for (int i = 0; i < PartySettings.PartyBlessSlotCount; i++)
            result.Add(slots is not null && i < slots.Count ? slots[i] : new PartyBlessSlot());
        return result;
    }

    // Rebuild the 10 row VMs against the active set's class roster, seeding each
    // from slots. Suppresses dirty during the swap so a profile load / set swap
    // doesn't mark the tab dirty.
    private void RebuildBlessSlots(List<PartyBlessSlot> slots)
    {
        bool wasSuppressed = _suppressDirty;
        _suppressDirty = true;
        try
        {
            IReadOnlyList<(int Number, string Name)> classes = ReadClasses();
            BlessSlots.Clear();
            for (int i = 0; i < PartySettings.PartyBlessSlotCount; i++)
                BlessSlots.Add(new PartyBlessSlotViewModel(i + 1, classes, slots[i], MarkDirty));
        }
        finally
        {
            _suppressDirty = wasSuppressed;
        }
    }

    // Read the active set's Classes table as (Number, Name) pairs, ordered by
    // Number. Empty when no set is loaded — the bless rows then render with no
    // class checkboxes.
    private IReadOnlyList<(int Number, string Name)> ReadClasses()
    {
        List<(int Number, string Name)> classes = new();
        JsonDocument? doc = _gameData.GetRawTable("Classes");
        if (doc is null) return classes;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Number", out JsonElement numEl) ||
                !numEl.TryGetInt32(out int number))
                continue;
            string name = row.TryGetProperty("Name", out JsonElement nameEl)
                ? nameEl.GetString() ?? $"Class {number}"
                : $"Class {number}";
            classes.Add((number, name));
        }
        classes.Sort(static (a, b) => a.Number.CompareTo(b.Number));
        return classes;
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
    partial void OnSendJoinToInvitedChanged(bool value)         => MarkDirty();
    partial void OnSendHealthToMembersChanged(bool value)       => MarkDirty();
    partial void OnIfLeadingWaitTotalSecChanged(int value)      => MarkDirty();
    partial void OnMinorPartyHealSpellChanged(string? value)    => MarkDirty();
    partial void OnMinorPartyHealAoeSpellChanged(string? value) => MarkDirty();
    partial void OnMajorPartyHealSpellChanged(string? value)    => MarkDirty();
    partial void OnMajorPartyHealAoeSpellChanged(string? value) => MarkDirty();
    partial void OnMinorHealMemberThresholdPercentChanged(int value) => MarkDirty();
    partial void OnMajorHealMemberThresholdPercentChanged(int value) => MarkDirty();
    partial void OnAoeMinMembersChanged(int value)              => MarkDirty();
    partial void OnMaxMonstersWhenPartyingChanged(int value)    => MarkDirty();
    partial void OnWaitIfMemberBelowPercentChanged(int value)   => MarkDirty();
    partial void OnIgnoreWaitWhenLeadingChanged(bool value)     => MarkDirty();
    partial void OnHelpLeaderOpenDoorsChanged(bool value)       => MarkDirty();
    partial void OnBlessWhileRestingChanged(bool value)         => MarkDirty();
    partial void OnBlessDuringCombatChanged(bool value)         => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty) return;
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }
}
