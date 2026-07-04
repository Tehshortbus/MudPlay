using Avalonia.Controls;
using FujinTerm.Services;

namespace FujinTerm.Views.GameData.Edit;

public partial class ItemEditDialog : Window
{
    // Stable id under which the editable/MDB column split persists in
    // CharacterProfile.SplitterRatios.
    private const string SplitterId = "ItemEditDialog";

    public ItemEditDialog()
    {
        InitializeComponent();

        AppServices.Current.SplitterLayouts.AttachGrid(
            owner:            this,
            grid:             PanesGrid,
            leftColumnIndex:  0,
            rightColumnIndex: 2,
            id:               SplitterId);
    }
}
