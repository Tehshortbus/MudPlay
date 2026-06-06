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
                FujinTerm.ViewModels.GameData.Edit.ItemEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.ItemEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.PlayerEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.PlayerEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.PlayerAddDialogViewModel,
                FujinTerm.Views.GameData.Edit.PlayerAddDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.MacroEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.MacroEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.TriggerEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.TriggerEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.AliasEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.AliasEditDialog>();

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

            // Room-name learned prompt — fires when the tracker adopts
            // a name for a previously-unnamed map-15 ganghouse room.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.RoomNameLearnedDialogViewModel,
                FujinTerm.Views.RoomNameLearnedDialog>();

            // Modify Room Blacklist (Game Data menu) — staged editor
            // over the per-BBS room blacklist.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.BlacklistEditorDialogViewModel,
                FujinTerm.Views.BlacklistEditorDialog>();

            // Right-click → "Center on…" — two-int (map / room) input
            // that returns a RoomKey for the Navigation window to
            // rebuild its layout around.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.ManualCenterDialogViewModel,
                FujinTerm.Views.ManualCenterDialog>();

            // EngineRecoveryGate → "Lost — couldn't recover" info dialog.
            // Single OK button; pops on tier-3 backtrack exhaustion.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Navigation.LostRecoveryDialogViewModel,
                FujinTerm.Views.Navigation.LostRecoveryDialog>();

            // Loops pane → right-click → "Edit…" opens this dialog.
            // Name / Notes / Steps (command-step CRUD; moves locked).
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Navigation.LoopEditorDialogViewModel,
                FujinTerm.Views.Navigation.LoopEditorDialog>();

            // Auto-Lair Setups pane → right-click → "Edit…" + "Save lairs"
            // bottom-strip button both route through this dialog. Name /
            // Notes / Marker list with per-marker respawn override.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Navigation.LairEditorDialogViewModel,
                FujinTerm.Views.Navigation.LairEditorDialog>();

            // CURRENT NAV ✎ button on a marked-lair row → single-marker
            // timer-override editor. Mutates AutoLairManager directly;
            // result payload is only used by callers that care.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Navigation.LairTimerEditDialogViewModel,
                FujinTerm.Views.Navigation.LairTimerEditDialog>();

            // Loop editor ✎ button on a waypoint row → per-waypoint
            // command + delay editor. Payload routes back through the
            // Loop editor's apply path.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Navigation.WaypointActionEditDialogViewModel,
                FujinTerm.Views.Navigation.WaypointActionEditDialog>();

            // Navigation → "Manage" chip → loops + auto-lair markers
            // CRUD surface. Modeless; replaces the bottom-strip
            // save/discard/name textbox UX with a dedicated window.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Navigation.NavigationManagerDialogViewModel,
                FujinTerm.Views.Navigation.NavigationManagerDialog>();

            // .mp importer disambiguation prompt — only fires when
            // multiple candidate rooms tie on the closure score.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Navigation.MpAnchorPickerDialogViewModel,
                FujinTerm.Views.Navigation.MpAnchorPickerDialog>();

            // Settings → General → "Change data directory" confirm + execute dialog.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Settings.DataDirectoryRelocateDialogViewModel,
                FujinTerm.Views.Settings.DataDirectoryRelocateDialog>();

            // Generic "are you sure?" confirm dialog — owned by
            // ConfirmService, surfaced by the exit / hangup / save /
            // delete paths whose matching flag is on in Settings →
            // BBS's Display group.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.ConfirmDialogViewModel,
                FujinTerm.Views.ConfirmDialog>();

            // Register the LogPane double-click handler for the spell-
            // coverage auditor's summary entries. Opening reuses any
            // already-open window (single-instance) so repeated
            // double-clicks just focus the existing detail surface
            // instead of stacking new ones.
            FujinTerm.Views.GameData.SpellCoverageReportWindow? coverageWindow = null;
            AppServices.Current.Log.RegisterDetailHandler(
                FujinTerm.Services.SpellCoverageAuditor.LogSource,
                () =>
                {
                    if (coverageWindow is not null && coverageWindow.IsVisible)
                    {
                        coverageWindow.Activate();
                        return;
                    }
                    var vm = new FujinTerm.ViewModels.GameData.SpellCoverageReportViewModel(
                        AppServices.Current.SpellCoverage);
                    coverageWindow = new FujinTerm.Views.GameData.SpellCoverageReportWindow
                    {
                        DataContext = vm,
                    };
                    coverageWindow.Closed += (_, _) => coverageWindow = null;
                    coverageWindow.Show(desktop.MainWindow!);
                });
        }

        base.OnFrameworkInitializationCompleted();
    }
}
