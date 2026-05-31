using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels.GameData;

namespace FujinTerm.Views.GameData;

public partial class SpellCoverageReportWindow : Window
{
    public SpellCoverageReportWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SpellCoverageReportViewModel vm) vm.Detach();
    }
}
