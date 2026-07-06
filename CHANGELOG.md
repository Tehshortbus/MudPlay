# Version history

Notable changes per merged PR, **newest first**. The top of the [README](README.md) mirrors the most recent entry. Versioning follows semver (post-1.0): **MAJOR** = whole-program refactor, **MINOR** = a large PR, **PATCH** = a small / bugfix PR.

## 1.5.9

A looping walk no longer bails out and gets "lost" mid-circuit when the room simply re-prints during combat — the tracker stops mistaking a harmless redisplay of the room you're in for a refused move, so it keeps waiting for the step you actually took instead of firing a bogus recovery that then bonks a real exit at the wrong room.

**Fixed**
- **A passive room redisplay during a pending move was misread as a refusal, derailing a running loop.** While a loop step was in flight, the tracker inferred a "move refused" purely because the next room display matched the room you were standing in — but during combat the game re-prints your room for all sorts of reasons (a mob clears, someone arrives or leaves, a bare re-glance), none of which mean your move failed. That false refusal made the loop believe it hadn't moved, so it launched a recovery that re-sent a step from the *old* room while you had actually already crossed into the next one — and that step then bonked a real exit ("There is no exit in that direction!"), desynced the tracker, and drifted it into "lost" until you manually intervened. This rests on a confirmed game rule: **a refused move never redisplays the room — it always prints an explicit refusal line instead** (the wording varies by why it bonked). So a same-room redisplay while a move is pending is now treated as the passive re-look it is: the tracker keeps the pending move and waits for the real outcome, and genuine refusals continue to be handled by their explicit refusal line. A real self-loop exit that lands you back in the same room is unaffected — it's a real move with a real room display and resolves normally.

## 1.5.8

A walk-to no longer wedges into a permanent "walking but not moving" state after crossing into a new area — the room tracker now recognises the engine's own echoed move regardless of how long a map re-root delays it, so it stops mistaking that echo for a phantom second step that jammed the pending queue.

**Fixed**
- **A walk-to could enter a zombie "walking but stalled" state, and stopping + re-queuing it made things worse.** Every move the walker sends is echoed back over the wire, and the room tracker debounced that echo on a fixed 100 ms wall-clock window. Crossing into a new area triggers a synchronous map re-root (thousands of rooms re-laid-out) that runs *between* the walker announcing the move and the echoed bytes arriving — pushing the echo past the 100 ms window, so it was treated as a real second move and double-enqueued. That phantom kept the tracker's pending-move queue non-empty forever: the next room display confirmed as "queue not empty" and held the tracker in `Pending`, so the walker believed it was mid-step and never advanced (the brief "map flipped to another room then back" was that same re-root). Stopping the walk didn't drain the phantom, so re-queuing inherited the jammed queue and stayed buggy. The echo is now matched by a **consume-once claim** armed only by the engine's own send and cleared by the first matching echo — timing-independent, so a re-root of any duration can't turn the echo into a phantom. A generous expiry bounds a claim whose echo never arrives (a refused move) so it can't later swallow an unrelated manual step, and manual movement (which never arms a claim) is unaffected — including two manual steps of the same direction in quick succession, which the old window used to wrongly collapse into one.

## 1.5.7

Walk-to routing now treats an unaffordable `(Toll: N)` exit the same way it already treats a level gate you fall outside of — the planner routes around a toll you can't cover instead of marching you into a refusal, so a walk that would only end with `You do not have enough to cover the toll of N gold crowns.` never starts down that road.

**Fixed**
- **Walk-to would march into a toll it couldn't pay.** A `(Toll: N)` exit needs the crosser to carry a wealth value of `N × 100` (the consolidated `Wealth:` figure — any coin mix totalling that copper-value passes; it doesn't have to be gold), and the game refuses the step with `You do not have enough to cover the toll of N gold crowns.` when you're short. The path planner ignored toll cost, so it would happily route you through a toll you couldn't afford and then stall on the refusal. Toll exits now gate at planning time against your live on-hand wealth exactly as level-gated exits gate against your level: the walker routes around a toll you can't cover, and when *every* route to the target is blocked by a level or toll requirement it says so (`all routes blocked by a level or toll requirement`) instead of failing with a bare "no path". As with the level gate, an unknown wallet (no inventory parsed yet) never refuses a walk — we don't gate on a bar we can't yet evaluate. This is the self / leader affordability check; a party-wide `@wealth` poll that gates the whole group's route is a separate follow-up.

## 1.5.6

When a party member drops in front of you, a party healer now reacts on its own: it holds movement to stay with the downed ally, aids them, keeps healing them by name until they recover, then re-invites them if you're leading — including the case where the one who dropped was the leader whose disconnect already dissolved the party.

**Fixed**
- **A dropped party member got no automatic rescue.** Seeing `<name> drops to the ground!` used to leave a party healer idle — it kept farming / moving away while the downed ally stayed at 0 HP. The client now treats an ally's drop as a wait condition: it pauses movement (an `AllyDown` hold), sends `aid <name>` to lift them above 0, then keeps healing them by name through the normal spell tiers even though a dropped ally has left the `par` roster, polling their health with an `@health` telepath until they recover. Once they're back to full it releases the hold; if you're the leader it re-invites them (recovery to positive HP restores their ability to act but not their party membership). The reaction also recognises the hardest case — the ally who dropped was the **leader**, whose disconnect had already wiped the whole party from your roster — via the handler's own short recent-leader memory, so a leader's drop still gets aided and held rather than ignored as a stranger.

## 1.5.5

