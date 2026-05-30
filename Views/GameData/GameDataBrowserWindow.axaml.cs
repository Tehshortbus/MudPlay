using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views.GameData;

public partial class GameDataBrowserWindow : Window
{
    public GameDataBrowserWindow()
    {
        InitializeComponent();
        // Browser VMs subscribe to long-lived AppServices events
        // (GameDataCache.ActiveSetChanged, engine CollectionChanged).
        // Dispose detaches them so the VM tree — plus every cached
        // row collection and lazy-built per-section View — can GC.
        // Without this each reopen leaked ~90 MB of heap.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
