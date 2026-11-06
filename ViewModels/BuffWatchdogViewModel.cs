using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Read-only Buff Watchdog window VM. Lists the buffs the character has CONFIGURED
// (self-bless slots + HP/MA-regen + when-full + #item-cast, and party-bless slots),
// each with a live timer bar and a recast-window marker. The row list is (re)built
// from config; each 1-second heartbeat refills the bars from the CastingDirector
// timer snapshot. Never lists the whole learnable spellbook — only what's configured.
public sealed partial class BuffWatchdogViewModel : ObservableObject, IDisposable
{
    private readonly CastingDirector _castDirector;
    private readonly SpellbookState _spellbook;
    private readonly Game.TickEngine _tick;
    private readonly ProfileService _profile;
    private readonly Func<SpellsSettings> _readSpells;
    private readonly Func<BuffSettings?> _readPartyBuffs;
    // Live party roster (non-self), so a single-target party buff can list ONE ROW
    // PER targeted member. Null on the test ctor (no per-member rows there).
    private readonly Game.PartyState? _party;
    // Your own IN-GAME character name (PlayerStats.Name, parsed from the game), for
    // the self section header — NOT the profile name, which can differ. Null on the
    // test ctor / before the statline is parsed.
    private readonly Func<string?>? _readSelfName;
    private readonly Game.Tokens.TokenTracker? _tokens;

    private string _configSignature = string.Empty;
    private bool _needsRebuild = true;
    private bool _disposed;

    // Per whole-party buff (by cast code): the member given-names in the party the last
    // time it was cast, and the timer's expiry we captured them at. Recast is driven by
    // OUR OWN timer only — but a whole-party buff lands only on who was present, so a
    // member who swaps in after the cast isn't covered until the next recast. We snapshot
    // the roster when the timer first appears / jumps forward (a recast) and render a
    // "not up" row for any current member not in the set, flagging who's missing it.
    private readonly Dictionary<string, (DateTime Until, HashSet<string> Covered)> _wholePartyCoverage =
        new(StringComparer.OrdinalIgnoreCase);

    // Live buff-timer bars, grouped by the player each buff is on: your own name
    // (self + whole-party buffs) first, then one section per party member carrying
    // the buffs cast on them.
    public ObservableCollection<BuffWatchdogPlayerGroup> Groups { get; } = new();

    [ObservableProperty] private bool _isEmpty;

    // True while a Paradigm transport-token use is holding buffing (the token's negate
    // magic is about to wipe every buff). Surfaced as a status line in the window;
    // cleared when the token fires or its 30s safety window times out. Driven by
    // TokenTracker.BuffPauseChanged.
    [ObservableProperty] private bool _tokenPauseActive;
    public string TokenPauseStatus => "Paused by token usage";

    // Window layout: whether the config table sits above / below / left / right of the
    // timer bars. Chosen in Settings → General (persisted on
    // CharacterProfile.BuffWatchdogLayout); the window's code-behind reads this to
    // arrange the two zones + the drag splitter. Reloaded live on profile load /
    // mutate so a Settings Apply reflows the open window at once.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigToggleGlyph))]
    private BuffWatchdogLayout _layout = BuffWatchdogLayout.ConfigTop;

    // Whether the config panel is collapsed (bars-only), toggled by the button on the
    // timer-bar side. The code-behind watches this to reflow the zones; persisted per
    // character (CharacterProfile.BuffWatchdogConfigCollapsed) so it reopens as left.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(ConfigToggleTooltip))]
    private bool _configCollapsed;

    // An arrow that points the direction the divider moves on the next click (same
    // convention as the nav map's collapse chip): while the config panel is SHOWN the
    // arrow points toward it (collapse it away); while COLLAPSED it points back toward
    // where it'll reappear (expand it). Depends on which side the panel is on.
    public string ConfigToggleGlyph => Layout switch
    {
        BuffWatchdogLayout.ConfigTop    => ConfigCollapsed ? "▼" : "▲",
        BuffWatchdogLayout.ConfigBottom => ConfigCollapsed ? "▲" : "▼",
        BuffWatchdogLayout.ConfigLeft   => ConfigCollapsed ? "▶" : "◀",
        BuffWatchdogLayout.ConfigRight  => ConfigCollapsed ? "◀" : "▶",
        _                               => ConfigCollapsed ? "▼" : "▲",
    };
    public string ConfigToggleTooltip => ConfigCollapsed
        ? "Show the buff-config panel"
        : "Hide the buff-config panel (bars only)";