While your own character is dropped (mortally wounded at 0 HP or below), the client now stops every background engine from spamming commands the game will only reject, and corrects the party state a drop invalidates — so a downed character sits quietly until aided / healed instead of hammering the wire and holding movement on a party it's no longer in.

**Fixed**
- **Background engines kept firing commands while you were dropped.** A mortally-wounded character can't act — the game bounces every command with `You may not do that while you are mortally wounded!` — but the automation engines (rest, cast, party polls, walk-to, etc.) kept sending anyway, flooding the wire with rejected commands. Dropping now raises a blanket engine-send hold and a movement pause (surfaced as the `MortallyWounded` pause reason) for the whole time HP is at or below 0, so all the background engines fall silent until you recover. The emergency low-HP hangup is deliberately exempt — hanging up is still allowed while dropped, so it pierces the hold and remains your last escape.
- **Party / following state went stale after a self-drop.** Dropping removes you from the party game-side; the leader dragging your body around is physical relocation, not membership. The client used to keep believing it was partied and following, so the follower-movement gate held movement forever on a party you'd already left. A drop now clears the tracked roster / leader / following flags, and recovery re-confirms membership only from a real follow / `par` signal — which arrives after the leader re-invites you (recovery to positive HP restores your ability to act but not your membership; a re-invite is required to rejoin).

## 1.5.4

Casting a party buff (Bless slots) now reports its effect duration and recast timing on the always-on program log, so you can confirm the recast timer actually armed and see when it will re-fire.

**Fixed**
- **The program log didn't show a party bless's duration or recast timing.** Casting a party bless armed its recast timer and logged the confirmation, but only on the combat-diagnostics channel — which is off in normal play — so nothing showed and you couldn't tell whether the timer was set. The confirmation now lands on the always-on Info channel with the effect duration and the recast lead, e.g. `party-buff confirmed spell=bles target=Fujin duration=300s — recast in 285s.` (the recast fires 15s before expiry). The item-cast buff confirmation moved to the same always-on channel with the same duration/recast enrichment.

## 1.5.3

The realm death-floor estimate now sharpens from live play, not just from clean deaths: any HP reading you survive below the current floor ratchets the estimate deeper, so the "how far past zero can I go" figure keeps improving without waiting for the next death.

**Fixed**
- **The death floor wasn't refined from a survived bleeding-out reading.** The floor estimate only moved on a captured death, so a character who bled well past the current floor and lived left that evidence on the table. The tracer now ratchets the floor to one below the deepest HP you're seen alive at — a later in-band prompt proves the previous reading survived — so the estimate tightens from live play. The terminal death reading is structurally excluded (it never proves survival), and an overkill that masks the reached HP can't corrupt the estimate.

## 1.5.2

The miracle-save death — the "but, due to a miracle, you have been saved" sequence — is now recognised as the real death it is and captured by death recovery, so a run that ends in a last-instant rescue still records the death and its floor.

**Fixed**
- **A miracle-save death was not captured by death recovery.** When the game kills you but immediately revives you on a miracle, the readout differs from an ordinary death — it announces your remaining lives directly ("You now have N lives remaining." / "You have N life left.") rather than the plain slain line the detector keyed on. The death therefore slipped past recovery entirely: no death record, no floor update, no lives decrement. The detector now matches both readout forms, so a miracle-save is captured exactly like any other death and feeds the same death-floor estimate.

## 1.5.1

The low-HP emergency hangup no longer fights itself: after it drops the carrier to escape a losing fight, the client used to immediately auto-reconnect straight back into the danger. The hangup now flags the disconnect as intentional so the reactive-reconnect path stands down.

**Fixed**
- **The auto-hangup fired, then the client reconnected on its own — dialling straight back into the situation it just fled.** The emergency low-HP hangup sends the configured game-exit command to drop the carrier, but it never told the reconnect layer the drop was deliberate. The disconnect was then classified as an unexpected server-side drop and a reactive reconnect was scheduled, undoing the escape. The hangup path now arms the same intentional-disconnect signal the remote `@hangup` uses, so the drop is classified as `HangupInitiated` and no auto-reconnect fires — the client stays down until the user brings it back.

## 1.5.0

A readability batch across Session Stats, navigation, and the Player Workshop quest view, all from live play: coin figures now read as denominations instead of a raw copper count, the rate graphs match their headline numbers and label the current rate, the loop lap counter actually counts, the main-window looping chip is trimmed to the essentials, and multi-class ability quests show each class's own unlock level.

**Changed**
- **Session Stats currency reads as coin denominations now, not a comma-grouped copper count.** The total-collected, per-hour, and stashed figures "flip up" to the largest denomination that has a whole unit — 1000 copper an hour shows as `10 gold/hr`, not `1,000/hr` — following the same copper/silver/gold/platinum/runic ladder the inventory tracker uses. The exact itemised wealth line (`1 runic 93 platinum 5 gold 6 copper`) moves to a hover tooltip, so the compact figure fits the narrow stat columns while the full breakdown stays one hover away. No thousands-commas anywhere in the currency rows.
- **The exp/hr and kills/hr rate graphs now carry the current rate as a header label.** Each graph header shows the same figure as its table stat (e.g. `5.7k`), abbreviated to k / M so it fits without a long digit run — so you can read the rate off the graph at a glance instead of eyeballing the plotted line against the axis.
- **The navigation top-bar path label shows step progress and the lap number while looping** (e.g. `Docks run · step 3/8 · lap 2`), turning the bare loop name into a live position + lap readout that mirrors the Current-Nav panel.
- **The main-window looping chip is trimmed to the essentials** — the Looping state, the lap counter, and XP/hr — dropping the longer status text so the bottom-left chip stays compact during a farm.
- **Multi-class ability quests show each class's own required level in the Player Workshop quest "Requires" line.** A quest several classes can learn at different levels (Smash, Meditate, Supernatural Stealth) now renders each restricted class with its own gate appended — `Warrior-22, Mystic-20` instead of bare class names — surfacing the distinct per-class unlock level the crawl already knew.

