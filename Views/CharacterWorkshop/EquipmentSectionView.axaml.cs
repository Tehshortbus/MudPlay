using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class EquipmentSectionView : UserControl
{
    public EquipmentSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // The slot AutoCompleteBox commits its Text on LostFocus (so typing doesn't
    // re-persist the set on every keystroke), but picking a suggestion from the
    // dropdown must refresh the Equipment Bonuses panel immediately — so push the
    // chosen item into the row's ItemName here rather than waiting for blur.
    private void OnSlotItemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is AutoCompleteBox { DataContext: EquipmentSlotRowViewModel row } box
            && box.SelectedItem is string picked)
        {
            row.ItemName = picked;
        }
    }
}