    // Flip the config panel's collapsed state and remember it on the character.
    [RelayCommand]
    private void ToggleConfig()
    {
        ConfigCollapsed = !ConfigCollapsed;
        if (_profile.Current is { } p && p.BuffWatchdogConfigCollapsed != ConfigCollapsed)
        {
            p.BuffWatchdogConfigCollapsed = ConfigCollapsed;
            _profile.Save();
        }
    }

    // Where the user last dragged the config/bars splitter (the config pane's fixed
    // extent in DIPs) and which orientation it was for — loaded from the character so the
    // window reopens at the same division. The code-behind reads these on open and writes
    // them back via SaveConfigExtent on close. 0 = never dragged (use the layout default).
    public double ConfigExtent { get; private set; }
    public bool ConfigExtentVertical { get; private set; }

    // Persist the splitter position (called by the code-behind on window close). No-op
    // when unchanged so a close without a drag doesn't rewrite the profile.
    public void SaveConfigExtent(double extent, bool vertical)
    {
        if (extent <= 0) return;
        if (_profile.Current is not { } p) return;
        if (p.BuffWatchdogConfigExtent == extent && p.BuffWatchdogConfigExtentVertical == vertical) return;
        ConfigExtent = extent;
        ConfigExtentVertical = vertical;
        p.BuffWatchdogConfigExtent = extent;
        p.BuffWatchdogConfigExtentVertical = vertical;
        _profile.Save();
    }

    // The editable buff-config panel (add / edit / remove / target). It lives in this
    // window now — the Buff Watchdog is the single place to both SEE and CONFIGURE
    // buffs. Null on the test ctor (no live services).
    public BuffPanelViewModel? Buffs { get; }

    // Production ctor — pulls the live services. Settings come through the resolver
    // (4-tier merged; bless slots live at the character tier, which wins).
    public BuffWatchdogViewModel()
        : this(AppServices.Current.CastDirector, AppServices.Current.Spellbook,
               AppServices.Current.Tick, AppServices.Current.Profile,
               () => AppServices.Current.Resolver.Resolve<SpellsSettings>("Spells"),
               () => AppServices.Current.Profile.Current?.PartyBuffs,
               AppServices.Current.PartyState,
               () => AppServices.Current.PlayerStats.Name,
               AppServices.Current.Tokens)
    {
        Buffs = new BuffPanelViewModel(AppServices.Current.PartyState);
    }

    public BuffWatchdogViewModel(
        CastingDirector castDirector, SpellbookState spellbook,
        Game.TickEngine tick, ProfileService profile,
        Func<SpellsSettings> readSpells, Func<BuffSettings?> readPartyBuffs,
        Game.PartyState? party = null, Func<string?>? readSelfName = null,
        Game.Tokens.TokenTracker? tokens = null)
    {
        _castDirector = castDirector;
        _spellbook = spellbook;
        _tick = tick;
        _profile = profile;
        _readSpells = readSpells;
        _readPartyBuffs = readPartyBuffs;
        _party = party;
        _readSelfName = readSelfName;
        _tokens = tokens;

        _spellbook.Changed += OnSpellbookChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileMutated += OnProfileMutated;
        _tick.HeartbeatElapsed += OnHeartbeat;
        _castDirector.TimersResumed += OnTimersResumed;
        if (_party is not null) _party.Members.CollectionChanged += OnPartyMembersChanged;
        if (_tokens is not null)
        {
            _tokens.BuffPauseChanged += OnTokenBuffPauseChanged;
            _tokenPauseActive = _tokens.IsBuffPausedForToken;
        }

        _layout = _profile.Current?.BuffWatchdogLayout ?? BuffWatchdogLayout.ConfigTop;
        _configCollapsed = _profile.Current?.BuffWatchdogConfigCollapsed ?? false;
        ConfigExtent = _profile.Current?.BuffWatchdogConfigExtent ?? 0;
        ConfigExtentVertical = _profile.Current?.BuffWatchdogConfigExtentVertical ?? false;
        Refresh();
    }

    // The roster changed (a member joined / left) → a single-target buff's per-member
    // rows must be rebuilt so the target columns follow the party.
    private void OnPartyMembersChanged(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __)
        => MarkRebuildAndRefresh();

    // Current non-self party members as (Display, lower-cased given). Empty when solo.
    private List<(string Display, string Given)> CurrentMembers()
    {
        List<(string, string)> members = new();
        if (_party is null) return members;
        foreach (Game.PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            string name = m.Name;
            string given = (name.Split(' ') is { Length: > 0 } parts ? parts[0] : name).ToLowerInvariant();
            members.Add((name, given));
        }
        return members;
    }

