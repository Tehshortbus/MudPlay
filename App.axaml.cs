using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MudPlay.Services;
using MudPlay.ViewModels;
using MudPlay.Views;

namespace MudPlay;

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

        // Pre-build the system-font catalogue off the UI thread so the first
        // Settings open doesn't stall while enumerating installed fonts. The counts
        // are logged so a "my font isn't in the list" report can be reasoned about
        // without re-running the scan.
        System.Threading.Tasks.Task.Run(() =>
        {
            InstalledFontCatalog.Warm();
            AppServices.Current.Log.Info("Fonts",
                $"{InstalledFontCatalog.Monospace.Count} monospace system fonts for the terminal picker, "
                + $"{InstalledFontCatalog.Text.Count} text fonts for the Conversation picker; "
                + InstalledFontCatalog.Source);
        });

        // On classic desktop platforms (Windows / Linux / macOS) the
        // lifetime exposes a MainWindow slot — fill it with our window
        // and a fresh view-model.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel mainViewModel = new();
            MainWindow mainWindow = new()
            {
                DataContext = mainViewModel,
            };
            desktop.MainWindow = mainWindow;

            // Every other window's title ends with the main window's own tail
            // ("<profile> — <bbs>:<realm>"), kept current as the profile or BBS changes.
            MudPlay.Controls.WindowTitleTail.Install(mainWindow, () => mainViewModel.TitleTail);
            mainViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.WindowTitle))
                    MudPlay.Controls.WindowTitleTail.Refresh();
            };

            // Keep typing directed at the terminal when any OTHER window is
            // focused — an app-wide class handler covers every window (dialogs +
            // directly-shown ones); the main window is excluded so its own input
            // is untouched.
            MudPlay.Controls.DialogKeyboardFallthrough.Install(mainWindow);

            // DialogService parents every modeless dialog to the main window so
            // closing main tears down its children.
            AppServices.Current.Dialogs.SetMainWindow(mainWindow);

            // Per-record edit dialogs — Messages tab + Monsters tab.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.MessageEditDialogViewModel,
                MudPlay.Views.GameData.Edit.MessageEditDialog>();
            // Messages tab "Compare with seed…" — put edited messages back to the seed.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.MessageSeedCompareDialogViewModel,
                MudPlay.Views.GameData.Edit.MessageSeedCompareDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.MonsterEditDialogViewModel,
                MudPlay.Views.GameData.Edit.MonsterEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.ItemEditDialogViewModel,
                MudPlay.Views.GameData.Edit.ItemEditDialog>();
            // Batch-edit dialogs — apply chosen fields to many selected rows at once.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Batch.MonsterBatchEditDialogViewModel,
                MudPlay.Views.GameData.Batch.MonsterBatchEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Batch.ItemBatchEditDialogViewModel,
                MudPlay.Views.GameData.Batch.ItemBatchEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Batch.PlayerBatchEditDialogViewModel,
                MudPlay.Views.GameData.Batch.PlayerBatchEditDialog>();
            // Interactive room-detail popup — Rooms tab double-click + Monsters
            // tab room chips. Clickable title/exits centre the Nav map and monster
            // names jump to their record; blacklisting stays on the map's
            // right-click menu.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.RoomDetailDialogViewModel,
                MudPlay.Views.GameData.Edit.RoomDetailDialog>();
            // Quest Flags tab double-click — one flag's script lines laid out as steps.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.QuestFlagStepsViewModel,
                MudPlay.Views.GameData.QuestFlagStepsWindow>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.PlayerEditDialogViewModel,
                MudPlay.Views.GameData.Edit.PlayerEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.PlayerAddDialogViewModel,
                MudPlay.Views.GameData.Edit.PlayerAddDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.MacroEditDialogViewModel,
                MudPlay.Views.GameData.Edit.MacroEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.TriggerEditDialogViewModel,
                MudPlay.Views.GameData.Edit.TriggerEditDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.TriggerWildcardsViewModel,
                MudPlay.Views.GameData.Edit.TriggerWildcardsDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameData.Edit.AliasEditDialogViewModel,
                MudPlay.Views.GameData.Edit.AliasEditDialog>();

            // Bosses tab dialogs — the mark-timer picker and the boss-list editor.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.CharacterWorkshop.MarkTimerDialogViewModel,
                MudPlay.Views.CharacterWorkshop.MarkTimerDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.CharacterWorkshop.ManageBossesDialogViewModel,
                MudPlay.Views.CharacterWorkshop.ManageBossesDialog>();
            // @timer sync merge window.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.CharacterWorkshop.BossTimerSyncViewModel,
                MudPlay.Views.CharacterWorkshop.BossTimerSyncWindow>();

            // Boss-room picker — shown when a double-clicked boss has multiple rooms.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.CharacterWorkshop.BossRoomPickerDialogViewModel,
                MudPlay.Views.CharacterWorkshop.BossRoomPickerDialog>();

            // Per-action keybind rebind dialog — opened from any
            // toolbar button or menu item that owns a BuiltInAction.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Keybind.KeybindEditDialogViewModel,
                MudPlay.Views.Keybind.KeybindEditDialog>();

            // Settings.Events tab's per-row editor.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Settings.EventEditDialogViewModel,
                MudPlay.Views.Settings.EventEditDialog>();

            // Folder name prompt — New / Rename folder on both the Manage
            // dialog (loops + lairs) and the rail (gotos).
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.NavFolderNameDialogViewModel,
                MudPlay.Views.Navigation.NavFolderNameDialog>();

            // Add-favourite room picker — the Manage dialog's Go To tab "Add"
            // button (search by name or map/room, then save as a favourite).
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.AddFavoriteDialogViewModel,
                MudPlay.Views.Navigation.AddFavoriteDialog>();

            // Full favourite editor — the Go To tab's Edit button (name + map +
            // room, so a favourite can be re-pointed at a different room).
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.FavoriteEditDialogViewModel,
                MudPlay.Views.Navigation.FavoriteEditDialog>();

            // Folder picker — the Go To tab / rail "Move to folder…" action lists
            // existing folders to choose from instead of typing a path.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.FolderPickerDialogViewModel,
                MudPlay.Views.Navigation.FolderPickerDialog>();

            // Profile pick / name dialogs — custom modeless dialogs in place of
            // the platform file pickers (the per-folder layout means profiles
            // live as subfolders, not flat .json files).
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Profile.ProfilePickerDialogViewModel,
                MudPlay.Views.Profile.ProfilePickerDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Profile.ProfileNameInputDialogViewModel,
                MudPlay.Views.Profile.ProfileNameInputDialog>();
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Profile.MegaMudImportDialogViewModel,
                MudPlay.Views.Profile.MegaMudImportDialog>();

            // Room-name learned prompt — fires when the tracker adopts
            // a name for a previously-unnamed map-15 ganghouse room.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.RoomNameLearnedDialogViewModel,
                MudPlay.Views.RoomNameLearnedDialog>();

            // Unknown-entity fix dialog, opened by double-clicking a
            // RoomEntityClassifier Warn row for an Also-Here name it
            // couldn't resolve. It returns the user's choice (flavor
            // prefix / player observation / monster), which the detail
            // handler below commits.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.UnknownEntityFixDialogViewModel,
                MudPlay.Views.UnknownEntityFixDialog>();

            // Modify Blacklist… (Game Data menu) — staged editor
            // over the per-BBS room blacklist.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.BlacklistEditorDialogViewModel,
                MudPlay.Views.BlacklistEditorDialog>();

            // Modify avoid/stash rooms… (Game Data menu) — staged editor over the
            // per-character avoided + stash room sets.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.AvoidRoomsEditorDialogViewModel,
                MudPlay.Views.AvoidRoomsEditorDialog>();

            // Add party buff (Party window buff panel) — pick a buff + recast timer.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.AddBuffDialogViewModel,
                MudPlay.Views.AddBuffDialog>();

            // Manage Game Data… (Game Data menu) — copy/move a set's loop
            // library between sets, or delete a set (tables + loops).
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.GameDataManagerViewModel,
                MudPlay.Views.GameDataManagerDialog>();

            // Quest editor (Player Workshop → Quest Status → "Edit
            // Quests…"). Names / show-hides / annotates quest steps;
            // writes the active set's {set}/quests.json overlay.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.CharacterWorkshop.QuestEditorViewModel,
                MudPlay.Views.CharacterWorkshop.QuestEditorWindow>();

            // Death-log viewer (Player Workshop → Death Recovery → "How did
            // I Die?"). Read-only replay of the backscroll snapshot captured at
            // the moment of a recorded death.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.CharacterWorkshop.DeathLogViewModel,
                MudPlay.Views.CharacterWorkshop.DeathLogWindow>();

            // The Item Finder's Configure Estimates window (Player Workshop → My
            // Equipment → Item Finder → Gear Finder panel).
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.CharacterWorkshop.ItemFinderEstimatesViewModel,
                MudPlay.Views.CharacterWorkshop.ItemFinderEstimatesWindow>();


            // Right-click → "Center on…" — two-int (map / room) input
            // that returns a RoomKey for the Navigation window to
            // rebuild its layout around.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.ManualCenterDialogViewModel,
                MudPlay.Views.ManualCenterDialog>();

            // Terminal right-click → "Bug report…" — collects the user's
            // problem description; the client-state capture happens in the
            // MainWindow command at click time and is written to Desktop.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.BugReportDialogViewModel,
                MudPlay.Views.BugReportDialog>();

            // EngineRecoveryGate → "Lost — couldn't recover" info dialog.
            // Single OK button; pops on tier-3 backtrack exhaustion.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.LostRecoveryDialogViewModel,
                MudPlay.Views.Navigation.LostRecoveryDialog>();

            // Loops pane → right-click → "Edit…" opens this dialog.
            // Name / Notes / Steps (command-step CRUD; moves locked).
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.LoopEditorDialogViewModel,
                MudPlay.Views.Navigation.LoopEditorDialog>();

            // Auto-Lair Setups pane → right-click → "Edit…", the chip row's Save,
            // and Navigation Management's lair New / Edit all open this dialog.
            // Name / Notes / Marker list with per-marker respawn override.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.LairEditorDialogViewModel,
                MudPlay.Views.Navigation.LairEditorDialog>();

            // CURRENT NAV ✎ button on a marked-lair row → single-marker
            // timer-override editor. Mutates AutoLairManager directly;
            // result payload is only used by callers that care.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.LairTimerEditDialogViewModel,
                MudPlay.Views.Navigation.LairTimerEditDialog>();

            // Clicking a building-loop or running-loop row in the Navigation
            // rail → per-waypoint action editor (command, delay, rest /
            // attack flags).
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.WaypointActionEditDialogViewModel,
                MudPlay.Views.Navigation.WaypointActionEditDialog>();

            // Map right-click "Toggle: Roomba Room" (or the tab's Add Room box) →
            // Roomba Mode's rule-list editor for that room (category/slot rules +
            // catch-all flag).
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.GhRoomLabelPickerDialogViewModel,
                MudPlay.Views.Navigation.GhRoomLabelPickerDialog>();

            // Navigation Management (the Navigation rail's footer button) →
            // loops + auto-lair setups + GOTO favourites CRUD surface.
            // Modeless.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.NavigationManagerDialogViewModel,
                MudPlay.Views.Navigation.NavigationManagerDialog>();

            // MegaMUD .mp import review (the decoded file beside our translation).
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.MpImportReviewViewModel,
                MudPlay.Views.Navigation.MpImportReviewWindow>();

            // Free-vs-direct route picker — fires on a user-initiated walk when a
            // shorter route crosses an acquirable gate the crosser can't yet pass.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.RouteChoiceDialogViewModel,
                MudPlay.Views.Navigation.RouteChoiceDialog>();

            // Current-route "Details…" browse window — the full step plan for the
            // route the nav engine is executing, with each lair room's monsters.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Navigation.RouteDetailsDialogViewModel,
                MudPlay.Views.Navigation.RouteDetailsDialog>();

            // Settings → General → "Change data directory" confirm + execute dialog.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.Settings.DataDirectoryRelocateDialogViewModel,
                MudPlay.Views.Settings.DataDirectoryRelocateDialog>();

            // Generic "are you sure?" confirm dialog — owned by
            // ConfirmService, surfaced by the exit / hangup / save /
            // delete paths whose matching flag is on in Settings →
            // BBS's Display group.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.ConfirmDialogViewModel,
                MudPlay.Views.ConfirmDialog>();

            // "Which waiting events should run?" — asked when the master switch
            // comes back on after holding the event queue for a long spell.
            AppServices.Current.Dialogs.RegisterWindow<
                MudPlay.ViewModels.HeldEventsPromptViewModel,
                MudPlay.Views.HeldEventsPromptWindow>();

            // Register the LogPane double-click handler for the spell-
            // coverage auditor's summary entries. Opening reuses any
            // already-open window (single-instance) so repeated
            // double-clicks just focus the existing detail surface
            // instead of stacking new ones.
            MudPlay.Views.GameData.SpellCoverageReportWindow? coverageWindow = null;
            AppServices.Current.Log.RegisterDetailHandler(
                MudPlay.Services.SpellCoverageAuditor.LogSource,
                () =>
                {
                    if (coverageWindow is not null && coverageWindow.IsVisible)
                    {
                        coverageWindow.Activate();
                        return;
                    }
                    var vm = new MudPlay.ViewModels.GameData.SpellCoverageReportViewModel(
                        AppServices.Current.SpellCoverage);
                    coverageWindow = new MudPlay.Views.GameData.SpellCoverageReportWindow
                    {
                        DataContext = vm,
                    };
                    coverageWindow.Closed += (_, _) => coverageWindow = null;
                    coverageWindow.Show(desktop.MainWindow!);
                });

            // RoomEntityClassifier unknown-entity click-to-fix. The
            // classifier emits Warn rows with the raw "Also Here:" line
            // carried as LogEntry.Context and the unknown name quoted in
            // the Message. Double-clicking such a row opens the fix dialog,
            // and the chosen action is committed here: a player
            // observation, a placeholder monster record, or the leading
            // word added to the flavor-prefix vocabulary.
            AppServices.Current.Log.RegisterDetailHandler(
                MudPlay.Game.Combat.RoomEntityClassifier.LogCategory,
                async (entry) =>
                {
                    if (entry.Context is not { Length: > 0 } rawLine) return;
                    string unknownName = ParseUnknownNameFromMessage(entry.Message);
                    if (unknownName.Length == 0) return;

                    var vm = new MudPlay.ViewModels.UnknownEntityFixDialogViewModel(
                        rawLine, unknownName);
                    MudPlay.ViewModels.UnknownEntityFixResult? result =
                        await AppServices.Current.Dialogs
                            .OpenWindowAsync<
                                MudPlay.ViewModels.UnknownEntityFixDialogViewModel,
                                MudPlay.ViewModels.UnknownEntityFixResult?>(vm);
                    if (result is null) return;

                    switch (result.Action)
                    {
                        case MudPlay.ViewModels.UnknownEntityFixAction.AddPlayerObservation:
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

                        case MudPlay.ViewModels.UnknownEntityFixAction.AddAsMonster:
                            AddPlaceholderMonster(result.EntityName);
                            break;

                        case MudPlay.ViewModels.UnknownEntityFixAction.AddFlavorPrefix:
                            // Add the leading word to the active set's shared flavor
                            // vocabulary — every monster then resolves that adjective.
                            AddFlavorPrefixWord(result.EntityName);
                            break;
                    }
                });

            // A monster the classifier recognized only by stripping a leading word
            // that isn't in the flavor vocabulary. Its LogEntry.Context carries that
            // leading word; double-click adds it to the active set's flavor-prefix
            // vocabulary so the adjective resolves cleanly from then on.
            AppServices.Current.Log.RegisterDetailHandler(
                MudPlay.Game.Combat.RoomEntityClassifier.MissingFlavorCategory,
                (entry) =>
                {
                    if (entry.Context is { Length: > 0 } word)
                        AddFlavorPrefixWord(word);
                });

            // A wire line neither the Messages catalogue nor any registered Router
            // pattern recognized. LogEntry.Context already IS the exact raw
            // candidate text (unlike RoomClassifier above, there's no substring to
            // parse out of Message). Double-click opens the same edit dialog the
            // Messages tab itself uses, pre-seeded with the raw text — Save commits
            // a real record and resolves the candidate, Cancel leaves it pending.
            AppServices.Current.Log.RegisterDetailHandler(
                MudPlay.Game.MessageCandidateWatcher.LogCategory,
                async (entry) =>
                {
                    if (entry.Context is not { Length: > 0 } rawText) return;
                    string id = MudPlay.Models.GameData.MessageCandidateRecord.ComputeId(rawText);

                    MudPlay.Models.GameData.MessageRecord seed =
                        MudPlay.ViewModels.GameData.Edit.MessageCandidateCommit.BuildSeedRecord(
                            new(id, rawText, default, default, 0, false));
                    MudPlay.ViewModels.GameData.Edit.MessageEditDialogViewModel vm = new(
                        seed, AppServices.Current.Messages.Messages,
                        isNew: true, cache: AppServices.Current.GameData);
                    MudPlay.ViewModels.GameData.Edit.MessageEditResult? result =
                        await AppServices.Current.Dialogs
                            .OpenWindowAsync<MudPlay.ViewModels.GameData.Edit.MessageEditDialogViewModel,
                                MudPlay.ViewModels.GameData.Edit.MessageEditResult>(vm);
                    if (result is null) return;   // Cancel — candidate stays pending, no re-nag

                    MudPlay.ViewModels.GameData.Edit.MessageCandidateCommit.Commit(
                        AppServices.Current.Messages, AppServices.Current.MessageCandidates, result, id);
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

    // Insert a placeholder MonsterMessageRecord for an unknown name into the active
    // MonsterMessageStore so the room classifier recognizes the bare name from then on.
    // Links is null (no MDB row to bind yet). Skips when a record with the same
    // case-insensitive name already exists.
    private static void AddPlaceholderMonster(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return;

        MudPlay.Services.MonsterMessageStore store = AppServices.Current.MonsterMessages;
        foreach (MudPlay.Models.GameData.MonsterMessageRecord existing in store.Messages)
        {
            if (string.Equals(existing.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                AppServices.Current.Log.Warn("RoomClassifier",
                    $"Monster record for '{trimmed}' already exists — not adding a duplicate.");
                return;
            }
        }

        // Stable SHA1-derived id (first 8 bytes hex) keyed off the name, so the
        // record round-trips through Save / Load without churning the on-disk file.
        string id = ComputeMonsterMessageId(trimmed);
        MudPlay.Models.GameData.MonsterMessageRecord placeholder = new(
            Id:   id,
            Name: trimmed,
            Links: null);

        store.Upsert(placeholder);
        AppServices.Current.Log.Info("RoomClassifier",
            $"Added placeholder monster record: '{trimmed}' — the room classifier now " +
            "recognizes this name.");
    }

    // Stable id for a placeholder record — SHA1 of the name, first 8 bytes as hex.
    private static string ComputeMonsterMessageId(string name)
    {
        string blob = name;
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(blob));
        System.Text.StringBuilder sb = new(16);
        for (int i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // Add the leading whitespace token of entry to the active game-data set's
    // flavor-prefix vocabulary (Services.FlavorPrefixStore). Called from the
    // unknown-entity fix dialog's "flavor prefix" action (entry may be a full
    // multi-word occupant name) and the missing-flavor log-row double-click
    // (entry is already the single leading word). Both want just the first word.
    private static void AddFlavorPrefixWord(string entry)
    {
        string word = (entry ?? string.Empty).Trim();
        int sp = word.IndexOf(' ');
        if (sp > 0) word = word[..sp];
        if (word.Length == 0) return;

        if (AppServices.Current.FlavorPrefixes.Add(word))
            AppServices.Current.Log.Info("RoomClassifier",
                $"Added '{word}' to the flavor-prefix vocabulary for set " +
                $"'{AppServices.Current.FlavorPrefixes.ActiveSet ?? "(none)"}'.");
        else
            AppServices.Current.Log.Info("RoomClassifier",
                $"'{word}' is already in the flavor-prefix vocabulary.");
    }
}
