# FujinTerm

<!-- current-version:start -->
> **Version 1.69.0**
> - Trainer room detail now lists the per-level training cost across the trainer's whole level band, priced at that trainer's own markup
> - Game Data Rooms filter accepts a `map,room` coordinate (`1,1`) — comma, slash, or space all jump straight to that one room
> - Item detail's bought/sold shops are now clickable — each jumps the Game Data browser to the host room's Rooms-tab record
> - Item detail surfaces two more acquisition paths: `Found in` lists the chests an item drops from (with per-open odds), and `Given by` lists the monsters/rooms that hand it over via a textblock award — turn-in, purchase, or quest reward — each a clickable jump to that record
> - Character Info tab moves Quest Bonuses beneath the attack accuracy/damage box, freeing the right column for the full inventory readout
> - Quest Status cards now show the completion experience a quest awards on its own reward line (guide-only — it doesn't feed the Character Info bonuses)
> - Weapon-flap fix: a combat-entry gear-set trigger now defers the weapon/off-hand to the combat engine while it holds a per-monster alternate-weapon override, so the Default set can't re-wear the normal weapon over the swap mid-fight
> - Fallback-death fix: a kill with no per-monster death line (exp + `*Combat Off*`) is now attributed to the current target and dropped from the room roster — the survivor is re-engaged at once, ending the re-swing at the corpse and the post-kill idle stall
> - `@stop` now stacks a pause on top of combat exactly like the Pause button — a route paused mid-fight stays paused after the fight clears instead of walking on (and `@rego` lifts only that user pause)
> - Search-bar walk-to now rebounds to auto-following the player once the browse window lapses, matching how a pan-drag rebounds
> - Crossing an up/down no longer rebuilds/refocuses the map while you're panning or numpad-browsing — the re-root defers until browsing ends
> - Picking a new walk-to destination while manually paused now lifts the pause and walks there, instead of changing the destination but staying frozen
> - Walker now disarms a known-trapped exit directly instead of searching it first — the exit hint already proved the trap, so the confirming `search` is skipped
> - A between-round buff/heal cast that lands after the death→re-observe already re-swung now resumes the weapon on its `*Combat Off*` instead of idling a full round
> - A monster that walks in under a name the game data doesn't recognize (a colour-stripped arrival like "dragon serpent") is now auto-attacked instead of stopping the walker on a mob it never engages
> - Renaming the currently-running loop via Save-current now updates the navigation header at once, instead of holding the old (often loop-builder-generated) name until the next lap
> - Quest seed: Phoenix Feather guide reordered (`ask morukai orfeo` moved up to follow `ask orfeo morukai`) and the missing `ask morukai return` step added before `use potion`
>
> See the [version history](CHANGELOG.md) for the full changelog.
<!-- current-version:end -->

A modern Telnet terminal client for **MajorMUD** and other BBS door games, built in C# / .NET 10 with [Avalonia](https://avaloniaui.net/). It renders a faithful CP437 cell grid with full VT100/ANSI parsing, and layers a MegaMUD-style automation suite (combat, party, navigation, healing, and more) on top — all in modeless, dockable windows so the terminal stays live while you configure anything.

Linux is the primary platform; Windows and macOS are supported through Avalonia.

## Features

- **Faithful terminal** — Telnet (RFC 854/855 with NAWS + TERM-TYPE), an explicit VT100/ANSI escape-sequence parser, and a CP437 cell grid rendered by a custom Avalonia control. No host TTY dependency.
- **Combat automation** — attack rotations, target ordering, backstab handling, area/debuff spells, and per-room monster gating.
- **Party play** — party tracking, remote `@`-commands over chat channels, leader-aware wait/invite logic, and coordinated healing/blessing.
- **Navigation** — a room-graph map with go-to routing, repeatable movement loops, Auto-Lair hunting, and trap handling.
- **Healing & spells** — HP/mana thresholds, rest management, cures, buffs, and mana-regen roll-spell rerolling.
- **Character Workshop** — a unified hub for stats, equipment sets with auto-equip triggers, CP allocation plans, and quest tracking.
- **Scripting** — macros, pattern triggers, and scheduled/lifecycle events.
- **Game data** — import MajorMUD `.MDB` databases to JSON, then browse and override records (monsters, items, spells, rooms, shops, and more).
- **Layered settings** — a 4-tier hierarchy (installed defaults → all characters → per-BBS → per-character) where each tier stores only its deltas.
- **Quality of life** — session statistics, scrollback + a searchable backscroll window, a conversation/chat pane, a configurable toolbar and statline, and a built-in bug reporter (see below).

## Getting started

### Requirements

- The [.NET 10 SDK](https://dotnet.microsoft.com/) (the exact version is pinned in `global.json`).

### Build & run

```bash
git clone https://github.com/Tehshortbus/FujinTerm.git
cd FujinTerm
dotnet build      # compile check
dotnet run        # launch
```

If local state ever gets weird, `dotnet clean` and rebuild.

### First connection

1. Launch the app and create a character profile (auth + which BBS to connect to).
2. Set the BBS host/port and connect.
3. For the full automation suite, open **Game Data** and import a MajorMUD `.MDB` database — this populates the monster/item/spell/room tables the engines read from. The terminal itself works without it.

### Where your data lives

Everything is stored under a single `Data/` root, resolved per platform:

- **Linux** — `~/.local/share/FujinTerm/Data/`
- **Windows** — `%AppData%\FujinTerm\Data\`
- **macOS** — `~/Library/Application Support/FujinTerm/Data/`

Profiles, per-BBS settings, global settings, imported game data, and logs each live in their own subfolder. Settings files store only deltas from the tier beneath them, so they stay small and easy to back up.

## Reporting a bug

FujinTerm has a **built-in bug reporter** that snapshots the client's state at the moment of the problem — far more useful than describing it from memory. Please use it when filing an issue:

1. **Capture** — click the **Bug Report** button in the menu bar (or right-click the terminal → **Bug report…**). Type a short description of what went wrong and confirm.
2. FujinTerm writes a Markdown report to your **Desktop**, named `<realm>-<timestamp>.md`. It contains your player/inventory state, movement-engine status, relevant settings, the program log, and recent scrollback — with time-sensitive data frozen at click time.
3. **File the issue** — open a new issue at **https://github.com/Tehshortbus/FujinTerm/issues/new**, describe the problem, and **attach the generated `.md` file**.

The more of that capture you include, the faster a fix lands. Review the file before attaching if you'd like to redact anything.

## Contributing

- The build is **zero-warning** (`TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`) and XAML bindings are compile-checked — a clean `dotnet build` is the baseline.
- `dotnet test` runs the xUnit suite (parsers, structural invariants, and critical decision logic).
- Coding conventions, architecture rules, and the per-change Definition of Done live in [`CLAUDE.md`](CLAUDE.md).

## License

MIT — see [`LICENSE`](LICENSE).
