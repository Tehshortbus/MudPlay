# FujinTerm — Claude Code Instructions

> C# / .NET 10 / Avalonia 12 BBS terminal client. Linux primary, cross-platform via Avalonia.
> This file is **workflow guidance**, not architecture documentation. Subsystem invariants live in code comments next to the code they constrain. If a rule here conflicts with what the code actually does, the code wins and this file gets updated.

## Project at a glance

FujinTerm is a Telnet-based BBS terminal client. It speaks Telnet (RFC 854/855 with NAWS and TERM-TYPE), parses VT100/ANSI escape sequences in `Terminal/TerminalEmulator.cs`, renders a CP437 cell grid with a custom Avalonia control in `Controls/TerminalControl.cs`, and uses MVVM (CommunityToolkit.Mvvm source generators — `[ObservableProperty]`, `[RelayCommand]`).

What it is **not**: not a PTY wrapper, not a generic SSH client, not a curses-style application terminal. All ANSI parsing is explicit; we don't rely on a host TTY.

## Workflow

The phased implementation plan is **complete** — the app is at **1.0.0** and in a bugfix / maintenance cycle. There is no more `docs/` build plan; work now flows as focused fixes and small enhancements, each on its own branch + PR.

- **PR cadence**: one open PR at a time; the next doesn't begin until the current merges. **Bug reports dropped together all land on the same open PR** — a batch shares one branch + PR (consolidated changelog entry, PATCH counting the reports per the versioning rule below). The exception is a report that needs **more than a fix to resolve** — a significant feature-scale design change or new implementation: **suggest breaking that onto its own PR and let the user decide** (they'll agree, or tell you to fold it into the current open PR). Keep each PR focused (see PR-size discipline under Scope discipline). The user merges the PR when it looks good to them; after the merge, wait for whatever comes next.
- **Versioning (semver, post-1.0)**: the version lives once in `FujinTerm.csproj` `<Version>` (`AppInfo.Version` reads it back for the `@version` reply). Bump it by **change type, not PR size**:
  - **MAJOR** — a whole-program refactor or other sweeping/breaking overhaul. Rare.
  - **MINOR** — a new feature or an enhancement to an existing one (added or changed capability). One bump per feature/enhancement, and it resets PATCH to 0 (e.g. 1.5.11 + a feature → 1.6.0).
  - **PATCH** — bug fixes only. **Increments by the number of bug reports handled** — one report on 1.5.11 lands 1.5.12; a batch of five handed over together advances 1.5.11 → 1.5.16.

  A PR that mixes types takes the highest (a feature plus fixes is a MINOR). The version counts the reports, but the changelog does **not** need one entry per report: a batch shares a single consolidated `## <final-version>` entry whose bullets cover all the fixes.
- **Every PR updates the version history.** `CHANGELOG.md` is the running record, newest entry first. On each PR, in the same branch:
  1. Bump `<Version>` in `FujinTerm.csproj` per the semver policy above.
  2. Prepend a new `## <version>` section to `CHANGELOG.md` (above the previous entry) — **no summary paragraph, no Added/Changed/Fixed/Removed subheads**, just a short bullet list, one terse line per change. Short, sweet, to the point: describe the *effect*, not the diff, in a handful of words. A couple of bullets, not a prose write-up. (Example: `## 1.5.11` → `- Party-wide toll gate checkbox removed, now always on` / `- Navigation engine verifies party's cash before using a toll en-route`.)
  3. Replace the current-version block at the top of `README.md` (between the `<!-- current-version:start -->` / `<!-- current-version:end -->` markers) so it mirrors the new top CHANGELOG entry — a `> **Version <x.y.z>**` header line, then the same terse bullets.

  These three moves are part of Definition of Done — a PR that changes behavior but leaves the version, changelog, or README block stale is incomplete.
