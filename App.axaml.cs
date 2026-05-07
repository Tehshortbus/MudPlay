using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels;
using FujinTerm.Views;

namespace FujinTerm;

// The root Avalonia "Application" object. Loads the XAML and creates the
// main window when the framework finishes initializing.
public partial class App : Application
{
    // Loads the matching App.axaml file into this instance.
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // On classic desktop platforms (Windows / Linux / macOS) the
        // lifetime exposes a MainWindow slot — fill it with our window
        // and a fresh view-model.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
