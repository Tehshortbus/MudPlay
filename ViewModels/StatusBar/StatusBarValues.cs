using System.Globalization;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.StatusBar;

// The current text of each status-bar item. Every reader is a few property reads
// and a format — nothing here touches game data or the wire — because the bar asks
// for the items it shows twice a second.
public static class StatusBarValues
{
    // Items whose value is one of the main window's own status properties: the bar
    // refreshes them the moment that property changes instead of on its timer, so
    // the tick countdowns keep their own cadence.
    public static string? SourceProperty(string id) => id switch
    {
        "engine" => nameof(MainWindowViewModel.EngineActionBadge),
        "location" => nameof(MainWindowViewModel.LocationText),
        "exprate" => nameof(MainWindowViewModel.ExpRateText),
        "tnl" => nameof(MainWindowViewModel.TimeToLevelText),
        "target" => nameof(MainWindowViewModel.TargetHpText),
        "statline" => nameof(MainWindowViewModel.IsStatlineMismatchVisible),
        "tick" => nameof(MainWindowViewModel.CombatTickText),
        "hptick" => nameof(MainWindowViewModel.HpTickText),
        "matick" => nameof(MainWindowViewModel.MaTickText),
        "connection" => nameof(MainWindowViewModel.ConnectionStatusText),
        _ => null,
    };

