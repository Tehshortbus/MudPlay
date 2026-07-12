# Version history

Notable changes per merged PR, **newest first**. The top of the [README](README.md) mirrors the most recent entry. Versioning follows semver (post-1.0), by change type: **MAJOR** = whole-program refactor, **MINOR** = a new feature or enhancement, **PATCH** = bug fixes (one increment per report handled).

## 1.41.0

- On Paradigm, a suspected position mismatch now asks the game where you are (`rm`) and re-anchors to the authoritative `Location: map,room` instead of dropping straight to the heuristic backtrack / "Lost" dialog
- The navigation engine pauses during the `rm` round-trip so the reply reports a stationary room, then re-plans from the confirmed position
- Heuristic backtrack recovery stays the fallback — used when the realm isn't Paradigm, the reply times out, or the reported room isn't in the map graph
- Bug report captures the resync state (awaiting-rm flag, request in flight, last resolved room)

## 1.40.4

- Movement refusals ending in `!` (Paradigm's "There is no exit in that direction!") now clear the pending move instead of stranding the walker
- Auto-deposit re-reads holdings at the bank before depositing, so an unobserved en-route toll no longer makes it try to bank a stale pre-toll amount and bank nothing
- A player failing a sneak into your room ("You notice X sneaking in…") is no longer mis-tagged as a monster that jams the combat gate and freezes the loop
- Conversation channel-filter toggles now repopulate in one pass instead of shuddering through the whole history line by line
- bug reports addressed: paradigm-20260712-101344, paradigm-20260712-105506, paradigm-20260712-114119, paradigm-20260712-144258

## 1.40.0

- Fixed a freeze when a walk-to was queued during a loop whose auto-deposit route crossed a dark area — the reroute no longer lets two controllers drive one walker
- Auto-deposit bank runs that return through the dark now chain an errand: origin → bank → light shop → origin → resume loop, buying only the light the route needs
- The dark return leg falls through to a plain return without light when auto-light is off or no reachable shop stocks the needed light
- Bug report captures the auto-deposit reroute status

## 1.39.0

- Backscroll window now draws only the rows in view — drag-selecting and scrolling stay smooth on a deep history instead of bogging down
- Program log is teed to a rolling on-disk file (Data/Logs/{timestamp}-program.log) so a hard hang or kill leaves a post-mortem trail the in-memory ring can't
- `train stats` now switches to character-mode input the moment the command is sent, so arrow keys drive the full-screen stat box on realms whose menu marker arrives too late (Paradigm) — no longer captured by history recall
- Conversation window: auto-scroll now pins to the true bottom, the search box no longer stretches its height, and it moved above the auto-scroll checkbox so the filter row can wrap freely as the window narrows
- Spells & Ailments tab gains "Bless self while resting" / "Bless self during combat" toggles — a solo hunting loop that's rarely idle can now recast its own buffs during rest or combat instead of being starved between fights
- bug reports addressed: paradigm-20260711-235738, paradigm-20260712-093615, paradigm-20260712-100737

## 1.37.0

- Auto-deposit bank runs no longer reset session statistics on the way back to a loop — the reset fires only on a genuine first start
- Transaction history is user-owned — only its own Clear button (or connect / character switch) clears it, never a loop start or party @reset
- Transaction History window gains a Clear button
- Auto-deposit no longer wedges for the session when a bank run can't complete — an aborted reroute re-arms the gate and retries (throttled so an unreachable bank can't thrash the engine)
- bug reports addressed: paradigm-20260711-235419

## 1.36.0

- Loops now open a closed door mid-circuit — bash / pick / key it like the walker does — instead of idling on it
- Combat resumes right after a between-round heal fired the instant the fight engaged, instead of missing a round
- A fleeing player dragging the engaged mob out of the room now clears the combat gate, so the walker stops swinging at empty air
- Auto-light lights only rooms we can't see and puts the light away on entering one we can — no more over-lighting a lit town
- A burned-out light re-readies a same-named carried spare instead of leaving the player stuck blind
- bug reports addressed: paradigm-20260711-152210, paradigm-20260711-152453, paradigm-20260711-175844, paradigm-20260711-180449, paradigm-20260711-181619

## 1.35.9