**Fixed**
- **The Session Stats exp/hr and kills/hr graphs plotted a rolling-window rate that didn't match the table's session-lifetime stat** — a graph reading ~6.5k/hr sitting next to a table reading 5,749. The graph series is now anchored at session start as a cumulative average over the whole session, the same basis the table uses, so the series' right edge equals the headline number and the two can't disagree.
- **The navigation lap counter never advanced past lap 1.** The displayed lap derived from a lap-time history list capped at ten entries, so its count froze once the cap was hit (and the "current lap" it implied was already off by the capping). A dedicated uncapped lap counter now drives the display, incrementing on every loop wrap and resetting when the loop stops, so lap 2 reads as lap 2.

## 1.4.10

The Health tab's "Hang up if below" ticker can now go negative — down to the realm death floor — so a player can set the emergency disconnect deep in the bleeding-out band, closer to death (issue #107).

**Changed**
- **The "Hang up if below" HP threshold now accepts negative values in *both* Percentage and Value mode, bounded at the per-BBS death floor (`BBS → Player dies at`).** Hitting 0 HP in MajorMUD only *drops* you — you're bleeding out but still revivable and still able to hang up — and death only happens at the realm's negative floor (default -25). HP% doesn't clamp at zero either: a dropped character reads a negative percentage, exactly as the `par` party display shows. Previously the hangup ticker floored at 0, so the only choices were "hang the instant I drop" or a positive-HP panic button; a player who wanted to squeeze out a few more rounds of party rescue before pulling the plug couldn't express it. The ticker is now one continuous scale from the top (100 %/max) down through 0 into the negatives, bottoming out at the death floor — set the hangup anywhere on it (e.g. hang at -15 HP, or the percentage that resolves there), in whichever unit you prefer. Sliding it to the floor makes an empty fire window, the natural "never hang up" position; **0 is a live trigger** ("hang the moment I drop"), no longer a disable. To turn the auto-hangup off entirely, use the existing **Disable hang-ups** master switch. The ticker's lower bound and the engine's fire window read the same active-BBS death floor, so the UI can never offer a value the engine would reject.

## 1.4.9

Three heal/buff-engine fixes from live Mystic play: the auto-caster double-cast heals and buffs each round, drained kai into a "not enough kai" error, and ignored the per-set percentage-vs-value choice on the Health tab's HP / MA thresholds.

**Fixed**
- **The auto-caster fired the same self-heal twice in a row (e.g. `swan` → `swan`), wasting a full mana pool on a redundant top-up.** The cast-cooldown layer enforces one cast per round, but a combat tick deliberately wipes that cooldown so the next round's cast can go out immediately — and a second evaluation landing in the same round (an extra tick, a redundant prompt) would re-run on *stale* HP/MA, because the server hadn't yet reflected the first heal. With no per-heal recast clock to lean on, the identical heal went out again. A self-heal now records the exact HP + MA it was sent at; a re-evaluation that finds the same spell against an unchanged pool inside an 8-second window is recognised as a not-yet-reflected duplicate and skipped, so a lower-priority (or genuinely different) cast can still fire. The moment the pool actually moves — the heal lands, or fresh damage arrives — the guard releases and a real heal is free again.
- **A self-buff (e.g. the Mystic's `tige`) double-cast the instant it was applied, and the second cast drained kai below the spell's cost — surfacing "You do not have enough kai to invoke that power."** A self-buff's recast timer only started once the game *confirmed* the buff landed via its "you feel…" message, which arrives a round or more after the cast is sent. In that gap the buff looked "never cast" to every evaluation, so the same combat-tick cooldown wipe let it re-fire before the first cast resolved — spending kai twice on a buff that costs several. A self-buff now starts its recast clock the instant the cast is *sent* (using the spell's own effect duration, or a conservative fallback), so the in-flight window is covered; the confirmation message still overwrites the clock with the true duration when it lands.
- **The Health tab's HP / MA threshold radials (percentage vs. absolute value) were ignored by the spell engine — it always read every heal and "bless if above" trigger as a percentage.** The passive rest/run/hang gates already honoured each set's radial, but the active heal- and buff-casting director hardcoded percentage math, so a character configured with absolute triggers (natural for a small Kai pool — "bless when at 4 kai or more", not "4%") had those thresholds silently misread. All of the director's HP heal triggers, the mana heal-floor, and the "bless if above" mana/kai floor now resolve through the same shared threshold math the rest gates use, so the percentage/value choice on each of the HP and MA sets is honoured identically on both sides.

## 1.4.8

Three navigation fixes from live play: the self-healing recovery could take the whole client down when it gave up on a lost route, a stationary player parked in an ambiguous area could watch their map marker vanish on its own, and a `go path` move through a same-name area could get the tracker completely lost.