    public static string Read(string id, MainWindowViewModel main)
    {
        AppServices svc = AppServices.Current;
        PlayerState state = svc.PlayerState;
        PlayerStats stats = svc.PlayerStats;
        return id switch
        {
            "engine" => main.EngineActionBadge,
            "location" => main.LocationText,
            "exprate" => main.ExpRateText,
            "tnl" => main.TimeToLevelText,
            "target" => main.TargetHpText,
            "statline" => main.IsStatlineMismatchVisible ? "STATLINE MISMATCH" : string.Empty,
            "tick" => main.CombatTickText,
            "hptick" => main.HpTickText,
            "matick" => main.MaTickText,
            "connection" => string.Join(" · ",
                new[] { main.ConnectionStatusText, main.ReconnectCountdownText, main.HangupWatchHoldText }
                    .Where(part => part.Length > 0)),

            "profile" => svc.Profile.CurrentProfileName ?? string.Empty,
            "name" => stats.Name,
            "level" => stats.Level > 0 ? $"Lvl {stats.Level}" : string.Empty,
            "class" => $"{stats.Race} {stats.Class}".Trim(),
            "lives" => stats.Level > 0 ? $"Lives {stats.Lives}" : string.Empty,
            "bbs" => main.ActiveBbsName ?? string.Empty,
            "gamedata" => svc.GameData.ActiveSet ?? string.Empty,
            "combatprofile" => CombatProfile(svc, number: true, name: true),
            "combatprofilenumber" => CombatProfile(svc, number: true, name: false),
            "combatprofilename" => CombatProfile(svc, number: false, name: true),

            "gearset" => GearSet(svc),

            "hp" => state.HasPromptData ? $"HP {state.Hp}/{state.MaxHp}" : string.Empty,
            "hppct" => state.HasPromptData && state.MaxHp > 0 ? $"HP {Percent(state.Hp, state.MaxHp)}%" : string.Empty,
            "mana" => state.HasPromptData && state.MaxMa > 0 ? $"MA {state.Ma}/{state.MaxMa}" : string.Empty,
            "manapct" => state.HasPromptData && state.MaxMa > 0 ? $"MA {Percent(state.Ma, state.MaxMa)}%" : string.Empty,
            "hpregen" => svc.RegenReadout.HpLastText,
            "hpregenexp" => svc.RegenReadout.HpExpectedText,
            "maregen" => svc.RegenReadout.MaLastText,
            "maregenexp" => svc.RegenReadout.MaExpectedText,
            "position" => state.Position == PlayerPosition.Standing ? string.Empty : state.Position.ToString(),
            "stealth" => state.IsHidden ? "Hidden" : state.IsSneaking ? "Sneaking" : string.Empty,
            "encumbrance" => Encumbrance(state, svc.Inventory.Snapshot.Encumbrance),

            "roomkey" => svc.RoomTracker.State.CurrentRoom?.Key.ToString() ?? string.Empty,
            "room" => svc.RoomTracker.State.CurrentRoom?.Name ?? string.Empty,
            "loop" => RunningLoop(svc)?.Name ?? string.Empty,
            "lap" => RunningLoop(svc) is null ? string.Empty : $"Lap {svc.LoopRunner.CompletedLaps + 1}",
            "loopstep" => LoopStep(svc),
            "destination" => svc.Walker.State != WalkState.Idle && svc.Walker.Destination is { } to ? $"To {to}" : string.Empty,

            "fighting" => state.InCombat ? svc.Combat.CurrentTarget ?? string.Empty : string.Empty,
            "expneeded" => stats.Level > 0 ? $"Needs {RateText.Compact(Math.Max(0, stats.ExpToNext))}" : string.Empty,
            "party" => Party(svc),
            "hitpct" => Combat(svc) is { TotalSwings: > 0 } c ? $"Hit {c.HitPercent:0}%" : string.Empty,
            "critpct" => Combat(svc) is { LandedSwings: > 0 } c ? $"Crit {c.CritPercent:0}%" : string.Empty,
            "backstabpct" => Combat(svc) is { BackstabAttempts: > 0 } c ? $"BS {c.BackstabPercent:0}%" : string.Empty,
            "avghit" => Combat(svc) is { LandedSwings: > 0 } c ? $"Avg hit {c.PhysicalAvgDamage:0}" : string.Empty,
            "avground" => Combat(svc) is { RoundsWithDamage: > 0 } c ? $"Round {c.RoundAvgDamage:0}" : string.Empty,
            "roundstokill" => Combat(svc) is { TimedKills: > 0 } c ? $"{c.AvgRoundsToKill:0.0} rds/kill" : string.Empty,
            "dodgepct" => Combat(svc) is { IncomingAttacks: > 0 } c ? $"Dodge {c.DodgePercent:0}%" : string.Empty,
            "hittakenpct" => Combat(svc) is { IncomingAttacks: > 0 } c ? $"Hit by {c.HitTakenPercent:0}%" : string.Empty,

            "sessiontime" => Duration(Session(svc).TimeOnline),
            "sessionexp" => $"Exp {RateText.Compact(Session(svc).ExperienceEarned)}",
            "kills" => $"Kills {Session(svc).MonstersKilled}",
            "killrate" => $"{Session(svc).KillsPerHour:0} kills/hr",
            "sessioncash" => Session(svc).CurrencyCollected > 0 ? $"Got {Worth(Session(svc).CurrencyCollected)}" : string.Empty,
            "cashrate" => Session(svc).CurrencyPerHour > 0 ? $"{Worth(Session(svc).CurrencyPerHour)}/hr" : string.Empty,
            "itemsgot" => $"Items {Session(svc).ItemsCollected}",
            "itemssold" => $"Sold {Session(svc).ItemsSold}",
            "steps" => $"Steps {Session(svc).Steps}",
            "steptime" => Session(svc).AverageStep is { } step ? $"Step {step.TotalSeconds:0.00}s" : string.Empty,
            "sneakpct" => Session(svc).SneakPercent is { } sneak ? $"Sneak {sneak:0}%" : string.Empty,

            "auto" => AutoEngines(main),
            "nextevent" => NextEvent(svc),
            "cash" => Cash(svc.Inventory.Snapshot.Currency),
            "clock" => DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    private static int Percent(int value, int max) => (int)Math.Round(100.0 * value / max);

    // The two session tallies, taken at most once per poll however many items read
    // them.
    private static readonly TimeSpan SnapshotAge = TimeSpan.FromMilliseconds(250);
    private static long _sessionAt = long.MinValue, _combatAt = long.MinValue;
    private static SessionActivityStats _session;
    private static CombatSessionStats _combat;

    private static SessionActivityStats Session(AppServices svc)
    {
        long now = Environment.TickCount64;
        if (now - _sessionAt < SnapshotAge.TotalMilliseconds) return _session;
        _sessionAt = now;
        return _session = svc.SessionActivity.Snapshot();
    }

    private static CombatSessionStats Combat(AppServices svc)
    {
        long now = Environment.TickCount64;
        if (now - _combatAt < SnapshotAge.TotalMilliseconds) return _combat;
        _combatAt = now;
        return _combat = svc.CombatSession.Snapshot();
    }

    // "8.7 platinum" — a copper value in the highest coin it reaches.
    private static string Worth(double copper)
    {
        foreach (CoinDenomination coin in new[]
                 {
                     CoinDenomination.Runic, CoinDenomination.Platinum, CoinDenomination.Gold, CoinDenomination.Silver,
                 })
        {
            long unit = CurrencyHoldings.CopperUnit(coin);
            if (copper >= unit) return $"{(copper / unit).ToString("0.#", CultureInfo.InvariantCulture)} {coin.ToString().ToLowerInvariant()}";
        }
        return $"{copper:0} copper";
    }

    // "Medium 1420/2400 (59%)" — the word, then the weight carried, the limit and
    // the percentage once an inventory read has supplied them.
    private static string Encumbrance(PlayerState state, EncumbranceReading read)
    {
        EncumbranceLevel level = read.Category != EncumbranceLevel.Unknown ? read.Category : state.Encumbrance;
        if (level == EncumbranceLevel.Unknown) return string.Empty;
        return read.MaxWeight > 0
            ? $"{level} {read.CurrentWeight}/{read.MaxWeight} ({read.Percentage}%)"
            : level.ToString();
    }

    // "Step 36 of 60" once the loop is circling; nothing during its approach walk.
    private static string LoopStep(AppServices svc)
    {
        if (RunningLoop(svc) is null || svc.LoopRunner.State == LoopState.Approaching) return string.Empty;
        string step = MainWindowViewModel.LoopStepText(svc.LoopRunner);
        return step.Length == 0 ? string.Empty : char.ToUpperInvariant(step[0]) + step[1..];
    }

    private static Loop? RunningLoop(AppServices svc) =>
        svc.LoopRunner.State == LoopState.Idle ? null : svc.LoopRunner.CurrentLoop;

    private static string CombatProfile(AppServices svc, bool number, bool name)
    {
        int active = svc.CombatProfiles.ActiveIndex;
        var profiles = svc.CombatProfiles.Profiles;
        if (active < 0 || active >= profiles.Count) return string.Empty;
        string label = number ? $"P{active + 1}" : string.Empty;
        return name ? $"{label} {profiles[active].Name}".Trim() : label;
    }

    private static string GearSet(AppServices svc)
    {
        if (svc.Equipment.CurrentSetId is not { Length: > 0 } id) return string.Empty;
        string? name = svc.Profile.Current?.Equipment?.Sets.FirstOrDefault(s => s.Id == id)?.Name;
        return string.IsNullOrWhiteSpace(name) ? string.Empty : $"Gear: {name}";
    }

    // "Next: Bank run in 12m" — the Event with the nearest countdown. Lifecycle
    // events (logon, logoff, re-log) have none and never show here.
    private static string NextEvent(AppServices svc)
    {
        if (svc.Profile.Current?.EventsGloballyDisabled == true) return string.Empty;
        Models.GameData.ScheduledEvent? soonest = null;
        DateTime due = DateTime.MaxValue;
        foreach (Models.GameData.ScheduledEvent e in svc.Events.Events)
        {
            if (svc.EventScheduler.GetNextFire(e) is not { } at || at >= due) continue;
            soonest = e;
            due = at;
        }
        if (soonest is null) return string.Empty;
        string name = string.IsNullOrWhiteSpace(soonest.Name) ? "event" : soonest.Name;
        return $"Next: {name} in {Countdown(due - DateTime.Now)}";
    }

    private static string Countdown(TimeSpan left) =>
        left <= TimeSpan.Zero ? "0s"
        : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m"
        : left.TotalMinutes >= 1 ? $"{left.Minutes}m {left.Seconds:00}s"
        : $"{left.Seconds}s";

    private static string Party(AppServices svc)
    {
        PartyState party = svc.PartyState;
        if (!party.IsInParty) return string.Empty;
        string size = $"Party {party.Members.Count}";
        return party.SelfIsLeader ? $"{size} · leading"
            : string.IsNullOrEmpty(party.LeaderName) ? size
            : $"{size} · {party.LeaderName} leads";
    }

    private static string Duration(TimeSpan time) =>
        time <= TimeSpan.Zero ? string.Empty
        : $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";

    private static string AutoEngines(MainWindowViewModel main)
    {
        List<string> on = new(10);
        if (main.IsAutoCombatActive) on.Add("Combat");
        if (main.IsAutoNukeActive) on.Add("Nuke");
        if (main.IsAutoHealActive) on.Add("Heal");
        if (main.IsAutoRestActive) on.Add("Rest");
        if (main.IsAutoBlessActive) on.Add("Bless");
        if (main.IsAutoLightActive) on.Add("Light");
        if (main.IsAutoGetItemsActive) on.Add("Items");
        if (main.IsAutoGetCashActive) on.Add("Cash");
        if (main.IsAutoSneakActive) on.Add("Sneak");
        if (main.IsAutoHideActive) on.Add("Hide");
        if (main.IsAutoSearchActive) on.Add("Search");
        return on.Count == 0 ? "Auto: off" : "Auto: " + string.Join(' ', on);
    }

    // "Cash 2r 3p 40g" — every coin held, highest first.
    private static string Cash(CurrencyHoldings held)
    {
        if (held.TotalCoinCount == 0) return string.Empty;
        List<string> coins = new(5);
        if (held.Runic > 0) coins.Add($"{held.Runic}r");
        if (held.Platinum > 0) coins.Add($"{held.Platinum}p");
        if (held.Gold > 0) coins.Add($"{held.Gold}g");
        if (held.Silver > 0) coins.Add($"{held.Silver}s");
        if (held.Copper > 0) coins.Add($"{held.Copper}c");
        return "Cash " + string.Join(' ', coins);
    }
}