- Loop no longer stalls when a room refuses entry mid-combat — sends break, waits, then retries the move
- Walker holds for combat in a dark room instead of stepping through while a mob is still engaging
- Dark-corridor drift re-anchors on a uniquely-named lit room reached through a door instead of losing position
- Route planner won't buy a ferry skiff just to shave a single step off a free path
- Who-list parses rows with freeform guild names, so the players table no longer truncates on Paradigm
- Auto-light readies a carried light the moment a dark room is seen, even off a loop or a manual step
- A readied light burning out re-readies a carried spare instead of trusting the stale inventory
- Map no longer snaps back to the player mid-browse while panning another floor
- Club seed no longer carries an auto-collect flag
- bug reports addressed: paradigm-20260711-140923, paradigm-20260711-141409, paradigm-20260711-141605, paradigm-20260711-141644, paradigm-20260711-145959, paradigm-20260711-150847, paradigm-20260711-151442, paradigm-20260711-154537, paradigm-20260711-154840

## 1.35.0

- Backscroll now shows a frozen snapshot of scrollback history from the moment it opens instead of live-appending, so it no longer lags while following a fast party leader
- New output keeps recording in the background; close and reopen to catch up with nothing missed
- The "Go to live" button is now "Jump to end" — scrolls to the newest captured row
- Last history line clears the status bar and multi-line drag-select is snappier
- bug reports addressed: stock-20260711-090329

## 1.34.15

- Character Info's Inventory box now shows the coins line and a keys list, parsed from the pack readout
- Discard currency drops are re-audited after banking, buying, or selling so stale held-cash flags clear
- Combat's "attack last" now fires only after every party melee and cast announce, under the Follow-target priority
- A "no effect" result no longer forces a manual Resume — the engine wait auto-clears
- Toolbar and nav pause controls read only the user-override tier, never engine-owned waits
- Walk-to now shows a Save→Pause chip so a queued route is visible before it starts
- A @wait-held, un-poisoned leader rests to use the downtime, and a follower mirrors the leader's rest unless it's poisoned
- Movement while blinded dead-reckons position through the room graph, re-anchoring when sight returns
- Curable-ailment on/off say pairs clear their chip authoritatively, and a @status reply pulls a fresh chip resync
- Party-window health/mana bars now align across rows regardless of which status chips a row shows
- bug reports addressed: stock-20260711-083241, stock-20260711-083306, stock-20260711-083614, stock-20260711-083759, stock-20260711-084637, stock-20260711-090022, stock-20260711-091137

## 1.34.7

- Walker no longer strands at a bashable/pickable door mid-route — a sub-FSM step is no longer double-driven into a duplicate, stray-verb door request
- Duplicate per-direction door requests are dropped instead of stacking behind a live one
- Combat resyncs the room immediately when a kill's death line can't be pinned to a roster mob, instead of stalling ~5s until the next swing no-ops
- Nav tooltips now list standalone room actions ("pull drawer", etc.) under Room commands for rooms with no multi-action exit
- A non-followed party member's attack announce no longer drives a duplicate re-fire under a Follow-target priority
- Between-round self-heal now resumes the attack in the same round instead of waiting a full round for a follow announce that never comes
- bug reports addressed: stock-20260710-221533, stock-20260710-221612, stock-20260710-221703, stock-20260710-221836, stock-20260710-222050, stock-20260710-222610

## 1.34.1

- Party re-invite after a chime/CMD teleport now waits until each member materializes, so no one is left behind by a "you don't see them here" invite
- bug reports addressed: stock-20260710-221344

## 1.34.0

- Navigation routes around item-gated exits and hazard rooms it can't safely cross, instead of walking into them
- Room-entry hazards (damage/drown spells, raft crossings) are recognized off the game data; a room is avoided unless a counter item is carried
- User-initiated walks with a shorter gated shortcut now pop a free-vs-direct route picker listing what each route needs
- Cross-room multi-action exits (act in one room to open an exit in another) are planned and executed in step order
- Choosing the direct route provisions its missing gate/hazard items through the existing acquire pipeline

## 1.33.0

- Combat priority is now a simple "Spells first / Physical first" dropdown, replacing the reorderable priority list
- Backstab and debuffs no longer sit in the reorder list — the backstab opener always leads when enabled, debuffs queue alongside buffs/heals
- Physical first falls back to the attack-spell cascade when no configured weapon can damage the target (magical creature), instead of swinging uselessly

## 1.32.0

- Items flagged CannotBeTaken are never auto-collected, even with AutoCollect set
- Containers flagged AutoOpen now auto-`open` once when picked up, then re-read the pack with a single `i` even when several arrive at once
- Monsters flagged DontBackstab are skipped as the backstab opener — a non-flagged target is preferred, and the room still clears via a normal opener when all are flagged
- Per-monster override attack / pre-attack spells now substitute for the global Combat-tab choice for that species, bypassing the immunity/level/resist gates while keeping mana and cast-count limits
- Removed the redundant NotHostile monster flag (alignment + guard flags already cover it)