**Fixed**
- **The client crashed to desktop when navigation recovery failed terminally — e.g. a "go path" that got lost with no anchor to backtrack from, or a route that exhausted its backtrack without finding a unique room.** The recovery gate's terminal-failure path called the engine's abort, which resets the engine and detaches it from the gate (nulling the gate's engine reference) — then the very next line read that now-null reference to name the engine in the failure event, throwing a `NullReferenceException` that propagated to the top and aborted the process. The gate now captures the engine name *before* the re-entrant abort call, so a failed recovery surfaces the normal "Lost — use the map to set your location" dialog instead of killing the app. A regression test drives the exact detach-during-abort re-entrancy.
- **Standing still in an ambiguous area (MajorMUD's identical "Main Road" rooms, or a "Darkwood Forest" tile with dozens of same-name candidates) could make your map marker vanish on its own.** When the position tracker can't pin a single room from name + exits, it parks in a "Suspect" state that keeps showing your last marker. But every passive redisplay of that same room — an *Enter* echo, a cash-on-ground notice, a party member arriving — was counted as a fresh mismatch and accrued a "suspect strike"; a stationary player idling in place (resting, casting) would silently rack up strikes with no move between them until the third one declared them Lost and wiped the marker. A redisplay of the identical room with no move since the last one carries no new position evidence, so the tracker now ignores it instead of striking — strikes only accrue after an actual move. The moment you move, the normal ladder re-arms, so genuine desyncs still escalate.
- **Using a `go path` (or any command-style text exit) to travel inside a same-name area could get the tracker completely lost, marching you to a wiped marker.** A `go path` exit is hidden from the "Obvious exits:" line, so the room redisplay after the move carried only the visible exits — and a manually typed `go path` records the move with no cardinal direction. The tracker skipped its move prediction for that null-direction step and fell back to a name+exits candidate search, which structurally can't match a go-path destination (the hidden exit still counts toward the room's stored exit set but never shows in the display), so it landed dozens of candidates and slid into Suspect → Lost. But a `go path` exit is a *deterministic* graph edge — it always carries you to the exact room and direction recorded on that exit line — so the tracker now follows that edge directly: the pending-move check and the replay-from-last-known recovery both resolve a text-exit command through the source room's matching exit instead of giving up, pinning the true room even when name + visible exits are ambiguous.

## 1.4.7

A currency-capture fix so the Session Stats window records coins picked up in a real realm, not just synthetic fixtures.

**Fixed**
- **Session Stats currency stayed at zero even while you looted coins, because the cash pickup/drop/stash patterns only matched a synthetic wording the live game never sends.** The three confirmation patterns required a literal "pieces" noun and a trailing period, but this realm names coins in full — copper farthings, silver nobles, gold crowns, platinum pieces, runic coins — and the pickup line carries no period, so `You picked up 6 silver nobles` never matched, the `CoinCollected` event never fired, and the panel's currency counters never moved (even though the inventory tracker recorded the holdings correctly all along). The patterns now anchor on the denomination keyword plus its specific coin noun, capture the keyword, and drop the mandatory period — so real loot registers. The coin-noun anchor keeps a shared-verb item line (`You dropped a silver key.`) from being misread as coin, and "piece" stays in the noun set so existing `N gold pieces` fixtures still resolve.

## 1.4.6

A layout fix for the Session Stats window from live use: the panels now share one width and the rate graphs get room to breathe.

**Fixed**
- **In the Session Stats window, the "Player Statistics" section rendered narrower than "Time Analysis" / "Session Statistics" and its numbers were squished, while the two rate graphs collapsed to a thin sliver.** Every panel was left-anchored under a fixed width cap, which let each one shrink to just its own content — so the graphs shrank to about their header-text width and Player Statistics (naturally narrower) came up short with a cramped rate column. The panels now stretch to fill the scroll column, so all five share one width and line up. The Kills/hour and Exp/hour sparklines widen to the full panel and gained a little height (44 → 60 px) so the trend is legible, and the Player Statistics rows were re-columned so the label flexes on the left and the numeric block (count / min–max / avg / rate) groups tightly on the right — matching the other two sections.

## 1.4.5

A navigation-reliability pass from live play: a per-room "can't reach" flag, last-position recall after a client restart in same-named areas, and two loop-engine fixes so a route recovers itself instead of stalling out.

**Added**
- **A per-room "Can't reach" flag in the Modify-Blacklist dialog, for dev / orphan rooms a normal player can never stand in.** Plain blacklisting only declutters the map render — but such a room is still a valid *(name, exit-set)* candidate the navigation position-tracker can resolve you *into*, which strands the nav system somewhere you physically can't be (e.g. an orphan room in the MDB with no walkable inbound edge). Ticking **"Can't reach"** on a blacklist entry drops that room from position-candidate resolution entirely, so an ambiguous login or silent-desync observation can never land you there. It's a separate opt-in bit from the plain blacklist because a normally-reachable room can be blacklisted purely to tidy the render while still being a legitimate position — and the flag round-trips to disk with the entry.

**Fixed**
- **After closing and reopening the client deep in a same-named area, your position came up wrong or lost — even though you weren't lost when you quit.** In an ambiguous area (MajorMUD's Darkwood Forest is ~8 identical *"Main Road"* rooms), the tracker is hydrated Confirmed at your last *known-unique* room and primed with the walk you took since — but that anchor is stale, because you walked deeper into the identical rooms before quitting. On relaunch the first room redisplay disagrees with the anchor, no unique candidate resolves it, and a lone mismatch only bumped a Suspect strike (never reaching the limit that would trigger recovery), so you sat stranded on the wrong room. The tracker now projects the persisted trail forward from the anchor: when its endpoint matches the redisplay, it lands Confirmed at the true room. Harmless mid-walk — a stale non-anchor start over-walks to a room that won't match, so recovery declines and the old behaviour is unchanged.
- **A loop's "Run" took two clicks: the first walked all the way to the first loop room, then just sat there idle.** When the approach-walk to the loop's entry finished during a pause window (a rest / combat gate that lifted right as the walker arrived), the walker's own resume handler completed the walk and reset itself *before* the loop runner's resume handler ran — so the arrival was dropped and the runner sat waiting for a "finished" signal that would never re-fire. The runner now buffers that arrival and enters the circle on resume, so a single **Run** click both walks to the loop and starts running it.
- **A running loop would stop on its own and drop straight to idle instead of working out where it was and carrying on.** When a step was refused (a mob blocking the doorway, lag) or the character landed somewhere unplanned, the runner failed the entire loop rather than trying to recover. It now enters a bounded auto-recovery: re-determine the current room (issuing a bare `look` when the tracker is unsure), then reroute onto the nearest segment of the loop and continue from there — picking up whatever leg of the route the character actually fell in. A block that never clears trips a retry cap (3 attempts) and finally surfaces as a real failure, so recovery can't spin forever.

