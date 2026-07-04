using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class LevelProjectionSectionView : UserControl
{
    public LevelProjectionSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Tint the live character's current-level row. DataGrid recycles row
    // containers, so the background is both set and cleared on every load.
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        bool isCurrent = e.Row.DataContext is LevelProjectionRow { IsCurrentLevel: true };
        e.Row.Background = isCurrent
            && this.TryFindResource("ChromeBgElevBrush", out object? res) && res is IBrush brush
            ? brush
            : null;
    }
}
