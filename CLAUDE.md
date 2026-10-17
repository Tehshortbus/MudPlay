# MudPlay — Claude Code Instructions

> C# / .NET 10 / Avalonia 12 BBS terminal client. Linux primary, cross-platform via Avalonia.
> This file is **workflow guidance**, not architecture documentation. Subsystem invariants live in code comments next to the code they constrain. If a rule here conflicts with what the code actually does, the code wins and this file gets updated.

## Project at a glance

MudPlay is a Telnet-based BBS terminal client. It speaks Telnet (RFC 854/855 with NAWS and TERM-TYPE), parses VT100/ANSI escape sequences in `Terminal/TerminalEmulator.cs`, renders a CP437 cell grid with a custom Avalonia control in `Controls/TerminalControl.cs`, and uses MVVM (CommunityToolkit.Mvvm source generators — `[ObservableProperty]`, `[RelayCommand]`).

What it is **not**: not a PTY wrapper, not a generic SSH client, not a curses-style application terminal. All ANSI parsing is explicit; we don't rely on a host TTY.

## Workflow

The phased implementation plan is **complete** — the app is at **1.0.0** and in a bugfix / maintenance cycle. There is no more `docs/` build plan; work now flows as focused fixes and small enhancements, each on its own branch + PR.

- **PR cadence**: one open PR at a time; the next doesn't begin until the current merges. **Bug reports dropped together all land on the same open PR** — a batch shares one branch + PR (consolidated changelog entry, PATCH counting the reports per the versioning rule below). The exception is a report that needs **more than a fix to resolve** — a significant feature-scale design change or new implementation: **suggest breaking that onto its own PR and let the user decide** (they'll agree, or tell you to fold it into the current open PR). Keep each PR focused (see PR-size discipline under Scope discipline). The user merges the PR when it looks good to them; after the merge, wait for whatever comes next. **You never merge or close a PR yourself, and you never auto-open a *follow-up / split* PR — those are the user's calls (see the auto-commit rule below).**
- **Auto-commit + push when a batch is done — don't wait to be asked.** The default "never commit unless explicitly asked" holds *mid-work*, but the moment a batch of fixes/feature is genuinely finished — `dotnet build` clean, full `dotnet test` green, and the version-history trio (csproj `<Version>`, CHANGELOG top entry, README current-version block) updated — commit it (with the `Co-Authored-By` trailer) and push on your own. **Opening a PR autonomously is OK only when the user handed you a discrete new work item** — a bug report (or batch of them), or a feature / fix / update to the client — that fresh item gets its own PR. **When the work is a follow-up to, or a split of, the PR you're already in, do NOT open a new PR on your own: the user decides whether it folds into the current PR or gets a new one — wait for that call.** If a PR already exists for the branch, confirm it's still open (`gh pr view <n> --json state`) and push to it. **Never close or merge a PR unless the user explicitly said to.** Never auto-commit a red build / failing tests / half-finished work — this is the *closing* move on a completed batch, not a running habit. **After each commit, rebuild the user's debug local so what they run reflects the latest** — which is why the working branch lives on their main checkout, not an isolated worktree.
- **Versioning (semver, post-1.0)**: the version lives once in `MudPlay.csproj` `<Version>` (`AppInfo.Version` reads it back for the `@version` reply). Bump it by **change type, not PR size**:
  - **MAJOR** — a whole-program refactor or other sweeping/breaking overhaul. Rare.
  - **MINOR** — a **brand-new feature** (a capability that didn't exist before), **or a large rewrite / expansion of an existing feature** (roughly **1000+ lines net**). One bump per such change, and it resets PATCH to 0 (e.g. 1.5.11 + a new feature → 1.6.0).
  - **PATCH** — bug fixes **and ordinary enhancements to existing features** (a changed default, a tweak, a smaller improvement — anything under the ~1000-line MINOR bar). **Increments by the number of bug reports handled plus the number of enhancements** — one report or one enhancement on 1.5.11 lands 1.5.12; a batch of five handed over together advances 1.5.11 → 1.5.16. An ordinary enhancement counts exactly like a bug report: it moves the third number, never the minor.

  A PR that mixes types takes the highest (a new feature or a 1000+-line expansion plus fixes/enhancements is a MINOR; fixes plus ordinary enhancements stays a PATCH). The version counts the reports + enhancements, but the changelog does **not** need one entry per item: a batch shares a single consolidated `## <final-version>` entry whose bullets cover all the changes.
- **Every PR updates the version history.** `CHANGELOG.md` is the running record, newest entry first. On each PR, in the same branch:
  1. Bump `<Version>` in `MudPlay.csproj` per the semver policy above.
  2. Prepend a new `## <version>` section to `CHANGELOG.md` (above the previous entry) — **no summary paragraph, no Added/Changed/Fixed/Removed subheads**, just a short bullet list, one terse line per change. Short, sweet, to the point: describe the *effect*, not the diff, in a handful of words. A couple of bullets, not a prose write-up. (Example: `## 1.5.11` → `- Party-wide toll gate checkbox removed, now always on` / `- Navigation engine verifies party's cash before using a toll en-route`.) **When the entry closes one or more bug reports, its LAST bullet is `- bug reports addressed: <name>, <name>` naming the report file(s)** (e.g. `stock-20260707-203503`, `Crash-20260707-210804`) — so confirming a report shipped is a grep of CHANGELOG, not an investigation. Omit the line for a pure feature/enhancement entry with no report behind it.
  3. Replace the current-version block at the top of `README.md` (between the `<!-- current-version:start -->` / `<!-- current-version:end -->` markers) so it mirrors the new top CHANGELOG entry — a `> **Version <x.y.z>**` header line, then the same terse bullets. The `- bug reports addressed:` line is a bookkeeping tail for the CHANGELOG; leave it out of the README mirror.

  These three moves are part of Definition of Done — a PR that changes behavior but leaves the version, changelog, or README block stale is incomplete.
- **Code-review gate per PR** (in addition to Definition of Done below): scan the diff for dead code, stale comments, function placement against the folder layout, duplication of existing helpers, thread-safety, and PR-size discipline. New tests drive the real paths (see Tests), and no `…ForTests` member appears in production.
  - **Feature reachability, not just dead code.** Dead-code scans only catch *unreferenced* symbols — they miss a feature that's been *half-disconnected*: its code is still referenced (so not "dead"), but a rework removed the entry point that let a human reach it. When a PR **removes or reworks an entry point** (menu item, button, window section, command binding, tab, right-click affordance), verify the feature it fronts is still **reachable and usable end-to-end** afterward — or is explicitly parked with a note. Flag "half-wired" surfaces: symbols still referenced but no longer reachable by a user, and empty-state traps (an affordance that only appears once content already exists, so the first item can never be created). This is the gap that stranded GOTO-favourites management for a long time.
- **Push every commit to the open PR — but confirm it's still open first.** Before pushing follow-up work onto a branch with an existing PR, check the PR hasn't already merged (`gh pr view <n> --json state`). A merged PR means the branch is dead: new commits pushed to it strand outside `main`, so carry them to a fresh branch + PR instead. When the PR is confirmed open, every commit landed before it merges goes straight up — never accumulate local commits the user has to ask about. After each commit (or batch of related commits), `git push` so the PR on GitHub matches local HEAD. If the PR description's scope goes stale because of the new commits, refresh it via `gh pr edit` in the same push.
- **Reproduce from a bug report.** Users capture client state via the in-app **Bug Report** (menu-bar button or terminal right-click → *Bug report…*), which writes a Markdown snapshot to their Desktop that they attach to a GitHub issue. When fixing a reported bug, start from that capture — its Holds and waits / Commands sent / Warnings and errors / Movement / Player / Settings / Program-log / Scrollback sections pin the failing state at the moment of the problem. For an "it just sat there" or "why did it send that" report, start with Holds and waits and Commands sent. **Read every timestamped section newest-first: the report is captured right after the problem, so the incident lives at the END of any section that carries timestamps (Scrollback, Program-log, Movement, death history, and the like) — start at the latest timestamps and work back toward the earliest.** Entries near the top are old context, not the failure; never judge a report "not a bug / working as designed" off an early cycle — confirm against the last events. (The program log covers several minutes and is the longer window; warnings and errors have their own section that is never trimmed; cross-reference the timestamped sections by their shared clock.)
- **Never invent game mechanics.** MajorMUD / MegaMUD behavior is domain truth the code depends on — how a mechanic actually works (disarm/unequip effects, equip verbs, timers, message wording, stacking/immunity rules, party quirks, etc.) is **not** something to guess or infer from plausibility. A fabricated mechanic silently poisons every decision built on top of it — it's a dangerous path (e.g. assuming a "disarm / forced-unequip" effect exists when the game has none). If you're unsure how a game-engine mechanic behaves, **ask the user before building on it.** State the assumption explicitly, flag it as unverified, and get it confirmed. Confirmed mechanics get recorded in `GAME_MECHANICS.md` (the running reference of how the engine functions and what messages it emits) so the next session doesn't re-guess — read it before reasoning about engine behavior, and append to it when the user confirms something new. How to use and edit it: the next section.

## GAME_MECHANICS.md — the engine reference

`GAME_MECHANICS.md` (repo root) is the trusted record of how the MajorMUD engine behaves on Stock and Paradigm, and the exact text it prints. Facts in it are not re-derivable — each one came from the user, a bug-report capture, or the imported game data. Treat it like code: keep it structured, keep it true, never let it drift.

### Looking something up

- **Before reasoning about or building on any engine behavior**, look it up here. Find the chapter by subject in the Contents at the top, then **grep** for the exact thing: the message text, the command, a spell / item / monster / TBInfo number, an ability name or code, or a data field (`RegenTime`, `CastsSp`).
- **Read the whole topic.** Check three things:
  - the **status line**: how sure we are, when, from where, and which realm;
  - the **inline tags** on individual bullets;
  - the **Client use** list: which code already relies on the rule.
- **A missing realm means "not recorded", not "both".** Never apply a rule to the other realm without checking. Stock and Paradigm differ in many places. Paradigm and GreaterMUD are the same game, and MMUD-Explorer's `bGreaterMUD` branch is Paradigm.
- **Tagged facts need care before you build on them.**
  - `[NEEDS CONFIRMATION]` or **Unrated**: check the code or data, or ask the user.
  - `[CONFLICT — ask the user]`: always ask the user.

### What belongs in it — and what doesn't

**Belongs:**
- engine behavior;
- exact wire text, verbatim;
- command syntax and verbs;
- formulas;
- game-data field meanings (ability codes, TBInfo directives, table columns);
- realm differences;
- timings and caps.

Each fact carries its source: user confirmation, report ID, capture, or game-data lookup. A **Client use** note says which class/method relies on a rule, and the report that drove it. A **Client policy** note records a MudPlay design decision, and only when it sits beside the mechanic it acts on.

**Doesn't belong:**
- client architecture, UI or settings docs (settings go in the Help guide, `Assets/Help/guide.md`);
- bug-fix narratives (one line plus the report ID at most);
- speculation presented as fact;
- `[[wiki-links]]` to private notes;
- anything you inferred and nobody confirmed. If you must record a belief, tag it `[NEEDS CONFIRMATION]` with the question.

### Structure

- **Front matter:** title, purpose, the tag legend, the entry format, how to add an entry, and the Contents.
- **16 fixed chapters, in this order.** Don't add or reorder chapters without asking:
  1. **Timing & rounds** — combat and spell rounds, swings/fires per round, exp/hr ceilings, step time per room.
  2. **Wire, prompt & command output** — prompt/statline, rate limiter, output formats of info commands, and the message catalogue (lines the client parses).
  3. **Talk & chat channels** — say/gossip/gang/telepath forms, self-echo, telepath throttling.
  4. **Character stats & progression** — CP, exp tables, trainers, and each stat's derived formulas.
  5. **Armour, defence & to-hit** — AC/DR storage and sources, to-hit floors, dodge caps, reflect.
  6. **Health, resting & recovery** — HP to 0, dropping, resting rules, regen, `look` wound bands.
  7. **Combat** — attack announce/repeat, what breaks combat, the attack cascade, weapons, backstab, monster aggro and targeting, kill detection.
  8. **Spells, buffs & conditions** — cast chance, resists, monster type tags, RemovesSpell, buff timers, ailments and cures, item-cast spells.
  9. **Monsters, lairs & spawns** — lair/NPC/boss respawn, `Summoned By`, exp values, monster movement lines.
  10. **Movement & navigation** — moves and bonks, dark/blind, light, stealth, doors/gates/winches, hidden exits, tolls, teleports, boats, hazards, route gates, `rm`.
  11. **Items, inventory & equipment** — acquire/get/drop/batching, room item caps, equip/wear, slots, charges, chests, NPC hand-overs.
  12. **Money, banks & shops** — denominations, coin wire text, stashing, bank commands, shop pricing, `price` directives.
  13. **Party** — `par`, follow/drag, dropping, leader loss, training, ailment and `@`-signal exchange between party clients.
  14. **Quests** — quest-flag values and the kill / dialogue step shapes.
  15. **Death & corpse recovery** — death threshold and lines, the effect wipe, deathpile / corpse / spill-over, coins.
  16. **Sysop commands** — `sys …` gating and output.
- **Headings:** `##` is a chapter, `###` is a topic. Nothing deeper; use bullets and sub-bullets inside a topic. One `###` per subject. Never two topics on the same subject in different places.
- **Topic shape:**
  1. **Status line** directly under the heading:
     `*Status: CONFIRMED 2026-09-26 (user; report \`paradigm-20260926-102406\`) · Realm: both | Paradigm | Stock | differs*`
     When facts in one topic have different confidence, put the tag inline on each bullet instead.
  2. **Rules as bullets**, each leading with the rule in **bold**, then the detail. Exact game text and commands go in backticks, verbatim, including case and trailing punctuation.
  3. A **Client use:** list at the end, when code relies on the rule: class/method names plus the report that drove it.
- **Tags:**
  - `[CONFIRMED]` — the user said so.
  - `[OBSERVED]` — a capture, the code, or a game-data lookup.
  - `[NEEDS CONFIRMATION]` — unverified; add the question.
  - `[CONFLICT — ask the user]` — two statements disagree; keep both until the user settles it.
  - **Unrated** — no confidence was recorded.
  - **Client policy** — a MudPlay design decision.
- **Dates and IDs:** dates are absolute (`YYYY-MM-DD`), never "today". Report IDs stay backticked and complete (`paradigm-20260714-002413`, not `002413`).
- **Cross-references** name the exact heading: *Chapter → Heading*, or just *Heading* within the same chapter. Never "see above / below / the next topic".

### Adding or changing an entry

1. **Search first.** Grep the command, message text, spell/item number and ability name, so a fact is never recorded twice. If the subject has a topic, **extend it**. Start a new `###` only for a genuinely new subject, in the chapter its subject belongs to.
2. **One home per fact.** If a fact touches two chapters, put it where its subject lives and cross-reference it from the other. Don't copy it.
3. **Settle what you can before asking.**
   - Client-behavior questions: settle from the code.
   - Data questions (ability codes, TBInfo chains, room exits, item fields): settle from the imported game data under the app folder's `game data/{set}/`.
   - Only questions about how the game engine behaves go to the user.
4. **When new information contradicts an entry**, rewrite the entry to the current truth. Keep the old claim as a one-line "(an earlier note said …; superseded <date>)". Never leave two live statements that disagree. If you can't tell which is right, mark `[CONFLICT — ask the user]` with the question.
5. **Record user confirmations right away**, with the date and "user" as the source, when the answer arrives. Add a Client use line if code now depends on the rule.
6. **Headings are referenced.** Code comments cite sections by heading (e.g. `GAME_MECHANICS "Winch gates"`), and so do other topics. When you rename a heading, grep both the repo and the file and update every reference in the same change.
7. **Lose nothing.** Rewrites and merges keep every number, report ID, backticked term, date, source, caveat and Client use note. Before committing a restructuring edit, diff the backticked terms and report IDs against the previous version.

## Project structure

Top-level folders, each with one responsibility (no catch-all `Util/` / `Common/` / `Helpers/` — new code goes in the folder whose name already fits; domain logic never leaks into `Views/` or `Controls/`):

- **`Terminal/`** — ANSI/VT100 parser, CP437 cell grid, palette (`TerminalEmulator.cs`, `TerminalScreen`).
- **`Net/`** — Telnet protocol (`TelnetClient.cs`, `TelnetProtocol.cs`), off-UI-thread I/O.
- **`Controls/`** — Avalonia custom controls (`TerminalControl.cs`); cell rendering + macro-key interception.
- **`Views/`** — XAML windows (all modeless).
- **`ViewModels/`** — MVVM presentation glue (CommunityToolkit.Mvvm source-gen).
- **`Services/`** — cross-cutting infrastructure: the POCO `AppServices` holder, profile/settings I/O, `SettingsResolver` (4-tier merge), `MessageRouter`, `DialogService`, log/debug writers, importers, `GameDataCache`.
- **`Game/`** — the MUD-domain layer (combat, party, map/navigation, spells, inventory, macros, events, remote commands), grouped by subsystem subfolder.
- **`Models/`** — DTOs: `Profile/` (per-character) and `Settings/` (global).
- **`Assets/`** — fonts, embedded resources.

### Data flow

| Direction | Path |
|---|---|
| **Server → screen** | `TelnetClient` → `TerminalEmulator` → `TerminalScreen` → `TerminalControl` (render) |
| **Server → game state** | `TerminalScreen` → `LineExtractor` → `MessageRouter` (fan-out) → subsystems → observable game-state → ViewModels → Views |
| **User → server** | `TerminalControl` key → `MainWindowViewModel.SendUserInput` → `TelnetClient.SendAsync` |

### Data layout (single app-folder root)

All app data lives directly under the app folder, resolved per-platform by `AppPaths` (Linux `~/.local/share/MudPlay/`, Windows `%AppData%\MudPlay\`, macOS `~/Library/Application Support/MudPlay/`) — no nested `Data/` level (older installs are lifted out of it on first launch by `FlattenDataSubfolder`). Files store **deltas only**, stacked per the 4-tier hierarchy:

- `game data/{set}/*.json` — imported MDB tables (Defaults tier, read-only base); `game data/{set}/Loops/` holds the set's loops and Auto-Lair setups.
- `Global/global.json` — Global-tier setting + game-data deltas, default active set.
- `BBS/{bbs}/bbs.json` — BBS-tier setting deltas + connection info (host / port / accounts) and the BBS's realms.
- `BBS/{bbs}/Realms/{realm}/` — the BBS tier's game-data overrides (they follow the active realm) and the realm's shared files: players seen, bosses and boss timers, learned room names, staged message candidates, Roomba data.
- `BBS/{bbs}/profiles/{char}/profile.json` — character workspace + Char-tier deltas (auth, macros / triggers / events, equipment sets, favorites, quest state, death history, statline); the character's side files (e.g. `DeathLogs/`) sit in the same folder.
- `Logs/` — debug logs.

Merge order, first hit wins: `BBS/{bbs}/profiles/{char}` → `BBS/{bbs}` (settings) / `Realms/{realm}` (game data) → `Global/global` → app Defaults → game-data Defaults.

## Architecture rules (cross-cutting)

These are workflow rules — short enough to live here. Subsystem-internal invariants live in code comments next to what they constrain.

- **All windows are modeless.** The terminal must remain interactive while any settings/editor/dialog window is open. `DialogService.OpenWindowAsync<TViewModel,TResult>()` is the only spawner; uses `Window.Show()` + a `TaskCompletionSource`. There is no `ShowDialog()` wrapper — modal-by-mistake is impossible.
- **Every window's menu / hotkey / toolbar entry is one three-state toggle** (user, 2026-09-29). Every command that spawns a window (View menu entries, Tools panels, the InfoDialogs under Help, the toolbar icons that mirror them, the hotkeys) behaves the same way:
  - **not open** → open it;
  - **open but buried** (behind another window, minimized) → bring it to the front;
  - **already in front** → close it. "In front" is focused (`Window.IsActive`), or, while one of our windows has focus, above every window of ours it overlaps (`Window.SortWindowsByZOrder`). A hotkey is usually pressed in the terminal, so focus alone would never read the window it toggles as in front.

  **`DialogService.RaiseOrClose(window, close)`** implements it (`MainWindowViewModel.RaiseOrClose` forwards). Closing only when the window is in front keeps a re-press from shutting a window the user was hunting for behind another one or another client (the reason the old always-toggle-closed convention was dropped). **`RaiseExisting`** (un-minimize + a Topmost flip for Linux WMs + `Activate()`, never closes) is kept for deep links only: a menu entry or link that opens a window at a section / tab / record navigates there and raises. It closes the window only when the window is already showing that target and is in front.

  Implementation pattern: track the open instance in a field or a `Dictionary<id, Window>`, hook `Closed` to null/remove the tracker, and on re-press call `RaiseOrClose` instead of constructing a new window.
- **A window's title is its own name; the profile and BBS are added for it** (user, 2026-10-06). Every window other than the main one is titled `<menu name> — <profile> — <bbs>:<realm>`, the tail the main window carries. `Controls/WindowTitleTail` appends and refreshes that tail for every window, however it was opened, so set `Title` (in XAML or bound) to the window's name alone and never build the profile or BBS into it.
- **Edit windows: the re-press close saves.** A window that edits persisted state with explicit Save / Cancel buttons (Settings) has these close paths:
  - **Save / OK button** → apply pending changes and close (`ApplyAndClose()`).
  - **Cancel button or title-bar X** → discard pending changes and close (`DiscardAndClose()` or the commit-nothing dispose).
  - **The hotkey / menu / toolbar that opened it, pressed while it's in front** → the same as OK: `RaiseOrClose(existing, vm.ApplyAndClose)` (user, 2026-09-29). Pressed while it's buried, it only raises and leaves pending edits untouched.
  - **The client exiting, or restarting for an update, with the window open** → save the pending changes, as OK would, with no prompt (user, 2026-10-08): `MainWindowViewModel.SavePendingSettingsForExit` → `ApplyPendingForExit()`. A dirty section with a `SaveWarning` is left unsaved and logged, since its "Save anyway?" can't be asked. The window's own `Closing` handler discards only for its own X (`CloseReason == WindowClosing`), never when the client is closing it.

  Windows that apply edits as they're made (Workshop, Game Data, Navigation, Profile Management, Quick Connect) have no pending state, so a plain `RaiseOrClose` closes them.
- **POCO service holder, no DI container.** `AppServices` (singleton, instantiated in `App.OnFrameworkInitializationCompleted`) constructs and exposes services as instance properties. Consumers receive instances explicitly. Per-character / per-game-data lifetime is event-driven: services subscribe to `ProfileService.ProfileLoaded` and `GameDataCache.ActiveSetChanged` and reload their per-scope state in handlers. `IAsyncDisposable` on services holding background work; outgoing per-char services are disposed before swap.
- **4-tier settings hierarchy.** All settings + game-data record overrides go through `SettingsResolver`. Tiers: Defaults → Global → BBS → Character. Reads merge in priority order; writes target an explicit tier (`SettingsResolver.WriteAt(scope, ...)`). Tier picker UI uses MegaMUD-parity labels (`installed defaults` / `for all characters` / `only for this BBS` / `only for this character`). No file duplication — each tier file stores deltas only.
- **Single-writer invariant on observable fields.** Each `[ObservableProperty]` field on `PlayerState` / `PartyState` / etc. carries an `[Owner(typeof(SomeWriter))]` attribute declaring its sole writer. Multiple writers to the same field is forbidden — enforced by the single-writer test that scans assembly IL via Mono.Cecil. Consumers subscribe to the observable, never to the writer. If you need to update an observed field, route through its owner.
- **`MessageRouter` is fan-out, not exclusive.** Every matching pattern fires for a given line; priority orders execution, not exclusivity. Same line can drive ChatRouter + Triggers + CombatSessionTracker simultaneously. Handlers run on the UI thread (already marshalled upstream); long work in a handler must offload via `Task.Run`.
- **Statline is server-owned.** The Phase 12 Settings → Statline tab builds a wildcard string and the app sends `set statline <string>` to the game on logon (and on parser mismatch). `PromptParser`'s regex is generated from the same string, so parsing is always in sync. Don't hand-author parser regexes that drift from the editor's output.
- **Every feature audits its settings + gamedata + permissions surface.** Before a feature ships, enumerate every Settings field (across all tabs, all tiers) that affects it, every Game Data option the user can set that relates to it, and every PlayerRemoteControls permission flag involved. Map what it reads vs writes vs reacts-to-changes-of. Features built without this audit silently miss connections — settings end up not doing what they claim and gamedata permissions don't gate the right things. Audit at design time, wire at implementation time, verify on the per-PR review pass.
- **Every feature audits its program-logging + bug-report surface.** When adding a feature or changing an existing one, verify and update the two diagnostic surfaces it touches: (1) **program log** — does the new behavior emit `LogService` entries at the right severity for the events an operator needs to see it work (lifecycle/decisions at Info, verbose per-line detail at Debug/Combat)? Add or adjust them so a log read explains what the feature did. (2) **bug report** — does `BugReportBuilder` capture the new state a report would need to diagnose the feature (new flags, engine state, targets)? Add the fields. New state that never reaches the log or the capture is invisible when a user reports it broken. Audit at design time, wire at implementation time, verify on the per-PR review pass — same as the settings/gamedata surface above.
- **Every feature audits its Help-compendium surface.** The Help window (`Assets/Help/guide.md`, opened via Help → Help topics) is the user-facing explanation of how features work, how to use the client, and what every setting means. When you add a feature, change behavior in a way that affects how a user understands or uses something, or **wire up a control the guide flags as non-functional**, update the guide to match — the Help menu must never drift out of sync with the program. Help text that still describes old behavior, or still flags a now-wired setting as "not functional", is a defect the same way a stale CHANGELOG is. Audit at design time, wire at implementation time, verify on the per-PR review pass. Part of Definition of Done.
- **Remote-command reply policy.** All invalid / denied @-command replies — engine-emitted (unknown command, permission-denied, suicide policy block) AND handler-emitted (e.g. `@suicide` invalid-password) — obey `Settings.Talk.WarnOnInvalidRemoteCommand` as a master gate. When unchecked, ALL failure replies are suppressed. When checked: a specific failure path uses its specific reason (e.g. `"suicide blocked, N lives <= threshold M"`); generic paths fall back to `Settings.Talk.RemoteCommandFailureMessage`. Replies always travel on the SAME chat channel the command arrived on — `RemoteCommandManager.SendReply` already routes this way via the `RemoteChannel` switch. Unconditional hard-blocks (`reroll`, `@party suicide`) are the exception — they stay silent regardless of WarnOnDenial because any reply leaks info to a malicious caller. Handler-side failure replies must explicitly check `_engine.WarnOnDenial` before invoking `ctx.Reply` (success replies like `@health`'s vitals payload don't gate).

## Build & run

```
dotnet build      # compile check
dotnet run        # launch
dotnet clean      # if state gets weird
```

- The SDK is pinned in `global.json`. Don't pass `--framework`, `--runtime`, or `-c` overrides unless the user asks — the `.csproj` already knows what to produce.
- **Zero-warning policy.** `MudPlay.csproj` has `TreatWarningsAsErrors=true` and `EnforceCodeStyleInBuild=true`. A build that emits warnings is a failed build. Fix the warning rather than suppressing it; if a `#pragma warning disable` is genuinely necessary, leave a one-line comment explaining why on the line above.
- **XAML bindings are compile-checked.** `AvaloniaUseCompiledBindingsByDefault=true` means a typo in a `{Binding ...}` is a build error, not a silent runtime no-op. Treat XAML compile errors with the same seriousness as C# ones.

## Tool invocation rules

These exist to keep permission prompts from interrupting the session. Apply them in every Bash call — including from sub-agents (review, explore, plan, fixer).

- **Don't prefix Bash with `cd "<project-path>" &&`.** The working directory is already set to the project root. The `cd <cwd>` prefix turns auto-allowed commands (`grep`, `git`, `dotnet build`, etc.) into compound commands that miss the allowlist and force a fresh permission prompt every time. Run the command directly. Sub-agents may use absolute paths in *arguments* (e.g. `git diff /home/fujin/Desktop/Projects/MudPlay/Foo.cs`), but must not prefix the invocation with `cd`.
- **Use the Read tool, not `cat`/`head`/`tail`/`sed -n`.** Read returns numbered lines and supports offset/limit.
- **Use the Edit / Write tools, not `sed -i` / `echo > file`.** Edits render as diffs the user can review.
- **Use the Grep / Glob tools, not Bash `grep`/`find`** for searches. Reach for shell `grep` only when piping through other shell filters in a way Grep can't express.

We are on Linux with fish. There are no PowerShell or `cmd.exe` rules to worry about.

## Code style — what the codebase already does

This is descriptive, not aspirational. Match what's there:

- **File-scoped namespaces** (`namespace MudPlay.Terminal;`).
- **`sealed class` by default.** Un-seal only when something actually needs to inherit, and say why.
- **`readonly record struct`** for value types like `Cell` and `CellAttributes`. Update with `with` expressions, not mutation.
- **Naming**: `_camelCase` private fields, `PascalCase` for everything public (types, methods, properties, events).
- **Events**: `public event Action<T>?` on the producer; subscribers add `+=` and unsubscribe with `-=` when the producer can outlive them (see `MainWindowViewModel` swapping emulators).
- **Nullable reference types are on.** Initialize fields, or use `= null!;` with a comment explaining who sets the value before first use.
- **Latest C# language features are allowed** (`LangVersion=latest`). Implicit usings are on (`ImplicitUsings=enable`); add explicit `using` statements only when the namespace isn't covered by the SDK defaults.
- **No `#region` blocks.** Group related members with a one-line `// ----- Section ---------` comment if a file genuinely needs visual breaks (see the parser-state section in `TerminalEmulator.cs`).

## Threading & async

- **Telnet I/O runs off the UI thread.** Anything that touches view-model state, controls, or invokes `InvalidateVisual` must be marshalled with `Dispatcher.UIThread.Post(...)`. Both `MainWindowViewModel` and `TerminalControl` already do this — follow the existing pattern.
- **Async returns `Task` / `Task<T>`.** Never `.Result` or `.Wait()` — propagate `await` up. `async void` is for event handlers only.
- **Use `ConfigureAwait(false)` on awaits inside library/network code** (see `TelnetClient`). View-model code that needs to land back on the UI thread can omit it, but is usually clearer if it `Post`s explicitly.
- **`IAsyncDisposable`** for anything holding a socket or background pump (`TelnetClient`). The owner calls `DisposeAsync` when swapping it out.
- **Locks are named for what they protect** (`_dumpLock` guards the dump-stream handle, not "the telnet client"). Keep the locked region small; never call `await` while holding a `lock`.

## Comment philosophy

- **Default to no comment.** Well-named identifiers carry the *what*. If you're tempted to write a comment that restates the code, rename instead.
- **Write a comment when the *why* is non-obvious**: a hidden invariant, a workaround for a real-world BBS quirk, a deliberate spec deviation. Examples already in the tree:
  - *"VT-style delayed wrap matches xterm and BBS expectations."* (`TerminalEmulator.cs`)
  - *"Aliased rendering to prevent color fringing on block-drawing characters."* (rendering code)
- **Never** write `// added for X feature`, `// used by Y`, or `// TODO(date): …` referencing a current task. Those rot the moment X gets renamed or Y goes away. Rationale belongs in commit messages and PR descriptions, not in the source.
- **Plain `//` comments, not XML doc tags.** This is an app, not a published library — nobody consumes generated API docs. Don't write `/// <summary>`, `</summary>`, `<param>`, `<returns>`, `<see cref="..."/>`, `<c>`, `<b>`, etc. Use a normal `//` (or `/* */`) comment in prose. Refer to other code by plain name — `RoomTracker`, `TerminalEmulator.cs` — not a `cref` link. A short prose sentence beats a tag-decorated one.
- **Explain the decision, not the mechanics.** A comment earns its place by capturing *why* the code is shaped this way — the constraint, the quirk, the tradeoff. It must not narrate what the next line plainly does. Short, sweet, to the point; no fluff.
- **No references to projects outside this repo.** Don't name tools/apps the code was ported from or modeled on (no "MMUD Explorer", no "MudProxy", no phase-plan / `docs/` / `PR N.N` breadcrumbs). Rewrite the comment so the *why* stands on its own; if the reference was the only content and the code is self-evident, delete the comment. **MegaMUD and MajorMUD are the exception** — they're the game and the reference client this app targets, so naming them for parity/behavior context is legitimate domain "why".

## Scope discipline

- **No speculative abstractions** for "future BBSes" or "other transports." Three similar lines is fine. Extract on the fourth, when you can see the shape.
- **No backwards-compat shims** for code that hasn't shipped externally. Just change it. Renames are free; ceremony is not.
- **No silent error swallowing.** If a Telnet/ANSI sequence is malformed, clamp or skip it explicitly and (where appropriate) surface it via the existing `Log` event or `StatusText`. An empty `catch {}` needs a one-line comment justifying it (see the `await _readLoop.ConfigureAwait(false)` shutdown path in `TelnetClient` — that one is intentional).
- **No new top-level folders without reason.** Current layout — `Terminal/`, `Net/`, `Controls/`, `Views/`, `ViewModels/`, `Assets/`, `Services/`, `Game/`, `Models/` — covers the responsibilities. `Services/` holds cross-cutting infrastructure (POCO service holder, profile/settings I/O, settings resolver, message bus, dialog spawner, log/debug writers, importers, game-data cache, owner attribute); `Game/` holds the MUD-domain layer (combat, party, map, spells, inventory, macros, events, etc., grouped by subsystem subfolder); `Models/` holds DTOs (`Profile/` per-character settings, `Settings/` global settings). New code goes in the folder whose name already fits.
- **Don't refactor on the side.** A bug fix or feature stays focused. If you spot something worth cleaning up, mention it; don't bundle it.
- **No duplication of existing functions.** Before adding a helper, search for an existing one. If a similar function exists, reuse it. Duplicating is allowed only when absolutely necessary, and requires a one-line `// why` comment on the duplicate.
- **Function placement matches the folder layout.** Parsing logic in `Terminal/`, network protocol in `Net/`, MUD-domain logic in `Game/`, presentation glue in `ViewModels/`, XAML windows in `Views/`, custom controls in `Controls/`, infrastructure in `Services/`. Domain logic must not leak into views or controls.
- **No dead code.** A method/field/property/event with no callers is deleted, not retained "for later". CLAUDE.md already forbids speculative scaffolding; this is the same rule, enforced at review.
- **File contents over file size.** A `.cs` file can be whatever length its responsibility honestly is — what matters is that the code inside it actually belongs there. Combat logic lives in a combat-themed file under `Game/Combat/`, not bolted onto `MainWindowViewModel.cs`; map parsing lives in `Game/Map/`, not in a control. If a file is growing because a single coherent responsibility (e.g. the connection lifecycle on the main window VM) is large, that's fine. If it's growing because unrelated concerns are getting bolted on, split them out into the file whose name already fits. One public type per file (small private helper enums/records used only by that type may share the file).
- **PR-size discipline.** Target 600–1000 lines net change per PR, with a soft ceiling of ~1500 lines. Past the ceiling is OK when the work is genuinely a single coherent change that can't be split without leaving the codebase in a broken halfway state — but it's an exception to justify in the PR description, not a default. Better to land a small follow-up PR than to bundle scope just because it's already on the branch.

## Definition of Done (self-review checklist)

Before reporting a change as complete:

- [ ] `dotnet build` is clean — 0 warnings, 0 errors.
- [ ] For changes touching startup, the control, the view, or the view-model: the app still launches with `dotnet run`. Type checking does not verify rendering.
- [ ] For terminal/protocol changes: smoke-test against a real BBS or a captured stream. The state machine has subtle invariants that compile-time checking won't catch.
- [ ] Comments follow the Comment philosophy: plain `//` prose (no XML doc tags / `cref`), explain *why* not *what*, no outside-project references. New non-obvious branch has a `// why` comment.
- [ ] No `Console.WriteLine` left in app code — log via the existing `Log` event so the UI can show it.
- [ ] No `.Result` or `.Wait()` introduced. No `async void` outside event handlers.
- [ ] No new top-level folders. No new dependencies without flagging the choice to the user.
- [ ] Version history updated: `<Version>` bumped, `CHANGELOG.md` gets a new top entry, and the README current-version block mirrors it (see Versioning under Workflow).
- [ ] Help compendium current: if the change adds a feature, alters user-facing behavior, or wires up a previously non-functional setting, `Assets/Help/guide.md` reflects the current program state (see the Help-compendium audit under Architecture rules).
- [ ] `MudPlay.Tests/TEST_CHECKLIST.md` matches the tests in the diff: every added, removed, renamed or repurposed test file has its entry updated.

## Tests

`MudPlay.Tests` is an xUnit project in a sibling folder so `dotnet build` at the repo root builds both. It covers parsers (input → state correctness), structural invariants, and critical decision logic. The cross-cutting single-writer invariant test (Mono.Cecil scans IL for writes to `[Owner(typeof(...))]`-marked fields and fails the build if a non-owner writes) enforces the single-writer rule.

Test scope philosophy:
- Test where compile-time can't catch the concern: parsers (input → state correctness), structural invariants (single-writer, no-cycles), critical decision logic (CastingDirector tier resolution).
- Don't chase coverage numbers. A test that just exercises the type system without finding bugs is dead weight.
- UI rendering, view-model bindings, and Avalonia plumbing are not unit-tested — smoke-tested via `dotnet run`.
- Add a test the first time a class shows behavior worth pinning down. Don't write tests speculatively.

The test checklist:
- `MudPlay.Tests/TEST_CHECKLIST.md` is the record of every test file: one entry per file with what it covers, what it checks and why. Adding, removing, renaming or repurposing a test updates its entry in the same change.
- Every entry names the client behaviour it protects. A test that can't name one is deleted, not added.

Real paths only:
- Tests drive the client's real input and output paths: game output as bytes through `ServerFeed` (a `TerminalEmulator` + `LineExtractor`), chat through `ChatLines` and the router, the wire through each engine's `SetWireSender`, and time through injected schedulers (`ManualTimers`) and clocks (`ManualClock` or an optional `now` parameter). Profiles start the way the client starts them (`TestProfiles`).
- Never a test-only hook: no `…ForTests` member, test-only constructor, reflection into private members, or copy of production logic inside the test. A seam production still carries for a test is named, with its reason, in the checklist entry of the file that uses it.
- A test that deletes folders through the app's stores calls `ScratchRoot.Require()` first, and the suite runs with `MUDPLAY_DATA_ROOT` set to a scratch folder.

## What this file is not

- Not a tour of the architecture — read the code.
- Not a list of subsystem invariants — those live in code comments where they're enforced.
- Not a commit/PR style guide — the repo is too young for that to be useful yet.

When this file falls out of date with the code, update the file. The code is the source of truth.
