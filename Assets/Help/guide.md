# Getting Started

New to MudPlay? Here's the short path from launch to playing — and where the rest of this help lives.

## What MudPlay is

A Telnet terminal client for **MajorMUD / MegaMUD**-style BBS door games. It renders a faithful CP437/ANSI terminal and layers a large, tunable automation suite on top — auto-combat, healing, spellcasting, navigation and looping, party coordination, and coin/item collection. Play it as a plain terminal, or turn on as much automation as you like.

## Connecting to a BBS

Two ways to connect:

- **Quick Connect** (one-off) — **File → Quick Connect…**, type the board's host (name or IP) and port, and click **Connect**. Nothing is saved; it's the fastest way to try a board.
- **A saved profile** (persistent) — set up a character profile so your login, macros, settings, and the board's details are remembered and reconnect on their own. This is how you'll normally play (see below).

## Profiles

A **profile** is one character's workspace — its BBS login, macros, triggers, equipment sets, favorites, quest state, and every per-character setting. One profile is loaded at a time.

- **New profile** (Ctrl+N) starts a blank draft; set up its BBS + credentials (below), then **Save** (Ctrl+S) to name it.
- **Open profile** (Ctrl+O) loads a saved one.
- **Auto-load last profile** (Settings → General) reopens the profile you used last on every launch.

Settings live in four tiers — **Defaults → Global → BBS → Character** — so a profile only records what differs from the tier beneath it. (The Settings Menu section notes each setting's tier.)

## Setting up a BBS

In **Settings → BBS + Display**, fill in the board's **name**, **host**, and **port**, your **username / password**, and — if the board needs it — the **automated logon** steps that walk you from the BBS menu into the game. Reconnect behavior and terminal size live here too. These are **BBS-tier**: shared by every character on that board.

Each logon step is a **Message** to wait for and a **Response** to send when it appears (with `{username}` / `{password}` tokens for your saved credentials). Add **only steps that LOG YOU IN** — never a log-out or quit step (e.g. a *"Are you sure you want to log off? (Y/N)"* confirmation, a common MegaMUD holdover). That prompt never appears on the login path, so a logout step just sits there unmatched and stalls the sequence. You don't need a final "enter the realm" step either: once your steps reach the game's entry menu, MudPlay sends the entry command for you — and it does so even if your steps don't perfectly reach the end, so an automatic reconnect after a drop still lands you back in the game. (The one time it won't auto-enter is right after you hang up on purpose — a manual `@hangup` or a hang-up-on-low-HP / hang-up-when-naked rule — so you can read the screen and enter manually.)

With that saved, **Connect** (Alt+H, or File → Connect) and MudPlay logs you in.

## Playing, and turning on automation

In the game, the terminal works like any MUD client — type a command and it's sent to the game. The **numpad is pre-wired to compass movement**, and you can add your own **macros** (key → command) and **aliases** (typed shortcuts). The startup splash plays until you connect or load a profile.

The automation engines — Auto-Combat, Auto-Heal, Auto-Nuke, navigation looping, and more — are **toolbar toggles** whose behavior is tuned on the matching Settings tabs. Flip them on to let MudPlay fight, heal, and travel for you. The **Combat**, **Navigation & Looping**, **Party Play**, and **Healing & Spells** sections explain how each engine decides what to do.

---

# The Interface

The **terminal** is the center of MudPlay — everything the game sends, rendered as a CP437/ANSI screen, and where everything you type is sent. Around it, every other panel is a **modeless window**: open it from the **View** menu, a **toolbar** icon, its **hotkey**, or the **terminal's right-click menu**, and press that same control again to close it. The terminal always stays live while you configure or check anything.

## The terminal and status bar

Type, and your keystrokes go straight to the game. The **numpad** is pre-wired to compass movement out of the box. **Paste** with **Ctrl+V** or **Shift+Insert** — a single line drops onto your input, and a multi-line paste is sent as one command per line. **Right-click the terminal** for a quick menu: your starred GOTO **Favorites** and **Recent destinations** (the last 10 places you walked — click either to walk there), quick-opens for Backscroll / Player Workshop / Party / Spell Book / Conversation / Navigation / Session Stats, **Reset States** (the recovery escape hatch — see Automation), and **Bug report…**.

The status bar along the bottom packs several live readouts:

- The **connection light** — **red** idle · **yellow** connecting · **green** connected (a reconnect countdown shows beside it while reconnecting). It's just the dot; hover it for the state text.
- An **engine-state badge** mirroring the Navigation one — **IDLE / WALKING / LOOPING / AUTO-LAIR** — whose border turns **yellow** then **red** as the engine-recovery gate escalates.
- Your **location** (the map/room key), the session's **exp/hr** rate, and **- TNL:** — the estimated time to next level at that rate.
- A **TGT HP:** readout that appears after you `look <monster>` — a coarse wound band × the monster's max HP, so you get an absolute HP range (invaluable on fast-regen bosses). The same estimate is also printed as a yellow line in the terminal. A **Settings → Other → "Show monster HP lookup"** checkbox (default on) toggles both. The max HP is read from the monster record **placed or summoned in your current room**, so a display name shared across zones (an "orc lieutenant" in the barracks vs the slums) resolves to the one you're actually fighting.
- **Tick countdowns** — the combat round tick, the natural HP-regen tick, and the mana / meditate tick.

## The toolbar and menus

A customizable **toolbar** of icon buttons sits under the menu bar. The full bar is **File · View · Action · Game Data · Tools · Help · Bug Report** — **Action** is your in-play on/off surface for the auto-engines and the manual one-shots (see **Automation**), **Game Data** switches imported data sets, and **Bug Report** captures client state to a file on your Desktop. You choose which toolbar buttons appear — and rebind every shortcut — in **Settings → Toolbar + Shortcuts**.

## The windows

Each is modeless and toggles closed on its own key. Default hotkeys are shown; all are rebindable.

- **Navigation** (Alt+M) — the room map: where you are, your route lines, and the controls for GOTO, loops, and Auto-Lair.
- **Backscroll** (Alt+L) — scroll back through terminal history, with search and export. See **Tools & Diagnostics** for how to use it.
- **Conversation** (Alt+C) — chat, gossip, and telepaths collected in one window with their own input box, per-channel colors, and optional logging. See the **Conversation** section for how to use it.
- **Party** (no default hotkey — View → Party, a toolbar button, or right-click → Open Party) — your live view of the party: each member's rank, health, status, and an uninvite button. See the **Party Play** section for how to use it.
- **Program Log** (F4) — a running diagnostic of what the engines are doing; the first place to look when something automated didn't behave. See **Tools & Diagnostics** for its filters and toggles.
- **Player Workshop** (F1) — your gear sets and the Item Finder, CP allocation and level projection, quest log, boss timers, and death history. See the **Player Workshop** section for how to use it.
- **Game Data Browser** (F3) — the imported game-data tables (rooms, items, monsters, spells) you can browse and override per-character. See the **Game Data** section for how to use it.
- **Spell Book** (F2), **Session Stats**, and **Wire Inspector** (F5) round out the set — a read-only spell reference, session counters, and raw wire I/O for troubleshooting. The Spell Book is covered under **Healing & Spells**; Session Stats and the Wire Inspector under **Tools & Diagnostics**.

The **Settings** window follows the same modeless rule — the terminal stays interactive while it's open — and **OK / Apply / Cancel** decide whether your edits stick.

---

# Combat

How MudPlay fights for you once **Auto-Combat** is on (its toolbar toggle, or Settings → General). The knobs live on **Settings → Combat** and **Settings → Spells**; this is what the engine does with them.

## The round loop

Each combat round the engine picks one main action — **cast an attack spell** or **swing your weapon** — following your **Action order**:

- **Spells first** — try your attack spells; fall back to the weapon only when every spell fails to fire that round (out of mana, cast cap hit, target immune).
- **Physical first** — swing first; turn to spells only when the weapon is proven useless against this target.
- **Alternate** — flip the preferred action every round; a round whose preferred type can't fire falls back to the other, so no round is wasted.
- **Custom round cycle** — spend a set number of rounds swinging, then a set number casting, on repeat.

Two things always sit above that choice: a **backstab opener** fires first when eligible, and **debuff spells** are a separate extra action that can land the same round.

**Taking a round yourself.** If you hand-type an attack mid-fight — a **combat spell** (any spell that costs round energy, i.e. an attack, as opposed to a 0-energy heal/buff) or a **physical attack** (`a`/`at`/`att`/`aa`, `bash`/`sm`/`sma`/`smash`, `bs`) — the engine treats it as a **user override** and holds its own auto-attack for that round, so it won't fight you by re-sending its action on top of yours. Control returns automatically on the next combat round. A hand-cast **heal/buff/cure** (0 energy) is *not* an override — after it lands the engine resumes attacking right away, same as before.

## Targeting

When several hostiles share a room, **Target order** and **Target priority** decide who gets hit first — the highest-priority monster by default, or a "follow the party's target" mode. Per-monster priority is ranked in Game Data.

## Fighting a crowd

Against several enemies MudPlay uses your **multi-attack** and **area-debuff** spell slots to hit the whole room, falling back to single-target attacks once the room thins below a slot's minimum-enemies setting. Those room spells are gated by **Auto-Nuke**; single-target attack spells aren't "nukes" and stay available regardless.

## Backing off

If your health drops past the thresholds on **Settings → Health**, the engine can **run** instead of fighting to the death — breaking combat first (if set), then moving a configured distance in a chosen direction. Healing and fleeing are covered under **Healing & Spells**.

---

# Navigation & Looping

MudPlay walks you around the world — one-off trips, repeating circuits, and lair camping — all from the **Navigation window** (**Alt+M**, or View → Navigation), driven off the imported room map.

## The Navigation window

Three areas:

- A **top status bar** — an engine badge reading **IDLE / WALKING / LOOPING / AUTO-LAIR**, a plain-English status line, the **Go to…** button, and a **search box**.
- The **map** on the left.
- A **right rail** of collapsible panels: **ROOM INFO** (records for the last-clicked room — see below), **CURRENT NAV** (the live step list), **GOTO** (your favourites), **LOOPS + AUTO-LAIRS** (your saved circuits), and **EXP/HR ESTIMATOR** — with a **Navigation Management** button at the bottom for full editing.

A row of action chips — **Save**, **Run**, **Loop mode**, **Lair mode** — sits just above the map.

## Walking somewhere (GOTO)

To send your character to a room:

- **Search** — type a room name or a map/room key (e.g. `1/297`) in the top search box, pick the match, then click the green **Run** chip.
- **Right-click a room** on the map → **Walk here**.
- **Favourites** — save rooms you visit often (right-click a room → **Add to favorites**, or the Management dialog's **Go To** tab), then click one in the **GOTO** rail to walk there. **Right-click a Go To** in the rail for **Walk here**, **Edit…**, **Move to folder…**, an **Add to / Remove from favourites** toggle (stars it — the ★ that promotes it to the terminal's right-click **Favorites** flyout — without deleting it), and **Delete this Go To** (removes the saved location entirely).
Type **"favourite"** (or any 3+ character part of the word) into the GOTO or loop filter box to surface your starred Go Tos and favourited loops.

MudPlay plots the shortest route and walks it, opening doors, disarming traps, and revealing hidden exits along the way. Click the red **Stop** chip to stop, or the **Pause / Resume** chip to hold and continue.

If you **type a movement command yourself** while a walk, loop, or auto-lair is running — a direction (`n`, `sw`, …) or a text-exit step (`go path`) — navigation **pauses automatically** so the automation never fights your hand-driven step. It's a user pause, just like clicking **Pause**: press **Start** (Alt+V) when you're ready to hand control back. (Peeking with `l <dir>` doesn't count — that's a look, not a move.)

## Building and running a loop

A **loop** is a saved circuit of rooms MudPlay walks over and over, fighting and looting as it goes. To build one the quick way:

1. Click the **Loop mode** chip (it changes to **Building**).
2. **Left-click the rooms on the map, in order** — each becomes a waypoint. Reorder or remove them in the **CURRENT NAV** rail.
3. Click **Run** to save and start it (you'll name it), or **Save** to keep it without running.

Or build it off the map: **Navigation Management → New Loop** opens an editor where you add rooms by name or key, name and annotate the loop, and set per-waypoint options.

**Run a saved loop** from the **LOOPS + AUTO-LAIRS** rail (or the Management dialog) — each has **Load** (stage it) and **Run** (start now). Queue one and, if you aren't already there, MudPlay walks you to the loop's start, then begins the circuit; combat, healing, and pickup keep running throughout. While it runs the badge reads **LOOPING** with "step X of Y on lap Z" — **Pause** to edit mid-run, **Stop** to end.

**Right-click a loop or Auto-Lair setup** in the rail for **Load**, **Run**, **Edit…** (opens its editor), **Move to folder…**, and **Add / Remove from favourites** — favouriting a loop or lair adds it to *both* right-click Favorites flyouts (the terminal's and the map's, green for loops, amber for lairs) alongside your starred GOTO rooms, so you can start it from anywhere.

Each waypoint can carry its own **command and delay** (e.g. `rest`, `dep 100`, `ask barmaid pie`) and a **"Do not rest in this room"** flag, set from the waypoint's **✎** button. If a route crosses a locked gate or a hazard room, a **Choose a route** prompt lets you take the free way around or push through.

## Estimating a loop's exp/hour

The **EXP/HR ESTIMATOR** panel in the right rail projects how much experience a prospective circuit would earn per hour *before* you commit to it — factoring in boss respawn timers and room summon rates, not just a flat monster count. It simulates the loop's *actual room order* against each lair's per-mob respawn timer, so the **shape** of the circuit matters: an out-and-back line that re-crosses just-cleared lairs on the way back (walking through empty rooms while the respawn timer runs) reads lower than the same rooms walked as a ring, because those return steps waste combat time — which is exactly how it plays out in game. The **Seconds per room** tunable is the single biggest lever, and it's the one most easily set wrong: it's your **effective time per room *while looping and fighting*, not your raw walk speed**. Each room in a combat loop is a move command plus its server round-trip plus an attack plus the 5-second combat tick, so the real pace is ~**1.2–1.4s** per room even when your bare movespeed is 1.0 (which is why it defaults to 1.4). Set it to your raw movespeed and a tight loop that backtracks past just-cleared lairs will read noticeably high, because the model then under-charges the wasted ticks you spend walking those empty return rooms. The **real-world multiplier** (0–1) then scales the result down for the remaining friction the room-by-room model can't simulate — the odd late combat round, a missed pull, imperfect pacing; **0.9–0.95** is the usual band for clean, attentive play, lower for a distracted session. It's a discount from the modeled ceiling, not a fudge factor — set it to the fraction of the ideal pace you actually sustain. Click **Start estimating**, then **click the rooms** on the map to sketch the circuit; the panel shows a running **exp/hr** figure as you add rooms. From there, **Save as loop** turns the sketch into a real loop, **Load loop…** pulls an existing loop in to evaluate it, **Clear rooms** starts over, and **Stop Estimating** exits the mode. Use it to compare two hunting circuits without walking either one.

## Auto-Lair

**Auto-Lair** camps a monster's lair: travel there, wait out the respawn timer, enter to kill the spawn, then repeat. Mark lairs with the **Lair mode** chip (left-click the lair rooms, then **Save**), or build a setup in **Navigation Management → New Lair** (where you can override each lair's respawn timer). Start one from the **LOOPS + AUTO-LAIRS** rail's **Run** button — it cycles the marked lairs. Its routing heuristic and travel-cost model live in **Settings → Auto-Lair**.

## The map and obstacles

**Right-click any room** for its menu: **Favorites** and **Recent destinations** sub-lists at the top (the Favorites list holds your starred GOTO rooms *and* your favourited loops + auto-lairs — click a room to walk there, a loop or lair to start it — and Recent destinations walks to a recent GOTO target), then **Walk here**, **I am here** (re-anchor if the map loses track of you), **Save as Go To** (saves the room to your Go To list), **Use Teleport**, **Center on…**, and toggles to mark a room **Avoid** or **Stash**.

**Shift+right-click** skips the menu when a room's only jump is unambiguous — a room with just an up exit, just a down exit, or a single teleport destination immediately follows it (recentres the map there) instead of opening the menu.

**Left-click any room** to load it into the **ROOM INFO** rail panel. Clicking never forces the panel open — it just refreshes the panel's contents to the room you clicked, so expand ROOM INFO whenever you like and it shows the last room you clicked. It lists clickable links to everything attached to that room — the **room name** itself (click it to open the room's record), each **monster** that lairs, is summoned, or is placed there, each **floor item** the room scatter-places (via its `roomitem` command), the **shop** as a whole when the room hosts one, and the room's cast-on-enter **room spell**. Clicking a **monster** or the **room spell** opens its full record in a dialog (the same record you get from the Monsters / Spells browser tabs); the **shop** opens the room-detail popup — its stock table with buy/sell prices and the live Charm picker (the same popup the Shops browser tab opens); and the room / item links open that record in the **Game Data Browser**. Either way it's a quick jump from "what's in this room" to the full record without hunting through the browser's tables.

The **Overlays ▾** button layers lairs, shops, and spell rooms onto the map and toggles the **Legend** — which you can **drag anywhere on the map** (it remembers where you put it; toggle it off and back on and it snaps back into view if the window has since shrunk). Route lines are colour-coded — walk-to **blue**, a running loop **green**, a loop you're previewing **red**, an Auto-Lair approach **orange**. Exit stubs carry their own colours (shown in the Legend): **red** for a trapped exit, **magenta** "Action required" for an exit you can't just walk — one that needs a command or in-room action to cross (a `go path`-style named exit, a lever, or an ask-a-guard door), and **cyan** for a hidden exit revealed with `sea`.

Hovering a room shows its details, including the monsters that spawn there with their game-data record numbers (e.g. `Dark Goblin Archer(#48)`).

