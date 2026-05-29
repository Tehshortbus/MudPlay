using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Statline" tab — Phase 4 PR 4.7 basic surface. Holds the wildcard
/// string and a "send to BBS" button so the user can push a fresh
/// statline at the game. Phase 12 PR 12.1 replaces this with a
/// token-builder dialog; the persisted DTO shape is the same so the
/// upgrade is transparent.
/// </summary>
public sealed partial class StatlineSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Statline";

    private readonly ProfileService _profile;
    private readonly Func<string, Task<bool>>? _sendText;
    private bool _suppressDirty = true;
    private bool _dirty;
    private Control? _view;

    public override string Id => "statline";
    public override string Title => "Statline";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Statline", "Prompt", "HP", "MA", "Set statline",
    };

    public override Control View => _view ??= new StatlineSectionView { DataContext = this };

    /// <summary>True when any character profile is loaded.</summary>
    public bool HasProfile => _profile.Current is not null;

    /// <summary>The current statline wildcard authored by the user.</summary>
    [ObservableProperty] private string _wildcard = string.Empty;

    /// <summary>
    /// Short status line shown next to the Send button — last action's
    /// result. Reset when the user edits the wildcard.
    /// </summary>
    [ObservableProperty] private string _sendStatus = string.Empty;

    public StatlineSectionViewModel(ProfileService profile, Func<string, Task<bool>>? sendText)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _sendText = sendText;
        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;

        LoadFromProfile();
        _suppressDirty = false;
    }

    /// <summary>True when the Send button should be enabled — needs both a sender and non-empty text.</summary>
    public bool CanSend => _sendText is not null && !string.IsNullOrWhiteSpace(Wildcard);

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        StatlineSettings dto = new()
        {
            Wildcard = string.IsNullOrWhiteSpace(Wildcard) ? null : Wildcard,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();
        _profile.NotifyMutated();
        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    /// <summary>
    /// Push the current wildcard to the connected BBS as
    /// <c>set statline &lt;wildcard&gt;\r</c>. No-op (with a status
    /// banner) when nothing is connected or the wildcard is blank.
    /// </summary>
    [RelayCommand]
    private async Task SendToBbsAsync()
    {
        if (_sendText is null)
        {
            SendStatus = "Connect to a BBS first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Wildcard))
        {
            SendStatus = "Statline is empty.";
            return;
        }

        bool ok = await _sendText($"set statline {Wildcard}\r").ConfigureAwait(true);
        SendStatus = ok ? "Sent." : "Send failed — check connection.";
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
        StatlineSettings dto = ReadOrDefault();
        Wildcard = dto.Wildcard ?? string.Empty;
        SendStatus = string.Empty;
    }

    private StatlineSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new();
        return JsonSerializer.Deserialize<StatlineSettings>(json.GetRawText()) ?? new();
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

    partial void OnWildcardChanged(string value)
    {
        if (!_suppressDirty) SendStatus = string.Empty;
        OnPropertyChanged(nameof(CanSend));
        Dirty();
    }
}
