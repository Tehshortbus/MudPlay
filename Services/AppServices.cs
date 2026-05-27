namespace FujinTerm.Services;

/// <summary>
/// Lightweight singleton service holder. POCO — no DI container.
/// Every cross-cutting service the app owns is exposed as an instance property
/// here as it's introduced by later phase PRs (profile/settings I/O, message
/// bus, dialog spawner, log service, importers, game-data cache, etc.).
/// </summary>
/// <remarks>
/// Per-character / per-game-data lifetime is event-driven: services subscribe
/// to <c>ProfileService.ProfileLoaded</c> (added in PR 0.3) and
/// <c>GameDataCache.ActiveSetChanged</c> (Phase 5) and reload their per-scope
/// state in those handlers. There is intentionally no IoC container —
/// explicit subscription and explicit teardown beats magic resolution at this
/// scale (see CLAUDE.md "Architecture rules").
/// </remarks>
public sealed class AppServices
{
    private static AppServices? _current;

    /// <summary>The active service holder. <see cref="Initialize"/> must be called first.</summary>
    public static AppServices Current => _current
        ?? throw new InvalidOperationException(
            "AppServices not initialized — call AppServices.Initialize() during app startup.");

    /// <summary>Owns <c>Data/Global/global.json</c> — the Global settings tier.</summary>
    public SettingsService Settings { get; }

    /// <summary>Owns the currently loaded character profile (Character tier).</summary>
    public ProfileService Profile { get; }

    /// <summary>Owns <c>Data/BBS/*.json</c> — the BBS tier.</summary>
    public BbsProfileStore Bbs { get; }

    /// <summary>
    /// Single read / write API for the 4-tier settings + game-data override
    /// hierarchy (Defaults → Global → BBS → Character).
    /// </summary>
    public SettingsResolver Resolver { get; }

    /// <summary>Modeless-only window spawner (no <c>ShowDialog</c> wrapper).</summary>
    public DialogService Dialogs { get; }

    /// <summary>App-wide severity-tagged ring-buffer log. Status bar + Phase 1 log pane subscribe.</summary>
    public LogService Log { get; }

    /// <summary>
    /// Construct and register the singleton. Idempotent — repeated calls return
    /// the existing instance. Touches <see cref="AppPaths"/> to force
    /// directory creation before any service tries to read or write a file.
    /// </summary>
    public static AppServices Initialize()
    {
        if (_current is not null) return _current;

        // Read any AppPaths member to fire its static constructor and create
        // the Data/ tree on disk before anyone else needs it.
        _ = AppPaths.DataRoot;

        _current = new AppServices();
        return _current;
    }

    private AppServices()
    {
        Settings = new SettingsService();
        Profile = new ProfileService();
        Bbs = new BbsProfileStore();

        // Resolver subscribes to Profile events for active-BBS tracking; build
        // it before Load() below so it catches the auto-load's ProfileLoaded
        // (it also self-syncs from Profile.Current as a defensive fallback).
        Resolver = new SettingsResolver(Settings, Bbs, Profile);

        Dialogs = new DialogService();
        Log = new LogService();

        // Auto-load the most recently used profile if one is recorded and the
        // file still exists. First-launch (no recorded profile) leaves
        // Profile.Current null; the user picks or creates from the menu.
        string? last = Settings.Current.LastUsedProfileName;
        if (!string.IsNullOrWhiteSpace(last) &&
            File.Exists(AppPaths.CharacterProfileFile(last)))
        {
            Profile.Load(last);
        }

        // Track which profile was last loaded so the next launch can reopen it.
        Profile.ProfileLoaded += OnProfileLoaded;
    }

    private void OnProfileLoaded(Models.Profile.CharacterProfile profile)
    {
        if (Profile.CurrentProfileName is null) return;
        if (Settings.Current.LastUsedProfileName == Profile.CurrentProfileName) return;

        Settings.Current.LastUsedProfileName = Profile.CurrentProfileName;
        Settings.Save();
    }
}
