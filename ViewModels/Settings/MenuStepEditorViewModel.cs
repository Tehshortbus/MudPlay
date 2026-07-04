using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.Settings;

// Row view-model for one entry in the BBS section's "Menu nav after login"
// editor. Wraps a MenuStep with INotifyPropertyChanged so the table cells two-way
// bind cleanly, and any edit dirties the parent section via the onDirty callback.
public sealed partial class MenuStepEditorViewModel : ObservableObject
{
    private readonly Action _onDirty;

    [ObservableProperty] private string _waitForPattern = string.Empty;
    [ObservableProperty] private string _send = string.Empty;

    public MenuStepEditorViewModel(Action onDirty)
    {
        ArgumentNullException.ThrowIfNull(onDirty);
        _onDirty = onDirty;
    }

    public MenuStep ToModel() => new()
    {
        WaitForPattern = WaitForPattern,
        Send = Send,
    };

    public static MenuStepEditorViewModel FromModel(MenuStep model, Action onDirty)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new MenuStepEditorViewModel(onDirty)
        {
            WaitForPattern = model.WaitForPattern,
            Send = model.Send,
        };
    }

    partial void OnWaitForPatternChanged(string value) => _onDirty();
    partial void OnSendChanged(string value) => _onDirty();
}