    // TokenTracker.BuffPauseChanged (UI thread) — flip the "paused by token usage"
    // status on/off as the pre-token buff-hold opens and releases.
    private void OnTokenBuffPauseChanged() => TokenPauseActive = _tokens?.IsBuffPausedForToken ?? false;

    private void OnSpellbookChanged() => MarkRebuildAndRefresh();
    private void OnProfileLoaded(CharacterProfile p)
    {
        Layout = p.BuffWatchdogLayout;
        ConfigCollapsed = p.BuffWatchdogConfigCollapsed;
        _wholePartyCoverage.Clear();   // a new character starts with no tracked coverage
        MarkRebuildAndRefresh();
    }

    // A Settings Apply mutates the loaded profile in place (no ProfileLoaded), so
    // pick up a layout change chosen in Settings → General while this window is open.
    private void OnProfileMutated(CharacterProfile p) => Layout = p.BuffWatchdogLayout;

    // Keep _wholePartyCoverage in step with the live whole-party timers: snapshot the
    // party roster when a whole-party buff's timer first appears or jumps forward (a
    // recast re-covers the then-current party), and drop coverage once its timer is gone.
    private void ReconcileWholePartyCoverage(BuffSettings? buffs, IReadOnlyList<ActiveBuffTimer> snap)
    {
        HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
        if (buffs is not null)
            foreach (BuffSlot p in buffs.Slots)
            {
                if (string.IsNullOrWhiteSpace(p.Spell)) continue;
                string code = p.Spell.Trim();
                if (!IsWholePartySlot(code)) continue;

                ActiveBuffTimer? wp = null;
                foreach (ActiveBuffTimer t in snap)
                    if (t.Target.Length == 0
                        && string.Equals(t.Short, code, StringComparison.OrdinalIgnoreCase))
                    { wp = t; break; }
                if (wp is not { } timer) continue;

                active.Add(code);
                // First sighting, or a recast pushed the expiry later → (re)snapshot the
                // current members as the covered set. A timer put back from an earlier
                // session's profile covers nobody we can name: that cast wasn't seen
                // by this client, whoever is in the party when its bar first shows.
                if (!_wholePartyCoverage.TryGetValue(code, out (DateTime Until, HashSet<string> Covered) cur)
                    || timer.Until > cur.Until)
                    _wholePartyCoverage[code] = (timer.Until,
                        timer.Restored ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : CurrentMemberGivens());
            }

        // Drop coverage for buffs whose timer is no longer up.
        if (_wholePartyCoverage.Count > 0)
            foreach (string gone in _wholePartyCoverage.Keys.Where(k => !active.Contains(k)).ToList())
                _wholePartyCoverage.Remove(gone);
    }

    // The timers on ourselves ran on after standing still out of the game, the ones
    // named having had their expiry moved on by the time away. The expiries kept here
    // move with exactly those: a later expiry is how a recast is told, and this one is
    // no recast, so the record of who the buff covered stays as it was. (How long it
    // still covers them is another matter: their rows count by ActiveBuffTimer.RanOnUntil.)
    private void OnTimersResumed(TimeSpan away, IReadOnlyCollection<string> moved)
    {
        foreach (string code in moved)
        {
            if (!_wholePartyCoverage.TryGetValue(code, out (DateTime Until, HashSet<string> Covered) cov)) continue;
            _wholePartyCoverage[code] = (cov.Until + away, cov.Covered);
        }
    }

