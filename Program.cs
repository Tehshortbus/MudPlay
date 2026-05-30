using Avalonia;

namespace FujinTerm;

// Entry point. Boots the Avalonia application and hands off to App.
internal static class Program
{
    // Avalonia requires a single-threaded apartment on Windows for COM
    // interop (clipboard, drag/drop, native dialogs). Marking Main with
    // [STAThread] is what guarantees that.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Builds the Avalonia configuration. The XAML previewer in IDEs also
    // calls this method by reflection, so its signature must stay stable.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()       // Pick the right backend (X11, Win32, ...).
#if DEBUG
            .WithDeveloperTools()      // F12 inspector in debug builds.
#endif
            .WithInterFont()           // Fall-back UI font for chrome/text boxes.
            .LogToTrace();             // Route Avalonia diagnostics to the trace listener.
}
