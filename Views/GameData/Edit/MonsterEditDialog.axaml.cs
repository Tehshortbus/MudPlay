using Avalonia.Controls;
using FujinTerm.Services;

namespace FujinTerm.Views.GameData.Edit;

public partial class MonsterEditDialog : Window
{
    /// <summary>
    /// Stable id under which the editable/MDB column split persists in
    /// <see cref="FujinTerm.Models.Profile.CharacterProfile.SplitterRatios"/>.
    /// </summary>
    private const string SplitterId = "MonsterEditDialog";

    public MonsterEditDialog()
    {
        InitializeComponent();

        // Per-profile splitter memory — restores the left/right column
        // ratio on Opened, captures on Closing. Indexes 0 and 2 are
        // the two * star-width pane columns; index 1 is the fixed
        // GridSplitter column.
        AppServices.Current.SplitterLayouts.AttachGrid(
            owner:            this,
            grid:             PanesGrid,
            leftColumnIndex:  0,
            rightColumnIndex: 2,
            id:               SplitterId);
    }
}
