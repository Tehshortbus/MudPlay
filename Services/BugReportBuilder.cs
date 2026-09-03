using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Terminal;

namespace MudPlay.Services;

// Snapshots the live client state into a self-contained Markdown bug report.
// Capture freezes everything time-sensitive (recent scrollback, the program
// log tail, all gameplay settings, engine + player state) at the instant the
// user clicks "Bug report", so the report reflects the moment of the problem
// rather than whenever the user finishes typing their description. Render then
// folds the user's description in and produces the final document; FileName
// derives the Desktop file name (realm-timestamp.md).
//
// The two-phase split (capture → render) keeps the capture pure data: the
// description arrives from a dialog that opens after the click, and the
// scrollback / log keep growing while the user types. Rendering per-section
// Markdown at capture time is deliberate — it freezes each subsystem's view
// without holding live references that could mutate underneath us.
public static class BugReportBuilder
{
    // How many trailing transcript lines (scrollback + live screen) to include.
    private const int ScrollbackLines = 750;

    // How many trailing program-log entries to include.
    private const int LogLines = 750;

    // One captured section of the report — a heading and its pre-rendered Markdown body.
    public readonly record struct Section(string Heading, string Body);

    // Frozen point-in-time capture produced by Capture. Holds the realm +
    // timestamp used for the file name and every pre-rendered section. The
    // user's issue description is folded in later by Render.
    public sealed record BugReportCapture(
        DateTimeOffset CapturedAt,
        RealmType Realm,
        IReadOnlyList<Section> Sections);

    // Freeze the current client state into a BugReportCapture. Every section is
    // built defensively — a failure reading one subsystem is surfaced inline in
    // that section rather than aborting the whole report, because a bug report
    // is most needed exactly when something is in a bad state.
    public static BugReportCapture Capture(AppServices svc, TerminalEmulator emulator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(emulator);

        DateTimeOffset now = DateTimeOffset.Now;
        RealmType realm = Guard(() => svc.GameData.ActiveRealm, RealmType.Stock);

        List<Section> sections =
        [
            new("Session", SafeSection(() => BuildSession(svc, realm, now))),
            new("Player state", SafeSection(() => BuildPlayerState(svc))),
            new("Party", SafeSection(() => BuildParty(svc))),
            new("Inventory", SafeSection(() => BuildInventory(svc))),
            new("Player Workshop", SafeSection(() => BuildWorkshop(svc))),
            new("Movement engine", SafeSection(() => BuildMovement(svc))),
            new("Navigation engines", SafeSection(() => BuildNavigationEngines(svc))),
            new("Exp/Hr estimator", SafeSection(() => BuildExpEstimator(svc))),
            new("Special room markers", SafeSection(() => BuildRoomMarkers(svc))),
            new("Auto-mode", SafeSection(() => BuildAutoMode(svc))),
            new("Keybindings", SafeSection(() => BuildKeybindings(svc))),
            new("Live engine state", SafeSection(() => BuildEngineState(svc))),
            new("Room combat assessment", SafeSection(() => BuildRoomCombatAssessment(svc))),
            new("Spell resolution", SafeSection(() => BuildSpellResolution(svc))),
            new("Combat spell profiles", SafeSection(() => BuildCombatProfiles(svc))),
            new("Monster overrides", SafeSection(() => BuildMonsterOverrides(svc))),
            new("Monster observations (this character)", SafeSection(() => BuildMonsterObservations(svc))),
            new("Item overrides", SafeSection(() => BuildItemOverrides(svc))),
            new("Effective settings (resolved)", SafeSection(() => BuildEffectiveSettings(svc))),
            new("Settings overrides (deltas, excluding BBS + Display)", SafeSection(() => BuildSettings(svc))),
            new("Program log", SafeSection(() => BuildLog(svc))),
            new("Scrollback", SafeSection(() => BuildScrollback(emulator))),
        ];

        // Wire Inspector capture — only when the user has those panes up (they're
        // large; irrelevant to most reports). The raw ANSI + the recognizer's read
        // of each combat line are exactly what a combat-recognition bug needs.
        WireInspectorVisibility wire = Guard(() => svc.WireInspectorVisibility, new());
        if (wire.RawVisible)
            sections.Add(new("Wire — raw (last 750 lines)",
                SafeSection(() => BuildRawWire(svc))));
        if (wire.ClassifiedVisible)
            sections.Add(new("Wire — classified combat (last 750 lines)",
                SafeSection(() => BuildClassifiedWire(svc))));

        return new BugReportCapture(now, realm, sections);
    }

    // Compose the final Markdown document from a capture and the user's
    // issueDescription. The description is placed at the top so a triager reads
    // the "what went wrong" before the state dump.
    public static string Render(BugReportCapture capture, string issueDescription)
    {
        ArgumentNullException.ThrowIfNull(capture);

        StringBuilder sb = new(capacity: 16 * 1024);
        sb.Append("# MudPlay bug report\n\n");
        sb.Append("_Captured ").Append(capture.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))
          .Append("  •  realm ").Append(RealmLabel(capture.Realm)).Append("_\n\n");

        sb.Append("## Issue\n\n");
        sb.Append(string.IsNullOrWhiteSpace(issueDescription) ? "_(none provided)_" : issueDescription.Trim());
        sb.Append("\n\n");