**Getting past obstacles.** En route, MudPlay handles closed and locked doors (key, pick, or bash), traps (search and disarm, or delegate to a capable party member), and hidden exits. It also routes through **NPC ask-transport** exits — a sealed room whose only way out is asking a resident NPC to port you elsewhere (the Floating Citadel's Grey Lord ports you to Town Square when asked) — sending the `ask <npc> <keyword>` for you, so those pockets are no longer dead-ends to the router. For an **action-gated** exit whose opener is a lever or switch in *another* room (the magenta "Action required" stubs), it drives a go-pull-return detour automatically — visiting each lever room on the way past, then crossing the primed exit. This works even when a lever alcove is itself behind **another** action-gated door: the walker opens each inner door first (walk in, pull, return) before crossing, so a multi-level lever vault is solved end-to-end. Only a very deep (4+ levels) or self-referential (levers that gate each other) puzzle is left unsolved — those fail cleanly at plan time (*"route needs an action-gated exit the walker can't auto-solve"*) and log the exit that stopped it, so you can drive that stretch by hand. It also crosses two further special exits: a **room-command reveal** — a hidden passage opened by typing a command *in the room itself* (e.g. `clear rubble` at a rubble-blocked entrance), which it sends before stepping through — and an **item-use teleport**, where *using* an item transports you across (e.g. `use potion of levitation` to reach an otherwise-unreachable area); it uses the item for you when your route crosses one. When a route is blocked *only* because you lack a required item for one of these gates — often a quest item that can't be auto-fetched — the walk fails with a message that **names the item to go obtain**, rather than a bare "no path".

A genuinely impassable obstacle halts the walk with a clear reason rather than looping on a door it can't open — and the reason **names the obstacle**: which room the door is in, the direction, and what it takes to pass (the key and/or the picklocks/strength), e.g. *"a locked door south from 10/218 (Frozen Cavern) — needs the glass key, or 61 picklocks/strength."* Door requirements are read per-direction, so a door's far side (which can differ — one way an "any" bash/pick door, the other a keyed one) is never mistaken for the way you're heading.

When the only route somewhere is fully blocked but you can still reach the obstacle, the route picker offers **"run to the blocked room anyway"** — it walks you as far as you can go and stops at the block, so you can clear it (open the door, fetch the key) by hand. Every blocked walk-to is also written to the program log.

**Marking a room Avoid** makes the pathfinder treat it as a wall — every route (GOTO, loops, Auto-Lair, auto-deposit, auto-train) plans around it. Toggling avoid on a room your **running loop doesn't pass through leaves the loop undisturbed** — it keeps circling without a restart. If a room *is* on the loop, the loop re-plans around it, keeping its session (no stats reset).

If an avoid ends up walling off your only route somewhere, MudPlay tells you which room is the culprit — a **GOTO** to a blocked destination reports *"only route is blocked by user set avoid in room (map/room)"*, while auto-deposit and auto-train quietly skip and log it rather than getting stuck.

---

# Party Play

MudPlay coordinates multi-character parties — following a leader, healing each other, and taking remote `@`-commands from party members.

## The Party window

Open it from **View → Party**, a toolbar button, or **right-click the terminal → Open Party** (it has no default hotkey — you can assign one in Settings → Toolbar + Shortcuts). It's your live roster: one row per member, updated as their health and status broadcasts arrive. Each row shows —

- a **★** on the party leader;
- a colour-coded **rank chip** — **F** front, **M** mid, **B** back — the member's combat rank;
- the member's **name and class**, and **HP / MA bars**;
- **status chips** that light up as conditions apply — **REST** resting · **MED** meditating · **BLD** blinded · **PSN** poisoned · **DIS** diseased · **CNF** confused · **HELD** held · **WAIT** waiting · **INVITED** invite pending;
- an **uninvite (⨯)** button — active only when *you* lead — that kicks a follower or withdraws a pending invitation.

The healing, ranks, nags, and re-invite behaviour the window reflects are all configured on **Settings → Party**.

## Leaders and followers

One character leads; the rest follow. A follower tracks the leader's movement and holds position; if the leader disconnects, the party disbands. A party is 2–6 characters.

## Party healing

With party heal spells configured (Settings → Party), members watch each other's health broadcasts and heal whoever drops below the minor/major thresholds — single-target, or an area heal once enough members qualify.

## Remote @-commands

Party members can drive each other with `@`-commands sent over chat. Commands are accepted on three channels — **telepath**, **gangpath**, and **say (local)** — and the reply always comes back on the same channel it arrived on. (Gossip, yell, and broadcast are ignored for `@`-commands; there's no separate "page" channel — pages count as telepaths.)

**What's allowed** is gated per character. Every remote command belongs to a permission *category* (query health, move me, alter settings, execute commands, and so on), and you grant those categories per player in **Game Data Browser → Players** — the edit dialog's permission grid, where the high-trust ones sit under "Elevated Commands." A never-seen player has no grants, so their commands are refused.

On top of that, **Settings → Talk** has master and per-channel kill switches (disallow all remote control, or mute telepath / gangpath / say), a separate gate for `@party` directives, and a "warn on invalid/denied command" toggle that decides whether a refused command replies or stays silent. An @-word that matches no command at all — someone just typing "@because" in chat — is always ignored silently regardless of that toggle; it only governs a *recognized* command that's denied (a permission the sender lacks, the `@party` whitelist, the suicide policy).

Active party members get a few things for free regardless of the grid: the party-coordination signals, the health queries (`@health` / `@status` / `@lives`), `@reset`, and a bare `@party` status check.

### Query commands — they report; nothing changes

| Command | Args | Replies with |
|---|---|---|
| `@version` | — | the app name + version |
| `@help` | — | the commands *that sender* is allowed to use |
| `@health` | — | HP / MA / Kai and resting-or-meditating state |
| `@status` | — | what you're doing (walking / looping / fighting / resting), your room, and any ailments |
| `@lives` | — | lives remaining |
| `@exp` | — | session exp earned, exp/hour, and time-to-level |
| `@level` | — | level, current exp, and exp to next |
| `@where` | — | room name, map/room, and exits |
| `@path` | — | the movement engine's activity and step progress; when stopped/idle, names the last loop or auto-lair that was run (so you can help a dead player resume their circuit) |
| `@who` | — | other players / monsters in your room |
| `@timer` | — or `<name>` | boss respawn timers (all, or matching a name) |
| `@death` | — or `all` | unrecovered deaths from the recovery log — the most recent one, or `all` of them (each with when, status, room, and lives left) so you can help a dead player recover; own permission ("Query deaths") |
| `@what` | — | items on the room floor |
| `@wealth` | — | your coins and total value |
| `@enc` | — | encumbrance |
| `@have` | `<item>` | whether you carry or wear a matching item |
| `@inv` | — | your carried pack and keys |

### Move me around

| Command | Args | Does |
|---|---|---|
| `@goto` | `<destination>` | walks you to a saved GOTO favorite, a searched room (coords / name / acronym), or a boss |
| `@loop` | `<name>` or ≥2 coords | starts a saved loop, or an ad-hoc coordinate loop |
| `@lair` | `<name>` or coords | starts an Auto-Lair setup |
| `@stop` | — | pauses your movement |
| `@rego` | — | resumes it |

### Change my settings

- The auto-engine toggles — `@auto-combat`, `@auto-nuke`, `@auto-heal` (`@auto-rest` is the same flag), `@auto-bless`, `@auto-light`, `@auto-cash`, `@auto-get`, `@auto-sneak`, `@auto-hide`, `@auto-search` — each flips that engine (bare toggles it; add `on` or `off` to force it).
- `@auto-all` — the kill switch: `off` stops every engine, `on` restores what was running. `@settings` — reports every engine's on/off state.
- `@atkprio` — Target Priority: bare reports it; `1` Default, `2` follow-leader, `3 <name>` attack-what-player.
- `@atkorder` — Attack Order: bare reports it; `1` Default, `2` last-party, `3` last-room, `4 <name>` attack-after.
- `@divert <player>` — forwards your incoming telepaths to another player; bare `@divert` stops.
- `@reset` — zeroes your Session Stats counters.

### Do something on my behalf

- `@do <command>` — sends the command verbatim to the game (the highest-trust command).
- `@kill <target>` — retargets your combat onto the named monster this round.
- `@heal` — asks a configured party healer to heal whoever's low (only a healer responds).
- `@trap <dir>` — search and disarm a trap in that direction; `@trap stop` aborts.
- `@train` — trains (and applies your CP plan, if Auto-train-stats is on) — assumes you're already at a trainer.
- `@equip-<set>` — wears one of your saved gear sets by keyword (e.g. `@equip-backstab`; `@equip-all` applies the Default set).
- `@get-all` / `@drop-all` / `@deposit-all` — pick up everything on the ground / drop everything unworn / bank all excess coin.
- `@invite` / `@join` — ask you to invite the sender into your party, or to join theirs.

### Party coordination — any active party member, no grant needed

- `@wait` — hold: automation pauses until you `@ok` (which releases it).
- `@comeback` (optionally `<map/room>`) — a stranded member asks the party to come recover them; `@forget` calls that recovery off.
- `@share` — splits your held coin evenly across the party.
- `@party` — bare, it reports whether you're solo / following / leading. Sent on **say** *with* arguments, it relays whatever follows verbatim to your character as if you typed it (the party version of `@do`) — `@party rest`, `@party use chime`, and so on. The directive form only works on the say channel, and Settings → Talk can disallow it.

### Irreversible and always-blocked

- `@suicide` — forces your character's death, using the suicide password MudPlay captured from your in-game `set suicide`. It's an **Elevated Command**, and Settings → Other blocks it when your remaining lives are at or below your threshold.
- A few things are **always refused, silently, no matter what's granted**: anything containing `reroll`, and `@party set suicide` — these can't be leaked or overridden.

**Not commands:** the ailment broadcasts `@poisoned` / `@blind` / `@confused` / `@diseased` / `@held` look like `@`-commands but aren't — they're state announcements the party window reads to mirror a member's condition, governed by your cure/ailment settings rather than the remote-control grid.

## Reconnecting

If a member drops, the party can auto-re-invite and reform on reconnect, and a member left behind can `@comeback` to rejoin the leader.

---

# Healing & Spells

The health and spellcasting engines keep you alive and buffed — resting, healing, curing, and blessing on their own.

## Health: rest, heal, flee

Auto-Heal / Rest (its toolbar toggle, or Settings → General) watches your HP and mana. Below your rest thresholds it sits and rests (or meditates) back up; below your run thresholds it flees; below your hang-up threshold it can drop the connection as a last resort. Every threshold is set on Settings → Health, as a percentage or an absolute value.

## Casting priorities

When more than one spell wants to fire, the caster follows the priority order on Settings → Spells — party heals, self heals, curing, buffing, then debuffing — and won't cast if it would drop you below your mana floors.

## Curing and blessing

Configure cure spells for holds, poison, disease, and blindness, plus several bless (buff) slots that recast as they expire. Auto-blessing — self *and* party — is controlled by the **Auto-Bless** toggle and nothing else (it's independent of Auto-Combat and Auto-Rest/Heal). By default the engine buffs while you're **moving or standing idle** (including an idle rest) and holds off **during combat** and **during a triggered recovery rest** (when HP or MA fell below your rest-if-below setting). Two opt-in checkboxes override those holds — one to also bless during a recovery rest, one to also bless while actively fighting. You can also tell it to ignore, or not announce, specific ailments.

## Mana regen

For mana-regen classes, the caster can rest to regen and — if configured — reroll a poor regen result up to a cap.

## The Spell Book (F2)

Press **F2** to open the **Spell Book** — a read-only reference to your class's spells. It's a lookup companion for the Spells settings, not a place you configure automation: use it to find a spell's cast-code and effect, then type that code into the pickers on **Settings → Spells**. F2 again closes it, and it updates itself as you play (type `spells` or `stat` in the game to refresh what it knows).

The header names the class and level it's showing. The grid lists each spell with a **✓** if you've learned it, its **Code** (the cast-code you type), **Name**, **Lvl** (the level **your class** can actually learn it — respecting a trainer's level gate, so a spell your class learns late from a specific NPC reads its real level, not the spell's lower base requirement), **Mana** cost, and **Effect** at your current level (hover the Effect cell for the raw scaling formula). **Double-click a spell** to open the game-data record of whatever teaches it — the **item** for a normal spell, or the **trainer NPC**'s record for a spell learned from an NPC (e.g. a Paladin's divine disfavour) — handy for finding where to buy or how to obtain a spell you haven't learned. Spells with neither an item nor a trainer source do nothing. Three controls up top narrow the list:

- **Show all** — off by default (you see only spells you're high enough level to cast); tick it to preview the whole class list, reading the **Lvl** column for when each unlocks.
- **Known only** — hides spells you haven't learned yet.
- **Search** — filter by cast-code or name.

If your class carries wands, scrolls, or potions that cast a spell, a **Cast-on-use items** section at the bottom lists what each one casts, its mana, and its charges.

---

# Cash & Items

MudPlay collects coin and loot, banks your wealth, and manages your gear.

## Collecting coin and loot

With the collection engines on, MudPlay picks up coin and flagged items off the ground after a fight, following your per-currency rules (Settings → Cash) and the per-item flags in Game Data. It can skip a pickup that would push you into a heavier encumbrance band, and drop smaller coin to make room for larger.

You don't have to wait for the engines, either: the **Action menu** (and the matching toolbar buttons) has **Get All**, **Drop All**, and **Equip All** to grab everything on the floor, drop everything unworn, or re-wear your Default set on demand — the local twins of the `@get-all` / `@drop-all` remote commands.

## Banking

When your wealth crosses a threshold, MudPlay routes to a configured bank and deposits, keeping a set amount on hand. Set the bank and thresholds on Settings → Cash. To bank right now regardless of the threshold, use **Action → Deposit All** (or its toolbar button / the `@deposit-all` remote command), which banks down to your keep-on-hand floor.

## Equipment sets

Gear is organized into named equipment sets in the **Player Workshop** — a Default set feeds your normal/alternate weapons and armor, a Backstab set feeds your stealth gear — and MudPlay swaps to the right set automatically (and re-equips after recovering a death pile). The **Item Finder** helps you build sets by browsing every equippable item with full stats.

---

# Player Workshop

Press **F1** (or View → Player Workshop) to open the **Player Workshop** — a tabbed window for managing your character: gear, leveling, quests, bosses, and deaths. There are no Save buttons anywhere in it; every edit auto-saves to your profile. Its tabs, most-used first:

## Equipment Manager — gear sets

Your gear lives in **four fixed sets**, each auto-equipped at a specific moment:

- **Default** — your baseline loadout (and backstab fallback).
- **Backstab** — worn for the opening backstab round.
- **Pre-rest HP** / **Pre-rest Mana** — swapped in out of combat before resting.

You don't create sets, you fill them. Pick a set on the left, then either click **Update from live** (fills it from what you're wearing) or type items into the **Item** boxes on the slot grid — each box only suggests gear your character can actually wear in that slot, and a blank slot means *{no change}* (left as-is). Click **Enable** so automation may use the set, and **Equip Now** to wear it at once. The **Equipment Bonuses** panel shows the set's projected AC and stat totals.

## Item Finder

The **Item Finder** button (in Equipment Manager) opens a searchable catalog of every equippable item, with columns for damage, AC, resists, stat bonuses, and more. Filter it by class, slot, level, or any stat, and sort by any column. A **Negates** column lists the spells an item cancels while worn, and the **Negates** dropdown in the stats filters lets you narrow to items that negate a particular spell (it's populated with every spell any item in the set negates; the default `(none)` doesn't filter).

It's a **reference tool**: double-click a row to see the item's full data record, and use the **Trial gearset** panel (with **Find Best**) to plan a loadout and read its projected stats. To actually equip something you found, note its name and type it into that slot's **Item** box back in Equipment Manager.

## CP Allocation

Plan how you'll spend character points as you level. **Add level** appends the next level's row; edit the **STR / INT / WIL / AGL / HEA / CHM** targets and the CP columns recompute live (a target that would overspend is clamped so **CP Left** never goes negative). At a trainer, **Apply this level** trains the selected row, or **Train now** walks to a trainer and trains the plan for you. Two checkboxes mirror Settings → Auto-Trainer: **Auto-train** (level up at trainers) and **Auto-train stats** (apply this plan).

## Level Projection

A read-only what-if table: pick a level **from–to** range (and optionally any **Race / Class**) to see the exp, training cost, HP, and mana at each level — reflecting your CP Allocation plan. **Reset to current** re-seeds it from your live character.

## Quests, Bosses, and Deaths

- **Quest Status** — a journal of the realm's quests. Expand a card for its requirements, reward, and step checklist; tick every step (or the **Complete** box) to fold its permanent bonus into your character. Inside a step, two kinds of token are **clickable**: a `(map/room)` coordinate (cyan) walks you there, and a single-quoted `'command'` (green) is typed at the game for you, exactly as if you'd entered it in the terminal — so annotate a step with `'ask jorah transport'` and clicking it sends that line. **Edit Quests…** lets you name, hide, or annotate them — and for the handful of quests that are class-locked in a way the crawler can't see (Magebane, Tarl), its **Restrict to classes** dropdown (a checklist of every class) pins the quest to the ticked class(es), so any other class is marked *Cannot complete*. **Quests you can't complete — wrong class, race, or alignment, or a class restriction — are hidden from the journal by default;** tick **Show in quest journal** for one in the editor to keep it visible anyway (that choice is saved per character, since eligibility is per character). The **Announce available quests** checkbox at the top (on by default, saved per character) prints `[<quest> Quest is Now Available]` to the terminal the moment you train past a quest's minimum level — including a several-level jump, which announces every quest whose gate you crossed — and dumps the full list of quests you can now start once at login (after the stat/inventory/who sequence). That dump only lists quests your class/race can do and that you haven't already completed, and never includes a cannot-complete quest even if you've chosen to show it in the journal. Alignment quests are gated separately: the three **Evil / Neutral / Good** checkboxes on the second header row (off by default, saved per character) declare which alignment chain(s) you're committed to — an alignment-gated quest only counts as available when its matching box is ticked, since in-game you're locked to one alignment chain once you start it regardless of your live alignment.
- **Bosses** — a respawn-timer tracker. **Mark** or **Now** stamps a boss's kill time and the **100%** column counts down to its respawn; on Paradigm the **-5% / -10% / -20%** columns count down to each early-spawn window (how far before 100% the boss can appear), and on Stock a single **87.5%** column does the same. A **Last Killed** column shows when each boss's timer was last set — by a Mark / Now button, a back-dated Mark, or an auto-detected kill. The tab **opens sorted by the 100% timer with running timers on top**, so a fresh open surfaces what's active instead of a name-ordered list that looks empty. Sorting by any timer column — or by **Boss**, **Respawn**, or **Last Killed** — groups cleanup spawns first, then bosses with a running timer, then idle ones, ordering by that column within each group (so your live timers stay at the top). **Manage Bosses…** edits the list, and you can **Import / Export** a shared table. Tick **Stop before** to halt automation ahead of a boss.
- **Chest Offload…** (on the Bosses tab) — a helper for cashing in boss chests. It snapshots your carried inventory, lists the containers you're holding (click one to `open` it), then — after re-reading your inventory — shows every new item, grouped into the fewest shops. **Coin gained** is measured for each open on its own (the coins you held just before that chest versus just after, so money from selling loot never counts as chest coin) and shown by denomination, most-valuable first (runic/plat/gold/silver/copper).
- **Chest Offload — pricing and selling** — each item has an editable sell quantity (keep some, sell the rest) priced at a **charm** picker, its own **Sell** button that sells the picked quantity, and a **Drop** button that drops the *leftover* you're not selling (held − picked). Both act while you're standing in the shop, and the list reconciles against the game's own confirmations: the row shrinks (and clears at zero) only when the `You sold …` / `You dropped …` actually lands, so a refused sale or blocked drop leaves your plan untouched. A **Total to sell** figure sums everything selected across all shops.
- **Chest Offload — picking a shop** — when an item is sold by more than one shop it gets a **⇄** button. The offload plan already assigns it to whichever shop keeps the trip to the fewest counters, but if that shop is an expensive detour, click **⇄** to see the other shops that buy it (each with its name, map/room, and current walking distance, nearest first), pick one, and hit **Change** to move it there.
- **Chest Offload — the selling trip** — each shop header shows a running total and **walks you there** when clicked, with the shop's map/room and the number of steps to reach it from your current room. It also carries a **Sell All** button that fires the sell commands for every item in the group (batched on Paradigm) once you're standing in the shop, plus a **Drop All** button to bin that shop's items. The shops are ordered into a short nearest-first trip using the same routing the walker uses (respecting avoid rooms, usable teleport gates, and item/hazard/boat gates).
- **Death Recovery** — your death history. **How did I Die?** replays the backscroll from the moment of death, and **Recover Now** walks to the death room and grabs the pile (or toggle **Auto-Recover Deathpiles** to do it automatically).

## Character Info and Calculators

**Character Info** is your read-only character sheet — stats, skills, the attack table (per attack type: accuracy, damage range, and swings per round, computed from your stats and equipped weapon), and folded-in quest bonuses. It also lists your worn, carried, and key-ring inventory, each a clickable link to its Game Data record (an item whose dumped name didn't resolve stays plain text).

**Calculators** holds what-if tools: the Hit Calculator, Swing and Backstab calculators, Movement Speed, Mana Regen, and Realm Rankings. The **Hit Calculator** projects your hit% / damage against a monster and the monster's hit% against you; its **"hits me %"** picker plus a **Show me the Monsters** button jump to the Monsters game-data tab filtered to monsters accurate enough to hit you at least that often, given your AC + dodge.

---

# Automation

MudPlay's automation is a set of independent engines you switch on and off — combat, healing, spells, pickup, movement, and more.

## The auto-engines

Each engine — Auto-Combat, Auto-Nuke, Auto-Heal/Rest, Auto-Bless, Auto-Light, Auto-Get Items, Auto-Get Cash, Auto-Sneak, Auto-Hide, Auto-Search — is an independent on/off switch. Your primary surface for them during play is the **Action menu** in the menu bar (the toolbar can also carry each as a button — add them in Settings → Toolbar + Shortcuts). An engine only acts while it's on, and each has a matching Settings tab for its behavior. Some gate others: Auto-Combat, for example, gates the combat/spell tuning. But **Auto-Bless stands alone** — self and party buffing is controlled by the Auto-Bless toggle and nothing else, so turning off Auto-Combat or Auto-Rest/Heal never stops your blessing.

## Manual one-shots and Reset States

The **Action menu** also carries commands you fire once, on demand, rather than leaving running:

- **Get All / Drop All / Equip All / Deposit All** — pick up everything on the floor, drop everything unworn, wear your Default gear set, or bank your wealth down to the keep-on-hand floor, right now. (These are the local twins of the `@get-all` / `@drop-all` / `@deposit-all` remote commands, and the toolbar Get / Drop / Equip / Deposit buttons drive the same actions.)
- **Reset States** — the recovery escape hatch. Clears *your own* stuck ailments, waits, and movement holds and returns you to an idle state — reach for it when an engine looks wedged (e.g. the walker parked "held" or "waiting" with nothing actually happening). It's also on the terminal's right-click menu.

## Base modes

The Settings → General **"Auto-Engines base modes"** checkboxes are your character's default engine states. The live toolbar settles to them **every time you load the character** — so a character always comes up in its configured defaults, not in whatever transient state the last session happened to end in — and the toggles also **snap back to them at the start of a loop or Auto-Lair**. So you can flip combat off to travel somewhere and it returns to your defaults when the circuit begins (or next time you load the character).

(A character created before these checkboxes existed adopts its current live modes as its base the first time it loads, so nothing changes until you edit the boxes.)

## The kill switch

The **All auto-responses** toggle at the top of the Action menu (and the `@auto-all` remote command) flips every engine off in one press, remembering what was on so a second press restores it — a fast "stop everything" that doesn't lose your setup. While it's off, auto-entry to the game is gated too, and **all movement is frozen** — a walk, loop, auto-lair, or a right-click Queue-walk-to will plan but hold until you turn Auto-All back on (then it resumes where it left off). Your own manual Pause/Resume is untouched by this.

## Macros, aliases, and triggers

Beyond the engines, you can script your own automation. All three editors live in the **Game Data Browser** — press **F3** (or use View → **Macros** / **Triggers** / **Aliases** to jump straight to one) and pick **Macros**, **Triggers**, or **Aliases** from the *Tables + editors* list on the left. Each shows the same surface: a **Filter…** box, an **Add** button, a **Remove** button, and a grid of what you've already made. **Double-click a row to edit it.** There's no separate save step — each editor's **Save** button writes to disk immediately, and the list's **Enabled** column shows a ✓ for the ones that are live.

- **Macros** bind a **key chord to a command.** Click **Add**, press **Capture** and hit the key combo (release the main key to lock it in, or Esc to cancel), then type the **Command** to send. Split it into several lines with `^M` or `;` — each fragment fires as its own command. Macros work while you're typing in the terminal; new profiles start with the numpad pre-wired to compass movement.
- **Aliases** expand a **typed word into a longer command.** Give the alias a **Name** (matched on the first word you type, case-insensitive) and an **Expansion**, where `{0}` is the whole rest of the line and `{1}`, `{2}`, … are the individual words — so an alias `cast` → `c '{1}' {2}` turns `cast heal bob` into `c 'heal' bob`. Aliases only expand when you press **Enter in the Conversation window's input box**; typing in the main terminal bypasses them.
- **Triggers** are **auto-responses to game text** — when a line matches, MudPlay fires a reply. Give the trigger a **Name**, then set:
  - **Location** — *Game data* (saves with the active game-data set, so it travels with the realm) or *Profile* (saves with this character).
  - **Scope** — which incoming lines it watches: *Game messages* (the default), a single chat channel (*Say / Yell / Gossip / Telepath / Gangpath / Broadcast*), *Chat (any)*, or the *System log*.
  - **Match type** — *Literal* (type the text as it appears; `*` wildcards a span and `{name}` captures a piece) or *Regex* (full .NET regex, with `(?<name>…)` for captures).
  - **Pattern** — the text or expression to match against each line. Any pieces you capture appear in the **Captures** row.
  - **Response** — what MudPlay sends back on a match. Drop a captured value in with `{name}`, split multiple lines with `^M` or `;`, or leave it blank to send a bare Enter.
  - **Sound** (optional) — a file picker is here, but sound playback isn't wired up yet, so it does nothing today.

---

# Game Data

MudPlay's automation reads from **game data** — the monster, item, spell, room, and shop tables imported from a MajorMUD `.MDB` database. The **Game Data Browser** (press **F3**, or the toolbar's *Game Data Browser* button) lets you inspect all of it and override individual records for your character.

## Importing and switching sets

The top **Game Data** menu (in the menu bar) manages your data sets:

- **Import .mdb…** — pick a MajorMUD `.MDB` file; MudPlay imports it as a new named set and switches to it. This populates the tables the engines read from — the terminal itself works without it.
- **The set list** — every imported set appears at the top of the menu with a checkmark on the active one; click another to switch. The Browser's status bar shows *Set: <name>*.
- **Manage Game Data…** — copy or move a set's saved loops and lairs into another set, or delete a set.
- **Modify Blacklist…** — hide specific rooms (by map/room number) from the map and room search, and mark ones the walker should treat as unreachable.

## Getting around the Browser

The window is a sidebar plus a content pane:

- The sidebar's **Search…** box filters the **section list**, not the rows — type "weapon" and unrelated sections drop away.
- **Tables + editors** (top group) holds what you build: **Players, Macros, Triggers, Aliases, Messages**. (The macro/alias/trigger editors are covered in the **Automation** section.)
- **Imported tables** (bottom group) holds the game data: **Monsters, Items, Spells, Rooms, Lairs, Shops, Races, Classes, TextBlocks, Info, Unobtainable, Quest Flags.**

Click a section to open it. Each table has its own **Filter…** box (this one filters *rows*), sortable and resizable columns, and a row-count line at the bottom. The rightmost **Use** column shows which tier owns each row — **Def** for the untouched import, or **Glob / BBS / Char** once you've overridden it.

The **Monsters** table carries a full column set for browsing and filtering monster stats: **Rgn** (respawn timer), **Exp** (the actual experience earned per kill — base × multiplier), **HP**, **AC/DR**, **Dodge**, **MR**, **Acc (Maj/Mx)** (majority/highest attack accuracy), **Damage**, **Exp/(Dmg+HP)** (an exp-per-effort efficiency score), **Lair Exp**, **# Lairs**, **Avg Lair Size**, **Biggest Lair**, **Mag** (the hitmag level a weapon must meet to land a hit), and **Undead**.

It also carries a **filter sidebar** on the right — drag its left edge to resize it — with one **at-least (≥)** threshold per stat, so you filter for monsters that *have at least* that much of a stat:

- **HP ≥**, **AC ≥**, **DR ≥**, **Dodge ≥**, **MR ≥**, **Damage ≥**, **Mag ≥**, **Rgn ≥**, **Acc ≥** (monsters accurate enough to threaten you), **Exp ≥**, **Lair Exp ≥**, **# Lairs ≥**.
- Plus an **Undead only** checkbox and an **Alignment** dropdown.

Values group with thousands separators. Unlike the live **Filter…** text box, the sidebar filters are applied on demand: edit the boxes, then press **Apply filter** to run them (a ticker's outline turns amber while you're editing and green once applied), or **Clear filters** to empty them. Everything AND's together.

## Overriding a record

**Double-click a row to open it** — what happens depends on the table:

- **Items** and **Monsters** open a real **override editor**: an editable pane on the left, the read-only **Other Info (from MDB)** on the right. For an item you can flip its automation flags (**Auto-collect, Auto-discard, Auto-buy, Auto-sell, Auto-stash**, and more), set **Min. to keep / Max to get**, and toggle **Auto-obtain for path**. A **Message** section shows the item's on-use / proc message — its `use <item>` buff line (with wear-off, so a Buff Watchdog timer can track it) or a weapon-proc line — and **Add / Edit message…** opens the same editor the Spells tab uses, with the item's facts filling its Game Data tab. Because you edit an item's message here, a message **claimed by an item in this set is hidden from the Unfiltered Messages tab**, exactly like a spell's message (an orphaned item link — the item isn't in this set — keeps the record on the Unfiltered Messages tab). For a monster you can set its **Relationship** and **Priority** and its pre-attack and override-attack spells; the read-only pane's **Spawns In** list shows each room's lair size (e.g. `1/2122 (lair: 2)`). (Combat message wording and per-monster flavor prefixes are no longer edited here — hits, misses, dodges, blocks, and deaths are recognized generically from line colour and the experience line, and flavor adjectives come from one shared vocabulary you edit under **Flavor Prefixes** (below), so you never hand-enter a monster's messages or prefixes.) When you set the Relationship to **Neutral**, a **Kill on sight** checkbox appears — a neutral is normally left alone (it never attacks first), but checking this makes auto-combat engage it while leaving other passive neutrals safe to rest among, so the engine can rest/meditate between kills instead of being forced to clear the whole room. Even *without* Kill on sight, if you hand-attack a passive neutral yourself (a manual swing or combat cast), the engine takes over and finishes it — hitting a neutral turns it hostile, so it's treated like an enemy until it dies and the walker holds in the room — so you don't have to keep swinging manually; the other un-engaged neutrals stay passive and rest-safe. The **Use** dropdown chooses where the override saves — **Character** (this character only), **BBS** (everyone on this BBS), or **Global** (the whole install) — then **OK** writes it and the row's Use column updates to match.
- On an item, the right-hand info pane is also interactive: a **Charm** picker (default 50) re-prices the **Bought / sold** buy/sell figures live so you can compare, say, a higher-charm party member selling; each shop links to its room record and offers **Queue Walking here →** (arms a walk to that shop, like typing it in the nav search box); and **Dropped by** lists the monsters that drop it as links to their records.
- **Spells** — double-click edits the spell's player-cast **message** wording (its success and wear-off lines); the spell's own stats are read-only. The read-only record also lists **Negated by** — any items that cancel the spell while carried (the inverse of the Item Finder's *Negates* column). Record references on that tab — Summons, Casts, Casted By, Negated by, Learned From — are clickable links that open the monster / spell / item record. A spell's message is stored in the Unfiltered Messages table, but because you edit it here, a message **claimed by a spell in this set is hidden from the Unfiltered Messages tab** — it would just be the same record listed twice. (A message whose spell link is orphaned — the spell isn't in this set — stays on the Unfiltered Messages tab, since the Spells section can't reach it.)
- **Rooms** — double-click opens a read-only detail popup (exits, lighting, shop, placed monsters, room commands); its "Also here" monsters show their record number and link to their records. For a shop room, a **Charm** picker re-prices the stock table live.
- **Shops** — double-click opens the room-detail popup for the shop's room directly (the same popup the Rooms tab opens, showing the stock table with its live Charm picker). A shop that spans several rooms opens on the first and lists the others as clickable links in brackets next to the popup's title — click one to hop the popup to that room.
- The rest (Lairs, Races, Classes, and so on) are read-only reference.

**Flavor Prefixes** is a small editor of its own in the *Tables + editors* list (not a double-click table). It's the vocabulary of adjectives the game prepends to a monster's name — *large*, *nasty*, *huge*, and so on. The room classifier strips a leading word in this list so "large giant rat" resolves to "giant rat" with no per-monster data. It starts from the built-in stock list and applies to the **active game-data set**, so a custom realm that uses different adjectives just adds them here (type a word → **Add**; **✕** removes one; **Reset to defaults** restores the built-ins). Edits save to that set immediately. If the classifier ever meets a prefixed name whose leading adjective isn't in the list, it flags a Program-Log row you can double-click to add the word in one click.

---

# Conversation

Press **Alt+C** to open the **Conversation** window — a dedicated view of all the chat MudPlay pulls out of the terminal, with its own input box so you can talk without hunting for the game prompt. Alt+C again closes it.

## The chat log

Chat is collected into one merged, timestamped stream (not per-channel tabs). Each line shows the time, a colored **channel tag**, the speaker, and the message:

- **GOS** gossip · **SAY** local say · **YELL** yell · **←TELE / TELE→** telepaths received and sent · **GANG** gang/guild · **BCAST** broadcasts · **SERVER** realm notices (players entering and leaving, PvP messages).

Each channel has its own color, and web links inside a message are clickable. Party chat isn't shown here — it has its own **Party** window.

**Actions / emotes** (the socials from your board's `action list` — `hug`, `wave`, `smile`, `tickle`, and so on) are pulled in too, whether you perform them, someone aims one at you, or you just witness one in the room. They show under the **SAY** chip (they're room-local, like say) with the message text in **green** — the board's own color for them. Since the obvious-exits line is also fully green, MudPlay only captures true actions: your own start with "You <verb>", and someone else's must come from a **player who's actually in your room** — so obvious exits, room-entry/exit, and party-follow movement never get mistaken for an emote.

## Filtering and searching

The toolbar across the top controls what you see:

- **Channel checkboxes** — **Gossip, Say, Telepath, Gang, Broadcast, Yell, Server** — tick or untick to show or hide each channel. Each box is painted in its channel's color, so the row doubles as a color key. Your choices are remembered per character. (Telepaths in and out share the one Telepath box; realm notices and PvP messages share the Server box.)
- **Search** box — narrows the log to lines whose speaker or text matches what you type (this one isn't remembered between sessions).
- **Auto-scroll** — when ticked, the log stays pinned to the newest line; untick it to read back without being yanked to the bottom.

## Talking

Type into the input box at the bottom and press **Enter** (or click **Send**) to send the line to the game — you still type the game's own chat commands (`gos hi`, `/bob hey`, and so on). This is the input box where your **aliases** expand and where `;` or `^M` splits one line into several commands. **↑ / ↓** recall what you sent before, and the chevron at the right edge of the box opens a list of recent commands to pick from.

## Logging and history

The window keeps its history even after you close it, and replays your last session's chat when you reconnect. To save chat to a file, turn on **Settings → Talk → Log conversations** — it writes to the `Logs` folder, which you can open from **Tools → Open logs folder**. There's no clear button in the window itself; use **Tools → Clear chatlog** on the main window to wipe it. The chat font and channel colors are set on the Talk tab and take effect the next time you open the window.

---

# Tools & Diagnostics

A few smaller windows for reviewing your session and troubleshooting. Each is modeless and toggles closed when you press its key again.

## Program Log (F4)

Press **F4** (or **Tools → Program Log…**) to open the **Program Log** — a running, timestamped record of what the engines are actually doing, and the first place to look when something automated didn't behave. Each row is tagged with a severity and the source engine.

- **INF / WRN / ERR** — severity filters; tick the ones you want to see.
- **Search** filters the rows by source or message text; **Clear** empties the view; **Auto-scroll** keeps it pinned to the newest row.
- **Debug** and **Combat** are *generation* toggles (not just filters): they turn the verbose cross-engine trace and the combat-decision channel on or off across the whole app, and show those rows here. Both are **on by default** and persist per character — leave them on for the richest diagnostics; turn one off to quiet the noise. (These are the same two channels you'll see in a bug report.)
- **Auto-collect logs** writes the program, memory, and combat-trace files to the Logs folder for the session (off by default, so a normal run leaves nothing behind). **Hop timing** logs one line per confirmed room hop with its measured wall-clock time — used to tune the Auto-Lair travel-cost table. **Simulate Death button** reveals a test button on the Player Workshop's Death Recovery tab (off by default, and reset off every launch). **Simulate Chest button** does the same for the Chest Offload window (Bosses tab → Chest Offload) — it reveals a **Simulate Chest** button that seeds a few random containers so you can exercise the window without real boss chests.

## Backscroll (Alt+L)

Press **Alt+L** to open **Backscroll** — the full terminal history, including lines that have scrolled off the top, on a timestamped transcript that opens at the newest line.

- **Search** — type a term and press **Enter** (or **Find next**) to step through matches, newest to oldest, wrapping back to the top. The footer shows the line count and how many matches were found.
- **Jump to end** — return to the newest line.
- **Export…** — save the whole transcript to a text file, each line prefixed with its timestamp.
- Drag to select a region, then **Ctrl+C** or **right-click → Copy** to put it on the clipboard as plain text. Right-click → **Select all** grabs the whole transcript to copy at once.

Backscroll is a **snapshot taken when you open it**, not a live tail — to pick up newer output, close and reopen it (nothing is lost in the meantime).

## Session Stats

Open **Session Stats** from the **View** menu or its toolbar button (it has no default hotkey — you can assign one on Settings → Shortcuts). It tracks this session's performance in a stack of panels: **Kills/hour** and **Exp/hour** graphs, an **HP/MA per loop step** chart, and **Player Statistics**, **Time Analysis**, and **Session Statistics** tables (kills, experience, currency, and time spent moving, resting, and fighting).

- **Right-click** the panel area to show or hide individual panels, and **drag a panel by its title** to reorder them — your layout is saved per character.
- **Reset session** zeroes every counter and restarts the clocks; individual panels have their own **Reset** too. (These don't ask for confirmation.)
- **Transaction history** and **Players Seen** open the detailed ledgers — coin banked and stashed this session, and every player you've encountered. In the transaction ledger, **stash** entries are tinted faint gold (the map's stash-marker colour) so they stand out from bank deposits, and **double-clicking any entry** opens the Navigation map centred on the room where that deposit or stash happened. Each row has a **Keep** checkbox: check the entries you want to hold onto, and **Clear history** wipes everything *except* those — a way to prune a full ledger without losing the rows that matter (with nothing checked it clears the whole thing, as before). The clear updates the on-disk log too, so kept rows survive a reconnect and cleared ones don't come back.

## Buff Watchdog

Open **Buff Watchdog** from the **View** menu (right after Party) or its toolbar button — it has no default hotkey, but you can assign one on Settings → Shortcuts. It lists the buffs you've **configured** (never the whole spellbook) — your **self-bless slots**, the **HP/MA-regen** and **when-HP/MA-full** utility buffs, any `#item`-cast buffs, and your **party-bless slots** — each on its own row with a live timer bar:

- Each row **is** the timer bar: the buff's 4-letter cast code (or a `#`-prefixed item name) sits **left-aligned inside the bar**, with the **time remaining** just after it. Party-buff rows also show the target member/class as a caption beneath the bar.
- The **bar fills as the buff ages** (empty just after it lands, full at wear-off), and a **vertical amber marker** shows where its **recast window** opens — the "recast within (seconds)" lead you set per slot. When the fill crosses the marker the bar turns amber: the buff is now due to be recast.
- A buff that **isn't up** (worn off, or never cast) shows an empty bar labelled **not up**, so you can see at a glance which configured buffs are missing.
- A configured buff your character **hasn't learned** is flagged **unlearned**.
- Party-buff rows show the **soonest-expiring** member's timer (the next one due) and that member's name.

**What it counts as "up".** A buff's timer is armed by the **cast code** — whether the client cast it or **you typed it by hand** — so a manual cast shows up here the same as an automated one. The client deliberately **ignores the `stat` screen's buff list** (Paradigm's `You feel …! (Ns)` lines): those shared effect messages can't say which buff is which, so they're never treated as a cast. **Any disconnect freezes the timers** (the display too) and reconnecting **resumes** them with the same remaining — a brief drop keeps your recast clock instead of restarting it. Switching characters (or a gap longer than the buff could last) starts the watchdog empty, with no buffs assumed.

The window is a live view — it refreshes about once a second while open — and re-selecting the menu item (or toolbar button) toggles it closed. It's read-only: configure the buffs themselves on the Player Workshop (self-bless slots, regen, when-full) and Settings → Party (party-bless).

## Wire Inspector (F5)

Press **F5** to open the **Wire Inspector** — a troubleshooting view of the data the server sends, in up to three panes you toggle with the **Raw / Stripped / Classified** checkboxes: **Raw** (control codes made visible, e.g. `^[` for escape), **Stripped** (the same stream with the ANSI escape sequences removed), and **Classified** (each combat-window line tagged with how the combat engine read it — e.g. `[Combat: Monster Miss (you)]`, `[Combat: You Hit]`, `[Combat: Armor Block (you)]`). The Classified pane also marks each **recognized monster death** with `[Monster Death: <name>]` — and an exp-inferred death whose message *wasn't* recognized shows as `[Monster Death: inferred from exp — message not recognized]`, so an unrecognized death line stands out. **Raw and Classified are on by default** (Stripped off); unchecking a pane collapses its column so the others fill, and your choice sticks. It shows inbound server output only, and keeps the most recent 64 KB.

- **Pause / Resume** freezes the view so you can read it; **Clear** empties the buffer.
- **Auto-scroll** keeps the panes pinned to the newest bytes, and **Sync scroll** ties the Raw and Stripped panes' scrolling together.
- **Find next** locates a term in the Stripped pane, and **Export raw… / Export stripped… / Export classified…** save any pane to a file.

Reach for this when reporting a display or parsing glitch — it shows exactly what arrived on the wire. Because **Raw and Classified are on by default**, a **Bug Report** attaches the last 750 lines of each unless you turn them off — so a combat-recognition problem lands with the exact wire and the engine's read of every combat line and death.

---

# Settings Menu

MudPlay is a Telnet terminal client for MajorMUD / MegaMUD-style BBS door games. On top of a faithful terminal, it layers a large automation suite — auto-combat, auto-healing, auto-spellcasting, navigation/looping, party coordination, cash and item collection, and more — and almost every piece of that automation is tunable. This guide documents every one of those tunable settings: what it does, what happens when you change it, and where to find it.

**What these settings control.** Broadly: how your character fights, heals, casts spells, and buffs; how the client walks you around the map and loops between monster spawns; how it handles party coordination, chat, and remote `@`-commands from other players; how it manages coin and item pickup; how the terminal looks and behaves; and various connection/reconnection behaviors for the BBS itself.

**Where to find them.** Almost everything lives in one place: the **Settings window** (opened from the toolbar, the View menu, or its keybind — default varies by build). It's organized into tabs down the left side: General, Toolbar + Shortcuts, BBS + Display, Health, Spells, Combat, Party, Cash, Statline, Talk, Auto-Light, Auto-Lair, Auto-Trainer, Other, Events, and Sounds. A search box at the top of the window filters the tab list. Two related editors live outside this window: the **keybind rebind dialog** (opened from a row on the Toolbar + Shortcuts tab) and the **macro editor** (a separate Game Data dialog).

**Where settings are stored.** MudPlay never stores a setting in one flat file. It uses a four-tier hierarchy — **Defaults → Global → BBS → Character** — and each tab's fields belong to one specific tier:

- **Character-tier** (the vast majority of settings — Combat, Spells, Health, Party, Cash, Talk, Auto-Light, Auto-Lair, Auto-Trainer, most of General, keybinds, macros) live inside that character's own profile file and only apply to that one character.
- **BBS-tier** (connection info, reconnect behavior, terminal size, per-BBS realm quirks) live in that BBS's own file and are shared by every character who plays there.
- **Global-tier** (a handful of install-wide toggles — navigation-line colors, the Pyramid/Asylum puzzle solvers, confirmation prompts, the Help-menu website list, player-database cleanup) apply to every character on every BBS on this install.

All of this is stored under a single MudPlay data folder (`~/.local/share/MudPlay/` on Linux, `%AppData%\MudPlay\` on Windows, `~/Library/Application Support/MudPlay/` on macOS) as JSON files that only record *deltas* from the tier below them — so an unmodified setting isn't written to disk at all.

**Does MudPlay save automatically?** No — the Settings window uses an explicit **OK / Apply / Cancel** model. Edits are staged in memory; **OK** applies every changed tab and closes the window, **Apply** applies without closing, and **Cancel** (or the window's X button) discards everything you changed since opening it. A few things outside the main Settings tabs are the exception and save the instant you change them: keybind rebinds, macro edits, the Events tab's list, and the "Disable all events" toggle.

**Before you start changing things — a few things worth knowing:**
- Nearly every setting documented here takes effect **live**, with no restart or reconnect required — this guide calls out the exceptions explicitly (e.g. terminal scrollback size, Conversation-window fonts/colors, a handful of BBS-connection fields that only apply on the *next* connect).
- A handful of controls exist in the UI but currently **do nothing** — they're either genuine stubs (the whole Sounds tab) or fields that were built but never wired into the automation engines (Combat's *Polite mode* and *Show combat round totals*). This guide flags every one of them explicitly rather than describing invented behavior.
- Many settings only matter once a corresponding **master switch** is on. For example, the entire Auto-Light tab only matters once the Auto-Light engine itself is enabled (Settings → General, or its toolbar toggle); Combat/Spells/Health settings only matter while Auto-Combat is on.

---

## General

Settings → General. Everything here is character-tier (follows the loaded character) except two install-wide (Global-tier) items — the navigation-line color block and the startup-animation toggle — which apply to every character on the install. No character loaded means this whole tab shows a "load or create a profile" banner instead of controls.

### Data files (directory display)

**What it does:** Shows the resolved path to MudPlay's data folder, with an "Open Data folder…" button (opens it in your file browser) and a "Change…" button (relocates every file under that folder to a new location and restarts the app).
**Important notes:** This is informational, not a saved setting. "Change…" triggers a full app restart at the new location; MudPlay validates the destination is empty, writable, and not nested inside the current folder before allowing the move.

### Terminal font (family + size)

**Default:** Family = bundled MX437 IBM VGA 8×16 CP437 bitmap font; Size = 16 pt.
**Available options:** MX437 (bundled), JetBrains Mono (bundled), plus every monospace font installed on your system. Sizes: 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28, 32.
**What it does:** Controls the font the main terminal canvas renders with. MX437 reproduces classic BBS CP437 output (box-drawing characters, line art); JetBrains Mono is a clean modern monospace alternative if you don't care about retro accuracy.
**When you might change it:** Switch to JetBrains Mono if the block-drawing glyphs in MX437 look odd on your display, or if you just prefer a more modern look.
**Important notes:** Applies live on Save/Apply, no restart needed.

### Navigation tooltip font (family + size)

**Default:** Family = MX437; Size = 13 pt.
**Available options:** Same font list as the terminal font; same size list, defaulting smaller.
**What it does:** Controls the font used only by the room-name tooltip that pops up when you hover over the Navigation map — independent of the main terminal font.
**Important notes:** Applies to the next tooltip hover after you save — no restart needed.

### Scale terminal output to fill the window

**Default:** Off
**What it does:** When on, the terminal's text grows to fill the window (zooming in on a fixed 80×25 grid) instead of leaving empty space around a fixed-size grid in a larger window.
**When you might change it:** Turn it on if you run MudPlay maximized or in a large window and don't want dead space around the text.
**Important notes:** Applies live. This setting resets to Off whenever you close a profile, since it's stored per-character.

### Keep typing directed at the terminal when other windows are open

**Default:** On
**What it does:** With this on, keys you press while a non-terminal window (Settings, an editor, etc.) is focused still reach the game — so you can keep sending commands with a dialog open — unless you're actually typing in a text box in that window, or the key is something the window itself needs (Tab, Escape, a menu shortcut). Turn it off to make keystrokes go only to whatever window is currently focused, like a normal application.
**When you might change it:** Turn it off if you notice game commands accidentally leaking through while you're trying to type into a settings field.
**Important notes:** Applies live, no restart needed.

### Show the mud-throwing startup animation

**Default:** On
**What it does:** Plays a small animated splash on the terminal while MudPlay is starting up, before you've connected or loaded a profile. Turning it off shows just a static title/byline instead.
**Important notes:** This is actually an install-wide preference (not per-character) even though it's edited from this character's tab — it's stored on the app's default profile so it survives switching characters. Applies immediately; a splash already playing stops the moment you turn it off.

### Navigation line appearance (color + thickness)

**Default:** Go-to `#1E64DC` (blue), Loop `#7AB870` (green), Preview `#E66C5A` (orange-red), Loop-builder preview `#E66C5A`, Auto-Lair `#DC821E` (orange) — all 3.0 px thick.
**Available options:** Any RGB color via the color-picker; thickness 1.0–8.0 px in 0.5 steps.
**What it does:** Sets the color and line thickness for each of the five distinct route lines the Navigation map draws — the active walk-to path, an active loop's route, a queued go-to preview, the in-progress loop-builder preview line, and an Auto-Lair run's route.
**When you might change it:** Make the lines thicker or higher-contrast if you find the default map lines hard to see; give each route type a color you can tell apart at a glance.
**Important notes:** This is a **Global-tier** setting — changing it changes the map for every character on the install, not just the current one. "Restore Defaults" resets every line at once, and each row has its own **Reset** button. Applies live — the Navigation map repaints immediately with no restart.

### Default task

**Default:** `Do nothing`
**Available options:** `Do nothing`, `Begin looping` (plus a saved-loop picker), `Begin Auto-Lair` (plus a saved-setup picker).
**What it does:** Chooses what MudPlay does automatically the moment you enter the game. "Do nothing" leaves you sitting at the prompt. "Begin looping" auto-starts a saved Loop (walking you to its nearest point first, if needed). "Begin Auto-Lair" auto-starts a saved Auto-Lair setup the same way.
**When you might change it:** Set this once you have a reliable farming loop or Auto-Lair setup, so logging in and grinding become one step instead of several clicks.
**Important notes:** If you pick "Begin looping" or "Begin Auto-Lair" but haven't actually saved anything to run, MudPlay just logs a warning and does nothing — it won't error out. Loops/Auto-Lair setups are tied to a specific game-data set; a saved pick that isn't in your currently active set stays remembered but won't do anything until you switch back to that set.

### Auto-connect when profile loads

**Default:** Off
**What it does:** Dials the profile's saved BBS the instant the profile finishes loading, instead of waiting for you to click Connect.
**Important notes:** Only checked once, right when a profile loads — not something that re-triggers mid-session.

### Backup profile when making changes

**Default:** Off
**What it does:** Before saving any change to this character (including from this very tab), copies the existing profile file to a `.json.bak` file first — a simple one-step-back safety net if a settings change goes wrong.

### Auto-Engines base modes (11 checkboxes)

**Default:** On — Auto-Combat, Auto-Nuke, Auto-Heal / Rest, Auto-Bless, Auto-Get Items, Auto-Get Cash, Auto-Sneak. Off — Auto-Light, Auto-Hide, Auto-Search, Auto-Train.
**What it does:** Each checkbox is the base on/off state for one automation engine: Auto-Combat (fighting), Auto-Nuke (offensive AoE/debuff spells), Auto-Heal / Rest (healing and resting), Auto-Bless (buffing), Auto-Light (keeping a light lit), Auto-Get Items (picking up ground loot), Auto-Get Cash (picking up coin), Auto-Sneak and Auto-Hide (the two stealth engines), Auto-Search (searching for hidden things), and Auto-Train (the Auto-Trainer tab's leveling automation).
**When you might change it:** Set the automation posture a character should return to — e.g. a scout that should never auto-fight, or a healer that should always rest.
**Important notes:** These are your character's **base** engine states, not the live toolbar toggles. They're applied when the character loads, and the live toggles snap back to them at the start of a loop or Auto-Lair run — so you can flip an engine off to travel somewhere and have it return to your baseline when the circuit begins. See **Automation → Base modes** for the full picture.

### Allow hangup in all-off mode

**Default:** Off
**What it does:** Normally, if every Auto-* engine above is off, MudPlay does nothing at all — including the emergency low-HP hangup. Turning this on carves out one exception: even with everything off, MudPlay still disconnects you if your HP drops below the Health tab's "Hang up if below" threshold.
**Important notes:** Depends on the Health tab's threshold to know when to fire. It's silenced entirely if the toolbar's "Disable hangups" toggle is on — that flag always wins.

### Re-enable on reconnect (11 checkboxes)

**Default:** Off (all)
**What it does:** One checkbox per automation engine (the same 11 engines as above). When you reconnect after having been disconnected mid-session (not the very first connect of an app session), each checked engine gets automatically turned back on — useful if you manually paused something, got dropped, and want your automation state to reset to "on" on redial rather than staying off.
**When you might change it:** Check the engines you always want running even through a flaky connection (e.g. Auto-Heal/Rest); leave off the ones you deliberately paused for a reason (e.g. Auto-Nuke while grinding a safe area).

---

## Toolbar / Shortcuts

In-app tab title: "Toolbar + Shortcuts". The toolbar layout/visibility/keybinds portion is character-tier; the Help-menu website list is Global-tier and stays editable even with no character loaded.

### Show toolbar

**Default:** On
**What it does:** Master visibility switch for the whole toolbar.
**Important notes:** Applies live, no restart.

### Toolbar position

**Default:** `Top`
**Available options:** `Top`, `Bottom`, `Left`, `Right`.
**What it does:** Which window edge the toolbar docks against.
**Important notes:** Greyed out while "Show toolbar" is off. Applies live.

### Toolbar layout (button/separator list)

**Default:** The standard button layout (17 buttons plus 3 separators) in its original order.
**What it does:** An ordered list of buttons (and separators) that make up the toolbar. "Add to toolbar" promotes an action from the Shortcuts pool onto the toolbar; "Remove" demotes it back off (it can still carry a keybind); "Add separator" appends a visual divider to the end of the list; "Move up"/"Move down" reorder the selected row; "Reset Toolbar to Default" restores the factory layout without touching your keybinds.
**When you might change it:** Trim the toolbar down to just the buttons you actually click, or reorder it to match your workflow.
**Important notes:** Per-character — each character can have a different toolbar. Applies live.

### Shortcuts pool

**What it does:** Lists every action that isn't currently on the toolbar, so it can still carry a keyboard shortcut without needing a visible button.
**Important notes:** Not itself a saved setting — just a computed view of "everything not currently on the toolbar."

### Change keybind… / Reset Keybinds to Default / Import toolbar + Keybinds

See the [Keybindings](#keybindings) section below — the rebind dialog is launched from here, but keybind changes apply immediately rather than waiting on this tab's Apply button. "Reset Keybinds to Default" restores every built-in shortcut in one click. "Import toolbar + Keybinds" copies another character's toolbar layout (staged, needs Apply) and rebinds your keybinds to match theirs (applied immediately, even if you cancel the tab afterward) — a genuine asymmetry worth knowing about.

### Help menu websites (list editor)

**Default:** 4 seed links — MajorMUD wiki, MajorMUD subreddit, MudInfo.net, MajorMUD Facebook Group.
**What it does:** An editable list of label/URL pairs that populate the app's Help menu with one clickable link per entry. Add, remove, reorder, rename freely; "Reset to default" restores the 4 seed links.
**Important notes:** This is a **Global-tier** list — shared by every character and BBS, and editable even with no profile loaded. Applies live on Apply.

### {BBS name} site: URL + "Show in Help menu"

**Default:** URL empty; "Show in Help menu" On.
**What it does:** A link to the currently active BBS's own website, shown in the Help menu as a separate "BBS site" entry alongside the list above.
**Important notes:** Only shown/editable when a BBS is actually active.

### Disable hangups (toolbar toggle)

**Default:** Off
**What it does:** This is a toolbar button, not a checkbox on a settings tab — but it's documented here because that's where you'll actually find it (look for the "no hangup" icon). When on, **no** automatic mechanism can drop your connection — not a remote `@hangup`, not the emergency low-HP hangup, nothing — only you disconnecting manually will end the session.
**Important notes:** This is a hard override — it wins over the General tab's "Allow hangup in all-off mode" carve-out. One narrow exception still fires even with this on: a graceful log-off ahead of the BBS's nightly server cleanup, if you've opted into "reconnect after cleanup" on the BBS tab.

---

## Keybindings

Not its own Settings tab — the rebind editor is a small popup dialog opened from a row on **Settings → Toolbar + Shortcuts**, one instance per action you want to rebind.

### What's rebindable

Every built-in action that has (or can have) a keyboard shortcut: connection toggle, opening the Navigation/Backscroll/Conversation windows, movement start/pause/stop, capture toggle, the function-key row (Player Workshop, Spell Book, Game Data Browser, Program Log, Wire Inspector), and the Ctrl-cluster File-menu actions (New/Open/Save/Save As profile, Quit). A few actions (Open Party, Open Session Stats, Open Settings) ship with **no** default shortcut — you can only reach them via their toolbar button or menu until you assign one yourself.

### Default shortcuts out of the box

| Action (as labeled in the list) | Default key |
|---|---|
| Connect / Disconnect | Alt+H |
| Navigation | Alt+M |
| Start movement | Alt+V |
| Pause movement | Alt+B |
| Stop movement | Alt+N |
| Backscroll | Alt+L |
| Capture | Alt+S |
| Conversation | Alt+C |
| Player Workshop | F1 |
| Spell Book | F2 |
| Game Data Browser | F3 |
| Program Log | F4 |
| Wire Inspector | F5 |
| New profile | Ctrl+N |
| Open profile | Ctrl+O |
| Save profile | Ctrl+S |
| Save profile as | Ctrl+Shift+S |
| Quit | Ctrl+Q |

### How rebinding works

**What it does:** Click **Capture**, then press the key combination you want — the dialog waits for you to release a non-modifier key (so you can hold Ctrl/Shift/Alt first, then land on the target key) to lock it in. Press **Esc** to cancel without changing anything. **Clear** removes the shortcut entirely, leaving that action with no keybind until you assign a new one.
**Important notes:** A captured combo is checked live against several exclusion lists and shows a red error (blocking Save) if it collides with anything:
1. Reserved keys — Enter, Escape, Tab, Backspace, Delete, the lock and system keys (Caps Lock, Num Lock, etc.), and specifically the main-row period key (`.`), which MudPlay reserves because a leading period is MajorMUD's say-precursor. (The numpad period stays bindable.)
2. System combos — Alt+F4, Ctrl+C, Ctrl+V can never be rebound.
3. Another built-in shortcut already using that combo.
4. A user-defined macro (see below) already using that combo — macros and keybinds share one conflict list; a combo can never be assigned to both.

Rebinding takes effect **immediately** on Save — it doesn't wait for the Toolbar tab's own Apply button.

### Scope

Per-character, not global — each character can have entirely different shortcuts. Closing a profile resets the in-memory bindings back to defaults for whichever profile loads next. "Reset all shortcuts" (on the Toolbar + Shortcuts tab) wipes every custom rebind on the current character back to the factory table in one click.

---

## Macros & Aliases

Two per-character typing shortcuts, both managed in the **Game Data Browser** (F3), not the main Settings window, and saved the moment you change them — there's no separate Apply step, unlike most Settings tabs. **Macros** bind a key combination to a command; **aliases** expand a typed word into a longer command. Macros share the built-in keybindings' "can't double-book a key" check; aliases are instead checked against MajorMUD chat commands so they can't hijack your chat. (For the step-by-step of building either — plus triggers — see the **Automation** section.)

### What a macro is

**Default:** none beyond the seeded numpad defaults (see below)
**What it does:** Binds a key combination (with optional Ctrl/Shift/Alt) to a text command that's sent to the game instead of the literal keystroke, whenever you're focused on the terminal or the Conversation window's input box.
**How it works:** A macro's command can contain several steps separated by `^M` or `;` — each piece is sent as its own line, with no delay in between. There's no built-in timed pause between steps; if you need to wait for the game to respond before the next command, a trigger that fires on that response is the tool instead. Each macro also has its own Enabled flag; a disabled macro is skipped entirely.

### Default numpad macros

Every brand-new character profile starts with the numpad wired to compass movement:

| Key | Command |
|---|---|
| Numpad 8 | n (north) |
| Numpad 2 | s (south) |
| Numpad 4 | w (west) |
| Numpad 6 | e (east) |
| Numpad 9 | ne |
| Numpad 7 | nw |
| Numpad 3 | se |
| Numpad 1 | sw |
| Numpad 0 | u (up) |
| Numpad . | d (down) |

**When you might change it:** Remap or delete these if you use the numpad for something else, or prefer a different movement scheme.
**Important notes:** These seed macros only appear on a character that has **never** saved any macro configuration at all. The moment you save any macro setup — even an empty one — the seeded numpad defaults are gone for good on that character; they won't come back.

### What an alias is

**Default:** none.
**What it does:** A typed-command shortcut — you type a short name and MudPlay expands it into a longer command before sending, matched on the first word of the line (case-insensitive). Aliases expand only when you press Enter in the **Conversation** window's input box — typing in the main terminal sends each keystroke straight to the game and bypasses alias expansion.
**How it works:** The rest of the line after the alias name fills positional placeholders in the expansion: `{0}` is the entire rest of the line, and `{1}`, `{2}`, … are the individual whitespace-separated words. So an alias `cast` → `c '{1}' {2}` turns `cast heal bob` into `c 'heal' bob`.
**Important notes:** An alias name that would collide with a MajorMUD chat-channel command is rejected in the editor, so an alias can't hijack your chat. Aliases are separate from macros (key → command) and from triggers (auto-responses to game text).

---

## BBS + Display (Connection & Network)

Settings → "BBS + Display" — despite the plain "BBS" name in some places, this tab also carries terminal-size/scrollback settings and, at the very bottom, the four global confirmation-prompt checkboxes (documented separately below since they're Global-tier, not per-BBS). Connection fields here are stored per-BBS — shared by every character who connects to that board — except credentials, which are per-character.

### Name

**Default:** empty
**What it does:** The display name for this saved BBS entry (also its on-disk filename).
**Important notes:** Renaming moves the underlying save file (and every character profile stored under it) to the new name the moment you save — not deferred to a later step. If another entry already has that name, the rename is silently rejected.

### Host / Port

**Default:** Host empty; Port `23`
**What it does:** The address and TCP port MudPlay dials. Port 23 is the standard Telnet port; some boards use a different port for their door-game access.
**Important notes:** Both only take effect on your *next* connect attempt — changing them mid-session doesn't affect an already-open connection.

### Max redials / Redial pause (s) / Infinite retries

**Default:** Max redials `3`, Redial pause `5` s, Infinite retries Off.
**What it does:** Controls how persistently MudPlay tries to reconnect once a reconnect is actually triggered (see the "Reconnect when…" toggles below — these numbers don't by themselves cause any reconnect attempts). Max redials caps the total number of tries; Redial pause is the wait between each. "Infinite retries" overrides both — it retries forever at a fixed 3-second pause.
**When you might change it:** Turn on Infinite retries for a flaky board you want the client to keep hammering unattended rather than giving up after a handful of tries.
**Important notes:** Max redials and Redial pause are greyed out (irrelevant) while Infinite retries is checked.

### No-response (s)

**Default:** `20`
**What it does:** How many seconds of total silence on the wire before MudPlay's underlying network connection starts actively probing to check if it's still alive. `0` disables the idle keepalive probing — but MudPlay still caps dead-connection detection at about 60 seconds either way.
**When you might change it:** Lower it for faster detection of a dead link; raise it on a connection that goes quiet for long stretches while still alive, to avoid probing too eagerly.
**Important notes:** Detecting a dead connection this way doesn't reconnect you by itself — you also need "Reconnect when: Server stops responding" (below) turned on. Only applied at the moment you connect, so a change here takes effect on your next connection, not the current one.

### Reconnect when: Connect attempt fails / Carrier is lost mid-session / Server stops responding / After Cleanup

**Default:** all Off
**What it does:** Four independent triggers for automatic redialing: a failed initial connect attempt, the connection dropping mid-session, the server going silent long enough for the No-response check above to flag it dead, or the BBS's scheduled nightly cleanup finishing. "After Cleanup" is a two-part behavior: it also makes MudPlay proactively exit to the main menu and hang up *before* the BBS forcibly disconnects it, once a "shutting down soon" warning is seen.
**Important notes:** "Server stops responding" fires once MudPlay detects the connection is dead — the "No-response (s)" value above sets how quickly that happens (even at `0`, a hung server is caught within about 60 seconds). "After Cleanup" depends on the "Cleanup wait (m)" field below to know how long to wait before redialing.

### Cleanup wait (m)

**Default:** `0`
**What it does:** Extra minutes to wait, on top of the BBS's own announced cleanup window, before redialing after a cleanup-triggered disconnect. Only matters if "Reconnect when: After Cleanup" is on.
**When you might change it:** Pad this if your BBS's nightly maintenance routinely runs longer than it announces.

### Columns / Rows (terminal size)

**Default:** 80 columns × 25 rows.
**What it does:** The terminal size MudPlay advertises to the server at connect time.
**Important notes:** MajorMUD itself renders against a fixed 80×25 grid and won't reflow to a larger size — raising these numbers only helps with non-game BBS menus/doors that do reflow.

### Scrollback (lines)

**Default:** `4000`
**Available options:** 100–100,000
**What it does:** How many scrolled-off lines the Backscroll window's history buffer keeps.
**Important notes:** This one is an exception to the "changes apply live" rule — it only takes effect on your **next launch** of MudPlay, not immediately.

### Wheel scroll (lines)

**Default:** `5`
**Available options:** 1–50
**What it does:** How many rows one notch of your mouse wheel scrolls inside the Backscroll window.
**Important notes:** Unlike Scrollback lines above, this one *does* apply live.

### Username / Password (credentials)

**Default:** both empty
**What it does:** Your login for this specific BBS, saved per-character (so two different characters logging into the same board keep separate credentials).
**Important notes:** Encrypted at rest — plaintext passwords never touch disk. The password field starts blank when you open Settings and only reveals the saved value if you click **Show**; leaving it blank and saving preserves whatever was already stored (it won't blank out your saved password).

### Suicide password (read-only)

**Default:** none stored
**What it does:** Shows the MajorMUD suicide password MudPlay has on file for this character — and only appears when one is stored. This row is **read-only**: the client captures the password passively when you run `set suicide` in the game, then keeps an encrypted copy so the `@suicide` remote command can supply it automatically. Click **Show** to reveal it.
**Important notes:** Saved per-character (encrypted at rest), even though it sits on the BBS tab. You can't type into it — to change the password, run `set suicide` in-game again; to clear it, run `pro` in-game and observe "You do not have a suicide password set." and MudPlay drops its stored copy.

### I have sysop / goto powers on this BBS

**Default:** Off
**What it does:** Marks this character as having elevated privileges on this specific board — e.g. lets the `@goto <player>` remote command skip a permission check that would otherwise apply.

### Automated Logon Menu Navigation

**Default:** empty list
**What it does:** A sequence of "wait for this text, then send this reply" steps MudPlay walks through after your username/password to reach the actual game (skipping "press any key" prompts, picking door-game menu options, etc). The reply text can include placeholders like `{user}` and `{pass}` that get filled in with your saved credentials automatically.
**When you might change it:** Set this up once per BBS so logging in is fully automatic. You can also import a working sequence from another saved character if several boards you play share the same login flow.

### Game entry command / Game exit command

**Default:** `E` / `=x`
**What it does:** The literal keys sent at the main menu to enter the game realm, and to log off cleanly.
**When you might change it:** Only if a particular board remaps its main-menu options away from the MajorMUD-standard letters.

### Player dies at (HP)

**Default:** `-25`
**What it does:** The negative HP value at which this board's realm actually kills a character (0 HP alone just "drops" you — bleeding out but revivable). Used by the emergency-hangup safety logic to know how far into negative HP it's safe to let things go.
**Important notes:** With "Auto-refine the floor from slow deaths" (below) on, MudPlay learns the real number over time from observed deaths and updates this automatically.

### Boss cleanup time / Boss cleanup zone

**Default:** `21:00`, your computer's local time zone.
**What it does:** The BBS's daily maintenance time. Some boss monsters only respawn at this specific wall-clock time rather than on a countdown timer — MudPlay's boss tracker uses this to know when a "cleanup-only" boss should flip back to alive.

### Board disconnect line

**Default:** blank (built-in lines only)
**What it does:** An optional extra logoff line for MudPlay to watch, on top of the built-in "just disconnected" / "just hung up" forms. Some boards emit a custom logoff line keyed on a player's **account** name rather than their character name, which the standard detection misses — so a party member's drop slips past and the party runs off without them. Teaching MudPlay that line means the drop is caught and the party waits for them.
**How the options work:** Uses the same literal syntax as triggers — `{name}` captures the disconnecting player (matched against a member's account-name override in Game Data → Players, else their character name), and `*` matches a varying run (e.g. a trailing "Lines in Use: N" count). Example: `►►► [{name}] logs OFF*`.
**Important notes:** BBS-tier — the line is shared by every character who plays this board. Leave it blank on boards that use the standard disconnect wording.

### Name of runic currency

**Default:** `runic`
**What it does:** Some boards rename MajorMUD's top currency denomination to a board-specific word. This field tells MudPlay what that word is on this particular BBS, so cash automation keeps parsing coin messages correctly.

---

## Confirmation Prompts

Found near the bottom of the "BBS + Display" tab, under a "Show confirmations" heading. All four are **Global-tier** — one shared preference across every BBS and every character on this install — and all default to **off**, so a fresh install has no nagging popups.

### Confirm exit

**Default:** Off
**What it does:** Pops up an "are you sure?" prompt before MudPlay closes (window X, File → Quit, or the quit shortcut).

### Confirm hangup

**Default:** Off
**What it does:** Prompts before a disconnect **you** explicitly triggered (a toolbar button, hotkey, or menu item). Automatic disconnects — a dropped connection, a remote `@hangup` from someone else — never prompt, regardless of this setting.

### Confirm save settings

**Default:** Off
**What it does:** Prompts "Save your changes?" before the Settings window's OK/Apply actually writes anything. Answering "No" returns you to the editor with nothing saved and the window still open. (Game Data browser edits save immediately and aren't gated by this prompt.)

### Confirm deletes

**Default:** Off
**What it does:** Prompts before destructive deletions — deleting a saved BBS profile, removing a navigation favorite or Game Data record, and similar. (Removing a toolbar button is not gated by this prompt.)

**Important notes (all four):** Applies live the moment you click OK/Apply on Settings — no restart needed.

---

## Combat

Settings → Combat. Two switches live *outside* this tab and gate everything here: **Auto-Combat** (Settings → General, or its toolbar toggle) must be on for any of this to matter at all; **Auto-Nuke** separately gates the **multi-attack** and **AoE-debuff** spell slots (single-target attack spells aren't considered "nukes" and stay available regardless). The **single-target debuff** is part of the attack rotation, so it follows **Auto-Combat**, not Auto-Nuke.

A debuff slot only accepts a **0-energy** between-round spell — an attack spell (which costs energy) can't be a debuff — with **slot-appropriate targeting**: a single-enemy scope for the single-target slot, an area/room scope for the AoE slot. A mismatch (an attack spell, or a targeted spell in the AoE slot / an AoE in the single slot) is flagged right under the slot on this tab and refused at cast time with a program-log note.

### Action order

**Default:** `Spells first`
**Available options:** `Spells first`, `Physical first`, `Alternate — spell, then physical`, `Alternate — physical, then spell`, `Custom round cycle`.
**What it does:** The single most important combat setting — it decides what your character does each round: cast an attack spell, or swing the weapon.
**How the options work:**
- **Spells first** — always tries your attack spells before falling back to the weapon, and only swings once every configured spell fails to fire that round (out of mana, hit its cast cap, target immune, and so on).
- **Physical first** — always swings the weapon first, and only turns to spells once the weapon path is *proven* useless against this specific target (it can't hurt this monster and there's no working backup weapon either).
- **Alternate (either direction)** — flips your preferred action every single round; a round whose preferred type can't fire falls back to the other type for that round only, so a round is never wasted.
- **Custom round cycle** — spend a set number of rounds swinging, then a set number of rounds casting, on repeat (see *Round cycle* below).
**When you might change it:** *Physical first* for a melee build that shouldn't burn mana on trash mobs; *Spells first* (default) for a caster; *Custom round cycle* for something like "swing twice, then nuke until it dies."
**Important notes:** Two things always sit above this choice: a backstab opener always fires first when eligible, and debuff spells (see the Spells tab) are a separate "extra" action that can land the same round as your main choice. Applies live, mid-fight.

### Round cycle (Physical rounds / Spell rounds / Start on spell)

**Default:** 1 physical round / 1 spell round / starts on physical.
**What it does:** Only matters when Action order is `Custom round cycle`. Sets how many rounds to spend swinging before switching to spells, and vice versa, on repeat for the whole fight.
**How the options work:** A `0` in either field makes that phase permanent once reached — e.g. 2 physical rounds and 0 spell rounds means "swing twice, then cast spells for the rest of the fight."
**When you might change it:** A hybrid build that wants to open with a couple of weapon swings (to build up a resource) before nuking.
**Important notes:** These fields stay visible even when Action order isn't Custom, so a tuned value isn't lost if you switch away and back.

### Normal / Alternate weapon attack command

**Default:** `a` (both)
**What it does:** The literal command word MudPlay sends each round to attack — `a` is the standard MajorMUD attack alias. The Alternate command is used instead whenever you're swinging your configured alternate weapon, since some off-hand or two-handed weapons want a different verb.
**When you might change it:** Only if your class or realm uses a non-standard attack word.

### Weapon slots (Normal / Alternate / Backstab weapon)

**Important notes:** These fields exist in the underlying data, but they are **not** edited from the Combat tab — your actual weapon choices come from the Character Workshop's Equipment Manager gear sets (a "Default" set feeds your normal/alternate weapons, a "Backstab" set feeds your stealth gear) and get applied automatically. The Combat tab only holds the attack-verb text fields and the backstab-behavior checkboxes.

### Target order

**Default:** `Normal`
**Available options:** `Normal`, `Reverse`
**What it does:** When several hostile monsters share a room, this decides which one gets attacked first, based on the priority ranking you set per-monster in Game Data. `Normal` goes after the highest-priority monster first; `Reverse` clears the lowest-priority (weakest/least important) monster first.
**When you might change it:** `Reverse` if you'd rather clear trash before tackling the room's most dangerous monster.
**Important notes:** Only applies when Target Priority (below) is `Default` — the "follow" modes override target choice entirely.

### Target Priority

**Default:** `Default`
**Available options:** `Default`, `Attack what party leader attacks`, `Attack what player attacks`
**What it does:** Controls *who* you target while partying. `Default` uses your own priority list plus Target order above. The two "follow" modes make you mirror whatever target the party leader (or a named player) is currently attacking, instead of choosing your own.
**When you might change it:** Group play where you want everyone stacking damage onto one target instead of spreading across the room.
**Important notes:** If you can't actually hurt the monster you're told to follow, MudPlay falls back to your own next actionable target rather than getting stuck doing nothing.

### Player name (Target Priority)

**Default:** empty
**What it does:** The specific player to mirror when Target Priority is set to `Attack what player attacks`.

### Attack Order

**Default:** `Default`
**Available options:** `Default`, `AttackLastParty`, `AttackLastRoom`, `AttackAfter` (shown verbatim in the dropdown).
**What it does:** Pure timing — controls *when* you re-announce your own current target relative to other people's attacks, for coordinating who "goes" in what order. It never changes *what* you're targeting — that's Target Priority's job.
**When you might change it:** A tank who wants to always commit their attack last, after everyone else in the party has already gone.

### Attack-after player name

**Default:** empty
**What it does:** The player Attack Order re-fires after, when Attack Order is set to `AttackAfter`.

### Polite mode ⚠️ Not currently functional

**Default:** `Off`
**Available options:** `Off`, `WaitForOthers`, `SkipRoom`, `AttackDifferent`
**What it's intended to do:** Govern how you handle a monster another (non-party) player is already fighting — wait for them to finish, skip the room entirely, or pick a different target.
**Important notes:** This control is fully present and editable on the Combat tab, but tracing the code shows **no part of the automation engine actually reads this setting** — changing it currently has no effect on how you fight. It's documented here so you don't spend time tuning something that doesn't do anything yet.

### Min. / Max. monsters (room-skip thresholds)

**Default:** Min `0`, Max `20`
**What it does:** Skips engaging a room entirely if the number of hostile monsters in it falls outside this range — too few to bother stopping for, or too many to be safe. The defaults are effectively a no-op (rooms cap at 20 monsters anyway); you opt in by tightening either bound.
**Important notes:** Only applies while you're actively walking through rooms (a route, loop, or lair run) — if you're just standing still with nothing else queued, you fight regardless of count, since standing undefended is worse. While in a party, the Party tab's own monster cap overrides this Max (the Min still comes from here).

### Do BS attacks (backstab)

**Default:** Off
**What it does:** When on, attempts a backstab as the very first action when you enter a room with a sneakable target. Backstab only ever lands on that opening action — once anything else has happened in the room (a spell, a swing, another backstab attempt), the surprise is gone for that room until you leave and re-approach freshly.
**Important notes:** A monster with the "see-hidden" ability reveals you before the opener, forcing a normal attack instead. A successful backstab is silent (no public "moves to attack" announcement) — you only know it worked from the "surprise" damage line.

### Don't BS if multi-attack room spell is firing

**Default:** On
**What it does:** Skips the backstab attempt on a round where your configured room-wide attack spell would otherwise fire, so you don't waste a sneak opener on an AoE round.

### Run if BS fails

**Default:** Off
**What it does:** Automatically triggers flee behavior if your backstab attempt clearly failed (no "surprise" in the result line) — on the theory that a failed backstab means the target is now fully alert and the fight is riskier than planned.

### Clear hostiles when sneak broken by see-hidden monster

**Default:** Off
**What it does:** A safety valve for stealth routes: if Auto-Combat is off and Auto-Sneak is on (you're trying to sneak through a route untouched) and you stumble into a room with a see-hidden monster, your stealth breaks. With this on, MudPlay fights and clears that one room instead of continuing to walk while exposed and dragging monsters behind you — bypassing the Min/Max room-skip gate for just that room.

### Run distance

**Default:** `2` rooms
**What it does:** How many rooms MudPlay flees before re-checking whether it's safe to stop.

### Go backwards if running

**Default:** On (backward)
**What it does:** When fleeing, `Backward` retraces the rooms you just came through (safer — you already know what's there); unchecked (`Forward`) instead keeps pushing along your planned route into unexplored territory (faster, riskier).

### Break combat before running

**Default:** On
**What it does:** Sends a `break` command before the first flee move so you disengage cleanly first. Turning it off starts fleeing immediately, which is faster but the game may reject the first move since you're technically still fighting, wasting a round.

### Minimum mana per cast — Percentage / Value

**Default:** `Percentage`
**What it does:** Decides how every "Min mana per cast" field on the five spell slots below is read — as a 0–100% share of your maximum mana, or as a flat number.

### Combat spell slots (Multi-attack / Debuff AOE / Debuff single-target / Normal attack / Alternate attack)

**Default:** all unset
**What it does:** This is the heart of MudPlay's spell-combat automation — five rows, each assigned one role:
- **Multi-attack** — a room-wide damage spell, cast with no target (room spells hit everyone; naming a target gets it rejected by the game).
- **Debuff (AOE)** — a room-wide debuff, also cast bare.
- **Debuff (single target)** — a single-target weakening spell.
- **Normal attack spell** — your primary single-target damage spell.
- **Alternate attack spell** — a backup single-target damage spell, used only once the primary can't fire that round.

Each row also has **Min enemies** (don't cast this slot below this many hostiles in the room — ignored on the three single-target rows), **Max casts** (a repeat cap — blank means unlimited, `0` means never, a number caps it; this counts combat *rounds* spent on the spell, not individual casts, and resets per-target for the three single-target rows but per-room for the two AoE rows), and **Min mana per cast** (a mana floor, read per the Percentage/Value toggle above).

**Picking a spell (learned-spell guard):** each slot is a typeahead — start typing a **cast-code or name** and it lists your class's spells (it commits the 4-letter code). Spells your character **hasn't learned yet** are shown **struck through and dimmed** in the list, and if a slot is pointed at one the box **outlines red** as a warning — so you can't quietly misconfigure a slot with a spell you can technically learn but haven't (the value is still saved; the red outline is only a heads-up). The same picker and guard are on **Settings → Spells** (heals, cures, bless). The guard needs to know what you've learned: type `spells` (or `stat`) in the game once so it can read your spell list — until then nothing is flagged. It also updates the moment you learn a spell mid-session (reading a teaching item, e.g. *"You add agony to your spellbook!"*).

**How the cascade works each round:** A pending backstab always wins first. Then, whichever action type (spell or physical) your Action Order setting prefers gets tried; on the spell side, the order is Multi-attack → Normal attack → Alternate attack, falling through to the weapon if nothing can fire. Debuffing is a separate "extra" action that can land the same round as your main attack. Once you commit to a single-target spell against a specific monster and it later becomes unaffordable, MudPlay sticks with the weapon for the rest of that fight rather than flip-flopping back once mana regenerates.
**Important notes:** Once a spell is announced, it auto-repeats server-side every round exactly like a weapon swing — MudPlay does **not** re-send the cast command every round, only when the situation actually changes (target dies, cap hit, mana too low, target proves immune).

### Drain (life-steal) spell

**Default:** unset (HP trigger 50%, "Drains override AOE" off)
**What it does:** Some mage spells (e.g. `vamp`, `dtch`, high-level `nebo`) are **life-drain** spells — the damage they deal also **heals you**. This slot treats one as an *emergency heal that also attacks*: when your HP falls to the **Heal when ≤ HP** percentage, the drain takes the round in place of your normal attack (whatever it would have been — an attack spell or a weapon swing), then the engine reverts to your normal pick once HP recovers a little past the trigger (a small hysteresis margin, so it can't flip-flop drain↔normal every round) or mana drops below the drain's **Min mana per cast**. It has the same **Max casts** (per-target) and **Min mana** fields as the other single-target rows; **Min enemies** doesn't apply.

**Targeting:** a drain can only affect a **living, non-undead** target — there's no life to steal from a construct or a skeleton — so against a NonLiving or Undead monster the drain is skipped and MudPlay falls back to your normal attack cascade for that fight. (If game data is thin, the game's own "no effect" reply is caught as a backstop.)

**Drains override AOE:** by default the drain **yields to your room AoE** — if you have enough enemies present to trigger the Multi-attack spell, rooming is usually the safer play, so the AoE keeps firing and the drain only overrides single-target / weapon rounds. Check this box to let the drain pre-empt the AoE too, when your loop calls for it.

A per-monster override configured in Game Data can substitute a different spell or command for a specific monster species, bypassing some of these gates — worth checking if a particular monster seems to ignore your setup here. In the monster editor's **Override Attack** box you can type a `Spell.Number` **or** the spell's cast-code (e.g. "turn") — either way it casts through the mana-gated spell rung (set a Max); only a non-spell verb like "attack"/"bash" is sent verbatim as an ungated raw command. The override applies to the monster record **placed or summoned in your current room**, so a name shared across zones (a "zombie" in the graveyard vs the tunnels) picks the right one — an override you set on the graveyard zombie won't bleed onto the tunnels zombie.

At **0 mana** a mana-costing action can't land (the server silently ignores it), so the engine falls back to your physical weapon and resumes casting once mana recovers.

### Show combat round totals ⚠️ Not currently functional

**Default:** Off
**What it's intended to do:** Append a running total of damage dealt to the terminal after each combat round.
**Important notes:** Like Polite mode above, this control is fully editable but **not consumed anywhere in the automation engine** — turning it on currently has no visible effect.

---

## Spells (+ Ailments)

Settings → Spells. This tab picks *which spell* fills each automated role and sets the *priority order* the caster walks through each tick. The actual HP/mana percentage *thresholds* that trigger a cast live on the **Health** tab, not here.

### Spell type priority

**Default order (highest priority first):** Minor party heal → Major party heal → Minor self heal → Major self heal → Curing → Buffing → Debuffing.
**What it does:** Every tick, MudPlay checks all seven categories and casts the highest-priority one that has something ready to fire. Use the **▲ / ▼** arrows on each row to reorder them — higher in the list casts earlier.
**When you might change it:** Move Curing above self-heals if you'd rather cure a debilitating ailment before topping off HP; move Debuffing higher if landing your debuff matters more to you than proactive buffing.
**Important notes:** A downed ally rescue always jumps the queue no matter how you rank things — it's not part of this list and can't be demoted.

### Minor heal / Major heal

**Default:** unset
**What it does:** Your primary self-heal spell (Minor) and your emergency self-heal spell (Major). Minor fires at the Health tab's Minor thresholds; Major fires at the (lower) Major/life-threat threshold. If you haven't set a Major heal, MudPlay uses Minor heal at the Major threshold instead of doing nothing.

### HP Regen

**Default:** unset
**What it does:** A heal-over-time spell. When your Minor-heal threshold trips, this is cast *first*, ahead of an instant heal — but only while you're still above the Major/life-threat threshold, and only if it isn't already active. Inside the life-threat band, you always get an instant heal instead.
**When you might change it:** If you want this HoT kept up permanently rather than only cast reactively, put it in a Bless slot instead.

### Mana Regen

**Default:** unset
**What it does:** A spell cast during downtime to boost mana regeneration — either a "roll" spell (rerolls a random regen bonus each cast) or a flat mana heal-over-time buff.
**Important notes:** The reroll feature below only works with roll-type spells, and only on certain realms (see next entry).

### Reroll if roll below / Max rerolls

**Default:** Threshold unset (off); Cap `3`
**What it does:** After the Mana Regen roll spell lands, if the roll came in below this threshold, MudPlay recasts to try for a better one, up to the cap. Each recast runs through the normal between-round priority (like every other automated cast), so a due heal or cure fires ahead of a reroll and only one between-round cast goes out per combat round.
**Important notes:** Only works on realms that expose a way to read the actual roll value back — inert on realms that don't. Leave the threshold blank to disable rerolling entirely.

### When HP full / When Mana full

**Default:** unset
**What it does:** A spell cast whenever the matching pool (HP or mana) is sitting completely full — a way to spend a maxed-out resource on something useful instead of letting it sit idle.

### Cure Holds / Cure poison / Cure disease / Cure blindness

**Default:** unset
**What it does:** The specific spell used to cure each named ailment. These feed the Curing priority category (self first, then party members).

### Room light

**Default:** unset
**What it does:** A spell automatically cast when you enter a dark room (works together with the light-item automation on the Auto-Light tab).

### Bless spells

**Default:** all empty (10 rows on a Stock realm, 15 on ParaMud)
**What it does:** A ranked list of self-buffs MudPlay recasts whenever they aren't currently active (the on-screen section is headed **Bless spells**, its rows labeled Bless 1, Bless 2, …). Row order is cast priority. Each row can hold a spell code or a `#item name` for a reusable buff item. The same learned-spell guard applies here, with one twist for the `#item` entries: an item-cast is only flagged (struck through / red-outlined) when you **aren't carrying or wearing that item** — a buff item you actually hold is treated as available, just like a learned spell.
**How the options work:** Each row has its own "recast within (seconds)" — how early before the buff's tracked expiry MudPlay proactively recasts it. It defaults to **15**; set it to `0` to wait for the buff to actually wear off before recasting.
**Important notes:** The visible row count matches your active realm's buff-slot cap; extra picks beyond that cap aren't lost, they just wait until you load a realm that supports more.

### Bless self while resting / Bless self during combat

**Default:** both Off
**What it does:** Two opt-in overrides for your own buffs. With both off (the default), the engine buffs while you're **moving or standing idle** — including an idle rest — and holds off **during combat** and **during a triggered recovery rest** (HP or MA fell below your rest-if-below setting and you're resting back up). "Bless while resting" lets it also buff during that recovery rest; "Bless during combat" lets it also buff mid-fight (casting spends that round). Note "while resting" means a *triggered recovery rest* only — idle resting always buffs.
**When you might change it:** Turn on "during combat" for a fast hunting loop that rarely stays out of combat long enough to bless between fights; turn on "while resting" if you'd rather top off your buffs during recovery downtime than wait until you're back on your feet.

### Ignore poison / blindness / confusion / diseased

**Default:** all Off (i.e. every ailment pauses the party)
**What it does:** Normally, catching one of these ailments makes MudPlay ask the party leader to pause (`@wait`) until it clears. Checking a box here suppresses that pause request for that specific ailment — useful for "push through it, don't stop the group" situations.
**Important notes:** Some conditions (over-encumbered, being held, being stunned) always pause regardless of these checkboxes — they can't be suppressed this way.

### Don't announce poison / blindness / confusion / diseased

**Default:** all Off (i.e. announce)
**What it does:** Separately from the Ignore checkboxes above, suppresses broadcasting your ailment status to other MudPlay users in your party (who otherwise mirror it on their own party display). You can suppress the pause and keep announcing, or vice versa — the two are independent.

---

## Health

Settings → Health. Two stacked sections — **Health (HP)** on top, **Mana / Kai** below — each independently switchable between percentage and raw-number thresholds.

### Percentage / Value (mode picker)

**Default:** `Percentage` (both HP and Mana)
**What it does:** Switches whether every threshold in that section means "a percentage of your max pool" or "an absolute number." Switching modes doesn't rescale the numbers you've entered — each value is simply re-read against the new scale (and switching to Percentage clamps anything above 100 down to 100). A small live readout beside each field shows the equivalent in the other scale.

### Rest max (HP / MA)

**Default:** 95% (both)
**What it does:** Once resting, MudPlay stops and stands back up once the pool reaches this value. Both HP and Mana need to reach their own target before you stand (unless your class has no mana pool).

### Rest if below (HP / MA)

**Default:** HP 60%, Mana 30%
**What it does:** The trigger for auto-resting. Once a pool drops to or below this, MudPlay pauses movement and starts resting the moment combat ends (never mid-fight).

### Heal (rest)

**Default:** 80%
**What it does:** While actually resting, cast the Minor heal spell if HP is still below this — a way to speed along recovery rather than waiting on the passive rest tick alone.

### Minor heal (combat) / Major heal (combat)

**Default:** Minor 70%, Major 40%
**What it does:** During a fight, cast the Minor heal spell once HP drops to this level, and the Major (emergency) heal once it drops to the lower Major level.
**Important notes:** Both are also gated by a mana floor ("Heal if above," below) — if your mana is too low, the heal is skipped so mana can regenerate instead, unless that floor is set to 0.

### Run if below (HP / MA)

**Default:** HP 20%, Mana 10%
**What it does:** Triggers flee behavior when either pool (HP *or* mana) drops to or below its own trigger — an out-of-mana caster is treated the same as a low-HP fighter. MudPlay resumes normal activity only once **both** pools have recovered back above their triggers.
**Important notes:** Set either to `0` to disable that pool's flee trigger — useful for a class with no mana pool.

### Hang up if below

**Default:** 5%
**What it does:** The absolute last resort: disconnects the game outright once HP falls to or below this value. Since 0 HP only "drops" you in MajorMUD rather than killing you outright, this threshold can go negative, all the way down to (but never past) the point your BBS's realm actually treats as death.
**Important notes:** There's no "0 disables it" here — to fully disable the emergency hangup, use the toolbar's "Disable hangups" toggle instead.

### Heal if above (rest/idle) / Heal if above (combat)

**Default:** Resting 50%, Combat 0% (disabled — always heal)
**What it does:** A mana floor that gates self-heal casts — below this, MudPlay skips the heal so mana can regenerate. `0` disables the gate entirely (always heal regardless of mana).

### Bless if above

**Default:** 70%
**What it does:** Re-casts your buffs only once mana has climbed back above this value — letting mana recover past a floor before you spend it on upkeep rather than survival.

### Use 'meditate' ability

**Default:** Off
**What it does:** Uses the class-specific `meditate` command instead of `rest`, on classes that have it.

### Meditate before resting

**Default:** Off
**What it does:** Only relevant with "Use 'meditate' ability" on. If both HP and mana are low at the same time, this decides whether MudPlay meditates first to top off mana before resting for HP. If only mana is low, MudPlay always meditates regardless of this setting.

### Utilize shadowrest

**Default:** Off
**What it does:** ShadowRest is a class ability on certain realms (not stock MajorMUD) that lets a stealthed character rest safely even with a monster in the room. With this on — and your class has the ability, and you're solo and currently hidden/sneaking — MudPlay uses that instead of retreating to rest.
**Important notes:** This checkbox only appears at all on realms that actually have a class with the ShadowRest ability; it's invisible on stock realms.

### Pre-rest / meditate command, Post-rest / meditate command

**Default:** empty (both)
**What it does:** Custom commands sent right before entering rest/meditate, and right after standing back up — e.g. checking your surroundings first, or re-arming something the moment you stand.

---

## Party

Settings → Party.

### Rank

**Default:** `Mid`
**Available options:** `Front`, `Mid`, `Back`
**What it does:** Records your preferred combat position in a party. It's a saved preference only — it doesn't send any in-game command, and the automation doesn't act on it yet (it's reserved for future target-ordering).
**When you might change it:** Set it to reflect your role, but don't expect it to change behavior on its own today.

### Minor / Major Party Heal — Single-target and Party (AOE) spells

**Default:** all four blank
**What it does:** The spells MudPlay auto-casts to heal hurt party members — a cheap single-target pick and a group/AOE pick, for both the routine (Minor) and critical (Major) tier.
**Important notes:** When enough party members are hurt at once, the AOE pick is used instead of the single-target one — see the next setting.

### Minor/Major heal threshold (%)

**Default:** Minor 70%, Major 40%
**What it does:** The HP percentage below which the matching heal tier fires on a party member.

### Use party healing spells when N or more members meet threshold

**Default:** `2`
**Available options:** 2–6
**What it does:** How many hurt party members are needed at once before MudPlay switches from single-target healing to the AOE/group heal pick.

### Request healing (@heal broadcast) — not functional

**Important notes:** This control exists in the UI but has no setting behind it — it's a placeholder for a future feature, currently fixed and greyed out.

### Party bless (10 slots)

**Default:** all empty
**What it does:** Up to 10 beneficial spells you want auto-cast on party members, each restricted to specific classes (e.g. cast a warrior buff only on Warriors and Barbarians). Row order is cast priority.
**How the options work:** The first time you type a spell into an empty slot, every currently-known class gets auto-checked as a convenience — untick the ones you don't want it cast on. Each row also has its own **recast within (s)** — how early before the buff's tracked expiry to recast it; it defaults to **15**, and `0` waits for the buff to actually wear off.

**Party-only, and targeting:** the party-bless slots are cast **only while you're actually in a party** — solo, none of them fire (your self-bless slots still do). A **party-wide** spell (chant and the like — one cast blankets the whole party, including you) is sent once with no target. A **single-target** spell is cast on each class-matched member individually with its own recast timer per person, and it is **not** cast on your own character — self comes from the self-bless slots.

**Supersession:** if a party-wide party buff *removes* a spell you have in a self-bless slot (the Spell Book shows it as "Removes …" — e.g. **chant removes bless**), then in a party the client stops self-casting the removed spell and lets the party buff cover you. The Buff Watchdog shows that self-buff row as **"covered by"** the party buff instead of a timer.

### Bless party while resting / Bless party during combat

**Default:** both Off
**What it does:** The party-bless mirror of the self-bless overrides. Party buffing runs under the same Auto-Bless toggle and the same rule: with both off (the default) it buffs the party while moving or standing idle (including an idle rest) and holds during combat and during a triggered recovery rest. "While resting" adds a triggered recovery rest; "during combat" adds mid-fight. As with self-bless, "while resting" means a *triggered recovery rest* — idle resting always buffs.

### Help leader open doors

**Default:** Off
**What it does:** When you see your party leader failing to bash a locked door, you automatically pitch in (bashing or picking, depending on your own door-preference setting).

### Ignore @wait when leading

**Default:** Off
**What it does:** Normally, if any party member sends `@wait`, your automation pauses until they say `@ok`. With this on, **while you're the leader**, incoming @wait requests are ignored — your automation keeps running instead of stalling for a follower.
**When you might change it:** Leading a group where you don't want one slow member to stall everyone else's progress.

### Reset statistics on loop start

**Default:** On
**What it does:** At the start of every loop or Auto-Lair run, broadcasts a stat-reset request to the whole party so everyone's kill/exp counters start from zero together, for a clean comparison.

### Re-invite lost party members

**Default:** On
**What it does:** Leader-only. If a party member disconnects and reconnects within the grace window (see "If leading, wait only" below), you automatically re-invite them instead of having to notice and do it manually.

### Send @join nags to invited members

**Default:** On
**What it does:** After inviting someone, MudPlay follows up with reminder nags if they haven't joined yet, on a repeating cadence, until they join, decline, or the attempt window runs out.

### First nag after (seconds) / Resend frequency (seconds) / Max attempt window (seconds)

**Default:** 5s / 10s / 55s
**What it does:** Controls the nag cadence described above — how long before the first nag, how often it repeats, and the total time before MudPlay gives up. This cadence is shared between the @join nag and the @health nag below.

### Send @health nags to party members

**Default:** On
**What it does:** When someone joins your party, MudPlay asks them for their current HP/mana so it can display real numbers rather than percent-only, retrying on the shared nag cadence above until it gets a real answer.

### Probe party members' level & version on the first party of the day

**Default:** On
**What it does:** The first time you party with a given player on a given day, MudPlay quietly asks for their level and client version to record on their player profile.

### Max. monsters when partying

**Default:** `20`
**What it does:** While actively partied, this caps how many hostile monsters in a room MudPlay's combat engine will engage — overriding (only) the upper bound of the Combat tab's own room-monster cap while you're grouped up.
**Important notes:** Since the default (20) matches the Combat tab's own default, this is a no-op out of the box — you have to lower it for it to matter.

### Wait if members are below (%)

**Default:** `0` (disabled)
**What it does:** Pauses the whole party's automated movement while any observed member's HP is below this percentage, so the group holds position instead of leaving someone behind to recover.

### If leading, wait only (s)

**Default:** `90` seconds
**What it does:** As leader, how long you keep watching for a disconnected member to come back before giving up on them.

### Return distance (rooms)

**Default:** `30`
**What it does:** How far (in map rooms) the leader is willing to walk to go retrieve a reconnected party member. Beyond this, the leader gives up on walking over and tells them to catch up on their own.

### par poll frequency (s)

**Default:** `5` seconds
**What it does:** How often MudPlay checks in-game party status to keep everyone's info current.

---

## Cash + Items

Settings → Cash + Items.

### Per-currency policy (Copper / Silver / Gold / Platinum / Runic)

**Default:** Copper = `Ignore`; everything else = `Collect`
**Available options:** `Collect`, `Ignore`, `Discard`
**What it does:** What MudPlay does automatically whenever it sees each coin type on the ground or as loot. `Collect` picks it up. `Ignore` leaves it alone. `Discard` means if you're already holding any of that coin, MudPlay drops it (it won't pick new piles up, but it'll shed what you're carrying).
**When you might change it:** Set Copper to `Discard` if you never want to bother carrying near-worthless coin.

### Auto-deposit if wealth exceeds / Auto-deposit if coins exceed

**Default:** both `0` (disabled)
**What it does:** When your total held wealth (converted to a single value) — or, separately, your total raw coin count — passes this number, MudPlay automatically detours to your chosen Bank/Stash and deposits the excess.
**Important notes:** Either threshold tripping is enough to trigger a deposit; both must fall back below their thresholds before it can trigger again. Requires a Bank/Stash to actually be selected below — without one, nothing happens even if the threshold is crossed.

### Bank

**Default:** empty (auto-deposit disabled)
**What it does:** Picks the destination for the auto-deposit trips above — either an in-game bank, or one of your own map-marked stash rooms. Banks receive a deposit command; stash rooms get individual "hide" commands for each coin type.

### Keep wealth (copper) — "Keep on hand"

**Default:** `0`
**What it does:** The minimum amount of wealth you want left in your pocket after an auto-deposit or auto-stash trip — i.e., don't offload everything, keep this much.

### Don't collect if it makes you Light / Medium / Heavy

**Default:** all Off
**What it does:** Skips picking up a coin if doing so would push your encumbrance into the named bracket. The three are nested by strictness — checking "Light" implies "Medium" and "Heavy" are also refused, since those are looser thresholds.
**When you might change it:** Turn on "Don't make you Medium" if you want to stay light on your feet while exploring or fighting.

### Collect after combat finished (Cash and Items)

**Default:** Off
**What it does:** Waits until a room's fight is fully over before picking up ground coin and items (this one switch governs both engines), instead of grabbing them mid-fight.

### Drop smaller currency to make room for larger Collect-flagged coin

**Default:** Off
**What it does:** When picking up a higher-value coin would push you over an encumbrance limit, this drops just enough lower-value **Collect-flagged** coin you're already carrying to make room, instead of skipping the pickup. It never sacrifices Ignore-flagged coin.

### Don't get item if it makes you Light / Medium / Heavy

**Default:** all Off
**What it does:** Same nested-strictness idea as the coin version above, but applied to picking up ground *items* instead of coin.

---

## Talk

Settings → Talk.

### Disallow all remote control commands

**Default:** Off
**What it does:** A total kill-switch — with this on, MudPlay silently ignores every `@`-command from anyone on any channel, including from your own party.

### Disallow @party commands (from any party member)

**Default:** Off
**What it does:** Blocks the normal rule that any active party member can send you steering directives (`@party attack`, `@party rest`, etc.) that get relayed to your character.
**When you might change it:** If you're technically partied but want this character to act independently without being steered.

### Disallow @commands from telepaths / pages, gangpaths, or say (local)

**Default:** all Off
**What it does:** Three separate switches to drop `@`-commands arriving via each specific channel (telepaths and pages, guild/gang chat, local room speech). These are the only three channels MudPlay listens for `@`-commands on at all.

### Warn sender on invalid / denied remote command

**Default:** On
**What it does:** The master gate for replies to denied or unrecognized `@`-commands. When on, most refusals send a reply back (a specific reason when there is one, otherwise the generic message below); when off, refusals are silent. A few hard-blocked commands (such as `reroll` and `@party` suicide) stay silent either way, so a reply can't leak information to a malicious caller.

### Failure message

**Default:** `"command invalid or not allowed"`
**What it does:** The generic text sent back for a denied/unrecognized command, when a reply is sent at all (see above).

### Greet players when first met

**Default:** Off
**What it does:** The first time each day you spot a new (non-party) player in your room, MudPlay automatically greets and looks at them.

### Look back when a player looks at us

**Default:** Off
**What it does:** When the game tells you someone is looking at you, MudPlay reflexively looks back at them.

### Look at players to learn/update their inventories

**Default:** Off
**What it does:** Automatically looks at any non-party player who walks into your room, so MudPlay can learn/refresh what gear they're carrying.

### Log conversations / Log transactions

**Default:** both On
**What it does:** Saves the Conversation window's chat history, and separately the Session Stats transaction history, to a log file so either survives an app restart.

### Log line limit

**Default:** `2000`
**What it does:** How many of the most recent lines each of the two logs above keeps — older lines roll off as new ones come in.

### Font / Font size (Conversation window)

**Default:** JetBrains Mono, 12pt
**Available options:** Font — JetBrains Mono, IBM Plex Sans, MX437 IBM VGA; Size — 10–20pt in a fixed list.
**What it does:** The font used inside the Conversation window's chat log.
**Important notes:** Unlike almost everything else in this app, this only applies the **next time you open** the Conversation window — not live to a window already open.

### Channel colors (per-channel Accent / Text)

**Default:** theme defaults (no override)
**What it does:** Lets you pick a custom color for each chat channel's tag/speaker name (Accent) and separately its message body (Text).
**Important notes:** Also only applies the next time the Conversation window is opened, same as the font settings above.

---

## Auto-Light

Settings → Auto-Light. Everything here only matters once the master **Auto-Light** engine toggle (Settings → General, or the toolbar) is turned on — these fields tune its behavior, they don't turn it on by themselves.

### Preferred light

**Default:** `Automatic (per route)`
**Available options:** `Automatic (per route)`, `Only use my room-light spell (no items)`, or the name of any purchasable light item.
**What it does:** Chooses what light source MudPlay buys and lights when it needs one. "Automatic" picks whatever's strong enough to cover the route ahead. Choosing a specific item pins it to that light (falling back to auto-pick if it's unavailable in your current game data). "Only use my room-light spell" tells MudPlay to never buy or ready a light item at all — it relies purely on your gear and the Spells tab's room-light spell.
**When you might change it:** Pin a specific light for predictable weight/cost; use spell-only mode if you're a caster who never wants automation touching your light inventory.

### Carry (hours)

**Default:** `6`
**Available options:** 0–48
**What it does:** How many hours of burn time you want stocked up before committing to a dark route — MudPlay divides this by your chosen light's burn time to figure out how many to buy.
**When you might change it:** Raise it before a long session in a dark area; set to 0 if you'd rather just light what's needed on the spot without stockpiling.

### Reorder at (min left)

**Default:** `60` minutes
**Available options:** 0–600
**What it does:** When your lit light's remaining burn time drops below this, MudPlay detours to a shop, restocks back to your Carry-hours target, and returns to what it was doing.
**When you might change it:** Lower it to squeeze more use out of what you're carrying before triggering a resupply detour.
**Important notes:** A live readout at the bottom of the tab summarizes the current plan (e.g. how many of your chosen light it will stock for your Carry-hours target), or says provisioning is off.

---

## Auto-Lair

Settings → Auto-Lair. This tab tunes the scheduler that loops between "lairs" (marked monster-spawn rooms). Note: which rooms actually count as lairs is set up separately, from the **Navigation window**, not this Settings tab — and that list is shared by every character on the BBS (it's game-world data, not a personal preference).

### Routing heuristic

**Default:** `Default`
**Available options:** `Default — balance wasted respawn vs idle wait`, `Throughput — minimise wasted respawn only`
**What it does:** How the scheduler weighs "arriving too early and standing around" against "arriving too late and wasting respawned time." Default balances both; Throughput only cares about not wasting respawn and treats waiting as free.
**When you might change it:** Pick Throughput if you don't mind parking and waiting as long as you never walk into an already-picked-clean lair.

### Idle penalty weight

**Default:** `1.0`
**Available options:** 0–100
**What it does:** Only matters under the Default heuristic — the multiplier applied to idle-wait time when scoring which lair to visit next. `0` makes waiting free (same as Throughput mode); higher values make the scheduler avoid waiting more aggressively.

### Engage timeout

**Default:** `30` seconds
**Available options:** 1–3600
**What it does:** After walking into a lair, how long the scheduler assumes you're busy fighting/looting before it re-evaluates where to go next.
**When you might change it:** Raise it for lairs where fights reliably take longer than 30 seconds so you're not yanked away mid-fight.

### Travel cost model

**Default:** `Automatic (match realm)`
**Available options:** `Automatic (match realm)`, `Flat seconds per hop`, `Encumbrance-gated`
**What it does:** How the scheduler estimates travel time to a candidate lair. Automatic matches your realm's known movement pacing; Flat applies one fixed number to every step; Encumbrance-gated looks up a separate per-bucket number based on your live encumbrance.
**When you might change it:** Automatic needs no tuning for most people. Pick Encumbrance-gated if you want to hand-tune timing yourself after measuring your actual walking speed.

### Flat seconds per hop / Per-encumbrance seconds per hop

**Default:** Flat `1.5`s; per-bucket None/Light/Medium `0.7`s, Heavy/Encumbered `1.7`s.
**What it does:** The actual timing numbers used by the two non-Automatic travel-cost modes above.

---

## Auto-Trainer

Settings → Auto-Trainer. Automates leveling up and (separately) spending banked Character Points on stat training. These two behaviors are deliberately independent — you can turn one on without the other.

### Auto-train

**Default:** Off
**What it does:** The master auto-leveling switch. When on, and you're running a Loop or Auto-Lair, the moment your banked experience makes a new level trainable, MudPlay automatically pauses, detours to an allowed trainer, trains every level you can, then resumes what it was doing.

### Auto-train stats

**Default:** Off
**What it does:** Independent of Auto-train. When on, every time a training happens (whether from Auto-train, a manual "Train Now," or a remote `@train`), MudPlay also applies your saved CP allocation plan's spending for the level you just reached.
**Important notes:** You need a saved CP plan (from the Player Workshop's CP Allocation tab) before this checkbox will actually stay checked — MudPlay reverts it and warns you if you try to enable it with no plan saved.

### Levels to keep banked

**Default:** `0`
**What it does:** A reserve buffer — Auto-train (and manual training) stops once only this many further trainable levels remain, rather than always training everything available.
**When you might change it:** Set to 2–3 if you like to keep some levels "in the bank" as a cushion.

### Do not train above level

**Default:** `0` (no ceiling)
**What it does:** A hard level cap — Auto-train stops permanently once you reach this level, even if more experience would normally allow further training.
**When you might change it:** Deliberately capping a character for a challenge run or a competitive-play limit.

### Announce level-ups over [channel]

**Default:** Off; channel defaults to `Gangpath`
**Available channel options:** `Gangpath`, `Gossip`, `Yell`, `Say`
**What it does:** When on, the moment you become able to train a new level, MudPlay sends a short message on the chosen chat channel (`I can now train to level: N`) — handy for letting a static party know it's time to regroup at a trainer.
**Important notes:** Deliberately doesn't spam on login — only a genuine in-session level-up crossing announces, never a backlog of levels you were already eligible for when you connected.

### Discovered trainers table

**Default:** every discovered trainer allowed
**What it does:** A list of the trainers in your loaded game data that apply to you — the universal Training Room plus your own class's trainer — each with a checkbox controlling whether MudPlay is allowed to route to it. Uncheck a specific trainer to exclude it — useful if a trainer sits somewhere dangerous or inconvenient. A **Usable at my level** filter above the table narrows it to trainers whose level range covers your current level.

---

## Statline

Settings → Statline, modeled on MegaMUD's Statline dialog. Statline is **server-owned** — this tab builds a text string that gets sent to the game with a `set statline` command, and MudPlay's own screen parser is generated from that same string, so the two stay in sync. The tab has three parts: a read-only **Current Statline** preview (how the prompt will look, using your live numbers when connected or sample numbers otherwise), the editable **Statline Command** field, and a **Customize** row for building the string from wildcards.

### Statline Command

**Default:** `full` (a sensible class-appropriate default format)
**Available options:** `full` (class default), a hand-built wildcard string, or `full custom <wildcards>`.
**What it does:** Controls the exact text/format your character's status-line prompt uses in the game, which MudPlay then reads back to track your live HP/mana/etc.
**How the options work:** Pick tokens from the **Customize** dropdown (current/max HP, current/max mana, resting flag, wealth, experience, color codes, and more) and click **Add** to build a custom string. **Default** resets back to `full`.
**Important notes:** When you change this and click OK/Apply while connected, MudPlay sends the updated `set statline` command to the game immediately. On each connect it also checks that the game's live prompt matches your saved statline and re-sends the command if it doesn't (self-correcting, up to 3 retries) — so a server reset that lost your custom statline fixes itself without you having to do anything.

---

## Other

Settings → Other. A catch-all tab for safety thresholds and walker (auto-pathing) behavior. Most fields here are character-tier; the two solver toggles and the player-database cleanup setting are Global-tier (install-wide).

### Block @suicide commands when lives ≤

**Default:** `5`
**Available options:** 0–9
**What it does:** Refuses to let a remote `@suicide` command through if your remaining lives are at or below this number — protects a near-dead character from a careless or malicious remote kill command. `0` disables the protection entirely. (If MudPlay can't read your current life count, it blocks the command regardless of this threshold.)

### Utilize self or party members to disarm traps

**Default:** On
**What it does:** When the walker's route crosses a trapped exit, MudPlay tries to disarm it (using your own Traps skill) before stepping through. Turning this off makes the walker just walk through and eat any trap damage.

### @trap max searches / @trap max disarms

**Default:** 20 / 5
**What it does:** Caps how many times MudPlay retries searching for a trap (in response to a remote `@trap` command), and separately how many times it retries actually disarming one, before giving up.

### Door max bash / Door max pick

**Default:** 10 / 10
**What it does:** Caps how many times the walker retries bashing or picking a locked door before giving up on it.

### Pick locks instead of bashing

**Default:** Off (bash first)
**What it does:** When a door supports both, this decides which the walker tries first. Picking is quieter and keeps you stealthed; bashing is louder but faster/more reliable for a strong character.

### Search rooms if item needed

**Default:** Off
**What it does:** If your route crosses an exit that needs an item you don't have (a boat, a rope, a ticket), turning this on makes MudPlay search every room along the way hunting for it — even if the separate Auto-Search master toggle elsewhere is off.

### Hide items when discarding

**Default:** Off (plain drop)
**What it does:** When auto-discard offloads an item from your pack, this makes it use `hide` instead of `drop` so the item lands concealed rather than in plain view on the ground.

### @comeback backtrack up to N rooms

**Default:** `10`
**What it does:** If you're the leader and a stranded follower sends a bare `@comeback` with no room specified, this is how far back along your own recent path you'll walk searching for them before giving up.

### Auto-request @comeback when left behind

**Default:** On
**What it does:** If you're a follower who gets stranded behind a moving leader, MudPlay automatically sends the `@comeback` request on your behalf.

### Show monster HP lookup

**Default:** On
**What it does:** When you `look <monster>`, MudPlay shows its estimated remaining hit points — both in the status bar's **TGT HP:** slot and as a yellow line printed to the terminal (e.g. `[orc remaining Hitpoints: 35-48]`). Turning this off suppresses both.

### Enable the Great Pyramid climb solver / Enable the asylum (random-teleport maze) solver

**Default:** both On
**What it does:** Two Global-tier toggles for automated navigation through two of MajorMUD's notoriously tricky areas — the Great Pyramid's climbing puzzle and the Warped Asylum's random-teleport maze. On means walking to a destination inside either area drives the puzzle-solving automatically; off means a walk there just fails like any other unreachable spot, and you navigate manually.
**Important notes:** These apply to every character on the install, not just the current one.

### Cleanup Player Database after N days

**Default:** `90`
**What it does:** MudPlay keeps a database of every player it's seen. Records not seen within this many days get deleted automatically at the next startup. `0` disables cleanup entirely.
**Important notes:** Global-tier (one setting for the whole install). Cleanup runs at startup, so changing this doesn't retroactively purge anything until you next launch MudPlay.

### "Teleport to avoid combat instead of hanging" — not functional

**Important notes:** This checkbox is permanently disabled in the UI — a placeholder for a planned feature that isn't built yet. It does nothing currently.

---

## Events

Settings → Events. Lets you define per-character scheduled actions that fire on a timer or on connection events (logon, logoff, reconnect).

### Disable all events

**Default:** Off
**What it does:** A single master pause switch for every scheduled event on this character, without deleting or individually disabling each one.
**Important notes:** Saves immediately on toggle — no separate Apply step.

### Event list (New… / Modify… / Remove)

**What it does:** Shows every scheduled event you've defined, with its name, trigger, and action. **New…** and **Modify…** open the event editor; **Remove** deletes the selected event. Changes save to the profile immediately.
**Important notes:** Each event has a **Name** and a **Disabled** checkbox in its editor — untick Disabled to make it live. A row can show a "target missing" warning if it points at a saved Loop or Auto-Lair setup that's since been deleted or renamed — the event auto-disables itself in that case, and you'll need to clear its **Disabled** box again once you've fixed the reference.

### Event editor — trigger types

- **Logon** — fires on every successful game entry, including the first connect of a session and every reconnect.
- **Logoff** — fires once, right before a clean, user-initiated disconnect (or a BBS cleanup-shutdown warning). A dropped/lost connection does **not** fire this.
- **Re-log** — fires like Logon, but only on reconnects — never the very first connect.
- **At time** — fires once at a specific daily clock time. If MudPlay wasn't connected when the time passed, that occurrence is simply skipped, not caught up later.
- **Every** — a recurring interval (seconds/minutes/hours). The timer restarts fresh at every connect and stops on disconnect.

### Event editor — action types

- **Walk to** — navigate to a coordinate or room name. Stops any other running Loop/Auto-Lair first, and automatically resumes whatever was running before once the event-walk finishes successfully.
- **Start loop** — starts a saved Loop by name.
- **Auto-lair** — starts a saved Auto-Lair setup by name.
- **Command** — sends free-form text to the game; an empty command is valid (useful for paging through a prompt).

---

## Sounds (stub — not functional)

**⚠️ This entire tab is a placeholder and does nothing.** Every control on it is permanently disabled, there's no underlying setting behind any of them, and no audio system exists anywhere in MudPlay yet — there is currently no way to assign or play a sound at all. The tab exists to preview what a future audio-cues feature might look like. Documented here only so you know not to expect any effect from interacting with it:

- **Sounds enabled** — intended as a global mute switch.
- **Master volume** — intended to scale all cue volumes (0–100).
- **Event cue fields** — six placeholder text fields intended to hold sound-file paths for: incoming telepath, level-up, character death, party invite, a default Trigger-fire cue, and a default Events-tab cue.

---

## Diagnostics / Log Pane

Not a Settings tab — these four toggles live in the **Program Log** window (default shortcut F4), and are documented here for completeness since they're genuine per-character saved preferences. They control how much detail MudPlay records about its own decisions, mainly useful for troubleshooting or preparing a bug report.

### Debug channel

**Default:** On
**What it does:** Turns on the generation of Debug-level log lines across the app's engines. With it off, that channel's lines simply aren't produced (not just hidden) — turning it on gives you a much more detailed decision trail, at the cost of a noisier log.

### Combat channel

**Default:** On
**What it does:** The same idea, but specifically for verbose combat-decision tracing (why an attack/spell choice was made each round).
**Important notes:** Both Debug and Combat default **on** so that a fresh character's Program Log already has enough detail to diagnose a problem the first time something goes wrong — a bug report captured with both off has nothing useful in it.

### Auto-collect logs

**Default:** Off
**What it does:** When on, MudPlay writes out full on-disk diagnostic files (program log, memory log, combat-trace log) for the session, under the data folder's `Logs/` directory, instead of only keeping recent lines in memory.
**When you might change it:** Turn on before a play session where you're trying to reproduce and capture an intermittent bug.

### Hop timing

**Default:** Off
**What it does:** Emits one log line per confirmed room-to-room movement, recording how long it actually took — useful for calibrating the Auto-Lair tab's "Encumbrance-gated" travel-time numbers against your own real movement speed.

---

## Equipment Sets (Player Workshop)

Not a Settings-window tab. Your character's gear loadouts — the four fixed sets **Default**, **Backstab**, **Pre-rest HP**, and **Pre-rest Mana** — are configured in the **Player Workshop**'s Equipment Manager, not in Settings. They're mentioned here because the Combat tab's weapon fields (Normal/Alternate/Backstab weapon) are actually populated from the Default and Backstab sets rather than being typed in directly — see the note under the Combat tab's weapon slots above. See the **Player Workshop** section for how to build and enable a set.

---

## Command-Line / Environment

MudPlay has essentially no command-line interface — no custom flags like `--profile` or `--data-dir` exist; the app receives standard startup arguments and does nothing further with them.

### MUDPLAY_DATA_ROOT (environment variable)

**Default:** unset
**What it does:** If set before launching MudPlay, overrides where the app reads/writes all of its data — game-data sets, settings, profiles, logs — replacing the normal per-platform data folder entirely.
**Important notes:** This exists mainly for automated testing, not as a documented end-user feature — there's no in-app UI to set it, and it must be set in your OS environment before starting MudPlay. It's only read once at startup; changing it while MudPlay is running has no effect. For normal use, the in-app "Change…" button on Settings → General (which moves your data folder and restarts the app) is the supported way to relocate your data.

---

## Advanced Configuration Reference

This section is a compact, technical lookup table for every setting documented above — useful if you're hand-editing a profile/settings JSON file, writing about MudPlay, or just want the exact property name behind a UI label. "Location" gives the C# file where the setting is defined.

### General / Toolbar / Statline

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Terminal font family | `null` (MX437) | avares:// URI / system family name | `TerminalFontFamily` | Models/Profile/GeneralSettings.cs |
| Terminal font size | `null` (16) | 8–32 pt (fixed list) | `TerminalFontSize` | Models/Profile/GeneralSettings.cs |
| Nav tooltip font family | `null` (MX437) | avares:// URI / system family name | `NavTooltipFontFamily` | Models/Profile/GeneralSettings.cs |
| Nav tooltip font size | `null` (13) | 8–32 pt (fixed list) | `NavTooltipFontSize` | Models/Profile/GeneralSettings.cs |
| Scale terminal to window | `false` | bool | `ScaleTerminalToWindow` | Models/Profile/GeneralSettings.cs |
| Type-to-terminal fallthrough | `true` | bool | `TypeToTerminalFromOtherWindows` | Models/Profile/GeneralSettings.cs |
| Show startup mud animation | `true` | bool | `ShowStartupMudAnimation` | Models/Profile/GeneralSettings.cs |
| Nav line appearance | factory pens | hex colour + 1.0–8.0 px thickness, per line | `GlobalSettings.NavLines` (`NavLineStyles`) | Models/Settings/NavLineStyles.cs; Models/Settings/GlobalSettings.cs |
| Default task | `DoNothing` | DoNothing / BeginLoop / BeginAutoLair | `DefaultTask` | Models/Profile/GeneralSettings.cs |
| Default loop name | `null` | saved loop name | `DefaultLoopName` | Models/Profile/GeneralSettings.cs |
| Default Auto-Lair name | `null` | saved Auto-Lair name | `DefaultAutoLairName` | Models/Profile/GeneralSettings.cs |
| Auto-connect on profile load | `false` | bool | `AutoConnect` | Models/Profile/GeneralSettings.cs |
| Backup profile on save | `false` | bool | `BackupOnSave` | Models/Profile/GeneralSettings.cs |
| Auto-Combat / Auto-Nuke / Auto-Heal-Rest / Auto-Bless / Auto-Light / Auto-Get-Items / Auto-Get-Cash / Auto-Sneak / Auto-Hide / Auto-Search enabled | true/true/true/true/false/true/true/true/false/false | bool each | `AutoMode.AutoCombat` etc. | Models/Profile/AutoActionDefaults.cs |
| Auto-Train enabled | `false` | bool | `AutoTrainerSettings.AutoTrain` (mirrored on the General tab) | Models/Profile/AutoTrainerSettings.cs |
| Allow hangup in all-off mode | `false` | bool | `AllowHangupInAllOffMode` | Models/Profile/GeneralSettings.cs |
| Re-enable on reconnect (11 flags) | `false` (all) | bool | `ReEnableAutoCombatOnReconnect` etc. | Models/Profile/GeneralSettings.cs |
| Disable hangups (toolbar toggle) | `false` | bool | `DisableHangups` | Models/Profile/GeneralSettings.cs |
| Auto-load last profile (edited on MainWindow, not Settings) | `false` | bool | `GlobalSettings.AutoLoadLastProfile` | Models/Settings/GlobalSettings.cs |
| Player cleanup days (edited on Other tab) | `90` | int, 0–3650 | `GlobalSettings.PlayerCleanupDays` | Models/Settings/GlobalSettings.cs |
| Show toolbar | `true` | bool | `ToolbarSettings.Visible` | Models/Profile/ToolbarSettings.cs |
| Toolbar position | `Top` | Top/Bottom/Left/Right | `ToolbarSettings.Position` | Models/Profile/ToolbarSettings.cs |
| Toolbar layout | `null` (13 defaults) | ordered `{Kind, ActionId}` list | `ToolbarSettings.Layout` | Models/Profile/ToolbarSettings.cs |
| Help menu website links | 4 seed links | `List<HelpWebsite>{Label, Url}` | `GlobalSettings.Settings["HelpWebsites"]` | Models/Settings/HelpWebsitesSettings.cs |
| Active BBS website URL / show in Help | `null` / `true` | URL string / bool | `BbsProfile.WebsiteUrl` / `ShowWebsiteInHelp` | Models/Settings/BbsProfile.cs |
| Statline command | `null` (= `full`) | `full`, `full custom <wildcards>`, or raw wildcard string | `StatlineSettings.Command` | Models/Profile/StatlineSettings.cs |

### Keybindings / Macros

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Built-in keybind overrides | seed defaults (table above) | `Dictionary<BuiltInAction, KeyChord>` | `CharacterProfile.BuiltInKeybindings` | Services/KeybindingStore.cs |
| Macros | 10 seeded numpad macros | list of `Macro{Key, Modifiers, Command, Enabled}` | `CharacterProfile.Macros` | Services/MacroStore.cs |

### BBS + Display / Confirmations

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Name | `""` | any string | `Name` | Models/Settings/BbsProfile.cs |
| Host | `""` | hostname/IP | `Host` | Models/Settings/BbsProfile.cs |
| Port | `23` | 1–65535 | `Port` | Models/Settings/BbsProfile.cs |
| Max redials | `3` | 1–9999 | `MaxRedials` | Models/Settings/BbsProfile.cs |
| Redial pause (s) | `5` | 1–300 | `RedialPauseSeconds` | Models/Settings/BbsProfile.cs |
| Infinite retries | `false` | bool | `InfiniteRetries` | Models/Settings/BbsProfile.cs |
| Cleanup wait (m) | `0` | 0–600 | `CleanupPeriodMinutes` | Models/Settings/BbsProfile.cs |
| No-response (s) | `20` | 0–3600 | `NoResponseTimeoutSeconds` | Models/Settings/BbsProfile.cs |
| Reconnect on failed connect / carrier lost / no response / after cleanup | `false` (all) | bool | `ReconnectOnFailedConnect` etc. | Models/Settings/BbsProfile.cs |
| Game entry / exit command | `"E"` / `"=x"` | string | `GameEntryCommand` / `GameExitCommand` | Models/Settings/BbsProfile.cs |
| Player dies at (HP) | `-25` | -999–0 | `PlayerDiesAtHp` | Models/Settings/BbsProfile.cs |
| Auto-refine death floor | `true` | bool | `AutoRefineDeathFloor` | Models/Settings/BbsProfile.cs |
| Boss cleanup time / zone | `"21:00"` / local zone | `HH:mm` / IANA/Windows tz id | `CleanupTimeOfDay` / `CleanupTimeZoneId` | Models/Settings/BbsProfile.cs |
| Board disconnect line | `null` | pattern string | `DisconnectPattern` | Models/Settings/BbsProfile.cs |
| Name of runic currency | `"runic"` | string | `RunicCurrencyName` | Models/Settings/BbsProfile.cs |
| Columns / Rows (NAWS) | `80` / `25` | 40–200 / 20–100 | `TerminalCols` / `TerminalRows` | Models/Settings/BbsProfile.cs |
| Scrollback (lines) | `4000` | 100–100,000 | `ScrollbackLines` | Models/Settings/BbsProfile.cs |
| Wheel scroll (lines) | `5` | 1–50 | `BackscrollWheelLines` | Models/Settings/BbsProfile.cs |
| Username / Password (per-char) | `null` | encrypted string | `EncryptedUsername` / `EncryptedPassword` | Models/Profile/BbsCredentials.cs |
| Sysop powers (per-char) | `false` | bool | `HasSysopPowers` | Models/Profile/BbsCredentials.cs |
| Menu nav steps (per-char) | `[]` | list of `MenuStep{WaitForPattern, Send}` | `MenuNavSteps` | Models/Profile/BbsCredentials.cs |
| Confirm exit / hangup / save settings / deletes | `false` (all) | bool | `ConfirmExit`, `ConfirmHangup`, `ConfirmSaveSettings`, `ConfirmDeletes` | Models/Settings/ConfirmSettings.cs |

### Combat

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Normal / Alternate weapon attack command | `a` | free text | `NormalAttackCommand` / `AlternateAttackCommand` | Models/Profile/CombatSettings.cs |
| Action order | `SpellsFirst` | SpellsFirst / PhysicalFirst / AlternateSpellPhysical / AlternatePhysicalSpell / CustomRoundCycle | `ActionOrder` | Models/Profile/CombatSettings.cs |
| Round cycle (physical / spell rounds, start on spell) | 1 / 1 / false | 0–999 / 0–999 / bool | `CycleRoundsPhysical`, `CycleRoundsSpell`, `CycleStartOnSpell` | Models/Profile/CombatSettings.cs |
| Do BS attacks | `false` | bool | `DoBackstab` | Models/Profile/CombatSettings.cs |
| Don't BS if multi-attack | `true` | bool | `SkipBackstabIfMultiAttack` | Models/Profile/CombatSettings.cs |
| Run if BS fails | `false` | bool | `RunIfBackstabFails` | Models/Profile/CombatSettings.cs |
| Clear hostiles when seen hidden | `false` | bool | `ClearHostilesWhenSeenHidden` | Models/Profile/CombatSettings.cs |
| Target order | `Normal` | Normal / Reverse | `TargetOrder` | Models/Profile/CombatSettings.cs |
| Target Priority (+ member name) | `Default` / `null` | Default / FollowLeader / FollowMember | `TargetPriority` / `TargetPriorityMemberName` | Models/Profile/CombatSettings.cs |
| Attack Order (+ after-player name) | `Default` / `null` | Default / AttackLastParty / AttackLastRoom / AttackAfter | `AttackTiming` / `AttackAfterPlayerName` | Models/Profile/CombatSettings.cs |
| Polite mode ⚠️ unwired | `Off` | Off / WaitForOthers / SkipRoom / AttackDifferent | `PoliteMode` | Models/Profile/CombatSettings.cs |
| Min. / Max. monsters | 0 / 20 | 0–20 / 1–20 | `MinMonstersInRoom` / `MaxMonstersInRoom` | Models/Profile/CombatSettings.cs |
| Run distance | `2` | 1–100 | `RunDistance` | Models/Profile/CombatSettings.cs |
| Go backwards if running | `true` | Backward / Forward | `RunDirection` | Models/Profile/CombatSettings.cs |
| Break combat before running | `true` | bool | `BreakBeforeFleeing` | Models/Profile/CombatSettings.cs |
| Minimum mana per cast mode | `Percentage` | Percentage / Absolute | `SpellManaThresholdMode` | Models/Profile/CombatSettings.cs |
| Multi-attack / AOE debuff / single debuff / normal / alternate attack spell | unset | spell code + MinEnemies(0-20) + MaxCastsPerRoom(null/0-100) + MinManaPerCast | `MultiAttackSpell`, `AreaDebuffSpell`, `SingleTargetDebuffSpell`, `NormalAttackSpell`, `AlternateAttackSpell` | Models/Profile/CombatSettings.cs |
| Drain (life-steal) spell + HP trigger + Drains override AOE | unset / 50% / off | spell code + MaxCastsPerRoom + MinManaPerCast; DrainHpTrigger(0-100); DrainsOverrideAoe(bool) | `DrainSpell`, `DrainHpTrigger`, `DrainsOverrideAoe` | Models/Profile/CombatSettings.cs |
| Show combat round totals ⚠️ unwired | `false` | bool | `ShowCombatRoundTotals` | Models/Profile/CombatSettings.cs |

### Spells / Health

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Spell type priority (7 categories) | Minor party heal(1)…Debuffing(7) | 1–7 permutation | `PriorityMinorPartyHeal` … `PriorityDebuffing` | Models/Profile/SpellsSettings.cs |
| Minor / Major heal, HP Regen, Mana Regen | unset | spell code | `MinorHealSpell`, `MajorHealSpell`, `HpRegenSpell`, `MaRegenSpell` | Models/Profile/SpellsSettings.cs |
| Reroll if roll below / Max rerolls | unset / `3` | int or blank / 1–20 | `ManaRegenRerollThreshold` / `ManaRegenRerollCap` | Models/Profile/SpellsSettings.cs |
| When HP/Mana full | unset | spell code | `WhenHpFullSpell` / `WhenMaFullSpell` | Models/Profile/SpellsSettings.cs |
| Cure Holds/Poison/Disease/Blindness | unset | spell code | `CureHoldsSpell` etc. | Models/Profile/SpellsSettings.cs |
| Room light | unset | spell code | `RoomLightSpell` | Models/Profile/SpellsSettings.cs |
| Bless slots (1–10/15) + recast margin | empty / 15s default | spell code or `#item` / 0–999 | `BlessSlots` / `BlessSlotRecastMargins` | Models/Profile/SpellsSettings.cs |
| Bless self while resting / during combat | false / false | bool | `SelfBlessWhileResting` / `SelfBlessDuringCombat` | Models/Profile/SpellsSettings.cs |
| Ignore / Don't announce poison, blindness, confusion, diseased | false (all) | bool | `IgnorePoison` etc. / `DoNotAnnouncePoison` etc. | Models/Profile/SpellsSettings.cs |
| HP/MA threshold mode | `Percentage` (both) | Percentage / Absolute | `HpThresholdMode` / `MaThresholdMode` | Models/Profile/HealthSettings.cs |
| Rest max / Rest if below (HP, MA) | 95/60/95/30 (%) | 0–100,000 | `RestMaxHp`, `RestIfBelowHp`, `RestMaxMa`, `RestIfBelowMa` | Models/Profile/HealthSettings.cs |
| Run if below (HP, MA) | 20 / 10 (%) | 0–100,000 (0=off) | `RunIfBelowHp` / `RunIfBelowMa` | Models/Profile/HealthSettings.cs |
| Hang up if below | `5` (%) | death-floor minimum–100,000 | `HangIfBelowHp` | Models/Profile/HealthSettings.cs |
| Heal (rest) / Minor / Major heal (combat) | 80/70/40 (%) | 0–100,000 | `HealRestTrigger`, `MinorHealCombatTrigger`, `MajorHealCombatTrigger` | Models/Profile/HealthSettings.cs |
| Bless if above | `70` (%) | 0–100,000 | `BlessIfAboveMa` | Models/Profile/HealthSettings.cs |
| Heal if above (rest / combat) | 50 / 0 (%) | 0–100,000 (0=off) | `HealIfAboveMaResting` / `HealIfAboveMaCombat` | Models/Profile/HealthSettings.cs |
| Use meditate / Meditate before resting / Utilize shadowrest | false (all) | bool | `UseMeditateAbility`, `MeditateBeforeResting`, `UtilizeShadowRest` | Models/Profile/HealthSettings.cs |
| Pre/Post-rest command | empty | free text, `^M`/`;` chained | `PreRestCommand` / `PostRestCommand` | Models/Profile/HealthSettings.cs |

### Party / Cash / Talk

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Rank | `Mid` | Front / Mid / Back | `Rank` | Models/Profile/PartySettings.cs |
| Minor/Major party heal (single/AOE) | blank (all 4) | spell code | `MinorPartyHealSpell` etc. | Models/Profile/PartySettings.cs |
| Minor/Major heal threshold %, AOE min members | 70/40/2 | 0–100 / 2–6 | `MinorHealMemberThresholdPercent` etc. / `AoeMinMembers` | Models/Profile/PartySettings.cs |
| Party bless slots (10) | empty | spell + class list + recast sec | `BlessSlots` | Models/Profile/PartySettings.cs |
| Bless while resting / during combat | false / false | bool | `BlessWhileResting` / `BlessDuringCombat` | Models/Profile/PartySettings.cs |
| Help leader open doors / Ignore @wait when leading / Reset stats on loop start | false/false/true | bool | `HelpLeaderOpenDoors`, `IgnoreWaitWhenLeading`, `ResetStatisticsOnLoopStart` | Models/Profile/PartySettings.cs |
| Re-invite lost members / send @join nags / send @health nags / probe on join | true (all) | bool | `AutoInviteReconnecting`, `SendJoinToInvited`, `SendHealthToMembers`, `ProbeStatsOnPartyJoin` | Models/Profile/PartySettings.cs |
| Nag initial delay / frequency / max window (s) | 5/10/55 | 1–60 / 1–60 / 5–600 | `JoinNagInitialDelaySec`, `JoinNagFrequencySec`, `JoinNagMaxTotalSec` | Models/Profile/PartySettings.cs |
| Max monsters when partying | `20` | 1–20 | `MaxMonstersWhenPartying` | Models/Profile/PartySettings.cs |
| Wait if members below % | `0` | 0–100 | `WaitIfMemberBelowPercent` | Models/Profile/PartySettings.cs |
| If leading, wait only (s) / Return distance (rooms) | 90 / 30 | 0–3600 / 1–500 | `IfLeadingWaitTotalSec` / `ReturnDistanceRooms` | Models/Profile/PartySettings.cs |
| par poll frequency (s) | `5` | 1–60 | `ParPollFrequencySec` | Models/Profile/PartySettings.cs |
| Copper / Silver / Gold / Platinum / Runic policy | Ignore/Collect×4 | Collect / Ignore / Discard | `CopperPolicy` etc. | Models/Profile/CashSettings.cs |
| Auto-deposit if wealth / coins exceed | 0 / 0 | 0–100,000,000 | `AutoDepositIfWealthExceeds` / `AutoDepositIfCoinsExceed` | Models/Profile/CashSettings.cs |
| Bank | none | dropdown of banks/stashes | `BankRoomKey` | Models/Profile/CashSettings.cs |
| Keep wealth (copper) | `0` | 0–100,000,000 | `KeepOnHandWealth` | Models/Profile/CashSettings.cs |
| Don't collect/get item → Light/Medium/Heavy (6 flags) | false (all) | bool | `SkipCollectIfMakesLight` etc. / `SkipGetItemIfMakesLight` etc. | Models/Profile/CashSettings.cs |
| Collect after combat finished / Drop smaller for larger | false / false | bool | `CollectAfterCombatFinished` / `DropSmallerForLarger` | Models/Profile/CashSettings.cs |
| Disallow all remote / @party / telepaths / gangpaths / local | false (all) | bool | `DisallowAllRemoteCommands` etc. | Models/Profile/TalkSettings.cs |
| Warn on invalid remote command / Failure message | true / default text | bool / free text | `WarnOnInvalidRemoteCommand` / `RemoteCommandFailureMessage` | Models/Profile/TalkSettings.cs |
| Greet / Look back / Look on arrival | false (all) | bool | `GreetPlayersWhenFirstMet`, `LookBackWhenLookedAt`, `LookAtPlayersOnArrival` | Models/Profile/TalkSettings.cs |
| Log conversations / transactions / line limit | true/true/2000 | bool / bool / 100–100,000 | `LogConversations`, `LogTransactions`, `LogMaxLines` | Models/Profile/TalkSettings.cs |
| Conversation font / size / channel colors | defaults | 3 fonts / 10-20pt / hex per channel | `ConvoFont`, `ConvoFontSize`, `ChannelColors` | Models/Profile/TalkSettings.cs |

### Auto-Light / Auto-Lair / Auto-Trainer / Other / Events

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Preferred light | `Automatic (per route)` | Auto-pick / spell-only / purchasable light name | `PreferredLightName` (+ `UseRoomLightSpellOnly`) | Models/Profile/AutoLightSettings.cs |
| Carry (hours) / Reorder at (min left) | 6 / 60 | 0–48 / 0–600 | `CarryHours` / `ReorderThresholdMinutes` | Models/Profile/AutoLightSettings.cs |
| Routing heuristic | `Default` | Default / Throughput | `Heuristic` | Models/Profile/AutoLairSettings.cs |
| Idle penalty weight | `1.0` | ≥0 (UI 0–100) | `IdlePenalty` | Models/Profile/AutoLairSettings.cs |
| Engage timeout | `30` | 1–3600 s | `EngageTimeoutSeconds` | Models/Profile/AutoLairSettings.cs |
| Travel cost model | `Auto` | Flat / EncumbranceGated / Auto | `TravelCostMode` | Models/Profile/AutoLairSettings.cs |
| Flat / per-encumbrance seconds per hop | 1.5 / 0.7-0.7-0.7-1.7-1.7 | 0.1–60 each | `FlatSecondsPerHop` / `HopTimesByEncumbrance.*` | Models/Profile/AutoLairSettings.cs |
| Lair marker override respawn / Skip (parked, unused) | null / false | int? seconds / bool | `LairMarker.OverrideRespawnSeconds` / `.Skip` | Models/Profile/LairMarker.cs |
| Auto-train / Auto-train stats | false / false | bool | `AutoTrain` / `AutoTrainStats` | Models/Profile/AutoTrainerSettings.cs |
| Levels to keep banked / Do not train above level | 0 / 0 | ≥0 (UI 0–60 / 0–200) | `LevelsToKeep` / `DoNotTrainAbove` | Models/Profile/AutoTrainerSettings.cs |
| Announce level-ups / channel | false / Gangpath | bool / Gangpath,Gossip,Yell,Say | `AnnounceLevelUps` / `AnnounceChannel` | Models/Profile/AutoTrainerSettings.cs |
| Discovered trainers "Use?" | all allowed | bool per trainer (disabled-list) | `DisabledTrainers` | Models/Profile/AutoTrainerSettings.cs |
| Block @suicide when lives ≤ | `5` | 0–9 | `OtherSettings.MaxSuicideLivesThreshold` | Models/Profile/OtherSettings.cs |
| Utilize disarm traps | `true` | bool | `OtherSettings.UtilizeDisarmTrapsIfAble` | Models/Profile/OtherSettings.cs |
| @trap max searches / disarms | 20 / 5 | 1–100 / 1–50 | `MaxTrapSearchAttempts` / `MaxTrapDisarmAttempts` | Models/Profile/OtherSettings.cs |
| Door max bash / pick / Pick over bash | 10/10/false | 1–100 / 1–100 / bool | `MaxBashAttempts`, `MaxPickAttempts`, `PicklocksOverBash` | Models/Profile/OtherSettings.cs |
| Search rooms if item needed / Hide items when discarding | false / false | bool | `SearchRoomsIfItemNeeded` / `HideWhenDiscarding` | Models/Profile/OtherSettings.cs |
| @comeback backtrack rooms / auto-request | 10 / true | 1–50 / bool | `MaxComebackBacktrackRooms` / `AutoRequestComebackWhenLeftBehind` | Models/Profile/OtherSettings.cs |
| Pyramid / Asylum solver enabled | true / true | bool (Global) | `GlobalSettings.PyramidSolverEnabled` / `AsylumSolverEnabled` | Models/Settings/GlobalSettings.cs |
| Cleanup Player DB after N days | `90` | 0–3650 (Global) | `GlobalSettings.PlayerCleanupDays` | Models/Settings/GlobalSettings.cs |
| Disable all events | `false` | bool | `CharacterProfile.EventsGloballyDisabled` | Models/Profile/CharacterProfile.cs |
| Event (Name/Disabled/Trigger/Action fields) | see above | see above | `ScheduledEvent.*` | Models/GameData/ScheduledEvent.cs |

### Diagnostics / Log Pane / Equipment

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Debug channel | `true` | bool | `LogDiagnosticsSettings.Debug` | Models/Profile/LogDiagnosticsSettings.cs |
| Combat channel | `true` | bool | `LogDiagnosticsSettings.Combat` | Models/Profile/LogDiagnosticsSettings.cs |
| Auto-collect logs | `false` | bool | `LogDiagnosticsSettings.AutoCollect` | Models/Profile/LogDiagnosticsSettings.cs |
| Hop timing | `false` | bool | `LogDiagnosticsSettings.HopTiming` | Models/Profile/LogDiagnosticsSettings.cs |
| Equipment sets (gear loadouts, edited in Character Workshop) | empty list, seeded per trigger type | list of `EquipmentSet` | `EquipmentSettings.Sets` | Models/Profile/EquipmentSettings.cs |

### Not user-configurable (confirmed, for completeness)

The following were traced and confirmed to have **no** exposed setting — listed so it's clear they were checked, not missed: Telnet terminal-type string (fixed `"ansi-bbs"`), the Telnet option negotiation whitelist, TCP keepalive probe interval/retry count, outgoing text encoding (fixed Latin-1), IAC byte-escaping, and command-line argument parsing (none exists beyond what Avalonia's framework startup consumes internally).

---

*This guide reflects the MudPlay source as of the `main` branch. Two settings in the Combat tab (Polite mode, Show combat round totals) and the entire Sounds tab are present in the UI but not currently wired to any runtime behavior — see their entries above for details. If a setting here stops matching what you see in the app, the code is the source of truth; please report the discrepancy.*

---

# Troubleshooting

Common snags and how to deal with them.

## Reconnecting

MudPlay can auto-reconnect when a connect attempt fails, the carrier drops mid-session, or the server stops responding — each toggled per-BBS on Settings → BBS + Display, with a retry count (or infinite) and a redial pause. The **No-response** timeout controls how quickly a dead connection is noticed.

## Something automated didn't behave

Open the **Program Log** (F4) — it records what the engines decided and why. Turn on Debug / Combat diagnostics from the log pane for more detail when you're reproducing an issue.

## Filing a bug report

Use the menu-bar **Bug Report** button, or right-click the terminal → **Bug report…**. It writes a Markdown snapshot of your current state — movement, player, settings, program log, and scrollback — to your Desktop, ready to attach to a GitHub issue, so a problem can be diagnosed from the exact moment it happened.
