using System.Text;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// Five small sections of the bug report: what the lesser engines have queued or
// are waiting for, the user's own triggers, aliases and macros, who may send the
// client which remote commands, the character's deaths, and the corpses
// recovered and not yet handed back.
public static class BugReportAutomationSections
{
    public const string PendingHeading = "Pending work";
    public const string AutomationHeading = "Triggers, aliases and macros";
    public const string PermissionsHeading = "Remote-command permissions";
    public const string DeathsHeading = "Death history (oldest first)";
    public const string CorpseHandoffsHeading = "Corpses to hand back (newest first)";

    // How many deaths are listed: the history is kept for the character's life.
    private const int DeathsShown = 25;

    // One line per engine that has something queued, in flight or awaited; an
    // engine with nothing to say isn't listed.
    public static string PendingWork(AppServices svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        StringBuilder sb = new();
        void Line(string engine, Func<string?> read)
        {
            // One engine that can't be read must not hide the rest.
            try
            {
                if (read() is { Length: > 0 } pending) sb.Append("- **").Append(engine).Append("**: ").Append(pending).Append('\n');
            }
            catch (Exception ex)
            {
                sb.Append("- **").Append(engine).Append("**: _(could not read: ").Append(ex.GetType().Name).Append(")_\n");
            }
        }

        Line("Auto-buy", () => svc.AutoBuy.DescribePending());
        Line("Auto-sell", () => svc.AutoSell.DescribePending());
        Line("Auto-deposit", () => svc.AutoDeposit.DescribePending());
        Line("Auto-search", () => svc.AutoSearch.DescribePending());
        Line("Pickup gate", () => svc.Acquisition.DescribePending());
        Line("Equipment", () => svc.Equipment.DescribePending());
        Line("Token route", () => svc.TokenRoute.DescribePending());
        Line("Shortcut item", () => svc.ShortcutSource.DescribePending());
        Line("Lair-entry debuff", () => svc.LairDebuffHold.DescribePending());
        Line("Party inventory probe", () => svc.PartyInventory.DescribePending());
        Line("Party level probe", () => svc.PartyLevelProbe.DescribePending());
        Line("Party wealth probe", () => svc.PartyWealthProbe.DescribePending());
        Line("Telepaths", () => svc.Telepaths.Queued + svc.Telepaths.InFlight == 0 && !svc.Telepaths.Held ? null
            : $"{svc.Telepaths.Queued} queued, {svc.Telepaths.InFlight} awaiting the game's acknowledgement"
              + (svc.Telepaths.Held ? $"; held by {svc.Telepaths.HeldBy}" : string.Empty));
        Line("Corpse handoff", () => svc.CorpseHandoff.DescribePending());
        Line("Relog", () => svc.RelogSignal.DescribePending());
        Line("Cleanup log-off", () => svc.CleanupLogout.DescribePending());
        Line("Game entry", () => svc.MainMenuEntry.DescribePending());
        // The state's name only: nothing of what is typed in the flow is kept.
        Line("Suicide password flow", () => svc.SuicidePassword.State == Game.SuicidePasswordTracker.FlowState.Idle
            ? (svc.Suicide.AwaitingPasswordPrompt ? "@suicide sent `suicide`, waiting for the game's password prompt" : null)
            : svc.SuicidePassword.State.ToString());

        return sb.Length == 0 ? "_(nothing queued, in flight or awaited by these engines)_" : sb.ToString();
    }

    // The user's own automation, which sends commands like any engine. A
    // definition that holds a login name or a password is left out whole.
    public static string UserAutomation(
        IReadOnlyList<Trigger> triggers, IReadOnlyList<Alias> aliases, IReadOnlyList<Macro> macros,
        BugReportScrub.Scrubber scrubber, BugReportChatCensor censor)
    {
        ArgumentNullException.ThrowIfNull(scrubber);
        ArgumentNullException.ThrowIfNull(censor);
        StringBuilder sb = new();
        sb.Append("What each one sent, and when, is in Commands sent (origin `user`).\n\n");

        sb.Append("**Triggers** (").Append(triggers.Count).Append(")\n\n");
        if (triggers.Count == 0) sb.Append("_(none)_\n");
        Dictionary<string, TriggerLevel> nearest = TriggerEngine.Nearest(triggers);
        foreach (Trigger t in triggers)
            Definition(sb, scrubber, t.Name + t.Pattern + t.Response,
                $"{Name(t.Name)}{Off(t.Enabled)} [{t.Level}"
                + (nearest[t.Name.Trim()] < t.Level ? $", overridden by the {nearest[t.Name.Trim()]}'s" : string.Empty)
                + $"] — {t.Scope}, {t.MatchType}: `{t.Pattern}` → `{censor.CensorSentCommand(t.Response)}`"
                + (string.IsNullOrEmpty(t.SoundFile) ? string.Empty : " (plays a sound)"));

        sb.Append("\n**Aliases** (").Append(aliases.Count).Append(")\n\n");
        if (aliases.Count == 0) sb.Append("_(none)_\n");
        foreach (Alias a in aliases)
            Definition(sb, scrubber, a.Name + a.Expansion,
                $"{Name(a.Name)}{Off(a.Enabled)} → `{censor.CensorSentCommand(a.Expansion)}`");

        sb.Append("\n**Macros** (").Append(macros.Count).Append(")\n\n");
        if (macros.Count == 0) sb.Append("_(none)_\n");
        foreach (Macro m in macros)
            Definition(sb, scrubber, m.Command,
                $"{(m.Ctrl ? "Ctrl+" : string.Empty)}{(m.Shift ? "Shift+" : string.Empty)}{(m.Alt ? "Alt+" : string.Empty)}{m.Key}"
                + $"{Off(m.Enabled)} → `{censor.CensorSentCommand(m.Command)}`");
        return sb.ToString();
    }

