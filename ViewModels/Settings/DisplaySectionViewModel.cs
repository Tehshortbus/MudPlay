using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Display" tab. Wires the in-PR scope (font size + scrollback line
/// count) and live-pushes font-size changes through
/// <see cref="AppServices.Display"/> so the user sees the new size in
/// the terminal as soon as they tweak it. Scrollback writes through
/// too but doesn't resize the in-flight ring — that applies on the
/// next launch.
/// </summary>
/// <remarks>
/// Apply persists the values into the character profile under the
/// <c>"Display"</c> settings key. Discard re-reads from the same
/// place and also rolls the live <see cref="DisplayConfig"/> back, so
/// cancelled previews don't stick.
/// </remarks>
public sealed partial class DisplaySectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Display";

    private readonly ProfileService _profile;
    private readonly DisplayConfig _display;
    private bool _suppressDirty = true;
    private bool _dirty;
    private Control? _view;

    public override string Id => "display";
    public override string Title => "Display";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Display", "Font", "Font size", "Scrollback", "Backscroll", "Buffer",
    };

    public override Control View => _view ??= new DisplaySectionView { DataContext = this };

    public bool HasProfile => _profile.Current is not null;

    [ObservableProperty] private double _fontSize = 16.0;
    [ObservableProperty] private int _scrollbackLines = 10_000;

    public DisplaySectionViewModel(ProfileService profile, DisplayConfig display)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(display);
        _profile = profile;
        _display = display;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosedExternally;

        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        DisplaySettings dto = new()
        {
            FontSize = FontSize,
            ScrollbackLines = ScrollbackLines,
        };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();

        // Push the just-applied values into the live singleton too — Apply
        // is the "make it real" path for cycles where the user previewed
        // (which already wrote through) and for cycles where Apply happens
        // without any preview pass (e.g. programmatic).
        _display.FontSize = dto.FontSize;
        _display.ScrollbackLines = dto.ScrollbackLines;

        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    private void OnProfileLoaded(CharacterProfile _) => ReloadAfterProfileSwap();
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
        DisplaySettings dto = ReadOrDefault();
        FontSize = dto.FontSize;
        ScrollbackLines = dto.ScrollbackLines;

        // Mirror to the live singleton so the terminal reflects the loaded
        // profile's display settings (font size in particular).
        _display.FontSize = dto.FontSize;
        _display.ScrollbackLines = dto.ScrollbackLines;
    }

    private DisplaySettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new();
        return JsonSerializer.Deserialize<DisplaySettings>(json.GetRawText()) ?? new();
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

    // Font size is live-preview — every change pushes into the singleton so
    // the terminal re-renders immediately. Apply / Discard are still the
    // commit boundary for the JSON file.
    partial void OnFontSizeChanged(double value)
    {
        _display.FontSize = value;
        Dirty();
    }

    // Scrollback only persists — the live ScrollbackBuffer is sized at
    // construction. Don't push to _display.ScrollbackLines mid-edit; the
    // value lands on Apply / next launch.
    partial void OnScrollbackLinesChanged(int value) => Dirty();
}
