using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Models.Profile;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// Row view-model for one entry in the BBS section's "Menu nav after login"
/// editor. Wraps a <see cref="MenuStep"/> with INotifyPropertyChanged so the
/// table TextBoxes / ComboBox / NumericUpDown all two-way bind cleanly, and
/// any edit dirties the parent section via the <c>onDirty</c> callback.
/// </summary>
public sealed partial class MenuStepEditorViewModel : ObservableObject
{
    private readonly Action _onDirty;

    [ObservableProperty] private string _waitForPattern = string.Empty;
    [ObservableProperty] private MenuStepMatchType _matchType = MenuStepMatchType.Literal;
    [ObservableProperty] private string _send = string.Empty;
    [ObservableProperty] private int _timeoutSeconds = 15;

    public MenuStepEditorViewModel(Action onDirty)
    {
        ArgumentNullException.ThrowIfNull(onDirty);
        _onDirty = onDirty;
    }

    public MenuStep ToModel() => new()
    {
        WaitForPattern = WaitForPattern,
        MatchType = MatchType,
        Send = Send,
        TimeoutSeconds = TimeoutSeconds,
    };

    public static MenuStepEditorViewModel FromModel(MenuStep model, Action onDirty)
    {
        ArgumentNullException.ThrowIfNull(model);
        MenuStepEditorViewModel vm = new(onDirty)
        {
            WaitForPattern = model.WaitForPattern,
            MatchType = model.MatchType,
            Send = model.Send,
            TimeoutSeconds = model.TimeoutSeconds,
        };
        return vm;
    }

    partial void OnWaitForPatternChanged(string value) => _onDirty();
    partial void OnMatchTypeChanged(MenuStepMatchType value) => _onDirty();
    partial void OnSendChanged(string value) => _onDirty();
    partial void OnTimeoutSecondsChanged(int value) => _onDirty();
}
