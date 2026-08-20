# Version history

Notable changes per merged PR, **newest first**. The top of the [README](README.md) mirrors the most recent entry. Versioning follows semver (post-1.0), by change type: **MAJOR** = whole-program refactor, **MINOR** = a brand-new feature or a large (~1000+ line) rewrite/expansion of an existing one, **PATCH** = bug fixes AND ordinary enhancements to existing features (one increment per bug report handled or per enhancement).

## 3.22.21

- Fixed max HP/mana drifting stale when equipping/removing gear that grants a flat pool bonus (e.g. severed head of Goru-Nezar's +50 mana) — the health engine's rest and "pool is full" checks now track the change immediately instead of waiting on a manual stat-screen check

## 3.22.20

- Item-cast buffs (e.g. `#emerald-tipped crozier`) now free a `Worn`-bucketed off-hand blocker (a charm/skull the game's `i` text doesn't label `Off-Hand`) before wielding a two-handed cast item, and re-equip it after — previously the whole equip/use/restore sequence silently failed every recast
- bug reports addressed: paradigm-20260819-234712

## 3.22.19

- Navigation: the search-box dropdown now **groups results by source** — saved GOTO locations first, then boss-table targets, then rooms — instead of interleaving them by relevance
- Navigation: **door bashing is now rest-aware and uncapped** — a bashable door is bashed until it opens (no fixed attempt cap), pausing to rest to your rest-max whenever HP dips to the rest trigger; the old "Max bash attempts" setting is removed (picking keeps its cap)
- Combat: the **"clear hostiles when sneak broken by see-hidden monster"** toggle now force-clears the room with **Auto-Combat on or off** (previously combat-off only)

## 3.22.16

- Remote: `@exp` now reads `4,500,000 EXP to level, making 1.1m/hr ~4h 10m to level.` — it leads with the **exp still needed to level**, abbreviates the rate (exact under 100k, `853k` in the hundred-thousands, `1.1m`/`30m` in the millions), and keeps the time-to-level
- Time-to-level formatting (the `@exp` reply and the status-bar TNL) now shows **plain minutes under 90m** (`89m`, not `1h 29m`) and **rolls up to days past 25h** (`1d 1h 0m`)

## 3.22.15

- Bug report: new **Room combat assessment** section — the engine's live engageability verdict for every monster in the current room (Magical level, spell-immunity, each weapon's hit-magic, and the **CanAct / StuckOnMana / Unkillable** call with its reason), so a "won't attack this monster" report is self-diagnosing without combat logging being on
- Bug report: new **Spell resolution** section — every configured combat/heal/cure/bless slot resolved to its **Spell number, name, learned flag, ReqLevel, EnergyCost, and mana cost**, so a mis-cast or "spell looks blocked" report shows the bad value at a glance
- Bug report: new **Monster overrides** section — per-monster attack command / attack spell / pre-attack spell (with counts), relationship, priority, and flags you've customized, tagged with the tier each comes from
- Bug report: new **Item overrides** section — per-item loot-automation flags (collect / discard / buy / sell / stash / keep-minimum, etc.) you've customized, tagged with the owning tier

## 3.22.11

- Remote: `@have <item>` now reports the **true carried quantity** — a stack of 25 counts as 25, not 1 (it was counting inventory *entries*, and a stack is one entry) — and the reply reads `yes - Nx 'item'` instead of `yes - Nx matching 'item'`

## 3.22.10

- Combat: fixed the **attack-spell level lookup clobbered by duplicate short cast-codes** — a monster/item-triggered spell sharing a player spell's short command (e.g. `disr`) could silently overwrite its ReqLevel with an unrelated one, making the real spell look blocked by a monster's spell immunity when it would actually land; the player's own learnable spell now wins the lookup over any same-code duplicate
- bug reports addressed: paradigm-20260819-195419

## 3.22.9

- Combat: attack-spell **MaxCasts are now counted once per real combat round** (off the round timer) instead of per damage-line tick — so a spell no longer switches to its alternate before it fires, or blows past its cast cap (the recurring miscount reports)
- Combat: the cap-switch to the alternate attack now fires **exactly once** — the deferred switch is idempotent, so an alternate like magic-missile can't double-fire after the primary caps or after a between-round buff
- Healing: the **major heal now takes precedence over the minor heal below its threshold** (self and party) — minor no longer keeps firing while you fall through the major band; if the major heal is unaffordable it falls back to the configured minor, and both respect the HP triggers + the "heal if above" mana floors
- Navigation: the **pass-through stash now hides your coins in the actual stash room**, not the next one — the stash fires before the loop steps out
- Navigation: **auto-collect is suppressed in a stash room while looping** — a search there no longer re-exposes and re-grabs the pile you just stashed
- bug reports addressed: paradigm-20260819-120938, paradigm-20260819-121003, paradigm-20260819-121247, paradigm-20260819-121516, paradigm-20260819-142147, paradigm-20260819-054200

## 3.22.3

- Navigation: the **Room Info** panel now mirrors the map tooltip — monsters are shown in three labelled groups, **Placed** / **Assigned** / **Lair**, instead of one flat tagged list, with the lair's **Max Regen** line beneath the Lair group

## 3.22.2

- Game Data: an item that sits on a room's floor now shows a **"Placed in"** section on its record — a clickable link to each room that holds it, with **Queue Walking here →**; room-only items (like the bogwood box) previously showed no source at all
- Navigation: the Room Info panel **and the map hover tooltip** now list a room's statically-**placed** floor items, not just ones its `roomitem` command scatters — so an item sitting in a room (like the bogwood box at 14/10415) shows up when you hover / go there
- Navigation: the room tooltip / Room Info panel now split monsters into **Placed** (a boss / NPC fixture), **Assigned** (roams / rarely spawns there), and **Lair** (a consistent lair spawner) instead of one lumped "Also Here" line — a monster can appear under more than one, showing the distinction at a glance; the lair's **Max Regen** now sits directly beneath the Lair line

## 3.22.0

- Navigation: the walker now routes through two special exits it couldn't before — an **item-use teleport** (an item whose use transports you, e.g. `use potion of levitation` to reach the far side) and a **room-command reveal** (a hidden exit opened by typing a room command, e.g. `clear rubble`) — so quest routes gated on them, like the trek to the Necromancer at 9/1431, now plan and walk on their own instead of failing with "No path"
- Navigation: a route blocked **only** because you lack a required action-exit item now fails with a named **"a required item to go obtain (…)"** message that names the item, instead of a bare "No path"; these quest items are never auto-fetched
- Navigation: fixed a latent case where an item-gated command teleport (an emblem / chime that ports you past a locked door) dropped its inventory check — it no longer routes you through as if you held the item
- bug reports addressed: paradigm-20260818-061428

## 3.21.11

- Reconnect: after an unexpected drop, first profile load, or a cleanup relog, the client now **enters the realm even if your logon steps don't cleanly reach the game** — the realm-entry command fires the moment the entry menu appears, so a mis-authored or holdover nav step no longer strands you at the menu (it still won't auto-enter right after a deliberate `@hangup` / hang-up-on-low-HP / when-naked, by design)
- BBS settings: the logon-steps editor now says to add **only log-in steps, never a log-out/quit step** — a MegaMUD-holdover "log off? (Y/N)" step never matches on login and stalls the sequence
- bug reports addressed: paradigm-20260818-142340

## 3.21.10

- Combat: when you hand-attack a **passive neutral** (one the engine normally leaves alone), the auto-combat engine now **takes over and keeps killing it** — hitting a neutral turns it hostile, so the engine treats that instance like an enemy until it dies instead of stopping the moment you engaged it; the walker also holds in the room until it's dead
- bug reports addressed: paradigm-20260818-081214

## 3.21.9

- Navigation: position recovery in same-named mazes now replays your move chain from the room the chain **started** at (not the newest confirmed room), so after fast manual movement outruns the tracker it re-derives where you are instead of aborting the replay and going Lost

## 3.21.8

- Combat: debuff casting reworked — the AoE debuff now casts **once** and "tags" the mobs present, re-firing (up to its per-room cap) only when a **new** mob enters/is summoned rather than every round; and the single-target debuff now correctly takes over whenever the AoE isn't covering the room (Auto-Nuke off, unconfigured, or the room below its minimum-enemy count), fixing a case where a configured AoE debuff with Auto-Nuke off left the single-target debuff never firing
- Recovery: auto-rest no longer gets stuck off after a fight ends in an empty room — a post-combat "wait for the room to re-confirm" hold could sit forever when the room stays empty and you don't move (an empty room never re-announces its contents), leaving you below your HP/mana rest threshold yet never resting; the hold now releases on a short timeout once nothing has re-asserted a hostile
- Combat: a room/AoE attack spell now drops to single-target the same round the room thins below its minimum-enemy count, even when a lone survivor keeps the fight going — previously it kept re-casting the AoE at the last mob (which the server auto-repeats) until a `*Combat Off*` landed or you pressed Enter, because the room roster only re-parsed on the trailing Off
- Navigation: Auto-Search no longer stalls the walker with a per-room settle pause when nothing is set to collect what it reveals — with Auto-Get Items / Auto-Get Cash off and no path-item hunt active, it releases the walker the moment the `sea` goes out instead of idling ~⅓ second in every room, so travelling with Auto-Search on is markedly faster
- Combat: a capped attack spell no longer fires an extra round against a lone monster — the engine's cap-switch to the alternate was landing one round late when a solo fight's first round arrived quickly (under ~4s), so e.g. LBOL set to cast **1** fired twice before switching to MMIS; the per-round tally now counts a solo fight's first round instead of mistaking it for a multi-mob premature tick
- Combat: a hand-cast attack/drain spell that draws "no effect" (e.g. probing an immune elemental with `dtch`) no longer marks the engine's **own** last auto-cast spell immune — a manual probe used to wrongly drop the auto-attack cascade to melee instead of trying the next attack spell
- Combat: a hand-cast enemy debuff (a monster-targeting spell such as `vuln`) no longer arms a phantom self-buff recast timer or shows up as a bogus self-buff in the Buff Watchdog
- Game Data: an item's "bought / sold" list now shows **every** room a shop operates from — a shop that runs from several rooms (e.g. the silverbark canoe's Boat Launch, at both Arlysia City Docks and the Pier) previously surfaced only its first room
- bug reports addressed: paradigm-20260818-055820, paradigm-20260818-055955, paradigm-20260818-080337, paradigm-20260818-060742, paradigm-20260818-052120, paradigm-20260818-050950, paradigm-20260818-092532, paradigm-20260817-205819

## 3.21.0