- **Code-review gate per PR** (in addition to Definition of Done below): scan the diff for dead code, stale comments, function placement against the folder layout, duplication of existing helpers, thread-safety, and PR-size discipline.
- **Push every commit to the open PR — but confirm it's still open first.** Before pushing follow-up work onto a branch with an existing PR, check the PR hasn't already merged (`gh pr view <n> --json state`). A merged PR means the branch is dead: new commits pushed to it strand outside `main`, so carry them to a fresh branch + PR instead. When the PR is confirmed open, every commit landed before it merges goes straight up — never accumulate local commits the user has to ask about. After each commit (or batch of related commits), `git push` so the PR on GitHub matches local HEAD. If the PR description's scope goes stale because of the new commits, refresh it via `gh pr edit` in the same push.
- **Reproduce from a bug report.** Users capture client state via the in-app **Bug Report** (menu-bar button or terminal right-click → *Bug report…*), which writes a Markdown snapshot to their Desktop that they attach to a GitHub issue. When fixing a reported bug, start from that capture — its Movement / Player / Settings / Program-log / Scrollback sections pin the failing state at the moment of the problem.
- **Never invent game mechanics.** MajorMUD / MegaMUD behavior is domain truth the code depends on — how a mechanic actually works (disarm/unequip effects, equip verbs, timers, message wording, stacking/immunity rules, party quirks, etc.) is **not** something to guess or infer from plausibility. A fabricated mechanic silently poisons every decision built on top of it — it's a dangerous path (e.g. assuming a "disarm / forced-unequip" effect exists when the game has none). If you're unsure how a game-engine mechanic behaves, **ask the user before building on it.** State the assumption explicitly, flag it as unverified, and get it confirmed. Confirmed mechanics get recorded in `GAME_MECHANICS.md` (the running reference of how the engine functions and what messages it emits) so the next session doesn't re-guess — read it before reasoning about engine behavior, and append to it when the user confirms something new.

## Project structure

Top-level folders, each with one responsibility (no catch-all `Util/` / `Common/` / `Helpers/` — new code goes in the folder whose name already fits; domain logic never leaks into `Views/` or `Controls/`):

- **`Terminal/`** — ANSI/VT100 parser, CP437 cell grid, palette (`TerminalEmulator.cs`, `TerminalScreen`).
- **`Net/`** — Telnet protocol (`TelnetClient.cs`, `TelnetProtocol.cs`), off-UI-thread I/O.
- **`Controls/`** — Avalonia custom controls (`TerminalControl.cs`); cell rendering + macro-key interception.
- **`Views/`** — XAML windows (all modeless).
- **`ViewModels/`** — MVVM presentation glue (CommunityToolkit.Mvvm source-gen).
- **`Services/`** — cross-cutting infrastructure: the POCO `AppServices` holder, profile/settings I/O, `SettingsResolver` (4-tier merge), `MessageRouter`, `DialogService`, log/debug writers, panel framework, importers, `GameDataCache`.
- **`Game/`** — the MUD-domain layer (combat, party, map/navigation, spells, inventory, macros, events, remote commands), grouped by subsystem subfolder.
- **`Models/`** — DTOs: `Profile/` (per-character) and `Settings/` (global).
- **`Assets/`** — fonts, embedded resources.

### Data flow

| Direction | Path |
|---|---|
| **Server → screen** | `TelnetClient` → `TerminalEmulator` → `TerminalScreen` → `TerminalControl` (render) |
| **Server → game state** | `TerminalScreen` → `LineExtractor` → `MessageRouter` (fan-out) → subsystems → observable game-state → ViewModels → Views |
| **User → server** | `TerminalControl` key → `MainWindowViewModel.SendUserInput` → `TelnetClient.SendAsync` |

### Data layout (single `Data/` root)

