# FujinTerm

<!-- current-version:start -->
> **Version 1.72.0**
> - Navigation can now reach a destination inside a random-teleport maze (e.g. the Warped Asylum), where every room shares a name so normal tracking gives up
> - The maze is detected structurally — a one-way cast mouth whose interior random-teleports on every step — with no hardcoded room numbers
> - After each teleport the walker relocalizes by peeking neighbours with `look <dir>` and matching a unique exit signature, then routes to the goal, re-teleporting ("reshuffling") when the goal is only reachable through another teleport
> - Runs on every realm — on stock the look-sweep is the only tool, while on Paradigm the solver relocalizes with `rm` (an authoritative position query whose room numbers stay distinct even though every asylum room shares a name) and never looks at all: every teleport landing and every plain step re-locates by `rm`, which also pinpoints the dead-end Padded Cells the look-sweep can't disambiguate
> - Paradigm's asylum pull-lever escape is treated as a one-way pocket dimension so the maze detects and routes there the same as on stock
> - On stock, after each teleport the solver forces a `look` to read the landing's exits — in brief mode (the default) a room shows only its name on entry, so relocalization was keying off the room just left and desyncing at the entrance
> - On Paradigm the solver sends a bare `rm` after each move (never a `look`); telnet ordering guarantees `rm` reads the room the move landed us in, and a dropped reply is re-sent rather than falling back to a look
> - The solver now drives the final plain route to the goal itself (ungated, like a reshuffle step) instead of handing off to the walker, so it no longer stalls on a stuck combat gate mid-maze
> - Arrival at a dead-end goal room (e.g. the old man's padded cell, whose signature can't be uniquely matched) is recognized by room name on stock, or directly by `rm` on Paradigm, so the solver stops there instead of blind-reshuffling back out
> - When a landing has several reshuffle exits, the solver picks the one whose teleport spell is likeliest to land somewhere useful — favouring the pool with the most rooms it can both relocalize in and route to the goal from, instead of spiralling into a dead-end pool
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