        AppendSections(sb, capture);
        return sb.ToString();
    }

    // State-only variant for the crash reporter: the section dump with no
    // bug-report title and no user-description block, so a crash document can
    // embed the same live-state snapshot under its own headings.
    public static string RenderStateOnly(BugReportCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        StringBuilder sb = new(capacity: 16 * 1024);
        AppendSections(sb, capture);
        return sb.ToString();
    }

    private static void AppendSections(StringBuilder sb, BugReportCapture capture)
    {
        foreach (Section section in capture.Sections)
        {
            sb.Append("## ").Append(section.Heading).Append("\n\n");
            sb.Append(section.Body.TrimEnd()).Append("\n\n");
        }
    }

    // Desktop file name for a capture: realm-yyyyMMdd-HHmmss.md, e.g.
    // paradigm-20260703-142530.md. Uses the click timestamp so the name matches
    // when the problem was seen, not when the file was written.
    public static string FileName(BugReportCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return $"{RealmLabel(capture.Realm)}-{capture.CapturedAt:yyyyMMdd-HHmmss}.md";
    }

    // ----- Section builders ----------------------------------------------

    private static string BuildSession(AppServices svc, RealmType realm, DateTimeOffset now)
    {
        StringBuilder sb = new();
        Kv(sb, "Version", AppInfo.Version);
        Kv(sb, "Captured at", now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Kv(sb, "Realm", $"{RealmLabel(realm)} ({realm})");
        Kv(sb, "Active game-data set", svc.GameData.ActiveSet ?? "(none)");
        Kv(sb, "Character", svc.Profile.CurrentProfileName ?? "(none loaded)");
        Kv(sb, "BBS", svc.Profile.CurrentBbsName ?? "(none)");
        // Retry/reconnect config for the active BBS. A "won't stop redialing" or
        // "never reconnected" report hinges on whether InfiniteRetries is on (which
        // overrides the count+pause to unlimited @ 3s) and which triggers are armed.
        var bbs = svc.ResolveActiveBbs();
        Kv(sb, "Retry behaviour", bbs is null
            ? "(no active BBS)"
            : (bbs.InfiniteRetries ? "infinite @ 3s" : $"{bbs.MaxRedials} redials @ {bbs.RedialPauseSeconds}s")
              + "; triggers:"
              + (bbs.ReconnectOnFailedConnect ? " failed-connect" : "")
              + (bbs.ReconnectOnCarrierLost ? " carrier-lost" : "")
              + (bbs.ReconnectOnNoResponse ? " no-response" : "")
              + (bbs.ReconnectOnFailedConnect || bbs.ReconnectOnCarrierLost || bbs.ReconnectOnNoResponse ? "" : " none")
              + $"; no-response idle {(bbs.NoResponseTimeoutSeconds > 0 ? bbs.NoResponseTimeoutSeconds + "s" : "off")}");
        // Startup profile-load setting — diagnoses "it didn't reopen my profile".
        Kv(sb, "Auto-load last profile", svc.Settings.Current.AutoLoadLastProfile ? "on" : "off");
        Kv(sb, "Last-used profile", svc.Settings.Current.LastUsedProfile is { } lp
            ? $"{lp.Name} on {lp.Bbs}" : "(none)");
        // Diagnostic-channel state gates whether the Program-log tail carries any
        // decision trail: both flags default off, and every _log?.Debug/Combat
        // site is skipped at generation time when off, so a report captured with
        // them off has Info-only logs. Surface the state so a triager knows why.
        Kv(sb, "Debug diagnostics", (svc.Log.Diagnostics?.DebugDiagnostics ?? false) ? "on" : "off");
        Kv(sb, "Combat diagnostics", (svc.Log.Diagnostics?.CombatDiagnostics ?? false) ? "on" : "off");
        // Direct-input (character) mode: on while a trainer / character-creation
        // stat box owns the keyboard, so arrow keys pass to the wire instead of
        // recalling command history. An "arrows don't move between stat fields"
        // report hinges on whether this flipped on entering the box.
        Kv(sb, "Direct-input mode", svc.TrainerMenu.MenuOwnsKeyboard ? "on (trainer/creation box)" : "off");
        // Death-pile auto-recovery: the toggles + the most recent death record's
        // state, so a "corpse didn't auto-recover" report shows whether it was
        // armed and how the last pile resolved (Recovered / Partial / Missing).
        Kv(sb, "Auto-recover deathpiles", svc.DeathRecovery.AutoRecover ? "on" : "off");
        Kv(sb, "Auto-equip on recovery", svc.DeathRecovery.AutoEquip ? "on" : "off");
        // Non-zero while an in-combat recovery is still pacing its re-equip across
        // rounds — shows a "recovered but not fully re-equipped" report mid-burst.
        if (svc.DeathRecovery.PendingReequipCount > 0)
            Kv(sb, "Re-equip pieces pending", svc.DeathRecovery.PendingReequipCount.ToString());
        var lastDeath = svc.DeathRecovery.Records.Count > 0 ? svc.DeathRecovery.Records[^1] : null;
        Kv(sb, "Latest deathpile", lastDeath is null
            ? "(none)"
            : $"{lastDeath.Status} @ {lastDeath.RoomKeyText}"
              + (lastDeath.RecoveryMessage is { Length: > 0 } msg ? $" — {msg}" : ""));
        return sb.ToString();
    }

    // Party roster snapshot — who's grouped, their roles, and the pending-invite
    // flags. Party-relevant bugs (self-cast family-name targeting, @join-nag
    // chasing an [Invited] row) hinge on exactly this state, which the `par`
    // echo in scrollback only shows indirectly.
    private static string BuildParty(AppServices svc)
    {
        PartyState party = svc.PartyState;
        StringBuilder sb = new();
        Kv(sb, "In party", party.IsInParty.ToString());
        Kv(sb, "Self is leader", party.SelfIsLeader.ToString());
        Kv(sb, "Leader", party.LeaderName ?? "(none)");
        // Board-specific disconnect line, if the active BBS defines one — the
        // config a "party sprinted off after a member dropped" report needs to
        // confirm the custom logoff line was actually taught to the client.
        Kv(sb, "BBS disconnect pattern", svc.ResolveActiveBbs()?.DisconnectPattern ?? "(built-in lines only)");
        // Follower reconnect-rejoin state — the leader we'd @comeback on the
        // next reconnect (crash-survivable). A "didn't auto-rejoin after a drop"
        // report hinges on whether the leader was remembered at all.
        Kv(sb, "Reconnect rejoin leader", svc.PartyRejoin.RememberedLeader ?? "(none remembered)");
        // Leader-side reconnect reform state — the followers we snapshotted at the
        // last drop and will wait for on reconnect. A "leader sprinted off / didn't
        // wait after a nightly-cleanup reconnect" report hinges on whether they
        // were captured at all.
        IReadOnlyList<string> pendingReform = svc.PartyReform.PendingReform;
        Kv(sb, "Reconnect reform followers",
            pendingReform.Count > 0 ? string.Join(", ", pendingReform) : "(none pending)");
        // Leader-side recovery state — who (if anyone) we're currently walking to
        // re-collect, and the reach cap that gates it. A "leader never came back
        // for me" report needs both.
        Kv(sb, "Recovering member", svc.PartyComeback.RecoveringMember ?? "(none in flight)");
        Kv(sb, "Recovery reach (rooms)", svc.PartyComeback.ReturnDistanceRooms.ToString());
        // Members we gave up chasing (return route un-crossable) — a "leader keeps
        // abandoning me" report should show the give-up was deliberate.
        var givenUp = svc.PartyComeback.GivenUpMembers;
        Kv(sb, "Recovery given up on", givenUp.Count == 0
            ? "(none)"
            : string.Join(", ", givenUp.Select(kv => $"{kv.Key} ({kv.Value} fails)")));
        Kv(sb, "Probe stats on partying (@level/@version)", svc.PartyProbe.Enabled ? "on" : "off");

        sb.Append("\n**Members** (").Append(party.Members.Count).Append(")\n\n");
        if (party.Members.Count == 0) { sb.Append("_(none)_\n"); return sb.ToString(); }

        foreach (PartyMember m in party.Members)
        {
            sb.Append("- ").Append(string.IsNullOrWhiteSpace(m.Name) ? "(unnamed)" : m.Name);
            if (!string.IsNullOrWhiteSpace(m.Class)) sb.Append(" (").Append(m.Class).Append(')');

            List<string> tags = new();
            if (m.IsSelf) tags.Add("self");
            if (m.IsLeader) tags.Add("leader");
            if (m.IsInvited) tags.Add("invited");
            tags.Add(m.Rank.ToString().ToLowerInvariant() + "rank");
            tags.Add(m.Position.ToString());
            if (m.IsWaiting) tags.Add("WAIT");
            foreach (string flag in AilmentFlags(m)) tags.Add(flag);
            sb.Append(" — ").Append(string.Join(", ", tags));

            // Invited rows carry no health round-trip yet, so their percents are
            // meaningless — skip the H/M readout for them.
            if (!m.IsInvited) sb.Append("  [").Append(m.HpRichDisplay).Append(' ').Append(m.MaRichDisplay).Append(']');
            // Level source drives the party level-gate routing; surface each
            // member's known level (exact + staleness, else title band) so a
            // "party routed the wrong way around a gate" report shows what the
            // gate check actually saw.
            if (!m.IsSelf && !string.IsNullOrWhiteSpace(m.Name))
            {
                sb.Append("  {lvl: ").Append(MemberLevelNote(svc, m.Name)).Append('}');
                // Client version recorded by the party stats probe (@version), when known.
                if (svc.Players.Find(m.Name)?.Version is { Length: > 0 } ver)
                    sb.Append("  {ver: ").Append(ver).Append('}');
            }
            sb.Append('\n');
        }

        // The most-constraining (Low, High) window the level gate routes on, or
        // "(n/a)" when not leading / nobody's level is known.
        (int Low, int High)? window = svc.PartyLevel.Bounds();
        Kv(sb, "Party level window",
            window is { } w ? $"{w.Low}–{w.High}" : "(n/a — solo, following, or no levels known)");
        return sb.ToString();
    }

    // One member's level as the party level-gate check sees it: the exact level
    // (with how long ago it was learned, since a reading not from the current day
    // is re-probed on a level-gated route) when known, else the title-derived
    // band, else unknown.
    private static string MemberLevelNote(AppServices svc, string name)
    {
        Models.GameData.PlayerRecord? rec = svc.Players.Find(name);
        if (rec?.Level is { } exact)
        {
            string age = rec.LevelAt is { } at
                ? $", {FormatAgeHours(DateTime.UtcNow - at)}"
                : ", age unknown";
            return $"{exact} exact{age}";
        }
        if (Game.GameData.ClassTitleTable.LookupLevelRange(rec?.Title) is { } band)
            return $"{band.MinLevel}–{band.MaxLevel} (from title \"{rec!.Title}\")";
        return "unknown";
    }

    private static string FormatAgeHours(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        double hours = age.TotalHours;
        return hours < 1
            ? $"{age.TotalMinutes:0}m ago"
            : $"{hours:0.#}h ago{(hours > 24 ? " STALE" : "")}";
    }

    private static IEnumerable<string> AilmentFlags(PartyMember m)
    {
        if (m.Resting) yield return "resting";
        if (m.Meditating) yield return "meditating";
        if (m.Blinded) yield return "blind";
        if (m.Poisoned) yield return "poison";
        if (m.Diseased) yield return "disease";
        if (m.Confused) yield return "confuse";
        if (m.Held) yield return "held";
    }

    // In-flight automation FSM state that the log lines only hint at: the @join
    // nag table (which invitees we're chasing and how far along) and the combat
    // weapon-swap shadow (what we believe is equipped, without re-parsing `inv`).
    // These are the exact internals a triager otherwise has to reconstruct from
    // code + log timestamps.
    private static string BuildEngineState(AppServices svc)
    {
        StringBuilder sb = new();

        IReadOnlyList<AutoPartyManager.NagSnapshot> nags = svc.AutoParty.ActiveNagSnapshot();
        sb.Append("**@join nags** (").Append(nags.Count).Append(")\n\n");
        if (nags.Count == 0) sb.Append("_(none active)_\n");
        else foreach (AutoPartyManager.NagSnapshot n in nags)
        {
            sb.Append("- ").Append(n.Given)
              .Append(": invited ").Append(n.InvitedAt.ToLocalTime().ToString("HH:mm:ss"))
              .Append(", sends=").Append(n.JoinSends)
              .Append(", lastJoin=").Append(n.LastJoinAt?.ToLocalTime().ToString("HH:mm:ss") ?? "(none)")
              .Append(", acknowledged=").Append(n.Acknowledged).Append('\n');
        }

        Game.Combat.CombatManager.DebugState combat = svc.Combat.Snapshot();
        // The believed-worn weapon is no longer shadowed in the combat engine —
        // EquipmentManager diffs against live inventory, so the report reads the
        // worn weapon / off-hand straight from the snapshot.
        Game.Inventory.InventorySnapshot inv = svc.Inventory.Snapshot;
        sb.Append("\n**Combat weapon state**\n\n");
        Kv(sb, "Current target", combat.CurrentTarget ?? "(none)");
        // A guarded priority we're chasing through the "moves to protect" redirect —
        // explains re-attacks aimed at a monster that isn't our live target.
        Kv(sb, "Guard-blocked priority", combat.GuardBlockedTarget ?? "(none)");
        // Passive neutrals the user hand-attacked that the engine has taken over killing —
        // explains why auto-combat is (or isn't) fighting a neutral the user engaged.
        Kv(sb, "User-engaged neutrals", combat.UserEngagedInstances.Count > 0
            ? string.Join(", ", combat.UserEngagedInstances)
            : "(none)");
        Kv(sb, "Worn weapon", WornSlot(inv, "Weapon Hand") ?? "(none)");
        Kv(sb, "Worn off-hand", WornSlot(inv, "Off-Hand") ?? "(none)");
        Kv(sb, "Using alternate weapon", combat.UsingAlternateWeapon.ToString());
        // The round's committed spell action + the cast-code the server is repeating —
        // "DrainSpell" here means the drain override is currently taking the round.
        Kv(sb, "Round spell action", combat.LastCastAction ?? "(weapon / idle)");
        Kv(sb, "Announced spell", combat.AnnouncedSpell ?? "(none)");
        // The attack-spell cascade's own latch, surfaced separately — it can go
        // stale relative to CurrentTarget/AnnouncedSpell above (report
        // paradigm-20260824-012300). A CastingSpellTarget the current room
        // doesn't hold, or SpellAttackOwed=true with no live fight, means every
        // automatic heal/cure/bless is being silently suppressed.
        Kv(sb, "Casting spell target", combat.CastingSpellTarget ?? "(none)");
        Kv(sb, "Spell attack owed", combat.SpellAttackOwed.ToString());
        // True here alongside a live CastingSpellTarget/CurrentTarget means the
        // spell-mode heartbeat is gated shut and nothing will ever retry the attack
        // (report paradigm-20260824-215802: an engage whose cast lost the round to a
        // recast-interval block left this stuck, and the character never attacked
        // again for the rest of the fight).
        Kv(sb, "Combat off (stuck?)", svc.Combat.CombatOff.ToString());
        // True when Auto-Combat is off but a room hostile is blocking a needed rest
        // (HP still above the flee trigger) — the engine is force-engaging to clear it
        // so recovery can proceed (report paradigm-20260901-093301).
        Kv(sb, "Engaging to clear a rest-blocker", svc.Health.ForceClearForRest.ToString());
        // Alternating action-order phase — pairs with the resolved Combat "ActionOrder"
        // setting below to explain why an alternate-order character is casting or
        // swinging this round (even rounds open on the mode's first phase).
        Kv(sb, "Alternation round", combat.AlternationRound.ToString());
        Kv(sb, "Awaiting backstab resolution", combat.AwaitingBackstabResolution
            ? $"yes (target={combat.PendingBackstabSpecies ?? "(none)"})"
            : "no");
        // ShadowRest hold explains a stealthed character resting instead of
        // engaging a monster in the room (combat stands down while true).
        Kv(sb, "ShadowRest holding", svc.Health.ShadowRestHolding.ToString());

        return sb.ToString();
    }

    // Per-monster game-data overlays the user has customized in the active set —
    // the deltas written via the Game Data Browser's Monster edit dialog (per-
    // monster attack command / attack spell / pre-attack spell, relationship,
    // priority, flags), shown as the EFFECTIVE overlay (realm seed + tier
    // overrides merged) with the tier that owns each record. A "won't attack this
    // monster" report hinges on whether a per-monster attack override is wired
    // (e.g. a physical-immune mob whose only kill means is a configured attack
    // spell) — that state lived nowhere in the capture before. Only records the
    // user actually overrode appear (the tier side-files hold deltas only), so
    // this stays a short list, not the whole realm seed.
    private static string BuildMonsterOverrides(AppServices svc)
    {
        StringBuilder sb = new();

        List<(int Number, string Id)> records = new();
        foreach (string id in svc.Resolver.GameDataOverrideIds("Monsters"))
            if (int.TryParse(id, out int n) && n > 0) records.Add((n, id));
        records.Sort((a, b) => a.Number.CompareTo(b.Number));

        sb.Append("Per-monster overlay deltas in the active game-data set — effective overlay (realm seed + tier overrides merged), tagged with the tier that owns each record (")
          .Append(records.Count).Append(")\n\n");
        if (records.Count == 0) { sb.Append("_(none)_\n"); return sb.ToString(); }

        foreach ((int n, string id) in records)
        {
            Models.GameData.MonsterOverlay o = svc.Resolver.ResolveGameData<Models.GameData.MonsterOverlay>(
                "Monsters", id, svc.MonsterOverlaySeed.GetOverlay(n));
            SettingsTier tier = svc.Resolver.GetGameDataSourceTier("Monsters", id);
            string name = svc.GameData.FindNameByNumber("Monsters", n) ?? "(unknown)";

            List<string> parts = new();
            if (!string.IsNullOrWhiteSpace(o.Name)) parts.Add($"name \"{o.Name}\"");
            if (o.Relationship is { } rel) parts.Add($"relationship {rel}");
            if (o.Priority is { } prio) parts.Add($"priority {prio}");
            if (!string.IsNullOrWhiteSpace(o.OverrideAttackCommand))
                parts.Add($"attack-cmd \"{o.OverrideAttackCommand}\"");
            if (o.OverrideAttackSpellId is { } atk and > 0)
                parts.Add($"attack-spell {SpellLabel(svc, atk)}{CountSuffix(o.OverrideAttackCount)}");
            if (o.OverridePreAttackSpellId is { } pre and > 0)
                parts.Add($"pre-attack {SpellLabel(svc, pre)}{CountSuffix(o.OverridePreAttackCount)}");
            if (o.DontBackstab == true) parts.Add("dontBackstab");
            if (o.KillOnSight == true) parts.Add("killOnSight");
            if (parts.Count == 0) parts.Add("(no live fields)");

            sb.Append("- #").Append(n).Append(' ').Append(name)
              .Append(" [").Append(tier).Append("] — ")
              .Append(string.Join(", ", parts)).Append('\n');
        }

        return sb.ToString();
    }

    // Per-character combat outcomes actually seen against a monster (Monster
    // Intel's "Your Observations") — the personal counterpart to the MDB-sourced
    // monster overrides above, useful when a report is about "why won't it hit /
    // cast on this thing".
    private static string BuildMonsterObservations(AppServices svc)
    {
        StringBuilder sb = new();
        List<Models.Profile.MonsterObservation> rows = svc.MonsterObservations.Snapshot()
            .OrderByDescending(o => o.LastObservedAt).ToList();

        sb.Append("Combat outcomes THIS character has observed per monster — landed-hit damage, hit rate, and confirmed physical/spell no-effect discoveries (")
          .Append(rows.Count).Append(")\n\n");
        if (rows.Count == 0) { sb.Append("_(none)_\n"); return sb.ToString(); }

        foreach (Models.Profile.MonsterObservation o in rows)
        {
            string name = svc.GameData.FindNameByNumber("Monsters", o.MonsterNumber) ?? "(unknown)";
            List<string> parts = new();
            if (o.HitCount > 0)
                parts.Add($"hits {o.HitCount} (dmg {o.HitDamageMin}-{o.HitDamageMax}, avg {o.AvgHitDamage:0.#})");
            if (o.SwingCount > 0)
                parts.Add($"hit-rate {o.HitRatePercent:0}% ({o.HitCount}/{o.SwingCount})");
            if (o.PhysicalNoEffectCount > 0) parts.Add($"physical-no-effect x{o.PhysicalNoEffectCount}");
            if (o.SpellNoEffectCount > 0) parts.Add($"spell-no-effect x{o.SpellNoEffectCount}");
            if (parts.Count == 0) parts.Add("(no outcomes recorded)");

            sb.Append("- #").Append(o.MonsterNumber).Append(' ').Append(name)
              .Append(" — ").Append(string.Join(", ", parts)).Append('\n');
        }

        return sb.ToString();
    }

    // "151 (disrupt)" — an override stores a Spell.Number; annotate it with the
    // Spells-table display name so a triager needn't cross-reference the id.
    private static string SpellLabel(AppServices svc, int spellNumber)
    {
        string? name = svc.GameData.FindNameByNumber("Spells", spellNumber);
        return string.IsNullOrWhiteSpace(name) ? $"{spellNumber}" : $"{spellNumber} ({name})";
    }

    // " x20" for a positive per-room cast cap; blank for null/0 (unlimited).
    private static string CountSuffix(int? count) => count is > 0 ? $" x{count}" : string.Empty;

    // The engine's live engageability verdict for every monster seen in the
    // current room — the reasoning behind a "skip un-actionable … Unkillable"
    // decision, frozen so a "won't attack this monster" report is self-diagnosing
    // (no dependency on combat logging being on or the scrollback still holding
    // the line). Per hostile: the Magical level (weapon-hit gate), SpellImmu
    // (attack-spell gate), each configured weapon's HitMagic, and the resolved
    // CanAct / StuckOnMana / Unkillable assessment with its reason.
    private static string BuildRoomCombatAssessment(AppServices svc)
    {
        StringBuilder sb = new();
        var rows = svc.Combat.SnapshotRoomEngage();

        sb.Append("Engine engageability of monsters known in the current room (weapon/spell magic gates + verdict) (")
          .Append(rows.Count).Append(")\n\n");
        if (rows.Count == 0) { sb.Append("_(no monsters tracked in the current room)_\n"); return sb.ToString(); }

        // -1 from the magic indexes means "unknown → fail open" — spell it out so a
        // triager doesn't read the sentinel as a real level.
        static string Lvl(int v) => v < 0 ? "?" : v.ToString();

        foreach (var r in rows)
        {
            string name = svc.GameData.FindNameByNumber("Monsters", r.MonsterNumber) ?? r.Species;
            sb.Append("- #").Append(r.MonsterNumber).Append(' ').Append(name)
              .Append(" — **").Append(r.Assessment).Append("**")
              .Append(", Magical ").Append(Lvl(r.Magical))
              .Append(", SpellImmu ").Append(Lvl(r.SpellImmu))
              .Append(", weapon HitMagic normal=").Append(Lvl(r.NormalWeaponHit))
              .Append(" alt=").Append(Lvl(r.AltWeaponHit));
            if (!string.IsNullOrWhiteSpace(r.UnengageableReason))
                sb.Append(" — ").Append(r.UnengageableReason);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Every configured spell slot resolved against the active game-data set:
    // cast-code → Spell.Number, name, learned?, ReqLevel, EnergyCost, mana cost.
    // A spell whose ReqLevel / EnergyCost / learned flag reads wrong here explains
    // a mis-cast or a "spell never fires / looks blocked" report at a glance (the
    // duplicate-short-code ReqLevel corruption would have jumped straight out).
    private static string BuildSpellResolution(AppServices svc)
    {
        var combat = svc.Resolver.Resolve<Models.Profile.CombatSettings>("Combat");
        var spells = svc.Resolver.Resolve<Models.Profile.SpellsSettings>("Spells");
        var party = svc.Resolver.Resolve<Models.Profile.PartySettings>("Party");

        StringBuilder sb = new();
        Kv(sb, "Current mana", $"{svc.PlayerState.Ma}/{svc.PlayerState.MaxMa}");

        // Buff-duration timers CastingDirector believes are still running, straight
        // from its own tracking (not re-derived) — a timer surviving past a real
        // death is the direct symptom of report paradigm-20260824-012300 (the
        // server clears every buff on death, but nothing told CastingDirector, so
        // it declines to recast a buff that's actually long gone).
        IReadOnlyList<Game.Spells.ActiveBuffTimer> buffs = svc.CastDirector.SnapshotActiveBuffs();
        sb.Append("\n**Active buff timers (CastingDirector)**\n\n");
        if (buffs.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            foreach (Game.Spells.ActiveBuffTimer b in buffs)
            {
                string target = string.IsNullOrEmpty(b.Target) ? "self" : b.Target;
                System.TimeSpan remaining = b.Until - System.DateTime.UtcNow;
                sb.Append($"- {b.Short} on {target}: {(remaining > System.TimeSpan.Zero ? $"{remaining.TotalSeconds:F0}s remaining" : "expired, not yet cleared")} (of {b.TotalSec}s)\n");
            }
        }
        sb.Append('\n');

        // Mana-regen reroll engine state — so a "flux stuck at a bad value" report
        // (paradigm-20260830-110918) shows the roll quality it judges from and its
        // cycle, not just the configured threshold in the buff plan above.
        Game.Spells.ManaRegenReroller reroll = svc.ManaRegen;
        string rerollSignal = svc.GameData.ActiveRealm == Game.RealmType.ParaMud
            ? "abil 145 spells value" : "observed mana tick";
        sb.Append("**Mana-regen reroll**\n\n");
        sb.Append($"- Roll signal: {rerollSignal}\n");
        sb.Append($"- Cycle active: {reroll.CycleActive}; rerolls used this cycle: {reroll.RerollsUsed}\n");
        sb.Append($"- Waiting for mana to resume: {reroll.WaitingForMana}\n");
        sb.Append($"- Last observed roll value: {(reroll.LastObservedValue is { } v ? v.ToString() : "(none judged yet)")}\n");
        sb.Append('\n');

        int shown = 0;
        void Group(string title, IEnumerable<(string Label, string? Code)> slots)
        {
            List<string> lines = new();
            foreach ((string label, string? code) in slots)
            {
                if (string.IsNullOrWhiteSpace(code)) continue;
                lines.Add("- " + SpellResolutionLine(svc, label, code));
                shown++;
            }
            if (lines.Count == 0) return;
            sb.Append("**").Append(title).Append("**\n\n");
            foreach (string l in lines) sb.Append(l).Append('\n');
            sb.Append('\n');
        }

        Group("Combat", new (string, string?)[]
        {
            ("normal-attack", combat.NormalAttackSpell.SpellName),
            ("alternate-attack", combat.AlternateAttackSpell.SpellName),
            ("multi-attack", combat.MultiAttackSpell.SpellName),
            ("area-debuff", combat.AreaDebuffSpell.SpellName),
            ("single-debuff", combat.SingleTargetDebuffSpell.SpellName),
            ("drain", combat.DrainSpell.SpellName),
        });
        Group("Heal & regen", new (string, string?)[]
        {
            ("minor-heal", spells.MinorHealSpell),
            ("major-heal", spells.MajorHealSpell),
            ("hp-regen", spells.HpRegenSpell),
            ("ma-regen", spells.MaRegenSpell),
        });
        Group("Cures", new (string, string?)[]
        {
            ("holds", spells.CureHoldsSpell),
            ("poison", spells.CurePoisonSpell),
            ("disease", spells.CureDiseaseSpell),
            ("blindness", spells.CureBlindnessSpell),
        });
        Group("Party heal", new (string, string?)[]
        {
            ("minor-party-heal", party.MinorPartyHealSpell),
            ("minor-party-heal-aoe", party.MinorPartyHealAoeSpell),
            ("major-party-heal", party.MajorPartyHealSpell),
            ("major-party-heal-aoe", party.MajorPartyHealAoeSpell),
        });

        // The one unified buff list (self bless + when-full + party buffs). Each slot's
        // label carries its targeting + any per-slot condition so a "buff didn't fire"
        // report shows exactly what was configured.
        int buffNo = 0;
        if (svc.Profile.Current?.PartyBuffs is { } unifiedBuffs)
            Group("Buffs", unifiedBuffs.Slots.Select(s => ($"buff {++buffNo} [{BuffScope(s)}]", s.Spell)));

        if (shown == 0) sb.Append("_(no spells configured)_\n");
        return sb.ToString();
    }

    // "code → #num name [learned] ReqLevel=X Energy=Y Mana=Z" for one slot's cast-
    // code, resolved against the active set. Unknowns render as "?" (fail-open in
    // the engine) rather than a sentinel number.
    private static string SpellResolutionLine(AppServices svc, string label, string code)
    {
        int? number = svc.SpellShort.NumberByShort(code);
        string head = $"{label}: `{code}`";
        if (number is not { } n)
            return $"{head} → (no Spells row with this short-code)";

        string name = svc.GameData.FindNameByNumber("Spells", n) ?? "(unnamed)";
        bool learned = svc.Spellbook.IsObtained(n);
        int req = svc.SpellReqLevel.ReqLevel(code);
        int? energy = svc.SpellCatalog.GetFormulaByNumber(n)?.EnergyCost;
        int? mana = svc.Spellbook.ManaCostOf(code);
        return $"{head} → #{n} {name} [{(learned ? "learned" : "NOT learned")}]"
             + $" ReqLevel={(req < 0 ? "?" : req.ToString())}"
             + $" Energy={(energy is { } e ? e.ToString() : "?")}"
             + $" Mana={(mana is { } m ? m.ToString() : "?")}";
    }

    // A unified buff slot's targeting + condition summary for the report label —
    // e.g. "self", "all", "Bob,Sue", "party-wide", with "+hp-full" / "+ma-full" when
    // a downtime condition is set. Derived from the slot's flags (whole-party is left
    // to WholePartyOn since the classifier isn't reachable here).
    private static string BuffScope(Models.Profile.BuffSlot s)
    {
        List<string> who = new();
        if (s.CastOnSelf) who.Add("self");
        if (s.AllMembers) who.Add("all");
        else if (s.Targets.Count > 0) who.Add(string.Join(",", s.Targets));
        else if (s.WholePartyOn) who.Add("party-wide?");
        string scope = who.Count > 0 ? string.Join("+", who) : "unset";
        if (s.OnlyWhenHpFull) scope += " +hp-full";
        if (s.OnlyWhenMaFull) scope += " +ma-full";
        if (s.OnlyWhenDark) scope += " +only-dark";
        if (s.CastBeforeRestingForMana) scope += " +pre-rest";
        if (s.RerollCount > 0) scope += $" +reroll<{s.RerollThreshold?.ToString() ?? "-"}x{s.RerollCount}";
        return scope;
    }

    // Per-item overlay deltas the user set in the active set — the loot-automation
    // flags written via the Game Data Browser's Item edit dialog. Symmetric to the
    // Monster overrides section; targets "why didn't it collect / sell / stash this
    // item" reports, which were blind to per-item flags before.
    private static string BuildItemOverrides(AppServices svc)
    {
        StringBuilder sb = new();

        List<(int Number, string Id)> records = new();
        foreach (string id in svc.Resolver.GameDataOverrideIds("Items"))
            if (int.TryParse(id, out int n) && n > 0) records.Add((n, id));
        records.Sort((a, b) => a.Number.CompareTo(b.Number));

        sb.Append("Per-item overlay deltas in the active game-data set — effective overlay (seed + tier overrides merged), tagged with the owning tier (")
          .Append(records.Count).Append(")\n\n");
        if (records.Count == 0) { sb.Append("_(none)_\n"); return sb.ToString(); }

        foreach ((int n, string id) in records)
        {
            Models.GameData.ItemOverlay o = svc.Resolver.ResolveGameData<Models.GameData.ItemOverlay>(
                "Items", id, svc.ItemOverlaySeed.GetOverlay(n));
            SettingsTier tier = svc.Resolver.GetGameDataSourceTier("Items", id);
            string name = svc.GameData.FindNameByNumber("Items", n) ?? "(unknown)";

            List<string> parts = new();
            if (!string.IsNullOrWhiteSpace(o.Name)) parts.Add($"name \"{o.Name}\"");
            Flag(parts, "autoCollect", o.AutoCollect);
            Flag(parts, "autoDiscard", o.AutoDiscard);
            Flag(parts, "autoFind", o.AutoFind);
            Flag(parts, "autoOpen", o.AutoOpen);
            Flag(parts, "autoBuy", o.AutoBuy);
            Flag(parts, "autoSell", o.AutoSell);
            Flag(parts, "autoStash", o.AutoStash);
            Flag(parts, "cannotBeTaken", o.CannotBeTaken);
            Flag(parts, "mustHaveMinimum", o.MustHaveMinimum);
            Flag(parts, "loyalItem", o.LoyalItem);
            Flag(parts, "autoObtainForPath", o.AutoObtainForPath);
            if (!string.IsNullOrWhiteSpace(o.MinToKeep)) parts.Add($"minToKeep {o.MinToKeep}");
            if (!string.IsNullOrWhiteSpace(o.MaxToGet)) parts.Add($"maxToGet {o.MaxToGet}");
            if (parts.Count == 0) parts.Add("(no live fields)");

            sb.Append("- #").Append(n).Append(' ').Append(name)
              .Append(" [").Append(tier).Append("] — ")
              .Append(string.Join(", ", parts)).Append('\n');
        }
        return sb.ToString();
    }

    // Append "flag" (on) / "!flag" (off) for a set tri-state bool; skip when null
    // (not overridden at any tier).
    private static void Flag(List<string> parts, string name, bool? value)
    {
        if (value is { } v) parts.Add(v ? name : "!" + name);
    }

    private static string BuildPlayerState(AppServices svc)
    {
        StringBuilder sb = new();
        sb.Append("**Live vitals (PlayerState)**\n\n");
        sb.Append(Json(svc.PlayerState)).Append('\n');
        // Self ailment flags (ConditionTracker) — the authoritative self view that
        // gates poison-sensitive behavior (e.g. the downtime-rest paths skip a
        // poisoned character). Distinct from the party self-row's broadcast flags.
        Kv(sb, "Active conditions (self)", svc.Conditions.ActiveFlags.ToString());
        sb.Append('\n');
        sb.Append("**Stat screen (PlayerStats)**\n\n");
        sb.Append(Json(svc.PlayerStats));
        return sb.ToString();
    }

    private static string BuildInventory(AppServices svc)
    {
        InventorySnapshot snapshot = svc.Inventory.Snapshot;
        return Json(snapshot);
    }

    // The Character Workshop's persisted, per-character artifacts — the gear
    // sets, the CP-allocation plan, and the quest log. These live as top-level
    // CharacterProfile properties (not in the settings-tab dictionary), so the
    // settings dump wouldn't otherwise carry them. The rest of the Workshop is
    // a read-only view over stats / inventory already captured above.
    private static string BuildWorkshop(AppServices svc)
    {
        var profile = svc.Profile.Current;
        if (profile is null) return "_(no character loaded)_";

        StringBuilder sb = new();

        sb.Append("**Gear sets (Equipment)**\n\n");
        sb.Append(profile.Equipment is { } equip ? Json(equip) : "_(none)_\n");

        // Unwearable-slot blocks — items a set can't currently equip (alignment /
        // level / class, or a game refusal). Skipped on apply until addressed, so
        // a "set won't equip X" report is answered right here.
        var blocks = svc.Equipment.BlockedSlotsSnapshot();
        sb.Append("\n**Equipment slot blocks** (").Append(blocks.Count).Append(")\n\n");
        if (blocks.Count == 0)
            sb.Append("_(none)_\n");
        else
            foreach ((string setId, var slot, string item, bool refused) in blocks)
            {
                string setName = profile.Equipment?.Sets
                    .FirstOrDefault(s => string.Equals(s.Id, setId, StringComparison.Ordinal))?.Name ?? setId;
                sb.Append("- ").Append(setName).Append(" / ").Append(slot)
                  .Append(": ").Append(item)
                  .Append(refused ? " (game refused)" : " (restricted)").Append('\n');
            }

        var plan = profile.CharacterPlan;
        sb.Append("\n**CP allocation plan (CharacterPlan)** (").Append(plan?.Count ?? 0).Append(")\n\n");
        sb.Append(plan is { Count: > 0 } ? Json(plan) : "_(none)_\n");

        var quests = profile.QuestLog;
        sb.Append("\n**Quest log (QuestLog)** (").Append(quests?.Count ?? 0).Append(")\n\n");
        sb.Append(quests is { Count: > 0 } ? Json(quests) : "_(none)_\n");

        // Count only — the Players Seen log can hold many rows; a count tells an
        // "empty / not recording" report apart from a working one without bloating
        // the capture with the whole roster.
        var seen = profile.PlayersSeen;
        sb.Append("\n**Players seen (PlayersSeen)**: ").Append(seen?.Count ?? 0).Append('\n');

        return sb.ToString();
    }

    private static string BuildMovement(AppServices svc)
    {
        StringBuilder sb = new();
        Kv(sb, "Coalesced state", svc.MovementControl.State.ToString());
        Kv(sb, "Active", svc.MovementControl.IsActive.ToString());
        Kv(sb, "Paused", svc.MovementControl.IsPaused.ToString());
        // Name the gate(s) actually holding the pause. "Paused: True" alone
        // can't tell a rest-hold (HealthRecovery) from a fight-hold (Combat) or
        // a manual stop (User) — the distinction a "walker stuck idle" report
        // needs to point at the right engine.
        var gates = svc.MovementCoordinator.AssertedGates;
        Kv(sb, "Paused by", gates.Count > 0 ? string.Join(", ", gates) : "(nothing)");
        // Whether the Auto-All kill switch is the one holding navigation — it
        // suspends an in-flight nav on engage and resumes it on restore.
        Kv(sb, "Auto-All suspended nav", svc.MovementControl.IsAutoAllSuspended.ToString());
        var loop = svc.LoopRunner;
        Kv(sb, "Loop runner", loop.State.ToString());
        // CurrentLoop is the loop of the LIVE run; StagedLoop is the loaded-but-
        // -not-started slot. They're mutually exclusive, so report both — a
        // running loop shows up under CurrentLoop, never StagedLoop.
        Kv(sb, "Running loop",
            loop.CurrentLoop is { } running
                ? $"{running.Name} — step {loop.CurrentIndex + 1}/{loop.StepCount}"
                : "(none)");
        if (loop.CurrentLoop is not null)
        {
            Kv(sb, "Loop approach target",
                loop.ApproachTarget is { } appr ? $"{appr.Map}/{appr.Room}" : "(none)");
            Kv(sb, "Loop circle start",
                loop.CircleStartRoom is { } start ? $"{start.Map}/{start.Room}" : "(none)");
        }
        Kv(sb, "Staged loop", loop.StagedLoop?.Name ?? "(none)");
        // Last loop / auto-lair run this session, retained past a stop/death —
        // what @path reports when idle so a party member can help the player
        // resume the circuit they were on.
        Kv(sb, "Last run loop", loop.LastRunLoopName ?? "(none)");
        Kv(sb, "Last run auto-lair", svc.AutoLair.LastRunLairName ?? "(none)");
        Kv(sb, "Auto-Lair phase", svc.AutoLair.Phase.ToString());
        Kv(sb, "Auto-Lair active", svc.AutoLair.IsActive.ToString());
        Kv(sb, "Auto-Lair paused", svc.AutoLair.IsPaused.ToString());
        Kv(sb, "Auto-Lair target",
            svc.AutoLair.CurrentTarget is { } lair ? $"{lair.Map}/{lair.Room}" : "(none)");
        // The live-resolved travel-cost model + its current per-hop figure — a
        // "walk-to ETA / lair ranking looks wrong" report needs to know which
        // model got wired (realm-aware Auto vs Flat vs bucketed) and what it
        // predicts for one hop at the moment of capture (live enc% / quickness
        // on Paradigm, the encumbrance bucket on stock).
        Kv(sb, "Travel cost model", svc.AutoLair.TravelCostModel.GetType().Name);
        Kv(sb, "Travel per-hop estimate",
            $"{svc.AutoLair.TravelCostModel.EstimateTravel(1).TotalSeconds:0.00} s");
        Kv(sb, "Auto-deposit reroute", svc.AutoDeposit.RerouteStatus);
        // Roomba Mode (GhSweepManager) — a "sweep won't start / got stuck"
        // report needs the phase, lap count, and how much of the sort queue
        // is still outstanding.
        Kv(sb, "Roomba mode", svc.GhSweep.Mode.ToString());
        Kv(sb, "Roomba sweep phase", svc.GhSweep.Phase.ToString());
        Kv(sb, "Roomba recon laps done", svc.GhSweep.CompletedReconLaps.ToString());
        Kv(sb, "Roomba completed sort laps", svc.GhSweep.CompletedSortLaps.ToString());
        Kv(sb, "Roomba searches per room", svc.GhRoomLabels.SearchesPerRoom.ToString());
        Kv(sb, "Roomba labeled / actively-managed (this char) / circuit rooms",
            $"{svc.GhRoomLabels.Labels.Count} / {svc.GhManagedRooms.Count} / {svc.GhSweep.CircuitRoomCount}");
        Kv(sb, "Roomba moved / left / pending / carried / hidden",
            $"{svc.GhSweep.MovedSoFar.Count} / {svc.GhSweep.LeftInPlace.Count} / "
            + $"{svc.GhSweep.PendingMoveCount} / {svc.GhSweep.CarriedPendingCount} / "
            + $"{svc.GhSweep.HiddenPendingCount}");
        // Full-ledger carry state — a "sweep stranded everything" or "won't pick up"
        // report needs the tracked working budget, what the ledger thinks is carried,
        // the live headroom, and how many items were left as too-heavy.
        Kv(sb, "Roomba working budget / carried / headroom",
            svc.GhSweep.WorkingWeightBudget == int.MaxValue
                ? "(no weight data)"
                : $"{svc.GhSweep.WorkingWeightBudget} / {svc.GhSweep.LedgerCarriedWeightNow} / "
                    + $"{svc.GhSweep.CarryHeadroomNow}");
        Kv(sb, "Roomba left too-heavy",
            svc.GhSweep.LeftInPlace.Count(f => f.Reason == GhLeftReason.TooHeavy).ToString());

        // Default-task startup state — a "my loop / Auto-Lair didn't start on
        // login" report needs to know whether the runner deferred the start
        // behind the party-reform hold and whether that hold is still counting.
        Kv(sb, "Default task party-hold armed", svc.DefaultTaskRunner.PendingPartyRebuildHold.ToString());
        Kv(sb, "Default task holding now", svc.DefaultTaskRunner.IsHoldingForParty.ToString());

        // Recovery gate + Paradigm rm re-sync — a "walker got lost / stuck
        // mid-walk" report needs the tier the gate climbed to, the anchor it
        // last held, and whether an authoritative-position round-trip was
        // pending (Paradigm). Without these a drift report can't tell a normal
        // walk from one stalled waiting on an `rm` that never answered.
        Kv(sb, "Recovery tier", svc.Recovery.CurrentTier.ToString());
        Kv(sb, "Recovery anchor",
            svc.Recovery.Anchor is { } anc ? $"{anc.Map}/{anc.Room}" : "(none)");
        Kv(sb, "Awaiting rm resync", svc.Recovery.AwaitingAuthoritativeResync.ToString());
        var resync = svc.ParadigmResync;
        Kv(sb, "rm resync enabled", resync.Enabled.ToString());
        Kv(sb, "rm request in flight", resync.RequestInFlight.ToString());
        Kv(sb, "rm requested at",
            resync.LastRequestedAt is { } req ? req.ToLocalTime().ToString("HH:mm:ss") : "(idle)");
        Kv(sb, "rm last resolved",
            resync.LastResolved is { } res ? $"{res.Map}/{res.Room}" : "(none)");

        var roomState = svc.RoomTracker.State;
        Kv(sb, "Current room",
            roomState.CurrentRoom is { } room ? $"{room.Key.Map}/{room.Key.Room} — {room.DisplayName}" : "(unknown)");
        Kv(sb, "Room confidence", roomState.Confidence.ToString());
        // A room whose cast-on-enter Spell strips buffs suppresses auto-bless —
        // a "buffs won't cast here" report needs this to explain the silence.
        Kv(sb, "Room strips buffs",
            svc.RoomBuffStrip.StripsBuffs(roomState.CurrentRoom?.Spell ?? 0).ToString());
        // Dark rooms print no name/exits/"Also here:", so the walker infers
        // position from moves and combat from attack lines. A "stuck in the dark"
        // report needs this flag to explain why the room display looks empty.
        Kv(sb, "In dark room", svc.RoomTracker.IsInDarkRoom.ToString());
        // Suspect-strike count + the last observation's exit sets drive the
        // walker's hidden-search / lost-recovery decisions — the exact inputs a
        // "walker got lost / re-searched" report needs.
        Kv(sb, "Suspect strikes", roomState.SuspectStrikes.ToString());
        Kv(sb, "Observed exits",
            roomState.ObservedExitDirections is { Count: > 0 } obs
                ? string.Join(", ", obs) : "(none observed)");
        Kv(sb, "Open-door exits",
            roomState.OpenDoorDirections is { Count: > 0 } doors
                ? string.Join(", ", doors) : "(none)");
        // RoomTracker anchors its timestamps in UTC (DateTimeOffset.UtcNow); the
        // rest of the report uses local .Now. The two are the same absolute
        // instant so all the tracker's comparisons work either way, but printing
        // the raw value would show the UTC hour next to local ones — normalize.
        Kv(sb, "Last move sent", svc.RoomTracker.LastMoveSentAt?.ToLocalTime().ToString("HH:mm:ss") ?? "(never)");
        // Sysop room-status capability. A "recovery didn't work" report needs to
        // distinguish never-enabled from enabled-but-the-BBS-refused: the probe
        // turns itself off after one unanswered attempt, and that leaves no other
        // trace in the report.
        Kv(sb, "Sysop status probe",
            svc.SysStatus.Available ? "available"
            : svc.SysStatus.AutoDisabled ? "auto-disabled (no room block came back)"
            : "off (no sysop powers set for this BBS)");

        IReadOnlyList<Game.Map.RoomKey> history = svc.RoomTracker.GetHistory();
        if (history.Count > 0)
        {
            sb.Append("\nRecent confirmed positions (newest first): ");
            sb.Append(string.Join(", ", history.Take(10).Select(k => $"{k.Map}/{k.Room}")));
            sb.Append('\n');
        }

        // Active boss timers at capture — a "boss timer / @timer looks wrong"
        // report needs the tracked set (name + time-to-full + next window).
        var bossTimers = svc.BossTimers.ActiveTimers(svc.GameData.ActiveRealm);
        Kv(sb, "Active boss timers", bossTimers.Count.ToString());
        foreach (var (def, state) in bossTimers.Take(15))
            Kv(sb, $"  {def.Name}",
                $"full {Game.Map.BossTimerMath.FormatHours(state.FullRemaining.TotalHours)}, "
                + $"next {state.NextLabel} {Game.Map.BossTimerMath.FormatHours(state.NextRemaining.TotalHours)}");

        return sb.ToString();
    }

    // The navigation engines the Movement section only summarizes: the
    // point-to-point walk engine (absent above — Movement covers loops/lairs but
    // not a plain "go to room X" walk), the obstacle FSMs a walk stalls on
    // (door / hidden-exit / trap), and the path-item detour routers. A "walker
    // stuck / took a wrong route / stalled on a door" report needs the walk's
    // live target + progress + last stop reason and which obstacle handler is
    // mid-request — the exact internals the log only hints at.
    private static string BuildNavigationEngines(AppServices svc)
    {
        StringBuilder sb = new();

        sb.Append("**Walk engine (point-to-point)**\n\n");
        Game.Map.AutoWalkManager walker = svc.Walker;
        Kv(sb, "State", walker.State.ToString());
        Kv(sb, "Destination", walker.Destination is { } dest ? $"{dest.Map}/{dest.Room}" : "(none)");
        if (walker.StepCount > 0)
            Kv(sb, "Progress", $"step {Math.Min(walker.CurrentStepIndex + 1, walker.StepCount)}/{walker.StepCount}");
        // A voyage in flight is the state most likely to hang (captain refused
        // boarding, arrival mismatch) — surface the sailing target + ETA so a
        // report captured mid-sail pins down which crossing stalled.
        if (walker.IsSailing)
            Kv(sb, "Sailing", $"to {walker.SailingDestinationName ?? "(port)"}, arriving in "
                + $"{Math.Max(0, (walker.SailingArrivalEta - DateTimeOffset.UtcNow).TotalSeconds):F0}s");
        Kv(sb, "Journey origin (flee anchor)",
            walker.JourneyOrigin is { } origin ? $"{origin.Map}/{origin.Room}" : "(none)");
        Kv(sb, "Next planned direction",
            walker.PeekNextPlannedDirection() is { } dir ? dir.ToString() : "(none / command step)");
        // The retained last event carries the failure/stop reason (Detail) — the
        // single most useful line for "why did the walk quit".
        Kv(sb, "Last walk event",
            walker.LastEvent is { } ev
                ? $"{ev.Kind}: {ev.Detail}" + (ev.Destination is { } d ? $" → {d.Map}/{d.Room}" : string.Empty)
                : "(none yet)");

        sb.Append("\n**Obstacle handlers (door / hidden exit / trap)**\n\n");
        Game.Map.DoorOpenManager door = svc.Door;
        Kv(sb, "Door FSM", $"{door.CurrentState}"
            + (door.CurrentDirection is { } dd ? $", dir={dd}" : string.Empty)
            + (door.QueueDepth > 0 ? $", queued={door.QueueDepth}" : string.Empty));
        Game.Map.HiddenExitRevealManager hidden = svc.HiddenSearch;
        Kv(sb, "Hidden-exit search", hidden.IsBusy
            ? $"searching dir={hidden.CurrentDirection ?? "(none)"}, queued={hidden.QueueDepth}"
            : hidden.QueueDepth > 0 ? $"idle, queued={hidden.QueueDepth}" : "idle");
        Game.TrapDisarmManager trap = svc.TrapDisarm;
        Kv(sb, "Trap disarm", $"{trap.CurrentState}"
            + (trap.CurrentDirection is { } td ? $", dir={td}" : string.Empty)
            + (trap.QueueDepth > 0 ? $", queued={trap.QueueDepth}" : string.Empty)
            + $", canDisarm={trap.CanDisarm}, trapsStat={svc.PlayerStats.Traps}"
            + $", skillFromClassRace={trap.SkillInferredFromClassOrRace}");

        sb.Append("\n**Path-item detours**\n\n");
        Kv(sb, "Path-item search demand", svc.PathItemDemand.SearchDemandActive.ToString());
        Kv(sb, "Party path-item search demand", svc.PartyPathItemGate.SearchDemandActive.ToString());
        Kv(sb, "Give detour active", svc.PathItemGiveRouter.DetourActive.ToString());
        Kv(sb, "Shop-buy detour active", svc.PathItemShopRouter.DetourActive.ToString());
        Kv(sb, "Monster-drop hunt detour active", svc.MonsterDropRouter.DetourActive.ToString());

        IReadOnlyList<Need> needs = svc.Needs.Outstanding(NeedKind.PathItem);
        sb.Append("\nOutstanding path-item needs (").Append(needs.Count).Append(")\n\n");
        if (needs.Count == 0) sb.Append("_(none)_\n");
        else foreach (Need n in needs)
            sb.Append("- ").Append(n.Descriptor).Append(" ×").Append(n.Quantity)
              .Append(" (requester: ").Append(n.Requester).Append(")\n");

        // Checkspell hazard-buff provisioning for the CURRENT room — a "walked into
        // the desert / drowned without `use`ing the waterskin" report needs whether
        // the room the character stands in is a buff-gated hazard, which item raises
        // the buff, and whether one is on hand for the provisioner to `use`.
        sb.Append("\n**Room hazard (current)**\n\n");
        Game.Map.Room? here = svc.RoomTracker.State.CurrentRoom;
        RoomHazardIndex.RoomHazard? hazard = here is { Spell: > 0 }
            ? svc.RoomHazards.HazardForSpell(here.Spell) : null;
        if (hazard is null || hazard.BuffCounters.Count == 0)
            Kv(sb, "Checkspell hazard", "(none — current room needs no buff counter)");
        else foreach (RoomHazardIndex.BuffCounter bc in hazard.BuffCounters)
        {
            List<string> names = bc.SourceItems
                .Select(id => svc.ItemNames.GetName(id))
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
            // Approximate carried check: the dump lists carried names (sometimes
            // count-prefixed), so a substring match tolerates "3 waterskins".
            bool carried = names.Any(n => svc.Inventory.Snapshot.CarriedItems
                .Any(c => c.Contains(n, StringComparison.OrdinalIgnoreCase)));
            string label = names.Count > 0 ? string.Join(" / ", names) : "(unnamed source)";
            // LapseSpell 0 means the buff-absent damage cast wasn't derivable from
            // the checkspell chain, so the reactive re-raise (fire on the lapse
            // prompt) can't arm — only the predictive timer holds the buff.
            string lapse = bc.LapseSpell > 0
                ? bc.LapseSpell.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "none — reactive re-raise off";
            // A held immunity guard (worn sunstone) makes the `use` a no-op — surface
            // which guards exist and whether one is held, so a report shows WHY a buff
            // was (or wasn't) raised.
            List<string> immunityNames = bc.ImmunityItems
                .Select(id => svc.ItemNames.GetName(id))
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
            bool immune = immunityNames.Any(n => svc.Inventory.Snapshot.CarriedItems
                .Any(c => c.Contains(n, StringComparison.OrdinalIgnoreCase)));
            string immunity = immunityNames.Count > 0
                ? $", immunity: {string.Join(" / ", immunityNames)} ({(immune ? "held — no `use` needed" : "not held")})"
                : "";
            Kv(sb, $"Buff {bc.BuffSpell}",
                $"{label} (dur ~{bc.DurationSeconds}s, carried: {(carried ? "yes" : "no")}, "
                + $"lapse spell: {lapse}{immunity})");
        }

        // Random-teleport maze solver — a "walker never reaches the asylum room /
        // spins forever teleporting" report needs whether the solver engaged, its
        // goal, which phase it's stuck in, and how many reshuffles it's burned.
        sb.Append("\n**Teleport-maze solver**\n\n");
        Game.Map.TeleportMazeSolver maze = svc.MazeSolver;
        Kv(sb, "Enabled", maze.Enabled.ToString());
        Kv(sb, "Pockets indexed", svc.MazeIndex.HasMazes.ToString());
        Kv(sb, "Active", maze.Active.ToString());
        Kv(sb, "Phase", maze.PhaseName);
        Kv(sb, "Goal", maze.Goal is { } mg ? $"{mg.Map}/{mg.Room}" : "(none)");
        Kv(sb, "Reshuffle attempts", maze.Attempts.ToString());

        // Great Pyramid climb solver — a "walker won't climb the pyramid / scattered
        // out" report needs whether it engaged, which floor + phase it reached, its
        // goal, and how many steps it drove before halting.
        sb.Append("\n**Pyramid solver**\n\n");
        Game.Map.PyramidSolver pyr = svc.PyramidSolver;
        Kv(sb, "Enabled", pyr.Enabled.ToString());
        Kv(sb, "Active", pyr.Active.ToString());
        Kv(sb, "Floor", pyr.FloorName);
        Kv(sb, "Phase", pyr.PhaseName);
        Kv(sb, "Goal", pyr.Goal is { } pg ? $"{pg.Map}/{pg.Room}" : "(none)");
        Kv(sb, "Steps driven", pyr.StepsDriven.ToString());

        return sb.ToString();
    }

    // The live Exp/Hr Estimator session, if one's active — route, tunables, and the
    // computed estimate + per-lair breakdown the user was looking at. The estimator
    // is a build-time UI tool whose state lives only on the Navigation view-model, so
    // it reaches the report through a snapshot provider the VM registers on
    // AppServices; null (provider unset or not estimating) reads as inactive. A
    // "the exp/hr number looks wrong" report needs exactly this to reproduce.
    private static string BuildExpEstimator(AppServices svc)
    {
        Game.Map.ExpEstimatorSnapshot? snap = svc.ExpEstimatorSnapshotProvider?.Invoke();
        if (snap is null) return "_(estimator not active)_";

        StringBuilder sb = new();
        Kv(sb, "Proposed loop name", snap.ProposedName);
        Kv(sb, "Estimate", $"{snap.ExpPerHour:N0} exp/hr");
        Kv(sb, "Laps", $"{snap.LapsPerHour} laps/hr · {snap.AvgLapSeconds:N1}s/lap");
        Kv(sb, "Summary", snap.Summary);
        Kv(sb, "Combat mode", snap.AreaCombat ? "area (rooming)" : "single-target");
        Kv(sb, "Seconds per step", $"{snap.SecondsPerStep:0.0}");
        Kv(sb, "Rounds to kill a mob", $"{snap.RoundsPerMob:0.0}");
        Kv(sb, "Real-world multiplier", $"{snap.RealConditionsMultiplier:0.00}");

        sb.Append("\n**Route** (").Append(snap.Rooms.Count).Append(")\n\n");
        if (snap.Rooms.Count == 0) sb.Append("_(none)_\n");
        else foreach (string r in snap.Rooms) sb.Append("- ").Append(r).Append('\n');

        sb.Append("\n**Per-lair** (").Append(snap.Lairs.Count).Append(")\n\n");
        if (snap.Lairs.Count == 0) sb.Append("_(none)_\n");
        else foreach (string l in snap.Lairs) sb.Append("- ").Append(l).Append('\n');

        if (snap.Bosses.Count > 0)
        {
            sb.Append("\n**Bosses** (").Append(snap.Bosses.Count).Append(")\n\n");
            foreach (string b in snap.Bosses) sb.Append("- ").Append(b).Append('\n');
        }

        if (snap.Summons.Count > 0)
        {
            sb.Append("\n**Room summons** (").Append(snap.Summons.Count).Append(")\n\n");
            foreach (string su in snap.Summons) sb.Append("- ").Append(su).Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildRoomMarkers(AppServices svc)
    {
        StringBuilder sb = new();

        var avoided = svc.RoomBlacklist.Entries;
        sb.Append("**Avoid rooms** (").Append(avoided.Count).Append(")\n\n");
        if (avoided.Count == 0) sb.Append("_(none)_\n");
        else foreach (var r in avoided) sb.Append("- ").Append(r.Map).Append('/').Append(r.Room)
            .Append(" — ").Append(r.Name).Append('\n');

        var profile = svc.Profile.Current;
        var stash = profile?.StashRooms;
        sb.Append("\n**Stash rooms** (").Append(stash?.Count ?? 0).Append(")\n\n");
        if (stash is not { Count: > 0 }) sb.Append("_(none)_\n");
        else foreach (var r in stash) sb.Append("- ").Append(r.Map).Append('/').Append(r.Room).Append('\n');

        // Only the starred quick-access favourites — the full GOTO list runs to
        // hundreds of entries and bloats the report without helping diagnosis.
        var favorites = svc.Favorites.StarredFavorites();
        sb.Append("\n**Starred favorites** (").Append(favorites.Count).Append(")\n\n");
        if (favorites.Count == 0) sb.Append("_(none)_\n");
        else foreach (var f in favorites)
        {
            sb.Append("- ").Append(f.Map).Append('/').Append(f.Room);
            if (!string.IsNullOrWhiteSpace(f.Label)) sb.Append(" — ").Append(f.Label);
            if (!string.IsNullOrWhiteSpace(f.Folder)) sb.Append("  (folder: ").Append(f.Folder).Append(')');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildAutoMode(AppServices svc)
    {
        StringBuilder sb = new();
        Kv(sb, "Kill-switch engaged", svc.AutoModeController.KillSwitchEngaged.ToString());
        Kv(sb, "All wired engines off", svc.AutoModeController.AllWiredOff.ToString());
        sb.Append("\nPer-engine toggles live in the `General` settings block below: `AutoMode` is the live toolbar state, `AutoModeBase` the base defaults reconciled onto it at profile load / loop / auto-lair start (null = pre-split character, treated as equal to `AutoMode`).\n");
        return sb.ToString();
    }

    // Per-character built-in keybindings — the chord bound to each app action,
    // flagged where it deviates from the shipped default. A "hotkey does
    // nothing" or "my Ctrl+S rebind stopped reaching the terminal" report hinges
    // on whether the user rebound the chord, which only lives in this per-
    // character store (deltas persist to CharacterProfile.BuiltInKeybindings).
    private static string BuildKeybindings(AppServices svc)
    {
        KeybindingStore store = svc.Keybindings;
        StringBuilder sb = new();
        foreach (Models.Profile.BuiltInAction action in Enum.GetValues<Models.Profile.BuiltInAction>())
        {
            Models.Profile.KeyChord chord = store.Get(action);
            Models.Profile.KeyChord def =
                KeybindingStore.DefaultBindings.TryGetValue(action, out Models.Profile.KeyChord d)
                    ? d : Models.Profile.KeyChord.Empty;
            string bound = chord.IsEmpty ? "(unbound)" : chord.Label;
            string suffix = chord.Equals(def)
                ? string.Empty
                : $"  — changed (default: {(def.IsEmpty ? "(unbound)" : def.Label)})";
            Kv(sb, KeybindingStore.ActionLabel(action), bound + suffix);
        }
        return sb.ToString();
    }

    // Fully-resolved effective values for every gameplay / automation section,
    // merged across all four tiers. The delta-only per-tier dump below hides any
    // knob left at its default — but "what behavior should be happening" is
    // exactly those defaults (combat target order/priority, attack timing, flee
    // thresholds, etc.). A triager can't reason about a combat report without
    // seeing the effective priority even when the user never overrode it, so we
    // dump the resolved DTO for each section here regardless of override state.
    private static string BuildEffectiveSettings(AppServices svc)
    {
        StringBuilder sb = new();
        sb.Append("Merged Defaults → Global → BBS → Character values — the actual knobs the engines read, ")
          .Append("including ones left at their defaults. The per-tier override deltas are in the next section.\n\n");

        AppendResolved<Models.Profile.CombatSettings>(sb, svc, "Combat");
        AppendResolved<Models.Profile.PartySettings>(sb, svc, "Party");
        AppendResolved<Models.Profile.HealthSettings>(sb, svc, "Health");
        AppendResolved<Models.Profile.SpellsSettings>(sb, svc, "Spells");
        AppendResolved<Models.Profile.GeneralSettings>(sb, svc, "General");
        AppendResolved<Models.Profile.OtherSettings>(sb, svc, "Other");
        AppendResolved<Models.Profile.CashSettings>(sb, svc, "Cash");
        AppendResolved<Models.Profile.TalkSettings>(sb, svc, "Talk");
        AppendResolved<Models.Profile.AutoLightSettings>(sb, svc, "AutoLight");
        AppendResolved<Models.Profile.AutoLairSettings>(sb, svc, "AutoLair");
        AppendResolved<Models.Profile.AutoTrainerSettings>(sb, svc, "AutoTrainer");
        return sb.ToString();
    }

    // Resolve one tab-keyed section across the tier hierarchy and emit it as a
    // labelled JSON block. Isolated per-section so one section failing to
    // resolve leaves the rest intact.
    // Casting spell profiles: how many exist, which is active, and every profile's
    // FULL held config — each slot (empty shown as —) with its gates, plus the
    // mana-mode / drain-trigger / drains-override knobs, the active one flagged. So
    // a "wrong spells firing" report shows which profile was live, what it held, and
    // what the others hold. Spells by cast code, never full name.
    private static string BuildCombatProfiles(AppServices svc)
    {
        System.Collections.Generic.IReadOnlyList<Models.Profile.CombatSpellProfile> profiles =
            svc.CombatProfiles.Profiles;
        if (profiles.Count == 0) return "No combat spell profiles.";
        int active = svc.CombatProfiles.ActiveIndex;

        StringBuilder sb = new();
        string activeLabel = active >= 0 && active < profiles.Count
            ? (string.IsNullOrWhiteSpace(profiles[active].Name)
                ? $"profile {active + 1}"
                : $"profile {active + 1} ({profiles[active].Name.Trim()})")
            : "(none)";
        sb.Append(profiles.Count).Append(profiles.Count == 1 ? " profile" : " profiles")
          .Append(". Active: ").Append(activeLabel).Append(".\n\n");

        for (int i = 0; i < profiles.Count; i++)
        {
            sb.Append(Game.Combat.CombatSpellProfileReport.DescribeConfig(profiles[i], i + 1));
            if (i == active) sb.Append("  (ACTIVE)");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void AppendResolved<T>(StringBuilder sb, AppServices svc, string tabKey)
        where T : class, new()
    {
        sb.Append("**").Append(tabKey).Append("**\n\n");
        try
        {
            sb.Append(Json(svc.Resolver.Resolve<T>(tabKey)));
        }
        catch (Exception ex)
        {
            sb.Append("_(could not resolve: ").Append(ex.Message).Append(")_\n");
        }
        sb.Append('\n');
    }

    private static string BuildSettings(AppServices svc)
    {
        StringBuilder sb = new();
        AppendTier(sb, "Global tier", svc.Settings.Current.Settings);

        string? bbsName = svc.Profile.CurrentBbsName;
        var bbsSettings = bbsName is null ? null : svc.Bbs.Get(bbsName)?.Settings;
        AppendTier(sb, "BBS tier", bbsSettings);

        AppendTier(sb, "Character tier", svc.Profile.Current?.Settings);
        return sb.ToString();
    }

    // Emit one settings tier's deltas as JSON, dropping any BBS / Display keys
    // per the "everything except BBS + Display" scope. Those live in separate
    // stores today (BbsProfileStore / DisplayConfig), so this is belt-and-
    // braces — the tab dictionary shouldn't contain them anyway.
    private static void AppendTier(StringBuilder sb, string label, Dictionary<string, JsonElement>? tier)
    {
        sb.Append("**").Append(label).Append("**\n\n");
        if (tier is not { Count: > 0 })
        {
            sb.Append("_(no overrides)_\n\n");
            return;
        }

        Dictionary<string, JsonElement> filtered = tier
            .Where(kv => !kv.Key.Equals("Bbs", StringComparison.OrdinalIgnoreCase)
                      && !kv.Key.Equals("Display", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (filtered.Count == 0) sb.Append("_(only BBS/Display overrides — omitted)_\n\n");
        else sb.Append(Json(filtered)).Append('\n');
    }

    private static string BuildLog(AppServices svc)
    {
        LogEntry[] entries = svc.Log.Snapshot();
        int take = Math.Min(LogLines, entries.Length);
        if (take == 0) return "_(log empty)_";

        StringBuilder sb = new();
        // Both diagnostic channels off ⇒ the tail below is Info-only; the
        // engines' Debug/Combat decision traces were never generated. Flag it so
        // a triager doesn't read the absence of a trail as the engine going quiet.
        bool debugOn = svc.Log.Diagnostics?.DebugDiagnostics ?? false;
        bool combatOn = svc.Log.Diagnostics?.CombatDiagnostics ?? false;
        if (!debugOn && !combatOn)
            sb.Append("> Debug + Combat diagnostics were off — no decision-trail entries below. ")
              .Append("Enable them in the Log pane and reproduce for a fuller capture.\n\n");
        sb.Append("Last ").Append(take).Append(" of ").Append(entries.Length).Append(" entries.\n\n```\n");
        for (int i = entries.Length - take; i < entries.Length; i++)
        {
            LogEntry e = entries[i];
            sb.Append(e.Timestamp.ToString("HH:mm:ss")).Append("  [").Append(e.Severity).Append("]  ")
              .Append(e.Source).Append(": ").Append(e.Message).Append('\n');
        }
        sb.Append("```");
        return sb.ToString();
    }

    // The raw ANSI wire, last ScrollbackLines lines (non-printables shown as the
    // Wire Inspector renders them). Only emitted when the Raw pane is visible.
    private static string BuildRawWire(AppServices svc)
    {
        string raw = WireFormatter.RenderRaw(svc.Wire.Snapshot());
        string tail = LastLines(raw, ScrollbackLines);
        if (tail.Length == 0) return "_(no wire captured)_";
        return "```\n" + tail + "\n```";
    }

    // The classified combat trace, last ScrollbackLines combat-window lines each
    // tagged with how the recognizer read it. Only emitted when the Classified pane
    // is visible.
    private static string BuildClassifiedWire(AppServices svc)
    {
        string log = svc.CombatClassifier.RenderLog(ScrollbackLines).TrimEnd('\n');
        if (log.Length == 0) return "_(no combat lines classified yet)_";
        return "```\n" + log + "\n```";
    }

    // The last n newline-delimited lines of s (trailing blank line ignored).
    private static string LastLines(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        string[] lines = s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        int skip = Math.Max(0, lines.Length - n);
        return string.Join('\n', lines.Skip(skip));
    }

    private static string BuildScrollback(TerminalEmulator emulator)
    {
        // Every content row carries the instant its content was written — the same
        // per-row write stamp whether it has scrolled off into the ring or is still
        // on screen, so the timestamps stay in order and line up against the program
        // log (e.g. matching a nag-cancel log line to the telepath that triggered
        // it, or a combat resume to the buff that interrupted it). Only blank
        // spacing rows have no time.
        IReadOnlyList<TranscriptSnapshot.Line> lines =
            TranscriptSnapshot.Tail(emulator, ScrollbackLines);
        if (lines.Count == 0) return "_(nothing on screen yet)_";

        StringBuilder sb = new();
        sb.Append("Last ").Append(lines.Count)
          .Append(" line(s), each prefixed with its write time (blank spacing rows have none).\n\n```\n");
        foreach (TranscriptSnapshot.Line line in lines)
        {
            sb.Append(line.Timestamp is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "        ")
              .Append(' ').Append(line.Text).Append('\n');
        }
        sb.Append("```");
        return sb.ToString();
    }

    // ----- Helpers -------------------------------------------------------

    private static string RealmLabel(RealmType realm) => realm switch
    {
        RealmType.ParaMud => "paradigm",
        _ => "stock",
    };

    private static void Kv(StringBuilder sb, string key, string value)
        => sb.Append("- **").Append(key).Append("**: ").Append(value).Append('\n');

    // The item worn in a given inventory slot (e.g. "Weapon Hand"), or null when
    // that slot is empty / the loadout hasn't been parsed yet.
    private static string? WornSlot(InventorySnapshot inv, string slot)
    {
        foreach (EquippedItem e in inv.EquippedItems)
            if (string.Equals(e.Slot, slot, StringComparison.OrdinalIgnoreCase))
                return e.Name;
        return null;
    }

    // Serialize value into a fenced JSON block.
    private static string Json(object? value)
    {
        try
        {
            return "```json\n" + JsonSerializer.Serialize(value, JsonStore.Options) + "\n```\n";
        }
        catch (Exception ex)
        {
            return $"_(could not serialize: {ex.Message})_\n";
        }
    }

    // Run a section builder, converting any throw into an inline note.
    private static string SafeSection(Func<string> build)
    {
        try { return build(); }
        catch (Exception ex) { return $"_(capture failed: {ex.Message})_"; }
    }

    private static T Guard<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
