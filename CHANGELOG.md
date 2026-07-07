# Version history

Notable changes per merged PR, **newest first**. The top of the [README](README.md) mirrors the most recent entry. Versioning follows semver (post-1.0), by change type: **MAJOR** = whole-program refactor, **MINOR** = a new feature or enhancement, **PATCH** = bug fixes (one increment per report handled).

## 1.11.8

- Looking into an exit no longer fires get / equip / attack against the peeked room — automation waits until you actually walk in
- Auto-combat now engages on a real walk-in that follows a look-direction peek
- Equip-all wears stacked / doubled-up gear instead of stopping after the first item
- Auto-combat-off mid-round releases the walker and clears the in-combat gate so movement resumes
- Hand-casting a spell mid-fight re-attacks a still-alive target immediately instead of idling until the next round
- Rest-if-below now actually sends `rest` when it triggers
- Learned spells persist across sessions — Spell Book checkmarks survive a relog instead of blanking until the next `spells` / `pow` poll
- Spell Book cast-on-use list shows only the class's own items, not every universal wand / scroll
- Backscroll copy survives a broken DBus clipboard instead of crashing the client

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
