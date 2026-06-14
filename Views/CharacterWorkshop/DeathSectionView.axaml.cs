using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class DeathSectionView : UserControl
{
    public DeathSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Double-clicking a death row opens the entry-detail panel for the
    /// selected record (same effect as the "View Entry" button).
    /// </summary>
    private void OnRecordDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DeathSectionViewModel vm && vm.ViewEntryCommand.CanExecute(null))
            vm.ViewEntryCommand.Execute(null);
    }
}
