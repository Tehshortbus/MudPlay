# MudPlay

<!-- current-version:start -->
> **Version 3.172.2**
> - Simulator area ranking: **Save as loop** turns a ranked area's tour into a saved loop you can run, with how it was tested in the loop's notes
> - A picked ranking now shows its route (the rooms walked, in order) and what it was tested at (level, runs × hours, walk pace, start room)
>
> See the [version history](CHANGELOG.md) for the full changelog.
<!-- current-version:end -->

A modern Telnet terminal client for **MajorMUD** and other BBS door games, built in C# / .NET 10 with [Avalonia](https://avaloniaui.net/). It renders a faithful CP437 cell grid with full VT100/ANSI parsing, and layers a MegaMUD-style automation suite (combat, party, navigation, healing, and more) on top — all in modeless, dockable windows so the terminal stays live while you configure anything.

Linux is the primary platform; Windows and macOS are supported through Avalonia.

## Features

- **Faithful terminal** — Telnet (NAWS, TERM-TYPE), full VT100/ANSI parsing, and crisp CP437 rendering.
- **Profiles** — per-character profiles across multiple BBSes and accounts, auto-logon and auto-reconnect; settings layer defaults → all characters → BBS → character.
- **Combat** — attack and spell priority, backstab, debuffs and room spells, immunity-aware fallbacks, and per-monster rules.
- **Healing & buffs** — HP / mana thresholds, resting, cures, buff upkeep with a **Buff Watchdog**, and a **Spell Book** with cast odds and damage.
- **Navigation** — a room map with go-to routing, loops, **Auto-Lair**, doors, traps, hazards, tolls, boats and teleports, plus exp/hour estimates.
- **Party play** — shared healing and buffs, wait / follow coordination, party auto-train, and remote `@`-commands gated by per-player permissions.
- **Items & cash** — auto loot / sell / buy / stash / bank, drop / hide / equip sweeps, and gear sets that swap themselves.
- **Character Workshop** — stats, Equipment Manager and Item Finder, CP plans, level projection, quests, bosses, death recovery, and calculators.
- **Game Data** — import MajorMUD `.mdb` sets; browse, filter and override every monster, item, spell and room; **Monster Intel** answers "can I fight this?".
- **Automation** — macros, aliases, triggers, scheduled events, per-engine toggles, and a one-press kill switch.
- **Chat** — a conversation window with channel filters, search and history.
- **Help & diagnostics** — a built-in Help guide, first-run setup tour, update checker, scrollback search, program log, session stats, wire inspector, and a one-click **bug reporter**.
- **Your layout** — editable toolbar, rebindable keys, a custom right-click menu, and windows that snap together.

## Getting started

### Requirements

- The [.NET 10 SDK](https://dotnet.microsoft.com/) (the exact version is pinned in `global.json`).

### Build & run

```bash
git clone https://github.com/Tehshortbus/MudPlay.git
cd MudPlay
dotnet build      # compile check
dotnet run        # launch
```

If local state ever gets weird, `dotnet clean` and rebuild.

### First connection

1. **Add a board.** File → **Profile Management** → BBSes → **Add**. Drops you into its settings.
2. **Fill it in.** Host, port, your username + password. If the board needs it, add the **logon steps** — a message to wait for, the reply to send — that walk you from the BBS menu into the game.
3. **Add a character** under that board (Profile Management → Characters → **Add**).
4. **Connect.** **Alt+H**, or File → Connect. You're in.
5. **Want the automation?** Open **Game Data** → **Import .mdb** and pick a MajorMUD database. That fills the monster/item/spell/room tables the engines read from. The terminal works fine without it — the robots don't.

On a fresh install a **setup tour** walks you through these steps. **Help → Help topics…** explains every feature and setting.

### Where your data lives

Everything is stored under a single app-data folder, resolved per platform:

- **Linux** — `~/.local/share/MudPlay/`
- **Windows** — `%AppData%\MudPlay\`
- **macOS** — `~/Library/Application Support/MudPlay/`

Profiles, per-BBS settings, global settings, imported game data, and logs each live in their own subfolder. Settings files store only deltas from the tier beneath them, so they stay small and easy to back up.

## Reporting a bug

MudPlay has a **built-in bug reporter** that snapshots the client's state at the moment of the problem — far more useful than describing it from memory. Please use it when filing an issue:

1. **Capture** — click **Bug Report** in the menu bar (or right-click the terminal → **Bug report…**), type a short description, and confirm.
2. MudPlay writes `<realm>-<timestamp>.md` to your **Desktop**: your settings, character name/stats/inventory, movement-engine state, the program log, and ~750 lines of scrollback — all frozen at click time.
3. **File the issue** at **https://github.com/Tehshortbus/MudPlay/issues/new**, and **attach the `.md` file**.

A short description still helps me target it faster, and you can review the report before sending. ***It does NOT include your BBS login name, password, or logon-menu steps.***

## Contributing

- The build is **zero-warning** (`TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`) and XAML bindings are compile-checked — a clean `dotnet build` is the baseline.
- `dotnet test` runs the xUnit suite (parsers, structural invariants, and critical decision logic).
- Coding conventions, architecture rules, and the per-change Definition of Done live in [`CLAUDE.md`](CLAUDE.md).

## License

MudPlay is licensed under the **MIT License** — see [`LICENSE`](LICENSE).

It bundles third-party components under their own licenses. The full text of each is viewable in-app under **Help → About**:

| Component | License |
|---|---|
| [Avalonia](https://avaloniaui.net/) | MIT |
| [JetDatabaseReader](https://github.com/diegoripera/JetDatabaseReader) | MIT |
| [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) font | SIL Open Font License 1.1 |
| [IBM Plex Sans](https://github.com/IBM/plex) font | SIL Open Font License 1.1 |
| [Px437 / Mx437 (Oldschool PC Fonts)](https://int10h.org/oldschool-pc-fonts/) | CC BY-SA 4.0 |