**Changed**
- **The navigation engine now emits Debug-channel tracing for position replay-recovery and unreachable-room candidate drops.** The lifecycle events stay always-on Info as before; the added Debug rows record *why* a replay projection declined (missing anchor, unwalkable step, endpoint mismatch) and when a "can't reach" flag actually pruned a candidate — so a *"still lost after a restart"* or *"nav won't resolve into that room"* bug report shows exactly where resolution diverged.

## 1.4.4

Four fixes from live play: combat follow-up attacks, room tracking on login, the loop-save prompt, and the navigation activity chip.

**Fixed**
- **With "attack after last party member" on, your follow-up swing never fired for a member who showed a family name.** MajorMUD announces a party member's actions (moves-to-attack, chat) by their *given* name alone, but the `par` roster stores the full *"Given Family"* name — so a member called "Raijin WuzHere" in the roster announces simply as "Raijin". The combat engine compared the announced given name against the full roster name, which never matched, so your character stood idle instead of attacking after them (the same mismatch also broke target-priority "follow this member/leader"). Roster matching now normalises both sides to the given name (the first whitespace-delimited token), so the follow-up attack and follow-target both fire whether or not the member carries a family name.
- **On login your location came up "completely unknown" even while you stood in a known room.** The room tracker is hydrated to your last-known room (Confirmed) on profile load, then the client auto-enters the realm by sending the configured entry command — default `E`, which collides with the cardinal *East*. That keystroke rides the same outbound observer that sniffs your manual movement, so the entry `E` was read as an East step and walked the freshly-hydrated tracker off the real login room (Confirmed → Pending → Suspect ×3 → Lost). The main-menu entry automation now flags that one keystroke to the observer as a menu selection rather than a move, so the login room stays confirmed and navigation knows where you are immediately.
- **Saving a running loop falsely prompted "you changed the loop — run the new one?" when you hadn't edited a thing.** The prompt compared the editor's rows against the runner's *live* loop, but the runner rotates its waypoint list in place as it walks (and re-rotates on restart), so an untouched loop read as changed just from having been walked. The prompt now gates on whether the editor's own steps actually differ from what you opened, so it only appears when you genuinely edited the route.
- **The Navigation activity chip claimed "looping and moving" while the character stood still.** When a loop failed, the runner raised its Failed event *before* resetting its own state, so the navigation view read the stale Running/looping state and pinned the chip on "Looping" even though the run had stopped. The runner now resets to Idle before raising Failed, so the chip reflects the real stopped state.

## 1.4.3

A per-BBS death-floor setting that keeps the emergency auto-hangup firing through the whole bleeding-out window instead of giving up at 0 HP — and learns the realm's true floor from your own slow deaths.

**Added**
- **A per-BBS "Player dies at (HP):" setting (Settings → BBS → Realm mechanics, seeded at -25).** In MajorMUD, hitting 0 HP doesn't kill you — you *drop* and bleed out (you can't move, fight, or cast, but you're still revivable by another player's `aid` or a heal, and you can still hang up); death only happens when HP falls to a per-realm negative floor. That floor is a realm balance knob, so it lives on the BBS profile alongside the game-menu commands, not in per-character health settings.
- **The death floor now auto-refines itself from your observed *slow* deaths (toggle: "Auto-refine the floor from slow deaths", default on).** The -25 seed is only a guess; a realm's real floor can sit anywhere. When you bleed out gradually — dropping into the negative band and losing a few HP per tick until you die — the character lands *right at* the floor, so that final HP reading is the floor, and the client refines the BBS setting toward it. Overkills are discarded: MajorMUD prints the *same* "You have been slain by …" line whether you bled out or were flattened by one blow, so the death line can't tell them apart — only the HP trajectory can. A death straight from positive HP (no bleed observed), or one where a single in-band drop exceeds 10% of your max HP (a combat hit blowing past the floor and over-negativing it), is rejected rather than risk corrupting the floor. The classifier is self-calibrating — it learns the realm's passive bleed-tick size from the descent itself rather than hard-coding one — and a missed measurement is cheap because the next clean slow death corrects it. Untick the toggle to pin your manually-entered value.