    // Lower-cased given names of the current non-self party members.
    private HashSet<string> CurrentMemberGivens()
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string _, string given) in CurrentMembers()) set.Add(given);
        return set;
    }

    private void MarkRebuildAndRefresh()
    {
        _needsRebuild = true;
        PostRefresh();
    }

    private void OnHeartbeat() => PostRefresh();

    // Manually clear one row's buff timer (the ✕ button) — mark that buff off. A
    // configured, still-due buff recasts on the next evaluation; a phantom timer just
    // drops. Refresh at once so the row updates without waiting for the heartbeat.
    [RelayCommand]
    private void ClearTimer(BuffWatchdogRowViewModel? row)
    {
        if (row is null) return;
        _castDirector.ClearBuffTimer(row.MemberKey, row.CastCode);
        PostRefresh();
    }

    private void PostRefresh()
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        if (_disposed) return;
        SpellsSettings spells = _readSpells();
        BuffSettings? buffs = _readPartyBuffs();
        IReadOnlyList<ActiveBuffTimer> snap = _castDirector.SnapshotActiveBuffs();

        // Refresh whole-party coverage before fingerprinting so a recast that re-covers
        // a new member (or a member joining) reflows the rows.
        ReconcileWholePartyCoverage(buffs, snap);

        string sig = BuildSignature(spells, buffs, snap);
        if (_needsRebuild || sig != _configSignature)
        {
            RebuildRows(spells, buffs, snap);
            _configSignature = sig;
            _needsRebuild = false;
        }
        UpdateTimers();
    }

    private void UpdateTimers()
    {
        IReadOnlyList<ActiveBuffTimer> snap = _castDirector.SnapshotActiveBuffs();
        // While a disconnect has the timers paused, freeze the display at the drop instant
        // (the 1s heartbeat is a wall clock that keeps firing offline). On resume it
        // catches up to the true remaining; the expiries were never shifted.
        DateTime now = _castDirector.PausedAtUtc ?? DateTime.UtcNow;

        // In a party, a self-buff a configured party-wide buff removes shows "covered by"
        // that buff instead of a timer (the director suppresses self-casting it).
        IReadOnlyDictionary<string, string> coverage = _castDirector.CurrentSelfBuffCoverage();
        // Buffs a configured winner PERMANENTLY removes (Paradigm continuous removal, one-
        // directional) aren't maintained on whoever the winner is cast on — those rows show
        // "covered by" the winner instead of a stale timer or a stuck "conflict".
        IReadOnlyDictionary<string, Game.Spells.BuffSuppression> suppressed = _castDirector.CurrentSuppressedBuffs();
        // Stock counterpart: a one-directional loser the director KEEPS by casting the
        // remover first (loser code → remover code). Its timer bar is genuine (it's still
        // maintained), so we don't replace it — we annotate "both kept" and, because the
        // ordering is deliberate, don't surface the transient remover-refresh clobber as a
        // conflict. Empty off stock.
        IReadOnlyDictionary<string, string> collisionKept = _castDirector.CurrentCollisionOrder();
        IReadOnlyCollection<string> hidden = _castDirector.HiddenPartyTargets;
        // Members who negated a buff of ours (BuffSlot.NegatedTargets), keyed (cast code, given).
        HashSet<(string Code, string Given)> negated = new();
        if (_readPartyBuffs() is { } plan)
            foreach (BuffSlot slot in plan.Slots)
                if (!string.IsNullOrWhiteSpace(slot.Spell))
                    foreach (string given in slot.NegatedTargets)
                        negated.Add((slot.Spell.Trim().ToLowerInvariant(), given));

        // Generalized RemovesSpell conflict pairing across ALL slot shapes (self-self,
        // whole-party-vs-whole-party, member-vs-whole-party, member-vs-member) — unlike
        // `coverage` above, this never replaces the timer bar (those buffs are still
        // genuinely being cast); it only annotates the row so the player knows why a
        // buff might be flaky. See AppServices.BuffSlotOverwritePairs.
        IReadOnlyList<Game.Spells.BuffOverwritePair> overwritePairs =
            AppServices.Current.BuffSlotOverwritePairs();

        // A CLOBBERED buff: another live buff whose spell removes it was cast at/after it,
        // so the game stripped it when that one landed — even though its own timer is
        // still ticking here (we never saw a removal line). Keyed (cast-code, target); a
        // whole-party remover and its self-keyed victim share target "". The later-cast
        // survivor is left counting; the clobbered row reads "conflict" (see the row VM).
        HashSet<(string Short, string Target)> clobbered = new();
        foreach (Game.Spells.BuffOverwritePair p in overwritePairs)
            foreach (ActiveBuffTimer removed in snap)
            {
                if (!string.Equals(removed.Short, p.RemovedCode, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime removedCast = removed.Until.AddSeconds(-removed.TotalSec);
                foreach (ActiveBuffTimer remover in snap)
                    if (string.Equals(remover.Short, p.RemovingCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(remover.Target, removed.Target, StringComparison.OrdinalIgnoreCase)
                        && remover.Until.AddSeconds(-remover.TotalSec) > removedCast)
                    {
                        clobbered.Add((removed.Short.ToLowerInvariant(), removed.Target));
                        break;
                    }
            }

        foreach (BuffWatchdogPlayerGroup group in Groups)
        foreach (BuffWatchdogRowViewModel row in group.Rows)
        {
            (string? removedBy, string? removes) = Game.Spells.BuffConflictAnalyzer.Resolve(overwritePairs, row.CastCode);
            row.SetOverwriteWarning(Game.Spells.BuffConflictAnalyzer.FormatTooltip(removedBy, removes));

            // Permanently removed by a configured winner (one-directional, Paradigm) on the
            // characters the winner is cast on: the engine doesn't maintain it there, so
            // those rows read "covered by" the winner rather than a stale timer or a stuck
            // "conflict". A member the winner misses keeps a live row.
            if (suppressed.TryGetValue(row.CastCode, out Game.Spells.BuffSuppression sup) && sup.Covers(row.MemberKey))
            {
                row.SetOverwriteWarning(null);
                row.Update(null, now, coveredBy: sup.WinnerName);
                continue;
            }

            bool isConflicted = clobbered.Contains((row.CastCode.ToLowerInvariant(), row.MemberKey));

            // Stock collision-order: this loser is deliberately kept alongside its remover
            // (cast right after it), so a remover refresh briefly re-stripping it isn't a
            // real conflict — annotate "both kept" and clear the transient flag.
            if (collisionKept.ContainsKey(row.CastCode))
            {
                isConflicted = false;
                if (removedBy is not null)
                    row.SetOverwriteWarning($"Both kept: cast after {removedBy}");
            }

            // Single-target member row (keyed by their given name). A member who's HIDING
            // (a cast came back "You do not see … here!") can't be reached — show that.
            if (row.IsParty && !row.IsWholeParty && row.MemberKey.Length > 0)
            {
                if (negated.Contains((row.CastCode.ToLowerInvariant(), row.MemberKey)))
                { row.Update(null, now, negated: true); continue; }
                if (hidden.Contains(row.MemberKey)) { row.Update(null, now, hidden: true); continue; }
                ActiveBuffTimer? match = null;
                foreach (ActiveBuffTimer t in snap)
                    if (string.Equals(t.Short, row.CastCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.Target, row.MemberKey, StringComparison.OrdinalIgnoreCase))
                    { match = t; break; }
                row.Update(match, now, conflicted: isConflicted);
                continue;
            }

            // A whole-party MEMBER row for someone who wasn't in the party when the buff
            // was cast (joined later) doesn't carry it — show "not up" until a recast
            // re-covers the party. Covered members fall through to read the shared timer.
            if (row.IsWholeParty && row.MemberKey.Length > 0 && !row.WholePartyCovered)
            {
                row.Update(null, now);
                continue;
            }

            // Self-cast or (covered) whole-party row: one cast keyed to self (""). Self-
            // cast rows may be covered by a configured party-wide buff.
            ActiveBuffTimer? entry = null;
            foreach (ActiveBuffTimer t in snap)
                if (t.Target.Length == 0 && string.Equals(t.Short, row.CastCode, StringComparison.OrdinalIgnoreCase))
                { entry = t; break; }
            // A member's row of a whole-party buff that stood still on us over time
            // out of the game: the member's ran on, so their bar counts by the expiry
            // the cast had with no time away, which is also what the recast goes by
            // in a party. Reading our own timer showed them covered for longer than
            // they were, by exactly the time we were out.
            if (row.IsWholeParty && row.MemberKey.Length > 0 && entry is { RanOnUntil: { } ranOn } carried)
                entry = carried with { Until = ranOn };
            string? coveredBy = null;
            if (!row.IsParty) coverage.TryGetValue(row.CastCode, out coveredBy);
            row.Update(entry, now, coveredBy: coveredBy, conflicted: isConflicted);
        }
    }

    private void RebuildRows(SpellsSettings spells, BuffSettings? buffs, IReadOnlyList<ActiveBuffTimer> snap)
    {
        Groups.Clear();

        // A snapshot-driven row (a not-maintained whole-party buff, or a member not
        // configured as a target) is surfaced only while its timer is genuinely LIVE.
        // An expired entry lingers in _activeUntil (nothing recasts a buff that isn't
        // set to recast, and there's no wear-off line to reap it), so match on
        // t.Until > now — otherwise the bar sits full at 0s forever. A buff that IS
        // set to recast (WholePartyOn / a targeted member / CastOnSelf) stays config-
        // driven and persists as "not up" past expiry, since the engine will refresh it.
        DateTime now = _castDirector.PausedAtUtc ?? DateTime.UtcNow;

        // Section order: you first, then each current party member (so a member with no
        // active buff still gets a seeded section, dropped below only if truly empty).
        Dictionary<string, BuffWatchdogPlayerGroup> byName = new(StringComparer.OrdinalIgnoreCase);
        BuffWatchdogPlayerGroup self = GetGroup(byName, SelfName());
        List<(string Display, string Given)> members = CurrentMembers();
        Dictionary<string, string> displayByGiven = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string display, string given) in members)
        {
            displayByGiven[given] = display;
            GetGroup(byName, display);   // seed member section in roster order
        }

        // Mana / HP regen still live on the Spells tab; every other self buff is a
        // CastOnSelf slot in the unified list. All land on you → your section.
        AddSelfRow(self, spells.HpRegenSpell);
        AddSelfRow(self, spells.MaRegenSpell);

        if (buffs is not null)
        {
            // Walk the slots in the SAME order the config table shows them (manual
            // arrangement, else grouped by type), so each player's timer bars line up
            // with the config rows — see BuffPriorityOrder.InDisplayOrder.
            IReadOnlyList<BuffSlot> ordered = BuffPriorityOrder.InDisplayOrder(
                buffs.Slots, buffs.ManualOrder,
                s => BuffPriorityOrder.Category(ItemCastToken.IsToken(s.Spell), IsWholePartySlot(s.Spell)));
            foreach (BuffSlot p in ordered)
            {
                if (string.IsNullOrWhiteSpace(p.Spell)) continue;
                string code = p.Spell.Trim();
                (string name, bool learned) = ResolveName(code);
                bool wholeParty = IsWholePartySlot(code);

                // Self-cast + whole-party buffs land on you → your section (both keyed "").
                if (p.CastOnSelf && !wholeParty)
                    self.Rows.Add(new BuffWatchdogRowViewModel(code, isParty: false, name, "self", learned)
                        { IsPriority = p.PriorityBuff });
                if (wholeParty)
                {
                    // Show a whole-party buff only when it's actually being maintained
                    // (WholePartyOn = set to recast) OR is currently up (a LIVE timer).
                    // A configured-but-off whole-party buff whose timer has expired isn't
                    // surfaced — the expired entry lingers in the snapshot, so the row
                    // would otherwise sit at 0s forever instead of dropping.
                    bool wpActive = snap.Any(t => t.Target.Length == 0 && t.Until > now
                        && string.Equals(t.Short, code, StringComparison.OrdinalIgnoreCase));
                    if (!(p.WholePartyOn || wpActive)) continue;

                    // Self always carries a whole-party buff (it lands on us too).
                    self.Rows.Add(new BuffWatchdogRowViewModel(
                        code, isParty: true, name, "whole party", learned, isWholeParty: true)
                        { IsPriority = p.PriorityBuff });

                    // One row under each CURRENT member: covered (in the party when it was
                    // cast → reads the shared timer) or NOT covered (joined after → shows
                    // "not up", flagging they lack the party buff). Recast is on OUR timer
                    // (or, for one carried over time out of the game, on the expiry it
                    // had with no time away); a new member is picked up on the next
                    // recast (which re-snapshots).
                    if (wpActive)
                    {
                        _wholePartyCoverage.TryGetValue(code, out (DateTime Until, HashSet<string> Covered) cov);
                        HashSet<string>? covered = cov.Covered;
                        foreach ((string display, string given) in members)
                            GetGroup(byName, display).Rows.Add(new BuffWatchdogRowViewModel(
                                code, isParty: true, name, display, learned,
                                isWholeParty: true, memberKey: given,
                                wholePartyCovered: covered?.Contains(given) ?? false)
                                { IsPriority = p.PriorityBuff });
                    }
                    continue;
                }

                // Single-target: one row per member, under that member's section. Show a
                // row for each CONFIGURED target (AllMembers = the roster, else the chosen
                // names) PLUS any member who already has a LIVE timer for this spell — so a
                // member you unticked (or who left) keeps their countdown until it expires.
                List<string> givens = new();
                void AddGiven(string g)
                {
                    if (!givens.Any(x => x.Equals(g, StringComparison.OrdinalIgnoreCase))) givens.Add(g);
                }
                if (p.AllMembers)
                    foreach ((string _, string given) in members) AddGiven(given);
                else
                    foreach ((string _, string given) in members.Where(m => p.Targets.Contains(m.Given)))
                        AddGiven(given);
                foreach (ActiveBuffTimer t in snap)
                    if (t.Target.Length > 0 && t.Until > now
                        && string.Equals(t.Short, code, StringComparison.OrdinalIgnoreCase))
                        AddGiven(t.Target);   // already lower-cased, still-live timer only

                // Configured single-target buff with nobody targeted and no live timer
                // — it's not set to recast on anyone, so don't surface a bar for it.
                if (givens.Count == 0) continue;
                foreach (string given in givens)
                {
                    string display = displayByGiven.TryGetValue(given, out string? d) ? d : Capitalise(given);
                    GetGroup(byName, display).Rows.Add(new BuffWatchdogRowViewModel(
                        code, isParty: true, name, display, learned, isWholeParty: false, memberKey: given)
                        { IsPriority = p.PriorityBuff });
                }
            }
        }

        // Keep an active buff visible even after its slot is removed. A live timer with
        // no configured row yet gets a read-only bar under its target's section (yours
        // for a self / whole-party cast, the member's otherwise), ticking down until it
        // wears off. So Remove all — or deleting one row — clears the config without
        // hiding a buff that's genuinely still up. Once it EXPIRES it clears itself:
        // nothing recasts an unconfigured buff, so an expired one earns no bar (unlike a
        // configured buff, whose row persists as "not up" because it's config-driven,
        // not snapshot-driven — the caster may just not have recast it yet).
        HashSet<(string Code, string Target)> shown = new();
        foreach (BuffWatchdogPlayerGroup g in Groups)
            foreach (BuffWatchdogRowViewModel r in g.Rows)
                shown.Add((r.CastCode.ToLowerInvariant(), r.MemberKey));
        foreach (ActiveBuffTimer t in snap)
        {
            if (t.Until <= now) continue;   // ran out + unconfigured → no bar
            string key = t.Short.ToLowerInvariant();
            if (!shown.Add((key, t.Target))) continue;
            (string nm, bool lrn) = ResolveName(t.Short);
            if (t.Target.Length == 0)
                self.Rows.Add(new BuffWatchdogRowViewModel(t.Short, isParty: false, nm, "self", lrn));
            else
            {
                string display = displayByGiven.TryGetValue(t.Target, out string? d) ? d : Capitalise(t.Target);
                GetGroup(byName, display).Rows.Add(new BuffWatchdogRowViewModel(
                    t.Short, isParty: true, nm, display, lrn, isWholeParty: false, memberKey: t.Target));
            }
        }

        // Drop seeded sections that ended up with no buffs.
        for (int i = Groups.Count - 1; i >= 0; i--)
            if (Groups[i].Rows.Count == 0) Groups.RemoveAt(i);

        IsEmpty = Groups.Count == 0;
    }

    // Find or create a player section by header name, appended in first-seen order.
    private BuffWatchdogPlayerGroup GetGroup(Dictionary<string, BuffWatchdogPlayerGroup> byName, string name)
    {
        if (byName.TryGetValue(name, out BuffWatchdogPlayerGroup? g)) return g;
        g = new BuffWatchdogPlayerGroup(name);
        byName[name] = g;
        Groups.Add(g);
        return g;
    }

    // Your own display name for the self section: your given name from the party
    // roster if present, else your IN-GAME character name (PlayerStats.Name — never
    // the profile name, which can differ, e.g. a "Fujinpvp" profile on a "Fujin"
    // character), else "You".
    private string SelfName()
    {
        if (_party is not null)
            foreach (Game.PartyMember m in _party.Members)
                if (m.IsSelf && !string.IsNullOrWhiteSpace(m.Name))
                    return Capitalise(GivenLower(m.Name));
        string? n = _readSelfName?.Invoke();
        return string.IsNullOrWhiteSpace(n) ? "You" : Capitalise(GivenLower(n));
    }

    private static string GivenLower(string name) =>
        (name.Split(' ') is { Length: > 0 } parts ? parts[0] : name).ToLowerInvariant();

    private static string Capitalise(string given) =>
        given.Length == 0 ? given : char.ToUpperInvariant(given[0]) + given[1..];

    private void AddSelfRow(BuffWatchdogPlayerGroup group, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        (string name, bool learned) = ResolveName(code);
        group.Rows.Add(new BuffWatchdogRowViewModel(code.Trim(), isParty: false, name, "self", learned));
    }

    // Display label + learned flag. Spells show their 4-letter cast code (not the
    // full name); item-casts show '#' + a short prefix of the item name.
    private (string Name, bool Learned) ResolveName(string code)
    {
        string trimmed = code.Trim();
        if (ItemCastToken.IsToken(trimmed))
        {
            string item = (ItemCastToken.ItemName(trimmed) ?? trimmed).Trim();
            string shortItem = item.Length > 4 ? item[..4] : item;
            return ("#" + shortItem, true);   // a carried buff item counts as available
        }
        return _spellbook.FindByCastCode(trimmed) is { } s
            ? (s.Short, _spellbook.IsObtained(s.Number))
            : (trimmed, false);   // unknown cast code — show it, flagged un-learned
    }

    // Whether a party-buff slot's cast value is whole-party — a spell with a whole-party
    // Targets scope, or a #item-cast whose item casts a whole-party spell.
    private bool IsWholePartySlot(string? spell)
    {
        if (string.IsNullOrWhiteSpace(spell)) return false;
        string s = spell.Trim();
        if (ItemCastToken.IsToken(s)) return _spellbook.IsTokenWholeParty(s);
        return _spellbook.FindByCastCode(s) is { } ks && BuffClassifier.IsWholeParty(ks.Targets);
    }

    // Cheap fingerprint of the configured buff set AND the active party-buff timer keys
    // — a config change (live edit) OR a timer arming / expiring triggers a rebuild. The
    // timer keys matter so a member you blessed then unticked keeps a row until the timer
    // actually expires (then the key drops → rebuild → row goes away). Only the KEY set
    // (short@target), never the remaining time, so it doesn't churn every second.
    private string BuildSignature(
        SpellsSettings spells, BuffSettings? buffs, IReadOnlyList<ActiveBuffTimer> snap)
    {
        StringBuilder sb = new();
        DateTime sigNow = _castDirector.PausedAtUtc ?? DateTime.UtcNow;
        // Mana / HP regen are the only self buffs still on the Spells tab.
        sb.Append(spells.HpRegenSpell).Append('|').Append(spells.MaRegenSpell).Append("||");
        if (buffs is not null)
            foreach (BuffSlot p in buffs.Slots)
            {
                // A whole-party buff's row shows / hides on its self-keyed ("") timer
                // arming or expiring (when it isn't auto-maintained), so fold that into
                // the fingerprint — the member-keyed timers below only cover single-target.
                string code = (p.Spell ?? string.Empty).Trim();
                bool wpActive = IsWholePartySlot(p.Spell)
                    && snap.Any(t => t.Target.Length == 0 && t.Until > sigNow
                        && string.Equals(t.Short, code, StringComparison.OrdinalIgnoreCase));
                sb.Append(p.Spell).Append(':').Append(p.CastOnSelf ? "S" : "")
                  .Append(p.WholePartyOn ? "W" : "").Append(p.AllMembers ? "A" : "")
                  .Append(p.OnlyWhenHpFull ? "H" : "").Append(p.OnlyWhenMaFull ? "M" : "")
                  .Append(p.PriorityBuff ? "P" : "")
                  .Append(wpActive ? "T" : "");
                // Which members a whole-party buff currently covers — so a recast that
                // re-covers a swapped-in member reflows their row (roster joins / leaves
                // already force a rebuild via the party collection-changed handler).
                if (wpActive && _wholePartyCoverage.TryGetValue(code, out (DateTime Until, HashSet<string> Covered) cov))
                    foreach (string g in cov.Covered.OrderBy(x => x, StringComparer.Ordinal))
                        sb.Append('#').Append(g);
                sb.Append(string.Join(",", p.Targets)).Append('|');
            }
        // A configured cast code already owns a row (self-cast or whole-party), so its
        // timer arming / expiring is reflected in place by UpdateTimers (the row shows
        // "not up") and must NOT churn the signature — otherwise a handful of maintained
        // self buffs cycling would rebuild every bar, every second. Only a LIVE
        // UNconfigured active buff (hand-cast, or a slot just removed) folds in, so its
        // read-only bar appears; keying on liveness means the signature flips the moment
        // it expires, so RebuildRows re-runs and drops the row (see RebuildRows' tail).
        // Member-keyed timers fold too, but likewise only while LIVE — an expired single-
        // target timer drops out so a member you unticked loses their row when it runs out.
        HashSet<string> configuredCodes = new(StringComparer.OrdinalIgnoreCase);
        if (buffs is not null)
            foreach (BuffSlot p in buffs.Slots)
                if (!string.IsNullOrWhiteSpace(p.Spell)) configuredCodes.Add(p.Spell.Trim());

        sb.Append("||");
        foreach (string k in snap
                     .Where(t => t.Until > sigNow
                         && (t.Target.Length > 0 || !configuredCodes.Contains(t.Short)))
                     .Select(t => t.Short + "@" + t.Target)
                     .OrderBy(k => k, StringComparer.Ordinal))
            sb.Append(k).Append(';');
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _spellbook.Changed -= OnSpellbookChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _profile.ProfileMutated -= OnProfileMutated;
        _tick.HeartbeatElapsed -= OnHeartbeat;
        _castDirector.TimersResumed -= OnTimersResumed;
        if (_party is not null) _party.Members.CollectionChanged -= OnPartyMembersChanged;
        if (_tokens is not null) _tokens.BuffPauseChanged -= OnTokenBuffPauseChanged;
        Buffs?.Dispose();
    }
}
