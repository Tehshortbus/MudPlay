using Avalonia.Controls;
using FujinTerm.Services;

namespace FujinTerm.Views.GameData.Edit;

public partial class ItemEditDialog : Window
{
    /// <summary>
    /// Stable id under which the editable/MDB column split persists in
    /// <see cref="FujinTerm.Models.Profile.CharacterProfile.SplitterRatios"/>.
    /// </summary>
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