- Navigation: the walker now **solves nested action-gated exits** — a lever whose alcove is itself behind another action-gated door — by opening each inner door first (walk in, pull, return) before crossing, so routes through multi-level lever vaults (e.g. Paradigm's 6/861 tomb → 6/924) complete on their own; fully generic off game data, no per-area code (Asylum + Pyramid stay the only bespoke solvers)
- Navigation: only a very deep (4+ level) or cyclic nest, or a genuinely unroutable one, still fails — now cleanly at plan time ("route needs an action-gated exit the walker can't auto-solve") instead of the old misleading "not supported on loop circuits" block
- Navigation: the walker and loop engines log their remote-action detour and special-exit dispatch decisions on the debug side, so a blocked or nested walk is diagnosable from the program log
- Navigation: the blue walk-to route line (and its "steps to arrive" ETA) now draws the **full planned path** including go-act-return lever detours, instead of collapsing each out-and-back into a straight line that redrew segment-by-segment as the walker looped out and back
- bug reports addressed: paradigm-20260817-233052, paradigm-20260818-000725

## 3.20.10

- Navigation: the walker no longer plans a route through a hidden, action-gated exit it can't actually open — some game-data exports annotate a fork/lever passage's opener on only one side (e.g. Paradigm 9/1050's West exit), leaving the reciprocal exit with no action data. The router now routes **around** such an un-openable exit (or fails cleanly with "no route") instead of sending a doomed move that the server refuses and stranding the walker
- bug reports addressed: paradigm-20260817-125209

## 3.20.9

- Combat: the mana-regen roll-spell **reroll** now runs through the same between-round priority pass as every other 0-energy cast — it competes by your Settings → Spells priority (a due heal/cure wins) and spends the one-cast-per-round slot, instead of firing straight to the wire and bypassing both
- Spell records now list **Negated by** — the items that cancel a spell while carried (the inverse of the item side's *Negates*), shown in the Game Data Browser Spells record and the Room Info room-spell dialog
- Spell records: the **Summons / Casts / Casted By / Negated by** (plus Learned From, Requires/Avoided carrying) record references are now clickable blue links that open the monster / spell / item record
- Remote commands: an @-word matching no command is now ignored silently — a stray "@because" in gang chat no longer draws an "invalid command" reply; the warn-on-invalid setting now governs only recognized-but-denied commands
- Combat: single-target debuff spells now actually fire — they were wrongly gated by Auto-Nuke (single-target debuff is now gated by **Auto-Combat**, the AoE debuff by **Auto-Nuke**); the debuff-cast path was also brought up to parity with the attack cascade — a corpse-cast guard (won't debuff a just-killed target) and the one-between-round-cast-per-round slot (a pre-attack debuff no longer draws a second cast → "already cast this round")
- Combat: a debuff slot now rejects a mis-configured spell — it must be a 0-energy between-round spell with slot-appropriate targeting (single-target vs AoE); enforced at cast time (program-log warning) and flagged live in Settings → Combat
- Reconnect: richer login-automation diagnostics — the log names each step matched and the prompt the next step awaits, and when the automator stalls (e.g. a carrier-lost relog whose final menu differs from a fresh login) names the exact step + prompt that never arrived, so a capture shows why auto-entry didn't fire
- bug reports addressed: paradigm-20260817-135739

## 3.20.3

- Auto-rest: after poison wears off, the client now goes back to resting when still below the rest-HP floor — a rest sent while poisoned never took (poison blocks it) and left a stale "resting" latch that wrongly suppressed the re-rest, so the character stood there regenerating slowly instead of resting
- Combat: an AoE that wipes a room in one round (several exp lines at once — e.g. a hand-cast fireball) no longer corpse-casts a single-target spell at a "survivor" the kills hadn't cleared yet; the engine now re-parses the room with a carriage return and re-picks from what's actually left
- Spell book: learned spells now survive a game-data set swap — the obtained set is keyed by spell name instead of the set's row numbers, so switching sets (which renumbers rows) no longer blanks every configured spell to "unlearned" until you re-poll `spells` or reload the profile; the Buff Watchdog's learned flags stay correct across a swap
- bug reports addressed: paradigm-20260817-092945, paradigm-20260817-105650, paradigm-20260817-114209

## 3.20.0

- New **Buff Watchdog** window (View menu, after Party): lists every buff you have configured — self-bless slots, HP/MA-regen, when-full, `#item`-cast, and party-bless slots — each with a live timer bar and a marker showing where its recast window opens, so you can see at a glance which buffs are up, which are due, and which aren't up at all
- Combat buffing: the between-round spell coordinator (heals / buffs / debuffs / item buffs) now casts at most ONE per combat round, matching the game's one-between-round-cast-per-round rule — fixes a self-buff (e.g. mageshield) re-casting every round during a fight and spamming "already cast this round"
- Combat buffing: a buff's recast timer is now cleared only by its OWN wear-off line — spells that merely share an "applied" message (the five "you feel protected" shields) can no longer clear each other's timers
- Combat buffing: program logging for the buff manager — timer armed (duration + recast-in), confirmed active, worn off, and dropped when a cast didn't fire — so a log read explains what it's doing and why
- Combat buffing: buff recast timers now measure the buff's REAL remaining seconds (server spell rounds run slightly long, ~3.04s not 3.0s), so a "recast within N seconds" slot fires at N seconds left instead of ~1-2 seconds early
- Buff tracking: a manually-cast buff is now caught — armed by the 4-letter cast code you typed (like an engine cast), so the Buff Watchdog and recast engine track hand-casts
- Buff tracking: Paradigm's `stat` "You feel X! (Ns)" status readout is ignored — it no longer gets mistaken for a fresh cast (which falsely marked buffs active on login and then suppressed the real cast's confirm)
- Buff tracking: any disconnect now freezes the buff timers (display included) and reconnecting resumes them with the same remaining, instead of clearing and recasting from full; switching characters or a gap longer than the buff could last starts fresh with no buffs assumed
- Buff Watchdog: taller, full-width timer bars with a larger label — a light-green outline over a flat darker-green fill (name left-aligned inside, remaining time after it); the fill and recast marker stretch to the bar's real width so the outline bounds exactly the actual bar (no dead space at the ends)
- Party buffs: the party-buff slots are cast only while you're actually in a party — solo, none of them fire (your self-buff slots still do)
- Party buffs: a party-wide buff that supersedes a self-buff (RemovesSpell — e.g. chant removes bless) now suppresses that self-cast while in a party, and the Buff Watchdog shows the self-buff "covered by" the party buff instead of a timer
- Game Data Browser: a message claimed by a spell in the set no longer appears on the Messages tab (you edit it by double-clicking the spell) — no more seeing the same record listed twice; an orphan-linked message whose spell isn't in the set stays listed
- Messages seed: pruned inert MegaMUD carry-over entries the engine never reads — every `monster entry` and `trap disarm` record (monster arrivals are recognized directly off the wire) and the standalone "you are shockshielded" shockshield record (the spell-linked shockshield is kept); further pruning tracked in #349
- bug reports addressed: paradigm-20260816-101702, paradigm-20260816-222917, paradigm-20260816-232454

## 3.19.4

- Navigation: the Warped Asylum teleport-maze solver now stops the per-room position-checking once it lands in a solvable room — the initial landing still relocalizes to confirm the room, but from there the unhindered route to the goal is driven straight through with no `look`-sweep (stock) or `rm` (Paradigm) spam per step

## 3.19.3

- Combat: **room-aware monster identification** — an observed monster name is now resolved against the monsters the current room actually places or summons here (its NPC + lair members + Summoned-By spawns + the monsters those summon) before any global name match, so a homonym pins to the variant in THIS room and per-monster overrides (spell overrides, relationship, Kill-on-sight) land on the right record
- Combat: the program log now traces each room occupant at **Combat** severity — the detection (name → resolved record number), its relationship + Kill-on-sight, and the engage/skip decision — so a log read explains exactly why the engine did (or didn't) attack something
- Combat: a friendly / neutral NPC whose name the classifier couldn't pin to its monster record (e.g. a greet-only "old man" quest-giver) is no longer attacked on sight — such records also resolve to their Monsters-table row by name so their Relationship / Kill-on-sight setting is honoured, and the engagement gate never proactively attacks a monster it can't identify (fixes a regression from the v3.15.0 neutral kill-on-sight work)

## 3.19.0

- Navigation: new **ROOM INFO** rail section — left-click any room on the map to list clickable links to everything attached to it: the room itself (its name links to the room record), each monster (lair / summoned / placed), each floor item, the shop, and the cast-on-enter room spell. A monster or room-spell link opens its full record dialog, the shop opens the room-detail popup (its stock menu), and the room / item links open the record in the Game Data Browser. Left-clicking never forces the panel open — expand it when you like and it shows the last room you clicked
- New room→floor-item index (`roomitem` placements) backing the floor-item links; monster and spell records can now be opened by number from anywhere (shared MonsterRecordDialogService / SpellRecordDialogService)
- Game Data Browser: double-clicking a **Shops** row opens the room-detail popup for the shop's room directly (was: a hop to the Rooms tab); a shop that spans several rooms opens on the first and lists the others as clickable siblings in the popup's title

## 3.18.18

- Quest guides: a single-quoted `'command'` in a step is now a clickable green link — clicking it types that command at the game exactly as if you'd entered it in the terminal (macro/alias expansion included), alongside the existing clickable `(map/room)` walk links
- Quest eligibility: alignment quests no longer show as doable for the wrong alignment — three Evil / Neutral / Good checkboxes at the top of the Quest Status tab (saved per character, off by default) declare which alignment chain(s) you're on, and alignment-gated quests only appear as available when their box is ticked
- Quest eligibility: the Quest editor's "Restrict to classes" control is a checkable class dropdown for the genuinely class-locked quests the crawler can't detect on its own (Magebane, Tarl)
- Quest journal: quests this character can't complete (wrong class/race/alignment, or a class restriction) are now hidden by default — tick "Show in quest journal" in the editor to keep one visible (saved per character); they never appear in the login availability dump regardless

## 3.18.16

- Startup: the three biggest game-data tables (Rooms / Monsters / Items) are now parsed on a background thread as the app launches, ahead of the auto-loaded profile's game-data set switch — trimming a chunk of the cold-start delay before the client can react to combat on reconnect (bigger sets benefit most)
- Game-data import: long textblock memos (those big enough to span more than one database page) no longer lose their first two characters — the Jet multi-page "long value" memo reader used a header 4 bytes too large, so e.g. a class-restricted quest's `class 1` directive imported as `ass 1` and never resolved to the class name (visible in a monster's Greet keyword effects). **Re-import your MDB(s) to correct already-imported data.**
- Game-data import: re-importing over the currently-active set now re-ingests the fresh tables immediately, instead of showing the old data until you switch to another set and back
- bug reports addressed: paradigm-20260816-075942

## 3.18.13

- Monster record: the Greet textblock now surfaces each keyword the block responds to as a chip right on the tab; click a keyword to fly out the effects it triggers — so a monster with dozens of keywords (e.g. The Grey Lord) stays compact instead of blowing the pane out
- Monster record: long attack names (e.g. "claws you with its plague-ridden claws") now wrap in full instead of being clipped in the Mob's Attacks list
- Monster record: the Name / Relationship / Override fields sit closer to their labels (labels right-aligned, columns tightened) and the whole editable panel is packed tighter vertically — less dead space between labels, headers, and boxes

## 3.18.12

- Item-cast buffing no longer double-sends the re-equip: when a buff item (e.g. a warhorn in the off-hand) is used and the swap breaks a rest, auto-equip now stands off the borrowed slot while the item-cast's own restore runs, instead of also firing a redundant `wear` the game rejects
- Caster no longer fires the alternate attack spell at a mob the capping cast just killed: when a max-casts-1 nuke lands its own killing blow, the cascade switch waits a short beat for the kill's death/exp to register, then re-checks the target is still alive — so `mmis` stops corpse-casting at "You don't see X here!"
- Attack spell with a max-casts of 1 no longer swaps to the alternate before it ever fires in a multi-monster room: the other monsters' swings were tripping the round counter early, so the normal spell got skipped ("LBOL → MMIS without firing LBOL"); the count is now anchored to real rounds
- After a self-buff cast mid-combat coincides with a kill in a multi-mob room, the caster re-attacks the surviving mob immediately instead of standing idle a round until it gets swung at
- bug reports addressed: paradigm-20260815-130733, paradigm-20260815-201731, paradigm-20260815-202241, paradigm-20260815-202319

## 3.18.8

- Spell pickers (Combat tab + Spells tab) now strike through and dim spells your character hasn't learned, and outline a slot red when it points at an unlearned spell — a guard against configuring a spell you can't actually cast (the value is still saved; the red outline is just a warning)
- Bless-slot cast-on-use item entries are flagged the same way, but only when you aren't carrying/wearing the item that provides them
- Recognize ParaMud's "You add <spell> to your spellbook!" so the learned-spell guard updates the instant you learn a spell from a teaching item
- Spell Book (F2): double-click a spell to open the record of the item — or trainer NPC — that teaches it
- Spell Book: the Lvl column now shows the level YOUR class can actually learn each spell — a spell gated higher by its trainer (e.g. a Paladin's divine disfavour at 50, not the spell's base 19) reads correctly and stays hidden until you can learn it
- Spell Book: the selected row's Code/Lvl/Mana cells stay readable instead of washing out on the highlight

## 3.18.5

- Caster combat: the per-round attack-spell cascade switch is deferred past the killing blow's server line burst, so the alternate spell (e.g. `lbol`→`mmis`) or weapon fallback no longer fires at the just-killed mob ("You don't see X here!") and the surviving mob engages cleanly
- Caster combat: MaxCasts now counts real rounds, not damage-line ticks — a multi-hit attack spell + the mob's counter-swing no longer trip the cascade a round early (e.g. `hamm` set to 2 swapping after 1 cast)
- bug reports addressed: paradigm-20260815-120544, paradigm-20260815-120934, paradigm-20260815-135756, paradigm-20260815-130957

## 3.18.1

- Combat diagnostics: the caster-side per-round spell re-announce (the cascade/MaxCasts switch, e.g. `lbol`→`mmis`) now logs the switch with its timing relative to the last attack / exp / death — to pin a corpse-cast where the killing blow's damage line fires the re-announce ahead of the death being processed (caster-side only; physical is passive). Reports paradigm-20260815-135756 / -135853 under investigation

## 3.18.0

- New: quest-availability announcements — `[<quest> Quest is Now Available]` prints to the terminal the moment you train past a quest's minimum level (a multi-level jump announces every quest whose gate you crossed), plus a one-time dump of everything you can now start at login. Only lists quests your class/race can do and that you haven't already completed. Toggle **Announce available quests** at the top of the Player Workshop → Quest Status tab (per character, on by default)

## 3.17.4

- Player Workshop → Bosses: new **Last Killed** column (between Timer and Notes) showing when each boss's timer was last set — by a Mark / Now button, a back-dated Mark, or an auto-detected kill
- Bosses tab now opens sorted by the 100% timer (running timers on top), so a fresh open shows what's active instead of a name-ordered list that looks empty

## 3.17.3

- Fixed: after auto-training + allocating CP, the loop/auto-lair could resume but then stand idle indefinitely — when the trainer-menu exit prompt was missed, the CP-form keyboard hold wasn't released, so the resumed engine's first move was silently dropped (the walker drew the route but never stepped). The CP-replay grace-timeout now forces the keyboard release before resuming
- bug reports addressed: paradigm-20260815-072308, paradigm-20260815-072819

## 3.17.2

- Wire Inspector: thorns/ShockShield reflect lines ("The armour spikes stab <monster> for N damage!") are now labelled **Reflect** instead of "Monster Hit (other)" — recognized generically by colour (a reflect is white, a real incoming hit is red) so it works for any item wording (armour spikes, collar spikes, …)

## 3.17.1

- Fixed: in a room with more than one monster, the engine no longer pauses and fires an attack at the just-killed monster ("Your command had no effect.") before switching to the survivor — the exp-inferred kill now drops the dead mob from the room roster immediately, so the next attack targets a living monster
- bug reports addressed: paradigm-20260815-081045, paradigm-20260815-081201

## 3.17.0

- Monster flavor adjectives (large / nasty / huge / …) are now one **editable per-set vocabulary** instead of per-monster data — new **Flavor Prefixes** section in the Game Data Browser lets a custom realm add or remove the words it uses; the room classifier strips a leading word in that list to resolve "large giant rat" → "giant rat"
- Per-monster `FlavorPrefixes` retired — removed from every monster record + the Monster editor's Flavor Prefixes box; the shared vocabulary fully replaces it (`MonsterMessageRecord` is now just name + link)
- The unknown-entity "add as flavor prefix" action and the missing-flavor log-row double-click now add the word to the active set's vocabulary (both were previously stubs / opened the record)

## 3.16.0

- Per-monster combat message data retired — hit / miss / dodge / armor-block are recognized generically from line colour + wording, and a death from the experience line, so the Monster editor no longer has a Combat Messages section and no one has to hand-enter a monster's messages (crucial for custom games with tens of thousands of monsters)
- Monster deaths are now recognized purely from `You gain N experience.` + `*Combat Off*` (our own targeting names the mob); the per-monster death-line matcher was removed
- `MonsterMessageRecord` shrank to name + flavor-prefix data (the 8 unused hit/miss/dodge/block fields and the death-line field are gone)

## 3.15.2

- Combat engine no longer casts the round's alternate attack spell at a just-killed monster — a kill is now recognized on the `You gain N experience.` line (which arrives before the kill's `*Combat Off*`) instead of waiting for the `*Combat Off*`, so `lbol`→`mmis` no longer fires `mmis` at the corpse ("You don't see X here!"). Generic — works for any monster, no per-monster death message needed
- Room monster names resolve their flavor adjectives (large / nasty / huge / …) from one shared vocabulary, so "large giant rat" is recognized even when the giant-rat record doesn't list "large" — a custom game needs no per-monster prefix data for the standard adjectives (canonical names like "huge basilisk" still match themselves)
- bug reports addressed: paradigm-20260814-230258

## 3.15.0

- Combat engine now recognizes monster hits / misses / dodges / armor-blocks **generically from line colour + wording** (no per-monster data), for both you and party members — surfaced live in the Wire Inspector's new **Classified** view
- Wire Inspector gains **Raw / Stripped / Classified** toggle checkboxes (Raw + Classified on by default); the Classified pane tags each combat line with how the engine read it (e.g. `[Combat: Monster Miss (you)]`) and marks recognized monster deaths with `[Monster Death: <name>]` (an unrecognized death shows as inferred-from-exp)
- Bug reports attach the last 750 lines of the Raw / Classified wire when those panes are on — on by default, so combat-recognition issues arrive with the exact wire without extra steps
- Neutral monsters get a **Kill on sight** checkbox (Monster edit dialog): engage a chosen neutral while leaving other passive neutrals safe to rest among — the engine rests/meditates between kills (only when below the rest trigger) instead of being forced to clear the room
- Rest is no longer sent mid-fight when the idle-stall watchdog optimistically clears combat — it waits for a room re-display to re-confirm the room is empty first
- bug reports addressed: paradigm-20260814-225055

## 3.14.5

- Auto-engine **base modes** (Settings → General) now actually take effect on **profile load** — the live toolbar settles to your base-mode checkboxes each time you load a character, instead of coming up in whatever transient state the last session ended in
- Legacy characters (from before the base/live split) adopt their current live modes as their base on first load, so nothing changes for them until they edit the checkboxes
- bug reports addressed: paradigm-20260814-173438

## 3.14.4

- Auto-All (kill switch) OFF now freezes **every** movement engine — a walk / loop / auto-lair, or a right-click Queue-walk-to, plans but holds until Auto-All is restored (previously a queued walk-to would run with Auto-All off)
- Manually hand-casting a combat spell whose cast-code is shared with other spells (e.g. `vamp` — vampiric touch plus monster vampiric-* variants) is now correctly recognized as your attack for the round, so the engine no longer re-announces its own attack spell over it
- Drain spell now has hysteresis: once it engages a target it keeps draining until HP recovers a margin above the trigger, instead of flip-flopping drain↔normal every round when a heal lands you right at the trigger
- Combat-diagnostics log now traces the drain gate (HP vs trigger/release, target eligibility, mana) each dispatch, for troubleshooting
- Combat tab: the drain trigger label now reads "Heal when ≤ HP"
- Gear-set apply no longer thrashes on paired finger/wrist slots — a set that swaps one ring/bracelet of a pair now frees the odd worn one first so the new one lands on the empty slot instead of trading places forever (a safety guard also halts a set that keeps re-applying)
- bug reports addressed: paradigm-20260814-165045, paradigm-20260814-210019, paradigm-20260814-210613, paradigm-20260814-215046

## 3.14.0

- Conversation window now captures **actions / emotes** (the socials from your board's `action list` — hug / wave / smile / tickle / …): the ones you perform, the ones aimed at you, and the ones you witness in the room
- They show under the **say** chip (room-local, like say) with the message text in **green** — the board's own colour for them
- Detected by colour + shape (own "You <verb>…"; others' only from a **player actually in your room**), so the also-green obvious-exits line and room enter/exit + party-follow movement are never mistaken for an emote
- Combat tab: separators now bracket the Drain spell settings (above and below), setting the group off from the alternate attack spell and the Display section

## 3.13.0

- New Combat-tab **Drain spell** slot: a mage life-steal spell (e.g. vamp / dtch / nebo) that casts in place of the round's attack once HP drops to its trigger %, healing you off the damage, then reverts to the normal pick when HP recovers or mana falls below its floor — an emergency heal that also attacks (per-target Max casts + Min mana, like the other single-target slots)
- Drains only fire on **living, non-undead** targets (a life-drain can't affect NonLiving / Undead); against an ineligible mob the engine falls back to the normal attack cascade, with the game's "no effect" reply as a backstop
- **"Drains override AOE"** toggle: by default the drain yields to the room AoE (multi-attack) when there are enough enemies to be rooming; check it to let the drain pre-empt the AoE too

## 3.12.5

- Startup splash: settled mud now paints as flat background fills instead of per-cell block glyphs, so the whole-lens mud floods (cover / slide / rain / geyser burial) stop throttling the frame rate — the heaviest scenes render far cheaper
- Seagulls scene: the perch wires are drawn as a background strip rather than a full-width row of `━` glyphs, cutting the bulk of that scene's per-frame glyph cost
- New splash scene: a detailed chocobo sprints across a scrolling grassy plain, faceplants into a mud puddle, and gets left behind as the view wipes off
- Mud-geyser scene: the jet now erupts from the top of the mound (narrow at the peak, fanning wider as it climbs) instead of looking rooted at the ground floor
- Goblin-sink scene: the pool now wipes away from the edges inward, closing over where he sank, instead of clearing from the centre out
- Mud-pie scene: the throwing arm now stays in frame and is covered by the growing pie/splat instead of blinking out the instant it releases

## 3.12.0

- Typing a movement command yourself (a direction, or a `go path`-style text exit) while a walk / loop / auto-lair is running now **pauses navigation** — a user pause you resume with Start, so the automation never fights a hand-driven step (`l <dir>` peeks don't count)
- Hand-typing an attack mid-fight — a **combat spell** (any energy-costing attack, vs a 0-energy heal/buff) or a **physical attack** (`a`/`aa`/`bash`/`smash`/`bs`/…) — now counts as a **user override**: the engine holds its own auto-attack for that round instead of re-sending it over yours, and resumes on the next round. A hand-cast heal/buff still lets it resume attacking right away
- Map right-click **Favorites** flyout now lists favourited loops (green, start on click) and auto-lairs (amber), matching the terminal menu — a favourited loop was only showing in the terminal one before
- Fixed a wedge where combat force-clearing via the idle-stall watchdog (a mob wandered off mid-fight with cash/loot on the ground) left the deferred pickup stranded on the Acquisition gate — the walker sat "looting" forever; the force-clear now flushes the deferred cash/item collect
- bug reports addressed: paradigm-20260814-131551, paradigm-20260814-135601, paradigm-20260814-135715

## 3.11.9

- Chest Offload: per-shop **Sell** button relabelled **Sell All**; each item row gets its own **Sell** button (sells the picked quantity) and its **Drop** now drops the *leftover* you're not selling (held − picked)
- Chest Offload: the list now reconciles against the game's own `You sold …` / `You dropped …` confirmations — a row shrinks (and clears at zero) only when the sale/drop actually lands, so a refused sale or blocked drop leaves your per-item pickings and ⇄ shop moves untouched
- Chest Offload: program-log trail through the whole flow (open, coin attribution, sell/drop sent, sold/dropped reconciled) for future diagnosis
- New **Simulate Chest button** toggle in the Program Log window (off by default, session-only) reveals a test button that seeds random containers to exercise the window without real chests

## 3.11.6

- Navigation: moving and then peeking (`l <dir>`) in quick succession no longer loses your spot — the look's preview no longer eats the move's confirming room display, so the tracker stays synced instead of drifting into Suspect
- bug reports addressed: paradigm-20260813-201720

## 3.11.5

- Combat: an attack spell set to **Max casts = 1** now fires exactly once per target before cascading to the next slot — the switch is announced on the round the cap is reached instead of one round late, so the server no longer auto-repeats the capped spell one extra time (VAMP was firing twice)
- Monsters game-data table: every stat threshold filter is now **at least (≥)** — filter for HP ≥ N, AC ≥ N, Damage ≥ N, and so on, so stacking Exp ≥ with HP ≥ narrows the results instead of appearing to do nothing
- bug reports addressed: paradigm-20260814-061340, paradigm-20260814-103219

## 3.11.3

- Help Topics readability: the recurring field labels (**Default:**, **What it does:**, **Important notes:**, …) now render in an accent colour so the field name is scannable at a glance, and the "⚠️ not currently functional" notes render in a tinted callout box instead of blending into the prose
- Split the longest guide paragraphs into shorter blocks at natural boundaries (Chest Offload, the Monsters filter sidebar, navigation obstacles, remote-command permissions, the combat spell cascade, and others) — no content changed, purely for scannability
- Thanks to @AzMarathon for the contribution

## 3.11.2

- Exp/Hr estimator now walks the loop's real room order over a simulated hour with a per-mob respawn clock keyed to each kill, instead of assuming every lair fires once per lap. So a **line (out-and-back) loop** — where you re-cross just-cleared lairs on the return, wasting combat ticks in empty rooms — is estimated correctly lower, matching real yields; a saturated ring still reports its full rate. Fixes loops (e.g. dense diamond-mine lines) reading ~15% high
- The 720-kills/hr tick cap is now treated as a ceiling a loop only reaches if its geometry keeps a mob engaged every tick, not a figure most loops hit
- Clarified the estimator's **Seconds per room** input: it's your effective per-room pace *while looping and fighting* (move + round-trip + attack + tick ≈ 1.2–1.4s even at 1.0 movespeed), not your raw walk speed — setting it to raw movespeed made tight backtracking loops read high
- bug reports addressed: paradigm-20260814-012556

## 3.11.1

- Transaction history: each row has a **Keep** checkbox, and **Clear history** now leaves the checked entries behind (in memory and on disk) — so you can prune a full ledger without losing the rows that matter

## 3.11.0

- Blocked walk-to messages now name the obstacle: which room the door is in, the direction, and what it takes to pass (the key and/or the picklocks/strength) — e.g. "a locked door south from 10/218 (Frozen Cavern) — needs the glass key, or 61 picklocks/strength" — instead of a bare "a locked door you can't open". Requirements are per-direction, so a door's far side (which may differ) is never confused for the way you're heading
- Blocked walk-to reasons are now written to the program log (Info), not just the top-bar chip
- New **"run to the blocked room anyway"** option: when the only route to a destination is fully blocked but you can still reach the obstacle, the route picker offers to walk as far as possible and stop at the block so you can clear it by hand
- Room tooltips show **"any"** for doors anyone can bash or pick (a bare door, or "[any picklocks/strength]"), instead of showing nothing

## 3.10.3

- Bosses tab: sorting by **Boss** or **Respawn** now uses the same active/idle grouping the timer columns do — cleanup spawns first, then bosses with a running timer, then idle ones — ordered by name / respawn length within each group
- Bosses tab: the early-window columns are relabelled **-5% / -10% / -20%** (how far before 100% the boss can spawn), matching how they're described
- Bosses tab: widened the **100%** column so the `(NN%)` progress no longer clips

## 3.10.0

- New **Chest Offload** window (Player Workshop → Bosses → **Chest Offload…**): open the boss chests you're carrying, then vendor the loot shop-by-shop
- On open it snapshots your carried inventory and lists the containers you hold; opening one shows every new item, grouped into the fewest shops with a charm picker
- **Coin gained** is measured per open (coins before vs after that chest, so selling loot never inflates it) and listed by denomination, most-valuable first (runic/plat/gold/silver/copper)
- Per item: an adjustable sell quantity, its sell value, and a **Drop** button; per shop: a running total, a clickable header (with the shop's map/room and the steps to walk there from where you are) that queues a walk there, a **Sell** and a **Drop All** button (sell/drop fire realm-batched commands)
- Per item, a **⇄** button opens a popup to move that item to another shop that buys it — each alternate lists its name, map/room, and current walking distance (nearest first), with **Change** / **Cancel** — for when the assigned shop is an expensive trip
- A **Total to sell** figure sums everything selected across all shops at the current charm
- Shops are ordered into a short nearest-first trip using the real walker routing (avoid rooms, usable teleport gates, item/hazard/boat gates)

## 3.9.16

- Monster table **Biggest Lair** / **Avg Lair Size** now read each spawn room's lair `(Max N)` cap — matching the record's "Spawns In" list — instead of the Lairs table's group-level `Mobs` count, which overstated the biggest lair
- Monster table **Acc** column is blank for spell-only monsters (no physical attack) instead of showing a spell id
- Renamed the **Monster Matchup** calculator to **Hit Calculator**
- Hit Calculator: a **"hits me %"** picker + **"Show me the Monsters"** button opens the Monsters game-data tab filtered to monsters accurate enough to hit you at least that often, given your AC + dodge (the monster table's Acc filter is now "at least")

## 3.9.12

- Navigation GOTO right-click menu: the destructive "Remove from favourites" is now labelled "Delete this Go To", and a new "Add to / Remove from favourites" toggle stars a Go To for the terminal Favorites flyout without deleting it
- Filtering the GOTO or loop lists by "favourite" / "favorite" (or any 3+ character part of the word) now also surfaces your favourited Go Tos and loops

## 3.9.10

- Monsters game-data table: expanded column set for browsing/filtering monster stats — Rgn, Exp (actual earned = base × multiplier), HP, AC/DR, Dodge, MR, Acc (Maj/Mx), Damage, Exp/(Dmg+HP), Lair Exp, # Lairs, Avg Lair Size, Biggest Lair, Mag, Undead
- Dodge and Mag (hitmag level) decoded from monster ability data; Exp/(Dmg+HP) is an exp-per-effort efficiency score
- New filter sidebar, resizable via a drag handle: one threshold per stat (HP ≤, Exp ≥, AC ≤, DR ≤, Dodge ≤, MR ≤, Acc ≤, Damage ≤, Mag ≤, Lair Exp ≥, # Lairs ≥, Rgn ≤), an Undead-only checkbox, and an Alignment dropdown — thousands-grouped values, all stacking with the text filter
- Filters apply on an explicit **Apply filter** button (with **Clear filters** beside it); a ticker's outline shows amber while edited/pending and green once applied

## 3.9.9

- Monster table (Game Data → Monsters): four new columns — **Lair Exp** (per-monster `AvgLairExp`), **# Lairs** (total lair rooms the monster spawns in, summed across its lair groups), **Mobs/Lair** (mob-count range across those groups), and **Script** (`ScriptValue`) — all previously-ignored MDB data

## 3.9.8

- Monster record (Game Data → Monsters): each "Spawns In" room now shows that room's lair size — e.g. `1/2122 (lair: 2)`

## 3.9.7

- Player Workshop → Character Info: equipped, carried, and key-ring items are now clickable links to their Game Data item record — a stacked item's count prefix ("3 piece of amber") is stripped for the lookup; a name the dump truncated stays plain text
- Room-detail popup: the "Also here" monsters now show their game-data record number — e.g. `chest(#69)`

## 3.9.5

- Item record (Game Data → Items): a **Charm** picker (default 50) re-prices the bought/sold buy/sell figures live, so you can compare charm levels (e.g. a higher-charm party member selling) — the "@Ncha" prefix is gone since charm now lives in the picker
- Item record: each bought/sold shop gains a **Queue Walking here →** link that arms a walk to that shop (like typing it in the nav search box); **Dropped by** monsters are now clickable links to their records
- Room-detail popup for a shop gains the same live Charm picker (replaces the static "prices at 50 charm")

## 3.9.2

- Game Data → Monsters → Override Attack: reopening the dialog now shows the cast-code you typed (e.g. "agon") instead of the internal Spell.Number it resolved to
- Override Attack now activates from the spell (number or cast-code) alone — Max is an optional per-room cast cap (blank = unlimited), no longer required to arm the override, which previously left it silently falling back to the global attack spell
- bug reports addressed: paradigm-20260813-131658, paradigm-20260813-132647

## 3.9.0

- Navigation now routes out of sealed rooms via an NPC ask-transport exit — a room whose only egress is asking a resident NPC to port you elsewhere (the Floating Citadel's Grey Lord → Town Square) is no longer a dead-end; the walker sends the `ask <npc> <keyword>` itself. Only ungated transports are used
- Turning auto-combat off mid-fight now resumes movement immediately instead of stalling until a manual room redisplay (a deferred-cash/get-items/search hold was left stranded)
- "Do not rest in this room" no longer suppresses resting for the rest of the loop — the rest gate re-arms when the loop steps out of the flagged room
- Manually casting a spell mid-combat can no longer make the attack spell burst — the resume is rate-limited to once per round for hand-typed casts (engine survival casts are unaffected)
- Navigation room tooltip now lists each monster with its game-data record number (e.g. `Dark Goblin Archer(#48)`)
- Map exit stubs now flag a command-required exit — a `go path`-style named exit — in the magenta "Action required" colour, alongside levers and ask-doors
- Terminal right-click menu adds a "Recent destinations" flyout (the last 10 GOTO targets, click to walk there)
- Navigation map room right-click menu adds Favorites and Recent-destinations walk-to sub-lists at the top, styled to match the terminal flyout (numbered, goto-blue)
- Nav room right-click "Add to favorites" renamed "Save as Go To" (it saves the room to your Go To list)
- Shift+right-click a map room whose only jump is unambiguous (up-only, down-only, or a single teleport) follows it immediately instead of opening the menu
- Spell Book now updates from `sp` as well as the full `spells` — the padding-aligned spell-list column header is matched regardless of its column spacing (a realm whose header was padded differently previously aborted the parse and left the book stale)
- A spell-list parse miss can no longer wipe the Spell Book, and the parse now logs its header + obtained count so a future "spellbook didn't update" report is diagnosable from the log
- bug reports addressed: paradigm-20260813-122226, paradigm-20260813-121114, paradigm-20260813-131020, paradigm-20260813-141450

## 3.8.51

- Auto-train now returns to the loop after training instead of stalling with the route drawn and nothing on the wire (the trainer-menu keyboard hold was still up when the resume step fired, so the move got dropped)
- Engine send-gate now re-drives a walk step that was dropped while the gate was held, as a general backstop against the same class of stall
- Loops/Auto-Lairs tree: expanding a folder now scrolls it into view instead of opening off-screen below the fold
- Selecting a room via the nav search box or loading a Go-to destination no longer slows a running loop (the per-step route preview was recomputing every step during loops/auto-lair)
- Terminal now accepts paste (Ctrl+V / Shift+Insert); multi-line pastes send as sequential commands
- Monster Matchup: changing the selected attack no longer resizes the Character Workshop window
- Status-bar TNL now uses the same banked-aware estimate as Session Stats' "time to next level", so the two no longer de-sync
- Combat diagnostics now log the cast-interrupt resume path (cast armed → *Combat Off* in the resume window → weapon/spell resume fired or why not), to expose mid-fight re-engage stalls
- bug reports addressed: paradigm-20260813-063517, paradigm-20260813-090629, paradigm-20260813-100939, paradigm-20260813-102748, paradigm-20260813-125617

## 3.8.43

- Game Data → Monsters → Override Attack: typing a spell's cast-code (e.g. "turn") now auto-resolves to its Number and lands on the mana-gated, cascading spell rung — same as typing the number directly, instead of silently becoming an ungated raw command
- Combat no longer stalls at 0 mana: a mana-costing action (a spell cascade, or a forced attack-command that's really a spell cast-code) stands down for the physical weapon at 0 mana — the server silently no-ops such casts, so re-sending them just left the player getting hit until a regen tick. A free command verb (bash/kick) still fires, and backstab is preserved; resumes automatically once mana recovers
- Fixed a self-sustaining cast-spam loop where a between-round-cast resume kept re-triggering off its own resulting *Combat Off*, firing dozens of casts from one interrupt — now fires at most once per interrupt
- bug reports addressed: paradigm-20260813-064159, paradigm-20260813-070249, paradigm-20260813-081016

## 3.8.40

- Monster HP lookup now reads the record placed/summoned in your current room, so a display name shared across zones (an orc lieutenant in the barracks vs the slums) shows the right monster's HP instead of the first same-named game-data match
- Per-monster spell overrides now match that same room-aware record, so an override set on one zone's monster (a graveyard zombie) no longer bleeds onto a same-named monster elsewhere (a tunnels zombie)

## 3.8.39

- Filter/search boxes across the app now show a ✕ clear button on the right while they hold text (click to clear)
- Navigation rail right-click: loops and Auto-Lair setups now offer Edit… and Add/Remove from favourites, matching the goto menu (favourited loops/lairs join the terminal Favorites flyout)
- Character Info attack table: new Swings column showing swings/round per attack type (Attack, Bash, Smash, Backstab, Punch/Kick/Jumpkick), computed from your stats + weapon via the same energy-budget model the Calculators tab uses
- Item Finder: new Negates column listing the spells an item cancels while worn, plus a Negates dropdown in the stat filters (populated with every spell any item negates; `(none)` = off) to narrow to items that negate a chosen spell
- Item record: flag-style properties (LoyalItem, Del@Maint, QuestItem, NotSellable, …) now read "Yes" instead of a misleading "0", and the never-drop (Not Droppable) / delete-on-death columns are now surfaced
- Status bar: connection indicator is now just the coloured dot (green/yellow/red), with the state text moved to its hover tooltip — freeing room on the bar
- Status bar: the exp/hr slot now appends "- TNL: <time>" (estimated time to next level at the current rate)
- Monster look: the HP estimate slot is relabelled "TGT HP:" and also prints a yellow "[<name> remaining Hitpoints: <est>]" line to the terminal; a new Settings → Other "Show monster HP lookup" checkbox (default on) gates both
- Auto-get MaxToGet cap no longer overshoots: a stacked pack count ("50 orc-head") is now read at its true quantity after an inventory refresh, so collection stops at the limit instead of topping up past it
- bug reports addressed: paradigm-20260812-235342

## 3.8.30

- Transaction history: the stash tint now fills the whole entry edge-to-edge (consecutive stashes read as one gold band, no gaps), and the timestamp / type / room-location text is brighter for readability
- After the last monster in a room dies, the room re-displays once instead of twice — the item and cash collect engines now share one post-kill re-render
- Fixed a doubled attack command after the first kill in a room: a matched death line no longer lets the same kill's exp/*Combat Off* re-drop and re-attack the freshly-picked next target
- Navigation goto flyout: "Clear destination" now closes the flyout, like picking a recent destination already does
- bug reports addressed: paradigm-20260812-215306, paradigm-20260812-215511

## 3.8.26

- Transaction history: stash entries are tinted faint gold (the map's stash-marker colour) to stand out from bank deposits
- Double-clicking a transaction opens the Navigation map centred on the room where that deposit/stash happened

## 3.8.24

- On Paradigm, the loot engines now batch multiple copies into one counted action (`get 5 piece of amber`, `hide 35 orc-head`, `drop/sell/buy N <item>`) instead of one command per copy; Stock still sends one at a time. Reply parsing handles the count-prefixed confirmations both ways
- Stashing items now lowers the encumbrance estimate immediately (a `hide` leaves the pack like a `drop`) — including Paradigm's counted batch form (`You hid 35 orc-head.`) — so cash/item collection no longer skips a pickup thinking you're still Heavy right after a stash when you actually have room
- Auto-get no longer re-sends `get` for the same floor items when a room re-renders (Cash re-grabbing ground coin, the post-kill re-look, the combat-clear redisplay) — four ground orc-heads no longer become eight-plus gets; dedup keys on the `You took <item>` pickup confirmation, so a genuinely fresh drop still collects
- Looting resumes movement the instant pickups are confirmed (`You took <item>` / `You picked up <coin>`) instead of waiting out a fixed timer — the settle window is now only a fallback for a get that never confirms
- bug reports addressed: paradigm-20260812-182429, paradigm-20260812-201631

## 3.8.20

- Help window: a failed/missing guide asset now logs a warning instead of silently opening an empty compendium, so a "Help is empty" report has a program-log trail
- Fixed a stale comment on the Help menu's composition describing the "Help topics…" item as a disabled placeholder — it's a live command

## 3.8.19

- Combat attack spells no longer double-fire the normal + alternate: `MaxCasts` is now counted per *fired* round instead of at announce, so a `MaxCasts=1` normal spell actually takes its round before the cascade can hand to the alternate — fixing "normal fires then immediately the alternate," the alternate cast at a target the normal just killed ("You don't see X here!"), and a fresh mob opening on the alternate. A kill now fully drops the target and resets the cascade so the next mob reconsiders the normal spell first
- Stashing currency now names coins by their full two-word noun (`hide 50 gold crown`) like get/drop already do, instead of the bare denomination that MajorMUD binds to a same-named item
- bug reports addressed: paradigm-20260812-200128

## 3.8.17

- `@path` remote command now names the last loop or auto-lair the player ran when they're stopped/idle (instead of just "not moving") — so if a party member dies and halts, you can see which circuit they were on and help them resume without guessing
- New `@death` remote command reports unrecovered deaths from the recovery log — bare `@death` gives the most recent one (when, status, room, lives left), `@death all` lists them all — so you can help a dead party member recover; gated by a new "Query deaths" permission per player in Game Data → Players

## 3.8.15

- Map legend is now draggable — position it anywhere on the map; the spot is remembered install-wide, and its backdrop is more opaque so it stays readable over map features. Toggling it off and back on re-clamps it into the currently-visible map, so a position left off-screen by a shrunk window snaps back

## 3.8.14

- Backscroll window: added a right-click **Copy** / **Select all** menu, and fixed both Ctrl+C and copy to reliably put the selection on the clipboard as plain text — the transcript now handles Ctrl+C itself (and is focused on open) and the write stays on the UI thread, instead of silently failing and leaving a stale image, or not coming across

## 3.8.13

- Help window: clearing the search box after picking a filtered subsection now keeps that topic selected and visible in the tree (the branch re-opens to it) instead of collapsing and burying the selection

## 3.8.12

- Toggling an avoid room that isn't on your running loop's path no longer disturbs the loop — it keeps circling instead of restarting into a stalled approach (avoiding an unrelated room used to Stop+Start the loop, reset session stats, and re-broadcast @reset every toggle); a loop whose path an avoid does block re-plans around it while keeping the same session
- A GOTO/walk-here whose only route is walled off by your own avoid now says "only route is blocked by user set avoid in room (map/room)" instead of a bare "no path"; auto-deposit and auto-train log the blocking avoid room and skip cleanly instead of stalling
- Route planning treats a key-only locked door as impassable without its key — the router no longer assumes a strength bash can open a door that only opens with a key, and surfaces the key requirement in the route picker
- Reconnecting after an involuntary server drop (carrier lost / no response) auto-enters the game again: a stale suppress-entry flag left by an earlier deliberate hangup no longer leaks across the drop and strand you at the main menu
- Auto-bless (self and party) is now controlled by the Auto-Bless toggle and nothing else — decoupled from both Auto-Combat and Auto-Rest/Heal, so turning either off no longer stops blessing (fixes a stuck InCombat that silenced it, and the survival loop no longer bails on the Auto-Rest/Heal master)
- Bless "while resting" now means a *triggered* recovery rest (HP/MA fell below rest-if-below) — idle/standing resting always buffs; and both the "while resting" and "during combat" checkboxes are now opt-in overrides, off by default (self and party alike)
- Startup splash redraws only the cells that change each frame (header/background drawn once), trimming per-frame render work
- Help compendium filled out: a comprehensive Remote @-command reference (every command, its arguments, the permission model), plus the Party window, Action menu, Program Log, status-bar readouts, and the loop exp/hr estimator now documented; Program Log Debug/Combat tooltips corrected to "on by default"
- bug reports addressed: paradigm-20260812-111920, paradigm-20260812-074651, paradigm-20260812-150324, paradigm-20260812-160212, paradigm-20260812-160829

## 3.8.4

- Combat now fires a configured area/single-target debuff BEFORE the attack on engage (it used to land a round late): the debuff goes out first, then the attack re-announces on its *Combat Off* — while still deferring to a higher-priority survival cast per the Spells + Ailments spell-type priority
- Door-open no longer hangs the walker when a bash/pick/open draws no recognised response (e.g. in the prompt churn right after a training detour): a per-command watchdog treats the silence as a miss — retrying to the attempt cap, then failing over — so the walker replans instead of stalling
- bug reports addressed: paradigm-20260812-052003, paradigm-20260812-050055

## 3.8.2

- Fixed the combat engine spamming its attack spell (e.g. `hamm`) many times a second until MaxCastsPerRoom capped it: its own announce was mistaken for a hand-typed cast, arming an immediate re-attack on the *Combat Off* the announce itself always causes — a self-sustaining recast loop. Engine-issued casts now ride a raw send that skips that observer.
- bug reports addressed: paradigm-20260812-084956, paradigm-20260812-085251

## 3.8.1

- Filled in the Help compendium: added Getting Started, The Interface, Combat, Navigation & Looping, Party Play, Healing & Spells, Cash & Items, Player Workshop, Automation, Game Data, Conversation, Tools & Diagnostics, and Troubleshooting sections alongside the Settings Menu reference — concise explanations of how the client and its features actually work
- The interactive windows are written as step-by-step how-to's — the Navigation window, Player Workshop, the macro/alias/trigger editors, the Game Data Browser, the Conversation window, the Spell Book, and the Backscroll / Session Stats / Wire Inspector tools: how to open each, its menus and buttons, and the workflow to drive it

## 3.8.0

- New Help window (Help → Help topics): a searchable, tree-organized help browser — table of contents on the left, rendered help on the right — that ships with a full "Settings Menu" reference explaining what every setting does, how it works, and when to change it (the first of a growing compendium)
- The search box filters the table of contents live by both topic title and body text; the content pane renders headings, bold/italic, inline code, bullet lists, and tables

## 3.7.3

- Fixed saving Settings → General resetting the "Disable hangups" toolbar toggle back off: that tab rebuilds the General settings from scratch on Save and wasn't carrying that flag, so saving it (even to change an unrelated setting) wiped the toggle — now preserved

## 3.7.2

- Fixed a lost carrier taking up to ~13 minutes to be noticed when it dropped mid-combat: the TCP keepalive meant to catch it resets on every send, so a client actively firing commands into a vanished server fell back to the kernel's retransmit timeout — a TCP_USER_TIMEOUT now caps dead-connection detection (to the no-response window, or ~60s when unset) so the auto-reconnect fires promptly
- The per-BBS "No-response (s)" default is now 20s (was disabled), so a fresh setup catches a dropped carrier in ~50s out of the box instead of relying on the OS default
- bug reports addressed: paradigm-20260811-210821

## 3.7.0

- Auto-engine toolbar toggles now snap back to your per-character base modes at the first start of a loop or auto-lair, and on profile load — so you can flip engines off to travel to a circuit (e.g. combat off to sprint 500 rooms to a loop) and settle into it with your defaults restored, badges and all
- Settings → General's engine checkboxes (renamed "Auto-Engines base modes") now define those base defaults, decoupled from the live toolbar: flipping a toolbar toggle no longer changes them, and they set the engine positions on profile load
- The snap-to-base fires once per run, not on later laps of the same loop; Auto-Train is excluded (it's not a toolbar engine)

## 3.6.2

- Captured transcripts — bug reports, death logs, and the Backscroll window — now stamp every line with the instant it was actually written, both the still-on-screen rows and the scrolled-off history, so the whole transcript stays in chronological order and lines up against the program log by the clock
- Previously the on-screen lines came through blank (reports) or all shared one window-open time (Backscroll), and scrolled-off lines were stamped when they left the screen rather than when they arrived — which ran the timestamps out of order at the boundary

## 3.6.1

- Fixed a mid-fight self-buff (e.g. armr / mshi) wasting a whole combat round: after the buff dropped *Combat Off*, the spell re-attack was held back by the 500ms recast burst-guard and slid to the next tick, so the monster got a free swing before the character resumed attacking; the interrupt-resume now re-attacks immediately
- bug reports addressed: paradigm-20260811-203111

## 3.6.0

- Navigation line colours and thickness are now customizable in Settings → General — the go-to, loop, preview, loop-builder, and Auto-Lair route lines each get a colour picker and a thickness stepper (per-line Reset, plus a "Restore Defaults" for all of them); the current look is the default, saves install-wide, and the map repaints live on Apply

## 3.5.23

- Fixed the alternating action orders (Alternate Spell/Physical, Alternate Physical/Spell, and Custom round cycle) flipping their attack/spell phase mid-round instead of once per real round — a monster's counter-swing line landing a beat after the player's own could each independently trip the engine's round-boundary signal, so a fight could switch from a physical swing straight to a spell cast (or back) within the same round instead of waiting for the next one
- Fixed a deadlock where a room with more hostiles than Combat → Max Monsters allowed left the character standing defenseless: CombatManager correctly declined to engage, but CombatStateTracker (which owns the walker's movement gate) didn't know about that window and held the walker there anyway — combat refusing to fight AND the walker unable to leave, absorbing hits from every monster in the room with no recourse
- The Min/Max Monsters window now only applies while a walker / loop / auto-lair is actively moving you through a room — it's meant to stop you from stopping to fight mid-route, not to leave you standing undefended when you're simply idle (freshly logged in, nothing queued) and a hostile room is the only thing there. Idle now fights back regardless of room population
- bug reports addressed: paradigm-20260811-130439, paradigm-20260811-133600, paradigm-20260811-134903

## 3.5.22

- As a party follower, the map now stays located through an identical same-named corridor (e.g. a run of "Slum Street") instead of discarding each leader-follow arrival as a stray re-look, desyncing, and needing repeated manual `rm`s — a follower's drag arrives instantly, which the map used to mistake for a passive redisplay
- bug reports addressed: paradigm-20260811-122610

## 3.5.21

- An attack-spell build no longer gets stuck healing/buffing forever and never attacking — a survival cast can't fire again until the attack spell it interrupted has gone back out (a fixed attack / heal-or-buff alternation, not a heal-until-HP-is-comfortable loop)
- A survival heal winning the round's single cast slot no longer permanently wedges combat out of retrying — no more sitting idle after a mid-fight heal until manual input
- After auto-lighting a dark room the client now redisplays it (a bare CR) so a monster standing there unseen is engaged, instead of relying on it to swing first and walking past a passive one
- Corpse-recovery auto-equip wields a held weapon with `eq` instead of `hold` (which only carried it in hand, never wielding it)
- `@party go <text-exit>` (e.g. `go hole`) keeps its `go` verb when relayed to followers — only a real cardinal direction is sent as the bare token
- Settings → Cash bank/stash picker updates live when a stash room is marked or unmarked on the map, instead of only after reopening Settings
- Bug report now captures more history — 750 lines of scrollback and 750 program-log entries (was 500 / 250)
- Combat re-engages the next monster right after a kill instead of re-attacking the corpse and stalling a round — the kill is inferred from the exp gain on its `*Combat Off*` (each realm's custom per-monster death messages can't be matched), gated so a mid-fight heal (or party share-exp) isn't misread as a kill
- Room-wide combat spells (multi-attack, area debuff) are now cast bare — `blad` / `stnk`, never `blad <mob>` (the targeted form the server rejected)
- Combat keeps its target and round-cycle / attack-spell progress across a mid-fight heal / bless / buff instead of restarting as a brand-new fight — no more "confused which attack to use" after an interrupt
- Rooms several monsters enter at once now nuke the whole group on the first action instead of committing to a single-target cast and rooming a beat late — combat briefly waits for the arrivals + room re-display to settle
- A room-wide attack spell no longer undercounts a monster whose number the client hasn't resolved, so a full room isn't held below its multi-attack threshold
- Coins a post-combat `search` surfaces are now collected instead of skipped as "already handled this room visit"
- Fixed the loop sitting forever after a `go path` step into a same-named room — the arrival now confirms the move instead of being mistaken for a passive re-look of the room just left
- bug reports addressed: paradigm-20260811-063936, paradigm-20260811-065736, paradigm-20260811-090358, paradigm-20260811-094533, paradigm-20260811-063728, paradigm-20260811-104042, paradigm-20260811-081053, paradigm-20260811-081654, paradigm-20260811-103708, paradigm-20260811-135433, paradigm-20260811-092255, paradigm-20260811-122253, paradigm-20260811-080136, paradigm-20260811-102544, paradigm-20260811-102843, paradigm-20260811-104012, paradigm-20260811-105620

## 3.5.1

- Startup animation preference is now install-wide — saved to the default profile and read at startup regardless of which character auto-loads, so turning it off stays off across relaunches and profile loads
- Fixed the animation briefly flashing when a named profile loads

## 3.5.0

- The route picker for a hazard you can't cross yet now offers an "Obtain, then cross" route — it grabs a counter off the current room's floor, or buys / asks / hunts one en route (e.g. the ice-cavern rope & grapple), naming where — instead of only "carry, buy, or use a counter yourself"
- Picking it fetches the counter even when the item isn't flagged auto-obtain (the explicit choice is the consent); a "cross unprotected — take the damage" option and Cancel remain

## 3.4.3

- Walking to an unreachable or gated room no longer hangs silently in "Walking" — a watchdog now surfaces the reason, naming the specific barrier (e.g. missing key, or "1/1420 (Marble Passage) needs level 40+"), or plans from your last confirmed room when an in-flight move never settles
- Room hazards whose spell stores its check in MinBase/MaxBase are now detected — the ice-cavern rope & grapple, plus ~40 other areas the client was silently missing — so the route picker offers the protection (or warns of the damage route) instead of routing you through unprotected
- bug reports addressed: paradigm-20260810-201953, paradigm-20260810-202239

## 3.4.1

- New Combat action order: "Custom round cycle" — spend N rounds attacking physically, then M rounds casting spells, repeating for as long as both are set; a round count of 0 means that phase runs for the rest of the fight (e.g. physical for 2 rounds, then spells till death)
- The cycle can open on either phase (physical or spell) via a "start on spell" toggle

## 3.4.0

- The "default profile" (loaded when no character is chosen) is now a real profile saved in the Global folder: edit it, hit Save, and your changes — e.g. turning the splash off — persist and load on every startup
- File → Save As copies it into a named character, so new profiles start from your defaults instead of the installed ones
- File → Save (on the default profile) writes it back to the Global folder rather than prompting for a name

## 3.3.5

- Unchecking "Show the mud-throwing startup animation" and hitting Apply now stops a splash that's already on screen, instead of only taking effect at the next launch
- The splash frame timer runs at background priority so the attract animation yields to input and session rendering instead of lagging the client while it plays
- Removed the leftover unused "Mud Now." splash scene

## 3.3.4

- Loop building now allows the same room twice in a row (map-click loop mode and the create-loop editor) — a zero-length "stay put" step that runs another command in place (e.g. two barmaid steps: hand in pies, then convert the coin)
- Program Log has a "Simulate Death button" toggle that reveals the Death Recovery tab's test button; off by default (and reset off each launch), so a normal session never shows it
- Settings → General now has a Navigation-tooltip font + size picker under the terminal font, defaulting to the map room-tooltip's current look

## 3.3.1

- Stat collection now arms on the `stat` abbreviations too (`st` / `sta`), not just the full word — the Player Workshop updates however you spell it
- bug reports addressed: paradigm-20260810-093510

## 3.3.0

- The startup splash is now a rotating collection of animated ANSI scenes — a random one plays each loop, drawn from a shuffle-bag so every scene shows once before any repeats, swapping seamlessly on a clear lens
- Scenes: the original mud-throw plus a monster truck, mountain mudslide, mud pie, pig wallow, mud geyser, mud rain, windshield wiper, swamp monster, sinking goblin, and the "Mud!" seagulls
- Settings → General "show startup animation" still gates all of them (the title/byline header stays either way)

## 3.2.6

- `@loop <name>` matches a saved loop by a close-enough 1-of-1 name — every typed word, any order (e.g. `@loop godfrey bank` starts "Bank of Godfrey Loop")
- `@goto <name>` resolves a saved GOTO location by name (takes precedence over a raw room name), and as a last resort a boss name → the boss's closest listed room (stops one room short for StopBefore bosses)
- `@goto` coordinate destinations accept a full map/room with space, comma, or slash separators; a bare room number is rejected (the same number is a different room on each map)
- The Navigation search box and `@goto` no longer match monster lairs — both resolve places only: rooms, saved GOTO locations, and boss names from the boss table

## 3.2.3

- App data now lives directly in the MudPlay app folder (e.g. `~/.local/share/MudPlay/`) instead of a nested `Data/` subfolder
- First launch after updating automatically lifts your existing data up and removes the empty `Data/` folder

## 3.2.2

- Per-slot "recast within N seconds of expiry" picker on every self-bless slot (Spells tab) and every party-bless slot (Party tab)
- Still defaults to 15s; set a slot to 0 to hold its recast until the buff actually expires (a wear-off message or the tracked timer running out)

## 3.2.1

- Two new Combat "Action order" modes: alternate the round's action every round — "spell, then physical" and "physical, then spell"
- On its off phase each mode falls back to the other action type (no castable spell → swing; weapon can't hit → cast) so the round never stalls

## 3.2.0

- New Help → About window: program name + version, a clickable link to the repo, a tab per bundled license (MudPlay, Avalonia, JetDatabaseReader, and the bundled fonts — MIT / SIL OFL 1.1 / CC BY-SA 4.0, shown verbatim), and a thank-you to the MajorMUD community and the tools it built (MegaMUD, Nightmare Redux, MajorMUD Explorer)
- New animated startup splash on the terminal — a figure hurls mud at the "lens", it splats and slides slowly off, revealing the "MudPlay" / "Created By Fujin" header, then loops; toggle it in Settings → General (the title/byline stay either way); never written to the backscroll log
- Updating from a 2.x build now carries your data over: profiles, BBS folders, settings, and imported game data migrate from the old FujinTerm folder into MudPlay on first launch (non-destructive, one-time) — fixes profiles appearing missing after the 3.0 rename

## 3.0.0

- Officially named the program MudPlay

## 2.39.6

- "All auto off" now also parks navigation: engaging it suspends any in-flight walk / loop / auto-lair right where it is, and toggling it back on resumes exactly that — a manual pause or stop you make in between is respected, and it's idle-safe (nothing to resume when nothing was running)
- Equipment Manager and Item Finder trial gear slots now list in the in-game "look" order — worn slots top-to-bottom (Head → Feet → Worn), then Off-Hand / Weapon at the bottom, with the alternates mirroring that pairing right after

## 2.39.4

- Death now clears the client's condition tracker, so a knockdown / held ("flat on your back") state whose in-game recovery line never arrived can't survive the death and leave navigation stuck "Paused by: Held" while the character is free to move
- Combat now falls back to your alternate attack command when a spell used as your attack command draws "no effect" (e.g. priest `harm` vs an acid slime immune to it) — previously only attack *spells* fell back, so a spell in the command slot kept firing uselessly
- Attack-spell cascade now swaps primary → alternate spell (e.g. `harm` → `hamm`) on the same round the primary is found immune, instead of idling a round until the next tick — the weapon fallback was already instant, only the spell fallback lagged
- Game Data → monster "Override Attack Spell" is now "Override Attack" and accepts either a spell number or a plain command/cast-code (`attack`, `bash`, `harm`): a typed command now saves (it was silently dropped before) and is forced against that monster over the normal combat flow
- bug reports addressed: paradigm-20260809-114444, paradigm-20260809-131642, paradigm-20260809-162350

## 2.39.0

- Keep typing at the terminal while other windows are open: keystrokes typed with a settings/editor window focused now fall through to the terminal, so you can keep sending commands without clicking back — unless you're editing a text field in that window, or the key is one the window needs (Tab, Escape, menu shortcuts)
- Forwarded keys run the terminal's real input path (macros, local line editing, command history, escape-sequence mapping), identical to typing directly in the terminal
- New Settings → General toggle "Keep typing directed at the terminal when other windows are open" (on by default); turn it off for classic focus behaviour
- End Game Trainer quest defs refreshed: base level 65, "Episode" relabeled "Part", map/room references normalized to a slash and bracketed so they all render as clickable walk-to links, rewritten advice for each part, a condensed sphere guide + reworked level gate (paradigm 75+/stock 66+) in Part 5, and per-realm trainer notes on the Part 4 halls
- Quest defs now follow the tier model cleanly: the bundled seed builds the read-only Global copy (re-synced on launch, reseeded if missing), and your quest edits are stored at the BBS tier (`Data/BBS/{bbs}/quests.json`) and win over the Global seed — so shipped guide updates reach you without deleting data files, and your edits belong to the board you play (was a per-game-data-set overlay before)

## 2.38.0

- Party stats probe: the first time you party with a player each day, the client asks them `@level` and `@version` and records their exact level + client version onto their player record (shown in Game Data → Players); `@health` still fires on every join for live vitals. New Party setting "probe party members' level & version on the first party of the day" (on by default)
- `@version` replies (e.g. `{MudPlay 2.38.0}`, `{MegaMud 1.03u}`) are now captured and stored per player; the player edit dialog shows Version + Last partied
- Level display now reconciles exact vs. title: a recorded exact level wins, unless the player's title band has climbed above it (they trained since we last asked) — then the title range is shown until we re-learn an exact at or above the band's floor
- Route level-gating now treats a recorded level as fresh for the current day (was a fixed 24h window), and re-learns it via the once-a-day probe or any manual `@level`; the old leader-only roster-change level poll is retired
- Peer `@level` replies from other MudPlay clients (brace-wrapped) now record correctly

## 2.37.0

- Item Finder: new trial-gearset panel (toggle at the top-right of the results, hidden by default) to plan a loadout — one dropdown per slot listing the currently-filtered items, plus a Hold lock per slot
- "Import from live" copies your worn set into the trial slots; "Clear" empties them
- "Find Best" clears every non-held slot then fills it with the best equippable item for a chosen stat (Armour Class, damage, HP, mana, each +stat, resists, …), honouring the finder's class / alignment / level filters — find-best one stat, Hold the keepers, find-best another, and fill up
- Quick "filter by name" box at the top of the results, and hovering a trial slot shows the placed item's stats
- The panel projects the trial set's worn-item stats (the same readout as the Equipment Manager) and its encumbrance — exact weight and None/Light/Medium/Heavy — over your current
- Drag the divider between the trial slots and the stats readout to rebalance them (so the readout stays visible when the window isn't maximized); the split is remembered per profile
- Right-click a result row → "Trial-Equip this Item" drops it into its slot
- Double-clicking a result now opens that item's record dialog directly instead of jumping to the Game Data Browser
- Find Best "Max/Min Damage" now counts the +damage gear bonus too (not just a weapon's base damage), so it fills damage-bonus armour/jewellery slots, not only the weapon
- A trial slot's hover tooltip now shows a weapon's base damage, swing speed and any proc-cast (e.g. "Casts (85%/swing): mana flare — Dmg 240-350"), not just its worn-stat bonuses
- AC Blur is now shown as its own stat everywhere — trial tooltip, projected-stats readout, a new results column and a Find Best filter — instead of being mislabeled as flat Armour Class (blur AC scales inversely with encumbrance, so it isn't the same thing)
- bug reports addressed: paradigm-20260808-203733

## 2.36.0

- Main window title now shows the running version — `MudPlay v<x.y.z> — <profile> — <BBS>`
- New Mana Regen calculator (Calculators tab): plan level / stat / gear against mana-regen tick breakpoints for a mage, priest or druid — a natural-tick readout, a tick-vs-level breakpoint chart (capped at each stat's racial trained max), and a roll-spell slider that shows the tick any roll lands so you can find the breakpoints; the class dropdown is limited to those three archetypes, the driving stat follows the class (mage INT, priest WIL, druid INT/WIL average), and priests pick between their two roll spells (serenity / profane link)
- Loops no longer freeze forever when combat interrupts a move mid-step: a stall watchdog now detects the never-confirmed move and re-establishes position (Paradigm `rm` resync / stock footprint backtrack) so the loop resumes instead of standing still
- Item-cast buff swap now handles an off-hand buff item (e.g. engraved warhorn) correctly — it restores the off-hand shield instead of the weapon, and when the buff item was left equipped from a prior session it puts the right gear back from your equipment set instead of stranding it
- bug reports addressed: paradigm-20260807-133143, paradigm-20260808-161554, paradigm-20260808-161643

## 2.35.3

- Map "Spells: by name" filter no longer tints a spell room near-grey — the palette dropped its neutral swatch, so spell rooms stay visible against normal rooms (was invisible in the icy mountains)
- Program Log now defaults Debug + Combat diagnostics ON, so a fresh character's bug report already carries the decision trail (on-disk log collection and hop-timing stay off)
- Fixed a crash on killing a boss (e.g. the mad wizard) when two client instances share one data folder: the boss-timer write no longer races on a shared temp file
- Atomic JSON saves now use a unique temp file per write and ride out a concurrent replace, so two instances persisting the same realm-wide file can't collide
- Boss-timer persistence failures are now logged instead of crashing the client mid-combat
- bug reports addressed: Crash-20260806-233028, Crash-20260807-060547

## 2.35.0

- Auto-light now treats a configured room-light spell as light coverage — its illu (individual + roomillu) counts toward the visibility total, so gear + spell that already cover no longer trigger item buying
- Auto-light auto-casts the configured room-light spell on entering a dark room when no carried light covers: buff realms light instantly; light-ball realms cast then ready the generated ball
- Auto-light preferred-light dropdown no longer lists shop-unsold lights (drops realm-generated light balls); new "only use my room-light spell (no items)" option never provisions items

## 2.34.0

- Exp/Hr estimator now counts monster-summoning room spells: rooms whose entry spell rolls extra monsters (e.g. Paradigm's crypt summons) add their expected exp to the estimate instead of being ignored — an averaged roll per visit plus a bonus roll on quick kills, with a new "Room summons" breakdown line
- bug reports addressed: paradigm-20260806-030133

## 2.33.1

- Item-cast buff swap now restores the item's actual slot: an off-hand buff (e.g. engraved warhorn) puts the off-hand shield back instead of re-equipping the weapon; works for any worn slot, not just weapon/off-hand
- bug reports addressed: paradigm-20260806-015831

## 2.33.0

- Auto-train and Auto-train-stats are now independent toggles — auto-level without auto-spending CP, or auto-apply the plan on a manual/@train without auto-levelling
- Player Workshop → CP Allocation: new "Apply this level" button applies a selected row's stats at the trainer you're standing at — but only when its CP fully lines up; it confirms via `stat` and clears the row only on a verified success
- Auto-train / Auto-train-stats now toggle from the CP Allocation tab too, in sync with Settings → Auto-Trainer; the old Auto-Train toolbar button, menu item and keybind are removed
- Settings → Auto-Trainer: new "Do not train above level" ceiling — auto-train reaches that level then stops (no accidental over-levelling)
- Navigation map: trainer rooms now show a triple up-chevron "level up here" marker — every trainer for the active game-data set
- Navigation Management is now one shared window from both the toolbar Start button and the map's Navigation Management button (re-focused, never a second copy) — Start opens it on the Go To tab, the map button on Loops
- Toolbar Start no longer greys out while a loop/goto is running; pressing it (click or Alt+V) opens Manage so you can switch destination by hitting Run on a different favourite / loop, and it stays depressed while any movement engine is in progress

## 2.32.0

- Navigation map: boss rooms from the Bosses table now show a gold crown; a boss flagged "stop before entering" gets a red halt ring around its crown
- Navigation map: the Spells overlay chip now cycles mono → by name → off — "by name" colours each room-spell room by which spell it carries, so clustered but different room spells (e.g. Swamp of Tharollok's spawners vs the swamp-poison) read apart; hover a room for its spell name. Saved per character
- Manage Bosses dialog: widened so all columns are visible without horizontal scrolling
- Manage Bosses dialog: "Add boss" now scrolls to and starts editing the new row instead of appending it out of sight at the bottom

## 2.31.4

- Navigation: your position is now saved as it actually is when you close — reopening lands you where you left off instead of at your last manual `rm`, even after grinding through same-named rooms (e.g. Paradigm's Graveyard) where moves confirm by prediction
- bug reports addressed: paradigm-20260805-224603

## 2.31.3

- Combat: the percentage mana reserve now matches the mana number shown in Settings — an 82% reserve on a 66 max means 54 mana casts (not 55), so the spell no longer swaps to physical at the exact value you set as castable
- bug reports addressed: paradigm-20260805-224742

## 2.31.2

- Combat: swapping an attack spell to physical on the round it kills the target no longer strands — a "Your command had no effect." reply now clears spell mode too, so the client re-observes instead of re-casting at the corpse
- Combat: fixed a doubled physical swing when a between-round self-bless landed just before a spell kill (the survivor's fresh swing was re-fired on top of itself)
- bug reports addressed: paradigm-20260805-220759

## 2.31.1

- Combat: once an attack spell stops for a monster — its MaxCasts rounds are spent, or mana falls below the per-cast reserve — the client now commits to the weapon for the rest of that monster instead of flipping back to the spell the moment a mana tick lifts it above the reserve again
- bug reports addressed: paradigm-20260805-130847

## 2.31.0

- Combat spell "Min mana per cast" now shows its live equivalent beside each slot — the mana amount in Percentage mode, the % in Value mode (mirrors the Health tab)
- Percentage mode now caps at 100%: Combat Min-mana-per-cast and the Health tab's HP / MA thresholds can no longer be set above 100% (existing over-100 values snap down when you switch to Percentage)
- Settings tickers are whole numbers only — spell max-casts, min-enemies, min-mana, room monster counts, and Health thresholds no longer show trailing decimals

## 2.30.3

- Spell combat now mirrors physical combat: an attack spell is announced once and the server auto-repeats it each round, so the client no longer re-casts every round. Fixes the "double cast" and the cast at the monster that just died ("You don't see X here!")
- Attack spells now engage a fresh monster instantly instead of waiting for it to swing at you first
- Combat switches action correctly when it must: MaxCasts elapsed (per-target for single-target spells, per-room for AoE), out of mana or immune (cascade to the alternate spell / weapon), or a room thinned below the AoE minimum-enemies threshold
- bug reports addressed: paradigm-20260805-105305, paradigm-20260805-105735, paradigm-20260805-105800

## 2.30.0

- Terminal right-click menu gains a **Favorites** flyout — star up to 10 Go To destinations and walk there in one click without opening Navigation
- Star a favourite from the Edit favourite dialog (checkbox, max 10); starred entries show a ★ in the Go To list
- Bosses tab now shows the **Notes** column (from Manage Bosses) to the right of the Timer column — read-only, resize to read a long one
- New **Exp** toolbar button sends the in-game `exp` command (add it via Settings → Toolbar; included by default on fresh profiles)
- Player Workshop window now fits its width to the active tab on each tab switch (wide tabs like Bosses get the room they need, narrow tabs shrink back); height matches the Equipment tab so long lists (Quest, Bosses) scroll instead of ballooning the window — stays freely resizable afterward
- Fixed the walker stalling after training on Paradigm: the reused "Char. Creation" stat box lingered on screen and kept re-arming character-mode input, holding movement so it advanced only one room per manual `rm`. Character-mode input now arms only when the box first appears, not while it lingers
- Fixed a mage re-casting its attack spell at the monster it just killed: the spell's own damage line fired an extra combat round-tick that slipped a second cast through, landing on the corpse. Combat spells now cast once per round (like a weapon swing), so the echo can't re-fire
- Bug report now lists only your starred favourites instead of the entire Go To list, so it stays small
- bug reports addressed: paradigm-20260805-095320, paradigm-20260805-095546, paradigm-20260805-095653

## 2.29.0

- New **Bosses** tab in the Player Workshop: the per-realm boss catalog (240 bosses) with respawn timers read from live game data, so they stay correct across game versions
- Live per-window countdowns: a 100% guaranteed-respawn column plus the realm's earlier spawn windows — Paradigm's 5 / 10 / 20%-off points in their own columns, collapsing to Stock's single 87.5% column — each counting down and blanking once that window has passed; sortable by boss, respawn, and each timer (cleanup spawns first, then counting timers, then unset / expired — in either direction) and filterable by boss, room, or respawn
- Per-row **Mark** (set / back-date a kill time via a date-time dialog), **Reset** (stamp the kill at now), and **Clear** (drop the timer); timers also auto-start on a detected kill, persist across restart, and are shared per realm
- "Respawns @ Cleanup" bosses read a colour-coded **DEAD** (red) / **ALIVE** (green) state instead of a countdown — DEAD once marked, flipping back to ALIVE at the BBS's nightly cleanup time (new per-BBS setting in Settings → BBS + display; default 21:00 in your computer's own time zone, auto-detected, with a dropdown to override)
- **Manage Bosses** dialog to add / edit / remove entries — name, rooms, respawn type, realm flags, a manual respawn-hours override for bosses game data can't resolve a timer for, and a "show in table" toggle to hide an entry from the tab (still tracked), and a free-text notes field — with its own filter; **Import / Export** share a realm's table as a JSON file. "Stop before" toggles inline in the table (walk-to halts one room short of a flagged boss room)
- New `@timer` remote command reports the boss timers being tracked, one reply line per boss (name, time to full, next window, e.g. "full 2h14m, next -20% 1h47m"): no argument lists them all, `@timer <name>` filters by name substring, and a boss you aren't holding replies "expired"; gated by its own "Query boss timers" player permission (grant it per-player in Game Data → Players)
- Walk-to now honours per-boss "stop before": a walk (map click / GOTO / @goto / recovery) to a flagged boss room halts one room short instead of stepping in and triggering the spawn — loops and Auto-Lair are unaffected
- Gang-channel messages now use the correct `bg` speak verb instead of `gang`, so remote-command replies, level-up announces, and party `@heal` broadcasts actually reach the gang
- Fixed a double movement send while looping through same-named corridors: a post-combat re-look of the current room, echoing back while a step was still in flight, was mistaken for arrival at the identically-named next room — phantom-advancing the loop and firing the next step before you'd actually moved; the tracker now recognises that too-fast same-room redisplay as a re-look and waits for the real arrival
- bug reports addressed: paradigm-20260804-200154, paradigm-20260804-211645, paradigm-20260804-221124, paradigm-20260804-221137, paradigm-20260804-221148, paradigm-20260804-225836, paradigm-20260804-225939

## 2.28.0

- Cash collection now decides once per room visit at an encumbrance gate (with collect-after-combat and/or drop-smaller-for-larger): coins are collected/traded a single time instead of re-running on every post-combat room re-display — no more flood of get/drop commands, no coin shed on redundant swaps, and the collect can no longer fire after the loop has stepped out of the room (which was failing the get and stalling the loop, or missing the pile entirely)
- Cash + Items settings: the Bank/stash picker gains a "Do not auto-deposit" option — pick it to disarm auto-deposit, and it's the default when no bank/stash is set
- Exp Estimator: the ✕ remove button in the clicked-rooms list is no longer hidden under the scrollbar when the list scrolls (same fix applied to the Loop Builder's room list)
- bug reports addressed: paradigm-20260804-143020, paradigm-20260804-143150, paradigm-20260804-143321

## 2.27.2

- Fixed a flood of get/drop/look commands when collecting cash after combat at an encumbrance gate: instead of replaying every coin drop seen mid-fight (which double-counted each kill's drop against the room's running ground total and re-queued on every re-render), the client now re-displays the room once combat clears and collects from the actual ground contents — one pass
- Reconnecting mid-loop no longer leaves the walker sitting idle until you type `rm`: a drop that stranded the cash/item post-combat collect hold is now released on the first in-game prompt after reconnect, so the loop resumes on its own
- bug reports addressed: paradigm-20260803-215518, paradigm-20260803-215605, paradigm-20260803-230756

## 2.27.0

- Conversation window header state persists per character: the seven channel filter checkboxes (Gossip / Say / Telepath / Gang / Broadcast / Yell / Server) and the auto-scroll toggle now survive closing and reopening the window, and reload with the character profile (saved on change, restored on profile load)

## 2.26.0

- Exp/Hr Estimator now accounts for monsters that summon more monsters on death (e.g. the Zombie Pen's stitched zombies): a lair's exp folds in the whole summon tree, so its yield reflects reality instead of the base monster's face value
- The summons cost combat time too — single-target counts every monster the spawn becomes; AoE/"rooming" adds one clear pass per summon tier — so the estimate rises but stays tempered by the extra kills. Affected lairs show a "summons" tag
- Respects the engine's 20-monster room cap: summons that would overflow a room aren't counted, so a big fan-out isn't scored as if every summon spawned
- bug reports addressed: paradigm-20260803-164838

## 2.25.0

- Loop waypoints gain a "Do not rest in this room" checkbox (in the waypoint action editor): when a running loop is in that room and HP/MA drop below your "rest if below" gates, it advances to the next room instead of resting — for rooms too dangerous to sit still in
- Only that exact room is protected; the moment the loop steps out (into a path room or an unmarked waypoint), resting resumes normally. Loops only (walk-to / auto-lair are unaffected); marked waypoints show a ⛔ badge

## 2.24.0

- Right-clicking a map room with an up and/or down exit now offers "Go Up → …" / "Go Down → …" menu items that jump the map view to the room on that floor (view-only, mirroring "Use Teleport" — your character doesn't move)

## 2.23.0

- Exp/Hr Estimator now handles bosses (a monster with game-limit 1 or a regen of an hour+), whether in a lair OR placed in a room: it's pulled out of the lair/fixture handling and added once as `boss exp ÷ regen-hours` — so a 15-hour crowned spider adds ~80k/hr instead of inflating every room every lap, and a placed 3-hour juggernaut adds ~433k/hr instead of its full value every pass
- A boss that can appear in any room of a multi-room lair (or a placed boss) is counted once for the whole loop, not per room; a new "Bosses" line in the breakdown shows each one's contribution
- Monster exp now reads `EXP × ExpMulti` (the boss multiplier) instead of raw `EXP`, so a ×20 boss no longer shows 1/20th its value
- bug reports addressed: paradigm-20260803-035136, paradigm-20260803-094657

## 2.22.0

- Auto-recover deathpiles now uses the correct Stock mechanic: on entering the death room it reads the "You notice" survey and, if your `corpse of <given-name>` is there, sends one `recover corpse <name>` — instead of blindly spamming `get <item>` for every pile item
- If the corpse isn't in the room (looted/decayed), the pile is marked Missing (new grey status) and nothing is sent — no more repeated "You don't see X here." spam
- Recovery completes on the `You have recovered the corpse of <name>.` line; an over-encumbered partial recovery correctly stays Partial (retried on re-entry) rather than being marked recovered
- Own corpse recovered without a password even with corpse passwords set; the given name only is used
- Bug report now records the auto-recover/auto-equip toggles + the latest deathpile's status

## 2.21.0

- Navigation Management window: added filter boxes above the Loops & Auto-Lairs and Go To trees (debounced, flat + virtualized while filtering) to quickly find an entry
- Folders in both Manage tabs now start collapsed, so switching tabs is snappy instead of laggy with large seeded lists

## 2.20.0

- Navigation filters (Go To / Loops / Auto-Lairs) no longer lag with large seeded lists: filtering is debounced, and while filtering the matches show as a flat, virtualized list (only visible rows render) instead of force-expanding every folder
- Each filtered list scrolls within a bounded height; the unfiltered view keeps the folder tree

## 2.19.2

- Character-creation stat box now flips to direct-input mode on entry, so the arrow keys move between stat fields instead of cycling the given/family names (detects the box on-screen, incl. ParaMUD's abbreviated "Char. Creation" title)
- Items bought during character creation (before the first inventory dump) now register, so "Equip all" sees them instead of an empty pack
- Bug report now records whether direct-input mode is active
- bug reports addressed: paradigm-20260802-164301, paradigm-20260802-164843

## 2.19.0

- BBS Settings → Retry behaviour: new "Infinite retries w/ 3 second pause" checkbox that retries reconnects forever at a fixed 3-second pause
- When enabled it overrides (and greys out) the Max redials + Redial pause tickers; the "Reconnect when" triggers are unaffected
- Bug report now records the active BBS's retry config (infinite/redial counts + which reconnect triggers are armed)

## 2.18.0

- Importing a new MajorMUD MDB now seeds the fresh game-data set with base navigation loops + GOTO favourites for its realm (stock or Paradigm, picked from the MDB's Legit field), so a newly-imported realm arrives pre-populated instead of empty
- The seed is additive (never overwrites a loop or drops a favourite you already have) and once-only per set — re-importing, or deleting a seeded loop/favourite, never re-adds it
- Seed bundles ship in the app's Defaults folder (`Defaults/nav-seed/{stock,paradigm}/`): 164 loops + 697 favourites for stock, 594 loops + 1,502 favourites for Paradigm

## 2.17.0

- Navigation gains an Exp/Hr Estimator (its own collapsible in the map's right rail): press Start estimating, click rooms on the map to build a throwaway loop, and the estimated experience-per-hour plus a per-lair breakdown surface right there in the panel — then Save it as a real loop or Discard it
- The estimate is built on the game's 5-second combat tick (720 kills/hour ceiling): movement between lairs rides the downtime, so a short hop is free and only a genuinely long stretch (5+ empty rooms) costs a combat round. It solves for the loop's steady lap time as a fixed point — each lair fires min(1, lap ÷ respawn) of the laps — so a dense loop with free travel lands at the 720/hr cap and a spread-out one lands lower; the per-lair readout shows how early ("early by Ns") each lair is hit. NPC-placed fixtures (e.g. the 1/1765 slime beast) respawn instantly; pool exp is the expected value across the lair's monster list. The real-world multiplier is the haircut from that ceiling to live conditions (~0.9)
- Loading a loop into the estimator centres the map on its first room; the Save / Stop Estimating / Load / Clear buttons sit at the top of the panel; the per-lair list is labelled by map/room and reserves room for its scrollbar so it never covers the figures
- Tunable per your character: movement speed (seconds per step), an "I'm Rooming" toggle for area vs single-target combat, rounds to kill a mob, and the real-world multiplier
- Locked out while a loop is being built (both consume map clicks as waypoints); available again once the loop is running. "Save as loop" opens a system dialog to rename and choose the location (defaults to the active set's Loops folder — saving there makes it a runnable loop); "Load loop…" browses for a saved .loop file to analyse
- A live estimator session (route, tunables, estimate, and per-lair fires/misses) is captured in the bug report, so a "the exp/hr looks wrong" report is diagnosable

## 2.16.0

- Go To favourites management is back as a dedicated tab in the Navigation Management window: create / rename / delete folders (buttons, not just right-click), Add a favourite by room-name search or map/room number, drag favourites between folders, and per-favourite Walk / full Edit (name + map + room, in the tab and the rail's ✎) / Move-to-folder (pick from a list) / Delete. The GOTO collapsible also gains a filter box, and adding a favourite from the map now auto-expands the pane so it's actually visible (it previously read as a no-op). GOTO folders in the rail start collapsed, matching the Loops/Lairs section
- Go To favourites are now stored per game-data set (shared by every character on that realm) rather than per-character — the same model as loops and lairs; add or edit a favourite once and it's there for every character on that set
- Filtering the Go To or Loops/Lairs collapsible now auto-expands folders that hold a match, so a nested match shows immediately instead of behind a collapsed folder — and clearing the filter restores your folders' expand state
- Backscroll window mouse-wheel now scrolls a configurable number of lines per notch (default 5, was 1) — set it in Settings → BBS + Display, right below Scrollback, saved per-BBS
- A party follower coming out of the train-stats screen is no longer left unable to rejoin: entering that screen breaks up the party server-side, so the follower now clears its own stale "following" state — and even if it lingers, an invite / @join from the leader you think you're following is now honored (a leader never re-invites a current follower) instead of rejected as "already following"
- A party member's exact level from an `@level` reply is now recorded even when the reply lands after the query window closes (a slow telepath round-trip) — so a narrow level gate (e.g. a room admitting only level 10) no longer stays blocked on the coarse title-derived level band when the member is actually the right level
- bug reports addressed: stock-20260801-002423, stock-20260801-041531, stock-20260801-043107

## 2.14.0

- Emergency low-HP hangup now closes the connection itself after sending the realm exit command, instead of waiting on the server to drop it — a stuck or slow drop can no longer leave a mortally-wounded character sitting connected
- Loop builder: clicking a step in the CURRENT NAV build list now opens the Waypoint action editor to attach a command to that step (same as editing a saved loop), instead of deleting it — deletion moved to the roomier ✕ box, and the ↑ / ↓ / ✕ boxes are larger. Steps with a command show a ⚙ marker
- A hostile that spawns/appears in your room while a loop is running no longer gets left behind: the abandoned-combat halt now covers loops and auto-lair (not just point-to-point walks), and holds a short settle so a monster following you out re-asserts combat and gets fought instead of out-walked
- Restoring a client from the taskbar with the map (or another window) open now brings the main window up too, instead of surfacing only the child window and leaving the main minimized
- A follower coming out of the trainer stats screen no longer wrongly re-invites its party leader (which tangled the auto-rejoin) — only the leader reforms the party after a trainer trip; a follower waits for the leader's invite and auto-joins
- Dying no longer lets a stale destination re-drive you back into the room you died in: death now does a clean stop of every movement engine (walk-to, loop, auto-lair) and clears every retained destination — the same as hitting Stop — with no lingering halt, so your own manual or remote navigation afterward runs normally
- bug reports addressed: stock-20260731-010401, stock-20260731-015726, stock-20260731-082602

## 2.12.0

- Game Data → Items "Dropped By" now lists each monster's drop rate, e.g. "Prismatic Dragon(10%)"
- Route picker shows an approximate ETA per route (steps + lair-fight time), matching the live walk status
- Map room tooltips surface locked-door pick/bash requirements (e.g. "Door: 50 picklocks/strength")
- Map room tooltips surface the Dwarven Mines "mine ore" gather commands
- Map room tooltips surface paid room-command costs (gambling, healer/summon buys, passage fares, the jail bribe-guard)
- Navigation lair highlight gains a combined heat+count mode, and the chosen mode is now saved per character
- Double-clicking a death in the Player Workshop death log opens the map centered on that death's map/room
- Lower native memory growth on long sessions: the terminal/backscroll renderer caches the bold typeface instead of reallocating it per run per frame, and on Linux/glibc malloc arenas are capped at startup to hold down the native RSS floor
- Auto-combat no longer stalls on a monster it can't hurt: with both weapons ineffective and no attack spell castable, it moves to the next hostile or room instead of standing there getting beaten — a mana shortage is retried once MA regenerates, a true dead-end logs "cannot attack <monster>"
- Physical-first combat now fully exhausts the weapon (forcing the alternate swap) before falling back to spells
- Fixed a stale teleport route-preview lingering after a re-route, and a mid-walk replan dropping the "walk it, no teleport" choice
- On "your weapon has no effect", auto-combat now force-swaps to the alternate weapon (or falls back to a spell) and retries instead of stalling
- Killing a summon-on-death monster now rechecks the room before the walker steps on, so a fresh summon isn't dragged into the next room
- Fixed WalkTo failing to route out of some rooms (e.g. ganghouse 15/945) whose CMD was misread as a teleport
- Auto-collect no longer fires doomed coin `get`s at 100% encumbrance — the hard weight cap now always applies, not only when a "skip if makes …" flag is set
- Auto-deposit no longer wedges: a bank reroute that returns without dropping wealth below the threshold now re-arms instead of looping forever, and logs why it's holding
- Equipment manager no longer auto-applies a gear set while the Auto-All kill-switch is engaged (manual "Apply Now" / "Equip All" / @equip still work)
- No-mana classes (warriors/ninjas) no longer break combat and run when you type `exp` — a latent MaxMana on the stat/exp screen is no longer applied as a live mana pool, so the Health tab's mana/kai settings (run/rest below mana) stay inert for a character with no mana
- Killing the last monster no longer triggers a flee: when the killing blow drops HP into flee territory but empties the room, the client now stays and rests instead of running from nothing (and a fresh monster entering while you're still low re-triggers the flee)
- Auto-search now holds the walker in a cleared room long enough to search it in place, instead of a zero-dwell loop stepping out before the search fires
- Auto-sneak now re-establishes sneak before leaving a room it just cleared of hostiles — the fight spends your sneak, and the client no longer treats the stale state as still-sneaking
- Old Mother Woodard (monster #545) now defaults to Friend on stock realms (she was missing from the stock overlay seed, so auto-combat treated her as an enemy); Paradigm already had her as Friend
- Picklock doors now open and the walk continues: the live "You successfully unlocked the door." / "You open the door." wording is now recognized, so a picked door no longer strands the walker waiting to send `open`
- Resting now starts the instant a room clears of hostiles instead of stalling several seconds — when a fresh monster interrupts a rest, the out-of-combat transition no longer sees a stale "hostile present" and re-sends `rest` immediately
- Combat no longer walks off mid-fight: on realms whose melee prints no damage number or gets armour-deflected, the mob's per-round swings now register as combat activity, so the idle-stall watchdog stops mistaking an active fight for a stuck-gate empty room and abandoning it
- Auto-collect no longer grabs cash on room entry before combat starts: with "collect after combat" on, a room whose "Also here" hostiles reveal a beat after the floor-cash line now holds the collect until the room is cleared, instead of firing a `get` into a fight
- Party reconnect: when a member re-enters the realm and you haven't moved, the client now sends a carriage return to re-observe the room and auto-invites them if they're standing there — only falling back to the `@where` round-trip if they re-entered elsewhere
- After death, the client sends a carriage return to re-observe the graveyard if the respawn room hasn't shown up on its own, so your position is re-established promptly instead of sitting "lost" until you move
- Death log detail now shows why a recovery is still "Partial" — the pile items that weren't seen picked up — instead of leaving the status unexplained
- `@equip-all` now triggers the "Equip All" action (applies your Default gear set), instead of hunting for a gear set literally named "all"
- "Get All" now works even when its floor cache is stale (e.g. loot dropped mid-combat): an empty cache re-surveys the room and grabs on the fresh survey, instead of reporting nothing on the ground
- A disconnect now suspends the party `par` poll and @health telepaths until you're back in the realm, so they no longer fire into the BBS login menu and derail re-entry
- bug reports addressed: paradigm-20260729-194421, paradigm-20260729-210839, paradigm-20260729-211336, paradigm-20260729-221044, paradigm-20260730-124104, paradigm-20260730-125716, stock-20260730-130949, stock-20260730-150957, stock-20260730-151145, stock-20260730-160706, paradigm-20260730-163244, stock-20260730-163044, stock-20260730-182812, stock-20260730-184622, stock-20260730-190736, stock-20260730-193107, stock-20260730-193610, stock-20260730-194053, stock-20260730-214157, stock-20260730-214959, stock-20260730-215247, stock-20260731-004105

## 2.11.0

- Navigation can now climb the Great Pyramid puzzle: walking to a pyramid room drives the party leader up all five floors (push-blocks, sphinx keywords, timed/chaos/door/footpath floors) to the top room, stopping there for the player to finish
- Pyramid climb pre-flights the floor-1 timer against the leader's encumbrance/speed and refuses a run that would scatter; a mid-climb scatter halts and reports
- Pyramid climb waits out combat and undead-priest holds on the paced floors (3–5), forces the golden lion key to the leader, and paces floor 4 slower for reaction time
- Pyramid floating key now defaults to enemy so party auto-combat clears it for its golden lion key
- Game-data messages carrying a Response now auto-send it (desert heat → "use water"); removed the no-op waterskin message
- Settings → Other gains master on/off toggles for the Great Pyramid climb solver and the asylum (random-teleport maze) solver, both default on
- Fixed a word-wrapped inventory "keys" line stranding "key" onto the last carried item (e.g. "3 waterskin key" instead of "large iron key")
- bug reports addressed: paradigm-20260729-165133

## 2.10.0

- Game Data → Items filter now recognizes flag keywords — type "collect" (or discard / open / buy / sell / stash) to show only items with that auto-flag set, hiding the rest
- Player Workshop → Quest Status gains a quest-name search box (far left of the "Edit Quests…" row) to narrow the quest list as you type

## 2.9.0

- Toolbar auto-combat icon is now a sword and auto-nuke a fireball
- Picking a recent destination from the Navigation "Go to…" history closes the flyout immediately instead of lingering until you click elsewhere
- A confusion wear-off now clears every source of confusion at once, so a spell's wear-off also releases the co-latched "you fumble in confusion" state — navigation no longer stays stuck "confused" after the confuse ends
- Redundant levers that open the same gate (identical command, no explicit action count) are pulled once en route instead of both
- Desert-heat rooms gated by the `failspell` buff directive are now recognized as hazards, so the walker raises the waterskin buff (`use waterskin`) before crossing instead of walking in unprotected — and carrying a sunstone wristband (full desert immunity) clears the route with no waterskin needed
- bug reports addressed: paradigm-20260728-173036, paradigm-20260728-180730, paradigm-20260728-180815, paradigm-20260728-201619

## 2.8.0

- New File-menu "Auto-load last profile on startup" toggle: when on, launch reopens the profile you loaded last instead of a blank draft (falls back to blank if that profile was since deleted); off by default, preserving the current blank-draft start

## 2.7.1

- Auto-search now waits until a room is clear of hostiles before searching, instead of firing a `sea` mid-combat where it was lost; it then holds briefly so revealed items are collected before the loop sneaks and moves on (empty rooms still search on entry)
- bug reports addressed: paradigm-20260727-185836

## 2.7.0

- Walking to a loop's start now shows the full walk-to readout (step X of Y, remaining, ~ETA) instead of a bare "Walking to … then looping …", switching to the loop's step/lap state once it starts cycling

## 2.6.0

- Web links (http / https) in the Conversation window are now clickable — a click opens the URL in your OS default browser; surrounding text stays selectable and trailing sentence punctuation isn't swallowed
- Conversation gossip channel chip shortened from "GOSS" to "GOS"

## 2.5.0

- Navigation overlay toggles (lairs / shops / spells / legend) collapse into one "Overlays ▾" flyout to reclaim toolbar room
- Lairs overlay gains a "count" mode that labels each lair room with its max monster spawn (a 3-spawn room shows a 3), cycling Uniform → Heat → Count → Off
- Search box now arms a "Go to…" button on a resolved match (Enter or single result), and clicking it drops a per-character history of your last 10 destinations
- Loops / Auto-Lairs list gains a filter box above the folders that live-narrows loops and lairs as you type
- Room search is more forgiving (whitespace/punctuation-insensitive, any-order word tokens), ranks literal word matches ahead of buried substrings ("aged" surfaces aged titan before Ravaged Farm), and now finds unique "max 1" monsters like aged titan
- Right-click menu no longer goes stale after deleting a room's favorite from the GOTO list — it now tracks external favorite / avoid / stash changes, so "Remove from favorites" stops mistakenly re-adding

## 2.4.0

- New "Unobtainable" table in the Game Data Browser lists the Items the game marks out of play (In Game = 0 — "bow of silver", placeholders, duplicate test rows) that the Item Finder skips, so they're inspectable instead of just hidden
- New "Quest Flags" table shows every quest-flag reference in the set's TBInfo — the flag, whether a block grants / gates / advances / clears it, and the NPC / room / spell that reaches it (resolved via the block's Called-From provenance)
- Both are computed live from the loaded game-data set, so they appear on every set without re-importing

## 2.3.0

- New "HP/MA History" graph in Session Stats: a per-loop-step range bar (HP red, mana cyan) showing the min/max your vitals hit at each step, with an average trend line threaded through so you can read your HP trajectory around the circuit at a glance
- Each step's range + average accumulate across laps; records only while a loop is stepping and resets at each loop start; toggle it from the Session Stats context menu like the kills/hr and exp/hr graphs
- Shows 15 steps at a time with a slider to pan a longer loop from step 1 to the tail; an in-graph readout names the centred step, and dragging the slider drops a cursor line onto it so you know exactly which step you're viewing
- The graph's vertical axis auto-floors 15 points below your lowest value (top stays 100%) to spread the plot, and the legend names each series' lowest % seen
- Session Stats window now sizes its height to its visible content, so showing/hiding graphs and sections no longer leaves a gap or needs a manual resize

## 2.2.0

- New @inv remote command reports the carried pack plus key ring — the inventory another player can't see by looking (worn/wielded gear and a readied light, which they can see, are excluded); gated by the Query inventory permission
- Inventory parse no longer registers a phantom "nothing" item from the "You are carrying nothing." dump line (it had leaked into @have / @drop-all)

## 2.1.0

- Character Workshop death list gains a "How did I Die?" button: each recorded death captures its backscroll to a per-character log, so a death that scrolls off the live buffer overnight stays reviewable
- @status reply overhauled — reports current activity and sub-state (idle / walking / looping / auto-lair, plus fighting / fleeing / resting), the room name with map/room numbers, walk ETA (steps left, or a countdown to the destination port when sailing), and active ailments

## 2.0.0

- Loop mode no longer walks past a hostile that leaps into an apparently-empty room: a combat line arriving while the room view shows no target now holds the walker for a beat so the mob reveals and the fight engages, instead of firing the next move mid-combat (the lit-room twin of 1.99.1's dark-room fix)
- Loop mode no longer bails off a freshly-engaged monster a beat before it dies: the "exp + Combat Off" fallback death no longer misfires on a prior kill's still-recent experience when a thrown-weapon Combat Off cycles mid-fight (a swarm of identical-exp mobs kept the last kill's exp inside the window), which had dropped the live target and walked a room early even though the mob hadn't died yet
- bug reports addressed: paradigm-20260723-205235, paradigm-20260723-213657, paradigm-20260723-230838, paradigm-20260723-231046, paradigm-20260723-231759

## 1.100.0

- Walk-to route picker now offers walk-vs-teleport: when a route could teleport but a walking route also exists, you choose — walk the safe long way, or take the much shorter teleport (which can drop you somewhere lethal, a call only your character can make), mirroring the existing acquire-item-vs-detour choice
- A blocked walk-to now names the obstacle on the route the character would actually take — a gate key or required item you must fetch — instead of a shorter level-gated backdoor it was never going to use
- Walk-to route ETA now shows in the Navigation window header ("~Nm Ss to arrive"), not just the program log
- Walk-to now auto-collects a free gate item en route when a giver hands it over on a single fail-proof command (an NPC keyword ask or a room command): the walk detours to the nearest giver, asks, collects, and resumes — no buying or hunting, and preferred over both when it applies
- Route picker names such a giver as "(ask <giver>)", taking precedence over the "(buy at …)" / "(dropped by …)" tails
- Walk-to whose only route is locked behind a door key (or an item you didn't flag for auto-fetch) now surfaces the picker naming that gate, then walks to the door and halts for you to clear it — instead of silently walking a route that dead-ends at the lock
- Post-kill loot re-survey sends a bare Enter instead of `look`, re-rendering ground drops without the room-description text
- bug reports addressed: paradigm-20260723-151900, paradigm-20260723-162143, paradigm-20260723-180918

## 1.99.2

- Party @level probe no longer telepaths members who are only invited: an invited-not-joined row is excluded from the level roster, bounds estimate, and stale-warm scan, and the probe re-fires the moment they accept and join
- bug reports addressed: paradigm-20260722-183942

## 1.99.1

- Dark-room movement no longer double-steps past lairs or drags monsters onward: combat's "where am I" CR refreshes (and the idle-stall resync CR) are suppressed while we can't see, so a blind refresh can't false-confirm the loop's in-flight step and collapse the dark-room settle window
- bug reports addressed: paradigm-20260722-233052

## 1.99.0

- Background memory hygiene: after a game-data set loads, the client compacts the large-object-heap fragmentation left by the JSON import (~125MB reclaimed), then periodically returns free native pages to the OS so a days-long loop-mode session no longer holds a working set far above its live heap
- Timed to stay unnoticed — the one stop-the-world compaction piggybacks on world-load, and the periodic native trim never suspends the UI or competes with a live combat round

## 1.98.0

- Last-known encumbrance now persists per character (saved on profile write, restored on load), so a fresh session starts with the real carry-weight bracket instead of Unknown
- Travel-cost estimates and hop-timing calibration tag the correct bracket from session start rather than waiting on the connect-time `i` — which never fires on a manual login or a hangup-suppressed relog

## 1.97.0

- Dark-room hunt diagnostics: the settle-gate log now reports whether each window ended on a revealed pursuer (with the reveal-lag in ms) or expired empty, so a capture shows the true reveal-lag distribution and the per-room cost of an empty dark room
- Combat idle-stall watchdog log now names how long the gate was held, how long it sat idle, and what last counted as activity — distinguishing a post-kill idle release from an unmatched-attack-line pattern gap

## 1.96.2

- "leave party" typed mid-loop no longer stalls a dark walk: the client stops misreading the phrase as a text-exit move that jammed the room-tracker's pending queue after a single step
- bug reports addressed: paradigm-20260722-111523

## 1.96.1

- Dark-room roster no longer accumulates: each dark advance resets the occupant list (keeping only the mob we're actively fighting), so pursuit arrivals stop piling into hundreds of phantoms that blocked engagement and stalled the loop 30s a room
- bug reports addressed: paradigm-20260722-104504

## 1.96.0

- New Session Stats → Players Seen window: logs every player spotted (also-here match, walk-in, or a failed sneak you notice) with timestamp, where (room name + map/room), who, and total times seen
- Players Seen data is per-character and persists on the profile; a Clear history button wipes it
- Transaction history "Time" column is now "Timestamp" and shows the date too (ddMMMyy HH:mm:ss)
- Quest seed: Phoenix Feather guide adds an optional "buy rafts for you + party members" step before `use potion`
- Dark-room hunting now settles a short beat after each move so monsters that reveal on entry get engaged, instead of the loop racing past the fight and double-firing moves
- bug reports addressed: paradigm-20260722-024841, paradigm-20260722-024915

## 1.95.0

- New default keybinds: Alt+H connect/disconnect, Alt+M navigation, Alt+V/B/N movement start/pause/stop, Alt+L backscroll, Alt+S capture, Alt+C conversation; F1 workshop, F2 spell book, F3 game data, F4 program log, F5 wire inspector (profile + quit shortcuts unchanged)
- Party, Session Stats, and Settings have no default shortcut now — F3 opens the Game Data Browser and the function keys are reserved for the editor/browser windows; all three stay on the toolbar/menu and a chord can be assigned in Settings → Shortcuts
- New default toolbar: connect, disable hangups | navigation, movement start/pause/stop | party, backscroll | all-auto plus auto combat/nuke/heal-rest/bless/get-items/get-cash/sneak
- Default auto engines now boot on: combat, nuke, heal/rest, bless, get-items, get-cash, sneak (light/hide/search default off)
- Existing profiles are migrated to the new keybinds + toolbar layout on next load; their auto-mode settings are left untouched
- Game Data Browser hotkey (F3) now toggles the window closed on re-press, matching every other panel
- "Manage" window renamed to "Navigation Management"; double-clicking a loop / Auto-Lair now runs it (was edit)
- Navigation Management rows reordered to Run · Load · Edit · Delete, and Auto-Lair setups gain Run / Load actions (were rail-only)

## 1.94.0

- Item Finder reorders columns to the item kind it's showing: an all-armour view leads slot, name, type, level, enc, ac, dr; an all-weapon view leads name, type, level, str, dmg, swings, hit magic, enc
- Item Finder hides the Slot column in an all-weapon view (every weapon shares the Weapon slot)
- Item Finder's Swings (W. Spd) column now shows the weapon's raw speed alongside the modelled swing count, e.g. "2.3 (30)"
- Navigation window reopens in the collapse state you left it — fixes the side-panel toggle resetting to expanded and clamping the saved (narrower) window size back to the expanded minimum

## 1.93.0

- Dark-room auto-combat now follows a party leader: a "moves to attack <monster>" announce injects that monster so we engage the same round instead of idling until it swings at us
- On Paradigm, an @where sent while our position is unknown now fires `rm` to re-fix our location from the game, then replies with the real room instead of "Location unknown"
- Realm Rankings no longer prunes the board when you view a smaller list: the table merges the most-recent reading per hero, so a "top 10" of a captured "top 100" refreshes the leaders without dropping ranks 11+
- A view is only treated as a cap when a numbered request comes back short (asked top 100, got 10 = capped at 10); choosing to display fewer retains the rest
- The widest real board is pinned in capture history so a run of small views can't evict it
- Reconnect auto-rejoin now fires @comeback on the first in-game prompt instead of the first room display, so a dark room can't defer it into a spurious @comeback when a light later reveals the room
- bug reports addressed: paradigm-20260721-162758, paradigm-20260721-192342, paradigm-20260721-194551, paradigm-20260721-203734

## 1.91.0

- Auto-Lair travel cost gains a realm-aware "Automatic" mode, now the default: ParaMUD runs the game's movement-speed formula against live carry weight + worn quickness; stock realms use the measured encumbrance hop-time table
- Stock encumbrance hop times reseeded to measured two-band timings — ~0.7 s (None/Light/Medium), ~1.7 s (Heavy/Encumbered)
- Movement Speed calculator is realm-gated: ParaMUD keeps the interactive solver, stock shows a static two-band findings card
- Walk-to status now reads "step X of Y, N steps / ~Ts to arrive" — remaining-route ETA surfaced in the navigation label and program log
- ETA sums the per-hop travel estimate and, when auto-combat is on, a 5 s/monster dwell for every lair room crossed
- Bug report captures the active travel-cost model and its per-hop estimate

## 1.90.0

- New "Realm Rankings" calculator in the Workshop Calculators tab: captures the realm's `top N` heroes off the terminal and adds a derived XP/HR column
- Captures are stored per-BBS, so every character on the same board feeds and reads one shared history
- Purely player-driven — run `top <N>` in-game (or press "Parse Toplist") and the block is snapshotted passively; no auto-polling
- A re-capture with no experience or roster change is discarded, so the history keeps only captures that move the needle
- XP/HR is measured against the most recent prior capture that actually changed for that hero — an idle reading is skipped and the rate reaches further back
- Each XP/HR carries a trend arrow: green ▲ when the grind sped up versus the previous interval, red ▼ when it slowed
- Each rank shows its movement since the last capture — green (+N) climbing toward #1, red (-N) sliding down
- Rate is figured by first name (last names change, first names don't); a class change or an experience drop is flagged as a likely reroll in the program log
- List-cap aware: a departed name is only flagged as gone when the board is complete or its last-known exp cleared the shown cutoff, so an overtake at the bottom of a capped board isn't mistaken for a reroll
- History is capped at the 5 most recent captures; the oldest fall off
- Table columns are sortable largest-first (one click puts the top scores on top, a re-click flips it) and auto-size to their widest value with centered headers, in the in-game color scheme (green Rank/Experience, gold ranks, cyan names, magenta class, grey guild) on a black background
- Class Filter dropdown narrows the table to a single class off the loaded game data; "No Filter" (the default) shows every hero
- Sea-captain docks now surface a right-click "Use Teleport" item to their arrival port, matching the map's teleport glyph
- bug reports addressed: stock-20260720-230037

## 1.89.1

- Auto-collect now grabs an entire stacked ground pile: a survey count ("You notice 5 piece of amber here") sends one `get` per unit instead of one item per room re-display
- The MaxToGet cap and encumbrance ceiling still apply per unit, so a stack is taken only up to the cap or carry limit
- A stacked item whose name starts with a denomination word ("2 gold key") is no longer mistaken for coin: the item table settles the cash-vs-item call, so it's looted instead of skipped as cash
- bug reports addressed: paradigm-20260720-153216

## 1.89.0

- Navigation can now route through sea-captain boats: a walk whose goal is cheaper (or only) reachable by a `secure passage` sailing walks to the dock, sails, then walks from the arrival port
- Boat sailings are discovered from each realm's room-command data (never hardcoded), gated on every party member's level and copper fare
- The sail is one party-split step (same relay as a chime teleport); the voyage fails out if the captain refuses boarding an under-level, too-poor, or un-attuned member
- A sea-captain dock now shows the teleport glyph on the map — a `secure passage` is a delayed teleport to a distant shore
- A boat that's the only crossing is no longer hidden behind a bare "no path" when a member can't cover its level or fare: the walker plans it anyway and warns the captain may leave a member behind
- The sail is timed from its transit-spell rounds; while sailing, the Navigation top bar reads "Sailing the high seas, reaching <place> in mm:ss" and reverts to normal walk status once you land

## 1.88.0

- Party-impassable level-gate routing is now always-on (the opt-in Settings → Other checkbox is gone); a following party still routes around a `(Level: MIN to MAX)` gate it can't clear rather than stranding a member
- A member's exact level (from `@level`) is now timestamped; readings older than 24h are treated as stale
- When a planned walk actually crosses a level gate, `@level` is re-probed for any unknown or stale member (route-scoped + debounced, mirroring the toll `@wealth` warm)
- Bug report now lists each party member's level estimate (exact + age, or title band) and the folded party level window

## 1.87.0

- Settings → Cash + Items "Keep on hand" is now a single raw wealth value (copper farthings) instead of five per-currency fields
- Depositing sends the excess as `dep <copper>`; stashing decomposes it into lowest-denomination-first `hide N <coin>` commands, leaving the fewest coins on hand
- Settings → Health: HP and Mana / Kai thresholds stack vertically so the tab no longer opens with a horizontal scrollbar
- Settings → Health: pre/post-rest command boxes are left-aligned at a sensible width instead of stretched to the pane's right edge

## 1.86.0

- Settings → General "Default task" enabled: pick Begin looping / Begin Auto-Lair to auto-start a saved loop or lair on game entry (Do nothing keeps today's idle behaviour)
- Startup task routes to the nearest waypoint first, just like Run in the Navigation window; starts even with all auto-engines off
- On a reconnect where you had a party, the startup task waits your "if leading, wait" window first so the party can reform
- Loop / Auto-Lair pickers list the active game-data set's saved names and keep a saved reference even if it isn't in the current set

## 1.85.0

- Settings → General: Auto-Train now listed in the "Auto-Engines enabled on start" section (mirrors the Auto-Trainer tab's master toggle)
- Settings → General: Auto-Train added to the "Re-enable on reconnect" section

## 1.84.0

- Settings → Talk: new "Look back when a player looks at us" toggle — mirrors a `look` back when the game shows "&lt;name&gt; is looking at you." (skips self, includes party)
- Settings → Talk: new "Look at players to learn/update their inventories" toggle — looks at each non-party player who walks into the room (skips self and party)
- Both default off, sit next to "Greet players when first met"
- Settings → Talk: removed two redundant help-text captions (master kill-switch, @party disable)
- Action menu "Auto Search" toggle now enabled and wired to the live engine (was stuck disabled with a stale tooltip); separator above Auto Train removed

## 1.83.0

- Navigation window gains a collapse/expand toggle beside the search box (▶ collapse the panels / ◀ bring them back)
- Collapsing hides the search box, Selected-Room readout, action buttons, the display-toggle chips (Lairs/Shops/Spells/Legend), and the whole right-hand nav rail, and lowers the window's minimum width and height so the map can be dragged smaller

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