    private static void Definition(StringBuilder sb, BugReportScrub.Scrubber scrubber, string everything, string line) =>
        sb.Append("- ").Append(scrubber.Contains(everything) ? BugReportScrub.OmittedDefinition : line).Append('\n');

    private static string Name(string? name) => string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
    private static string Off(bool enabled) => enabled ? string.Empty : " [off]";

    // Who may send which remote commands: an "@command was ignored" report is
    // answered here.
    public static string RemotePermissions(AppServices svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        List<PlayerRecord> allowed = svc.Players.Players
            .Where(p => p.RemoteControls != PlayerRemoteControls.None)
            .OrderBy(p => p.GivenName, StringComparer.OrdinalIgnoreCase).ToList();
        HashSet<string> party = new(svc.PartyState.Members.Where(m => !m.IsSelf).Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        StringBuilder sb = new();
        sb.Append("Player remote settings (permissions, party toggles, Don't-delete) are kept ").Append(SharedPlayerGrants.Describe(svc.Players.PermissionScope)).Append(".\n");
        sb.Append("Players given any remote-command permission (").Append(allowed.Count)
          .Append("); everyone else may send none. Denied or unknown commands are answered: ")
          .Append(svc.RemoteCommands.WarnOnDenial ? "yes" : "no (Warn on invalid remote command is off)").Append(".\n\n");
        if (allowed.Count == 0) sb.Append("_(nobody)_\n");
        foreach (PlayerRecord p in allowed)
            sb.Append("- ").Append(p.GivenName).Append(party.Contains(p.GivenName) ? " (in the party)" : string.Empty)
              .Append(": ").Append(p.RemoteControls).Append('\n');
        List<string> without = party.Where(name => allowed.TrueForAll(p => !string.Equals(p.GivenName, name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (without.Count > 0)
            sb.Append("\nParty members with no permission at all: ").Append(string.Join(", ", without)).Append('\n');
        sb.Append('\n').Append(BroadcastAnswers(svc.RemoteCommands.BroadcastAnswers())).Append('\n');
        return sb.ToString();
    }

    // Why a command said on the broadcast channel was passed over: the minute's turns
    // were spent. Names are left out; they are other players'.
    internal static string BroadcastAnswers(Game.Remote.RemoteCommandManager.BroadcastAnswerCounts counts)
    {
        int cap = Game.Remote.BroadcastAnswerBudget.AnswersPerWindow;
        return $"Broadcast channel, the last minute: {counts.Permitted} of {cap} answers to permitted speakers, "
            + $"{counts.Refused} of {cap} refusals; {counts.Remembered} speaker(s) remembered (names not shown).";
    }

    public static string DeathHistory(IReadOnlyList<Models.Profile.DeathRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return "_(no deaths recorded for this character)_";
        StringBuilder sb = new();
        int shown = Math.Min(DeathsShown, records.Count);
        sb.Append("The last ").Append(shown).Append(" of ").Append(records.Count)
          .Append(" recorded. The newest one's deathpile is detailed under Session.\n\n");
        for (int i = records.Count - shown; i < records.Count; i++)
        {
            Models.Profile.DeathRecord d = records[i];
            sb.Append("- #").Append(d.RecordNumber).Append(' ').Append(d.At == default ? "(time unknown)" : d.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))
              .Append(" at ").Append(d.RoomKeyText)
              .Append(string.IsNullOrWhiteSpace(d.RoomName) ? string.Empty : $" ({d.RoomName})")
              .Append(", ").Append(d.LivesRemaining).Append(" lives left — ").Append(d.Status)
              .Append(d.RecoveryMessage is { Length: > 0 } msg ? $": {msg}" : string.Empty)
              .Append(d.UnrecoveredItems is { Count: > 0 } missing ? $"; still missing {missing.Count} item(s)" : string.Empty)
              .Append(d.GameData is { } set ? $"; recorded on game data '{set}'" : "; game data not recorded (taken as the active one's)")
              .Append('\n');
        }
        return sb.ToString();
    }

    // Other players' corpses recovered and not yet handed back: names and items,
    // never the password (the record has none).
    public static string CorpseHandoffs(IReadOnlyList<Models.Profile.CorpseHandoffRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return "_(none)_";
        StringBuilder sb = new();
        foreach (Models.Profile.CorpseHandoffRecord r in records)
        {
            sb.Append("- ").Append(r.RecoveredText).Append(" in ").Append(r.RoomText).Append(" — ").Append(r.Summary)
              .Append(r.Note is { Length: > 0 } note ? $" ({note})" : string.Empty).Append('\n');
        }
        return sb.ToString();
    }
}
