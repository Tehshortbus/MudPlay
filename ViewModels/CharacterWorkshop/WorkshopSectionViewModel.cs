using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.ViewModels.CharacterWorkshop;

// Base for one tab in the CharacterWorkshopViewModel's flat tab strip: Character
// Info / Death Recovery / Level Projection / CP Allocation / Quest Status /
// Equipment Manager.
public abstract class WorkshopSectionViewModel : ObservableObject, IDisposable
{
    // Stable identifier — tab selection persists across reopens against this.
    public abstract string Id { get; }

    // Display title on the tab.
    public abstract string Title { get; }

    // The content UserControl rendered under the tab. Constructed lazily on first access.
    public abstract Control View { get; }

    // Detach from any long-lived service events the section subscribed to. The Workshop
    // window is recreated on every open, so its sections must unsubscribe on close or
    // each open/close cycle leaks one set of them. Base is a no-op; sections that
    // subscribe override this. Called by CharacterWorkshopViewModel.Dispose when the
    // window closes.
    public virtual void Dispose() { }
}
