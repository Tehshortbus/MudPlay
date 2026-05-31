using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FujinTerm.Services;
using FujinTerm.ViewModels;
using FujinTerm.ViewModels.Import;
using FujinTerm.Views;
using FujinTerm.Views.Import;

namespace FujinTerm;

// The root Avalonia "Application" object. Loads the XAML and creates the
// main window when the framework finishes initializing.
public partial class App : Application
{
    // Loads the matching App.axaml file into this instance.
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Bring up cross-cutting services before any window or view-model
        // exists; later code reaches them via AppServices.Current.
        AppServices.Initialize();

        // On classic desktop platforms (Windows / Linux / macOS) the
        // lifetime exposes a MainWindow slot — fill it with our window
        // and a fresh view-model.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow mainWindow = new()
            {
                DataContext = new MainWindowViewModel(),
            };
            desktop.MainWindow = mainWindow;

            // DialogService parents every modeless dialog to the main window so
            // closing main tears down its children. FloatingPanelHost owns the
            // floating panel windows with the same parenting story.
            AppServices.Current.Dialogs.SetMainWindow(mainWindow);
            AppServices.Current.Panels.SetOwnerWindow(mainWindow);

            // Phase 5 PR 5.3 — register the unified import-conflict dialog.
            // Every importer (MDB tables, MegaMUD spell messages, MegaMUD
            // .mp paths, favourites) routes its row-level conflicts through
            // this one window via DialogService.OpenWindowAsync.
            AppServices.Current.Dialogs.RegisterWindow<ImportConflictViewModel, ImportConflictWindow>();

            // Phase 5 per-record edit dialogs — Messages tab + Monsters tab.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.MessageEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.MessageEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.MonsterEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.MonsterEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.PlayerEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.PlayerEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.MacroEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.MacroEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.TriggerEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.TriggerEditDialog>();

            // Per-action keybind rebind dialog — opened from any
            // toolbar button or menu item that owns a BuiltInAction.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Keybind.KeybindEditDialogViewModel,
                FujinTerm.Views.Keybind.KeybindEditDialog>();

            // File → Open profile / Save profile as — custom modeless dialogs
            // replacing the platform file pickers (the per-folder layout means
            // profiles live as subfolders, not flat .json files).
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Profile.ProfilePickerDialogViewModel,
                FujinTerm.Views.Profile.ProfilePickerDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Profile.ProfileNameInputDialogViewModel,
                FujinTerm.Views.Profile.ProfileNameInputDialog>();

            // Settings → General → "Change data directory" confirm + execute dialog.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Settings.DataDirectoryRelocateDialogViewModel,
                FujinTerm.Views.Settings.DataDirectoryRelocateDialog>();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
