using Avalonia.Controls;
using FujinTerm.Services;

namespace FujinTerm.Views.GameData.Edit;

public partial class ItemEditDialog : Window
{
    // Stable id under which the editable/MDB column split persists in
    // CharacterProfile.SplitterRatios. Suffixed "Panes" so the narrower
    // default (splitter at the editable side's right edge) takes effect —
    // an older key holds the previous wide-left ratio that would otherwise
    // mask it on restore.
    private const string SplitterId = "ItemEditDialogPanes";

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
