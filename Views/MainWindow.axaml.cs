using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Reactive;
using Avalonia.Threading;
using FujinTerm.Models.Settings;
using FujinTerm.Services;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Wires the terminal control's user-input event to the view-model and
// re-focuses the terminal whenever a connection is established (so the user
// can start typing right away).
public partial class MainWindow : Window
{
    private TextBlock? _combatTickLabel;
    // Set once the user (or programmatic shutdown) has confirmed exit, so the second Close call sails through.
    private bool _exitConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        AppServices.Current.WindowLayouts.AttachWindow(this, "main");
        // Wire the keybinds from the per-character KeybindingStore so
        // they track the user's overrides. Lazily resolves the VM on
        // AttachedToLogicalTree because DataContext is set externally
        // by App.OnFrameworkInitializationCompleted.
        GlobalHotkeys.AttachMain(this);

        // Forward keystrokes captured by the terminal control to whatever
        // view-model is currently set as DataContext.
        Terminal.UserInput += bytes =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.SendUserInput(bytes);
        };
        // Local-line-edit buffer — printable keystrokes accumulate
        // client-side and only flush to the wire on Enter. Engine
        // auto-sends (par poll, AutoParty invite, @health round-trip)
        // can fire freely without interleaving into half-typed input.
        Terminal.InputBuffer = AppServices.Current.InputBuffer;

        // Feed the terminal-host viewport size into the control so its
        // ScaleToFit math can grow the font to fill the window. The control is
        // measured with infinite available size inside the ScrollViewer, so it
        // can't read the window size itself — this is the channel. The observable
        // fires the current bounds on subscribe, so the initial size is seeded.
        TerminalScroll.GetObservable(Visual.BoundsProperty)
            .Subscribe(new AnonymousObserver<Rect>(b => Terminal.ViewportSize = b.Size));

        // Subscribe to VM PropertyChanged so we can react to IsConnected.
        // Hooking via DataContextChanged covers the case where the VM is
        // swapped at runtime — even though today it's set once in App.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is INotifyPropertyChanged pc)
                pc.PropertyChanged += OnVmPropertyChanged;
            if (DataContext is MainWindowViewModel vm)
            {
                vm.GameDataSets.CollectionChanged += OnGameDataSetsChanged;
                RebuildGameDataMenu(vm);
                vm.HelpLinks.CollectionChanged += OnHelpLinksChanged;
                RebuildHelpMenu(vm);
            }
        };

        Opened += (_, _) =>
        {
            _combatTickLabel = this.FindControl<TextBlock>("CombatTickLabel");
            AppServices.Current.Tick.CombatTickElapsed += OnCombatTickElapsed;
        };
        Closed += (_, _) =>
        {
            AppServices.Current.Tick.CombatTickElapsed -= OnCombatTickElapsed;
        };

        // Confirm-exit prompt + auto-save the loaded profile before exit.
        //
        // When the user has "Confirm exit" turned on in Settings → BBS,
        // intercept the first Closing fire, cancel it, run the modeless
        // confirm dialog async, then re-issue Close() if the user said
        // yes. The _exitConfirmed latch makes the second Close skip the
        // prompt so we don't loop. App-initiated shutdowns (none today)
        // would set _exitConfirmed=true before calling Close.
        //
        // ProfileService.Save no-ops on blank drafts (no name on disk to
        // write to) and when nothing is loaded, so the only path that
        // hits disk is the common case: a named profile is open. Saves
        // the current in-memory state so any per-session edits (BBS
        // pin, settings tab changes, etc.) survive a relaunch without
        // requiring the user to remember Ctrl+S.
        Closing += async (_, e) =>
        {
            if (!_exitConfirmed && AppServices.Current.Confirm.Settings.ConfirmExit)
            {
                e.Cancel = true;
                bool ok = await AppServices.Current.Confirm.ConfirmExitAsync();
                if (!ok) return;
                _exitConfirmed = true;
                Close();
                return;
            }

            // Clean shutdown forgets the party we were following: a deliberate
            // quit must NOT auto-rejoin on next launch (only a crash, which
            // never runs this handler, leaves the memory populated). Clearing
            // before Save persists the forget.
            if (AppServices.Current.Profile.Current is { } profile)
                profile.PendingReconnectLeader = null;

            try { AppServices.Current.Profile.Save(); }
            catch (Exception ex)
            {
                AppServices.Current.Log.Error("Profile",
                    $"Auto-save on exit failed: {ex.Message}");
            }
        };
    }

    // Pulse the Tick status-bar label amber for a brief beat each time
    // TickEngine fires. Class is added immediately, removed after a 200 ms
    // dispatcher delay so the user gets a visual heartbeat.
    private void OnCombatTickElapsed()
    {
        if (_combatTickLabel is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            _combatTickLabel.Classes.Add("Pulsing");
            DispatcherTimer.RunOnce(
                () => _combatTickLabel.Classes.Remove("Pulsing"),
                TimeSpan.FromMilliseconds(200));
        });
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When we transition into "connected", move keyboard focus to the
        // terminal so typing goes to the BBS instead of the host textbox.
        if (e.PropertyName == nameof(MainWindowViewModel.IsConnected) &&
            DataContext is MainWindowViewModel vm && vm.IsConnected)
        {
            Terminal.Focus();
        }
    }

    private void OnGameDataSetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) RebuildGameDataMenu(vm);
    }

    private void OnHelpLinksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) RebuildHelpMenu(vm);
    }

    // Compose the Help menu: the disabled "Help topics…" placeholder, then one
    // launch item per user-editable website link (edited under Settings →
    // Toolbar + Shortcuts), then the active BBS's own site, then the static
    // Report / About actions. Same code-composition reason as the Game Data
    // menu — a MenuItem can't mix a bound dynamic list with inline static
    // children, so the whole list is assembled here on every HelpLinks change.
    private void RebuildHelpMenu(MainWindowViewModel vm)
    {
        HelpMenu.Items.Clear();

        HelpMenu.Items.Add(new MenuItem
        {
            Header    = "Help topics…",
            IsEnabled = false,
            [ToolTip.TipProperty] = "Old-school help browser lands in a later phase.",
        });
        HelpMenu.Items.Add(new Separator());

        foreach (HelpWebsite link in vm.HelpLinks)
        {
            HelpMenu.Items.Add(new MenuItem
            {
                Header          = $"{link.Label} ↗",
                Command         = vm.OpenHelpLinkCommand,
                CommandParameter = link.Url,
            });
        }

        // BBS site — bound live so its visibility + enable state + tooltip track
        // the active BBS without a menu rebuild (ShowBbsWebsiteInHelp /
        // BbsWebsiteUrl / HasBbsWebsite re-raise on every BBS pin change). The
        // per-BBS show/hide toggle drives IsVisible; the URL presence drives
        // IsEnabled.
        MenuItem bbsSite = new()
        {
            Header  = "BBS site ↗",
            Command = vm.OpenBbsWebsiteCommand,
        };
        bbsSite.Bind(MenuItem.IsVisibleProperty, new Binding(nameof(vm.ShowBbsWebsiteInHelp)) { Source = vm });
        bbsSite.Bind(MenuItem.IsEnabledProperty, new Binding(nameof(vm.HasBbsWebsite)) { Source = vm });
        bbsSite.Bind(ToolTip.TipProperty, new Binding(nameof(vm.BbsWebsiteUrl))
        {
            Source          = vm,
            FallbackValue   = "Set a Website URL on the active BBS (Settings → Toolbar + Shortcuts) to enable.",
            TargetNullValue = "Set a Website URL on the active BBS (Settings → Toolbar + Shortcuts) to enable.",
        });
        HelpMenu.Items.Add(bbsSite);

        HelpMenu.Items.Add(new Separator());
        HelpMenu.Items.Add(new MenuItem
        {
            Header  = "Report an issue…",
            Command = vm.ReportIssueCommand,
        });
        HelpMenu.Items.Add(new Separator());
        HelpMenu.Items.Add(new MenuItem
        {
            Header  = "About FujinTerm",
            Command = vm.OpenAboutCommand,
        });
    }

    // Compose the Game Data menu's items: every imported set on top
    // (each as a checkable MenuItem the user can click to activate),
    // a separator, then the static actions (Open Browser / Import .mdb
    // / Import loops). Avalonia's MenuItem can't mix ItemsSource-bound
    // dynamic children with inline static ones, so we assemble the
    // whole list in code on every change.
    private void RebuildGameDataMenu(MainWindowViewModel vm)
    {
        GameDataMenu.Items.Clear();

        foreach (GameDataSetMenuItem set in vm.GameDataSets)
        {
            GameDataMenu.Items.Add(new MenuItem
            {
                Header     = set.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked  = set.IsActive,
                Command    = set.SwitchCommand,
            });
        }

        if (vm.GameDataSets.Count > 0) GameDataMenu.Items.Add(new Separator());

        GameDataMenu.Items.Add(new MenuItem
        {
            Header       = "Open Browser…",
            InputGesture = new KeyGesture(Key.G, KeyModifiers.Control),
            Command      = vm.OpenGameDataBrowserCommand,
        });
        GameDataMenu.Items.Add(new Separator());
        GameDataMenu.Items.Add(new MenuItem
        {
            Header  = "Import .mdb…",
            Command = vm.ImportMdbCommand,
        });
        GameDataMenu.Items.Add(new MenuItem
        {
            Header  = "Import loops (MegaMUD .mp)…",
            Command = vm.ImportMegaMudLoopsCommand,
        });
        GameDataMenu.Items.Add(new MenuItem
        {
            Header  = "Manage Game Data…",
            Command = vm.OpenGameDataManagerCommand,
        });

        GameDataMenu.Items.Add(new Separator());
        GameDataMenu.Items.Add(new MenuItem
        {
            Header  = "Modify Blacklist…",
            Command = vm.OpenBlacklistEditorCommand,
        });
    }
}
