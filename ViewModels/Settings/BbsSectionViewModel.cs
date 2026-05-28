using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.Settings;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "BBS" tab. Owns the list of saved BBS records (globally shared across
/// every character) and the field-editor for whichever one is selected.
/// Per-character credentials (username, password, menu-nav sequence)
/// live on the character profile and ship in PR 4.5b / 4.5c — this PR
/// is the BBS record itself only.
/// </summary>
/// <remarks>
/// Apply walks the cached in-memory BBS profiles and persists every
/// dirty one. Discard reloads the currently-selected BBS from disk so
/// pending edits are dropped. Adding / deleting a BBS commits immediately
/// (those are structural, not field-level edits — the OK / Cancel commit
/// only covers field tweaks).
/// </remarks>
public sealed partial class BbsSectionViewModel : SettingsSectionViewModel
{
    private readonly BbsProfileStore _bbsStore;
    private readonly Dictionary<string, BbsProfile> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressDirty = true;
    private bool _dirty;
    private Control? _view;

    public override string Id => "bbs";
    public override string Title => "BBS";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "BBS", "Host", "Port", "Telnet", "Redial", "Cleanup", "Reconnect",
        "Sysop", "Terminal", "Cols", "Rows", "NAWS", "Connection",
    };

    public override Control View => _view ??= new BbsSectionView { DataContext = this };

    /// <summary>Names of every saved BBS profile (left rail of the tab).</summary>
    public ObservableCollection<string> AvailableBbsNames { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private string? _selectedBbsName;

    public bool HasSelection => SelectedBbsName is not null;

    // ----- Editable fields, populated from the selected BbsProfile -----
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 23;
    [ObservableProperty] private string? _websiteUrl;
    [ObservableProperty] private int _maxRedials = 3;
    [ObservableProperty] private int _redialPauseSeconds = 5;
    [ObservableProperty] private int _cleanupPeriodMinutes;
    [ObservableProperty] private bool _reconnectOnFailedConnect;
    [ObservableProperty] private bool _reconnectOnCarrierLost;
    [ObservableProperty] private bool _reconnectOnNoResponse;
    [ObservableProperty] private bool _reconnectAfterCleanup;
    [ObservableProperty] private bool _hasSysopPowers;
    [ObservableProperty] private int _terminalCols = 80;
    [ObservableProperty] private int _terminalRows = 25;

    public BbsSectionViewModel(BbsProfileStore bbsStore)
    {
        ArgumentNullException.ThrowIfNull(bbsStore);
        _bbsStore = bbsStore;

        ReloadBbsList();
        SelectedBbsName = AvailableBbsNames.FirstOrDefault();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        foreach (BbsProfile profile in _loaded.Values)
        {
            _bbsStore.Save(profile);
        }
        ClearDirty();
    }

    public override void Discard()
    {
        // Drop every cached in-memory edit and re-fetch from disk on the
        // next selection. Keeps the Apply contract: Cancel really cancels.
        _loaded.Clear();
        if (SelectedBbsName is not null)
        {
            _suppressDirty = true;
            ReloadSelected();
            _suppressDirty = false;
        }
        ClearDirty();
    }

    [RelayCommand]
    private void AddBbs()
    {
        string baseName = "New BBS";
        string name = baseName;
        int n = 2;
        while (_bbsStore.Get(name) is not null || _loaded.ContainsKey(name))
        {
            name = $"{baseName} {n++}";
        }
        BbsProfile fresh = new() { Name = name, Host = string.Empty, Port = 23 };
        _bbsStore.Save(fresh);
        _loaded[name] = fresh;
        ReloadBbsList();
        SelectedBbsName = name;
    }

    [RelayCommand]
    private void DeleteBbs()
    {
        if (SelectedBbsName is not { } name) return;
        _bbsStore.Delete(name);
        _loaded.Remove(name);
        ReloadBbsList();
        SelectedBbsName = AvailableBbsNames.FirstOrDefault();
    }

    partial void OnSelectedBbsNameChanged(string? value)
    {
        if (_suppressDirty) return;
        _suppressDirty = true;
        ReloadSelected();
        _suppressDirty = false;
    }

    private void ReloadBbsList()
    {
        AvailableBbsNames.Clear();
        foreach (string name in _bbsStore.ListNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            AvailableBbsNames.Add(name);
        }
    }

    private void ReloadSelected()
    {
        if (SelectedBbsName is not { } name)
        {
            ResetFields();
            return;
        }

        if (!_loaded.TryGetValue(name, out BbsProfile? profile))
        {
            profile = _bbsStore.Get(name) ?? new BbsProfile { Name = name };
            _loaded[name] = profile;
        }

        Host = profile.Host;
        Port = profile.Port;
        WebsiteUrl = profile.WebsiteUrl;
        MaxRedials = profile.MaxRedials;
        RedialPauseSeconds = profile.RedialPauseSeconds;
        CleanupPeriodMinutes = profile.CleanupPeriodMinutes;
        ReconnectOnFailedConnect = profile.ReconnectOnFailedConnect;
        ReconnectOnCarrierLost = profile.ReconnectOnCarrierLost;
        ReconnectOnNoResponse = profile.ReconnectOnNoResponse;
        ReconnectAfterCleanup = profile.ReconnectAfterCleanup;
        HasSysopPowers = profile.HasSysopPowers;
        TerminalCols = profile.TerminalCols;
        TerminalRows = profile.TerminalRows;
    }

    private void ResetFields()
    {
        BbsProfile defaults = new();
        Host = defaults.Host;
        Port = defaults.Port;
        WebsiteUrl = defaults.WebsiteUrl;
        MaxRedials = defaults.MaxRedials;
        RedialPauseSeconds = defaults.RedialPauseSeconds;
        CleanupPeriodMinutes = defaults.CleanupPeriodMinutes;
        ReconnectOnFailedConnect = defaults.ReconnectOnFailedConnect;
        ReconnectOnCarrierLost = defaults.ReconnectOnCarrierLost;
        ReconnectOnNoResponse = defaults.ReconnectOnNoResponse;
        ReconnectAfterCleanup = defaults.ReconnectAfterCleanup;
        HasSysopPowers = defaults.HasSysopPowers;
        TerminalCols = defaults.TerminalCols;
        TerminalRows = defaults.TerminalRows;
    }

    private void Dirty()
    {
        if (_suppressDirty || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void ClearDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    // Field-change hooks: writes the new value into the in-memory cache for
    // the currently-selected BBS so Apply has something fresh to persist.
    private void PushToCache()
    {
        if (_suppressDirty) return;
        if (SelectedBbsName is not { } name) return;
        if (!_loaded.TryGetValue(name, out BbsProfile? profile)) return;

        profile.Host = Host;
        profile.Port = Port;
        profile.WebsiteUrl = string.IsNullOrWhiteSpace(WebsiteUrl) ? null : WebsiteUrl;
        profile.MaxRedials = MaxRedials;
        profile.RedialPauseSeconds = RedialPauseSeconds;
        profile.CleanupPeriodMinutes = CleanupPeriodMinutes;
        profile.ReconnectOnFailedConnect = ReconnectOnFailedConnect;
        profile.ReconnectOnCarrierLost = ReconnectOnCarrierLost;
        profile.ReconnectOnNoResponse = ReconnectOnNoResponse;
        profile.ReconnectAfterCleanup = ReconnectAfterCleanup;
        profile.HasSysopPowers = HasSysopPowers;
        profile.TerminalCols = TerminalCols;
        profile.TerminalRows = TerminalRows;
    }

    partial void OnHostChanged(string value)                    { PushToCache(); Dirty(); }
    partial void OnPortChanged(int value)                       { PushToCache(); Dirty(); }
    partial void OnWebsiteUrlChanged(string? value)             { PushToCache(); Dirty(); }
    partial void OnMaxRedialsChanged(int value)                 { PushToCache(); Dirty(); }
    partial void OnRedialPauseSecondsChanged(int value)         { PushToCache(); Dirty(); }
    partial void OnCleanupPeriodMinutesChanged(int value)       { PushToCache(); Dirty(); }
    partial void OnReconnectOnFailedConnectChanged(bool value)  { PushToCache(); Dirty(); }
    partial void OnReconnectOnCarrierLostChanged(bool value)    { PushToCache(); Dirty(); }
    partial void OnReconnectOnNoResponseChanged(bool value)     { PushToCache(); Dirty(); }
    partial void OnReconnectAfterCleanupChanged(bool value)     { PushToCache(); Dirty(); }
    partial void OnHasSysopPowersChanged(bool value)            { PushToCache(); Dirty(); }
    partial void OnTerminalColsChanged(int value)               { PushToCache(); Dirty(); }
    partial void OnTerminalRowsChanged(int value)               { PushToCache(); Dirty(); }
}
