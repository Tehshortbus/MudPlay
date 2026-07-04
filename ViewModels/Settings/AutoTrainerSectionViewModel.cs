using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game;
using FujinTerm.Game.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// Settings → Auto-Trainer tab. Holds the master AutoTrain + cascading
// AutoTrainStats toggles, and a table of every training shop discovered in the
// active game-data set (via TrainerCatalog) — name, host map/room, served level
// range, and a per-trainer Allow toggle deciding whether auto-train may route to
// it. Trainers with the 999 MaxLVL sentinel (unreachable placeholders) are never
// listed. Persists to AutoTrainerSettings.
public sealed partial class AutoTrainerSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "AutoTrainer";

    private readonly ProfileService _profile;
    private readonly GameDataCache _gameData;
    private readonly PlayerStats _stats;
    // Every discovered trainer (allow-state preserved across filter toggles);
    // Trainers is the filtered view bound by the grid.
    private readonly List<AutoTrainerRowViewModel> _allRows = new();
    private Control? _view;
    private bool _suppressDirty;
    private bool _dirty;
    // Guards the re-entrant revert when Auto-train stats is forced off (no plan).
    private bool _forcingStatsOff;

    public override string Id => "autotrainer";
    public override string Title => "Auto-Trainer";
    public override bool IsDirty => _dirty;

    public override Control View => _view ??= new AutoTrainerSectionView { DataContext = this };

    // True when a profile is loaded — editor is hidden otherwise.
    public bool HasProfile => _profile.Current is not null;

    // False when the active set yields no trainers at all — drives the empty-state.
    public bool HasTrainers => _allRows.Count > 0;

    // True when the loaded profile has a saved CP allocation plan (needed for Auto-train stats).
    private bool HasCpPlan => _profile.Current?.CharacterPlan is { Count: > 0 };

    // Master auto-train toggle (level-up at the trainer during loop/auto-lair).
    [ObservableProperty] private bool _autoTrain;
    // Cascading toggle — apply the CP plan via `train stats` after each train.
    [ObservableProperty] private bool _autoTrainStats;

    // Trainable levels to always keep banked (0 = train everything).
    [ObservableProperty] private int _levelsToKeep;

    // Broadcast "I can now train to level: N" when a live exp gain makes a new level trainable.
    [ObservableProperty] private bool _announceLevelUps;
    // Channel the level-up announce is sent on (enabled only with AnnounceLevelUps).
    [ObservableProperty] private AnnounceChannel _announceChannel;

    // Channel choices for the announce dropdown.
    public IReadOnlyList<AnnounceChannel> AnnounceChannels { get; } =
        (AnnounceChannel[])Enum.GetValues(typeof(AnnounceChannel));

    // View filter (not persisted). Off = show every discovered trainer; on =
    // only trainers whose level range serves the character's current level.
    [ObservableProperty] private bool _onlyUsableLevel;

    // Set when the user tries to enable Auto-train stats with no saved CP plan —
    // the checkbox reverts and this explains why. Null = hidden.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoTrainStatsWarning))]
    private string? _autoTrainStatsWarning;

    // Drives the Auto-train-stats warning's visibility.
    public bool HasAutoTrainStatsWarning => !string.IsNullOrEmpty(AutoTrainStatsWarning);

    // Discovered trainers in the active set, ascending by level range.
    public ObservableCollection<AutoTrainerRowViewModel> Trainers { get; } = new();

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "Auto-train", "Auto-train stats", "trainer", "train", "guild", "level up",
        "announce level-ups", "announce channel", "levels to keep", "keep banked", "buffer",
    };

    public AutoTrainerSectionViewModel()
        : this(AppServices.Current.Profile, AppServices.Current.GameData, AppServices.Current.PlayerStats) { }

    public AutoTrainerSectionViewModel(ProfileService profile, GameDataCache gameData, PlayerStats stats)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(stats);
        _profile = profile;
        _gameData = gameData;
        _stats = stats;

        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _gameData.ActiveSetChanged -= OnActiveSetChanged;
        });

        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        // Build from _allRows (every trainer), not the filtered view — a hidden
        // row's allow-state must survive a Save while a filter is active.
        List<string> disabled = _allRows.Where(t => !t.Allowed)
            .Select(t => t.RowKey)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        AutoTrainerSettings dto = new()
        {
            AutoTrain = AutoTrain,
            AutoTrainStats = AutoTrain && AutoTrainStats,   // cascade — never store stats-on without train-on
            LevelsToKeep = Math.Max(0, LevelsToKeep),
            AnnounceLevelUps = AnnounceLevelUps,
            AnnounceChannel = AnnounceChannel,
            DisabledTrainers = disabled.Count == 0 ? null : disabled,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();
        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    private void OnProfileChanged(CharacterProfile _) => ReloadAfterSwap();
    private void OnProfileClosedExternally() => ReloadAfterSwap();
    private void OnActiveSetChanged(string? _) => ReloadAfterSwap();

    private void ReloadAfterSwap()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private void LoadFromProfile()
    {
        AutoTrainerSettings dto = ReadOrDefault();
        AutoTrain = dto.AutoTrain;
        AutoTrainStats = dto.AutoTrain && dto.AutoTrainStats;
        LevelsToKeep = Math.Max(0, dto.LevelsToKeep);
        AnnounceLevelUps = dto.AnnounceLevelUps;
        AnnounceChannel = dto.AnnounceChannel;
        RebuildTrainers(dto.DisabledTrainers);
    }

    private void RebuildTrainers(IReadOnlyCollection<string>? disabled)
    {
        _allRows.Clear();
        HashSet<string> off = disabled is { Count: > 0 }
            ? new HashSet<string>(disabled, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        foreach (TrainerShop t in TrainerCatalog.Enumerate(_gameData)
                     .OrderBy(t => t.MinLevel).ThenBy(t => t.MaxLevel)
                     .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.RoomName, StringComparer.OrdinalIgnoreCase))
        {
            string mapRoom = t.HasRoom
                ? string.Create(CultureInfo.InvariantCulture, $"{t.Map}/{t.Room}")
                : "—";
            string range = string.Create(CultureInfo.InvariantCulture, $"{t.MinLevel}–{t.MaxLevel}");
            _allRows.Add(new AutoTrainerRowViewModel(t, mapRoom, range, allowed: !off.Contains(t.RowKey), MarkDirty));
        }
        ApplyFilter();
    }

    // Rebuild the bound (filtered) view from _allRows. The filter is view-only —
    // it never marks dirty or persists. Other classes' guild trainers are always
    // hidden (a Mystic never needs to see the Warrior guild); the level filter is
    // the optional user toggle on top of that.
    private void ApplyFilter()
    {
        int level = _stats.Level;
        int classNumber = ResolveClassNumber();

        Trainers.Clear();
        foreach (AutoTrainerRowViewModel row in _allRows)
        {
            if (classNumber > 0 && !row.ServesClass(classNumber)) continue;
            if (OnlyUsableLevel && !row.ServesLevel(level)) continue;
            Trainers.Add(row);
        }
        OnPropertyChanged(nameof(HasTrainers));
    }

    // Resolve the loaded character's class to its game-data Classes.Number, or 0
    // when unknown (no class parsed yet / class not in the active set) — in which
    // case the class filter is skipped and every trainer is shown.
    private int ResolveClassNumber()
    {
        string className = _stats.Class;
        if (string.IsNullOrWhiteSpace(className)) return 0;
        if (_gameData.FindRowByName("Classes", className) is not { } row) return 0;
        if (!row.TryGetProperty("Number", out JsonElement numEl)) return 0;
        return numEl.ValueKind switch
        {
            JsonValueKind.Number => numEl.GetInt32(),
            JsonValueKind.String when int.TryParse(numEl.GetString(), out int n) => n,
            _ => 0,
        };
    }

    partial void OnOnlyUsableLevelChanged(bool value) => ApplyFilter();

    private AutoTrainerSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new AutoTrainerSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new AutoTrainerSettings();
        try
        {
            return JsonSerializer.Deserialize<AutoTrainerSettings>(json) ?? new AutoTrainerSettings();
        }
        catch
        {
            return new AutoTrainerSettings();
        }
    }

    // Turning the master toggle off disables the stats cascade too, so the UI
    // never shows "apply stats" enabled without "train" behind it.
    partial void OnAutoTrainChanged(bool value)
    {
        if (!value) AutoTrainStats = false;
        MarkDirty();
    }

    partial void OnAutoTrainStatsChanged(bool value)
    {
        if (_forcingStatsOff) return;   // re-entry from the revert below

        // Auto-train stats needs a saved CP plan to apply — without one, revert
        // the box and tell the user where to set the plan up.
        if (value && !HasCpPlan)
        {
            AutoTrainStatsWarning =
                "Set up and save a CP allocation plan in the Player Workshop (CP Allocation tab) before enabling Auto-train stats.";
            _forcingStatsOff = true;
            try { AutoTrainStats = false; }
            finally { _forcingStatsOff = false; }
            return;
        }

        AutoTrainStatsWarning = null;
        MarkDirty();
    }

    partial void OnAnnounceLevelUpsChanged(bool value) => MarkDirty();
    partial void OnAnnounceChannelChanged(AnnounceChannel value) => MarkDirty();

    partial void OnLevelsToKeepChanged(int value)
    {
        // The numeric stepper is clamped in the view, but guard here too so a
        // stray negative never persists as a bogus buffer.
        if (value < 0) { LevelsToKeep = 0; return; }   // re-enters with 0, marks dirty there
        MarkDirty();
    }

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
}
