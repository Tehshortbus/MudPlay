# Version history

Notable changes per merged PR, **newest first**. The top of the [README](README.md) mirrors the most recent entry. Versioning follows semver (post-1.0), by change type: **MAJOR** = whole-program refactor, **MINOR** = a new feature or enhancement, **PATCH** = bug fixes (one increment per report handled).

## 1.82.0

- Navigation map button bar shows "Current room" and "Selected Room" map/room readouts side by side between the Legend and Save chips
- Lairs chip is now a three-stage toggle: uniform colour → respawn heat-map → off
- Heat mode colours each lair by its 30-second respawn bucket — 30s red, stepping through the spectrum to 5min purple, longer lairs fading toward black (the game's slowest lair)

## 1.81.0

- Terminal font picker now lists every monospace font installed on the system, below the two bundled faces
- Proportional fonts filtered out (Latin advance-width probe), so a picked font can't mangle the fixed cell grid
- System fonts persist as their bare family name; a system copy of a bundled face is de-duplicated from the list
- Font catalogue is pre-built off the UI thread at startup, so opening Settings no longer stalls on the font scan

## 1.80.0

- Path-item auto-obtain simplified to one per-item toggle — the separate buy / drop-source / party-provision sub-checkboxes are gone, folded into a single "auto-obtain for path"
- Party path-item provisioning now acquires a per-person quota (enough for every member), not just one, redistributing from members who already carry spares
- Path-item shop router withdraws from the bank before buying when cash on hand is short but the bank covers it
- Route picker gains a "send it" card to walk a gated route without acquiring; a sole item/ticket-gated route now surfaces in the picker instead of silently aborting
- Desert/drown hazard buff now also re-raised reactively: the game's own lapse prompt (the desert "you suffer in the heat... you need water, soon!") fires one `use waterskin` when the predictive timer drifted and the buff dropped early
- A lapse prompt with no swig confirmation — out of charges/waterskins — halts the walk instead of marching deeper into a hazard it can no longer counter
- Lapse-damage spell is derived from the checkspell chain (desert spell 712), so the re-raise keys on the active set's message record, not hardcoded realm text
- Bug report's room-hazard line now shows the derived lapse spell (whether the reactive re-raise can arm)

## 1.79.0

- Hazard rooms countered by a `use`-cast buff (desert heat, drowning) now raise the buff mid-walk — `use`s the source item on approach, re-`use`ing when its duration lapses so a long crossing spends the fewest charges
- A route blocked only by a survivable hazard is now offered in the route picker (with a "buy at <shop>" tail when the counter is buyable) instead of aborting with "a room hazard you can't survive"
- Route picker also previews a "dropped by <monster>" tail when a gate item no shop sells is flagged to source from a reachable monster drop
- Bug report shows the current room's checkspell hazard, its buff-source item, and whether one is carried
- bug reports addressed: stock-20260719-020228

## 1.78.0

- Route picker no longer walks on click — clicking a route selects it and previews its line on the map
- A Go button (bottom of the picker) walks the selected route; disabled until one is chosen
- Cancel / X closes without walking and clears the preview

## 1.77.0

- Location recovery rebuilt: when genuinely lost it reverse-walks the exact steps since the last known room while growing a multi-room footprint, matched against the map until a single room survives, then re-confirms there and reroutes
- Lit rooms are look-swept in place first — peeking every exit to fingerprint the neighbours breaks name-ambiguous twins (e.g. Darkwood Forest) without taking a step
- Dark rooms skip the useless look-sweep and dead-reckon position from the moves that actually executed
- Recovery clears the room of hostiles before look-sweeping (lit) / waits out a combat tick before dead-reckoning (dark)

## 1.76.5

- Navigation recovery now trusts a Confirmed room tracker: a loop/walk mismatch in a name-ambiguous area (e.g. Darkwood Forest) re-anchors to the known room and reroutes instead of a doomed backtrack that popped a false "Lost" dialog
- bug reports addressed: stock-20260718-155138

## 1.76.4

- A poisoned party member (the `P` flag in par) no longer gets silently demoted to midrank — a force-frontranked leader now keeps Frontrank while poisoned
- bug reports addressed: stock-20260718-145855, stock-20260718-150350

## 1.76.3

- Party window now shows your OWN poison / blindness / disease chip, not just other members' (matches par + "You feel ill.")
- A member who joins after you missed the "started to follow you" line no longer stays stuck "Invited" — a joined par row clears the invite
- Fixes a below-threshold party member never being auto-healed when they were wrongly still flagged invited
- bug reports addressed: stock-20260718-140246, stock-20260718-141002, stock-20260718-141109

## 1.76.0

- Equipment Manager gains a "Projected AC" line above the item-only Armour Class row — item AC folded with race/class innate bonuses, completed-quest rewards, configured AC self-buff spells, and the shadow property (+10, once)
- Prot-Evil rides its own "vs evil" line (1 AC/point, evil-only); VileWard noted as present in the hover tooltip (magnitude scales with the wearer's evil)
- Spell Book no longer lists weapon combat procs (%Spell) or on-kill gear (CastOnKill%) as command-cast spell sources — only genuine "on use" cast items appear
- Spell Book cast items now show the cast spell's effect inline (e.g. "(AC +10)") and render unlimited-use items as "Unlimited" instead of "-1 uses"
- Terminal right-click menu gains "Open Party" and "Open Spell Book" quick-opens

## 1.75.0

- Auto-train master toggle now on the toolbar, Action menu, and hotkey-assignable — mirrors the Settings → Auto-Trainer "Auto-train" checkbox
- Toggling it off from the toolbar/menu also clears the "Auto-train CP" cascade; the CP plan and per-trainer list stay in the settings tab
- Typing several commands separated by `;` (or `^M`) in the terminal or conversation window now sends each as its own line — same multi-step split as macros
- "Hop timing" toggle moved from Settings → Other to the Program Log window, next to "Auto-collect logs"
- Hop-timing log line now shows the carry-weight encumbrance the workshop records — weight, percent, and bracket (e.g. `240/2880 Light [8%]`)
- Navigation window's collapsible sections now start collapsed on open

## 1.74.0

- Monster Matchup gains an attack-type dropdown — Attack / Bash / Smash plus the Mystic strikes (Punch / Kick / Jumpkick), filtered to what the class can do, driving hit% / damage / swings / DPS
- Monster Matchup player-side values no longer snap back to your live gear/stats — they seed from equipment on profile load and on the Reset buttons, and otherwise stay wherever you set them
- Monster Matchup expander now starts collapsed
- Item Finder numeric columns sort highest-first on the first click (positives before negatives)

## 1.73.0

- Item Finder weapon-type filter gains an "(All weapons)" option — show every weapon, hide armour
- Item Finder slot filter gains an "(All slots)" option — show every non-weapon item, hide weapons
- Hit-magic now reads blank on armour/jewellery rows; the stat only matters on weapons

## 1.72.0

- Navigation can now reach a destination inside a random-teleport maze (e.g. the Warped Asylum), where every room shares a name so normal tracking gives up
- The maze is detected structurally — a one-way cast mouth whose interior random-teleports on every step — with no hardcoded room numbers
- After each teleport the walker relocalizes by peeking neighbours with `look <dir>` and matching a unique exit signature, then routes to the goal, re-teleporting ("reshuffling") when the goal is only reachable through another teleport
- Runs on every realm — on stock the look-sweep is the only tool, while on Paradigm the solver relocalizes with `rm` (an authoritative position query whose room numbers stay distinct even though every asylum room shares a name) and never looks at all: every teleport landing and every plain step re-locates by `rm`, which also pinpoints the dead-end Padded Cells the look-sweep can't disambiguate
- Paradigm's asylum pull-lever escape is treated as a one-way pocket dimension so the maze detects and routes there the same as on stock
- On stock, after each teleport the solver forces a `look` to read the landing's exits — in brief mode (the default) a room shows only its name on entry, so relocalization was keying off the room just left and desyncing at the entrance
- On Paradigm the solver waits out the teleport's own room redisplay before sending a single `rm`, and advances only on the authoritative `Location:` reply — never on a same-second move-confirm — so move+`rm` pairs no longer pile up and desync the walker into non-existent exits; a dropped reply is re-sent rather than falling back to a look
- The solver now drives the final plain route to the goal itself (ungated, like a reshuffle step) instead of handing off to the walker, so it no longer stalls on a stuck combat gate mid-maze
- Arrival at a dead-end goal room (e.g. the old man's padded cell, whose signature can't be uniquely matched) is recognized by room name on stock, or directly by `rm` on Paradigm, so the solver stops there instead of blind-reshuffling back out
- When a landing has several reshuffle exits, the solver now picks the one whose teleport spell is likeliest to land somewhere useful — each cast exit fires a different spell with a different landing pool, so it favours the pool with the most rooms it can both relocalize in and route to the goal from, instead of walking the first exit into a dead-end pool and spiralling
- bug reports addressed: paradigm-20260717-094620, paradigm-20260717-094702, paradigm-20260717-100919, paradigm-20260717-100956, paradigm-20260717-102748, paradigm-20260717-103010, paradigm-20260717-111518, paradigm-20260717-111721, paradigm-20260717-115451, paradigm-20260717-150827, paradigm-20260717-151121, paradigm-20260717-152718

## 1.71.5

- Navigation now reaches Morukai from the overworld tree base for both invited and un-invited characters: the quest-gated `go portal` is crossed as a last-resort "gateway" and the walker re-plans from wherever the cast lands (the fixed chamber when invited, the Caves of Chaos when not)
- Routing inside the Morukai cluster no longer loops down through the random portal — a deterministic path is always preferred and the gateway is taken only when no cardinal route to the goal exists
- bug reports addressed: paradigm-20260717-062940, paradigm-20260717-063059, paradigm-20260717-063236, paradigm-20260717-070404, paradigm-20260717-073104

## 1.71.0

- Navigation now recognizes a "guard door" — a pick/bash-proof door opened only by asking a stationed monster the right password (e.g. the grove shadow guard's `ask guard morukai` raising the west gate to Morukai's chamber) — and routes across it via ask-then-move instead of discarding the route
- Guard doors are gated on an untrackable quest ability, so the walker issues the ask and reacts to whether the door actually opens; every greet topic that opens the same door is offered as an alternative command

## 1.70.0

- Auto-get now re-surveys the room after a kill whose monster could drop an item you auto-collect, so a ground drop is picked up instead of left behind
- Auto-get never grabs a ground item that would exceed your carrying capacity; the "Cash" tab is renamed "Cash + Items" and adds optional Light/Medium/Heavy item weight gates, separate from the coin gates
- Encumbrance-bracket math shared between the coin and item collect engines

## 1.69.0

- Trainer room detail now lists the per-level training cost across the trainer's whole level band, priced at that trainer's own markup
- Workshop level-projection table's train-cost column now shows raw copper without thousands separators (pastes straight into the game); the exp columns stay comma-grouped
- Settings → Other adds a "Hide items when discarding" toggle — auto-discard then offloads each excess flagged item with `hide <item>` instead of `drop <item>`, and these engine hides stay out of the Transaction ledger (manual and stash-room hides still record)
- Game Data Rooms filter accepts a `map,room` coordinate (`1,1`) — comma, slash, or space all jump straight to that one room
- Item detail's bought/sold shops are now clickable — each jumps the Game Data browser to the host room's Rooms-tab record
- Item detail surfaces two more acquisition paths: `Found in` lists the chests an item drops from (with per-open odds), and `Given by` lists the monsters/rooms that hand it over via a textblock award — turn-in, purchase, or quest reward — each a clickable jump to that record
- Character Info tab moves Quest Bonuses beneath the attack accuracy/damage box, freeing the right column for the full inventory readout
- Quest Status cards now show the completion experience a quest awards on its own reward line (guide-only — it doesn't feed the Character Info bonuses)
- Weapon-flap fix: a combat-entry gear-set trigger now defers the weapon/off-hand to the combat engine while it holds a per-monster alternate-weapon override, so the Default set can't re-wear the normal weapon over the swap mid-fight
- Fallback-death fix: a kill with no per-monster death line (exp + `*Combat Off*`) is now attributed to the current target and dropped from the room roster — the survivor is re-engaged at once, ending the re-swing at the corpse and the post-kill idle stall
- `@stop` now stacks a pause on top of combat exactly like the Pause button — a route paused mid-fight stays paused after the fight clears instead of walking on (and `@rego` lifts only that user pause)
- Search-bar walk-to now rebounds to auto-following the player once the browse window lapses, matching how a pan-drag rebounds
- Crossing an up/down no longer rebuilds/refocuses the map while you're panning or numpad-browsing — the re-root defers until browsing ends
- Picking a new walk-to destination while manually paused now lifts the pause and walks there, instead of changing the destination but staying frozen
- Walker now disarms a known-trapped exit directly instead of searching it first — the exit hint already proved the trap, so the confirming `search` is skipped
- A between-round buff/heal cast that lands after the death→re-observe already re-swung now resumes the weapon on its `*Combat Off*` instead of idling a full round
- A monster that walks in under a name the game data doesn't recognize (a colour-stripped arrival like "dragon serpent") is now auto-attacked instead of stopping the walker on a mob it never engages
- Renaming the currently-running loop via Save-current now updates the navigation header at once, instead of holding the old (often loop-builder-generated) name until the next lap
- Quest seed: Phoenix Feather guide reordered (`ask morukai orfeo` moved up to follow `ask orfeo morukai`) and the missing `ask morukai return` step added before `use potion`
- Crawled quest guides (those with no hand-written seed) now auto-draft in the seed's own style: step rooms render as clickable `(map/room)` links, the player command is backtick-wrapped, a monster-sourced grant reads `kill <monster> (<drop>)` and a bare grant `obtain <item>`, and the noisy `flag(order)` prefix is dropped
- A crawled kill step now links to the room the quest places its target in (the room's NPC field), falling back to the monster's summon room — or, when it's summoned by another NPC, that summoner's room
- A crawled quest's pure flag-advance steps (an alignment ladder's automatic value ticks, story textblocks the player never directly triggers) are now dropped from the auto-draft instead of listed as an opaque "Step 31" — the guide shows only the followable actions
- A crawled dialogue step now recovers the `ask <npc> <keyword>` that reaches it — walking the textblock dispatch chain up to the NPC whose keyword branches into the step — and links that NPC's room, so a multi-NPC quest (Mandos etc.) drafts its full ask-by-ask flow instead of one bare line
- A crawled step's prerequisite / turn-in item now trails `, from <source>` naming where to get it — the chest that drops it, the NPC that hands it over (with a room link), or the room CMD reward — and a required item the step also turns in is listed once, not twice
- bug reports addressed: paradigm-20260716-095547, paradigm-20260716-095716, paradigm-20260716-101002, paradigm-20260716-123358, paradigm-20260716-124255, paradigm-20260716-124409

## 1.67.7

- A weapon's magic-hit level now sums both magic abilities (Magical + HitMagic), matching the character sheet — an inherently magical weapon (a "shimmering" longsword carrying only the Magical ability) is no longer misread as un-magical
- Fixes the walker stalling "un-actionable" against a monster its magical weapon could actually hit, and the spurious auto-swap to an alternate weapon
- Door-key possession check now strips the count prefix on key-ring entries ("2 black serpent key"), so a key held in multiples is recognized as carried instead of triggering a spurious floor "get" before "use"
- bug reports addressed: paradigm-20260715-235300, paradigm-20260715-222258

## 1.67.5

- Root-cause fix for the stuck "Fighting" chip: after a fallback death empties a room, an empty room re-displays with no "Also here:" line so the classifier fired no observation — the combat gate hung forever, re-displaying the empty room on a loop
- The idle-stall watchdog now auto-recovers in a single step: once the gate has been held ~6s with no combat activity it sends one resync probe and force-clears the stuck gate in the same beat (no manual Reset States needed)
- Watchdog moved onto the 1s heartbeat instead of the coarse 5s combat tick, cutting total auto-recovery from ~10-15s down to ~6s
- Optimistic clear self-heals: if a monster actually lingered, its re-displayed "Also here:" re-asserts the gate a beat later
- Reset States still force-clears combat state too, as a manual escape hatch
- bug reports addressed: paradigm-20260716-011443

## 1.67.4

- A "look &lt;player&gt;" no longer arms room-peek suppression — only a "look &lt;direction&gt;" does, since only that renders an adjacent room
- Fixes the post-teleport movement stall: after a party-splitting "go hole", the trap-delegation race-probe (`look <member>` on re-join) was eating the walker's next-step room confirmation, freezing the walk until a manual room re-display
- Party-splitting-teleport reform now suppresses the trap race-probe look entirely — no member looks during that evolution
- Reform adds a fixed 2s settle then a single room re-display, a backstop that reforms a member who teleported in ahead of us and whose arrival we never witnessed
- bug reports addressed: paradigm-20260716-005420

## 1.67.3

- Kills detected only by the fallback path (exp + *Combat Off*, used when the monster's death line isn't in the active dataset) now force an immediate room re-display instead of stalling ~5s for the next combat tick
- Fixes the post-kill freeze and the wasted first swing at an already-dead mob before the surviving monster is engaged
- The "par polling delays re-attack" symptom was the same ~5s stall coinciding with the 5s party-poll cadence — resolved by the above; the party poller is unchanged
- bug reports addressed: paradigm-20260716-003144, paradigm-20260716-003531, paradigm-20260715-223821

## 1.67.0

- Outgoing telepath chip now reads "TELE→" (arrow trailing) so it mirrors the incoming "←TELE" and the two directions are distinguishable at a glance
- Realm-event chip and filter are now a red "SERVER" chip / "Server" checkbox, matching Paradigm's server PvP notices

## 1.66.9

- A flee-if-below trigger of 0 now disables fleeing on that pool instead of firing at 0 — a caster with "run if below mana" set to 0 no longer bolts off the loop path the moment mana bottoms out, which had relocated the character and then failed the lap to Idle
- bug reports addressed: paradigm-20260715-183717

## 1.66.8

- Idle-stall watchdog re-checks a quietly-cleared room after 6s instead of 12s — when combat-end goes unrecognized (room cleared, no further combat lines), the walker forces the resync re-display a round sooner, wasting 1 round instead of 2

## 1.66.7

- Loop no longer fires a phantom attack at an already-cleared room after a heal — a kill's *Combat Off* landing the same round as a between-round cast (mihe) is no longer misread as the cast's interrupt, so the resume can't re-attack a corpse from a roster the kill's re-display hasn't cleared yet
- bug reports addressed: paradigm-20260715-181944

## 1.66.6

- Loop no longer moves a room or two then fails out to Idle — a kill's forced room re-display and the gate-resume no longer both advance the same step (which had sent the next move from the stale room and failed the lap)
- bug reports addressed: paradigm-20260715-174119

## 1.66.5

- Party @wait arriving as combat ends no longer leaks the loop's next move past the wait — the walker holds formation
- Combat gate no longer hangs the walker "fighting" an empty room after a final kill that skipped the room refresh; a watchdog forces a re-display to resync
- Party heals skip a re-invited member that hasn't reported vitals yet, so a relogged ally no longer draws spam-heals at a phantom 0% HP
- Between-round cast (e.g. mihe) whose resync dropped the target now re-attacks the same round instead of idling one
- bug reports addressed: paradigm-20260715-162125, paradigm-20260715-162423, paradigm-20260715-162916, paradigm-20260715-163553, paradigm-20260715-163947

## 1.66.0

- Cast-teleport pocket areas (the Warped Asylum and its kin) no longer overdraw the map that houses them — the one-way cast entrance shows as a spell-wall bar on the cell divider plus a directional arrow, and the pocket lays out in full only when you're standing inside it
- Cast-on-walk exits (a spell fires as you move) are marked with a short perpendicular wall glyph in the spell colour, drawn between the two rooms
- Navigation no longer routes through random-teleport exits — their landing is unpredictable, so the walker prefers a deterministic route and only crosses cast exits with a fixed destination
- Trap disarm now advances past a successful search when the walker triggered it — direction matching normalizes both the game's reply and the walker's long-form direction, so a found trap ("You found a trap to the southeast!") no longer stalls in search and never disarms
- Trap-disarm capability is now inferred from the character's race and class via game data — when the Traps value hasn't been captured yet (freshly loaded profile, or a new character), a class/race that grants the Traps skill still lets the walker self-disarm instead of walking through the trapped exit
- Bug report captures the parsed Traps stat and whether disarm capability was inferred from class/race, alongside canDisarm
- bug reports addressed: paradigm-20260715-131801, paradigm-20260715-132150

## 1.65.0

- Auto-combat re-issues its last attack when a swing fumbles under confusion, so a confused character keeps fighting instead of silently losing attacks until manually re-sent
- Guard-aware retargeting: when a monster is shielded by guards ("<guard> moves to protect <target>"), combat re-attacks the intended priority as each guard falls instead of stalling once the last guard dies
- Conversation and transaction history reload from the persisted session logs on reconnect, so prior-session chat and ledger entries reappear instead of starting empty
- Up/down searchable hidden exits now retry and reveal correctly — the vertical search-miss line ("nothing different above/below you") is recognized like the cardinal form, so up/down searches no longer stall the walker
- Backscroll search / Find Next now walks newest → oldest (bottom to top), matching the window's orientation, instead of oldest → newest
- `walk to` now routes through single-destination CMD teleports (`go hole` / cast-teleport hops) — modeled as routable map edges, level-gated like any exit, and drawn as a gap into the portal room then out its far side rather than a line across the hop
- Blocked-route message now names the item(s) you're missing when every route depends on one you don't carry, instead of a bare "a required item you're missing"
- Navigation window header reorganized — engine badge / activity chip / status text share the top row with the search box pinned to its top-right corner, and the display-toggle + action chips drop to their own row
- "Collect after combat finished" now defers currency the same as items — ground / corpse / notice cash is queued while the room still holds hostiles and collected on room-clear, instead of picking up between kills
- Party-splitting teleports that fully disband the party (`go hole`) now reform on the far side — the deferred re-invite survives the disband and fires on each member's plain "walks in from nowhere" arrival, so the walker holds for the reform instead of leaving without the party
- Navigation rail's Loops + Auto-Lairs folders now start collapsed instead of expanded — the compact rail opens tidy each time, and any folder you expand stays open across refreshes
- Collect-after-combat re-surveys with a bare `look` when another player is seen grabbing deferred ground cash — the stale per-pile counts are refreshed before the post-combat flush, so it collects what's actually there instead of firing rejected gets ("You don't see 7 gold crown here.")
- Party-split teleport (`go hole`) reform now waits for a member's through-the-hole "from nowhere" arrival before re-inviting — a cardinal follow-in the staging room no longer fires the invite early ("You don't see <name> here."), so the group reforms on the far side
- The `train stats` / character-creation form now blanket-blocks background automation — while the form owns the keyboard, a single engine-send hold silences every engine (par HP poll, @health nag, the @heal-driven poll, combat, casting, auto-get, chat replies) so nothing can leak into the form's first field (Family Name / last name); only the user's manual input and the auto-trainer's own CP allocation reach the form. Fixes a stray `par\r` overwriting the character's last name on realms whose cursor-positioned stat box never shows the "Point Cost Chart" marker
- bug reports addressed: paradigm-20260714-093614, paradigm-20260714-115526, paradigm-20260714-121106, paradigm-20260714-163356, paradigm-20260714-164946, paradigm-20260714-231638, paradigm-20260715-002959, paradigm-20260715-092858

## 1.64.0

- Party-wealth probe now logs each member's reply as it arrives — the interpreted copper value (or "wealth unknown") alongside the verbatim reply — so a program-log read confirms every member's response was parsed correctly, not just the final replied/known tally

## 1.63.0

- Bug reports now capture the navigation engines in a dedicated section — the point-to-point walk engine (live target, step progress, next direction, and the last stop/failure reason), the door / hidden-exit / trap obstacle handlers mid-request, and the path-item shop/hunt detour state with outstanding route-item needs

## 1.62.0

- Transaction history now records where each offload happened — a bank deposit notes which bank (room name + map/room), and a stash notes which room hid the loot, shown as a muted second line under the entry and appended to the persisted transactions log

## 1.61.0

- New **Reset States** action (Action menu, terminal right-click, and a bindable/toolbar-promotable shortcut) — clears my own stuck ailments, party-wait signals, and the movement holds they drive, returning me to an idle, unafflicted state
- Fixes a phantom "waiting — confused" nav pause: a confusion wear-off now clears every effect that shares the generic "You are confused!" line, so a monster confusion carrying its own specific wear-off no longer strands the flag (and the nav hold) active
- bug reports addressed: paradigm-20260714-101922

## 1.60.0

- Transaction history now records manual bank deposits and stashes, not just the app's automated ones — a hand-typed `dep`, `hide <coin>`, or `hide <item>` shows up in the ledger like any auto action, sourced from the server's own confirmation echo
- Each deposited denomination and each hidden coin/item lands as its own chronological ledger row
- Log pane gains an "Auto-collect logs" checkbox (default off) — the program, memory, and combat-trace files are only written to Data/Logs while it's on, so a normal session leaves nothing on disk; persisted per-character
- Conversation window now opens scrolled flush to the newest message instead of stopping short of the bottom
- A multi-word monster with no flavour prefix (e.g. a lair boss) is classified off the Monsters table instead of landing as Unknown in the room roster
- A party member on a different client whose @wealth reply lacks our copper tally is now understood — coin phrases like "26 platinum pieces, 4792 gold crowns" fold to a copper value for the toll-gate check
- A lever-raised gate that renders as "gate" rather than "door" (e.g. "open gate north") now registers its live open/closed state, so the walker skips the door FSM on an already-raised gate instead of stalling
- A guardroom tooltip now names the gate its lever controls (e.g. "pull lever → Inner Gate (1/1331) north exit"), so a remote lever room no longer looks inert
- bug reports addressed: paradigm-20260714-085920, paradigm-20260714-090507, paradigm-20260714-091000, paradigm-20260714-091244

## 1.59.0

- Lever-opened doors are now walkable: a plain/locked door that a lever in another room lifts (annotated with action cells) is promoted to a lever exit, so the walker detours to pull the levers instead of routing around or bonking the closed door
- A hidden exit whose unlock action needs a held item (e.g. "hold up amber talisman") is now treated as an item gate — the walker routes around it or plans to fetch the item when it isn't in hand, and the room tooltip names the required item
- Our own confusion now pauses navigation locally: a confused leader or solo player holds their walk / loop / auto-lair (and lights the self chip) until it clears — the leader/solo analogue of the @wait a confused follower telepaths; honours the Ignore Confusion setting
- A knockdown now pauses navigation instead of hammering the server: while held ("flat on your back") the walker holds and resumes on "You get back on your feet.", and the flat-on-your-back refusal is recognised so an in-flight move can't strand the tracker
- Long chat messages that wrap across terminal lines are stitched back into one logical line, so the Conversation window captures the whole message instead of just the first row
- Corrected Chancellor Annora's quest-step room in the seed data (1/3333 → 1/1333) so the alignment/quest walkthroughs point at her real location
- bug reports addressed: paradigm-20260713-233737, paradigm-20260714-002413, paradigm-20260714-001001

## 1.58.0

- A key-locked door is now recognised as passable when the required key is on your key ring (not just loose in your pack) — the walker no longer falls back to the pick-only alternative and false-blocks a route you hold the key for
- A blocked walk names the actual obstacle — a locked door, a missing item, a level window, a toll, a class hall, or a room hazard — instead of always reporting "level, toll, or class"
- A toll no longer routes the party around it just because a follower's wealth is unread: unknown followers are @wealth-probed and the toll blocks only when someone is confirmed short
- bug reports addressed: paradigm-20260713-223929

## 1.57.0

- Auto-buff is now suppressed in rooms whose cast-on-enter spell strips buffs (RemovesSpell / DispellMagic) — no more burning mana re-casting a blessing the room tears straight back off every tick (e.g. the Crypt's "negate magic" halls)
- Party window tags the self row by the parsed in-game character name instead of the profile label, so a profile named differently from the character no longer spawns a phantom party entry or whispers yourself
- Auto-lair recognises a room clear and advances to the next lair instead of stalling after the first — a self-supersede stop was misread as an external move and re-armed the same walk ~1×/sec
- A door opened by levers/actions in other rooms is now pulled at the right time: the walk detours through the action rooms first (anchored at the approach room nearest them) before checking the door, instead of walking to the closed door first and wasting the trip
- Follower @wait/@ok no longer flap — @ok is held until both HP and MA reach the full rest ceiling, decoupled from the movement floor that releases at trigger+1
- A directed say ("Name says (to you) …") is now captured in the Conversation window's say channel (and a directed @-command still routes) instead of being dropped
- @reset from an active party member is accepted without an AlterSettings grant — it's a party-rhythm coordination signal, not a settings change
- A mid-send socket drop (e.g. the party poller ticking after a disconnect) no longer crashes the app with an unobserved task exception
- Navigation rail reserves a bottom buffer so the last loop / Auto-Lair row can't read as cut off under the Manage footer when scrolled
- bug reports addressed: paradigm-20260713-105825, paradigm-20260713-173953, paradigm-20260713-195552, paradigm-20260713-220904, paradigm-20260713-222201, paradigm-20260713-222618, paradigm-20260713-225011

## 1.56.0

- Clicking a saved GOTO favourite now stages it as the queued destination (map pans, route preview draws, Run arms) instead of immediately walking there — hit Run to go or the X to cancel, same as picking a room from the search box
- Staging a favourite no longer stops a running loop / auto-lair on its own; that only happens when you commit with Run
- All three user walk-to paths — map right-click, search box, favourites — now run through the same engine: committing a search-box or favourite destination with Run offers the free-vs-shortcut route picker when a shorter gated route exists, just like the map right-click already did
- When a shortcut needs a carry/ticket item the walk will auto-buy, the route picker now names the shop it will detour to (e.g. "a raft (buy at General Store)")
- Loop circuits now search-and-reveal a hidden exit mid-lap instead of failing out when a leg crosses one
- A monster that breaks off and flees on its own ("scuttles out to the west!") now clears the fighting chip and combat gate, like a dragged-out mob already did
- Stop now wipes any auto-lair markers off the map (was only cleared by re-toggling lair mode)
- A keyed door whose key is lying on the room floor is now grabbed (`get <key>`) before the `use`, instead of blindly trying to use a key not in inventory
- bug reports addressed: paradigm-20260713-174151, paradigm-20260713-193205, paradigm-20260713-174024, paradigm-20260713-195905

## 1.55.0

- Game-data catalogues (Messages, Monster Messages) now reload in one shot — a set switch rebuilds each subscriber's index once instead of once per record (~1100× at startup), so startup and set switches settle faster
- Map layout cache is now bounded to 32 most-recent origins (LRU), so a realm-touring session can't grow it without limit
- Memory log gained committed / gen2-size / LOH-size / LOH-frag / POH columns so a future capture can tell a managed-heap leak apart from GC working-set ratcheting

## 1.54.0

- Conversation window and Transaction history now persist to rolling per-character logs under Data/Logs (`<char>.<bbs>.talk.log` / `.transactions.log`), surviving restarts and the in-memory line cap
- Clear chatlog and the Transaction-history Clear button also wipe their log file
- Settings → Talk: Log conversations / Log transactions toggles and a shared line-limit picker (default 2000)
- Removed the Conversation window's Export chatlog menu item — the always-on log replaces it
- Settings → Talk: Conversation window font and size pickers, with the current row font/size tagged `{default}`
- Settings → Talk: per-channel accent and message-text colour overrides for the seven Conversation channels, picked with a visual colour picker (no hex code needed), with per-slot Reset to the theme default
- Selecting a recently-used profile no longer strands the File menu flyout at the window's old position — the profile load (and its window reposition) is deferred until the menu closes
- CP earn math no longer over-pays at decade tops (level 10 counted 15 CP instead of 10, level 20 counted 20 instead of 15) — the allocation plan can no longer offer a stat point the level's CP can't actually afford
- Auto-train now applies the CP plan on Paradigm's cursor-drawn stat box — the replay fires off the `train stats` command signal instead of the marker row that never scrolls there
- A train run whose trainer screen never opens keeps the CP plan rows instead of clearing them
- Auto-cast (bless / heal / cure) is held while the train-stats screen owns the keyboard, so a spell can't type its letters into the character-name field
- bug reports addressed: paradigm-20260713-104450

## 1.53.0

- Memory footprint is now sampled once a minute to its own Data/Logs/{ts}-memory.log (working set, private, managed heap, GC heap, fragmentation, collection counts) — kept out of the program log
- Session Stats per-hour rates (kills, exp, currency) now measure over a rolling window capped at 4 hours, so an all-night loop reports its recent pace instead of the whole night blended — the kill/exp histories are trimmed to that window so they no longer grow unbounded
- Party window disposes its view-model on close, releasing its subscriptions to the app-lifetime party state

## 1.52.0

- Navigation loop / Auto-Lair and GOTO favourite rows hug the rail edge — reclaimed the tree's fixed left chevron gutter
- Nav loop / Auto-Lair Run buttons no longer sit under the overlay scrollbar (right inset added to the trees)
- Character Info encumbrance shows the carry-load percent beside the bracket word; the label is shortened to "Enc"
- Punch / Kick / Jumpkick combat rows show only for classes that innately grant the strike (Mystic), not any character with a trained Martial Arts skill
- Navigation no longer stalls on "The door was not locked." mid-breach — the door is taken as unlocked and opened regardless of which verb (bash / pick / use-key) was in flight
- Spell Book cast-on-use items show each item's level requirement and are ordered by it, lowest first
- Bless-slot dropdown now lists the class's unlimited-use cast-on-use items (as `#item` tokens showing the cast spell, level, and mana), gated to items usable at your level — pick one to auto-schedule its `use` buff

## 1.51.0

- Item Finder surfaces more per-item stats — attribute (+STR/INT/…), min & max damage, spell-damage, resists, and skills (stealth / picklocks / traps / …), plus carry weight and light — as filterable columns
- Level Projection tab adds a per-level "Train (copper)" column showing the cheapest eligible trainer's fee to reach each level
- Character sheet shows the encumbrance bracket word (None / Light / Medium / Heavy / Encumbered) beside the carry weight
- Equipment Manager's bonus panel refreshes the instant an item is picked from a slot's dropdown, not only on blur
- Backscroll window ends at the live screen — on open it appends the current on-screen rows after the scrolled-off history
- A room monster with an unrecorded flavor prefix ("vicious kobold") is now recognized via the Monsters table so auto-combat engages it; the log flags the missing prefix and its double-click opens the monster's record
- Auto-train on Paradigm (level-less "train to the next level" wording) now applies the CP allocation plan and trains stats instead of resuming early
- A door that shuts in your path ("The door to the <dir> just closed.") reverts the pending move so the next attempt routes through door handling instead of bonking the closed door
- Player workshop drops the duplicate coins/wealth block from the bottom Inventory box (already shown under character stats)
- Currency get/drop commands name the coin in full (silver noble, gold crown, copper farthing, platinum piece, runic coin) so a bare "drop 1 silver" can't ditch a like-named item instead of the coins
- bug reports addressed: paradigm-20260713-025755, paradigm-20260713-033207

## 1.50.0

- Health "Run if below" mana threshold now triggers a flee — out-of-mana casters run, auto-resuming only once both HP and mana recover
- Turning auto-combat off mid-fight sends a "break" before releasing the walker, when "Break combat if running" is checked
- Custom board disconnect line now logs under the conversation window's realm category, not just the party roster
- Navigation loop / auto-lair scrollbar no longer covers the per-row Run button

## 1.49.0

- Item Finder gains an Attack-type picker (Attack / Bash / Smash / Punch / Kick / Jumpkick); the Swings column recomputes per type — Bash halves, Smash locks to one — and the martial-arts strikes add a bare-handed attack row
- Item Finder Slot dropdown drops its redundant Weapon entry (the Weapon-type filter already isolates weapons) and now sits below Armour type
- Item Finder hides worn-but-limited-use items (lights, potions, containers, signs, keys) that only matched a slot by coincidence — only real armour and weapons remain
- Item Finder wrist / finger slot labels drop the "(1)" position tag that carried no meaning there
- Equipment Bonuses' Hit Magic now reflects only weapon-granted hit magic, matching its per-item contribution list

## 1.48.0

- Game Data monster records now show each dropped item as a clickable chip that jumps to the item's record in the Items tab
- Settings → General gains a terminal font-family + font-size picker (per-character); MX437 and size 16 are marked {default}
- Terminal font size relocated from the per-BBS Display tab to the per-character General tab, so the font choice follows the character
- Default item seed curated: only a hand-picked list auto-collects (with per-item caps) or auto-discards — every other item is left unmarked
- Chests/containers and Leo's steel key auto-collect by default; junk gems (azurite, agate, moonstone, …) auto-discard
- Auto-collect honours each item's Max-to-get cap, counting key-ring keys, instead of grabbing every copy in a room
- Existing cannot-be-taken and loyal-item flags preserved; stale auto-buy / auto-sell / auto-stash defaults cleared
- Dead "Auto-find" checkbox removed from the item editor
- A door that shuts mid-combat no longer traps the walker/loop bonking a "closed door" — the refusal now re-opens it
- bug reports addressed: paradigm-20260712-234614, paradigm-20260713-000204

## 1.45.1

- Renaming a BBS now moves its whole folder — nested character profiles, saved logon-nav steps, and passwords survive instead of being wiped and recreated empty
- The rename re-keys each character's per-BBS credentials, so logon-menu nav and password lookup keep working under the new name
- Recent-profiles list and the "import logon steps from another character" picker now follow the renamed BBS instead of showing the vanished old name
- bug reports addressed: paradigm-20260712-231015

## 1.45.0

- Conversation window logs paradigm's server PvP announcements (any "Server PvP Message: …" line, e.g. "X just killed Y!") as a red SERVER entry under the Realm filter; realm-gated so only paradigm realms surface it
- CURRENT NAV's walking action line now reads "Walking to (map/room) - Name on step X of Y, remaining Z" instead of just the destination
- Main status bar's walk-to readout no longer trims the destination — the room-name slot sizes to its content so `C/D/Steps` always fits

## 1.44.0

- Auto-light equips a carried light one room ahead — stepping toward a room the map knows is dark lights it before the move, so it renders on arrival instead of a blind step or two later
- One-room lookahead only, so a light's burn timer isn't spent early; the reactive can't-see path still covers unmapped rooms
- Main status bar shows a walk-to readout while travelling — `C: map/room  D: map/room  Steps: <remaining> - <exp/hr>`; a loop keeps this readout while approaching its start and only switches to the lap counter once it begins cycling
- CURRENT NAV lists the walk-to steps and the loop's own steps together while approaching, then collapses to just the loop steps once the walk-to finishes
- CURRENT NAV's description line moved up next to the "Navigation" title as a plain-English action line — "Walking to (map/room) - Name then looping <loop>" while approaching, "Looping <loop> - step X of Y on lap Z" while cycling
- A monster dragged out by fleeing players ("<name> exits the room to …") now clears the fight — combat state, the fighting chip, and the paused walker all resume instead of hanging while the client swings at empty air
- bug reports addressed: paradigm-20260712-211917, paradigm-20260712-220516

## 1.43.0

- Session Stats abbreviates cash denominations in the compact total / per-hour / stashed cells — platinum→plat, silver→silv, copper→copp; the itemised tooltip keeps the full words
- `lo <dir>` / `loo <dir>` are now recognised as look-direction peeks (like `l` / `look`), so glancing into an adjacent room no longer walks the tracker onto the peeked room
- bug reports addressed: paradigm-20260712-202202

## 1.42.0

- Settings → Toolbar + Shortcuts now lists the File-menu actions (New / Open / Save / Save As / Quit) so their keybinds are editable
- Keybind-only rows show no icon and can't be added to the toolbar — only actions with a toolbar button can be promoted
- Bug report captures every built-in keybinding, flagging any that differ from the default
- Auto-deposit no longer bails out at the bank: a mid-walk route re-plan on the way there stopped aborting the reroute, so it deposits and returns to the loop as intended
- bug reports addressed: paradigm-20260712-185119

## 1.41.1

- Faster loop step-off after a cleared room — the loot settle window drops from 600 ms to 400 ms
- Navigation routes around a door the character can't pick or bash (a Bandit Keep front door needs far more strength than any build can reach), so a loop approach takes a traversable alternate entrance instead of walking into a door it can only bonk on
- bug reports addressed: paradigm-20260712-172326

## 1.41.0

- On Paradigm, a suspected position mismatch now asks the game where you are (`rm`) and re-anchors to the authoritative `Location: map,room` instead of dropping straight to the heuristic backtrack / "Lost" dialog
- The navigation engine pauses during the `rm` round-trip so the reply reports a stationary room, then re-plans from the confirmed position
- Heuristic backtrack recovery stays the fallback — used when the realm isn't Paradigm, the reply times out, or the reported room isn't in the map graph
- Bug report captures the resync state (awaiting-rm flag, request in flight, last resolved room)
- Auto-deposit fires again after a reroute torn down by an external stop — the guard re-arms instead of staying latched and looping past the deposit threshold forever
- Manually stopping a loop cancels any in-flight auto-deposit reroute, so a freshly built loop isn't yanked back toward the old route
- Carried wealth drops immediately after an auto-deposit, so a following toll gate isn't attempted on a stale pre-deposit balance
- Navigation toolbar's resume button now enables whenever the engine is paused, matching the Run entry in the navigation menu
- Shorter settle wait after a room is cleared and its loot collected, tightening the pause before the loop steps to the next room
- A fizzled self-buff no longer counts as active for its full duration — the recast timer clears on the failure so the buff re-attempts each round and holds near-100% uptime
- Auto-light torch-shop detour no longer tears itself down when it supersedes the in-progress walk — it reaches the shop, buys, and resumes the route instead of re-detouring forever
- A hand-typed `rm` on Paradigm now re-anchors the position tracker to the reported room, not only an engine-requested resync
- bug reports addressed: paradigm-20260712-154401, paradigm-20260712-155542, paradigm-20260712-155734, paradigm-20260712-160302, paradigm-20260712-160504, paradigm-20260712-162342, paradigm-20260712-164535, paradigm-20260712-165407, paradigm-20260712-170203

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
