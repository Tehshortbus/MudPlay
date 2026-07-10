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

            // Register the unified import-conflict dialog. Every importer
            // (MDB tables, MegaMUD spell messages, MegaMUD .mp paths,
            // favourites) routes its row-level conflicts through this one
            // window via DialogService.OpenWindowAsync.
            AppServices.Current.Dialogs.RegisterWindow<ImportConflictViewModel, ImportConflictWindow>();

            // Per-record edit dialogs — Messages tab + Monsters tab.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.MessageEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.MessageEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.MonsterEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.MonsterEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.ItemEditDialogViewModel,
                FujinTerm.Views.GameData.Edit.ItemEditDialog>();
            // Interactive room-detail popup — Rooms tab double-click + Monsters
            // tab room chips. Clickable title/exits centre the Nav map, monster
            // names jump to their record, Add/Remove toggle the blacklist.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameData.Edit.RoomDetailDialogViewModel,
                FujinTerm.Views.GameData.Edit.RoomDetailDialog>();
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

            // Settings.Events tab's per-row editor.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Settings.EventEditDialogViewModel,
                FujinTerm.Views.Settings.EventEditDialog>();

            // Favourite-room rename — Navigation GOTO pane pencil button.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Navigation.FavoriteRenameDialogViewModel,
                FujinTerm.Views.Navigation.FavoriteRenameDialog>();

            // Folder name prompt — New / Rename folder on both the Manage
            // dialog (loops + lairs) and the rail (gotos).
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.Navigation.NavFolderNameDialogViewModel,
                FujinTerm.Views.Navigation.NavFolderNameDialog>();

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

            // Unknown-entity fix dialog. RoomEntityClassifier opens this
            // when it emits a Warn row for an Also-Here name it can't
            // resolve to a Monster or a Player; the dialog collects the
            // user's intent (flavor prefix / player observation) and
            // returns it for the classifier to commit at the active
            // 4-tier scope.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.UnknownEntityFixDialogViewModel,
                FujinTerm.Views.UnknownEntityFixDialog>();

            // Modify Room Blacklist (Game Data menu) — staged editor
            // over the per-BBS room blacklist.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.BlacklistEditorDialogViewModel,
                FujinTerm.Views.BlacklistEditorDialog>();

            // Manage Sets… (Game Data menu) — copy/move a set's loop
            // library between sets, or delete a set (tables + loops).
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.GameDataManagerViewModel,
                FujinTerm.Views.GameDataManagerDialog>();

            // Quest editor (Character Workshop → Quest Status → "Edit
            // Quests…"). Names / show-hides / annotates quest steps;
            // writes the active set's {set}/quests.json overlay.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.CharacterWorkshop.QuestEditorViewModel,
                FujinTerm.Views.CharacterWorkshop.QuestEditorWindow>();

            // Item Finder (Character Workshop → Equipment Manager →
            // "Item Finder"). Read-only catalog of every equippable item in the
            // active set, grouped filters by class / level / alignment / stats.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.CharacterWorkshop.ItemFinderViewModel,
                FujinTerm.Views.CharacterWorkshop.ItemFinderWindow>();

            // Right-click → "Center on…" — two-int (map / room) input
            // that returns a RoomKey for the Navigation window to
            // rebuild its layout around.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.ManualCenterDialogViewModel,
                FujinTerm.Views.ManualCenterDialog>();

            // Terminal right-click → "Bug report…" — collects the user's
            // problem description; the client-state capture happens in the
            // MainWindow command at click time and is written to Desktop.
            AppServices.Current.Dialogs.RegisterWindow<
                FujinTerm.ViewModels.BugReportDialogViewModel,
                FujinTerm.Views.BugReportDialog>();

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

            // RoomClassifier unknown-entity click-to-fix. The classifier
            // emits Warn rows with the raw "Also Here:" line carried as
            // LogEntry.Context and the unknown name quoted in the Message.
            // Double-click on such a row opens the fix dialog. The dialog
            // returns Add-as-flavor-prefix / Add-as-player-observation /
            // Cancel; the handler commits the latter directly via
            // PlayerDatabase. The former just logs the intent for now —
            // picking the target monster needs a UI affordance (search +
            // selection) that hasn't shipped yet.
            AppServices.Current.Log.RegisterDetailHandler(
                FujinTerm.Game.Combat.RoomEntityClassifier.LogCategory,
                async (entry) =>
                {
                    if (entry.Context is not { Length: > 0 } rawLine) return;
                    string unknownName = ParseUnknownNameFromMessage(entry.Message);
                    if (unknownName.Length == 0) return;

                    var vm = new FujinTerm.ViewModels.UnknownEntityFixDialogViewModel(
                        rawLine, unknownName);
                    FujinTerm.ViewModels.UnknownEntityFixResult? result =
                        await AppServices.Current.Dialogs
                            .OpenWindowAsync<
                                FujinTerm.ViewModels.UnknownEntityFixDialogViewModel,
                                FujinTerm.ViewModels.UnknownEntityFixResult?>(vm);
                    if (result is null) return;

                    switch (result.Action)
                    {
                        case FujinTerm.ViewModels.UnknownEntityFixAction.AddPlayerObservation:
                            if (AppServices.Current.Players.AddManual(
                                    result.EntityName, familyName: string.Empty,
                                    nowUtc: DateTime.UtcNow))
                            {
                                AppServices.Current.Log.Info("RoomClassifier",
                                    $"Added player observation: {result.EntityName}");
                            }
                            else
                            {
                                AppServices.Current.Log.Warn("RoomClassifier",
                                    $"Player observation for '{result.EntityName}' already exists.");
                            }
                            break;

                        case FujinTerm.ViewModels.UnknownEntityFixAction.AddAsMonster:
                            AddPlaceholderMonster(result.EntityName);
                            break;

                        case FujinTerm.ViewModels.UnknownEntityFixAction.AddFlavorPrefix:
                            // Not yet wired: open a monster picker, then attach
                            // the prefix to the selected MonsterMessageRecord.
                            AppServices.Current.Log.Info("RoomClassifier",
                                $"Flavor-prefix add for '{result.EntityName}' " +
                                "queued — monster-picker UI ships in a follow-up PR.");
                            break;
                    }
                });
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Extract the single-quoted name from a RoomClassifier Warn message of
    // the form "unknown entity 'foozle' — double-click to fix". Returns
    // empty when the message doesn't carry a quoted name — caller treats
    // that as "nothing to fix".
    private static string ParseUnknownNameFromMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        int open = message.IndexOf('\'');
        if (open < 0) return string.Empty;
        int close = message.IndexOf('\'', open + 1);
        if (close <= open) return string.Empty;
        return message.Substring(open + 1, close - open - 1);
    }

    // Insert a placeholder MonsterMessageRecord for an unknown name into the
    // active MonsterMessageStore. AllowNoPrefix is true (the name alone is
    // the matchable form); every line list is empty (the user fills them in
    // later via the Monsters tab editor); Links is null (no MDB row to bind
    // yet). Skips when a record with the same case-insensitive name already
    // exists.
    private static void AddPlaceholderMonster(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return;

        FujinTerm.Services.MonsterMessageStore store = AppServices.Current.MonsterMessages;
        foreach (FujinTerm.Models.GameData.MonsterMessageRecord existing in store.Messages)
        {
            if (string.Equals(existing.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                AppServices.Current.Log.Warn("RoomClassifier",
                    $"Monster record for '{trimmed}' already exists — not adding a duplicate.");
                return;
            }
        }

        // Id derivation matches the Monsters-tab editor's ComputeId
        // scheme (SHA1 of name + canonical content blob, first 8 bytes
        // hex). Empty list-content + AllowNoPrefix=true still produces
        // a stable id so the record round-trips through Save / Load
        // without churning the on-disk file.
        string id = ComputeMonsterMessageId(trimmed);
        FujinTerm.Models.GameData.MonsterMessageRecord placeholder = new(
            Id:               id,
            Name:             trimmed,
            HitYou:           Array.Empty<string>(),
            HitOther:         Array.Empty<string>(),
            DeathLine:        Array.Empty<string>(),
            ArmorBlockYou:    Array.Empty<string>(),
            ArmorBlockOther:  Array.Empty<string>(),
            DodgeYou:         Array.Empty<string>(),
            DodgeOther:       Array.Empty<string>(),
            MissYou:          Array.Empty<string>(),
            MissOther:        Array.Empty<string>(),
            FlavorPrefixes:   Array.Empty<string>(),
            AllowNoPrefix:    true,
            Links:            null);

        store.Upsert(placeholder);
        AppServices.Current.Log.Info("RoomClassifier",
            $"Added placeholder monster record: '{trimmed}'. " +
            "Open Game Data → Monsters to fill in hit / death / dodge lines.");
    }

    // Mirror of MonsterEditDialogViewModel.ComputeId for the minimum case
    // (empty content + AllowNoPrefix=true). Kept inline rather than calling
    // the VM method to avoid coupling App startup to the Game Data editor
    // namespace.
    private static string ComputeMonsterMessageId(string name)
    {
        string blob = name + "||1";
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(blob));
        System.Text.StringBuilder sb = new(16);
        for (int i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
