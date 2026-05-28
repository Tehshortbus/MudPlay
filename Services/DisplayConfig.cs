using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.Services;

/// <summary>
/// Live, observable mirror of the currently-loaded
/// <see cref="Models.Profile.DisplaySettings"/>. The Settings → Display
/// section writes into this; the main terminal binds its
/// <c>FontSize</c> to it so font changes apply without reopening
/// Settings. <see cref="ScrollbackLines"/> is persisted here but the
/// underlying <see cref="Terminal.ScrollbackBuffer"/> is sized at
/// startup — runtime changes take effect on next launch.
/// </summary>
public sealed partial class DisplayConfig : ObservableObject
{
    [ObservableProperty] private double _fontSize = 16.0;
    [ObservableProperty] private int _scrollbackLines = 10_000;
}