## 1.31.0

- Merchant shops in the room-detail popup now show a stock table: item, max, restock, and buy/sell prices
- Training rooms in the room-detail popup now show the class and level band they train
- The item dialog decodes a chest's loot table — each possible drop with its % chance, plus the min/max items an open yields
- Chest drop names are clickable, jumping the Game Data browser to that item; the % column aligns with a separator beside the name
- Double-clicking another item row now swaps the open item menu to that item instead of stacking a second window
- Item dialog's Name/Use fields sit left with the pane splitter defaulting to their right edge

## 1.30.0

- Clicking an obvious exit in a Game Data room-detail popup now walks the popup to that neighbouring room
- An already-open Navigation map follows the exit click; a closed map is left closed instead of being forced open

## 1.29.0

- A BBS that renames the runic coin (e.g. "quatloos") is now honored everywhere: coin parsing, get/drop/hide/give commands, wealth math, and every wealth display
- Cash pickup, auto-deposit, stash, @share, and the Session Stats / Player Workshop coin readouts no longer break on a renamed-runic realm

## 1.28.0

- New Settings → General toggle scales the terminal font to fill the window, keeping the fixed cell grid
- Scaling is capped so a maximised window enlarges the text reasonably instead of absurdly
- Off by default: the grid keeps its configured font size and sits centred in a larger window

## 1.27.0

- Window positions now restore when you switch character profiles, instead of staying where they were
- A window whose saved monitor is gone, or that would open off-screen, re-anchors next to the main window
- Windows still visible on a connected second monitor keep reopening there

## 1.26.1

- Walker now halts instead of walking deeper when an in-flight move carries it out of a room with a hostile it had just engaged
- A movement step can no longer slip onto the wire in the instant between combat engaging and the walk pausing
- bug reports addressed: stock-20260710-002816

## 1.26.0

- Backscroll drag-select now spans multiple rows and Ctrl+C copies the exact character range across lines
- Timestamps moved to an aligned gutter, kept out of the copied text
- Backscroll opens parked just above the live line on the newest scrollback instead of jumping to the tail
- Transcript renders with the terminal's aliased VGA font — no colour fringing on glyph edges
- Fixes a crash when opening the Backscroll window
- bug reports addressed: Crash-20260710-021530

## 1.25.0

- Auto-discard drops flagged items down to their keep floor whenever inventory changes — clears chest dumps and unwanted auto-collected loot
- Auto-buy restocks flagged items at a shop `list` up to their Max-to-get cap, honoring live stock and reading affordability off the live result
- Auto-sell offloads flagged items at a shop `list` down to their keep floor, one `sell` per copy
- All three engines are driven by the item-edit dialog's Auto-buy / Auto-sell / Auto-discard flags and gated by the existing Auto-get items master toggle — no new toggles
- Auto-buy / Auto-sell are greyed for LIGHT items (Auto-light owns those); first ticking Auto-buy seeds a Max-to-get of 10

## 1.24.0

