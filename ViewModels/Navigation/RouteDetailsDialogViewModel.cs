using System;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// Modeless, read-only browse window for the route the nav engine is currently
// executing — the full step plan (route-picker "N> map/room < command" rows) with
// each lair room's monsters listed as clickable record links. A window (not a
// flyout) so it's easy to scroll and to click several monster records without it
// dismissing. Snapshot: the rows (and each monster's live Hits-You-%) are built once
// when opened; re-open for a fresh plan.
//
// Monster names tint by alignment by default. When "Color monsters by hit %" is on,
// each name is recoloured by its live Hits-You-% against a green / amber / red band
// split the user drags on a two-thumb slider — a low chance-to-hit-you reads safe,
// a high one dangerous. The toggle + band split persist install-wide (Global tier)
// through the persist callback the launcher supplies.
public sealed partial class RouteDetailsDialogViewModel : ObservableObject, IDialogViewModel<bool?>
{
    // (enabled, greenMax%, yellowMax%) → write the Global-tier delta. Null in tests.
    private readonly Action<bool, int, int>? _persist;
    private bool _suppressPersist;

    public event Action<bool?>? CloseRequested;

    public string Title { get; }
    public IReadOnlyList<RouteDetailRow> Rows { get; }
    public bool HasRows => Rows.Count > 0;

    // Any monster rooms on the route? The colour controls only make sense (and only
    // show) when there's a monster name to tint.
    public bool HasMonsters { get; }

    // Colour monsters by Hits-You-% instead of by alignment.
    [ObservableProperty] private bool _colorByHitPercent;

    // The two band boundaries (%), driven by the range slider's thumbs. Doubles so
    // they bind straight to the slider; rounded to ints for the mapping + persistence.
    [ObservableProperty] private double _greenMax;
    [ObservableProperty] private double _yellowMax;

    // Band brushes handed to the slider track so its colours match the monster tints
    // exactly (one source: MonsterHitBand).
    public IBrush GreenBrush => MonsterHitBand.Green;
    public IBrush YellowBrush => MonsterHitBand.Yellow;
    public IBrush RedBrush => MonsterHitBand.Red;

    // Middle band is labelled "yellow" in the UI even though the swatch is amber —
    // the user's preferred wording.
    public string BandLegend =>
        $"green ≤ {GreenMax:0}%      yellow ≤ {YellowMax:0}%      red > {YellowMax:0}%";

    public RouteDetailsDialogViewModel(string title, IReadOnlyList<RouteDetailRow> rows)
        : this(title, rows, false,
            MonsterHitColorSettings.DefaultGreenMax, MonsterHitColorSettings.DefaultYellowMax, null)
    {
    }

    public RouteDetailsDialogViewModel(
        string title, IReadOnlyList<RouteDetailRow> rows,
        bool colorByHitPercent, int greenMax, int yellowMax,
        Action<bool, int, int>? persist)
    {
        Title = title;
        Rows = rows ?? Array.Empty<RouteDetailRow>();
        HasMonsters = Rows.Any(r => r.HasMonsters);
        _persist = persist;

        _suppressPersist = true;
        _colorByHitPercent = colorByHitPercent;
        _greenMax = greenMax;
        _yellowMax = yellowMax;
        _suppressPersist = false;

        ApplyColors();
    }

    partial void OnColorByHitPercentChanged(bool value)
    {
        ApplyColors();
        Persist();
    }

    partial void OnGreenMaxChanged(double value)
    {
        // Belt-and-suspenders ordering (the slider already keeps a gap): never let the
        // green boundary pass the amber one.
        if (value > YellowMax) { _yellowMax = value; OnPropertyChanged(nameof(YellowMax)); }
        OnBandChanged();
    }

    partial void OnYellowMaxChanged(double value)
    {
        if (value < GreenMax) { _greenMax = value; OnPropertyChanged(nameof(GreenMax)); }
        OnBandChanged();
    }

    private void OnBandChanged()
    {
        OnPropertyChanged(nameof(BandLegend));
        if (ColorByHitPercent) ApplyColors();
        Persist();
    }

    // Repaint every monster link: the live hit-% band brush when the toggle is on,
    // else the monster's own alignment tint. Cheap — a handful of links per route.
    private void ApplyColors()
    {
        int g = (int)Math.Round(GreenMax), y = (int)Math.Round(YellowMax);
        foreach (RouteDetailRow row in Rows)
            foreach (RoomDetailLink link in row.Monsters)
                link.Accent = ColorByHitPercent
                    ? MonsterHitBand.BrushFor(link.HitPercent, g, y)
                    : link.AlignAccent;
    }

    private void Persist()
    {
        if (_suppressPersist) return;
        _persist?.Invoke(ColorByHitPercent, (int)Math.Round(GreenMax), (int)Math.Round(YellowMax));
    }

    // Toggle-close from the opener (re-clicking Details…) and the Close button both
    // route here; the title-bar X closes via the window itself (DialogService treats
    // that as an implicit cancel). Read-only, so there's no commit-vs-cancel to
    // distinguish — any close is fine.
    public void RequestClose() => CloseRequested?.Invoke(null);

    [RelayCommand]
    private void Close() => RequestClose();
}