All app data lives under one `Data/` root, resolved per-platform by `AppPaths` (Linux `~/.local/share/FujinTerm/Data/`, Windows `%AppData%\FujinTerm\Data\`, macOS `~/Library/Application Support/FujinTerm/`). Files store **deltas only**, stacked per the 4-tier hierarchy:

- `Data/game data/{set}/*.json` — imported MDB tables (Defaults tier, read-only base).
- `Data/Global/global.json` — Global-tier setting + game-data deltas, default active set.
- `Data/BBS/{bbs}.json` — BBS-tier deltas + connection info (host / port / accounts).
- `Data/profiles/{char}.json` — character workspace + Char-tier deltas (auth, macros / triggers / events, equipment sets, favorites, quest state, death history, statline).
- `Data/Logs/` — debug logs.

Merge order, first hit wins: `profiles/{char}` → `BBS/{bbs}` → `Global/global` → app Defaults → game-data Defaults.

## Architecture rules (cross-cutting)

These are workflow rules — short enough to live here. Subsystem-internal invariants live in code comments next to what they constrain.

- **All windows are modeless.** The terminal must remain interactive while any settings/editor/dialog window is open. `DialogService.OpenWindowAsync<TViewModel,TResult>()` is the only spawner; uses `Window.Show()` + a `TaskCompletionSource`. There is no `ShowDialog()` wrapper — modal-by-mistake is impossible.
- **Open-window menus / hotkeys / toolbar buttons toggle.** Every command that spawns a window (View menu entries, Tools panels, the InfoDialogs under Help, the toolbar icons that mirror them) **closes the existing window if it's already open** instead of activating it. Same applies to the hotkeys (F2 / F4 / F9 / etc.) and the toolbar icon buttons that mirror those commands. Implementation pattern: track the open instance in a field or a `Dictionary<id, Window>`, hook `Closed` to null/remove the tracker, and on re-press `Close()` the existing instance before constructing a new one. New windows added in later phases follow the same pattern.
- **Hotkey-toggle-closes-save, X / Cancel-discards** for edit windows. Any window that lets the user edit persisted state (Settings, Macro / Trigger / Event / Spell editors, Workshop builds — anything with explicit Save / Cancel buttons) has three close paths:
  - **Save button** → apply pending changes and close (explicit).
  - **Cancel button or title-bar X** → discard pending changes and close (explicit).
  - **The toggle hotkey / menu / toolbar that opened the window, pressed again** → treat as Save (apply pending changes and close). The toggle command can't pop a "save or not?" prompt — it's a binary open / close affordance — so we follow the save path by default. The user reaches for Cancel / X when they actually want to discard.

  **Implementation contract**: edit-window VMs expose a public `ApplyAndClose()` method that commits pending changes and closes the window. The toggle command (the `Open*` in `MainWindowViewModel`) calls that instead of `window.Close()` on the re-press path. The title-bar X / Cancel-button path routes to `DiscardAndClose()` (or the equivalent commit-nothing dispose). Read-only windows (LogPane, Backscroll, Conversation, etc.) don't need this — they have no pending state, so `window.Close()` is fine on re-press.
- **POCO service holder, no DI container.** `AppServices` (singleton, instantiated in `App.OnFrameworkInitializationCompleted`) constructs and exposes services as instance properties. Consumers receive instances explicitly. Per-character / per-game-data lifetime is event-driven: services subscribe to `ProfileService.ProfileLoaded` and `GameDataCache.ActiveSetChanged` and reload their per-scope state in handlers. `IAsyncDisposable` on services holding background work; outgoing per-char services are disposed before swap.
- **4-tier settings hierarchy.** All settings + game-data record overrides go through `SettingsResolver`. Tiers: Defaults → Global → BBS → Character. Reads merge in priority order; writes target an explicit tier (`SettingsResolver.WriteAt(scope, ...)`). Tier picker UI uses MegaMUD-parity labels (`installed defaults` / `for all characters` / `only for this BBS` / `only for this character`). No file duplication — each tier file stores deltas only.
- **Single-writer invariant on observable fields.** Each `[ObservableProperty]` field on `PlayerState` / `PartyState` / etc. carries an `[Owner(typeof(SomeWriter))]` attribute declaring its sole writer. Multiple writers to the same field is forbidden — enforced by the single-writer test that scans assembly IL via Mono.Cecil. Consumers subscribe to the observable, never to the writer. If you need to update an observed field, route through its owner.
- **`MessageRouter` is fan-out, not exclusive.** Every matching pattern fires for a given line; priority orders execution, not exclusivity. Same line can drive ChatRouter + Triggers + CombatSessionTracker simultaneously. Handlers run on the UI thread (already marshalled upstream); long work in a handler must offload via `Task.Run`.
- **Statline is server-owned.** The Phase 12 Settings → Statline tab builds a wildcard string and the app sends `set statline <string>` to the game on logon (and on parser mismatch). `PromptParser`'s regex is generated from the same string, so parsing is always in sync. Don't hand-author parser regexes that drift from the editor's output.
- **Every feature audits its settings + gamedata + permissions surface.** Before a feature ships, enumerate every Settings field (across all tabs, all tiers) that affects it, every Game Data option the user can set that relates to it, and every PlayerRemoteControls permission flag involved. Map what it reads vs writes vs reacts-to-changes-of. Features built without this audit silently miss connections — settings end up not doing what they claim and gamedata permissions don't gate the right things. Audit at design time, wire at implementation time, verify on the per-PR review pass.
- **Remote-command reply policy.** All invalid / denied @-command replies — engine-emitted (unknown command, permission-denied, suicide policy block) AND handler-emitted (e.g. `@suicide` invalid-password) — obey `Settings.Talk.WarnOnInvalidRemoteCommand` as a master gate. When unchecked, ALL failure replies are suppressed. When checked: a specific failure path uses its specific reason (e.g. `"suicide blocked, N lives <= threshold M"`); generic paths fall back to `Settings.Talk.RemoteCommandFailureMessage`. Replies always travel on the SAME chat channel the command arrived on — `RemoteCommandManager.SendReply` already routes this way via the `RemoteChannel` switch. Unconditional hard-blocks (`reroll`, `@party suicide`) are the exception — they stay silent regardless of WarnOnDenial because any reply leaks info to a malicious caller. Handler-side failure replies must explicitly check `_engine.WarnOnDenial` before invoking `ctx.Reply` (success replies like `@health`'s vitals payload don't gate).

## Build & run

```
dotnet build      # compile check
dotnet run        # launch
dotnet clean      # if state gets weird
```

- The SDK is pinned in `global.json`. Don't pass `--framework`, `--runtime`, or `-c` overrides unless the user asks — the `.csproj` already knows what to produce.
- **Zero-warning policy.** `FujinTerm.csproj` has `TreatWarningsAsErrors=true` and `EnforceCodeStyleInBuild=true`. A build that emits warnings is a failed build. Fix the warning rather than suppressing it; if a `#pragma warning disable` is genuinely necessary, leave a one-line comment explaining why on the line above.
- **XAML bindings are compile-checked.** `AvaloniaUseCompiledBindingsByDefault=true` means a typo in a `{Binding ...}` is a build error, not a silent runtime no-op. Treat XAML compile errors with the same seriousness as C# ones.

## Tool invocation rules

These exist to keep permission prompts from interrupting the session. Apply them in every Bash call — including from sub-agents (review, explore, plan, fixer).

- **Don't prefix Bash with `cd "<project-path>" &&`.** The working directory is already set to the project root. The `cd <cwd>` prefix turns auto-allowed commands (`grep`, `git`, `dotnet build`, etc.) into compound commands that miss the allowlist and force a fresh permission prompt every time. Run the command directly. Sub-agents may use absolute paths in *arguments* (e.g. `git diff /home/fujin/Desktop/Projects/FujinTerm/Foo.cs`), but must not prefix the invocation with `cd`.
- **Use the Read tool, not `cat`/`head`/`tail`/`sed -n`.** Read returns numbered lines and supports offset/limit.
- **Use the Edit / Write tools, not `sed -i` / `echo > file`.** Edits render as diffs the user can review.
- **Use the Grep / Glob tools, not Bash `grep`/`find`** for searches. Reach for shell `grep` only when piping through other shell filters in a way Grep can't express.

We are on Linux with fish. There are no PowerShell or `cmd.exe` rules to worry about.

## Code style — what the codebase already does

This is descriptive, not aspirational. Match what's there:

- **File-scoped namespaces** (`namespace FujinTerm.Terminal;`).
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
- **No new top-level folders without reason.** Current layout — `Terminal/`, `Net/`, `Controls/`, `Views/`, `ViewModels/`, `Assets/`, `Services/`, `Game/`, `Models/` — covers the responsibilities. `Services/` holds cross-cutting infrastructure (POCO service holder, profile/settings I/O, settings resolver, message bus, dialog spawner, log/debug writers, panel framework, importers, game-data cache, owner attribute); `Game/` holds the MUD-domain layer (combat, party, map, spells, inventory, macros, events, etc., grouped by subsystem subfolder); `Models/` holds DTOs (`Profile/` per-character settings, `Settings/` global settings). New code goes in the folder whose name already fits.
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

## Tests

`FujinTerm.Tests` is an xUnit project in a sibling folder so `dotnet build` at the repo root builds both. It covers parsers (input → state correctness), structural invariants, and critical decision logic. The cross-cutting single-writer invariant test (Mono.Cecil scans IL for writes to `[Owner(typeof(...))]`-marked fields and fails the build if a non-owner writes) enforces the single-writer rule.

Test scope philosophy:
- Test where compile-time can't catch the concern: parsers (input → state correctness), structural invariants (single-writer, no-cycles), critical decision logic (CastingDirector tier resolution).
- Don't chase coverage numbers. A test that just exercises the type system without finding bugs is dead weight.
- UI rendering, view-model bindings, and Avalonia plumbing are not unit-tested — smoke-tested via `dotnet run`.
- Add a test the first time a class shows behavior worth pinning down. Don't write tests speculatively.

## What this file is not

- Not a tour of the architecture — read the code.
- Not a list of subsystem invariants — those live in code comments where they're enforced.
- Not a commit/PR style guide — the repo is too young for that to be useful yet.

When this file falls out of date with the code, update the file. The code is the source of truth.