- Session Statistics shows time-to-level, honoring banked levels — "N levels gained · HH:MM:SS until level X" at the session's exp/hour rate
- Game Data monster Greet rows are click-through — the popup decodes the textblock chain like MegaMUD, listing each keyword the monster responds to and the effects it fires (Cast, Item give/take, Ability, Class/Race gate, AddExp, Learn/Checkspell, Summon, Random branches, Cost/Givecoins, Teleport, Remote Action, Testskill)
- Game Data monster record spawn/placed/summoned room lists are now clickable chips — click a map/room to open that room's detail popup
- The room-detail popup (Rooms-table double-click or a monster room chip) is now interactive — click the room title or any exit to open/centre the Navigation map on that room, click a monster name to jump to its Game Data record, and Add/Remove the room from the blacklist inline
- Modify Room Blacklist editor columns (Map, Room, Name, Can't reach) are click-to-sort, ascending/descending; a "Toggle can't reach" button inverts the flag on every highlighted row at once

## 1.23.19

- Dropped/dragged ally stays a heal target — the client keeps polling their health and name-heals them through a re-invite instead of abandoning them
- Auto-combat re-engages after the leader announces an attack instead of sitting idle
- Party-leader target priority + attack-last now hits the leader's target, not the first monster in the room
- No redundant re-attack when we're already on the leader's chosen target
- `Your command had no effect.` drops the vanished target and re-evaluates instead of stalling until a manual room redisplay
- Loop advances in a cleared room without a manual room redisplay
- Selecting a room and pressing Run seamlessly swaps modes (walk-to ↔ loop ↔ auto-lair) and starts immediately
- Starting a loop resets the session statistics and `@reset`s the party
- Hand-typed hidden-exit moves like `move wall` re-anchor navigation position
- Cleanup now exits and disconnects every party member — none left behind
- After cleanup, the leader reforms the party (waiting up to the wait period) and resumes the loop, instead of stalling until a manual re-invite
- Follow works after training — a trained follower is re-invited as they re-enter the realm, no manual re-invite/re-join
- Mystic kai shows `K` in the party menu instead of `M`
- Equipment Manager no longer carries loot toggles or a synthetic Inventory row; the item-edit dialog is the sole editor of auto-collect/stash/discard flags
- Bug reports capture resolved effective settings; the program log now records settings changes and engine commands
- bug reports addressed: stock-20260708-212146, stock-20260708-212316, stock-20260708-212647, stock-20260708-212732, stock-20260708-212931, stock-20260708-213015, stock-20260708-213610, stock-20260708-231716, stock-20260708-231759, stock-20260709-001417, stock-20260709-001547, stock-20260709-001623, stock-20260709-005001, stock-20260709-094623, stock-20260709-094822

## 1.23.4

- Leader crossing a chime teleport no longer re-fires the teleport or spams `@join` at members who already rejoined — the walker waits for the destination room to confirm before it treats the step as done
- The reformed party's walk continues on arrival instead of freezing at "waiting for invitee to join"
- Stopping a walk mid-reform now clears the party-invite hold, so you can start walking elsewhere without being pinned by a stuck gate
- bug reports addressed: stock-20260708-171842

## 1.23.3

- `@party <command>` now relays any command to the whole party (the party-bound analogue of `@do`), not just a fixed verb whitelist — so `@party use chime` / `@party ring chime` / `@party .hi` actually fire on followers
- Chime-teleport party reform now works end-to-end: followers relay-teleport with the leader, so the leader's re-invite reaches every member instead of stranding the ones who never crossed
- `@party` refuses only `set suicide` and `reroll`; every other command passes through
- bug reports addressed: stock-20260708-163726, stock-20260708-163814, stock-20260708-163926

## 1.23.0

- Navigation re-latches a name-unique room through a closed door: a swung-shut door dropping an exit from the display no longer freezes position until a manual reposition
- Auto-sneak re-fires after a silently lost sneak attempt, instead of stranding stealth for the rest of the run
- "Ring chime"-style CMD teleports are now walkable — navigation routes and crosses them like any other exit
- A party leader crossing a chime teleport relays the whole party through, then re-invites and waits in place for them to reform
- bug reports addressed: stock-20260707-205936, stock-20260708-075501, stock-20260707-235341, stock-20260708-000851

## 1.22.0

- Followers auto-rejoin their party after an unexpected disconnect: on re-entering the game they telepath @comeback to the leader they were following, who then owns the pickup (our room key attached when the map position is confirmed)
- The followed leader is remembered across a client crash but forgotten on a clean quit or deliberate leave, so only an unexpected drop rearms the rejoin
- Leaders also recover a dropped member on their own: when the member re-enters, the leader probes @where and walks out to collect them
- New Settings → Party "return distance" (default 30 rooms) caps how far a leader walks to recover; a farther-off member is declined and told why
- A leader who backfilled the party to its 6-member cap while a member was gone declines the return and tells them why
- @forget is now bidirectional: either side drops the other from the party and clears the rejoin memory; the leader uses it to decline a recovery
- Remembering a former leader overrides the per-player "join if invited" flag, so their re-invite is auto-accepted on reconnect
- bug reports addressed: stock-20260707-210828

## 1.21.0

- Leading party now holds in place when a follower drops connection, instead of sprinting off without them
- Hold lasts the "If leading, wait only" window, then resumes; the returning member re-parties in place if they reconnect first
- Settings → BBS gains an optional board disconnect line (literal `{name}`/`*` syntax) for boards whose logoff wording isn't the built-in one
- Player game-data table gains an optional account-name override, so a board that logs off by account name still maps the drop to the right party member
- bug reports addressed: stock-20260707-210828

## 1.20.4

- A monster that pursues us into the next room is now fought instead of dragged: its walk-in arrival no longer gets wiped on the room change, so the walker holds and we stop to kill it
- The pursuer-keep is suppressed while fleeing, so a monster that chases us mid-flee doesn't turn us around to fight — we keep running
- bug reports addressed: stock-20260708-000606

## 1.20.3

- Attack-last re-fire opens with `bs <target>` when the surprise round is still armed, instead of firing the normal attack (`pu`) and wasting the backstab — the re-fire still lands us last in line, just with the opener
- Kill re-pick no longer double-swings: the interrupt-resume stands down when a fresh attack just went out, so the surviving mob isn't attacked twice in the same round
- Utilize-shadowrest toggle is now hidden on realms without the ability (stock), showing only where the active game data ships a ShadowRest class
- bug reports addressed: stock-20260707-203503, stock-20260708-074641

## 1.20.0

- Backstab surprise round is now tracked to resolution: the first swing after `bs` is read for the `surprise` tell, so a landed vs failed opener is detected reliably
- Attack-order re-fire is held while a backstab is pending, so a party attack announcement can't fire a follow-up `pu` that clobbers the surprise round
- "Run if BS fails" now works: a detected backstab failure flees via the normal break-before-flee escape (previously the setting did nothing)
- Hidden characters now open with `bs` when a monster walks in: hide is tracked optimistically (its success isn't self-observable) and the surprise resolver confirms or flees
- A fresh in-place hide re-arms the surprise round, so a hidden character can backstab each monster that wanders in after a kill
- Auto-hide is now suppressed while in a party, so a member can't hide itself out of reach of party heals and buffs
- ShadowRest (Paradigm): solo, stealthed classes with the ability can now rest through a monster in the room — combat stands down while recovering, then re-opens with a backstab at rest-max
- New Settings → Health → Resting Options → "Utilize shadowrest" toggle (the category was renamed from "Meditation")
- bug reports addressed: stock-20260708-074756, stock-20260708-074918, stock-20260708-075121

## 1.19.4

- Backstab re-opens on every confirmed room change, so hand-walking (not just the walk-to/loop engines) re-arms the surprise round instead of falling back to a normal attack after the session's first backstab
- bug reports addressed: stock-20260707-235708

## 1.19.3

- Attack-last now sends one re-fire per round instead of one per party member — a round's burst of party attack announcements on our target coalesces into a single attack command, landing after the last announce so we stay last without spamming the wire
- bug reports addressed: stock-20260708-000134, stock-20260708-000419

## 1.19.1

- Backstab fires only on a room's true opening round — after the first action (including a cast-interrupt re-attack or a target re-pick) it falls back to the normal attack priority instead of re-sending `bs` into a fight already underway
- bug reports addressed: stock-20260707-235548

## 1.19.0

- Auto-flee now walks the real graph path instead of repeating one direction into a wall — backward retraces the reverse trail toward the run's start, forward keeps heading along the planned route
- Flee distance default lowered from 3 rooms to 2
- Auto-attack swaps to the alternate weapon on the first "no effect" against a monster — the No-effect threshold picker is gone (swinging the same weapon can't turn a no-effect into a hit)
- bug reports addressed: stock-20260707-205136

## 1.18.7

- Fixed walker crash when backtracking a room that had no active path
- Combat round counter now closes each round on the 5-second heartbeat instead of lagging a line behind
- Stuck "fighting" chip after walking into a new room clears — a stale walk-in no longer carries past the move
- Walk-to route overlay trims to the current room mid-combat instead of waiting for the whole room to clear
- Flipping a currency from Collect to Discard now drops the already-carried balance, not just fresh pickups
- Starting a manual run while auto-looping hands off to walk-to cleanly, without the destination chip flickering
- Examining a monster no longer misreads its name as the room name, so position stays in sync
- bug reports addressed: Crash-20260707-210804, stock-20260707-203425, stock-20260707-203928, stock-20260707-204056, stock-20260707-205556

## 1.18.0

- Item Game Data now prices each shop the item is bought/sold at — a line under every shop shows `@<charm>cha BUY: … SELL: …` for the character's charm (or retail 50 when unknown), branched to the active realm's stock/paradigm formula
- Weight moved into the right-hand info pane; the redundant read-only Body location / Item type / Price fields dropped from the left edit pane
- Double-clicking an Item Finder row opens the Game Data Browser at that item's record

## 1.17.4

- No more speculative `eq` on logon — the weapon-swap fast path waits for the first inventory dump instead of drawing "You do not have X left unequipped." for gear that's already worn
- Trainer-menu exit now re-invites a follower stuck at [Invited] after the leader trains, instead of treating the hot invite slot as a live member
- Self-casting bless no longer spams the program log with a buff line per matching catalogue record — one game line collapses to a single applied entry

## 1.17.1

- Status-bar location chip now shows the short map/room number instead of the room name, so exp/hr no longer gets pushed behind an ellipsis by a long name

## 1.17.0

- Item Finder auto-hides stat columns with no values in the current filtered view — narrow to a slot and the irrelevant Dmg/Swings/Hit Magic/etc. columns drop away
- Slot and Name columns always stay; hidden columns return the moment a matching item brings them back

## 1.16.3

- Status-bar location chip's xp/hr now ticks live instead of freezing at the rate captured when you last moved
- Main window opens shorter — trimmed the dead space between the terminal and the toolbar/status bar on first launch
- Darkwood Forest map now draws its whole area — the half hidden behind a same-plane go-path from room 1/1403 is no longer suppressed

## 1.16.0

- Turning off Auto-Heal/Rest now releases a held rest gate at once — a queued walk-to resumes instead of the character sitting idle resting
- Look-target HP readout now floats centered between the room name and the combat ticks instead of jammed against them
- Item Finder now opens pre-filtered to the current character's class, level, and alignment — widen back to (Any) to browse everything
- Character Info's equipped list now aligns every slot flag — (Hands) (Back) (Legs) — in a shared column instead of trailing each name at a ragged offset

## 1.15.0

- Dark rooms now tracked — walking into a room too dark to show its name/exits advances the map by move inference instead of stalling the marker
- Auto-combat engages a monster revealed only by its dark-cyan attack line, even when no "Also here:" line ever lists it
- Dark-room target retracted on "Your command had no effect." so combat stops swinging at a mob that died or fled unseen

## 1.14.1

- DataGrid column headers no longer truncate anywhere — short labels like "Str" render in full instead of clipping to an ellipsis (Item Finder, Game Data browser, Spell Book, Spell Coverage, and the rest)

## 1.14.0

- Logon menu-nav editor can import another character's steps instead of retyping a shared front-end per character
- Import lists every saved character (same-BBS candidates first), copies steps only — usernames / passwords never travel

## 1.13.0

- Item Finder hides items the realm never puts in play (sysop-only / unimplemented / duplicate rows like "bow of silver"), showing only obtainable gear
- Item Finder weapon-type filter adds "(All 1H weapons)" / "(All 2H weapons)" alongside the specific blunt/sharp types
- Item Finder weapons show the avg swings/round over 10 rounds for the live character, sortable like the other columns

## 1.12.0

- Looking at a monster shows its estimated HP range on the status bar — a coarse wound band applied to the monster's max HP, so a fast-regen boss's HP gate is readable at a glance
- Map draws a one-way arrow on connectors with no return exit (class-hall entrances, drop-only passages)
- Map keeps cross-level text portals (go-portal / manhole) off the plane instead of pulling a far floor's rooms onto it, de-cluttering ~4300 rooms
- Map holds a 15-second browse window after a pan / zoom / floor-crawl before snapping back to the player, and re-centres correctly when crossing an up/down exit instead of holding a stale room
- Class-gated exits are parsed, labelled in the room tooltip (e.g. "Druid only"), and dropped from walk-to routes for the wrong class
- Level-gated exits block a walk-to route when your level falls outside the exit's window
- Status-bar room slot now shows the session exp/hour rate alongside the room name
- Party `@wealth` is only probed when a toll is actually on the walk-to / loop route, not on an off-path toll the map search happened to touch
- Looking into an exit no longer fires get / equip / attack against the peeked room — automation waits until you actually walk in
- Auto-combat now engages on a real walk-in that follows a look-direction peek
- Equip-all wears stacked / doubled-up gear instead of stopping after the first item
- Auto-combat-off mid-round releases the walker and clears the in-combat gate so movement resumes
- Hand-casting a spell mid-fight re-attacks a still-alive target immediately instead of idling until the next round
- Rest-if-below now actually sends `rest` when it triggers
- A loop no longer hangs for minutes when a party @wait pause/resume lands mid-step — the in-flight move isn't re-sent, and arriving at the target advances even if the tracker's queue is momentarily out of sync
- Learned spells persist across sessions — Spell Book checkmarks survive a relog instead of blanking until the next `spells` / `pow` poll
- Spell Book cast-on-use list shows only the class's own items, not every universal wand / scroll
- Backscroll copy survives a broken DBus clipboard instead of crashing the client
- A benign background DBus service-missing fault (clipboard / portal on desktops without it) no longer drops a bogus crash report on the Desktop

## 1.11.0

- A fatal crash now drops a `Crash-<timestamp>.md` on the Desktop carrying the exception plus the live client state (scrollback / log / engine), so a lost session is recoverable after the fact
- Auto-equip / combat weapon-swap only issues wear/eq for gear still in your pack — a post-death empty inventory no longer floods "You do not have X left unequipped." each round
- Negative HP is parsed, so a mortally-wounded drop is recognised — engines stop firing commands into a downed body and the low-HP hangup no longer misses a plunge straight into the negatives
- A dropped ally is aided back up even by a non-healer — the rescue no longer requires a party-heal loadout (a name-heal top-up still needs one)
- A downed member answers an @join / @invite with why it can't — mortally wounded, and who (if anyone) is dragging it — instead of silently bouncing the command
- Low-HP auto-hangup only fires with a hostile in the room and re-arms once the danger passes — reconnecting into a clear room no longer loops through hang up → reconnect

## 1.10.0

- Navigation map marks each un-recovered death with a skull; it clears once the deathpile is fully recovered
- Any death — including miracle-save deaths — halts the loop / walk-to / Auto-Lair in the graveyard instead of rerouting straight back out
- Dying clears the room's monsters, so combat no longer re-attacks a phantom target after a party member walks into the graveyard
- Death detail's "Equipped at death" column renamed "Equipment Lost"
- Deathpile now lists the coins on hand at death, each denomination by its own count (100 gold crowns / 1 platinum piece), under "Inventory lost"
- Follower map stops drifting to "suspect" — a follower's `par` poll no longer misreads "You are following <leader>." as the room name
- Auto-deposit no-ops when the Bank dropdown has no valid pick — a stale/orphaned bank key no longer detours to a phantom bank or probes party `@wealth` for a toll
- Bank picker placeholder now reads "(Banks from game data and Stash rooms)"

## 1.9.1

- Party followers stay located on the map — leader-driven drags now feed the room tracker instead of dropping it to "lost"

## 1.9.0

- Session Stats gets a compact top bar for Reset session + Transaction history
- Each collapsible section has its own Reset button
- Resetting Time Analysis restarts the per-hour rates while keeping the running totals

## 1.8.0

- Backscroll window draws a "live" divider marking where logged history ends and the live tail begins
- Engine-sent telepaths (party @-command probes / nags) now show their message in the Conversation window instead of a blank line
- `@`-command replies sent via say now use the period precursor instead of the literal word "say"

## 1.7.0

- Equip All / @equip-<set> fill empty slots from carried gear when a set is empty or its items are missing
- Loose gear is level / class / alignment checked before it's worn
- Duplicate-named pieces are rejected; fingers and wrists take two distinct items each

## 1.6.0

- Help-menu websites now editable under Settings → Toolbar + Shortcuts
- Per-row add / remove / rename / reorder controls, with Reset to default
- MajorMUD Facebook Group added to the default link set
- BBS website field moved out of Settings → BBS into the same editor
- Per-BBS toggle to show or hide the BBS site in the Help menu

## 1.5.11

- Party-wide toll gate checkbox removed, now always on
- Navigation engine verifies the party's cash before using a toll en-route

## 1.5.10

- Party-wide toll affordability gate for path planning (Settings → Other toggle)
- Navigator routes around a `(Toll: N)` exit any party member can't afford
- Wealth demand-polled via `@wealth` only while a candidate route crosses a toll

## 1.5.9

- Passive room redisplay during a pending move no longer misread as a refusal
- Running loop no longer derails into "lost" from a combat re-print of the current room

## 1.5.8

- Walk-to no longer stalls "walking but not moving" after crossing into a new area
- Engine's echoed move matched by a consume-once claim, independent of map-re-root timing

## 1.5.7

- Walk-to routes around a `(Toll: N)` exit it can't afford instead of stalling on the refusal
- Reports when every route is blocked by a level or toll requirement

## 1.5.6

- Party healer rescues a dropped ally — holds movement, aids, heals by name, re-invites
- Handles a dropped leader whose disconnect already wiped the party

## 1.5.5

- Background engines fall silent while you're mortally wounded (emergency hangup still fires)
- Self-drop clears stale party / following state; rejoin requires a real re-invite

## 1.5.4

- Party-buff duration + recast timing now logged on the always-on Info channel

## 1.5.3

- Death-floor estimate now sharpens from any HP reading survived below the current floor

## 1.5.2

- Miracle-save death ("due to a miracle, you have been saved") now captured by death recovery

## 1.5.1

- Emergency low-HP hangup no longer auto-reconnects straight back into the danger it fled

## 1.5.0

- Session Stats currency reads as coin denominations, not a raw copper count
- Exp/hr + kills/hr graphs carry the current rate as a header label
- Rate graphs plot a session-lifetime average that matches the table stat
- Navigation top-bar path label shows step progress + lap number while looping
- Main-window looping chip trimmed to state, lap, and XP/hr
- Loop lap counter now advances past lap 1
- Multi-class ability quests show each class's own required unlock level

## 1.4.10

- "Hang up if below" HP threshold now accepts negatives down to the death floor (#107)
- Works in both Percentage and Value mode; 0 is a live trigger, not a disable

## 1.4.9

- Auto-caster no longer double-casts the same self-heal in one round
- Self-buff no longer double-casts and drains kai below the spell's cost
- Health tab HP / MA percentage-vs-value choice now honoured by the spell engine

## 1.4.8

- Navigation recovery no longer crashes the client on a terminal route failure
- Standing still in an ambiguous area no longer loses your map marker on its own
- `go path` through a same-name area no longer gets the tracker lost

## 1.4.7

- Session Stats currency now captures real looted coins, not just synthetic fixtures

## 1.4.6

- Session Stats panels share one width; rate graphs widened and made taller
- Player Statistics rows re-columned so numbers align with the other sections

## 1.4.5

- Per-room "Can't reach" flag drops dev / orphan rooms from position resolution
- Position recalls correctly after a restart deep in a same-named area
- A loop's "Run" walks to the entry and starts running in a single click
- A refused loop step auto-recovers and continues instead of dropping to idle
- Debug-channel tracing added for replay-recovery and unreachable-room drops

## 1.4.4

- Follow-up attack fires for a party member who shows a family name
- Login no longer walks the tracker off the real room via the `E` entry command
- Saving a running loop no longer falsely prompts "you changed the loop"
- Navigation chip no longer claims "looping and moving" while stopped

## 1.4.3

- Per-BBS "Player dies at (HP)" setting (Settings → BBS → Realm mechanics, seeded -25)
- Death floor auto-refines from observed slow deaths (toggle, default on)
- Emergency low-HP hangup now fires through the whole bleeding-out window to the floor
- A death halts every movement engine and holds you in the graveyard until you resume
- Leading + a member dies: their phantom `[Invited]` slot is cleared so the loop continues

## 1.4.1

- Navigation top bar shows why movement is holding (Moving / Fighting / Waiting / Paused)
- An inbound party `@wait` now holds our movement, releasing on `@ok` or the leader timer
- Your own held / entangled status now shows on the chip

## 1.4.0

- Mystic party member with kai `[K:N%]` no longer dropped + re-added each `par` poll
- Drained caster's mana/kai bar drops to 0 instead of freezing at its last reading
- `@health` no longer fires the instant an invite goes out, before the invitee joins
- Auto-walker no longer stalls after crossing a text exit (`go path`, `go manhole`)
- Loop-path overlay survives closing and reopening the Navigation window mid-loop
- Session Stats added to the terminal right-click menu
- Settings "Spells" tab renamed "Spells + Ailments"

## 1.3.0

- Attack spells skip a target that resists their element ≥ 100%
- `GAME_MECHANICS.md`: elemental resist recorded as signed (negative = vulnerability)
- Game Data browser shows undead monsters stored as `255` correctly

## 1.2.3

- `GAME_MECHANICS.md`: damage-type resistance split into elemental / Magic Resist / poison
- Spell-targeting monster-type taxonomy recorded (living / undead / animal tags)

## 1.2.2

- `GAME_MECHANICS.md`: three attack-spell no-damage modes documented (level gate / targeting / resist)
- Weapon-swap message corrected to the single "You are now holding X." line
- Attack-spell binary immunity vs percentage resistance recorded

## 1.2.0

- Enabled backstab loadout arms itself in the auto-walker's pre-move sequence
- Equipment Manager is now the sole actuator for gear, diffing against the live worn set
- Bug report reads the live worn weapon / off-hand instead of a stale shadow

## 1.1.1

- Bug report stamps app version + Debug / Combat diagnostics state
- New Party and Live-engine-state sections (roster, `@join` nag, weapon-swap shadow)
- Movement section reports suspect-strike count + last observed exit sets
- Scrollback lines timestamped for alignment against wire I/O

## 1.1.0

- Redundant hidden-exit search after a manual `sea` already uncovered the exit
- `@poisoned` / `@blind` / `@confused` / `@diseased` / `@held` sync no longer bounce "invalid command"
- Toggling an ignore-ailment setting mid-poison now releases the standing `@wait`
- Self-targeted party heals no longer include the family name
- Backscroll window no longer freezes opening a ~10k-line transcript
- `@join` nag no longer cancelled by an unrelated automated telepath
- Combat no longer re-equips an already-worn weapon on the first round

## 1.0.0

- Initial release — faithful CP437 / VT100 Telnet client for MajorMUD
- MegaMUD-style automation: combat, party, navigation, healing, spells, workshop, scripting
- Game-data import, 4-tier settings hierarchy, modeless dockable windows
