using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// The boards MudPlay comes with, Anomaly Mud and Paradigm, and the logon setups:
// theirs and the Stock one. Each board is added to the BBS list once per install.
// A board's characters get the setup its realm's game data names, Anomaly or
// Paradigm, else Stock, so a new player only types a username and password.
// No account, character or password ships: the steps send {username} / {password},
// which resolve from the character's own saved credentials at connect time.
public static class BuiltInBbses
{
    // Host is null for a setup that is only logon steps (Stock).
    public sealed record Preset(string Name, string? Host, int Port, string? WebsiteUrl, IReadOnlyList<MenuStep> LogonSteps)
    {
        // Same host and port, whatever the user named the board. Only keeps a board
        // from being added on top of one already listed: which setup a board gets
        // goes by its game data, since a board can move host or port.
        public bool IsBoardOf(BbsProfile bbs) =>
            Host is not null && bbs.Port == Port && string.Equals(bbs.Host.Trim(), Host, StringComparison.OrdinalIgnoreCase);

        // A fresh copy of the logon steps, for a character to own and edit.
        public List<MenuStep> CopyLogonSteps() =>
            LogonSteps.Select(s => new MenuStep { WaitForPattern = s.WaitForPattern, Send = s.Send }).ToList();

        // Whether steps are exactly this setup's, unedited.
        public bool HasSteps(IEnumerable<MenuStep> steps) =>
            steps.Select(s => (s.WaitForPattern, s.Send)).SequenceEqual(LogonSteps.Select(s => (s.WaitForPattern, s.Send)));
    }

    // Paradigm's character pick is left out: it is a slot on the player's own
    // account, so the login stops at the character list for them to choose.
    public static IReadOnlyList<Preset> All { get; } =
    [
        new("Anomaly", "playanomalymud.com", 2324, "https://playanomalymud.com",
        [
            new() { WaitForPattern = "Enter your BBS account name", Send = "{username}" },
            new() { WaitForPattern = "Enter your Password:", Send = "{password}" },
            new() { WaitForPattern = "Selection:", Send = "1" },
        ]),
        new("Paradigm", "paramud.mudinfo.net", 2323, null,
        [
            new() { WaitForPattern = "Please enter your username or ", Send = "{username}" },
            new() { WaitForPattern = "Please enter your password:", Send = "{password}" },
            new() { WaitForPattern = "P : Paradigm", Send = "P" },
            new() { WaitForPattern = "{ Realm Selection }", Send = "1" },
        ]),
    ];

    // Every other board is taken for a Stock one: the BBS login prompts every Stock
    // MajorMUD board shows (user, 2026-10-28), then `m` from the main menu. Only the
    // steps: a Stock board's host and port are the player's own.
    public static Preset Stock { get; } = new("Stock", null, 0, null,
    [
        new() { WaitForPattern = "Otherwise type \"new\":", Send = "{username}" },
        new() { WaitForPattern = "Enter your password:", Send = "{password}" },
        new() { WaitForPattern = "Main System Menu (TOP)", Send = "m" },
    ]);

    // Every setup the BBS tab's Apply setup menu offers.
    public static IReadOnlyList<Preset> Setups { get; } = [.. All, Stock];

    public static Preset? Named(string? name) =>
        Setups.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // The setup for a game data set: the board its data was exported for, when the
    // set's Info names Anomaly or Paradigm, else Stock.
    public static Preset ForGameDataSet(string? set) =>
        GameDataRealm.BoardName(set) is { } board
        && All.FirstOrDefault(p => string.Equals(p.Name, board, StringComparison.OrdinalIgnoreCase)) is { } preset
            ? preset : Stock;

    // The setup for a realm: its own game data set, or the global default set when
    // the realm uses that.
    public static Preset ForRealm(RealmProfile? realm, string? globalDefaultSet) =>
        ForGameDataSet(realm?.ActiveGameDataSet ?? globalDefaultSet);

    // As ForGameDataSet, or null when no such set is installed: with no data to go
    // by, steps already in place are left as they are.
    public static Preset? ForInstalledSet(string? set) =>
        set is not null && Directory.Exists(AppPaths.GameDataSetDir(set)) ? ForGameDataSet(set) : null;

    // The setup a board's logon steps were filled in from by the client, while
    // they're still exactly that setup's; null when the player set them.
    public static Preset? AutoFilled(BbsProfile board) =>
        board.LogonSteps is { } steps && Named(board.AutoLogonSetup) is { } setup && setup.HasSteps(steps) ? setup : null;

    // The logon steps connect walks for a character playing realm on board, and what
    // was decided, for the log (null when the board's saved steps are used as they
    // are). The character's own steps (login), when it has them, else the board's.
    // With none saved, the setup the realm's game data calls for. Saved steps the
    // client filled in itself, still unedited, give way to the setup installed data
    // calls for this character's realm; the board keeps its own, since another of
    // its realms may want them.
    public static (IReadOnlyList<MenuStep> Steps, string? Note) LogonFor(
        BbsProfile board, BbsCredentials? login, RealmProfile? realm, string? globalDefaultSet)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (login?.LogonSteps is { } own)
            return (own, "the character's own logon steps, not the board's");
        if (board.LogonSteps is not { } saved)
        {
            Preset setup = ForRealm(realm, globalDefaultSet);
            return (setup.LogonSteps, $"no logon steps saved for the board, so the {setup.Name} ones its realm's game data calls for");
        }
        if (AutoFilled(board) is { } filled
            && ForInstalledSet(realm?.ActiveGameDataSet ?? globalDefaultSet) is { } wanted && wanted != filled)
            return (wanted.LogonSteps, $"the {filled.Name} logon steps filled in for the board, unedited, give way to the {wanted.Name} ones this realm's game data calls for");
        return (saved, null);
    }

    // Add each preset not added before, and return the names added. A preset is
    // added once: one the user removed stays removed, and one whose name or board
    // is already in the list (a "Paramud" on Paradigm's host) is only marked done.
    // added is the install's record of presets already handled (GlobalSettings).
    public static IReadOnlyList<string> AddMissing(BbsProfileStore store, ICollection<string> added, IEnumerable<Preset> presets)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(presets);
        List<BbsProfile> existing = store.ListNames().Select(store.Peek).OfType<BbsProfile>().ToList();
        List<string> made = [];
        foreach (Preset preset in presets)
        {
            if (added.Contains(preset.Name, StringComparer.OrdinalIgnoreCase)) continue;
            added.Add(preset.Name);
            if (store.Exists(preset.Name) || existing.Any(preset.IsBoardOf)) continue;
            store.Save(new BbsProfile
            {
                Name = preset.Name,
                Host = preset.Host ?? string.Empty,
                Port = preset.Port,
                WebsiteUrl = preset.WebsiteUrl,
                Realms = [new RealmProfile { Name = preset.Name }],
            });
            made.Add(preset.Name);
        }
        return made;
    }
}