**Changed**
- **The emergency low-HP auto-hangup now fires all the way through the bleeding-out window, down to the death floor.** Previously the hangup bailed the instant HP reached 0 — which is exactly when a character has dropped and most needs the escape. Since a dropped-but-not-yet-dead character can still execute the main-menu exit, the auto-hangup now stays live across the whole `(death floor, hang-trigger]` band: it keeps trying to disconnect a bleeding-out character right up to — but not past — the point they actually die (a character already at or below the floor is dead, so there's nothing left to disconnect). This also fixes a gap where a *non-caster* who dropped (0 HP, 0 mana) would skip the hangup entirely, because the health engine's dead/dropped early-out ran before the hangup check. The hangup remains strictly HP-driven — mana is never a trigger — and every existing kill-switch (Disable hangups, hang-threshold 0, the all-off-mode opt-in) still applies.

**Added**
- **A death now halts every movement engine and holds you in the graveyard until you manually resume.** Dying in MajorMUD drops all your non-loyal gear and teleports you — alone — into a graveyard room; if you were leading a party, that party disbands on your death, so afterwards you're always the one who'd drive movement. Previously an active loop / walk-to / Auto-Lair would just keep going, walking your freshly-revived, stripped character back into whatever killed it. The client now recognises your own death (the canonical *"You have been slain by …"* line) and asserts the same pause the manual **Pause** button uses, so nothing moves until you deliberately resume from the Navigation window. Because it rides the existing user-pause, every resume affordance already clears it and it can never leave a stuck gate. While the death-hold is active the Navigation activity chip reads **Paused — recovering** (instead of a plain **Paused**) so it's clear *why* you're stopped; the flavour drops the instant you resume.
- **When a party member dies mid-route while you're leading, the client now clears their corpse's phantom invite slot so your loop keeps running.** When a non-leader party member is killed, MajorMUD doesn't drop them cleanly — the dead character lingers in your `par` as an `[Invited]` (pending) slot, indistinguishable from a genuine recruit you're still waiting on. Left alone, the auto-party engine treats that corpse as an invitee and holds your loop / walk-to / Auto-Lair until the whole *If leading, wait only* window elapses. Because we *know* this "invitee" is actually a member who just died — every observer sees a *"&lt;Name&gt; has died."* line — the client now waits for the current fight to finish, confirms the dead member is showing as an `[Invited]` slot, sends `uninvite` to clear it, and lets the route continue instead of stalling. Every action is doubly bounded: it only ever acts on a name that was an *active* member of your party at the moment it died, and only sends the uninvite once that same name actually shows as invited — so a still-pending recruit or a same-named mob can never trigger it. Gated on a movement engine actually running, since hands-on play leaves party state to you.

## 1.4.1

A live activity-status chip in the Navigation top bar, so a stalled loop explains itself — including holds from a party member's `@wait` and your own movement-blocking ailments.

**Added**
- **The Navigation top bar now shows *why* the movement engine is doing what it's doing.** Next to the loop name and step counter sits a new colour-coded chip that reads the movement-coordinator's pause gates directly: **Moving** (green) while stepping, **Fighting** (red) while the room is held for combat, **Waiting — …** (amber) with the actual reason (resting on low HP, meditating on low mana, **a party member asked us to `@wait`**, **held** by your own status effect, a hurt party member, a pending invitee, following the leader, corpse recovery, looting), or **Paused** (muted) when you paused it yourself. Previously a loop that stopped mid-run gave no on-screen reason — the gate holding it was only visible in the debug log — so "why did my loop just stop?" had no answer in the UI. The chip is hidden while idle. Backing it, `MovementCoordinator` gained a fine-grained `GatesChanged` event that fires on every gate transition (not just the coarse paused↔running flip), so the reason stays accurate even when one hold swaps for another without the engine ever un-pausing (e.g. combat ending straight into a rest).

**Changed**
- **An inbound `@wait` from a party member now actually holds our own movement, not just the roster chip.** The receive side already recorded who asked us to wait (and lit the PartyWindow's WAIT chip), but nothing tied that to the movement engine — a running loop would keep walking away from a resting member. A new pause gate now holds the active loop / Auto-Lair / walk-to while any member is waiting. It releases on **either** of two paths, matching the game's flag: the same member sends `@ok`, **or** the leader's wait timer expires — the existing *If leading, wait only (s)* value caps how long we hold before giving up on a member who never sent `@ok`, so a dropped / AFK member can't strand the party forever (0 disables the timeout — only `@ok` releases). The existing *ignore `@wait` when leading* opt-out still applies, so it only holds waits you haven't chosen to ignore. The chip surfaces this as **Waiting — party asked to wait**.
- **Your own movement-blocking ailment (held / entangled) now shows on the chip.** A *held* status stops movement at the server without any client-side gate, so a stuck loop used to read **Moving** with nothing happening; the chip now reads the live condition flags and shows **Waiting — held** until the effect ends.

## 1.4.0

A batch of party, movement, and navigation bug fixes plus two UI quality-of-life touches.

**Fixed**
- **A party Mystic/monk was dropped and re-added every `par` poll, spamming mid-combat `@health`.** The `par`-row parser only recognized the mana bracket `[M:N%]`, but a Mystic's secondary resource is *kai*, rendered `[K:N%]`. So every poll where a Mystic member had kai, their row failed to match, end-of-block reconciliation read them as having left the party and dropped their roster row — then re-added them on the one poll where their kai happened to read 0% (the game omits the bracket entirely at 0%), and that re-add fired PartyPoller's on-join `@health` round-trip *mid-fight*. The row regex now accepts both `[M:…]` and `[K:…]` into the same resource field, so a Mystic parses whether their kai is full or drained and their roster row stays put across the cycle.
- **A drained party caster's mana/kai bar froze at its last non-zero reading instead of dropping to 0.** `par` omits the secondary-resource bracket entirely when the pool is at *exactly 0 points* (a 0-*points* rule, not 0-*percent* — a caster with a few points left still prints `[M: 0%]`), and this holds for mana and kai alike. The row parser left the percentage untouched on an absent bracket, so a fully-drained member's bar stayed pinned at whatever it last showed. When the member is a known caster (a prior `@health` established their pool), an absent bracket is now read as 0, so the PartyWindow bar empties correctly; no-pool classes and not-yet-`@health`'d members are unaffected.
- **A `@health` round-trip was sent the instant an invite went out, before the invitee had joined.** Inviting a player added their roster row with the invited flag still unset, and `ObservableCollection.Add` fires its change notification *synchronously* — so the party poller saw the new member and fired the on-join `@health` before the code that marks the row invited had even run. The row is now constructed with the invited flag already set, so the poller's "skip invited members" gate holds and `@health` only fires once the player actually joins the party.
- **The auto-walker / movement loop stalled after crossing a text exit (e.g. `go path`, `go manhole`).** The walker announces a text exit to the room-tracker *with* its resolved cardinal, then sends the bytes — which flow back through the outbound-movement observer, which announced the *same* step a second time (cardinal-less). That phantom pending move kept the tracker's queue non-empty after the walker had already landed, holding it in the `Pending` state and stalling the walk until the next room re-display flushed the phantom. The observer's text-exit path now debounces against the engine's own announcement, exactly as the cardinal path already did, so a text-exit step enqueues once and the walk continues without a stall.
- **A navigation loop's blue loop-path overlay vanished when the Navigation window was closed and reopened mid-loop.** Reopening constructed a fresh view-model that seeded the walk path and status text but not the loop polyline, so the loop line only reappeared after the next step redrew it. The overlay is now seeded on construction, so an active loop's path shows immediately on reopen.

**Added**
- **Session Stats is now on the terminal right-click context menu.** The main-window right-click menu gains an *Open Session Stats* entry (mirroring its hotkey), alongside the other quick-open window shortcuts.

**Changed**
- **The Settings *Spells* tab is now labelled *Spells + Ailments*,** reflecting that it configures both spell casting and ailment handling. The persistence keys are unchanged, so existing per-tier settings carry over untouched.

## 1.3.0

Pre-emptive elemental-resistance guard for attack spells.

**Added**
- **Attack spells now skip a target that resists their element ≥ 100%.** Before casting a configured Normal / Alternate attack spell, the combat engine looks up the spell's damage element (`AttType`) and the monster's matching `Resist-<type>` value; when the target resists that element **≥ 100%** — where the spell deals **0 damage** (exactly 100%) or **heals** the monster (> 100%) — the slot is skipped down the attack cascade (Normal → Alternate → weapon), exactly like the existing "no-effect" immunity and `SpellImmu` level gates. It's deterministic and pre-emptive, so a round and its mana are never wasted casting into a wall. Only the **five elemental** types (Cold / Fire / Stone / Lightning / Water) are guarded — **Magic Resist** (`AttType 4`, a capped, probabilistic cut) and **poison** (`AttType 6`, a binary immunity) are never pre-empted, and a **negative** or partial (1–99%) resist still fires the spell (it's a damage bonus or a mere reduction, not a reason to skip). Two new game-data indexes back it — `MonsterResistIndex` (elemental resist codes 3/5/65/66/147 by monster) and `SpellAttackTypeIndex` (`AttType` by cast-code) — each failing open when the data is silent, so a thin data set never suppresses a spell.

**Changed**
- `GAME_MECHANICS.md` mechanism 3a now records that an elemental resist is **signed**: a negative `Resist-<type>` is a *vulnerability* (the element deals extra damage). The full curve runs vulnerability (negative) → normal (0) → immunity (100) → healing (> 100), and only the ≥ 100% end is safely pre-emptable.

**Fixed**
- **Undead monsters stored as `255` were shown as *not* undead in the Game Data browser.** The Monsters detail pane tested the `Undead` byte-boolean with `== 1`, but the MDB stores Boolean `True` as `-1`, which arrives as **`255`** for 8 of 1.11p's undead (banshee, zombie cat, skeletal steed, …). The test is now `!= 0`, so every undead flag renders regardless of whether the source stored `1` or `255`.

## 1.2.3

Corrects how damage-type resistance splits into three unlike flavors, and records the spell-targeting monster-type taxonomy.

**Changed**
- **Damage-type resistance split into its three real flavors, with the reference client's exact math.** `GAME_MECHANICS.md` previously lumped every `Resist-*` code — including Magic Resist — under one "flat N% cut, 100% = 0 damage, >100% = heal" rule. That's only true for the five *elemental* types (Cold/Fire/Stone/Lightning/Water, `AttType` 0/1/2/3/5 → resist codes 3/5/65/66/147), which is the one flavor deterministic enough to *pre-empt* (skip a spell whose element the target resists ≥100%). **Magic Resist** (M.R., code 36) — the cut on `AttType 4` "Normal" spells like `magic missile` and `harm` — is now recorded with its actual two-part equation: a partial damage reduction that baselines at M.R. 50 and **caps at 50%** (75% under AntiMagic), plus a separate full-resist *roll* at `M.R. / 2` percent. So 100 M.R. is **not** 0 damage (it's ~25% less damage and a ~50% negate chance); M.R. must never feed a ≥100%→skip guard, and code 17 `Damage(-MR)` bypasses the reduction. **Poison** (`AttType 6`) is recorded as *not resistible at all* — binary affected/immune, immunity sourced from race/items (Kang race, golden headdress, swamp/snakeskin boots) rather than a stat. Adds the `AttType`→element→resist-code mapping table and documents the `TypeOfResists` column (0 = never full-resistable, 1 = only under AntiMagic, 2 = always) that gates the full-resist roll.

**Added**
- **Spell-targeting monster-type taxonomy recorded.** `GAME_MECHANICS.md` gains a *Spell targeting: monster type tags* section: a spell's eligibility against a monster is a match between a **spell-side targeting tag** (`AffectsLivingOnly` 108, `AffectsUndeadOnly` 23, `AffectsAnimalsOnly` 80, or *no tag* = affects everything) and a **monster-side type flag** (the `NonLiving` ability 109 whose *absence* means living, the `Animal` ability 78, and a dedicated `Undead` column). The `Undead` column is a **byte-boolean** — `0` = not undead, any non-zero = undead — and across 1.11p it holds `0` (986 rows), `1` (107), **and `255`** (8 rows, the MDB's Boolean `True` stored as `-1`), so the correct test is `Undead != 0`, **never `== 1`**. `harm` (living-only, blocked by NonLiving) versus `magic missile` (no tag → hits living, nonliving, and undead alike) is the worked contrast, with a thug / lashworm / acid slime / skeleton example table; the charm family (`enslave`, `charm animal`, `song of charming`) all share the `Enslave` base ability (code 6) and differ *only* by targeting tag. A "charm level" cap is flagged **[NEEDS CONFIRMATION]** — unverifiable against the reference client, which only *displays* these tags.

## 1.2.2

Records how attack spells actually fail to damage a monster.

**Changed**
- **Attack-spell failure modes documented accurately.** `GAME_MECHANICS.md` now separates the three independent reasons an attack spell does no damage, each confirmed against 1.11p data: (1) `SpellImmu +N` — a level gate that blocks any spell whose base learnable level is below N (monster #184's `SpellImmu +10` blocks everything learnable at level ≤ 9); (2) a spell targeting restriction such as the priest `harm` spell's *living-only* tag, which no `NonLiving` monster (e.g. an acid slime) can be hurt by — the actual cause of the `Your spell has no effect on <monster>.` line, corrected from the earlier "immune to a damage type" framing; and (3) flat percentage resistance per damage type (#184's `Resist-Fire +50` halves fire damage; 100% deals 0, over-100% heals). The message-catalogue label for the no-effect line is corrected to a targeting mismatch rather than "immunity."

**Changed**
- **Weapon-swap message reference corrected.** `GAME_MECHANICS.md` claimed a weapon swap prints two lines (a removal then a wear). A live capture (swapping a quarterstaff and a dagger) confirms it prints a single line — `You are now holding <X>.` — with no removal line for the displaced weapon, which returns to the pack silently. The two-line pattern is the *armor*-into-an-occupied-slot case. The message catalogue and the Equipment prose now record the weapon-vs-armor distinction, matching the client's own inventory parser.

**Added**
- **Attack-spell immunity vs resistance recorded.** `GAME_MECHANICS.md` now documents that a spell's `Your spell has no effect on <monster>.` line is a hard, binary *immunity* (which the combat engine gates on) — distinct from percentage *resistance*, a numeric reduction where exactly 100% resist deals 0 damage and over-100% resist inverts into *healing* the monster with no "no effect" line to flag it.

## 1.2.0

Gear actuation gets a single owner, and enabled backstab loadouts now arm themselves in the auto-walker's pre-move sequence.

**Added**
- **Backstab gear auto-fire.** When a Backstab equipment set is enabled, the auto-walker now gears up for the next sneak as part of its pre-move sequence: the backstab weapon and the set's armor are equipped — sending only the pieces not already worn — and *then* the sneak fires, so the whole approach is weapon → armor → sneak → move. Because equipping breaks sneak, the gear has to land before the `sn`; sequencing it into the pre-move step guarantees that and puts the loadout in hand before the surprise round.

**Changed**
- **The Equipment Manager is now the sole actuator for gear.** Combat decides which weapon to wield and delegates the swap; equipping logic no longer lives in two places, so a set applied from the Workshop and a weapon flipped mid-fight take the exact same path. Swaps use the uniform `eq` verb and diff against the *live* worn loadout, so only the pieces that actually differ hit the wire — no redundant equips, and no cached "believed-equipped" shadow that could drift out of sync with what's really worn.
- The bug report's combat weapon state now reads the live worn weapon / off-hand straight from inventory instead of the removed shadow fields.

## 1.1.1

Richer in-app bug-report capture, so a single report pins more of the failing state.

**Added**
- Session section now stamps the app **version** and the **Debug / Combat diagnostics** on/off state.
- New **Party** section — roster, roles, leader, and pending-invite flags (the state party-targeting and `@join`-nag bugs hinge on).
- New **Live engine state** section — the `@join`-nag table (who's being chased and how far along) and the combat weapon-swap shadow (believed-equipped weapon + current target).
- Movement section now reports the suspect-strike count and the last observation's observed / open-door exit sets.
- Program log flags when both diagnostic channels were off, so an absent decision trail isn't mistaken for a quiet engine.

**Changed**
- Scrollback lines in the report are now timestamped (the live-screen tail stays unmarked), so log timestamps can be aligned against the wire I/O.
- Introduced this version history; the README top block now mirrors the current release.

## 1.1.0

Seven bugs surfaced in a live party session, batched into one PR.

**Fixed**
- Redundant hidden-exit search after a manual `sea` had already uncovered the exit.
- `@poisoned` / `@blind` / `@confused` / `@diseased` / `@held` party-sync announces bouncing an "invalid command" reply.
- Toggling an ignore-ailment setting mid-poison not releasing the standing `@wait`.
- Self-targeted party heals including the family name (`mihe Raijin Par` instead of a bare `mihe`).
- Backscroll window freezing when opening a ~10k-line transcript.
- The `@join` nag being cancelled by an unrelated automated telepath (an `@health` reply).
- Combat re-equipping an already-worn weapon on the first round.

## 1.0.0

Initial release. A faithful CP437 / VT100 Telnet client for MajorMUD with a MegaMUD-style automation suite — combat, party, navigation, healing, spells, character workshop, scripting, game-data import, a 4-tier settings hierarchy, and quality-of-life tooling — all in modeless, dockable windows.
