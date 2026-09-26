# Getting Started

New to MudPlay? Here's the short path from launch to playing — and where the rest of this help lives.

## First-time setup tour

On a brand-new install — no BBS, no character, or no game data imported yet — a **guided setup tour** appears in a small **floating card just left of the main window** (it never covers or shrinks the terminal). It walks you through each step click-by-click: it **glows the exact control to use next** — the menu item, the **Add** button, the **host/port** fields, the **OK** button — and as you do each one it **ticks that line (☑)** and moves the glow to the next. When a step's checklist is complete the tour **advances to the next step on its own**. **Back / Next** move between steps manually. Only the steps you're missing show (plus a final **Connect**). The usual order:

1. **Add a BBS** — File → Profile Management → **Add BBS**. That opens Settings; fill in the host/IP + port and click **OK**.
2. **Add a character** — back in Profile Management, add a character under your BBS, name it, and **Save**.
3. **Import game data** — Game Data → **Import .mdb**, then pick your MajorMUD `.mdb` file. That's all it takes — it's what the automation reads from.

Don't want it? Click **Skip — don't show this again** and it won't return on launch. You can replay your own outstanding steps any time from **Help → First-time setup…**, or walk the whole thing as a demo (as if nothing were set up) from the **Program Log → "Run setup tutorial"** button. The rest of this page is the same ground at your own pace.

## What MudPlay is

A Telnet terminal client for **MajorMUD / MegaMUD**-style BBS door games. It renders a faithful CP437/ANSI terminal and layers a large, tunable automation suite on top — auto-combat, healing, spellcasting, navigation and looping, party coordination, and coin/item collection. Play it as a plain terminal, or turn on as much automation as you like.

## Connecting to a BBS

Two ways to connect:

- **Quick Connect** (one-off) — **File → Quick Connect…**, type the board's host (name or IP) and port, and click **Connect**. Nothing is saved; it's the fastest way to try a board.
- **A saved profile** (persistent) — set up a character profile so your login, macros, settings, and the board's details are remembered and reconnect on their own. This is how you'll normally play (see below).

## Profiles

A **profile** is one character's workspace — its BBS login, macros, triggers, equipment sets, favorites, quest state, and every per-character setting. One profile is loaded at a time.

Everything to do with characters and BBSes now lives in one place: **File → Profile Management** (also on the View menu and the toolbar). See the **Profile Management** section just below for the full walkthrough.

- **New profile** starts a blank draft; set up its BBS + credentials (below), then **Save** to name it. New / Open / Save As all live in the **Profile Management** window now (**Ctrl+P** opens it, and it has a toolbar button). A quick **Save profile** (write the loaded character + settings) stayed on the **File** menu — **Ctrl+S**, or the floppy-disk toolbar button.
- **Open (swap to) a profile** loads a saved one — do it from the Profile Management window's character list.
- **Auto-load last profile** (File menu — *Auto-load last profile on startup*) reopens the profile you used last on every launch.
- **Launch straight into a profile** from the command line with `--profile`, so a shortcut or script can open you right where you want. Naming **more than one loads more than one** — each name opens its own window (one instance per profile), which is the easy way to run several characters at once. A name can be **bare** (`Fujin`) when only one of your saved profiles uses it; if the same character name exists on two BBSes, qualify it as **`BBS/Name`** (e.g. `Playpen/Fujin`) since a profile is really the BBS + character pair.

  The app reads the arguments the same way on every OS — only the shell's quoting differs, so quote each entry:

  - **Linux / macOS terminal** (the binary is named `MudPlay`):
    ```
    ./MudPlay --profile "Playpen/Fujin"
    ./MudPlay --profile "Playpen/Fujin,Playpen/Alt,RetroBBS/Bob"
    ```
  - **Windows — Command Prompt (`cmd.exe`):**
    ```
    MudPlay.exe --profile "Playpen/Fujin"
    MudPlay.exe --profile "Playpen/Fujin,Playpen/Alt"
    ```
  - **Windows — PowerShell** (the quotes are **required** here — an unquoted comma is PowerShell's array operator and won't reach the app):
    ```
    .\MudPlay.exe --profile "Playpen/Fujin,Playpen/Alt"
    ```
  - **Works in any shell** — repeat the flag instead of a comma list, which sidesteps every quoting difference:
    ```
    MudPlay --profile "Fujin" --profile "Alt" --profile "Bob"
    ```

  `--profile` overrides *Auto-load last profile* for that launch, and if the profile's own auto-connect is on it connects on its own — so `--profile` gets you all the way in.

  If a name **doesn't resolve** — a typo, or a bare name that lives on more than one BBS — MudPlay shows the reason **right on the terminal** and opens a blank profile; it won't silently load a different character. (For the ambiguous case, re-launch with the `BBS/Name` form it suggests.)

  Running several at once shares one data folder, so use a *different* character per instance; the game data and BBS setup are shared, only the character differs.

Settings live in four tiers — **Defaults → Global → BBS → Character** — so a profile only records what differs from the tier beneath it. (The Settings Menu section notes each setting's tier.)

## Profile Management

Open **Profile Management** from **File**, the **View** menu, or its toolbar button — it's the single home for your characters and your BBSes. If it's already open, re-selecting the menu item (or toolbar button) brings it back to the front (handy when it's hidden behind another window, or another running client). The window has three parts:

- **The current profile** (top strip) shows which character is loaded and gives you **New…**, **Save**, and **Save As…** — the same actions the File menu used to carry. A quick **Save profile** stayed on the File menu as well (**Ctrl+S**, or the floppy-disk toolbar button), and **Ctrl+P** opens this window from anywhere.
- **BBSes** (left) — every saved board. **Add** creates a new one and takes you straight to its settings, since a fresh board has no host yet; **Edit settings…** (or a double-click on the row) reopens those settings for whichever board is selected; **Rename** retitles it (carrying its characters and your saved logins with it); **Remove** deletes it. Removing a BBS deletes **every character saved under it**, so the confirm names how many will go.
- **Characters** (right) — the characters saved under the selected BBS (multi-select). **Add** creates a new one, **Rename** retitles it, **Delete** removes it, **Assign to BBS** moves a character to a different board (pick the destination in the **Move to** dropdown; if that board already has a character by the same name you're asked for a new one), and **Load** brings the selection online. The character you currently have loaded is shown in **bold** and marked *loaded*.
- **Load** is selection-aware, so you can bring a whole stable up at once: tick several characters and hit Load and you end up with one running client per character. The **first** selected character loads into **this** client when it's idle (a straight swap); each of the **rest** opens in its own new client instance. If this client is **actively connected**, it's left alone entirely — *every* selected character opens in a new client, so you never get the jarring disconnect → swap → reconnect just to launch alts. (Launching new clients uses the same multi-instance mechanism as the `--profile` command line.)

Because deleting, renaming, or moving the **loaded** character (or removing the BBS it lives on) would pull the rug out from under your live session, those actions ask you to **disconnect first**. Everything you do to *other* characters works while you're still connected.

BBS **connection details** (host, port, redial, display, realm mechanics, credentials) are edited over in **Settings → BBS + Display**, and **Edit settings…** in Profile Management is the shortcut there — it opens that tab with the selected board already picked, so you don't have to find it in the Settings list.

The division of labour: **Profile Management** is where BBSes are created, renamed, removed, and where characters are assigned to them; **Settings** is where a selected BBS's details are edited. Selecting a BBS in Settings **only** edits that board now — it never moves your character (use **Assign to BBS** for that).

## Setting up a BBS

First **add the board** in **Profile Management** (File → Profile Management → BBSes → Add) — that's where BBSes are created, renamed, and removed now. Add drops you straight into **Settings → BBS + Display** for the new board (you can get back there any time with **Edit settings…**, or by double-clicking the board in the list). Fill in:

- its **host** and **port**;
- your **username / password**;
- and — if the board needs it — the **automated logon** steps that walk you from the BBS menu into the game.

Reconnect behavior and terminal size live here too. These are **BBS-tier**: shared by every character on that board. (The board's **name** is set when you add/rename it in Profile Management.)

Each logon step is a **Message** to wait for and a **Response** to send when it appears (with `{username}` / `{password}` tokens for your saved credentials). Two rules keep the sequence healthy:

- **Add only steps that LOG YOU IN** — never a log-out or quit step (e.g. a *"Are you sure you want to log off? (Y/N)"* confirmation, a common MegaMUD holdover). That prompt never appears on the login path, so a logout step just sits there unmatched and stalls the sequence.
- **You don't need a final "enter the realm" step.** Once your steps reach the game's entry menu, MudPlay sends the entry command for you — and it does so even if your steps don't perfectly reach the end, so an automatic reconnect after a drop still lands you back in the game.
- **You don't need a step for the bulletin pager.** If the board pages news or bulletins with *"(N)onstop, (Q)uit, or (C)ontinue?"* during login, MudPlay presses Enter for you each time it appears and carries on with your steps — useful since that prompt only shows up on days there's something new to read.

(The one time it won't auto-enter is right after you hang up on purpose — a manual `@hangup` or a hang-up-on-low-HP / hang-up-when-naked rule — so you can read the screen and enter manually.)

With that saved, **Connect** (Alt+H, or File → Connect) and MudPlay logs you in.

## Playing, and turning on automation

In the game, the terminal works like any MUD client — type a command and it's sent to the game. The **numpad is pre-wired to compass movement**, and you can add your own **macros** (key → command) and **aliases** (typed shortcuts). The startup splash plays until you connect or load a profile.

The automation engines — Auto-Combat, Auto-Heal, Auto-Nuke, navigation looping, and more — are **toolbar toggles** whose behavior is tuned on the matching Settings tabs. Flip them on to let MudPlay fight, heal, and travel for you. The **Combat**, **Navigation & Looping**, **Party Play**, and **Healing & Spells** sections explain how each engine decides what to do.

---

# The Interface

The **terminal** is the center of MudPlay — everything the game sends, rendered as a CP437/ANSI screen, and where everything you type is sent. Around it, every other panel is a **modeless window**: open it from the **View** menu, a **toolbar** icon, its **hotkey**, or the **terminal's right-click menu**. Pressing that same control again **brings the window to the front** if it's open but buried (behind another window or minimized); for **read-only windows** (Conversation, Log, Backscroll, Party, Buff Watchdog, Spell Book, Monster Intel, Session Stats, and the like) pressing it again while the window is **already focused/frontmost closes it** — so the same key both summons and dismisses. **Edit windows** (Settings, Workshop, Game Data, the macro/trigger editors) only ever raise; you close them with **Save** / **Cancel** / the title-bar **X** so pending edits are never lost. The terminal always stays live while you configure or check anything.

## The terminal and status bar

Type, and your keystrokes go straight to the game. The **numpad** is pre-wired to compass movement out of the box. The terminal has a few input conveniences:

- **Paste** — **Ctrl+V** or **Shift+Insert**. A single line drops onto your input; a multi-line paste is sent as one command per line.
- **Tab-complete** — completes what you're typing against the START of your carried, worn, and key-ring item names (`drop emerald-` + **Tab** → `drop emerald-hilted rapier`; a "bronze emblem" needs `bro`/`bronze`, not `e`). Keep typing to narrow it — `drop padded h` + **Tab** → `drop padded helm`. A stack's count (`2 padded helm`) and a leading "a"/"an" are skipped, so you match on the name; a count you type yourself (`drop 5 padded h`) is left as is. Press it again, or **Shift+Tab**, to step through other matches; toggle it off in **Settings → General**.
- **Right-click menu** — your starred GOTO **Favorites** and **Recent destinations** (the last 10 places you walked — click either to walk there) lead the menu, followed by a set of entries you fully control: by default quick-opens for Backscroll / Player Workshop / Party / Spell Book / Conversation / Navigation / Session Stats, **Reset States** (the recovery escape hatch — see Automation), and **Bug report…**. Rebuild that lower section — add commands, direct links to a Workshop tab or a calculator, your own fly-out folders, and rename anything — under **Settings → Toolbar + Shortcuts** (see *Customizing the terminal right-click menu*).

The status bar along the bottom packs several live readouts:

- The **connection light** — **red** idle · **yellow** connecting · **green** connected (a reconnect countdown shows beside it while reconnecting). It's just the dot; hover it for the state text.
- An **engine-state badge** mirroring the Navigation one — **IDLE / WALKING / LOOPING / AUTO-LAIR** — whose border turns **yellow** then **red** as the engine-recovery gate escalates.
- Your **location** (the map/room key), the session's **exp/hr** rate, and **- TNL:** — the estimated time to next level at that rate, followed by a bracketed **(+N.NN lvls)** ratio. TNL runs as a **countdown**: once estimated it ticks down second by second, and only resets when a fresh estimate differs by more than a little (a much better or worse stretch, a level gained), not on every kill. Under 10 minutes it shows minutes and seconds (`4m 12s`), under a minute just seconds. Session Stats and your own Party-window row read the **same** clock, so all three always agree. Because TNL counts to the next level you can still *earn* (banked-but-untrained levels are already skipped), that bracket says how far past your current trained level your exp already sits — e.g. `(+2.91 lvls)` means you've banked two full levels and you're 91% of the way to a third, so a large TNL time on a lower level reads clearly. It's the same figure a website toplist shows in brackets beside a player's level.
- A **TGT HP:** readout that appears after you `look <monster>` — a coarse wound band × the monster's max HP, so you get an absolute HP range (invaluable on fast-regen bosses). The same estimate is also printed as a yellow line in the terminal. A **Settings → Other → "Show monster HP lookup"** checkbox (default on) toggles both. The max HP is read from the monster record **placed or summoned in your current room**, so a display name shared across zones (an "orc lieutenant" in the barracks vs the slums) resolves to the one you're actually fighting.
- **Tick countdowns** — the combat round tick, the natural HP-regen tick, and the mana / meditate tick.

## The toolbar and menus

A customizable **toolbar** of icon buttons sits under the menu bar. The full bar is **File · View · Action · Game Data · Tools · Help · Bug Report** — **Action** is your in-play on/off surface for the auto-engines and the manual one-shots (see **The auto-engines** and **Manual one-shots and Reset States**), **Game Data** switches imported data sets, and **Bug Report** captures client state to a file on your Desktop. You choose which toolbar buttons appear — and rebind every shortcut — in **Settings → Toolbar + Shortcuts**.

### Customizing the terminal right-click menu

The bottom of **Settings → Toolbar + Shortcuts** also lets you build the **terminal right-click menu** — everything that appears when you right-click the terminal. The whole menu is yours to arrange from a pool of everything addable:

- **Favorites / Recent destinations** — the GOTO walk fly-outs (your starred locations and the last places you walked, click one to walk there). They're at the top by default but you can move, rename, or remove them like anything else. Once placed they always show; on a new profile with an empty list the submenu just reads "(none yet)".
- **Commands** — any individual command from the File / View / Action / Tools menus (window opens, one-shots like Get All / Reset States, utilities like Bug report / Program Log / Wire Inspector). Auto-engine toggles are deliberately left out — those belong on the toolbar / Action menu.
- **Workshop tabs** — a direct link that opens the Player Workshop straight to a chosen tab (Character Info, Equipment Manager, Calculators, Bosses, Roomba, …).
- **Calculators** — a direct link that opens the Workshop on the **Calculators** tab with a chosen calculator (Hit / Movement / Swing / Backstab / Mana Regen / Realm Rankings / Monster Aggro) **expanded and centered** on screen.
- **Settings tabs** — a direct link that opens the Settings window straight to a chosen tab (General, Combat, Health, Party, Statline, Auto-Lair, …) instead of wherever it was last. (The plain **Settings…** command opens the window on its last tab.)
- **Game Data** — a direct link that opens the Game Data Browser on a chosen table (Monsters, Items, Spells, Rooms, Shops, Classes, Races, Messages, Players, Macros, Triggers, Aliases, and the rest). (The plain **Game Data Browser…** entry opens the window on its last tab.)
- **Folders** — click **Add folder** to add your own named submenu that flies out to the side. To fill it: select the folder and add items from the pool (the **Add** button reads **Add into folder** while a folder is selected). To move an item that's *already* in the menu into or out of a folder, just use **Move up / down** — an item stepping toward a folder moves *into* it, and the first/last item in a folder steps *out* of it when you move it up/down past the edge. Reordering a folder moves its contents with it.
- **Separators** to group things.

Select a placed entry (or folder) and type a **Name** to rename it however you like — an entry still links to the same action; leave an entry's name blank to use its default. **Move up / down** to reorder (a folder moves with its contents), **Remove** to drop one (removing a folder removes its contents), **Reset** to restore the built-in menu. Changes save when you click **Apply** (per character). You can also **Import from profile…** to copy another character's menu, or **Import from file… / Export to file…** to share a menu (a small `.json`) with a friend.

## The windows

Each is modeless; pressing its key again brings it to the front if it's already open. Default hotkeys are shown; all are rebindable.

- **Navigation** (Alt+M) — the room map: where you are, your route lines, and the controls for GOTO, loops, and Auto-Lair.
- **Backscroll** (Alt+L) — scroll back through terminal history, with search and export. See **Tools & Diagnostics** for how to use it.
- **Conversation** (Alt+C) — chat, gossip, and telepaths collected in one window with their own input box, per-channel colors, and optional logging. See the **Conversation** section for how to use it.
- **Party** (no default hotkey — View → Party, a toolbar button, or right-click → Open Party) — your live view of the party: each member's rank, health, status, and an uninvite button. See the **Party Play** section for how to use it.
- **Program Log** (F4) — a running diagnostic of what the engines are doing; the first place to look when something automated didn't behave. See **Tools & Diagnostics** for its filters and toggles.
- **Player Workshop** (F1) — your gear sets and the Item Finder, CP allocation and level projection, quest log, boss timers, and death history. See the **Player Workshop** section for how to use it.
- **Game Data Browser** (F3) — the imported game-data tables (rooms, items, monsters, spells) you can browse and override per-character. See the **Game Data** section for how to use it.
- **Buff Watchdog** (View → Buff Watchdog, or a toolbar button — no default hotkey) — the one place you configure every automated buff and watch each one's live recast timer. See the **Buff Watchdog** section for how to use it.
- **Spell Book** (F2), **Monster Intel** (View menu, or the toolbar's *Monster Intel* button — no default hotkey), **Session Stats**, and **Wire Inspector** (F5) round out the set — a read-only spell reference, a monster reference, session counters, and raw wire I/O for troubleshooting. The Spell Book is covered under **Healing & Spells**; Monster Intel under **Game Data**; Session Stats and the Wire Inspector under **Tools & Diagnostics**.

The **Settings** window follows the same modeless rule — the terminal stays interactive while it's open — and **OK / Apply / Cancel** decide whether your edits stick.

**Snapping windows together.** As you drag the panel windows — Conversation, Party, Buff Watchdog, Player Workshop, Navigation, Spell Book, and Session Stats — they **snap flush to each other's edges** when you bring one within about a finger's width of another, so you can build a tidy layout without lining anything up by hand.

Dragging the **main window** then carries the whole snapped cluster with it, keeping your arrangement intact; grab any of the other panels and it **pulls off freely**. Turn this off with **Settings → General → "Snap windows together"** if you'd rather every window float on its own. (Windows opened from *inside* a panel — editors and dialogs — don't snap.)

If a panel ever drifts off-screen or the layout gets untidy, **View → Reset layout** returns every window to its default position and size.

## Keeping MudPlay up to date

MudPlay can update itself from its GitHub releases — but only when you ask it to. Nothing is ever downloaded or installed in the background.

- **When it checks.** On launch, MudPlay quietly asks GitHub whether a newer build has been published for your platform — and then again at **9am and 9pm**, so a client you leave running for days doesn't keep reporting whatever was true when you started it. Both are governed by **Settings → General → "Check for updates automatically"** (on by default). With it off, nothing checks on its own and you can still check by hand any time. Opening the update window also checks if nothing has been checked yet this session.
- **How you're told.** Two notices, and that's the whole effect — nothing downloads or installs on its own. A red **UPDATE AVAILABLE!!!** banner flanks the title on the startup splash screen, and for as long as the update is waiting the same message **scrolls across the window's title bar**, highway-sign style. The character and BBS stay pinned on the end of the title while it scrolls, so running several clients at once you can still tell them apart in the taskbar. The crawl stops on its own once you've updated.
- **Update the Client.** **Help → Update the Client** (also on the **Tools** menu) opens a small window that tells you whether you're up to date or what the latest version is, shows the download size and the new version's changelog, and offers a **View release notes** link. If a newer build exists, click **Check again** to re-query, or **Download & Restart** to install it.
- **What "Download & Restart" does.** MudPlay downloads the release archive for your exact platform, **verifies its SHA-256 checksum** against the release's published sums (it refuses to install anything that doesn't match), unpacks it, then closes, swaps the new build into place — **replacing whatever folder you launched from, however custom** (keeping a one-time backup so a botched swap rolls back) — and relaunches into the new version. When the swap succeeds it cleans up after itself: the download, the backup, and the helper script are all removed. Your profiles, settings, and data folder are untouched — only the program files are replaced.
- **Updating while you're connected.** MudPlay finishes the download and verifies it *before* touching your session — if anything fails, you're still connected and nothing has changed. Once the new build is staged it closes the connection cleanly, saves your profile, and exits. The restart puts you back where you were: the **same character reopens**, and if you were connected when you started the update, it **reconnects on its own** (and logs you in, if your credentials are stored) — regardless of whether *Auto-connect when profile loads* is switched on. Note that the client can only close the socket, not log your character out of the game — so as with any disconnect, you'll be briefly link-dead until the relaunched client dials back in. Don't run an update mid-fight.
- **When it's unavailable.** Self-update only works for a real installed build. If you're running from source (`dotnet run`) or a build tree, the window will say so and point you to the release page to download by hand — the check and notice still work, only the one-click install is disabled.

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

**Taking a round yourself.** If you hand-type an attack mid-fight — a **combat spell** (any spell that costs round energy, as opposed to a 0-energy heal/buff) or a **physical attack** (`a`/`at`/`att`/`aa`, `bash`/`sm`/`sma`/`smash`, `bs`) — the engine treats it as a **user override** and holds its own auto-attack for that round, so it won't fight you by re-sending its action on top of yours. Control returns automatically on the next combat round.

A hand-cast **heal/buff/cure** (0 energy) is *not* an override — after it lands the engine resumes attacking right away, same as before.

## Fighting back (self-defense)

MudPlay normally leaves **Friend** and passive **Neutral** monsters alone. But if one turns hostile and starts swinging at you — you provoked it, or it just attacks — the engine **fights back**: any monster actively attacking you is engaged and finished, even one it would otherwise walk past. This is always on whenever **Auto-Combat** is on.

Exemptions: monsters whose Game Data relationship is **Flee** or **Hangup** (you run from / hang up on those instead of standing to fight), and any room you've marked **"do not attack"** (self-defense honours that too). Everything else — Friend, Enemy, Neutral — you defend against.

Self-defense is also suppressed while you're on a **walk-to** — an evil character crossing a guarded town, say, keeps running to their destination rather than stopping to fight the guards (a losing trade at low levels). It stays active when you're **idle, looping, or Auto-Lairing** (all farming/holding, where fighting back is what you want).

## Targeting

When several hostiles share a room, **Target order** and **Target priority** decide who gets hit first — the highest-priority monster by default, or a "follow the party's target" mode. Per-monster priority is ranked in Game Data.

## Fighting a crowd

Against several enemies MudPlay uses your **multi-attack** and **area-debuff** spell slots to hit the whole room, falling back to single-target attacks once the room thins below a slot's minimum-enemies setting. A room spell keeps hitting everything in the room from round to round, through kills and new arrivals, so it isn't recast while its conditions hold — until something breaks it: a spell cast between rounds (a mid-fight heal or buff) turns combat off, and the room spell is then re-cast. Those room spells are gated by **Auto-Nuke**; single-target attack spells aren't "nukes" and stay available regardless.

## Backing off

If your health drops past the thresholds on **Settings → Health**, the engine can **run** instead of fighting to the death — breaking combat first (if set), then moving a configured distance in a chosen direction. Healing and fleeing are covered under **Healing & Spells**.

---

# Navigation & Looping

MudPlay walks you around the world — one-off trips, repeating circuits, and lair camping — all from the **Navigation window** (**Alt+M**, or View → Navigation), driven off the imported room map.

## The Navigation window

Three areas:

- A **top status bar** — an engine badge (**IDLE / WALKING / LOOPING / AUTO-LAIR**), a plain-English status line, the **Go to…** button, and a **search box**. Detailed just below.
- The **map** on the left.
- A **right rail** of collapsible panels: **ROOM INFO** (records for the last-clicked room — see below), **CURRENT NAV** (the live step list), **GOTO** (your favourites), **LOOPS + AUTO-LAIRS** (your saved circuits), and **EXP/HR ESTIMATOR** — with a **Navigation Management** button at the bottom for full editing.

### The status line

The status line spells out **what the engine is doing and, when it's held, why** — e.g. *"Looping Ring - step 4 of 12 on lap 3 — resting (low HP)"* or *"Walking to (12/431) Tower — party asked to wait"*. A small colour-coded chip beside it shows the state — Moving / Fighting / Waiting / Paused — so Fighting and Paused aren't repeated on the line.

A route that's **queued but not moving** says so and names the hold (a common one is **auto-engines off (Auto-All)** — the kill switch is off, so nothing walks until you turn it back on).

When a walk / loop / Auto-Lair **can't continue**, the reason is named rather than a bare "lost": a blocked loop shows the offending door / winch / hidden exit and room, an Auto-Lair whose approach keeps failing shows *"retrying: …"*, and the **Lost — couldn't recover** dialog names the last room the engine was sure of so you have a concrete place to right-click **"I am here"**.

On **Paradigm**, that dialog should almost never appear: whenever the tracker drifts, the client asks the game `rm` for your authoritative room and re-anchors from the answer — before it ever falls back to the blind reverse-walk recovery, and again as a last resort before giving up — so it only truly gives up when `rm` itself can't answer (and a `rm` that a confusion fumble eats is simply re-asked until the confusion passes).

Even with **no automation running** — you've stopped the engines and are walking a block of identically-named rooms (a grid of same-name "Soldier's Quarters" cells, where no single room display can tell one cell from the next) by hand or being dragged by a party leader — the client keeps trying to place you: it narrows down which look-alike room you're in from the *sequence* of moves you make and the rooms they reveal, and silently re-anchors the moment that sequence fits exactly one room.

It sends nothing to the game to do this — it's pure inference from what you're already doing — and if the walk stays genuinely ambiguous it just stays Lost rather than guess.

The status line is also **colour-coded**: **amber** while movement is held for a reason (resting, held, confused, party wait, Auto-All off…) and **red** when a nav action fails or the tracker loses your position — so a problem is glaring rather than buried in grey.

A row of action chips — **Save**, **Run**, **Loop mode**, **Lair mode** — sits just above the map. While you're in Loop mode a **Clear all** chip appears to the left of **Save**; it wipes every step from the loop you're building so you can start fresh.

Any label too long for a narrow rail is trimmed with an ellipsis — **hover it to read the full text**. This covers the status line, the GOTO / loop / lair / favourite rows, the live CURRENT NAV step list, search results, folder names, and the EXP/HR estimator rows.

## Walking somewhere (GOTO)

To send your character to a room:

- **Search** — type a room name or a map/room key (e.g. `1/297`) in the top search box, pick the match, then click the green **Run** chip.
- **Right-click a room** on the map → **Walk here**.
- **Favourites** — save rooms you visit often (right-click a room → **Add to favorites**, or the Management dialog's **Go To** tab), then click one in the **GOTO** rail to walk there. In the Management dialog's **Go To** tab each saved room is listed as its label followed by its **(map/room)** number, so identically-named rooms are easy to tell apart. **Right-click a Go To** in the rail for **Walk here**, **Edit…**, **Move to folder…**, an **Add to / Remove from favourites** toggle (stars it — the ★ that promotes it to the terminal's right-click **Favorites** flyout — without deleting it), and **Delete this Go To** (removes the saved location entirely).
Type **"favourite"** (or any 3+ character part of the word) into the GOTO or loop filter box to surface your starred Go Tos and favourited loops.

MudPlay plots the shortest route and walks it, opening doors, disarming traps, and revealing hidden exits along the way. Click the red **Stop** chip to stop, or the **Pause / Resume** chip to hold and continue.

If you **type a movement command yourself** while a walk, loop, or auto-lair is running — a direction (`n`, `sw`, …) or a text-exit step (`go path`) — navigation **pauses automatically** so the automation never fights your hand-driven step. It's a user pause, just like clicking **Pause**: press **Start** (Alt+V) when you're ready to hand control back. (Peeking with `l <dir>` doesn't count — that's a look, not a move.)

## Building and running a loop

A **loop** is a saved circuit of rooms MudPlay walks over and over, fighting and looting as it goes. To build one the quick way:

1. Click the **Loop mode** chip (it changes to **Building**). If you've run a loop this session, it's **pre-loaded** into the builder — with **all its settings intact** (per-room command / delay / no-rest / no-attack **and** the loop-wide Only-attack-in-lair flag), so you can **Run** it straight away or re-**Save** it — handy when a stop / `@stop` dropped you off one, or you ran an ad-hoc loop you never saved. Hit **Clear all** to wipe it and build a fresh one instead. (Turn the pre-load off under **Settings → General → "Load last ran loop"** if you'd rather always start empty.)
2. **Left-click the rooms on the map, in order** — each becomes a waypoint. Reorder or remove them in the **CURRENT NAV** rail.
3. Click **Run** to save and start it (you'll name it), or **Save** to keep it without running.

You can start building a loop **while a walk-to is running** — building only collects rooms, it never moves you, so your walk continues uninterrupted. Clicking **Run** in the Navigation menu then hands movement over: it stops the walk and starts the loop. The **toolbar** Start / Stop / Pause buttons still control the *walk* itself, so reach for those to stop (or pause) the walk without starting the loop.

To put yourself (or a party member) back on the **last loop run this session** without reopening the builder, use the **`@loop last`** remote command — it re-runs it even if it was an ad-hoc loop that was never saved. This works regardless of the "Load last ran loop" setting above.

Or build it off the map: **Navigation Management → New Loop** opens an editor where you add rooms by name or key, name and annotate the loop, and set per-waypoint options. While that editor is open, you can also **left-click rooms on the Navigation map** to append them to the waypoint list — the same way the on-map builder works, without typing keys.

**Run a saved loop** from the **LOOPS + AUTO-LAIRS** rail (or the Management dialog) — each has **Load** (stage it) and **Run** (start now). Queue one and, if you aren't already there, MudPlay walks you to the loop's start, then begins the circuit; combat, healing, and pickup keep running throughout. While it runs the badge reads **LOOPING** with "step X of Y on lap Z", and the **CURRENT NAV** rail shows the loop's rooms in **green** — click any room to tune it live without stopping (see *Live-editing a running loop* below). **Stop** ends the loop.

**Right-click a loop or Auto-Lair setup** in the rail for **Load**, **Run**, **Edit…** (opens its editor), **Move to folder…**, and **Add / Remove from favourites** — favouriting a loop or lair adds it to *both* right-click Favorites flyouts (the terminal's and the map's, green for loops, amber for lairs) alongside your starred GOTO rooms, so you can start it from anywhere.

Each waypoint can carry its own per-room settings, edited **inline in the Edit Loop table**:

- a **command** + **delay** (e.g. `rest`, `dep 100`, `ask barmaid pie`);
- a **"No rest"** flag;
- a **"No atk"** flag.

Chain several commands in one waypoint with `;` or `^M` — each is sent as its own line (e.g. `get all;drop coins`), the same convention macros and the pre-/post-rest commands use. (You can set the same options by clicking a waypoint row in the CURRENT NAV strip — both while *building* a loop and while one is *running*; see *Live-editing a running loop* below.) If a route crosses a locked gate or a hazard room, a **Choose a route** prompt lets you take the free way around or push through.

- **No rest** — the loop won't rest in this room even when HP/MA drop below your "rest if below" gates; it advances instead. Only this exact room is protected.
- **No atk (do not attack here)** — the loop skips combat in this room *as if auto-combat were off*, walking on even when the Min/Max monster count is met. The one exception: if a **rest** is triggered here (HP or MA below its gate), it still clears the room so the rest can proceed. Only this exact room is affected.

### Live-editing a running loop

While a loop is **running**, the CURRENT NAV rail shows its rooms as **green** rows — the running counterpart of the builder's red list. **Click any room** to change its **command**, **delay**, **No rest**, or **No atk** right there, and use **⚙ Entire Loop Settings** to toggle **Only attack in lair rooms** — all applied **live**, with no stop/restart. The room the loop is currently in is **highlighted**, and the map draws matching **numbered green bubbles** on each waypoint (the running twin of the builder's red pins) so you can tell which rail row is which room. Flag changes take effect on the loop's next decision and a delay change on that step's next run; **adding or removing a command** re-plans the circuit on the **next lap**. You **can't add, remove, or reorder rooms** while running — for that, **Pause** and reopen the builder (the red list), then resume.

### Entire Loop Settings

Some settings apply to the **whole loop**, not one room. Reach them from **⚙ Entire Loop Settings** — it appears at the top of the CURRENT NAV area **while you're building a loop** and again at the top of the rail **while a loop is running** (click it for a flyout), and it's mirrored by a checkbox next to **Set as favorite** in the Edit Loop window.

- **Only attack in lair rooms** — the loop only engages hostiles in **lair rooms** (rooms the game data tags as monster lairs); every other room is walked through as if auto-combat were off. The inverse of per-room "no atk": instead of opting rooms out one by one, you opt the whole loop *in* to lair rooms only. A per-room **"do not attack"** still skips even a lair room (it wins), and a triggered rest still clears any suppressed room so it can rest. Note the suppressed rooms include the walked-through connectors between waypoints, not just the marked waypoints.

## Estimating a loop's exp/hour

The **EXP/HR ESTIMATOR** panel in the right rail projects how much experience a prospective circuit would earn per hour *before* you commit to it — factoring in boss respawn timers and room summon rates, not just a flat monster count. It simulates the loop's *actual room order* against each lair's per-mob respawn timer, so the **shape** of the circuit matters: an out-and-back line that re-crosses just-cleared lairs on the way back reads lower than the same rooms walked as a ring, because those return steps waste combat time — exactly how it plays out in game.

The estimate's assumptions live in the **⚙ Estimate Settings** flyout at the top of the panel — the **I'm Rooming** toggle (on = you hit the whole room at once; off = one mob at a time), **Rounds to kill a mob**, and the two biggest levers below. Two tunables drive the estimate most:

- **Seconds per room** is the single biggest lever, and the easiest to set wrong: it's your **effective time per room while looping and fighting**, not your raw walk speed. Each room is a move command plus its server round-trip plus an attack plus the 5-second combat tick, so the real pace is ~**1.2–1.4s** even when your bare movespeed is 1.0 (which is why it defaults to 1.4). Set it to your raw movespeed and a tight backtracking loop reads noticeably high, because the model then under-charges the wasted ticks in those empty return rooms.
- **Real-world multiplier** (0–1) scales the result down for the friction the room-by-room model can't simulate — the odd late combat round, a missed pull, imperfect pacing. **0.9–0.95** is the usual band for clean, attentive play, lower for a distracted session. It's a discount from the modeled ceiling, not a fudge factor — set it to the fraction of the ideal pace you actually sustain.

Click **Start estimating**, then **click the rooms** on the map to sketch the circuit; the panel shows a running **exp/hr** figure as you add rooms. **Save as loop** turns the sketch into a real loop, **Load loop…** pulls an existing loop in to evaluate it, **Clear rooms** starts over, and **Stop Estimating** exits the mode.

A small **Realm:** line under the headline notes which game-data realm is active — it affects **only** how often a room's **summon spell** re-rolls (Paradigm re-rolls every combat round plus on entry; Stock on a slower 6-second medium tick plus on room change), never your kill rate. A summon spell with a `nomonsters:` gate only fires while the room is empty, so it contributes a roll on a clear pass-through but nothing when you arrive to a full lair. Use it to compare two hunting circuits without walking either one.

## Auto-Lair

**Auto-Lair** camps a monster's lair: travel there, wait out the respawn timer, enter to kill the spawn, then repeat. Mark lairs with the **Lair mode** chip (left-click the lair rooms, then **Save**), or build a setup in **Navigation Management → New Lair** (where you can override each lair's respawn timer). Start one from the **LOOPS + AUTO-LAIRS** rail's **Run** button — it cycles the marked lairs. Its routing heuristic and travel-cost model live in **Settings → Auto-Lair**.

**How long it stays in a lair.** It leaves for the next one as soon as the fight is over *and* the drops are picked up — it won't walk off and abandon loot it just fought for.

"Fight over" means the room re-displays with no monster you'd engage left in it, which is the reliable signal; the game's own `*Combat Off*` line isn't usable on its own, since it also fires every time you cast and once per strike for thrown weapons and the like. **Engage timeout** (Settings → Auto-Lair, default 30s) is only the upper bound, for a fight that never resolves — something you can't kill, or one that ran away.

**A lair that hasn't respawned costs a few seconds, not the full timeout.** If nothing turns up within a moment of stepping in, Auto-Lair takes that as "not back yet" and moves on to the next lair rather than standing in an empty room — entering already burned that lair's timer, so there's nothing to be gained by waiting there. If it still looks like it's idling, the usual reason is that the monsters you're after don't actually *spawn* in that room: some wander in from elsewhere on their own schedule, and a room they merely pass through isn't a lair Auto-Lair can time.

## The map and obstacles

**Right-click any room** for its menu: **Favorites** and **Recent destinations** sub-lists at the top (the Favorites list holds your starred GOTO rooms *and* your favourited loops + auto-lairs — click a room to walk there, a loop or lair to start it — and Recent destinations walks to a recent GOTO target), then **Walk here**, **I am here** (re-anchor if the map loses track of you), **Save as Go To** (saves the room to your Go To list), **Use Teleport**, **Center on…**, and toggles to mark a room **Avoid** or **Stash**.

**Shift+right-click** skips the menu when a room's only jump is unambiguous — a room with just an up exit, just a down exit, or a single teleport destination immediately follows it (recentres the map there) instead of opening the menu.

**Left-click any room** to load it into the **ROOM INFO** rail panel. A room also lands here when you click a monster's **lair / placed / summoned** room chip in its Game Data record, or double-click a row on the **Rooms** browser tab — in those cases the map opens (if it was closed), centres on the room, selects it, and expands ROOM INFO. A plain map left-click never forces the panel open; it just refreshes its contents to the room you clicked, so expand ROOM INFO whenever you like and it shows the last room clicked.

The panel lists clickable links to everything attached to the room:

- **Room name** — click to open the room's record (or, for a shop room, its shop stock popup), with the map/room number and illumination beneath it.
- **Illumination** — **`Room Illu:`** shows the room's own light. If you carry any light — worn +illu gear, a readied light, or a light spell in the Buff Watchdog — a **`Your Illu:`** line appears with your effective value, and the visibility phrase moves onto it. The phrase reads the room's state — *pitch black*, *very dark*, *barely visible*, *dimly lit* — or **"You can see."** once fully lit.
- **Monsters** — grouped (like the map tooltip) into **Placed** (a boss / NPC fixture), **Assigned** (roams there / rarely spawns), and **Lair** (consistent lair spawners, with the lair's **Max Regen** beneath). A monster can appear in more than one group.
- **Obvious exits** — click one to re-root the map on that neighbour.
- **Floor items** — everything the room drops on the ground (static placements plus anything its `roomitem` command scatters).
- **Shop and room spell** — when the room hosts a shop, and its cast-on-enter room spell.
- **NPC transports** — teleports a monster standing in the room offers when you ask it a keyword, e.g. `ask Seher'Sahham activate → Damp Cavern, Wellspring (16/637) — 1 runic`. These live on the monster, not the room, so they're listed apart from Room commands. Click one to re-root the map on the destination. The walker routes through a paid transport only when **everyone** can pay: each person who asks is charged the fare, so in a party it checks the poorest member's cash (the same `@wealth` check a toll uses) and walks around it otherwise.

**Room commands** lists what you can type in that room and what it does — `touch statue / move statue — summons obsidian statue`, `give crane totem — teaches form of the crane`, `pull lever — drops frozen hydra in the room`, `hand over totem — takes crane totem`, `break apparatus — grants an ability` — with the cost appended when the command charges (`summon healer — summons healer — costs 100 Gold`). Synonyms that do the same thing share one row. It carries the same lines as the map tooltip's Room commands — teleports and sailings included (a captain's `secure passage` lists each port it sails to); click a teleport or sailing line to re-root the map on its destination. Long rows wrap to the panel width.

**Everything is clickable.** A **monster** or the **room spell** opens its full record in a dialog; the **shop** (and a shop room's name) opens the shop stock popup with buy/sell prices and the live Charm picker; a **room-command** row opens the record its effect names (the monster it summons, the spell it teaches, the item it drops or takes); and floor-item links open that record in the **Game Data Browser**. Either way it's a quick jump from "what's in this room" to the full record without hunting through the browser's tables.

**The illumination scale.** A room's illumination is a signed number — **0 is fully lit**, and the more negative it gets the darker the room. `Your Illu` folds your carried light (worn +illu gear, a readied light, and any configured light spells) into the room's own value, so it's the figure that decides what *you* actually see. Where a value lands, and the phrase it shows:

- **0 or higher** — *You can see.*
- **-1 to -100** — *The room is dimly lit*
- **-101 to -150** — *The room is barely visible*
- **-151 to -200** — *The room is very dark — you can't see anything*
- **-201 or lower** — *The room is pitch black*

**-150 is the cut-off**: at -150 or above you can make out a room's contents; below it (very dark / pitch black) the game hides them, so you need enough carried light to lift `Your Illu` to -150 or better.

**`@where` on the map.** When you `@where` another MudPlay user and their client answers with its location (a telepath like `Fujin telepaths: {Adventurer's Guild, Universal Trainer (map 1, room 1376); exit s: west}`), the map — if it's open — **flashes that room green and centres on it** for about 15 seconds, then drifts back to following you.

`@where` several people and **each answered square lights up at once**, fading out on its own 15-second timer; the map re-centres on the **newest** reply as it lands, leaving the earlier flashes where they are. It only reacts while the Navigation window is open; a reply that lands with the map closed is ignored.

The **Overlays ▾** button layers lairs, shops, spell rooms, and **level gates** onto the map and toggles the **Legend** — which you can **drag anywhere on the map** (it remembers where you put it; toggle it off and back on and it snaps back into view if the window has since shrunk).

The **Legend** keys every room-cell marker the map draws: the amber-ringed **current room**, the blue-ringed **walk-to destination**, room fills (lair, shop/bank, spell, auto-lair, up/down/up+down exit rooms), and the overlay glyphs — **deathpile** skull, **boss** crown (with a red halt ring when it's a *stop-before* boss), **trainer** chevrons, **gang-house** robot, **avoid** (red X), **stash** (gold X), the amber **level-gate** wedge, and the fading green **@where** result.

**Route lines** are colour-coded — walk-to **blue**, a running loop **green**, a loop you're previewing **red**, an Auto-Lair approach **orange** (these four are recolourable under Settings → General, so they're described here rather than pinned in the Legend).

**Exit stubs** carry their own colours (shown in the Legend):

- **red** — a trapped exit;
- **magenta** "Action required" — an exit you can't just walk, one that needs a command or in-room action to cross (a `go path`-style named exit, a lever, or an ask-a-guard door);
- **cyan** — a hidden exit revealed with `sea`.

Traps are **directional**, so a connecting line is only red on the trapped side: a line red for its **whole** length is trapped **both** ways, while one red for **half** its length (the half against the room whose exit is trapped) is a **one-way** trap — safe to walk back the other direction.

**Level gates** (on by default) marks every room that **holds a level gate** with a small **amber wedge in the top-left corner** — a room you can walk into and stand in, whose way onward is shut unless you're inside the gate's level window. It covers gated exits, **level-gated room teleports** (a vortex that won't take you until level 20), and **level-restricted boat sailings** (a captain who won't board you until level 50). That's deliberately a different mark from the **red exit stubs**, which mean a trap: a level gate is a locked door, not a hazard.

It describes the **map**, not your character — it marks where the gates *are*, not which ones happen to refuse you today. So it reads the same at level 5, at level 99, and while you're browsing game data with nothing connected, which is when "where are the gates?" is most often the question. Hover a marked room to see the gate's actual level window in the tooltip. Your choice is saved per character.

Hovering a room shows its details in a tooltip:

- **Monsters** — split into **Placed** (a boss / NPC fixture), **Assigned** (roams / rarely spawns there), and **Lair** (a consistent lair spawner), each with its game-data record number (e.g. `Dark Goblin Archer(#48)`). The lair's **Max Regen** sits directly beneath the Lair line.
- **Floor items, shop / room spell, exits, and lighting** — everything else attached to the room.
- **NPC transports** — the ask-a-keyword teleports of a monster placed there, with destination and any price.
- **Room commands** — anything you can type there: teleports and paid services, and the commands that act on the room itself — what they **summon**, the spell they **teach**, the ability they **grant**, and the item they **drop** or **take**.

(A locked door whose key id doesn't match any item in the set — a game-data typo, e.g. 8/462's north gate recording `Key: 1` — is shown as the plain door it behaves like, listing the picklocks/strength that actually opens it, rather than naming a key that doesn't exist.)

**Getting past obstacles.** En route, MudPlay clears most of what stands between you and a destination, stopping only when it hits something it genuinely can't solve:

- **Doors** — closed or locked, handled by key, pick, or bash.
- **Traps** — searched and disarmed, or delegated to a capable party member.
- **Hidden exits** — searched out and revealed.
- **NPC ask-transport** — a sealed room whose only way out is asking a resident NPC to port you elsewhere (the Floating Citadel's Grey Lord ports you to Town Square). It sends the `ask <npc> <keyword>` for you, so those pockets aren't dead-ends. A **class-restricted** one (the barmaid's bard-only jump) is offered only to the right class; everyone else is routed around it. And because some are a **skill roll** that can quietly fail, the walker confirms it actually arrived and **re-asks until it does**.
- **Action-gated exits** — a lever or switch in *another* room (the magenta "Action required" stubs). It drives a go-pull-return detour, visiting each lever room on the way past then crossing the primed exit — even when a lever alcove is itself behind another action-gated door (it opens each inner door first). Only a very deep (4+ levels) or self-referential puzzle is left unsolved: those fail cleanly at plan time (*"route needs an action-gated exit the walker can't auto-solve"*) and log the exit that stopped it.
- **Room-command reveals** — a hidden passage opened by typing a command *in the room itself* (e.g. `clear rubble`), sent before stepping through.
- **Item-use teleports** — where *using* an item transports you across (e.g. `use potion of levitation`); it uses the item for you.
- **Winch gates** — a fortress-style gate opened by pulling a winch: it pulls (re-pulling if it "does not budge") and waits for the gate to turn fully open before stepping through, so it never walks into a still-closed gate.

When a route is blocked *only* because you lack a required item for one of these gates — often a quest item that can't be auto-fetched — the walk fails with a message that **names the item to go obtain**, rather than a bare "no path". A **hidden exit whose opener needs a held item** counts the same way: the picker names that item up front (the Lower Caverns bloodstone orb, the fortress amber talisman), and if the item is flagged **Auto-obtain for path** the run fetches it before setting out.

The picker also distinguishes a **genuinely-required** gate (no way to the destination without it) from one that merely unlocks an **optional shortcut** (a longer route reaches the destination anyway). It commits the reliable route, lists only the required items — noting any you **already carry** ("— you have it") — and surfaces the shortcut separately with the rooms it would save, **without** ever fetching the shortcut item for you.

That shortcut is its own **selectable card**: pick it and — if you're not carrying the item — the walker heads to the item's **source** (a dropping monster's lair, a shop, a giver), tries to get it, then takes the shortcut if it turned up or falls back to the long reliable route if the source was dead or empty. So a shortcut whose item comes off a monster that may be gone is one click, not a manual side-trip.

**Door keys are a special case.** Normally a locked door's key is *not* something MudPlay goes looking for — pick and bash are the usual openers, so a key you simply don't have makes the exit fail in place. The exception is a key whose whole acquisition chain is certain: a **room command that summons a monster which drops it every time**. For those, the run detours to the summoning room, types the command, lets the fight resolve, re-surveys the floor, collects the key, and carries on to where you were going — no prompt, because nothing about it is a gamble.

- **The gate key behind the Black Steel Gate** works this way — `touch statue` summons the obsidian statue, which always drops it.
- **A low-percentage lair drop** — the black star key, for instance — is deliberately left alone: there's no way to promise it, so the walk won't commit you to an open-ended hunt for one.
- **A key you can pick or bash** is left alone too: the detour only arms when the door is genuinely shut to you.

A path item — key or otherwise — is fetched for you when you **consent to it for that walk**, which is what **accepting a gated route in the route picker** does: the pick itself arms the shop / give / drop acquisition for that one trip (an item flagged **Auto-obtain for path** in Game Data → Items is also fetched automatically on a sole route). While that fetch detour is running the map route line and **Details…** show the **whole journey** — to the shop / giver / summon room, then on to where you actually asked to go — rather than stopping at the fetch stop.

The *searching* half — hunting a missing item off the floor room-by-room — is driven by the **master Auto-Search toggle**: the picker's **Search en route** card turns it on for the leg so the search actually runs, and turns it back off once the item lands. (There's no longer a separate "search rooms if item needed" setting — Auto-Search is the single switch, and the routing cards carry the per-walk consent.)

Routing also respects **alignment-gated entrances** — the good / evil entrances marked `(Alignment: X to Y)`, which the game refuses to anyone whose alignment falls outside the band. It's **whole-party**: if any member's alignment excludes them from an entrance, the party is routed **around** it (the game would stop the party at that member). When a member's alignment isn't known yet (nobody's done a `who`), the router doesn't guess — it walks **up to** the gate and **stops** there, so you can decide, rather than detouring blindly or bonking through.

**The alignment scale.** Your `who`-title maps to a hidden alignment number the game keys these gates on, running most-good (negative) through most-evil (positive):

- **Saint** — -201
- **Good** — -100 (**Lawful** is not a separate rung — it's a "never do evil" flag on a Good character, so it counts as Good)
- **Neutral** — 0
- **Seedy** — 40
- **Outlaw** — 80
- **Criminal** — 120
- **Villain** — 180
- **Fiend** — 300

An entrance marked `(Alignment: X to Y)` admits you only when your value falls inclusively between the two named titles — so `(Alignment: Saint to Neutral)` (-201 to 0) lets Saint / Good / Neutral through but turns away Seedy and worse. The ladder is the same on stock and Paradigm (Paradigm just also shows the exact number).

A genuinely impassable obstacle halts the walk with a clear reason rather than looping on a door it can't open — and the reason **names the obstacle**: which room the door is in, the direction, and what it takes to pass (the key and/or the picklocks/strength), e.g. *"a locked door south from 10/218 (Frozen Cavern) — needs the glass key, or 61 picklocks/strength."* Door requirements are read per-direction, so a door's far side (which can differ — one way an "any" bash/pick door, the other a keyed one) is never mistaken for the way you're heading.

When the only route somewhere is fully blocked but you can still reach the obstacle, the route picker offers **"run to the blocked room anyway"** — it walks you as far as you can go and stops at the block, so you can clear it (open the door, fetch the key) by hand. Every blocked walk-to is also written to the program log.

**Crossing a hazard on a walk-to.** When the only route to your destination crosses a room-entry **hazard** you have no counter for — a river you'd cross by raft, lava, the desert heat — the route picker surfaces the choice instead of silently failing:

- **"Obtain, then cross"** — offered when the client can source the counter nearby; it fetches the raft / feather / waterskin, then crosses safely. **Any counter the hazard accepts counts:** the card names the one it would fetch (usually the cheapest) and lists the alternatives, so a river it would buy a log raft for is just as crossable with a wooden skiff, silverbark canoe or river punt already in your pack — and one picked up or handed to you on the way cancels the purchase. Where the game accepts fewer (Crystal Lake takes only a raft or skiff), only those count.
- **"Cross unprotected — take the damage"** — always offered for a **survivable-damage** hazard (a river, heat): walk straight through and eat the hit, on your say-so. A hazard that would *kill or displace* you (a drowning / freezing death, a forced teleport) never offers this — a counter is the only safe way past, so the picker only offers to obtain it or stops you at the edge.

Either way the previewed route now **draws its line on the map** even though you can't currently pass it, so you can see where it goes before you commit.

When a route crosses a survivable hazard **and** a hard gate past it — a keyed door you don't have the key for, like the walk to the Iceforge (across the Silver River, then through a locked door) — the picker offers the same hazard choices, but each one **stops at the hard gate** you must clear by hand:

- **"Obtain, then cross"** — fetches the counter and crosses, then halts at the door.
- **"Cross unprotected"** — takes the river damage and pushes on to the door.
- **"Walk to the hazard and stop"** — offered when no counter can be sourced nearby; walks you only to the room just short of the river, so you can fetch a raft (or clear the gate) from there rather than crossing blindly.

The requirement line names everything you'll need — and for a counter it can source, the **specific** item it'll fetch and where, e.g. *"Requires log raft (buy at Pier); the dragon key"* (picking the cheapest when several rafts are buyable) — so you know exactly what to gather before setting out.

**Avoiding traps on a walk-to.** If the shortest route to your destination crosses a trap and a route that crosses **fewer** traps exists, the route picker surfaces the choice:

- **Fewest-traps route** (pre-selected) — avoids every trap it *can* and crosses only the **unavoidable** ones (so a path with one dodgeable trap and one you can't get around routes past the dodgeable one and accepts the other). It's the default because a step-time disarm can fail (no lockpicks, no capable party member) and spring the trap.
- **Shortest route** — one click away when you'd rather take it; it disarms en route.

Both cards show their trap count. Click either route to preview its line on the map, then **Go**. When no route crosses fewer traps than the shortest, there's nothing to weigh, so the walk just proceeds and disarms en route as before.

When a route (the one you're walking, or a queued preview) crosses a trap, its **Details…** view flags that step in **red** with the trap's damage related to your HP — e.g. *trap: 36 dmg (~11% of HP)* — so you can see the hit each trapped step on the path would land.

**Walk it or teleport.** When the shortest route somewhere takes a **teleport** (a cast, an item-use portal, a CMD jump) and a plain **walking** route also exists, the picker asks which you want — **"Walk it"** (the safe overland route) or **"Teleport"** (the shortcut). A teleport can drop you somewhere lethal, so the client won't make that call for you. When the shortcut goes through a paid NPC transport, the card states its fare ("Costs 1 runic per person").

And once you're walking, a walk that **didn't** start on a teleport won't quietly switch to one: if the route has to re-plan mid-trip — say a counter you were searching for turns up and the destination is recomputed — it **keeps to the walking route** and only falls back to a teleport if walking has become genuinely impossible. So picking "Walk it" (or any ordinary walk-to) means you stay on foot the whole way, never surprised onto a vortex you didn't choose.

**Use a transport token (Paradigm).** If you're carrying a Paradigm **transport token** whose town reaches your destination meaningfully faster than walking, the picker adds a **blue token card** beside the plain overland walk — **"Use token of X — saves N rooms"**. It's never taken for you: using a token spends gold, one of its daily charges, and **wipes your buffs** (it casts negate magic), so it's always your click. Pick it and MudPlay uses the token and resumes the walk from where it drops you. If the room you're in isn't clear (a token can't be used with monsters present), it walks the overland route toward the destination and uses the token at the first monster-free room instead — a genuinely-shorter token route always reaches one before you arrive. Two toggles under **Settings → Other** (Paradigm only) control this: turn token routing off entirely, or set how many rooms a token must save before the card appears.

Because using a token casts negate magic and **wipes every buff**, MudPlay pauses buffing the moment a token use is on its way — whether you use it yourself or a party leader sends you across (it recognizes the relayed `use`, full name or shorthand). The hold lifts as soon as the token actually fires (buffs recast after you land) or after 30 seconds if the use never went through; while it's active the **Buff Watchdog** shows a *"Paused by token usage"* line.

**In a party**, only the leader can take a token route, and the leader goes **last**. From a monster-free room it sends the party across first with `.@party use token of <place>` (a party-relay every follower acts on — no special permission needed), then watches its own room as each member gryphons out. If someone hasn't gone, it checks who's still in the room and re-broadcasts `.@party use token of <place>` — a room-local relay, so only the members still standing there are re-told (no special permission needed) — up to three tries a few seconds apart. Once the whole party is across, the leader tokens over itself and the walk continues. If a member still can't follow, what happens depends on **Settings → Other → "Take a token route even if a party member can't follow"**: off (default) the leader stays put and **fails out with the reason in the nav header** so you can sort it out; on, the leader tokens across anyway and leaves them. (A party follower who tries to take a token route just walks the normal way instead.)

**Routing through a room you marked "Avoid".** Rooms you flag **Avoid** (nav-map right-click → *Toggle: Avoid this room*, or the Avoid/Stash editor) are normally treated as walls — the walker never routes into them. When a destination is reachable **only** by passing through one, the route picker surfaces a choice rather than just failing:

- **"Route through N avoided room(s)"** — walk it this once, or cancel.
- **Two-card fork** — when an avoid-respecting route *does* exist but a route through an avoided room is meaningfully shorter: **"Respect your avoids"** (the longer clean route, pre-selected) vs **"Shorter — through N avoided room(s)"**.

Either way the card warns exactly how many marked rooms it crosses, and your **avoid list is left untouched** — only that one walk ignores it.

It checks one thing first: if the destination *is* reachable without touching an avoided room once you **obtain** something — a raft to cross a river, a key for a door — the picker offers that obtain-and-cross route (which respects your avoids) instead of asking you to override them. So "route through your avoids" only comes up when crossing a marked room is genuinely the sole option, not when a raft two rooms away would do.

That said, when the raft crossing *is* offered, an extra **"Route through N avoided room(s)"** card sits alongside it — plow through the marked rooms (no counter needed) if you'd rather not fetch the raft.

Every card that skips the safe way — cross a hazard unprotected, or route through avoided rooms — is tinted **red** so the risky pick is obvious.

**Money and the party when a route needs buying something.** When crossing a hazard needs a counter you'd **buy** (a raft, a waterskin) and you can't cover it from coin on hand, the picker **checks where the money is before it offers the buy**. It reads your own **bank** deposits and, if you're in a party, asks the party for their **carried cash** (`@wealth`) and whether a member already **has** the item (`@have`) — or any other item that would do for that crossing, so a member's spare canoe is found and handed over instead of buying a raft.

The card then tells you what it found:

- "withdraw ~N copper at your bank first" — a deposit covers it.
- "it's on deposit at Bank of Albion" — the money's at another bank.
- "a party member has it — will hand it over on the way".
- "you're short ~N copper — the party has it on hand; walk there and provision".

If you can pay from cash (or a withdraw at your configured bank), **Go** buys and crosses as before. If the money's elsewhere — another bank, or spread across the party (many groups keep the leader light and the gold on one or two members) — **Go walks you to the shop and stops there**, so you can withdraw / pool coin / hand the counter round to everyone by hand, rather than setting off on a buy that can't complete. (Bank balances are self-only — you can't see a party member's bank — so party money means their on-hand cash.)

**"Calculating…" on a walk-to.** Working out a route across a large map can take a moment. When you're standing still, that planning runs in the background so the client stays responsive (no freeze), and if it takes long enough to notice, the **Choose a route** window pops up right away showing **"Calculating…"** and fills in the option cards the instant planning finishes. A quick plan skips the placeholder and opens the picker fully-built. (If a walk is already underway when you pick a new destination, planning runs inline instead, so the window just appears when it's ready.)

**Searching for a counter en route.** When a route crosses a hazard you'd counter, the picker also offers a **"Search en route"** card. Pick it and the walker heads toward the hazard **searching each room on the way** (`sea`) — and if a counter turns up on the floor it's grabbed automatically and you cross.

**Auto-Search drives that per-room search**, so picking the card **turns Auto-Search on for the leg** if you had it off (the card is a search, so it makes sure searching actually happens) and **flips it back off** once the counter lands — whether a search found it or the shop-buy did. If it was already on, it's left on.

Searching isn't all-or-nothing, though — the card also **buys the counter at a shop as a last resort**: if nothing turns up en route (or you toggle Auto-Search off mid-route yourself), it runs to the nearest shop that stocks the counter and buys it, so the walk still completes. Whichever delivers the counter first wins; the other is dropped. It only stops at the hazard's edge when there's genuinely **nowhere** to get the counter — not sold anywhere and not found.

This cuts both ways: on the **"Obtain, then cross"** (buy) card, turning Auto-Search **on** means a counter found loose en route is used instead of buying it, rather than the run marching past a free one to the shop. (A searched-up counter is collected by the route's own obtain pipeline, so it doesn't depend on the Auto-Get engine being on and the item flagged auto-collect.)

**Seeing the full step plan.** Once you **click a route** in the picker, the **Details…** button (bottom-left) lights up. It opens that route's complete, start-to-finish plan in a scrollable window — every move and every **detour** (a lever pulled in another room, a winch cranked, a door opened) shown inline as `12/431 Tower < s`: the room you're standing in, then the command sent from it. It's the same expansion the walker runs, so what you read is what it will do. The window's per-room extras — monster, hazard, and item-gate links — are described just below.

**Seeing the route you're already on.** The window's **title bar shows the ETA** to arrive via the route (the same realm-aware estimate the route cards use). The estimate charges combat dwell only for lairs the party will **actually fight** — a room whose occupants are friendly, fled, or neutral-and-not-kill-on-sight is walked straight through, so a hostile-free path reads close to raw walk time instead of inflating by every lair marker on the way.

The same **Details…** window opens from other places too:

- the **CURRENT NAV** panel's header (in the right rail) once a route is *running* — a point-to-point walk, a loop circuit, or an Auto-Lair approach, so you can check the path ahead without re-planning it;
- a **previewed** walk-to — arm a destination in the search box (before you press Go) and Details… shows the route you're about to take.

Three things the window shows at a glance:

- **Each room name is a link** — click it to flash the room on the map and centre there, the same as an `@where` reply.
- At every room on the route, its **notable monsters** — placed fixtures (a boss / NPC) and lair spawners — are listed under that step, each a **clickable link** to the monster's Game Data record — handy for sizing up what a hunting loop is about to walk into. Each name is **tinted by the monster's alignment** by default — evil red, neutral cyan, good or lawful white — mirroring how the game itself colours them. A **see-hidden** monster (one that defeats sneak) is flagged with an **👁 eyeball on either side of its name**, so you know it'll spot you coming.
- Tick **"Color monsters by hit %"** (top of the window, shown whenever the route passes monster rooms) to tint each name by **danger instead of alignment** — its live **Hits-You-%** (the same weighted chance-to-hit-you Monster Intel shows, against your current AC/Dodge/wards and assumed-up buffs). A safe monster reads **green**, a dangerous one **red**, with a **yellow** middle band. Drag the two thumbs on the slider to set where the bands fall — the defaults are **green ≤ 15%**, **yellow ≤ 45%**, **red above 45%**. A monster with no computable hit% (an NPC/caster with no physical attack, or before your character sheet is known) reads a muted grey. The toggle and the band split are **saved per character**, so each character's route Details opens the way you last left it.
- A step that needs a special item is flagged with a **⚠** on either side of the room name and a sub-line naming what's required — covering both a **hazard** room (a river crossing, lava, the desert heat: the harmful spell links its record, and the item(s) that make it safe to cross are listed) and an **item-gated exit** (a cliff you can only descend with a rope & grapple, a river you cross by raft). The required items — a raft (log raft / canoe / punt), rope & grapple, a phoenix feather, a waterskin, and so on — are shown in **dark yellow** and each link their item record, so you can see at a glance what a route needs before you set off.

The list ends with an **arrival** row for the destination itself — the room the route lands in — marked *(arrive)*, so the plan shows exactly where it finishes.

Click **Details…** again to close the window.

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

**Even while solo**, the window shows **your own entry** — the same row, live-updating your HP / MA and status chips from your state — so you can watch the client recognize an ailment applying and clearing in real time without needing a party. It's display-only: your lone self row is never treated as a party (automation that only runs in a real party stays off), and the row folds into the roster seamlessly the moment a party forms.

The healing, ranks, nags, and re-invite behaviour the window reflects are all configured on **Settings → Party**.

### Configuring party buffs

Party buffs are no longer set up here in the Party window. **All** automated buffing — self bless, party bless, room light, mana-regen, and the "when HP/MA full" utility casts — is now configured in **one unified list inside the Buff Watchdog** (View → Buff Watchdog): click **＋ Add buff**, pick a spell, and tick the party members (or **All**) it should be cast on. See **Buff Watchdog** under *Tools & Diagnostics* for the full walkthrough.

Two things about party buffs stay worth knowing here:

- **Who's targeted** — a single-target buff fires for any member who's **currently in your party** (a MajorMUD party is always in one room, so being in `par` means being in the room; a member who leaves or is uninvited drops out and is no longer targeted). The one exception is a member who's **hiding**: the cast comes back *"You do not see … here!"*, so the client backs off that member — the Buff Watchdog marks them **"hidden — can't target"** — and retries the next time you **move** or they **reappear**. Targets are remembered by name, so your setup survives parties dissolving and reforming.
- **When it casts** — the two **bless while resting / during combat** gates that decide *when* the party-buff engine may cast still live on **Settings → Party**.

## Leaders and followers

One character leads; the rest follow. A follower tracks the leader's movement and holds position; if the leader disconnects, the party disbands. A party is 2–6 characters.

**Leader reconnect re-invites the party.** A leader-drop dissolves the party, but the followers keep sitting in the room (they've no leader to follow). When the leader reconnects, MudPlay re-invites the ones still there — waiting until it actually sees each in the room before sending `invite`, since the game drops an invite aimed at someone who isn't present. (Gated by *auto-invite on reconnect*, same as the follower-reconnect case.)

## Party healing

With party heal spells configured (Settings → Party), members watch each other's health broadcasts and heal whoever drops below the minor/major thresholds — single-target, or an area heal once enough members qualify.

## Remote @-commands

Party members can drive each other with `@`-commands sent over chat. Commands are accepted on three channels — **telepath**, **gangpath**, and **say (local)** — and the reply always comes back on the same channel it arrived on. A reply to a **say**-channel command is a **directed say** (`>Name <reply>`) aimed at whoever sent it, so in a room with several players that person knows the answer is for them. (Gossip — which also carries auctions — yell, and broadcast are ignored for `@`-commands; there's no separate "page" channel — pages count as telepaths. `@dupe` is stricter still: telepath and gangpath only, never say.)

**What's allowed** is gated per character. Every remote command belongs to a permission *category* (query health, move me, alter settings, execute commands, and so on), and you grant those categories per player in **Game Data Browser → Players** — the edit dialog's permission grid, where the high-trust ones sit under "Elevated Commands." A never-seen player has no grants, so their commands are refused.

On top of that, **Settings → Talk** has master and per-channel kill switches (disallow all remote control, or mute telepath / gangpath / say), a separate gate for `@party` directives, and a "warn on invalid/denied command" toggle that decides whether a refused command replies or stays silent. An @-word that matches no command at all — someone just typing "@because" in chat — is always ignored silently regardless of that toggle; it only governs a *recognized* command that's denied (a permission the sender lacks, the `@party` whitelist, the suicide policy).

Active party members get a few things for free regardless of the grid: the party-coordination signals, the health queries (`@health` / `@status` / `@lives`), `@reset`, and a bare `@party` status check.

### Recursive remote commands (`&@`) — sending a command back to yourself

Put **`&`** in front of any remote command — `&@invite`, `&@where`, `&@wealth` — and the player you send it to **sends that `@`-command back to you**, on the same channel it arrived on (telepath, gangpath, or a directed say). Your own client then runs it as if they had sent it, under the permissions *you* grant *them*. It's the way to make another player ask something of you: telling a party member `&@invite` makes them send you `@invite`, and your client invites them.

The player relaying it needs to grant you **Execute commands** (the `@do` tier) — relaying a line on your behalf is something `@do` could already do. The relayed command must be one their client knows; anything else (or a bare `&@`) is ignored, and the reroll / `set suicide` blocks apply to it as well. Bare `@help` lists `&@<command>` when you hold that grant, and `@help &@` describes it. On **gangpath** or **say** everyone on that channel sees both lines: every MudPlay client there that grants you Execute commands relays the command back, and any that grants the relaying player the command runs it too. Use **telepath** when you want exactly one player to relay it, and only to you.

### Syntax examples

Every remote command is the `@`-word typed into **telepath, gangpath, or say**. A **bare** command (no argument) is just the word — `@health`, `@where`, `@inv`, `@wealth`, `@stop`, `@version`. The commands that take an argument follow the **Args** column in the tables below; here's one valid example of each shape:

- `@help goto` — one command's syntax and description (the `@` on the argument is optional — `@help @goto` works too)
- `@goto arlysia` · `@goto 3/599` · `@goto ogre king` — a GOTO favorite, a `map/room` coordinate, or a boss / room by name or acronym. A coordinate can separate the map and room with a **slash, comma, or space** — `3/599`, `3,599`, and `3 599` are all read the same. A **bare** room number is rejected (`@goto 599` → "needs a map"), because the same number is a different room on every map
- `@loop Black Fortress` — start a saved loop by name
- `@loop 5/10 5/11 5/12` — an ad-hoc loop from two or more `map/room` coordinates
- `@loop last` — re-run the last loop run this session
- `@loop send kings road` — ask the player for a copy of their saved loop (they offer it; answer `@loop send yes` or `@loop send no`)
- `@lair mud men` — start an Auto-Lair (a setup name or coordinates)
- `@timer dragon` — boss timers whose name matches "dragon" (bare `@timer` lists them all). Each line gives the full respawn plus every un-passed early-spawn window — on Paradigm all three (`-20%` / `-10%` / `-5%`), on Stock the single `87.5%`
- `@death all` — every unrecovered death (bare `@death` gives just the latest)
- `@have rope and grapple` · `@uses silvery skullcap` · `@token arlysia` — an item / limited-use item / transport token by name (shorthand and best-match are fine)
- `@roomba severed head` — Roomba sightings of matching items
- `@quest good align` — quest progress: marked-complete bands, plus the live flag step on Paradigm / sys-god (bare `@quest` lists all your completed quests)
- `@quest update` — have a party member re-read their quest flags and mark every quest those flags show they've finished; a quest they're part-way through has its checklist ticked up to the step the flag shows
- `@auto-combat off` — force an engine off (bare toggles it; `on` forces it on)
- `@atkprio 3 Fujin` — Target Priority: attack-what-player Fujin (`1` = Default, `2` = follow-leader)
- `@atkorder 4 Suijin` — Attack Order: attack after Suijin (`1`–`3` and `5` are the fixed orders)
- `@divert Raijin` — forward your incoming telepaths to Raijin (bare `@divert` stops)
- `@dupe Moron` — copy the sender's query, roomba, and quest permissions onto Moron (Elevated; once per player; telepath / gangpath only)
- `@profile 2` · `@profile backstab` — swap combat profile by number or name
- `@kill goblin shaman` — retarget your combat onto that monster this round
- `@trap north` — search and disarm a trap that way (`@trap stop` aborts)
- `@equip-backstab` — wear the saved gear set whose keyword is "backstab"
- `@do rest` — send `rest` to the game verbatim (highest-trust)
- `&@invite` — have the player send `@invite` back to you (any command works after `&`; needs their Execute commands grant)
- `@party use chime` — relay `use chime` to the whole party (say channel only)
- `@comeback 3/599` — ask the party to recover you (the coordinate is optional)

### Query commands — they report; nothing changes

| Command | Args | Replies with |
|---|---|---|
| `@version` | — | the app name + version |
| `@help` | — or `<command>` | bare, the commands *that sender* is allowed to use; with a command name (`@help goto` — the name is accepted with or without the `@`) it replies with that command's syntax + a one-line description. Requires the `@` like every remote command — a plain `help` in chat won't trigger it. |
| `@health` | — | HP / MA / Kai and resting-or-meditating state |
| `@status` | — | what you're doing (walking / looping / fighting / resting), your room, and any ailments |
| `@lives` | — | lives remaining |
| `@exp` | — | a session-progress line: exp **made** this session (zeroed by `@reset` / loop-start auto-reset), exp **needed** for the next level and which level that is (with the banked-levels ratio the status-bar TNL shows), the exp/hour rate, and the time to that level at the current rate (e.g. `Made: 474,216,179  Needed: 545,045,125 (L72, +2.14 lvls)  Rate: 14.3 m/hr  Will level in: 1d 14h 12m`) |
| `@level` | — | level, current exp, and exp to next |
| `@where` | — | room name, map/room, and exits |
| `@path` | — | the movement engine's activity and step progress; when stopped/idle, names the last loop or auto-lair that was run (so you can help a dead player resume their circuit) |
| `@who` | — | other players / monsters in your room |
| `@timer` | — or `<name>` | boss respawn timers (all, or matching a name) |
| `@timer sync` | — | (client-to-client) replies with your active timers, compressed, for another MudPlay user's merge table — see "Sync boss timers" under the Bosses tab; same `@timer` permission |
| `@death` | — or `all` | unrecovered deaths from the recovery log — the most recent one, or `all` of them (each with when, status, room, and lives left) so you can help a dead player recover; own permission ("Query deaths") |
| `@roomba` | `<item name>` | one line per matching item — total quantity across every gang-house room it was seen in during a Roomba sweep, EACH room's own quantity, and when the freshest of those sightings was scanned, in the sending client's own timezone (e.g. `total: 5x rope and grapple - seen in 15/12 (3), 15/13 (2) - last scanned 2026-08-30 09:22 MST`) — a loose query matching several similarly-named items (e.g. "head" matching every "severed head of ___") gets one line each, capped at 5 with an overflow tail — or "no record" when nothing matches at all; gated by the **Query Roomba** per-player permission (grant it on the Players tab) — a sender you haven't granted it to gets no reply. See Roomba (Player Workshop) below |
| `@roomba sync` | — | (client-to-client) replies with your entire item-sighting log **and** labeled gang-house rooms, compressed, so the requester's client merges it straight in — no file, no Discord, no import/export; the requester adopts the reply because they asked for it, so only the *responder* needs the grant; see Roomba (Player Workshop) below; same **Query Roomba** permission |
| `@quest` | — or `<name\|flag>` | quest progress. Bare lists every quest with a marked-complete band, grouped by flag. With a **name** (a quest's name, the ability name like `goodquest`, or a built-in alias like `good align` / `neutral` / `evil`) or a **flag number** (e.g. `126`), it reports that quest's marked bands by ordinal (`Good align 1, 2, 3 marked complete`) and — on **Paradigm**, or a **stock** board where you've granted this character **sys-god** access — appends the live flag step read off the game (`Abil: 126 step 16`). The live read also **marks** what it proves: a flag that has reached a quest band's complete value means that band is done, so it's ticked on the answering character's Quests tab and the reply shows the updated marks (plus how many were newly marked). **`@quest update`** reads every flag the answering character's current-level quests use (Paradigm: `abil`; stock: the one `sys god <name> abil`, which needs **Settings → BBS → "Sysop god lives"**), marks every completed one, and replies with the count and the full marked list. It isn't held to the login sync's once-a-day limit, since someone asked. On stock without sys-god it reports the marked state only (and `@quest update` says it can't read the flags, if failure replies are on); gated by the **Query quests** per-player permission |
| `@what` | — | items on the room floor |
| `@wealth` | — | your coins and total value |
| `@enc` | — | encumbrance |
| `@have` | `<item>` | whether you carry, wear, or hold a matching item on the key ring |
| `@inv` | — | your carried pack and keys |
| `@token` | — or `<name>` | remaining daily charges of your held transport tokens — bare lists them all, a name reports just that one (Paradigm) |
| `@uses` | — or `<item>` | remaining charges of a carried limited-use item — bare lists every charged item you carry, a name (shorthand ok, best-match) reports just that one |

### Move me around

| Command | Args | Does |
|---|---|---|
| `@goto` | `<destination>` | walks you to a saved GOTO favorite, a searched room (coords / name / acronym), or a boss |
| `@loop` | `<name>`, ≥2 coords, `last`, or `send <name>` / `send yes` / `send no` | starts a saved loop, an ad-hoc coordinate loop, or (`@loop last`) re-runs the last loop run this session — including an ad-hoc one that was never saved. `@loop send` asks for a copy of one of their loops instead (see *Getting a loop from another player* below) |
| `@lair` | `<name>` or coords | starts an Auto-Lair setup |
| `@stop` | — | pauses your movement |
| `@rego` | — | resumes it |

A new movement command overrides an `@stop`: after `@stop`, an `@goto` / `@loop` / `@lair` abandons the pause and starts the new movement straight away — you don't need `@rego` first (use `@rego` only to resume the *same* thing you paused).

#### Getting a loop from another player (`@loop send`)

Another MudPlay player who grants you **Move player** (the same grant `@loop` needs) can send you a copy of one of their saved loops over chat:

1. Send them **`@loop send <name>`** — the name matches the same way `@loop` does (exact name first, otherwise every word you type, in any order; apostrophes are optional, so `kings road` finds *King's Road*). They reply **`{preparing to send: King's Road, yes to confirm, no to deny}`**, or tell you the name matched nothing or several loops.
2. Answer **`@loop send yes`** to receive it, or **`@loop send no`** to call it off (they reply that it was cancelled). The offer lapses after two minutes.
3. On yes they reply how many rooms and lines are coming, then send the loop as a few encoded `@loopdata` lines, paced so they don't crowd out anything else. Your client puts it back together and saves it to your Loops list, with a note in the terminal.

What arrives is the route itself — every waypoint's room, command, delay, *Don't rest here* and *Don't attack here*, the loop's notes and its *Only attack in lair rooms* setting. Whether it's a favourite, and which folder it sits in, stay your own choice. A loop never overwrites one of yours: if you already have the identical loop you're told so and nothing is saved, and if you have a *different* loop by that name it's saved as **`<name> (from <player>)`**.

Your client only accepts loop lines within two minutes of your own `@loop send yes`, and — when you answered by telepath — only from the player you said yes to. Use telepath: on gangpath or say, every MudPlay player there who grants you Move player would offer their own match.

### Change my settings

- The auto-engine toggles — `@auto-combat`, `@auto-nuke`, `@auto-heal` (`@auto-rest` is the same flag), `@auto-bless`, `@auto-light`, `@auto-cash`, `@auto-get`, `@auto-sneak`, `@auto-hide`, `@auto-search` — each flips that engine (bare toggles it; add `on` or `off` to force it).
- `@auto-all` — the kill switch: `off` stops every engine, `on` restores what was running. `@settings` — reports every engine's on/off state.
- `@atkprio` — Target Priority: bare reports it; `1` Default, `2` follow-leader, `3 <name>` attack-what-player.
- `@atkorder` — Attack Order: bare reports it; `1` Default, `2` last-party, `3` last-room, `4 <name>` attack-after, `5` not-last.
- `@divert <player>` — forwards your incoming telepaths to another player; bare `@divert` stops.
- `@profile <n|name>` — swaps your active combat profile (spells + verbs + room thresholds + weapons + the whole Health tab); bare `@profile` reports the roster (see **Combat profiles** under Settings → Combat).
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
- `@hangup` — drops your connection and stays down (no auto-reconnect), so you can read the screen and log back in by hand. `@relog` — the opposite: cleanly exits, then reconnects and auto-logs back in. Both need the **Hangup/disconnect** grant, and both are silenced while the toolbar's *Disable hangups* toggle is on.

### Hand out permissions

- `@dupe <player>` — copies **the sender's query, roomba, and quest permissions** onto that player, so a trusted player can bring an alt up to speed without you ticking every box. It hands out trust, so it needs the **Elevated Commands** grant — the same top tier as `@suicide`. A player you've granted "All" has it; a player with every category *except* Elevated does not.
  - **Only queries move.** The categories it can copy are Query version, experience, health/status, location, inventory, boss timers, deaths, Query Roomba, and Query quests. Nothing that acts on your character (move, execute, alter settings, request invite, hangup, divert) and **never Elevated Commands** — so a duplicated player can't `@dupe` onward, and gaining anything beyond queries stays a manual step you take in the Players tab.
  - **One use per player.** Each Elevated player can `@dupe` once. After that it's refused ("your @dupe has already been used") until **you** re-arm it: open that player in **Game Data Browser → Players**, and under **Elevated Commands** press **Reset @dupe**. The dialog shows when it was spent and who it went to. Nothing sent over chat can reset it. A refused or no-op attempt (unknown name, the target already holds everything) doesn't spend the use.
  - **Telepath and gangpath only.** Said aloud in a room, or sent as a gossip, auction, broadcast, or yell, it's ignored outright (no reply), and the Local control API can't run it.
  - **Additive.** The target keeps anything they already have and only gains; it never takes a permission away, and it only moves the permission grid, not the target's party behaviours or notes.
  - **Refused:** your own character, the sender themselves, and any name your client has never seen (so a typo can't pre-grant trust to a name someone registers later). The sender is told which; the reply follows the "warn on invalid/denied command" toggle.
  - **Logged.** Every use — and every refusal — is written to the program log at Info (who, onto whom, exactly what was granted), and the sender's record keeps who they duplicated onto and when.

### Party coordination — any active party member, no grant needed

- `@wait` — hold: automation pauses until you `@ok` (which releases it).
- `@comeback` (optionally `<map/room>`) — a stranded member asks the party to come recover them; `@forget` calls that recovery off.
- `@share` — splits your held coin evenly across the party.
- `@ptrain` — the **Auto-train party** handshake between MudPlay clients (readiness reports, and the leader's give / withdraw / train orders during a party training trip). You never type it; a client only acts on it while its own *Auto-train party* box is on, and only on orders from its current leader. See **Auto-train party** under Settings → Auto-Trainer.
- `@party` — bare, it reports whether you're solo / following / leading. Sent on **say** *with* arguments, it relays whatever follows verbatim to your character as if you typed it (the party version of `@do`) — `@party rest`, `@party use chime`, and so on. The directive form only works on the say channel, and Settings → Talk can disallow it.
- `@panic` — the party-wide bail-out (MegaMUD parity). A **leader** whose HP crosses its **"hang if below"** floor says a bare `@panic` on say and then escapes (hangs up, or breaks + `sys goto <wimpy>` per the Health tab) — warning the whole party to get out. It's opt-in on both sides via two **Settings → Party** checkboxes: **Use @panic while leading** (whether you send it) and **Ignore @panics** (whether a received one makes *you* bail). Both default off. A received `@panic` makes you escape exactly as your own low-HP emergency would; it still respects the *Disable hangups* master switch for the carrier-drop (you'll `sys goto` wimpy if configured, but never be force-disconnected by someone else's panic).

**Telepath pacing.** The server throttles telepaths — fire several at once and the later ones come back `--- Telepath Not Sent ---`. MudPlay sends every telepath (its own @-command traffic and replies, and the ones you type) at least 100 ms apart, and resends any the server refuses, up to three tries. Other commands — movement, attacks, casts — are never held behind a telepath.

### Irreversible and always-blocked

- `@suicide` — forces your character's death, using the suicide password MudPlay captured from your in-game `set suicide`. It's an **Elevated Command**, and Settings → Other blocks it when your remaining lives are at or below your threshold.
- A few things are **always refused, silently, no matter what's granted**: anything containing `reroll`, and `@party set suicide` — these can't be leaked or overridden.

**Not commands:** the ailment broadcasts `@blind` / `@confused` / `@diseased` / `@held` look like `@`-commands but aren't — they're state announcements the party window reads to mirror a member's condition, governed by your cure/ailment settings rather than the remote-control grid. (Poison isn't broadcast — a member's **poison** chip is read from the `par` party screen's `P` flag, so it lights even for a partymate on another client.)

A member's chip clears on the first of:

- the member broadcasting they're clear (`@ok`);
- the effect's duration lapsing;
- the `par` `P` flag dropping (poison);
- **you witnessing any cure land on them** — including one cast by a party-mate using a spell your own class can't cast (a Priest's cure poison, antidote, freedom, cure disease, and heal+cures like curing wind). Cure recognition reads the game's own spell data, so it doesn't depend on you having that cure configured.

## Reconnecting

If a member drops, the party can auto-re-invite and reform on reconnect, and a member left behind can `@comeback` to rejoin the leader.

---

# Healing & Spells

The health and spellcasting engines keep you alive and buffed — resting, healing, curing, and blessing on their own.

## Health: rest, heal, flee

Auto-Heal / Rest (its toolbar toggle, or Settings → General) watches your HP and mana. Below your rest thresholds it sits and rests (or meditates) back up; below your run thresholds it flees; below your hang-up threshold it can drop the connection as a last resort. Every threshold is set on Settings → Health, as a percentage or an absolute value.

**A hostile blocking your rest, even with Auto-Combat off.** A monster in the room keeps you *in combat*, and you can't rest while it's swinging at you. So when a rest is due (HP **or** mana below its *rest if below*) and an enemy is blocking it — but your HP is still **above** *run if below* — the engine will **fight it to clear the room even if Auto-Combat is off**, then rest once it's dead. If your HP then falls to *run if below* during that fight, it stops and **flees** instead (breaking combat first when *break before running* is set).

This is automatic and needs no toggle — it's the only thing that reaches through an off Auto-Combat, and only to escape the sit-there-and-die deadlock; a healthy character just walks past monsters as before.

## Casting priorities

When more than one spell wants to fire, the caster follows the priority order on Settings → Spells — by default emergency heal, party heals, downed-ally rescue, self heals, curing, buffing, then debuffing — and won't cast if it would drop you below your mana floors. The one exception is **emergency heal**: it leads the order by default and ignores the mana floor entirely, spending whatever mana is left to save you (see Emergency heal, below).

Because a round's damage lines arrive a beat before the prompt that reports your new HP, the client waits for that confirmed HP before it will spend the round's one between-round cast on a **cure, buff, or debuff** right after a hit lands. Healing is never held this way — so if a round chunks you low, the client won't burn that round buffing on a stale "you look fine" reading and skip the heal; the heal fires the moment your real HP is confirmed.

## Curing and blessing

Configure cure spells for holds, poison, disease, and blindness on **Settings → Spells**; the bless (buff) slots that recast as they expire now live in the **Buff Watchdog** (View → Buff Watchdog — one unified list for self *and* party buffs).

Auto-blessing — self *and* party — is controlled by the **Auto-Bless** toggle and nothing else (it's independent of Auto-Combat and Auto-Rest/Heal). By default the engine buffs while you're **moving or standing idle** (including an idle rest) and holds off **during combat** and **during a triggered recovery rest** (when HP or MA fell below your rest-if-below setting).

Two opt-in checkboxes override those holds — one to also bless during a recovery rest, one to also bless while actively fighting (the self pair on **Settings → Spells**, the party pair on **Settings → Party**). You can also tell it to ignore, or not announce, specific ailments.

## Mana regen

For mana-regen classes, the caster can rest to regen and — if configured — reroll a poor regen result up to a cap. The mana-regen spell and its reroll settings are configured in the **Buff Watchdog** (View → Buff Watchdog); rerolling works on Paradigm.

## The Spell Book (F2)

Press **F2** to open the **Spell Book** — a read-only reference to your class's spells. It's a lookup companion for the Spells settings, not a place you configure automation: use it to find a spell's cast-code and effect, then type that code into the pickers on **Settings → Spells**. F2 again closes it, and it updates itself as you play (type `spells` or `stat` in the game to refresh what it knows).

The list also respects your **alignment** — a Good-only, Evil-only, or Neutral-only spell you haven't learned yet stays hidden until your alignment actually matches it, everywhere the class list feeds (the book itself and the Settings → Spells pickers alike). A spell you've **already** learned never disappears, even if your alignment later drifts away from it — an alignment-quest reward stays yours.

Alignment isn't part of `stat`'s output, so filtering only kicks in once the game has shown your own row in a `who` this session; before that, every alignment-gated spell still shows (nothing gets hidden on a guess).

**All / Heals / Buffs / Attacks / Party+AoE** tabs across the top narrow the grid by what a spell actually does:

- **Heals** — restores HP or cures poison.
- **Buffs** — applies a maintained (timed) effect.
- **Attacks** — costs casting energy (a combat-round spell).
- **Party+AoE** — hits more than one target in a single cast (a whole-party buff or heal, or an area attack).

A spell can land in more than one tab (a whole-party buff like chant shows under both Buffs and Party+AoE) — switching tabs re-filters the same list rather than sorting each spell into one fixed bucket. **All** clears the tab filter.

The header names the class and level it's showing. The grid lists each spell with a **✓** if you've learned it, plus these columns:

- **Code** — the cast-code you type.
- **Name**.
- **Lvl** — the level **your class** can actually learn it, respecting a trainer's level gate (so a spell your class learns late from a specific NPC reads its real level, not the spell's lower base requirement).
- **Mana** cost.
- **Success %** — see below.
- **Effect** — at your current level (hover the Effect cell for the raw scaling formula).

**Double-click a spell** to open the game-data record of whatever teaches it — the **item** for a normal spell, or the **trainer NPC**'s record for a spell learned from an NPC (e.g. a Paladin's divine disfavour) — handy for finding where to buy or how to obtain a spell you haven't learned. Spells with neither an item nor a trainer source do nothing. Three controls up top narrow the list:

- **Show all** — off by default (you see only spells you're high enough level to cast); tick it to preview the whole class list, reading the **Lvl** column for when each unlocks.
- **Known only** — hides spells you haven't learned yet.
- **Search** — filter by cast-code or name.

The **Success %** column is your **chance to land the cast** (as opposed to fizzling) — computed from your **Spellcasting** stat plus the spell's own difficulty, capped at 98% (100% for Kai). (It reads "Success %", not "Difficulty", because the number *is* your success chance — a higher value is better.) It's independent of your level: raising Spellcasting (or gear that boosts it) is what lifts it.

A spell shows **—** when no chance can be stated — you're not a caster class, or your stats haven't been read yet (type `stat` in the game to populate them). Reopen the book after a `stat` to refresh it.

If your class carries wands, scrolls, or potions that cast a spell, a **Cast-on-use items** section at the bottom lists what each one casts, its mana, and its charges.

---

# Cash & Items

MudPlay collects coin and loot, banks your wealth, and manages your gear.

## Collecting coin and loot

With the collection engines on, MudPlay picks up coin and flagged items off the ground after a fight, following your per-currency rules (Settings → Cash) and the per-item flags in Game Data. It can skip a pickup that would push you into a heavier encumbrance band, and drop smaller coin to make room for larger.

In a **stash room** the client stashes your excess coin (and any auto-stash items) as you pass through, so it deliberately does **not** re-grab a pile it just hid — but only the coin a `search` *re-reveals* is skipped. Coin that's plainly visible when you walk in, or that a kill drops on the floor, is still collected there (and, of course, in every ordinary room, including the room right after a stash room).

You don't have to wait for the engines, either: the **Action menu** (and the matching toolbar buttons) has **Get All**, **Drop All**, and **Equip All** to grab everything on the floor, drop everything unworn, or re-wear your Default set on demand — the local twins of the `@get-all` / `@drop-all` remote commands.

## Banking

When your wealth crosses a threshold, MudPlay routes to a configured bank and deposits, keeping a set amount on hand, then **walks back to where it left off and resumes the loop / Auto-Lair** it interrupted. The trip home uses the **full pathfinder** — the same one that handles your GOTOs — so if the grind area is walled behind a key-door, a hidden exit, a summon-drop key, or a lever/ask-NPC gate, it plans and crosses back *in* rather than stranding at the bank (getting *out* of such an area is easy; getting back *in* needs the gate-aware routing).

Set the bank and thresholds on Settings → Cash. To bank right now regardless of the threshold, use **Action → Deposit All** (or its toolbar button / the `@deposit-all` remote command), which banks down to your keep-on-hand floor.

## Equipment sets

Gear is organized into named equipment sets in the **Player Workshop** — a Default set feeds your normal/alternate weapons and armor, a Backstab set feeds your stealth gear — and MudPlay swaps to the right set automatically (and re-equips after recovering a death pile). The **Item Finder** helps you build sets by browsing every equippable item with full stats.

---

# Player Workshop

Press **F1** (or View → Player Workshop) to open the **Player Workshop** — a tabbed window for managing your character: gear, leveling, quests, bosses, deaths, your character sheet and calculators, and gang-house item sorting (**Roomba**). There are no Save buttons anywhere in it; every edit auto-saves to your profile. Its tabs, most-used first:

## Equipment Manager — gear sets

Your gear lives in **six fixed sets**, each auto-equipped at a specific moment:

- **Default** — your baseline loadout (and backstab fallback). It auto-equips when a **loop or Auto-Lair run starts** (unless you've set up a While Moving set, which then owns your travel gear instead), when you've **finished resting** (recovered to your rest-max — but only if you actually use the pre-rest swap sets below, so a rest that never left Default isn't disturbed), and on **death-pile recovery** if *Auto-Equip on recovery* is on. By default it does **not** swap on combat entry — if a fight interrupts a rest, you keep your pre-rest loadout until you've recovered. You can change that with the **"Don't swap to default upon entering combat"** checkbox (see below). When any set swaps, the loop holds in place until every wear/remove has streamed, so the swap always finishes **before** you step out — you never walk into the next room and change gear mid-fight.
- **Backstab** — worn for the opening backstab round.
- **Pre-rest HP** / **Pre-rest Mana** — swapped in out of combat before resting, and kept on for the whole rest: MudPlay won't revert to your Default set until you've actually recovered to your rest-max (so a between-round buff or a loot grab that briefly stands you up doesn't flip your gear back and forth). Two-handed weapons and off-hand items can't coexist, so a swap that changes your hands clears the conflicting piece first either way — a readied two-hander comes off before an off-hand goes on, and a worn off-hand comes off before a two-hander is wielded.
- **While Moving** — worn while you're travelling (a loop, Auto-Lair, or a walk-to) and *not* fighting or resting, so you can carry **+quickness / movement-speed gear** (simple sandals, brown leather boots, the white wolf mantle, and the like). The moment hostiles are recognized it swaps to **Default** and engages; when a walk-to reaches its destination it reverts to Default. **Off by default** — build and enable it to use it. Its own **"Swap to default before entering lairs"** checkbox controls lairs: checked, it swaps to Default the step *before* you enter a known lair (you arrive combat-ready) and swaps back to the While Moving set on the way out into the next non-lair room — a run of adjacent lairs stays in Default the whole way through, without flapping between the two; unchecked (default), you enter in movement gear and swap when monsters appear. By default only a loop, Auto-Lair or walk-to wears it; tick **"Also when moving by hand"** to wear it for typed moves too (`n`, `e`, `sw` … with nothing else running). A typed move has no arrival, so it comes off after **"Seconds without a move before going back to Default"** (default 10) — and a fight still swaps to Default the moment hostiles show up.
- **Bossing** — worn just **before you enter a room the Bosses table marks as a boss room**, so you fight the boss in dedicated gear; when you step out it reverts to **Default first**, then re-layers your **While Moving** set if it's enabled and you're still travelling. **Off by default.**

You don't create sets, you fill them. Pick a set on the left, then either click **Update from live** (fills it from what you're wearing) or type items into the **Item** boxes on the slot grid — each box only suggests gear your character can actually wear in that slot, and a blank slot means *{no change}* (left as-is). Click **Enable** so automation may use the set, and **Equip Now** to wear the selected set at once.

**Per-set behavior options.** Below the set list is a small options area that changes with the set you've selected. Select a **Pre-rest** set to see the **"Don't swap to default upon entering combat"** checkbox (per-character); select the **While Moving** set to see its **"Swap to default before entering lairs"** checkbox and its **"Also when moving by hand"** option with the seconds-without-a-move delay (both described above). Other sets show nothing there.

**Fighting a rest-interrupting mob in your Default gear** (the combat checkbox above) controls what you fight in when a hostile interrupts a rest:

- **Checked** (the default) — keeps the fight in your pre-rest loadout, reverting to Default only once recovered.
- **Unchecked** — fights in your combat gear: the moment a hostile enters while you're resting, MudPlay swaps to your **Default** set for the fight; once the room is clear, if you still haven't reached your rest-max HP and mana/kai, it swaps back to the pre-rest set and resumes the rest. (It only kicks in while you're actually mid-rest in a pre-rest set; a plain fight out on the loop is unaffected, since you're already in Default.)

The **Currently Equipped:** readout next to the Item Finder button names the last set the client put on this session — whether from Equip Now or an auto-fire trigger (loop start, pre-rest, recovery) — so you can see which loadout you're in at a glance.

The **Equipment Bonuses** panel shows the set's projected AC and stat totals. The projected AC assumes your **configured self-buffs are up** — it folds in the AC (and the Prot-Evil / Shadow / vile-ward effects) your buffs grant on top of the gear, and its tooltip breaks the total down by source (items, race/class/quests, buffs). "Configured buffs" here means everything that lands on you: self-only spells, whole-party buffs you keep on, and single-target buffs you cast on yourself.

**Unwearable items are flagged and skipped.** If a slot holds an item your character can't currently wear — its **alignment**, level, or class requirement isn't met — the slot's label turns **red** with a **⚠** marker, and the engine **skips that piece** on every swap instead of bonking the game with a wear it will refuse.

This matters most for **alignment**: MajorMUD force-removes an alignment-restricted item when your alignment drifts past its threshold (the "cleanup EP-zap"), and re-equipping it then fails (*"You may not wear that item!"* for armor, *"You may not use that weapon."* for a weapon). When that happens the client catches the refusal, blocks the slot, and prints a yellow terminal notice — `[<item> skipped, unable to wear — adjust set to correct]` — so you stop repeatedly failing on it.

**Change that slot's item** (pick something wearable, or clear it) to lift the block; if your alignment returns and the item becomes wearable again, an alignment-only flag clears on its own.

## Item Finder

The **Item Finder** button (in Equipment Manager) opens a searchable catalog of every equippable item, with columns for damage, AC, resists, stat bonuses, and more. Filter it by class, slot, level, or any stat, and sort by any column. A **Negates** column lists the spells an item cancels while worn, and the **Negates** dropdown in the stats filters lets you narrow to items that negate a particular spell (it's populated with every spell any item in the set negates; the default `(none)` doesn't filter).

It's a **reference tool**: double-click a row to see the item's full data record, and use the **Gear Finder** panel (with **Find Best**) to plan a loadout and read its projected stats. To actually equip something you found, note its name and type it into that slot's **Item** box back in Equipment Manager.

**Find Best searches whatever the results grid currently shows** — not the whole catalog. Leave every filter at its default and it searches everything; narrow the grid first and it searches only that. This is deliberate: a plate-capable class's "best AC" is plate almost by construction (nothing else comes close on raw AC), so without a way to narrow the search there'd be no way to ask for anything more specific.

Want the best AC available in **Leather** even though your class could wear Plate? Set **Armour Type** to Leather, pick **Armour Class** in the Find Best dropdown, and click it — only leather pieces are considered. The same applies to Slot, Weapon Type, Backstab-only, the name filter, and every stat-threshold filter.

- **Hold** a slot first to protect its current pick from the next Find Best pass, so you can layer several searches into one loadout (e.g. Find Best AC for armour slots, then switch the filter and Find Best again for the weapon).
- **Hovering an item inside the open dropdown** (not just the current pick) shows its full stat line, so you can see why Find Best chose something — or compare an alternative — without selecting it first.
- **The Find Best dropdown** covers every worn-stat column in the grid — AC/DR (flat, blur, and combined), Dodge, Magic Resist, ShockShield, VileWard, damage/accuracy (including backstab and the three martial-arts strikes), every attribute and regen, and every skill/resist/protection stat. (VileWard's magnitude is shown as the item's raw value — its actual AC effect scales with your own evil in a way the client doesn't model, so treat "higher" as "more VileWard on the item," not a guaranteed AC number.)

**Effective AC vs Evil** is a separate criterion from plain **Armour Class**: Prot-Evil is a confirmed 1 AC per point against evil monsters (most of what you'll fight), so an item with modest raw AC but a big Prot-Evil bonus can be the better pick even though plain AC sorting would rank it low — this criterion scores `AC + Prot-Evil` so that item shows up where it belongs.

Need more than one stat at once — "best VileWard, then AC, then Spellcasting"? Pick a criterion and click **+ Add to search order** to build a priority list (shown as "Search order: A → B → C" below the buttons); **Find Best** then resolves it highest-priority-first, filling each slot with whichever criterion earliest finds something for it — lower-priority criteria only get a turn at whatever's left over.

This is the same as manually **Hold**-ing a slot and re-running Find Best with a different criterion, automated into one click. **Clear order** empties the list, dropping back to searching by the single dropdown criterion.

The **Target weight** dropdown next to it caps what Find Best is willing to add: pick **None / Light / Medium / Heavy** and it stops picking items for a slot once the projected Gear Finder loadout's encumbrance would push past that band, using your character's live carry capacity — so "best AC" can mean "best AC that keeps me Light" instead of the raw-highest scorer regardless of what it weighs. **(Any)**, the default, is uncapped — the original behavior.

It only takes effect once your inventory has been read at least once this session (so the client knows your max carry weight); Hold locks, a search order, and the current filter/criterion still apply on top of it the same as always.

### Location-based auto-equip

Some gear only earns its slot in one place — a **feathered mask** across the whole Black Wastelands, say. The six gear sets swap on *moments* (moving, resting, a boss room), not on *where you are*, so **Settings → Other → Location-based auto-equip** fills that gap: it wears a named item while you're inside a map area and puts your normal gear back on the way out.

Add a rule and give it up to two criteria for **where**:

- **Map/room #s** — `16/153` matches that exact room; a bare `154` matches room 154 in any map. Comma- or space-separate several. Leave blank to match on name alone.
- **Room name contains** — a case-insensitive substring of the room title (e.g. `Black Wastelands`). Perfect for a whole zone that shares one room name. Leave blank to match on number alone.
- The **Match** dropdown between them decides how they combine when you fill in **both**: **Or** (either matches) or **And** (must be in the room number(s) *and* the name matches). With only one side filled, that side is used on its own.

Then name the **item to wear**. It goes on the moment you enter a matching room — **only if you're actually carrying it** — and the slot **reverts to whatever your current gear set holds there** when you leave. While you're in the area the Equipment Manager leaves that slot alone: a While Moving or Bossing swap won't knock the mask off. Because it's driven purely by room changes, it works the same whether you walked in by hand or a **loop / walk-to / Auto-Lair** carried you there. Uncheck a rule to park it without deleting it.

## CP Allocation

Plan how you'll spend character points as you level. **Add level** appends the next level's row; edit the **STR / INT / WIL / AGL / HEA / CHM** targets and the CP columns recompute live (a target that would overspend is clamped so **CP Left** never goes negative). At a trainer, **Apply this level** trains the selected row, or **Train now** walks to a trainer and trains the plan for you.

Three checkboxes here are the ONLY place the automation switches live: **Auto-train** (level up at trainers), **Auto-train stats** (apply this plan) and **Auto-train party** (train together with your party — see Settings → Auto-Trainer). They sit next to the plan they act on; Settings → Auto-Trainer holds the behaviour knobs (when to make the trip, what to keep banked, where to stop).

**Hover a stat's column header** to see everything that stat drives, one effect per line: the derived stat's **current value for your character**, its marginal rate (e.g. *~6 AGL → +1*, *+3 per 4*), and — where it's a discrete breakpoint — **the very next value of that stat where it ticks up** (`next at N`). That's the point of it: spend to a real breakpoint instead of guessing that every 5th or 10th point is a good stopping place.

The lists are complete and class-aware:

- **Health** — max HP (with the gain per point at your level) and HP regen (idle / resting).
- **Carry weight** — your current capacity and the per-point rate (steeper past 100 STR).
- **Casters** — **mana regen** and **spellcasting** under their actual casting stat (INT for Mages, WIL for Priests, both for Druids, CHM for Bards — max mana itself is level × magery, not a stat, so it isn't listed).
- **Accuracy** — follows the Stock vs Paradigm weighting.
- **Utility skills** — **Perception** always (every class has it), and **Thievery / Traps / Picklocks / Tracking** only when your class or race actually grants that skill, so you're never shown a breakpoint you can't use.

Values are the stat-and-level portion — your gear and quest bonuses stack on top in-game. See **What your stats do** below for the full picture; the projected numbers per level live in the **Level Projection** tab.

## Level Projection

A read-only what-if table: pick a level **from–to** range (and optionally any **Race / Class**) to see the exp, training cost, HP, and mana at each level — reflecting your CP Allocation plan.

Alongside HP and mana it also projects the **derived combat/utility stats** your CP plan grows — so you can watch a planned stat raise turn into real combat numbers, level by level:

- **Accuracy** (the normal-attack stat contribution), **Crit**, **Dodge**, **Stealth**;
- **Melee dmg** (STR's bonus onto your weapon's own damage range, shown as `+min/+max`);
- **Max enc** (carry weight), and **Magic res**.

The **HP/tick** column shows both rates as `idle / resting` (resting regen is 3× idle).

These figures reflect **your current character**: the base attributes carry your equipment's and completed quests' stat bonuses (the `stat` screen is already gear-inclusive), and the table folds your gear's and completed quests' **direct** bonuses on top too — extra max HP / max mana, HP- and MP-regen %, and flat +dodge / +crit / +stealth / +magic-resist / +damage / +carry / **+skill** from items. (Accuracy stays the stat-and-level contribution — a weapon's own accuracy is situational and can't be projected to future levels.)

Mark a quest **Complete** on the Quest Status tab and its bonuses flow in here automatically. **Reset to current** re-seeds it from your live character.

### Choosing which columns to show

**Columns ▾** opens a checklist of every column the table can show, and **your choice is saved per character** — each build keeps the columns it actually plans around. **Lvl** is always on (it's what labels the row); **Reset to defaults** forgets your choice and goes back to the built-in set, including any columns added in a later release.

Seven columns are **off by default**, because they only matter to some builds:

- **BS Accy** — backstab accuracy (see below).
- **Spellcast** — your spellcasting skill (`—` for non-casters and Mystics).
- **Percep** — Perception. Every class has it, and it's INT's biggest non-caster payoff.
- **Thievery**, **Traps**, **Picklocks**, **Tracking** — the four thief skills.

**BS Accy** projects your **backstab accuracy** per level. It reads `—` for a class and race with no stealth source, since that character can't backstab at all. Unlike the plain **Accy** column, this one folds in *everything* the game feeds it — level, stats, your gear and your completed quests — so it's a real number for your current loadout rather than a stat-only partial.

The trade-off: future levels assume **today's weapon**, so re-check it after a weapon swap. The two realms use genuinely different formulas (see *The exact formulas* below), and the client picks the right one from your active game-data set automatically.

The thief four are computed for whatever Race / Class the dropdowns are set to, so they're useful for previewing a rogue build — but a class that was never granted a skill has no score for it in-game. If they don't apply to you, leave them unchecked. (The CP Allocation tooltips are stricter: they only list a thief skill when **your** class or race actually grants it.)

A caution worth knowing: all four thief skills grow on a level term whose **slope halves at level 16**, so they climb quickly early and half as fast afterwards. Past that knee, stat points are what move them.

## What your stats do

Each of the six base stats feeds several derived numbers. The ratios below are the marginal rate (how many points buy one more of the derived stat); the exact breakpoints for *your* character are on the CP Allocation column tooltips.

- **Strength (STR)** — melee **damage** (adds to your weapon's own range: roughly +1 min damage per 10 STR above 100, +1 max per 10 above 50) and **carry weight** (+48 per point, steeper past 100). STR also feeds **accuracy** (~3/pt): on **Stock** for **all** attacks, on **Paradigm** for **bash / smash only** (normal Paradigm attacks get no STR accuracy).
- **Intellect (INT)** — **crit** rating (~10/pt), **stealth** (~8/pt), **magic resistance** (+1 per 4 INT), **perception** (+5 per 8 INT — the heaviest term in it), **all four thief skills**, and, for **Mages and Druids**, **mana regen + spellcasting**. On **Paradigm**, INT also feeds normal-attack **accuracy** (~6/pt); on **Stock** it does not. INT is the widest-reaching stat in the game — it's the only one that touches every utility skill as well as magic resistance, crit and mana.
- **Willpower (WIL)** — **magic resistance** (the heaviest term — resistance is `(INT + 3×WIL) / 4`, so +3 per 4 WIL), **perception** (+2 per 8 WIL), **tracking** (~8/pt), and, for **Priests and Druids**, **mana regen + spellcasting**. WIL does **not** raise your *maximum* mana (that's level × magery level); it scales how fast mana comes back. It feeds **no combat term at all** — not accuracy, damage, dodge or HP — so for a non-caster it buys only resistance, perception and tracking.
- **Agility (AGI)** — normal-attack **accuracy** (~6/pt on Stock, ~3/pt on Paradigm), **dodge** (~3/pt), **crit** (~20/pt), **stealth** (~4/pt), and **thievery / traps / picklocks**. Generally the most broadly useful combat stat.
- **Health (HEA)** — **max HP** (rises nearly every point, more per point the higher your level) and **HP regeneration** (idle, tripled while resting). Both scale with level. It feeds nothing else — no skill and no combat term.
- **Charm (CHM)** — **dodge** (~5/pt), **crit** (~30/pt), **stealth** (~6/pt), **perception** (+1 per 8), **traps** (~4/pt — CHM is weighted double there, the skill it moves fastest), **thievery** (~6/pt), **tracking** (~8/pt), and, for **Bards**, **mana regen**. On **Paradigm** it also feeds normal-attack **accuracy** (~10/pt).

**Mana regen scales off one stat per class.** Mage = INT, Priest = WIL, Druid = the average of INT and WIL, Bard = CHM (Mystics use a fixed Kai rate). Maximum mana is level × magery level regardless of stats.

**Realm accuracy differs, and by attack type.** On **Stock**, accuracy is driven by **STR + AGI** for every attack (INT and CHM don't affect accuracy at all). On **Paradigm** it splits by attack: a **normal** attack uses **AGI + INT + CHM**, while a **bash / smash** uses **STR + AGI** (INT and CHM don't help bash/smash). The tooltips label each accuracy line with the attacks it applies to, and the client uses the correct set for your realm automatically.

**Paradigm caveat.** The accuracy, dodge, stealth and damage ratios are verified for both realms. Crit's AGI term, carry-weight (encumbrance), and magic-resistance use the reverse-engineered **Stock** formula for both realms — they are **not independently verified for Paradigm**, so treat those three as close-but-unconfirmed there.

### The exact formulas

For the curious, here are the actual equations behind the numbers above, with everything that feeds them. These are the *base* (stat-and-level) values; your gear and completed-quest bonuses add on top in-game. Division drops the fraction (truncates) unless a formula says "round". `MinHits` is your class's per-level hit dice; `MageryLevel` is your class's magery level; `Level` is character level.

**Max HP** = `HEA/2 + Level×MinHits + (HEA−50)×Level/16 + per-level rolls + RaceHPPerLevel×Level` (+ gear `+MaxHP`). The per-level rolls are random, which is why the projection shows HP as a range.

**HP regen** (per tick) = `(Level+20)×HEA / divisor`, floored at 1, then **×3 while resting**, then **×(gearHPregen% + 100)/100**. `divisor` = **750 on Stock, 500 on Paradigm**.

**Max mana** = `MageryLevel×Level×2 + 6` (+ gear `+MaxMana`); 0 for non-casters. Mystics instead use **Kai = Level − 1**. Note this has *no stat term* — no attribute raises max mana.

**Mana regen** (per tick) = `(Level+20) × CastingStat × (MageryLevel+2) / 1650`, then the realm's regen-% step. `CastingStat` = **INT** (Mage), **WIL** (Priest), **(INT+WIL)/2** (Druid), **CHM** (Bard).

**Spellcasting** = `Level×2 + StatBlend + MageryLevel×5` (+ gear `+Spellcasting`). `StatBlend` = **(3×INT+WIL)/6** (Mage), **(3×WIL+INT)/6** (Priest), **(INT+WIL)/3** (Druid), **(3×CHM+WIL)/6** (Bard).

**Accuracy** (the stat contribution — a level/combat base, worn-weapon accuracy and encumbrance also apply but aren't stat-driven):
- **Stock**, every attack: `(STR−50)/3 + (AGI−50)/6`
- **Paradigm**, normal attack: `(AGI−50)/3 + (INT−50)/6 + (CHM−50)/10`
- **Paradigm**, bash / smash: `(STR−50)/3 + (AGI−50)/6`

**Backstab accuracy** splits hard by realm — these are two different equations, not one with a tweak:
- **Stock**: `(Stealth + AGI)/2 + gear+BSAccy/2`, then **+5** if your *class* grants stealth, or **−15** if only your race does.
- **Paradigm**: `Stealth/3 + (AGI − 50 + Level)/2 + 15 + gear+BSAccy + wornAccuracy`, then **−15** if your STR is below your weapon's requirement. Worn accuracy counts even when the STR check fails.

Encumbrance isn't applied on top — the Stealth value already carries it.

**Crit rating** = `clamp( Level/10 + (INT−50)/10 + (AGI−50)/20 + (CHM−50)/30, 1, 75 )`. *(AGI term unverified on Paradigm.)*

**Dodge** (raw value, before the vs-accuracy % conversion) = `Level/5 + (CHM−50)/5 + (AGI−50)/3` (+ gear `+Dodge`, + a bonus while under 33% encumbrance).

**Stealth** = `StealthLevel + 20 + stat terms`, where `StealthLevel = Level×2` at level ≤ 15, else `Level+15`. The stat terms differ by realm:
- **Stock**: `trunc(AGI/4) + trunc(INT/8) + trunc(CHM/6)` (each term truncated)
- **Paradigm**: `round(AGI/4 + INT/8 + CHM/6)` (summed, then rounded once)

**Max encumbrance** (carry weight) = `STR×48`, plus `STR×36 − 3600` once STR is above 100. *(Unverified on Paradigm.)*

**Magic resistance** = `(INT + 3×WIL) / 4`. *(Unverified on Paradigm.)*

**Melee damage bonus** (STR added onto the weapon's own min/max) = min `(STR−100)/10`, max `(STR−50)/10`, neither below 0.

**Perception** = `(INT×5 + WIL×2 + CHM) / 8` (+ gear `+Perception`). The only utility skill with **no level term** — it's pure stats, and every class has it. *(Unverified on Paradigm.)*

**The four thief skills** all share one level term, `LevelTerm = Level` below 16, else `15 + (Level−15)/2` — so **the level slope halves at 16**, and past that point stats are what move them. Each is a grant: a class or race that was never given the skill has no score for it. *(All unverified on Paradigm.)*
- **Thievery** = `(AGI + INT + CHM + LevelTerm×24) / 6`
- **Traps** = `(INT + AGI + CHM×2 + LevelTerm×28) / 7` — CHM counts double here
- **Picklocks** = `((AGI + INT + LevelTerm×10) × 2) / 7` — the doubling happens *before* the divide, so the effective divisor is 3.5
- **Tracking** = `(INT×2 + WIL + CHM + LevelTerm×40) / 8` — the heaviest level term of the four, so it grows mostly by levelling

## Quests, Bosses, and Deaths

### Quest Status

A journal of the realm's quests. Expand a card for its requirements, reward, and step checklist; tick every step (or the **Complete** box) to fold its permanent bonus into your character.

Inside a step, two kinds of token are **clickable**: a `(map/room)` coordinate (cyan) walks you there — through the same route picker as the map, so a room past a hazard, gate, or teleport offers its route choices instead of just failing — and a single-quoted `'command'` (green) is typed at the game for you, exactly as if you'd entered it in the terminal — so annotate a step with `'ask jorah transport'` and clicking it sends that line.

**Edit Quests…** lets you name, hide, or annotate them — and for the handful of quests that are class-locked in a way the crawler can't see (Magebane, Tarl), its **Restrict to classes** dropdown (a checklist of every class) pins the quest to the ticked class(es), so any other class is marked *Cannot complete*.

**Quests you can't complete — wrong class, race, or alignment, or a class restriction — are hidden from the journal by default;** tick **Show in quest journal** for one in the editor to keep it visible anyway (saved per character, since eligibility is per character).

The **Announce available quests** checkbox at the top (on by default, saved per character) prints `[<quest> Quest is Now Available]` to the terminal the moment you train past a quest's minimum level — including a several-level jump, which announces every quest whose gate you crossed — and dumps the full list of quests you can now start once you've entered the realm (after the stat/inventory/who sequence — never while you're still at the login menu). That dump only lists quests your class/race can do and that you haven't already completed, and never includes a cannot-complete quest.

Alignment quests are gated separately: the three **Evil / Neutral / Good** checkboxes on the second header row (off by default, saved per character) declare which alignment chain(s) you're committed to — an alignment-gated quest only counts as available when its matching box is ticked, since in-game you're locked to one alignment chain once you start it regardless of your live alignment.

**Auto-detect completed quests from your flags.** Turn on **Settings → General → "Auto-detect completed quests from flags on login"** (off by default) and, on your first login of the day, MudPlay reads your live quest-flag values and ticks **Complete** on any quest whose flag has reached its finished value — then sends the availability announce, so a quest you've already done drops off the "now available" list. It runs **at most once per day** per character, so relogging later the same day doesn't re-fire the flag-read burst; the next day's first login checks again. Turning the checkbox on **while you're already playing** fires the check right away if it hasn't run yet today (it does nothing if you flip it at the character-select menu):

A live flag read — that login check, `@quest update`, or `@quest <name|flag>` — also records how far you are through a quest you haven't finished: each quest step sets the flag to that step's number, so the checklist ticks every step up to the value read (a flag read of 7 ticks every step up to step 7). This works on the crawler's own checklist; a checklist you've rewritten in **Edit Quests…** keeps only your own ticks.

- **Multi-tier alignment quests** complete each tier at its own last flag value — so `128(17)` (Evil) reads tiers 1–4 done and tier 5 still in progress.
- **It only queries quests you can complete at your current level** (not every quest in the realm), and alignment "check" helper flags — internal turn-in markers nested inside an alignment quest — are never treated as quests. It only ever *marks* complete; it never un-ticks a quest, so your manual state is safe.
- **How it reads the flags** depends on the realm: on **Paradigm** it queries each not-yet-done quest's flag with `abil <flag>`; on **Stock** it uses the one-shot `sys god <name> abil`, which needs **sys-god powers** granted for the BBS (tick **Settings → BBS → "Sysop god lives"**) — without them the Stock sync is skipped.
- **The value it looks for** is derived per quest by the crawl that builds the journal, and is shown and **editable** in **Edit Quests… → "Completes at flag value"**: leave it blank to use the crawl's guess, or set one the crawl can't derive (a quest with no finished flag, or one — like Perfect Stealth — that completes at value 0). The **Game Data → Quest Flags** browser shows each flag reference's **Step** next to its name.

### Bosses

A respawn-timer tracker. **Mark** or **Now** stamps a boss's kill time and the **100%** column counts down to its respawn; on Paradigm the **-5% / -10% / -20%** columns count down to each early-spawn window, and on Stock a single **87.5%** column does the same. A **Last Killed** column shows when each boss's timer was last set; a **Clear** button in the Timer column wipes a running timer, and a **Notes** column holds your own per-boss annotations.

The tab **opens sorted by the 100% timer with running timers on top**, so a fresh open surfaces what's active — and it **re-sorts live whenever a timer starts or clears** (a marked or auto-captured kill), so a boss that just went active floats up into the running group without reopening the tab. Sorting by any timer column — or by **Boss**, **Respawn**, or **Last Killed** — groups cleanup spawns first, then bosses with a running timer, then idle ones. **Manage Bosses…** edits the list, and you can **Import / Export** a shared table. Tick **Stop before** to halt automation ahead of a boss.

**Double-click a boss** to walk to it. A boss with a **single** room walks there straight away; one with **several** rooms opens a picker listing them **nearest first**, where **Run** starts the walk and **Load** only arms the destination (so you can start it later, the same as a GOTO's Load). (Double-tapping the Stop-before / Grab-All checkboxes just toggles them — it never fires the walk.)

Tick **Grab All** (default off) to blindly grab a boss's loot the instant it's available — a "throw a get at everything" spray straight from game data, never a corpse scan. What it does depends on what the boss's name resolves to:

- a **monster** — the instant it dies, `get` every item in its drop table (one `get <item>` per item it could drop, percentages ignored); works for cleanup bosses too (no timer needed).
- an **item** that just sits in the room (a box, e.g. a bogwood box or Pastor Landor's box) — `get` it every time you **walk into** the room.
- **neither** (an unresolvable name — a touch-to-awaken mechanic like Iceforge) — Grab All doesn't apply, so its cell shows a muted dash reading *"Cannot resolve to a specific monster or item"*.

**Sync Timers…** shares respawn timers with other MudPlay users. Pick a channel (Gang, Telepath to a named player, or Local say) and click **Request Timers**; the client sends `@timer sync` on that channel, and any other MudPlay user who receives it (and grants the `@timer` remote command) replies with their active timers. Most arrivals need no action from you:

- a timer for a boss you track but have **no** timer for is **adopted automatically**.
- one that matches what you already hold is left alone (co-kills marked seconds apart count as the same timer).
- a genuine **conflict** — a boss where someone's timer disagrees with one you hold — shows your timer beside a **Keep ours** default plus a pick button per differing responder; choose whose to keep, then **Apply Selected**.
- a boss a responder tracks that **you don't** also asks — adopting it **adds the boss back to your list** (recovering a catalog boss you'd removed).

Bosses are matched by the monster itself (not by room), so it works even if you and they pinned different rooms. You don't have to open the tab first: sending `@timer sync` by hand — a telepath (`/name @timer sync`), a gang broadcast (`bg @timer sync`), or a say (`.@timer sync`) — **auto-opens this merge window** and starts collecting.

### Chest Offload

A helper for cashing in boss chests (on the Bosses tab). It snapshots your carried inventory, lists the containers you're holding (click one to `open` it), then — after re-reading your inventory — shows every new item, grouped into the fewest shops. **Coin gained** is measured for each open on its own (the coins you held just before that chest versus just after, so money from selling loot never counts as chest coin) and shown by denomination, most-valuable first.

- **Pricing and selling** — each item has an editable sell quantity (keep some, sell the rest) priced at a **charm** picker, its own **Sell** button, and a **Drop** button that drops that one item's **whole held stack**. Both act while you're standing in the shop, and the list reconciles against the game's own confirmations: the row shrinks (and clears at zero) only when the `You sold …` / `You dropped …` actually lands. A **Total to sell** figure sums everything selected across all shops.
- **Picking a shop** — when an item is sold by more than one shop it gets a **⇄** button. The plan already assigns it to whichever shop keeps the trip to the fewest counters, but click **⇄** to see the other shops that buy it (each with name, map/room, and current walking distance), pick one, and hit **Change** to move it there.
- **The selling trip** — each shop header shows a running total and **walks you there** when clicked. It carries a **Sell All** button that fires the sell commands for every item in the group (batched on Paradigm), plus a **Drop All** button. The shops are ordered into a short nearest-first trip using the same routing the walker uses (respecting avoid rooms, usable teleport gates, and item/hazard/boat gates).

### Death Recovery

Your death history. **How did I Die?** replays the backscroll from the moment of death, and **Recover Now** walks to the death room and grabs the pile (or toggle **Auto-Recover Deathpiles** to do it automatically).

Recovery matches your realm: on **Paradigm** it recovers your `corpse` in one command; on **Stock**, where death scatters your items loose on the floor (and can overflow into adjacent rooms), it `get`s each item back.

With **Auto-Equip on recovery** on, MudPlay re-wears everything you had on when you died — and if a hostile is in the room when the pile comes back, it does this **combat-aware**: grabbing the pile doesn't interrupt the fight, but wearing gear does, so it puts a few pieces on between combat rounds (weapon first, then armour heaviest-first) and keeps swinging in between, then equips whatever's left the moment the room clears.

On **Stock**, items that spilled into neighbouring rooms are chased down too:

- A *deliberate* recovery — **Recover Now**, or an Auto-Recover walk-to that **ends** in the death room — looks through each exit, then walks to the rooms holding your items (disarming any traps in the way, and skipping a direction whose trap it can't get through), grabs your gear, and returns.
- An Auto-Recover walk that simply **passes through** a death room grabs your overflow from the rooms right before and after it in passing.
- Just *manually* stepping into one of your death rooms grabs whatever's on that floor but never fires the adjacent-room sweep.

Finally, if the only thing left un-recovered is **currency**, the death counts as fully recovered: coins are picked up as cash automatically (never `get`-ed), so they never leave a pile stuck at "partly recovered".

## Character Info and Calculators

**Character Info** is your read-only character sheet — stats, skills, the attack table (per attack type: accuracy, damage range, and swings per round, computed from your stats and equipped weapon), and folded-in quest bonuses. It also lists your worn, carried, and key-ring inventory, each a clickable link to its Game Data record (an item whose dumped name didn't resolve stays plain text). A **limited-use item shows its remaining charges** next to it (e.g. *token of Silvermere - 5 Charges*). How the count is known depends on the realm:

- **Paradigm** prints "Uses remaining: N" when you `look` an item, so the count is read straight from that — the authoritative source. The client fills the readout in for you: any charged item you hold — **carried, worn, or on the key-ring** — whose count it doesn't know yet gets **looked automatically**, and it **re-looks after a use** (yours or one a party member remotes to you) to reconcile — so a use that's *blocked* (an NPC in the room, too little gold, too low a level) never wrongly drops the count. Transport tokens fill in the same way (they're looked on login). For a **stack** of the same item, the readout follows the **top-of-stack** copy (the one a `look` reports) — re-read whenever the top is used up or dropped and the next copy surfaces.
- **Stock** prints no charge line, so the client **counts your successful uses** and shows remaining = the item's max charges minus what you've spent. Only uses that actually fire count: a use is confirmed by the item's cast message, so a *bonked* one (sent between rounds) burns nothing. **Rechargeable** items (the align-quest cloaks, and tokens where they exist) restock to full at your BBS's configured **cleanup time** (Settings → BBS); **finite** items (the gnarled / teak / mahogany wands) stay spent. Infinite-use items (e.g. the nexus spear on stock) show no charge line. For a **stack**, once the top copy empties the next is **assumed full** (a fresh drop is max charges) — a guess, since a partly-used copy picked off the ground would start lower; stock has no charge line to confirm it.

On both realms these counts are **saved per character**, so they survive a restart — and a rechargeable item is assumed back to full once your BBS's cleanup time has passed, without needing to look at it again. The same figures back the `@uses` remote query.

Below the wealth block it shows an **AC / DR breakdown** in two lines: one for what your worn gear grants, and one for what your **configured self-buffs** add on top (assuming they're up) — the same buff figure the Equipment Manager and Monster Intel use.

**Calculators** holds what-if tools: the Hit Calculator, Swing and Backstab calculators, Movement Speed, Mana Regen, Realm Rankings, and Monster Aggro. The **Hit Calculator** projects your hit% and damage against a monster with your current weapon; for the reverse — how often a monster hits *you*, and whether it's safe to fight — see **Monster Intel**.

### Monster Aggro

**Monster Aggro** predicts which member of your party a monster will attack — the same target-selection the game engine runs. It shows the model for the **loaded game-data set's realm** automatically (the Paradigm version on a Paradigm / GreaterMUD set, the Stock version on a Stock set — the two engines are completely different). Configure up to **six** party members with **＋ Add member** / **✕ remove**.

**On Paradigm** each member is scored from a **150 base**, adjusted by:

- **Charm** — higher charm lowers your score, so mobs notice you less;
- **party position** — set for you where it isn't a choice: the first member is your point man (**Solo** when they're the only one — a lone player is as exposed as a frontliner — **Frontrank** once there's a party), and every added member defaults to **Midrank**, which you can change;
- **recent aggro** — tick *Last hit* on whoever swung at the mob most recently: a big bonus that scales with party size, while everyone else takes a small penalty.

The monster rolls a **weighted lottery** over the scores, so each member's **Odds** is their share of being picked — bigger score, bigger slice, but never a guarantee. No monster is needed; the odds are the same for any mob.

**On Stock** it's a different engine, so you pick the **monster**: type its record **number or name** (best match) and it fills in the matched **#/name**, its **Align** (shown as a label — it comes from the record), **Follow%**, and whether it's a **guard** (Follow% and guard stay editable). Each member sets their **alignment title**, whether they've **provoked** the mob (hit it first — forces it to aggro them), whether they **hit it last**, and how many **hits** they're already taking this beat. Per member the result shows:

- **Opens?** — whether the monster is hostile to them unprovoked, from its alignment vs theirs: evil / chaotic-evil / neutral-evil mobs open on everyone, lawful-evil spares the evil-titled, good / neutral / lawful-good open on no one, and guards attack Outlaw-or-worse titles. Hover for the reason.
- **Target%** — for members it's aggroed on, their chance of being *this beat's* target. Stock mobs spread away from whoever's already being piled on (each incoming hit lowers the odds), so a tank soaking hits pulls fire off the rest. Mark a member **Last hit** and the mob re-locks onto them **Follow%** of the time (the "attack last" behaviour), the rest re-spreading across the party.
- **Follow% stickiness** — how tightly the mob holds one target before re-spreading (a high-Follow% mob is hard to peel; a passive-aligned mob you provoked never lets go).

Reach it from the Calculators tab, or wire it to the terminal right-click menu / a toolbar deep-link like any calculator.

## Roomba

The Workshop's last tab, **Roomba**, automates sorting gang-house loot into labelled rooms and backs a shared item-location log you can query in-game with `@roomba`. It's involved enough to have its own writeup — see the **Roomba (Player Workshop)** section further down for the full walkthrough.

---

# Automation

MudPlay's automation is a set of independent engines you switch on and off — combat, healing, spells, pickup, movement, and more.

## The auto-engines

Each engine — Auto-Combat, Auto-Nuke, Auto-Heal/Rest, Auto-Bless, Auto-Light, Auto-Get Items, Auto-Get Cash, Auto-Sneak, Auto-Hide, Auto-Search — is an independent on/off switch. Your primary surface for them during play is the **Action menu** in the menu bar (the toolbar can also carry each as a button — add them in Settings → Toolbar + Shortcuts).

An engine only acts while it's on, and each has a matching Settings tab for its behavior. Some gate others: Auto-Combat, for example, gates the combat/spell tuning. But **Auto-Bless stands alone** — self and party buffing is controlled by the Auto-Bless toggle and nothing else, so turning off Auto-Combat or Auto-Rest/Heal never stops your blessing.

**Sneak-aware casting.** Casting a spell breaks Sneak (and Hide), so when **Auto-Sneak is on** MudPlay times its maintenance casts around your stealth:

- After any automated cast it **re-issues sneak in place**, so you don't walk on exposed.
- While sneaking with **Auto-Combat off** — slipping past rooms rather than fighting them — a due buff, cure, or top-off heal is **held until you reach an empty room** (no monsters, so the cast can be followed by a clean re-sneak) instead of stripping sneak in a room you're only passing through.
- While sneaking with **Auto-Combat *and* Backstab on**, a maintenance cast is likewise **held until you've thrown your backstab opener** (or the room is empty). `bs` has to be your first move from stealth, so letting a buff go out first would break sneak, forfeit the surprise round, *and* leave you mid-cast in a hostile room. Once the opener lands the hold lifts and buffing resumes for the rest of the fight. (Without Backstab, an engaging fight casts normally — you're trading blows either way, so there's no surprise round to protect.)
- If a **see-hidden** monster forces a fight (with *Clear hostiles when sneak broken by see-hidden monster* on), the now-cleared room becomes that opportunity: the held casts fire there, you re-sneak, and the walk continues.

Emergency survival is never held — a life-threatening heal, a flee, or an emergency hangup always fires immediately. Turn Auto-Sneak **off** and this all stops: casts simply go out on schedule, wherever you are.

## Manual one-shots and Reset States

The **Action menu** also carries commands you fire once, on demand, rather than leaving running:

- **Get All / Drop All / Equip All / Deposit All** — pick up everything on the floor, drop everything unworn, wear your Default gear set, or bank your wealth down to the keep-on-hand floor, right now. (These are the local twins of the `@get-all` / `@drop-all` / `@deposit-all` remote commands, and the toolbar Get / Drop / Equip / Deposit buttons drive the same actions.)
- **Reset States** — the recovery escape hatch. Clears your own stuck ailments, waits, and movement holds **and every party member's ailment chips** (blind / poison / disease / confuse / held), returning you to an idle state — reach for it when an engine looks wedged (e.g. the walker parked "held" or "waiting" with nothing actually happening) or a party row is stuck showing a condition that's already gone. It also **re-equips your Default gear set** (undoing a stuck Pre-rest swap) and **re-polls `health`** — the game's compact one-line HP/pool readout, far less scroll than the full stat screen — so a drifted max HP/mana snaps back to the real value. (Typing `health` yourself re-anchors the same way.) It's also on the terminal's right-click menu.

## Base modes

The Settings → General **"Auto-Engines base modes"** checkboxes are your character's default engine states. The live toolbar settles to them **every time you load the character** — so a character always comes up in its configured defaults, not in whatever transient state the last session happened to end in — and the toggles also **snap back to them at the start of a loop or Auto-Lair**. So you can flip combat off to travel somewhere and it returns to your defaults when the circuit begins (or next time you load the character).

(A character created before these checkboxes existed adopts its current live modes as its base the first time it loads, so nothing changes until you edit the boxes.)

## The kill switch

The **All auto-responses** toggle at the top of the Action menu (and the `@auto-all` remote command) flips every engine off in one press, remembering what was on so a second press restores it — a fast "stop everything" that doesn't lose your setup. While it's off, auto-entry to the game is gated too, and **all movement is frozen** — a walk, loop, auto-lair, or a right-click Queue-walk-to will plan but hold until you turn Auto-All back on (then it resumes where it left off). Your own manual Pause/Resume is untouched by this.

## Macros, aliases, and triggers

Beyond the engines, you can script your own automation. All three editors live in the **Game Data Browser** — press **F3** (or use View → **Macros** / **Triggers** / **Aliases** to jump straight to one) and pick **Macros**, **Triggers**, or **Aliases** from the *Tables + editors* list on the left.

Each shows the same surface: a **Filter…** box, an **Add** button, a **Remove** button, and a grid of what you've already made. **Double-click a row to edit it.** There's no separate save step — each editor's **Save** button writes to disk immediately, and the list's **Enabled** column shows a ✓ for the ones that are live.

- **Macros** bind a **key chord to a command.** Click **Add**, press **Capture** and hit the key combo (release the main key to lock it in; click **Capture** again to abort), then type the **Command** to send. Split it into several lines with `^M` or `;` — each fragment fires as its own command. Macros work while you're typing in the terminal; new profiles start with the numpad pre-wired to compass movement. **Esc is a bindable key** — you can put it on a macro or a shortcut; an unbound Esc still passes through to the game as usual.
- **Aliases** expand a **typed word into a longer command** — a shorthand you invent, so `cast heal bob` can send `c 'heal' bob`. See **Writing an alias** just below for a full walkthrough.
- **Triggers** are **auto-responses to game text** — when a line matches, MudPlay fires a reply. Give the trigger a **Name**, then set:
  - **Location** — *Game data* (saves with the active game-data set, so it travels with the realm — and every other client you have open on that set picks the change up within a moment; if a client can't read the file at that moment, it keeps its current list and retries, and it won't save game-data triggers until the read succeeds, so one client can never wipe the others' triggers) or *Profile* (saves with this character).
  - **Scope** — which incoming lines it watches: *Game messages* (the default), a single chat channel (*Say / Yell / Gossip / Telepath / Gangpath / Broadcast*), *Chat (any)*, or the *System log*.
  - **Match type** — *Literal* (type the text as it appears; `*` wildcards a span and `{name}` — or a numbered `{1}`, `{2}` — captures a piece) or *Regex* (full .NET regex, with `(?<name>…)` for captures).
  - **Pattern** — the text or expression to match against each line. Any pieces you capture appear in the **Captures** row.
  - **Response** — what MudPlay sends back on a match. Drop a captured value in with `{name}` (or `{1}`, `{2}`).

    To send **several commands**, put each on its own line in the box (the Response box accepts Enter) — every line is sent as a separate command, each with its own Enter. `^M` and `;` do the same thing on a single line, so `north;get all;south` is three commands too.

    Leave the box blank to send a bare Enter.
  - **Sound** (optional) — a file picker is here, but sound playback isn't wired up yet, so it does nothing today.

### Writing an alias

An **alias** is a typed shortcut: the **first word** you type is the alias *name*, and MudPlay swaps the whole line for the alias's **expansion** before sending it to the game. The rest of what you typed is handed to the expansion through numbered slots, so one short word can stand in for a long or awkward command.

**Make one:** Game Data Browser → **Aliases** → **Add**. Fill in two fields:

- **Name** — the word you'll type. Matched on the **first word only**, **case-insensitive**, as plain text (no wildcards). A name that would collide with a game chat command (`gos`, `yell`, a `/name` telepath, …) is rejected as you type, so an alias can never hijack your own chat.
- **Expansion** — what actually gets sent. Drop the words you typed into it with numbered slots:
  - `{0}` — **everything** you typed after the name, as one piece.
  - `{1}`, `{2}`, `{3}`, … — the **individual words** after the name, split on spaces.
  - A slot you don't type stays empty (so a trailing `{2}` with nothing to fill it just vanishes).

**Send several commands from one alias:** split the expansion with `;` or `^M` — each piece is sent as its own command, in order. So an alias `bs` → `sneak;backstab {1}` sends two commands.

**Worked examples** (you type → what's sent):

- Name `cast`, expansion `c '{1}' {2}` → `cast heal bob` sends `c 'heal' bob`.
- Name `k`, expansion `attack {0}` → `k big ugly troll` sends `attack big ugly troll` (`{0}` keeps the whole target name together).
- Name `gt`, expansion `gossip Heading to {0} — come along!` → `gt the docks` sends `gossip Heading to the docks — come along!`.
- Name `bs`, expansion `sneak;backstab {1}` → `bs orc` sends `sneak` then `backstab orc`.

**Where aliases expand:** only when you press **Enter in the Conversation window's input box**. Typing directly in the main terminal sends your keystrokes straight to the game, so aliases don't expand there — use the Conversation input for them. Aliases and macros are separate: a **macro** binds a *key* to a command in the terminal; an **alias** rewrites a *typed word* in the Conversation box. (Alias slots are also unrelated to trigger wildcards — see the note under the trigger examples below.)

### Writing a match pattern

**Literal** patterns match the text as it appears on the line. Two shortcuts make them flexible:

- `*` matches any run of characters — `You are hit by *` matches whatever follows.
- `{name}` captures a piece for the Response — `{attacker} hits you` captures the attacker's name, and you use it back as `{attacker}`. **Numbered wildcards** work too: `{1} telepaths: &@{2}` captures the sender into `{1}` and the message into `{2}`, and a Response of `/{1} @{2}` telepaths them back. Any run of letters, digits, or underscores is a valid name.

These captured values are the trigger system's **wildcards**, and they belong to triggers alone — they're never shared with aliases (whose own `{1}`/`{2}` mean the tokens you typed) or macros. The **Wildcards** button at the top of the Triggers table opens a live viewer of every wildcard captured this session and what each currently holds; **Clear all** empties it. The store also clears when you close MudPlay.

**Regex** patterns are full .NET regular expressions, for when a literal pattern can't say what you mean. The essentials:

- **Ordinary letters and spaces match themselves.** The characters `. * + ? ( ) [ ] { } ^ $ | \` are special — put a `\` in front to match one literally (`\.` matches a real dot).
- **Character shorthands:** `.` = any one character, `\d` = a digit, `\w` = a letter/digit/underscore, `\s` = a space. A set in brackets matches any one of its members — `[nsew]` matches a single compass letter.
- **Repetition:** `+` = one or more, `*` = zero or more, `?` = optional (zero or one). So `\d+` matches a number of any length, and `.*` matches any span (the regex twin of literal `*`).
- **Anchors:** `^` ties the match to the start of the line, `$` to the end — `^You gain \d+ experience\.$` matches only a whole exp line, nothing that merely contains one.
- **Captures:** wrap a piece in `(?<name>…)` to pull it out for the Response. `^(?<who>\w+) tells you '(?<msg>.*)'$` captures **who** and **msg** from a telepath; a Response of `reply {who} — got: {msg}` sends them back.

The **Captures** row lists every name your pattern defines, and the status line under the Pattern box turns **red** with the reason if the expression doesn't compile — so you can tell a typo from a valid pattern before you save.

---

# Game Data

MudPlay's automation reads from **game data** — the monster, item, spell, room, and shop tables imported from a MajorMUD `.MDB` database. The **Game Data Browser** (press **F3**, or the toolbar's *Game Data Browser* button) lets you inspect all of it and override individual records for your character.

MudPlay also ships **built-in defaults** for the automation-facing bits (a monster's default relationship/priority, item auto-flags, the message catalogue, boss and quest lists). These are baked into the program, so **updating the app refreshes them automatically** on the next launch — a shipped fix reaches you just by running the new version. Your own edits are never lost: overrides you make in the Browser (and your per-set message edits) resolve *above* the defaults, so they keep winning; and your custom **triggers** are left completely alone.

The same applies to the **starter navigation loops and GOTO favourites** that come bundled with each set: they're baked into the program too, so **new ones added in a later release are added to your existing sets on the next launch** — added only, never overwriting a loop or favourite you already have, and **never re-adding one you deleted** (MudPlay remembers what it has already offered each set). Your own loops and favourites are always left untouched.

## Importing and switching sets

The top **Game Data** menu (in the menu bar) manages your data sets:

- **Import .mdb…** — pick a MajorMUD `.MDB` file; MudPlay imports it as a new named set and switches to it. This populates the tables the engines read from — the terminal itself works without it. If you import after launch, the startup splash is dismissed so the import's progress and any errors show on the terminal.
  - **"No game tables found"** means the MDB's internal catalog is damaged — usually from being opened and edited in Microsoft Access without a *Compact and Repair* afterward, which detaches the game tables from the database's object list. MudPlay won't switch to an empty set; to fix it, run Access's **Database Tools → Compact and Repair Database**, or re-export a fresh MDB from Nightmare Redux, then import again. (The Program Log records the catalog scan so you can confirm what the database reported.)
  - **Each table is verified on write** — re-read after writing and retried once if it didn't come back as valid JSON, so a truncated or interrupted write is caught during the import. A table that still can't be read (or one already corrupt from an older import) is reported as **unavailable** on the terminal in red rather than crashing, and the engines that rely on it stay missing data until you re-import.
- **The set list** — every imported set appears at the top of the menu with a checkmark on the active one; click another to switch. The Browser's status bar shows *Set: <name>*.
- **Import loops (MegaMUD .mp)…** — pull loops out of a MegaMUD `.mp` file into the active set, so a circuit you already built in MegaMUD comes across without re-walking it.
- **Manage Game Data…** — copy or move a set's saved loops and lairs into another set, or delete a set.
- **Modify Blacklist…** — hide specific rooms (by map/room number) from the map and room search, and mark ones the walker should treat as unreachable. You can also blacklist a room straight off the map — **right-click it → Add this room to Blacklist**. A room blacklisted from the map stays drawn (and selected) until you click a **different** room, so you can confirm you hid the right one before it disappears — handy for pruning rooms that aren't really reachable or that you'd rather not see on the map or in the search box.
- **Modify avoid/stash rooms…** — a staged editor over your character's **avoid rooms** and **stash rooms** together. Each row is tagged by type (*Avoid Room* / *Stash Room*) with its map/room number and name. Avoid rooms are your personal no-go list — the walker, loops, and auto-lair route around them; stash rooms are the drop-off points the cash/item engines use. Quick-add a room by picking a type, typing its map and room number (the name fills in from the active set), and clicking **Add room**; select one or more rows and **Remove selected** to clear them. **Save** commits every change and redraws the map; **Cancel** or the title-bar X discards. (You can still mark either kind straight off the map with a right-click — this editor is for reviewing and bulk-editing the whole list.) The two sets are independent, so a room flagged as both appears once per type.

## Getting around the Browser

The window is a sidebar plus a content pane:

- The sidebar's **Search…** box filters the **section list**, not the rows — type "weapon" and unrelated sections drop away.
- **Tables + editors** (top group) holds what you build: **Players, Macros, Triggers, Aliases, Incomplete Messages, Unrecognized Lines, Flavor Prefixes**. (The macro/alias/trigger editors are covered in the **Macros, aliases, and triggers** section; Flavor Prefixes has its own note below.)
- **Imported tables** (bottom group) holds the game data: **Monsters, Items, Spells, Rooms, Lairs, Shops, Races, Classes, TextBlocks, Info, Unobtainable, Quest Flags.**

Click a section to open it. Each table has its own **Filter…** box (this one filters *rows*), sortable and resizable columns, and a row-count line at the bottom. The box matches the **visible cell text** across every column (including the friendly labels), and several tabs accept **special filter words** on top of that — the full list is under **Filtering a table**, below.

The rightmost **Use** column shows which tier owns each row — **Def** for the untouched import, or **Glob / BBS / Char** once you've overridden it.

The **Items** and **Players** tables carry a **Toggles** column that lists, per row, the flags *you've* turned on for it — an item's **Collect / Discard / Open / Buy / Sell / Stash** (plus **No-take / Keep-min / Loyal / Path-get**), or a player's **Invite-if-seen / Join-if-invited / Don't-delete** followed by each **remote-control permission** you've granted them (a full grant collapses to *All @-permissions*). It reads blank when you've set none; a crowded cell trims with an ellipsis — hover it for the full list, or drag the column wider. (The Players tab's separate **@'s** column keeps the quick None / Some / All summary of those permissions.) The **Monsters** table surfaces the same kind of per-record settings, but as their own columns — see its column list below.

The **Monsters** table carries a full column set for browsing and filtering monster stats:

- **Landmass**, **Region**, **Area** (right after the name) — where the monster lives, as the hierarchy Landmass → Region → Area (for example *Mainland → Volcano → Infernal Cavern*). The Paradigm set ships them filled in for every monster that has a spawn room; the monster's most common place wins when it appears in several. They are labels only — nothing in combat or navigation reads them — and they are searchable in the Filter… box. A monster with nothing set reads blank; fill it in from its record (below).
- **Relationship** — how *your* overlay tells the engine to treat this monster: **Enemy** / **Neutral** / **Friend** / **Flee** / **Hangup**, resolved across all four tiers just like the combat engine reads it, so an un-tagged monster shows **Enemy** and any relationship you or a shipped default set shows through here without opening the record.
- **Priority** — your attack-priority for this species (**First / High / Normal / Low / Last**), resolved across all four tiers like Relationship; **Kill-on-sight** and **No Backstab** — each reads **✓** when you've set that per-monster flag, blank otherwise.
- **Respawn** (respawn timer), **Exp** (experience per kill — base × multiplier), **HP**, **AC/DR**, **Dodge**, **Magic Res**.
- **Acc (typ/max)** (typical/highest attack accuracy), **Damage**, **Exp Eff** (an exp-per-effort efficiency score).
- **Lair Exp**, **# Lairs**, **Avg Lair Size**, **Biggest Lair**.
- **Mag-wpn req** (the HitMagic level a weapon must meet to land a hit), and **Undead**.

Every game-data record table — Monsters, Items, Spells, Rooms, Classes, Races, Lairs, Shops, and the like — has a **Columns ▾** button at its **top-right**: a picker to check/uncheck which columns show, so you can tailor each table to just the stats you care about. (The engine-backed utility tabs — Macros, Triggers, Aliases, Players, Incomplete Messages, Unrecognized Lines, Flavor Prefixes — keep their fixed columns, so they have no picker.)

It also surfaces columns otherwise only used by the filter sidebar: on the Monsters tab, for instance, you can turn on the per-element resist columns (Cold / Fire / Stone / Lightning / Water), spell-immunity, and more, to *see* them in the grid instead of only filtering by them. Your choices are saved **per character**, per table; **Reset to defaults** in the picker restores that table's standard columns.

On the **Monsters**, **Items**, and **Spells** tables you can also **rearrange the columns**: drag a column header left or right and drop it where you want it. The order is saved per character alongside the visible set, so it's how the table opens next time; a column you switch on afterwards joins at the right-hand end, and **Reset to defaults** puts the standard order back. The **Use** tier column always stays last.

It also carries a **filter sidebar** on the right — drag its left edge to resize it — that **curates** which monsters are in the list. Edit the boxes, then press **Apply** to run them (a deliberate step, so a half-typed range never re-filters mid-edit); **Reset** clears every filter and the search box at once. It's split into labelled sections, all AND'd together:

- **Combat** — Exp, HP, Avg damage, Accuracy, Armour Class, Damage Resist, Dodge, Magic Resist.
- **Elemental defenses** — Cold / Fire / Stone / Lightning / Water resist %. These are **signed**: a *negative* resist means the monster is **vulnerable** (takes extra of that element), so bracket the max at −1 to find things a given element shreds.
- **Casting & immunity** — Magic-weapon requirement, Spell immunity level, and a **Casts spells** toggle.
- **Type & alignment** — Type (Solo / Leader / Follower / Stationary) and Alignment dropdowns, plus **Undead**, **Animal**, and **Non-living** checkboxes.
- **Loot & lairs** — a **Drops an item** toggle, and Lair Exp / # Lairs / Respawn ranges.

Every numeric filter is a **min / max range** — either box can be blank for no limit on that side, so `HP 500–2000` brackets a band, `AC ≤ 20` finds easy kills, and a lone minimum works like the old "at least N". Hover any label for what the stat means. **Reset** (top-right of the panel) clears every filter and the search box at once. The **Filter…** text box at the top is separate: it **finds** a specific monster within the curated list, while the sidebar decides which monsters are in it.

## Filtering a table

Every table's **Filter…** box (top-left) *finds* rows in the current list as you type — it's live, no Enter needed, and clearing it shows everything again. By default it matches the **visible cell text** across **every** column, including the friendly label a formatter renders — so on the Items tab `Weapon`, `Plate`, or `Feet` match the type / armour / slot columns, and `Lawful Good` matches an alignment, not just the raw code behind it. On every imported table it also matches the **Use-tier badge**, so typing `Char`, `BBS`, `Glob`, or `Def` lists just the rows owned by that tier — a fast way to see only the records you've overridden.

Some tabs understand **special filter words** beyond that plain-text match:

| Tab | Type… | …to show |
|---|---|---|
| **Items** | `get` or `collect` · `drop` or `discard` · `open` · `buy` · `sell` · `stash` · `keep` · `loyal` · `notake` · `path` | only the items you've set that **auto-toggle** on (the flags in the Toggles column). Exact-word match, so `get` filters by the flag, not by names containing "get". |
| **Items** | `weapon`, `feet`, `plate`, … | items of that item type / worn slot / weapon or armour type (any of the formatted labels works) |
| **Spells** | `poison` · `confuse` · `blind` · `hold` | every spell that **applies** that ailment — read from the spell's ability codes (following the EndCast chain), not just spells with the word in their name |
| **Rooms** | `1,1` (also `1/1` or `1 1`) | the single room at that **map,room** coordinate |
| **Any imported table** | `Def` · `Glob` · `BBS` · `Char` | rows whose current values come from that **tier** (the Use column) |

Anything the box doesn't recognise as a special word falls back to the plain substring match, so names always work too. The **Monsters** tab additionally has a full **filter sidebar** — min/max stat ranges plus flag and type/alignment toggles that *curate* which monsters are listed, described just above; its Filter… box then finds a specific monster within that curated list.

## Overriding a record

**Double-click a row to open it** — what happens depends on the table:

- **Items** and **Monsters** — open a real **override editor** (detailed below).
- **Spells** — double-click edits the spell's player-cast **message** wording; the spell's own stats are read-only (detailed below).
- **Incomplete Messages** — the messages worklist that still needs attention (detailed below).
- **Rooms** — double-click opens the **Navigation map** on that room and selects it, so its details (exits, lighting, shop, monsters, room commands) show in the map's **ROOM INFO** panel (see *Navigation*).
- **Shops** — double-click opens the room-detail popup for the shop's room directly, showing the stock table with its live **Charm** picker. A shop that spans several rooms opens on the first and lists the others as clickable links in brackets next to the popup's title — click one to hop the popup to that room.
- The rest (Lairs, Races, Classes, and so on) are read-only reference.

### Batch edit (Monsters, Items, Players)

Select several rows (click-drag, or Ctrl / Shift-click) on the **Monsters**, **Items**, or **Players** table and a **Batch edit** button appears in the toolbar, between the **Filter…** box and the **Columns ▾** picker — it shows the count and lights up once **two or more** rows are selected. It opens a dialog that applies your chosen fields to **every** selected record at once.

- **Opt-in per field** — a field is only touched when you set it. Enum / text / number fields (Relationship, priority, Min-to-keep, …) have a **Change** checkbox; flags and permissions are a tri-state **Leave / On / Off** (for a player permission, On = grant, Off = revoke). Anything left **Leave** / unticked keeps whatever each record already has, so batching one field never clobbers a record's other overrides.
- **Monsters** — Relationship, attack priority, don't-backstab, kill-on-sight, the physical-attack command, and the three spell-override rungs (cast-code + Max + Mana floor).
- **Items** — the auto flags (collect / discard / open / buy / sell / stash), cannot-be-taken, must-have-minimum, loyal, auto-obtain-for-path, and Min-to-keep / Max-to-get.
- **Players** — the party behaviours (invite-if-seen, join-if-invited, don't-auto-delete) and all 16 remote-control permissions, with a **Set all permissions** master to grant or revoke the lot in one move. Under **Elevated Commands** it also shows whether that player's one-time `@dupe` has been spent, with a **Reset @dupe** button to re-arm it.
- **Tier** — Monsters and Items write to the tier you pick in the dialog's **Use** dropdown (only-this-character / only-this-BBS / for-all-characters), the same as the single editor; picking **Installed defaults** instead **resets** every selected record (after one confirm). Player permissions save to the character, no tier picker.

### The item / monster override editor

Items and Monsters open an editable pane on the left with the read-only **Other Info (from MDB)** on the right.

**For an item** you can flip its automation flags (**Auto-collect, Auto-discard, Auto-buy, Auto-sell, Auto-stash**, and more), set **Min. to keep / Max to get**, and toggle **Auto-obtain for path**.

A **Message** section shows the item's on-use / proc message — but where that message lives depends on what the item does:

- **When the item casts a spell** (any weapon use-bless, wand bolt, or proc weapon — an item with a CastsSp ability), the message lives on the **cast spell's record**, shared by every item that casts the same spell. **Add / Edit message…** opens that spell's record, and editing it from any one of those items updates all of them. The item's read-only pane lists what it casts as a clickable **Casts** link — `Casts (on use)` for a `use <item>` cast, `Casts (40%/swing)` for a combat proc — showing the spell name and its record number (`#N`).
- **An item that casts nothing** (a worn trinket whose sole message is a wield/remove line) keeps a message anchored to the item itself.
- **A weapon combat-proc that only deals damage** carries no message record at all — a proc is worth a record only when it applies a lasting effect (poison / blind / hold / disease, which its duration marks; e.g. the darkwood staff's HoldPerson proc keeps its record). A bare command **on-use** cast (the nexus spear's spear-slam) always keeps and needs its messages.

A complete message claimed by a spell or item in this set is **hidden from the Incomplete Messages tab** (an orphaned link — the spell/item isn't in this set — keeps the record listed there).

The item's right-hand info pane is also interactive:

- a **Charm** picker (default 50) re-prices the **Bought / sold** buy/sell figures live, so you can compare, say, a higher-charm party member selling;
- each shop links to its room record and offers **Queue Walking here →** (arms a walk to that shop, like typing it in the nav search box);
- **Dropped by** lists the monsters that drop it as links to their records;
- **Placed in** lists the rooms whose floor holds it, each a link to the room record with its own **Queue Walking here →** (so a room-only item like a quest box shows exactly where to find it).

**For a monster** you can set its **Landmass**, **Region** and **Area** (three type-ahead boxes over the labels already in use — pick one or type a new name; blank means *not set*), its **Relationship** and **Priority**, and pin its whole **single-target combat chain** for that species, rung-for-rung with the Settings → Combat spell grid. Like every other field, the location is saved to the tier you pick in **Use**; a box that still shows the shipped label saves nothing, so an updated shipped label still reaches every monster you haven't re-filed yourself (and clearing a box goes back to the shipped label rather than blanking it):

- **Debuff (single target)**, **Normal attack spell**, and **Alternate attack spell** — each a spell **picker** (type-ahead over your castable spells, same as the Combat slots; commits the cast-code) with a per-room **Max** cast cap and a **Mana** floor.
- **Physical attack** — a command box (the spell boxes are spell-only; a raw attack verb goes here).

Each configured spell rung **substitutes** its spell for this monster and runs the *same* gated cascade the global slot does: its Max cap, its Mana floor (read as % or absolute per the Combat tab's mana mode — below it the rung holds and the flow moves on), **and** the effectiveness gates — a target immune to that spell, or whose level or element fully resists it, skips it down the cascade exactly as a configured spell would. So an override is no longer a blanket bypass; pick a spell that can actually land.

The **Physical attack** box replaces the weapon command **only on a round the engine already chose physical** — it does not force physical or suppress the spell rungs, and carries no mana/cap gating.

A monster's **Greet** row shows every keyword you can ask it as a collapsible tree, the same layout as a room spell's **Conditional effects**: expand a keyword to see what happens when you ask it (**expand all** / **collapse all** sit beside the row). A keyword that **teleports** you is labelled `(teleport)` and tinted, and the destination room is a link that opens the map on that room. The read-only pane's **Spawns In** list shows each room's lair size (e.g. `1/2122 (lair: 2)`). Every spell a monster references — its **spell-attacks, per-hit, create, death, and between-rounds** spells — links to that spell's record and shows the spell's number (`[#N]`), and each entry in the **Summons** list links to the summoned monster's record.

(Combat message wording and per-monster flavor prefixes are no longer edited here — hits, misses, dodges, blocks, and deaths are recognized generically from line colour and the experience line, and flavor adjectives come from one shared vocabulary you edit under **Flavor Prefixes** (below), so you never hand-enter a monster's messages or prefixes.)

**Neutral monsters and Kill on sight.** When you set the Relationship to **Neutral**, a **Kill on sight** checkbox appears — a neutral is normally left alone (it never attacks first), but checking this makes auto-combat engage it while leaving other passive neutrals safe to rest among, so the engine can rest/meditate between kills instead of being forced to clear the whole room.

Even *without* Kill on sight, if you hand-attack a passive neutral yourself (a manual swing or combat cast), the engine takes over and finishes it — hitting a neutral turns it hostile, so it's treated like an enemy until it dies and the walker holds in the room — so you don't have to keep swinging manually; the other un-engaged neutrals stay passive and rest-safe.

**Where the override saves.** The **Use** dropdown chooses the tier — **Only for this character**, **Only for this BBS**, or **For all characters (global)** — then **OK** writes it and the row's Use column updates to match. Priority when the same record is set at more than one tier is character → BBS → global → installed defaults, so a character edit always wins over a global one for that character.

The dropdown also offers **Installed defaults**: picking it and saving **resets the record** — after a confirm it wipes your character, BBS, *and* global edits for that one record and restores the seeded value (the row returns to **Def**). This is the only way to clear a lower-priority edit that a higher tier is shadowing.

You don't have to use it for the common case, though: editing a record's values back to exactly what the installed default was and saving **removes that tier's now-redundant override on its own** (the row shifts back toward **Def**), so an accidental or reverted change doesn't leave a stale override behind.

### Editing a spell's message

Double-click a **Spells** row to edit the spell's player-cast **message** wording (its success and wear-off lines); the spell's own stats are read-only.

For a **damage spell** the Game Data tab leads with an interactive **damage calculator**: a **Level** picker (starting at the spell's learned level, ticking up to its cap) recomputes the min/max damage live so you can watch it grow, and — where they bear on the spell — a **Magic resist** picker (for `Damage(-MR)` spells) and an **elemental resist** picker (Cold / Fire / … matching the spell's type) show how a resistant target cuts it down.

The read-only record also lists **Negated by** — any items that cancel the spell while carried (the inverse of the Item Finder's *Negates* column). Record references on that tab — Summons, Casts, **End cast**, Cast By, Removes, Negated by, Learned From — are clickable links that open the monster / spell / item record, each showing that record's number (`[#N]`). (**End cast** is the spell this one chain-casts when its effect ends — e.g. poison bolt → the poison-bite DoT — so you can follow the chain to the follow-up spell's record.) **Cast in rooms** lists *every* room that casts this spell on entry (a river's damage-on-entry, a sea's crossing script), each a clickable link that jumps the map to that room — the first 20 show inline and the rest hide behind a **show N more** expander. (These rooms used to appear, capped and unlinked, in the *Cast By* row; that row now keeps only the non-room casters — a monster or textblock that casts the spell.) The two carry-gate rows name the command behind them — **Requires carrying (checkitem)** and **Avoided by carrying (failitem)** — so you can tell "you must hold this" from "hold this to skip the effect" at a glance.

For a **room spell with a scripted effect** (the sea-crossing and river spells, whose behaviour branches on your level and what boat you carry), a **Conditional effects** tree lays out the full percentage-gated logic the flat rows above can't: each **condition branch** — a level gate, a carry check, or a **no NPCs in the room** gate (a spell that only fires when the room is empty), its edge tinted red when it fires because you're *missing* an item and cyan when carrying one steers you onto a safe path — opens to its **weighted outcomes** (each with its `%` chance), and each outcome shows what it does — *summons* a monster, *casts* a spell, or *teleports* you to a room — as a link to that record or map room. Effects are read in the game's **top-down order**, so a step that only happens under a further gate (e.g. *summon crimson mist*, then sea hags **only if no NPCs remain**) nests that gated step beneath the unconditional one rather than lumping them together. A huge random-destination sweep (a spell that scatters you across a hundred sea rooms) collapses to a single summary line like "→ a random room (100 destinations)" instead of flooding the tree — expand that line to see the individual rooms, each a map link. **Expand all** / **Collapse all** beside the *Conditional effects* header open or close the whole tree at once.

A spell's message is stored in the Messages table, but because you edit it here, a **complete** message claimed by a spell in this set is **hidden from the Incomplete Messages tab**. (A message still **missing** a required line surfaces on the Incomplete Messages tab, and a message whose spell link is orphaned stays there too.)

**Confuse fumble.** A message flagged **Confused** also gets a **Confuse fumble** box (below *Wears off*): the line(s) that confuse source emits when a confused character fumbles a just-sent command — one wording per line. On a match, a fumbled *move* reverts instead of stranding the walker, **and** the client re-sends whatever command the fumble ate — a weapon swing, an attack spell, an item use — instead of sitting there getting beat on.

A command you typed yourself is never auto-repeated, and a fumbled move self-recovers through the walker rather than a blind re-send. No separate **Last action failed** checkbox is needed for a confusion fumble, since a fumble line already means "the command was eaten." It's seeded with the generic *"You fumble in confusion!"*; enter a spell's own wording (e.g. convulsions' *"You convulse violently"*) here rather than the client hardcoding it.

**Cast response.** Every message also has a **Cast response** box — an engine-driven response sent to the server when that record's spell is detected cast (`^M` = a carriage return, like a Trigger). Its one use is the silent *…temp* death-spells, which emit no line but stall the game engine: a monster whose **DeathSpell** is a temp spell fires this on death, seeded `^M^M` to nudge the engine past the stall.

**Effects checkboxes** tag what condition a matched line *means*:

- **Blinded / Confused / Poisoned / Diseased / Movement prevented** — drive the automatic cures, the navigation pauses, and the party ailment announcements.
- **Fear** — marks a forced-movement debuff (the "You are afraid!" shriek — the game shoves you between rooms until it wears off) so it's tracked as its own condition. The seeded fear records also keep Movement prevented so navigation still halts while you're feared.
- **Attack prevented** — holds *all* combat output: while active (a stun / petrify / bind, until its wear-off), the engine issues no weapon swing, attack spell, or debuff and retries each round once it clears.
- **Last action failed** — re-sends an action the server ate.
- **Disabled (don't use)** — switches the whole record off, so you can silence a mis-firing line without deleting it.

(The four MegaMUD effect bits nothing acted on — losing-HP, HP/mana regenerating, ends-combat — were retired, so they no longer appear here.)

### Incomplete Messages

The **Incomplete Messages** tab (formerly *Unfiltered Messages*, now always shown) is the messages worklist. A leading **Spell #** column shows the record's linked Spell number (blank when it's tied to no spell). It lists three kinds of record that still need attention:

- **A spell-linked message missing a required line** — its **Missing** column names which (caster, target, witness, applied, wears-off, or, on a *Confused* record, the fumble line).
- **A record tied to no spell or item** — an orphan awaiting a link.
- **A too-short pattern** — a record whose applied or wear-off slot holds a malformed pattern too short to match, flagged *"(too short)"* in the Missing column (a stray one- or two-character value left by some older MDB imports). It would otherwise Contains-match almost every line and spam the condition log, so the recognizer ignores it and lists the record here for repair.

Double-click a row to open its editor and fill the gaps in — when the record is linked to a spell in the active set, the editor shows the same read-only **Game Data** tab (the spell's facts + damage calculator) as the Spells tab.

If a spell genuinely has **no wording** for one of the lines, type **{null}**, **{void}**, or **{empty}**: that counts as filled (so the record clears the list) and the recognizer treats it as no line. (Player-facing spells with **no message record at all** are a separate list — the **Spell coverage** report, reached by double-clicking the coverage summary in the Program Log.)

**Upload edits.** At the far right of the Add / Remove row, this button exports every message record you've changed or added versus the shipped seed for the active game type — keyed by spell (or item) number, each field shown as *seed → your value*, with a machine-readable JSON block — to a timestamped Markdown file on your Desktop (the Program Log notes the path).

Filling incomplete lines in-game and clicking this hands the author a clean diff to fold your curation back into the next shipped seed; run it once per game-data set, since each realm's seed is separate.

**On seeds and precedence.** The shipped message seed **refreshes automatically on launch**, so an app update's recurated wording reaches the Global catalogue on its own. A game-data set where you've **saved your own message edits** keeps its per-set `messages.json`, which takes precedence over that seed — so a shipped recuration won't reach that set until you reset it (delete the set's `messages.json`, or reset those records in the Game Data Browser).

(Jumping from a **very old build** — one predating the realm-flavored seeds — and seeing a stale catalogue? The one-time cleanup that used to force this has been retired, so do a clean data install, or delete the obsolete `Global/Messages.seed.json` plus each set's `messages.json` to drop back to the shipped data.)

**Flavor Prefixes** is a small editor of its own in the *Tables + editors* list (not a double-click table). It's the vocabulary of adjectives the game prepends to a monster's name — *large*, *nasty*, *huge*, and so on. The room classifier strips a leading word in this list so "large giant rat" resolves to "giant rat" with no per-monster data.

It starts from the built-in stock list and applies to the **active game-data set**, so a custom realm that uses different adjectives just adds them here (type a word → **Add**; **✕** removes one; **Reset to defaults** restores the built-ins). Edits save to that set immediately. If the classifier ever meets a prefixed name whose leading adjective isn't in the list, it flags a Program-Log row you can double-click to add the word in one click.

**Unrecognized Lines** lists wire lines the Messages catalogue doesn't recognize, staged automatically by the **Capture unrecognized messages** diagnostic (Program Log window, on by default — see *Diagnostics / Log Pane*). It filters out lines the client already handles some other way — party/stat/spell-list screens, the inventory dump, item look text, and benign chatter like player departures, disconnects, follows, socials/emotes, gear swaps, toll payments, status labels, and broadcast-channel listings — so the queue stays focused on genuinely unknown spell/monster/proc messages. Each row shows two columns:

- **Seen In** — the map and room (`map:room`) where that line was *first* noticed.
- **Likely source** — the spell that probably produced *that* line, worked out from the line itself. If the line names a monster the room hosts, you get that monster's own spells, each tagged with how it fires (`bites (#80) — forest spider, on hit`), which also tells you which message slot the text belongs in: an on-hit proc reads as the target's line, an on-death spell can only fire as the monster dies. If the line names no monster, you get the room's **own** on-entry spell (`Rooms.Spell`) — the source of the atmosphere lines that read like scenery (`An ominous wind blows through the trees` is the darkwood forest spell). When neither applies the column is **blank**, on purpose — a hint that every row shares tells you nothing.

The list updates live as lines come in without moving you — new rows append at the bottom and a rising occurrence count refreshes in place, so you keep your scroll position and your selected row while the game is running.

Double-click a row to open the same editor Messages uses, pre-filled with the raw text, and Save it in as a real record. That editor is spell-only — type the **spell number** the line belongs to and **Add**; if that spell's record already carries message text, its empty slots fill in for you, and any slot where your captured line *differs* pops an inline **picker** so you choose per field between the record's value and the captured line. The **3rd party witness** slot accepts **multiple wordings, one per line** — a room spell fires a whole set of ambient flavor lines (the silvermere / darkwood-forest atmosphere), so put each on its own line and every one is recognized from that single record.

For the selected row(s) you have three actions:

- **Dismiss** — marks them decided and *frozen*: the row stays but the client then ignores every future recurrence of that text (no re-add, no re-count, no re-alert).
- **Remove** — hard-deletes the row (if the line shows up again later it's captured fresh).
- **Export** — writes every *non-dismissed* line, with its Seen-In location, occurrence count, and Likely-source shortlist, to a timestamped file on your Desktop (the Program Log notes the path) so a batch can be handed off for attribution.

## Monster Intel

**Monster Intel** (View menu, or the toolbar's *Monster Intel* button) is a fast pre-fight check, not a monster database browser — it answers one question: **can I safely fight this thing right now?** It now shows a quick **Abilities & resistances** summary (elemental weakness/strength, immunities, undead state, and so on), but for the full record on a monster (loot, every room it's placed in, the automation overlay editor), use the Game Data Browser's Monsters tab instead.

**Character bar** — a strip across the top (once a character is loaded) showing your name/level/class, live HP, live Mana or Kai (whichever your class uses), your currently-equipped weapon's HitMagic, how many attack spells you've obtained, and **AC vs Selected Target** — the effective Armour Class the monster you've selected actually rolls against: your base AC (worn + buffs) plus Shadow, plus the wards that apply to *that* monster's alignment.

Which anti-alignment ward exists depends on your realm:

- **Paradigm** uses **Vile Ward** (ability 1113, converted by your own evil tier) versus an evil target.
- **Stock** realms use **Prot Good** (ability 25) versus a good target.
- **Prot Evil** (versus evil targets) applies in both realms. Paradigm dropped Prot Good for Vile Ward, so a Prot-Good value is ignored there.

**AC vs Selected Target** reads "—" until you pick a monster; it updates live as HP/mana tick and stays current if you swap gear or learn a new spell while the window is open.

**Defense simulator** — the second row of the character bar is a live what-if for your defense. It **seeds to your current loadout** when the window opens, and re-seeds if you swap gear:

- **AC** — worn gear + your permanent race/class/quest bonuses + configured buffs, the defense every attacker rolls against.
- **Shadow AC** — a checkbox worth a flat +10 vs every attacker.
- **Prot Evil**, and a realm-specific anti-alignment ward — on **Paradigm** a raw **Vile Ward** value with an **alignment** picker beside it; on **Stock** a **Prot Good** value (no picker), since Paradigm dropped Prot Good for Vile Ward and Stock never had Vile Ward.

Edit any of them and the whole list's **Hits You %** recomputes instantly, so you can ask "what if I had +5 more AC?" or "how much safer am I with Shadow up?" without changing a thing in-game.

Prot Evil and Vile Ward are **evil-only** wards — they raise your defense only versus an evil monster; Prot Good is a **good-only** ward, applying only versus a good monster. Against off-alignment or neutral monsters each does nothing, so they're never folded into a single headline number that would overstate your AC. The Vile Ward alignment picker is **your own** evil tier: it scales how much of your raw Vile Ward converts to AC — **not evil = 0%, outlaw/criminal = 50%, villain/fiend = 100%** (~10 Vile Ward = 1 AC at full).

Because the seed already assumes your **configured self-buffs are up**, the numbers reflect how you'll actually fight, not how exposed you are standing around unbuffed.

The left list is filterable by name and shows six columns:

- **Name**, **HP**, **EXP** — the basics.
- **Accuracies** — every one of the monster's physical attacks' accuracies, most-used first, so you see the full spread that feeds Hits You %, not just its best (blank for a spell-only monster).
- **Hits You %** — that monster's chance to land a hit on you, **weighted across all its physical attacks** by how often it throws each, given your live AC/Dodge, your Shadow bonus if you have one, and whichever ward (Prot Evil/Prot Good) applies to its alignment. The detail pane breaks this down per attack.
- **Est. Rounds to Kill** — rounds for the attack you pick in **Edit Attacks** to drop it. The default is **Fastest of all my attacks**: for each monster, whichever of your usable melee attacks (Backstab excluded — it's a one-time opener) or obtained attack spells kills it soonest, so a caster's spells count instead of a weak melee swing. Pick a single attack there to pin the column to just that one. A melee attack projects your live accuracy/damage/swings/crit; an attack spell divides the monster's HP by that spell's resist-adjusted per-round damage. Shown as "—" when no attack you're using can out-damage it (unarmed, fully resisted, spell-immune, and so on).

A monster that would take longer than the **rounds-to-kill cap** (a spinner beside the Hits-You-% filter dropdown, default 999, editable right in this window) is **filtered out of the list entirely**, so you see only fights you can finish quickly — a superboss projecting into the millions of rounds simply drops out rather than showing a noise number. Because that filter is otherwise invisible, an amber note beside the spinner says how many monsters the cap is currently hiding; raise the cap to bring them back.

Raise the cap to include tougher monsters; a monster the selected attack *can't* kill at all still shows as "—" (a different axis — can't-kill, not slow-kill — whose Hits You % is still worth seeing). Editing the cap re-applies immediately and saves per character.

Every column is independently sortable (click a header; click again to reverse), and **double-clicking a monster opens its full record in the Game Data Browser**. Once a character is loaded, a monster with no computable Hits You % (an NPC/caster-only record with no catalogued physical attack — a trainer, quest-giver, etc.) is dropped from the list entirely — it isn't a meaningful "can this thing hurt me" entry.

A **Hide regen timers** checkbox (beside the rounds cap) drops monsters that respawn on their own timer — bosses, lair leaders, and other timed spawns (any with a non-zero respawn/regen time) — leaving only freely-farmable monsters in the list.

A **Hits You %** filter dropdown narrows the list by how dangerous a monster's own attack is. It offers a set of contiguous %-bands — each its own discrete range, together covering the realm's whole range with no gap — and you tick **any combination** (multi-select): a monster shows if its Hits You % falls in **any** ticked band (tick the safe end and the risky end to see both while hiding the middle); tick none and every monster shows.

The bands are **realm- and class-dependent**, because the lowest a monster's attack can ever land differs — **8% on Stock, 2% on ParaMUD**, dropping to **1% on ParaMUD for a light-armour class** (the engine lets Silk/Ninja/Leather armour-type classes floor one point lower):

- **Stock** skips the impossible sub-8% bands and starts at `≤8%`.
- **ParaMUD** offers `≤2%, 3–5%, 6–10%, 11–15%, …`.
- **A light-armour ParaMUD character** additionally gets a leading `≤1%` band.

The dropdown button shows how many bands you've picked, so the filter fits the realm — and class — you're actually playing.

Select a monster to fill the right-hand detail panel, which has two parts — **Your Matchup** and **Abilities & resistances**.

**Your Matchup** is only shown once a character is loaded. It gathers everything about fighting *this* monster with *your* character:

- **Weapon check** — whether your **currently-worn weapon** is magical enough to hit the monster physically (its HitMagic vs the monster's requirement).
- **Incoming threat** — a **Melee** line (the monster's chance to hit you, its damage per hit, its **attacks per round**, and its damage **per round**) plus one line per element it casts alongside how much your own worn gear resists it.
- **Your physical attacks** — each usable type (Normal, Bash, Smash, Backstab, Martial Arts) with its **rounds-to-kill**, per-round damage, hit %, and damage-per-hit against this monster.
- **Your attack spells** — every spell you've obtained, ranked by **mana efficiency** (damage per mana, rounds to kill, total mana to kill, per-round damage), **split into single-target and AOE groups**. A spell blocked by the monster's spell immunity, fully resisted by its element, or restricted to undead-only/living-only targets it doesn't qualify for shows the reason instead of a damage number.

The **Edit Attacks** button (top-right of the window) drives this list: check which attacks appear here, and pick (the radio) what fills the master list's **Est. Rounds to Kill** column — **Fastest of all my attacks** (the default) or one specific attack — so you can weigh "how many rounds if I nuke it with my best spell?" against "if I just swing my weapon?" (Backstab is listed too; since it's really a one-time opener, treat its rounds figure as a per-swing projection.) For the full attack-type-by-attack-type melee breakdown with editable what-if inputs, use the Player Workshop's **Calculators** tab.

The **Apply Debuffs** button (just under Edit Attacks) folds your known enemy debuffs onto the *selected* monster as a what-if: check any of your stat-affecting debuffs — the ones that lower a monster's **AC**, **DR**, **Dodge**, or **accuracy**, or **slow** it — and every number in Your Matchup (your hit %, rounds-to-kill, and the monster's Melee threat line) recomputes against the softened target, with a banner naming what's applied.

Debuffs stack and can push a stat **negative**: drive a monster's accuracy below zero and it can't hit you at all; drive its AC below zero and your accuracy benefits a lot. Slowness raises the monster's attack energy, thinning its attacks per round. This is a preview only — the client never applies these effects in live combat — and your picks save per character.

**Abilities & resistances** shows the monster's *own* properties (character-independent, always shown when the record carries any):

- its **elemental profile** as weakness vs strength (e.g. `Fire +50% (resists)`, `Cold -25% (weak)`; `100%` = immune, over `100%` = healed by that element instead);
- whether it needs a **magic weapon** to hit (and the HitMagic level), its **spell-immunity** level, **damage resist** and **magic resist**;
- **undead** / **non-living** (immune to life-drain), and any other notable abilities (see-hidden, fear, confusion, poison, and so on).

This is the quick "what is it" summary; the full record (loot, every placement, the overlay editor) still lives in the Game Data Browser.
- **Your Observations** — only shown once this character has actually fought the monster at least once: landed-hit damage extent and average, hit rate, and how many times a physical attack or a spell had **no effect** — a real, confirmed discovery that this monster's Magical or SpellImmunity requirement is higher than what you're using against it. The physical count is retired the moment one of your swings lands damage on that monster: the "no effect" was true of the weapon that drew it, not of the monster, so a weapon that does get through settles the question. This is deliberately kept separate from Your Matchup — that comes from the game-data record (the MDB); this is only what *this character* has personally seen happen in combat. A **Clear** button wipes every monster's recorded observations for this character (not just the one you're viewing).
- **Attacks** — every physical, spell, and rob attack slot with its chance, its damage, accuracy, and energy cost, plus its between-round spells — how dangerous is its swing, beyond the bare Hits You % number. A **spell attack** now shows the **computed damage** for its cast, not just the spell number: the linked spell's formula scaled to the monster's assigned cast level, for a **single cast** (a monster casts its spell once when the attack lands — how often it fires rides the monster's own attack energy, so the spell's own player-side energy cost is not folded in). A poison/blind/hold spell or other pure-effect cast shows no damage figure (it deals none), and the same computed range appears on the between-round spells.

---

# Conversation

Press **Alt+C** to open the **Conversation** window — a dedicated view of all the chat MudPlay pulls out of the terminal, with its own input box so you can talk without hunting for the game prompt. Alt+C again closes it.

## The chat log

Chat is collected into one merged, timestamped stream (not per-channel tabs). Each line shows the time, a colored **channel tag**, the speaker, and the message:

- **GOS** gossip · **SAY** local say · **YELL** yell · **←TELE / TELE→** telepaths received and sent · **GANG** gang/guild · **BCAST** broadcasts · **SERVER** realm notices (players entering and leaving, PvP messages).

Each channel has its own color, and web links inside a message are clickable. Party chat isn't shown here — it has its own **Party** window.

**Chat never drives the client.** Anything another player types — gossip, auction, broadcast, telepath, gangpath, yell, say — is treated as text to read, not as something the game said. A player quoting "You are flat on your back!", "You are blind.", or a combat line in chat can't make MudPlay think you were knocked down, blinded, or hit; only lines the server itself sends do. Chat still reaches this window, your chat-scoped triggers, and the party `@`-commands.

**Actions / emotes** (the socials from your board's `action list` — `hug`, `wave`, `smile`, `tickle`, and so on) are pulled in too, whether you perform them, someone aims one at you, or you just witness one in the room. They show under the **SAY** chip (they're room-local, like say) with the message text in **green** — the board's own color for them.

Since the obvious-exits line is also fully green, MudPlay only captures true actions: your own start with "You <verb>", and someone else's must come from a **player who's actually in your room** — so obvious exits, room-entry/exit, and party-follow movement never get mistaken for an emote.

**Selecting and copying:** click a line to select it, and click more lines to add them (each click toggles that line, so clicking a highlighted line again unselects it). You can also **click-hold and drag** across several lines to select — or deselect — a whole run at once; the line you press on sets the direction. With one or more lines selected, **Ctrl+C** (**⌘C** on macOS) or **right-click → Copy** puts the whole entry — time, speaker, and message — on the clipboard as plain text (one line per entry). Press **Escape** to clear the whole selection at once.

## Filtering and searching

The toolbar across the top controls what you see:

- **Channel checkboxes** — **Gossip, Say, Telepath, Gang, Broadcast, Yell, Server** — tick or untick to show or hide each channel. Each box is painted in its channel's color, so the row doubles as a color key. Your choices are remembered per character. (Telepaths in and out share the one Telepath box; realm notices and PvP messages share the Server box.)
- **Search** box — narrows the log to lines whose speaker or text matches what you type (this one isn't remembered between sessions).
- **Auto-scroll** — when ticked, the log follows the newest line while you're at the bottom. Scroll or drag up to read back and new lines leave your place alone; scroll back to the bottom (or re-tick the box) and it follows again. Untick it to never follow.

## Talking

Type into the input box at the bottom and press **Enter** (or click **Send**) to send the line to the game — you still type the game's own chat commands (`gos hi`, `/bob hey`, and so on). This is the input box where your **aliases** expand and where `;` or `^M` splits one line into several commands.

**↑ / ↓** recall what you sent before, and the chevron at the right edge of the box opens a list of recent commands to pick from. **Tab** completes the word at your cursor against your carried, worn, and key-ring item names, the same as the terminal (**Settings → General**) — press it again, or **Shift+Tab**, to step through other matches.

## Logging and history

The window keeps its history even after you close it, and replays your last session's chat when you reconnect. To save chat to a file, turn on **Settings → Talk → Log conversations** — it writes to the `Logs` folder, which you can open from **Tools → Open logs folder**. There's no clear button in the window itself; use **Tools → Clear chatlog** on the main window to wipe it. The chat font and channel colors are set on the Talk tab and apply live the moment you hit Apply — an already-open window re-fonts and recolors on the spot.

---

# Tools & Diagnostics

A few smaller windows for reviewing your session and troubleshooting. Each is modeless and toggles closed when you press its key again.

## Program Log (F4)

Press **F4** (or **Tools → Program Log…**) to open the **Program Log** — a running, timestamped record of what the engines are actually doing, and the first place to look when something automated didn't behave. Each row is tagged with a severity and the source engine.

- **INF / WRN / ERR** — severity filters; tick the ones you want to see.
- **Search** filters the rows by source or message text; **Clear** empties the view; **Auto-scroll** keeps it pinned to the newest row.
- **Debug** and **Combat** are *generation* toggles (not just filters): they turn the verbose cross-engine trace and the combat-decision channel on or off across the whole app, and show those rows here. Both are **on by default** and persist per character — leave them on for the richest diagnostics; turn one off to quiet the noise. (These are the same two channels you'll see in a bug report.)
- **Auto-collect logs** writes the program, memory, and combat-trace files to the Logs folder for the session (off by default, so a normal run leaves nothing behind). **Hop timing** logs one line per confirmed room hop with its measured wall-clock time — used to tune the Auto-Lair travel-cost table.
- **Simulate buttons** is a dropdown of test-only toggles, each revealing a hidden **Simulate …** button on its feature window (all off by default and reset off every launch, so a normal session never shows them): **Simulate Death button** (Player Workshop → Death Recovery tab), **Simulate Chest button** (Bosses tab → Chest Offload — seeds a few random containers so you can exercise the window without real chests), and **Simulate entry button** (Game Data → Unrecognized Lines — feeds a synthetic unknown line through the capture flow so a candidate appears, letting you see the feature work without waiting for the game to emit one).

## Backscroll (Alt+L)

Press **Alt+L** to open **Backscroll** — the full terminal history, including lines that have scrolled off the top, on a timestamped transcript that opens at the newest line.

- **Search** — type a term and press **Enter** (or **Find next**) to step through matches, newest to oldest, wrapping back to the top. The footer shows the line count and how many matches were found.
- **Jump to end** — return to the newest line.
- **Export…** — save the whole transcript to a text file, each line prefixed with its timestamp.
- Drag to select a region, then **Ctrl+C** (**⌘C** on macOS) or **right-click → Copy** to put it on the clipboard as plain text. Right-click → **Select all** grabs the whole transcript to copy at once.

Backscroll is a **snapshot taken when you open it**, not a live tail — to pick up newer output, close and reopen it (nothing is lost in the meantime). The transcript renders in your **terminal font** (family and size), so history looks exactly like the live screen; that font is captured when the window opens, so changing it takes effect the next time you open Backscroll.

## Session Stats

Open **Session Stats** from the **View** menu or its toolbar button (it has no default hotkey — you can assign one on Settings → Shortcuts). It tracks this session's performance in a stack of panels: **Kills/hour** and **Exp/hour** graphs, an **HP/MA per loop step** chart, and **Player Statistics**, **Time Analysis**, and **Session Statistics** tables (kills, experience, currency, and time spent moving, resting, and fighting). While a loop is running, the **Session Statistics** panel also shows a **Loop laps** readout — one lap is a full completion of the circuit — with the laps completed, last and average lap time, the live current-lap timer, and the room each lap starts at.

The **Player Statistics** panel breaks combat down by swing type (hit / miss / crit / backstab), per-round damage, weapon procs, and — when you fight with **attack spells** — one row **per spell**, showing its damage range, cast count, and accuracy. Spell casts are tracked apart from weapon swings: a cast's flavor line ("You scatter some ashes…!") is no longer miscounted as a swing miss, so a spell caster's miss rate reflects real resists (usually near zero on Paradigm, where most combat spells don't fail to land) rather than one phantom miss per cast.

- **Right-click** the panel area to show or hide individual panels, and **drag a panel by its title** to reorder them — your layout is saved per character.
- **Reset session** zeroes every counter and restarts the clocks; individual panels have their own **Reset** too. (These don't ask for confirmation.)
- **Transaction history** and **Players Seen** open the detailed ledgers — coin banked and stashed this session, and every player you've encountered. In the transaction ledger, **stash** entries are tinted faint gold (the map's stash-marker colour) so they stand out from bank deposits, and **double-clicking any entry** opens the Navigation map centred on the room where that deposit or stash happened. Each row has a **Keep** checkbox: check the entries you want to hold onto, and **Clear history** wipes everything *except* those — a way to prune a full ledger without losing the rows that matter (with nothing checked it clears the whole thing, as before). The clear updates the on-disk log too, so kept rows survive a reconnect and cleared ones don't come back.

## Buff Watchdog

Open **Buff Watchdog** from the **View** menu (right after Party) or its toolbar button — it has no default hotkey, but you can assign one on Settings → Shortcuts. This is the **one place you configure every automated buff** — self bless, party bless, room light, mana-regen, and the "when HP/MA full" utility casts all live here now, in a single unified list — **and** it shows a live timer bar for each one as it runs. Re-selecting the menu item (or toolbar button) toggles it closed.

### Building the buff list

Click **＋ Add buff** to open the Add-buff dialog:

- **Pick a buff** — a **dropdown**, not a text box: it lists every buff spell you've actually **learned** (attacks and heals filtered out), each shown as its **name and the level you learned it at** (e.g. *bless (Lvl 2)*), plus any **cast-on-use buff item** you can actually use — one you own (carried or worn) and meet the level for (an unlimited-use item like a *shimmering greatsword* that casts a buff when used; these show as a `#item` entry). A buff that's **already slotted** stays in the list but is **greyed out / unselectable**, so you can see it's taken rather than wonder where it went. The list is ordered by learn-level, low to high. What targeting a slot offers depends on the spell: a self-only spell can only be cast on you, a single-target spell can be aimed at you and/or party members, and a whole-party spell (chant and the like) blankets everyone with one cast.
- **Set a recast timer** — "recast (s)" recasts the buff that many seconds before it expires (0 = wait for it to actually wear off). It can also be **negative**, which recasts that many seconds *after* the buff wears off — e.g. `-30` on a 60s buff recasts it every 90s, letting it lapse on purpose to **spread out mana use**. The Watchdog bar shows the post-expiry wait as a **red** extension (see *The buff bars* below).
- **Set conditions** — optional per-slot gates, some of which only appear for the spell that uses them:
  - **Only when HP is full** / **Only when MA is full** — hold the cast until you've rested up to your **rest-max** target (not literal 100%); a "topped-off, ready for the next fight" buff. A triggered recovery rest suspends it until you're back at max.
  - **Only when the room is dark** — shown for a **light** spell. Ticked, it keeps the reactive cast-on-entering-a-dark-room behaviour (via the auto-light system); unticked, the light is maintained like any ordinary buff.
  - **Cast before resting for mana** — shown for a **mana-regen roll** spell (nature tap / mana flux / prfl). Ticked, the buff is only kept up **while you're resting for mana**: it's (re)cast when your mana drops below its rest threshold and recast on expiry through the whole rest — including if a fight interrupts the rest — and stops once your mana tops back up. Unticked, it's kept up all the time like a normal buff. (It also carries the reroll knobs, below.)
- **OK** adds it as a slot.

A mana-regen roll spell (nature tap, mana flux, profane link, and kin) rolls a random regen contribution each cast, so the "Cast before resting" condition also carries **reroll knobs** to chase a good roll:

- **Reroll below abil 145** — a threshold: reroll while the spell's rolled contribution (read off `abil 145`) lands under it. That value can be negative, so "reroll below 0" chases a non-negative roll.
- **Max rerolls** — how many times to chase a better roll before accepting what landed.
- **Reroll infinite** — a checkbox just below Max rerolls: keep re-casting until the roll clears the threshold, no cap (ticking it greys out Max rerolls).

Each reroll re-casts the spell, so it costs mana; if you run out mid-cycle the reroller **pauses rather than giving up** — it waits while you meditate back up, then resumes, so it spends its full budget instead of settling for a bad roll. It also **holds its rerolls during a fight**: every reroll is a cast between rounds, which turns combat off (and breaks a running room spell), so a roll that lands mid-fight is rerolled once the fight ends instead.

Rerolling works on **Paradigm** (reading the roll back from `abil 145`); on Stock, with no `abil 145`, it judges the roll from the observed passive mana tick. **Raising the cap / threshold (or ticking infinite) also re-checks the roll spell that's already up** — if its last roll now falls short, it rerolls right away. (A mana-regen roll spell is a self-only cast, so the caster always gets the roll whenever it fires.)

By default the list **auto-groups by type**: the **buffs you aim** (self / single-target) first, then **whole-party** buffs, then **item ("on use")** buffs. But you can re-order and re-prioritise:

- **Re-arrange rows yourself** — drag a row by its **grip** (the ⠿ handle at the far left) or use the **▲ / ▼** buttons. Once you re-arrange, the list switches to **manual layout**: it keeps your exact order and **new buffs append at the bottom** instead of sorting into a group. A **↺ Reset order** button (top of the panel) restores automatic grouping.
- **Cast priority** — a button at the top that toggles what the engine casts first when several buffs are due the same round: **Default (by type)** casts in the standard self → whole-party → item order *regardless of how you've arranged the rows*, while **Top → bottom (your order)** casts them in the exact order shown. (The two are identical until you re-arrange — so arranging rows doesn't change casting unless you flip this to top-to-bottom.) The **timer bars** on the tracking side follow this same order, so the two sides always line up.

Each slot is a **row** with the grip / ▲ / ▼ / **✎** (edit — reopens the dialog) / **⨯** (remove) at the left, then the buff's `name - recast` label (just the name and recast margin, no level tag), then the targeting checkboxes. **Double-clicking anywhere on a row opens the same edit dialog as ✎** — the spell / recast timer / conditions, including the reroll target for a mana-regen roll spell that "Add all blesses" added without one. **You choose who it's cast on right in the row:**

- A **Self** box casts it on you — and when you're **solo, that's the only box shown**, so there are no empty party columns to puzzle over.
- Once you're in a **party**, the row surfaces a **checkbox per member** (member names run along the top as column headers, so every row's boxes line up under them), followed by an **All/None** master on the right.
- **All/None** ticks or clears every party member at once — and it's **independent of your Self box** (toggling it never changes Self). Ticked, it blesses **every member, auto-adapting**, so anyone who joins later is blessed too; unticked, it blesses **no** members — a joiner is **not** auto-assigned, only the members you've explicitly ticked keep getting it. Unticking one member drops out of All/None but leaves the rest ticked.
- A **whole-party** spell shows a master **Party** on/off toggle plus a **Solo** option. Unticking **Party** disables that buff completely and clears Solo too, leaving both boxes visibly off; Solo stays disabled until Party is turned back on. While Party is enabled, tick **Solo** to also cast it when you're alone; a whole-party cast still lands on a lone character (a party of one), so it isn't wasted. Untick only Solo to make the enabled buff party-only. A **self-only** spell shows just the **Self** box (which already fires solo or partied).

A given spell is **one slot** — once it's slotted it drops out of the Add dialog, so you can't double up. Everything saves as you edit it; there's no Save button. Existing setups from before the unification are migrated into this list automatically.

**Add all blesses** adds a row for every buff you've actually **learned** — on yourself or the whole party — in one click, no dialog. That's every self-only spell, every single-target spell aimed at you, and every whole-party spell **in your spellbook** — only what you can really cast, not the class's full theoretical roster. Beyond spells:

- **Cast-on-use items** whose effect always lands on you or the whole party without needing to be aimed (a wielded weapon or staff like a bless-casting crozier, or an unlimited-use party item) — but only ones you can use right now: you meet the item's **level** requirement **and** the item is **in your pack** (carried or worn). An item whose effect needs a target isn't included, since `use <item>` can't be pointed at a person. (The pack check needs a recent inventory listing — until you've done an `i` this session every class cast-item shows; they filter to the ones you own the next `i`.)
- **Alignment-aware** where it can be: a class's spell list often carries both sides of a holy/unholy pair, and once MudPlay has seen your alignment in a `who` this run, only the side you can cast is offered — a "non-evil only" spell is hidden from an evil character, a "non-good only" one offered to them same as a neutral.

Skips anything already slotted.

Within each of those groups the rows land sorted **by level requirement, low to high** (ties broken alphabetically) — not by name — so a high-level pick shows up near the bottom of its group with the other high-level buffs. A few rules govern what gets pre-checked:

- **Every eligible buff gets a row** — it doesn't check off one pick per buff family and hide the rest, so you can see (and switch to) any of them.
- **Conflicting pairs** — where two buffs you've learned would strip each other off (e.g. **zeal** and **greater zeal**), only the higher-level one comes pre-checked; the others are listed unticked. Checking one member of a conflicting pair automatically **unticks the other** (and vice versa), so you never end up with both fighting over the same slot — this live swap applies to any row, not just ones this button added.
- **Your hand-configured buffs are left alone** — anything that conflicts with a buff you configured yourself (self or party-wide) is listed but not pre-checked, so it never silently clobbers your setup.
- **Whole-party buffs** (chant and the like) sit in their own group below the aimed buffs, but both their Party master and Solo option start **off** — a whole-party cast affects other players, so that's always your call to make by ticking the row's own **Party** box, never something a bulk-add button decides.

The button disables once there's nothing left to add.

**Remove all** clears the entire list in one click — every self, party, and whole-party slot, gone. It clears the *config*, not the *buffs*: any buff that's actually **up** keeps its live timer bar in the Buff Watchdog (now shown as a plain read-only bar, since nothing's configured to recast it) so you can still watch it run down — removing a slot only stops future recasts, it never cancels a running buff.

If you've turned on **Confirm deletes** (Settings → BBS + Display → "Show confirmations" — off by default), it asks you to confirm first, the same prompt every other list delete in the app uses. Disabled when the list is already empty.

**Unlearned spells** prints a report straight into the terminal — the same bright-yellow `[…]` notice style the quest-availability announcements use — of every spell your class hasn't learned yet that's **within reach**: the ones you could train **right now** at your current level, plus everything up to **five levels ahead** so you can see what's coming. Each spell is one line, `[Spell name - Unlearned, Requires Level XX]`, listed lowest level first.

It's a read-only look at your spellbook — it casts nothing and changes no config — handy for spotting a spell you've out-levelled and never went back to train. (It needs your level, so read your stats once after logging in; the button is disabled for a class with no spell roster.)

A row shows a **⚠** next to its name when it conflicts with another configured slot — some buffs remove others when both land on the same character (e.g. **chant removes bless**). Hover it to see which buff and in which direction: **"Removed by: …"** means that other buff strips this one, **"Removes: …"** means this one strips that other buff.

The warning is about the two buffs being **configured together**, not about which boxes are ticked right now — leaving a clashing buff added but switched off doesn't stop it clobbering the moment it's cast, so the ⚠ stays as long as both are in the list. It's purely a heads-up so you can see the cause and effect of your setup before it surprises you in play — it doesn't change what gets cast.

**What the engine actually does with a conflict depends on your realm** — because *when* a buff's removal fires differs:

- **Paradigm** — an active buff re-strips whatever it removes every few seconds, so a **one-directional** loser (e.g. **chant** alongside **greater bless**, which removes chant but isn't removed back) can never hold. The engine stops maintaining it entirely and its row reads **"covered by"** the winner instead of a stuck timer, so it doesn't burn mana on a buff about to be stripped again.
- **Stock** — removal fires only **at the moment of cast**, so both *can* be kept: the Watchdog casts the **remover first** and re-applies the removed buff after each remover recast, so a one-way pair stays up **together** — the loser keeps its timer bar and reads **"both kept"** rather than flagging a conflict.

(A **mutual** pair — bless ↔ greater bless, each removing the other — is **last-cast-wins** on either realm; only whichever was cast most recently survives, so pick one.)

> **Note:** the **HP-regen** spell is *not* a maintained buff and isn't set here — it's a reactive minor-heal that fires when your HP dips, and it stays on **Settings → Spells** as **HP Regen**. Everything else moved to this list.

**The mana budget** is a live readout just under the buttons — **`Mana/Tick gained: N - Mana/Tick to maintain: N`** — both sides measured per 30-second passive-regen tick (the "MP +N after ~30s" cadence) so they compare directly. The instant one number exceeds the other you know whether a buff loadout is self-sustaining before you commit to it in-game. It updates the moment you check or uncheck a box, the party roster changes, your gear changes (an `i`), or you level.

- **Mana/Tick gained** is your **natural passive mana regen** per tick — from your level, casting stat, and worn **+ManaRgn%** gear. It does *not* fold in a mana-regen roll spell (nature tap / mana flux and kin): those roll a variable amount and the spell itself already shows up on the cost side. 0 for a non-caster.
- **Mana/Tick to maintain** is what every currently-**checked** buff costs to keep recast forever: each buff's mana cost spread over its (level-scaled) duration, scaled to the 30-second tick, times how many casts it actually fires:
  - a **single-target** buff counts one cast per person it's aimed at (yourself plus each targeted member — blessing 3 members is 3 casts);
  - a **whole-party** buff counts as a **single** cast no matter how many are in the party, since one cast covers everyone (its cost only moves with your level, if its duration scales);
  - a **Paradigm-suppressed loser** (one a configured buff permanently removes) isn't maintained, so it costs nothing;
  - a **stock collision-ordered loser** (one you keep alongside a buff that removes it) is budgeted at the **shorter** of its own and its remover's duration, because each remover refresh strips and re-casts it — so if the remover is the shorter-lived of the two, the loser costs *more* per tick than its own duration would suggest.

### Reading the timer bars

A small **arrow button at the top-right of the timer-bar side** collapses or expands the config panel — one click hides it (bars fill the whole window), another brings it back. It's styled like the navigation map's collapse chip, and the arrow points the way the next click moves the divider (so it follows whichever side the config sits on).

Collapsing also **shrinks the window** to just the bars (the far edge pulls in to where the separator was); expanding **grows it back out** to fit the config panel again — no manual resize. Handy once your buffs are set and you just want to watch the timers. The choice sticks per character, so the window reopens the way you left it. (The button only appears for a class that actually has buffs to configure.)

The timer bars are grouped **by player**: **your own name first** (your self buffs and any whole-party buffs), then one section per party member with the buffs cast on them.

- Each bar shows the buff's cast code (or `#item` name) left-aligned inside it, with the **time remaining** just after.
- The **bar fills as the buff ages** (empty just after it lands, full at wear-off), and a **vertical amber marker** shows where its **recast window** opens — the recast lead you set per slot. When the fill crosses the marker the bar turns amber: the buff is now due. If you set a **negative** recast, the bar instead grows a **red segment past the green** — green is the time the buff is still up, red is the deliberate post-expiry wait, and the recast fires when the red fills (the label reads *"expired · recast in Ns"* during the wait).
- A buff that's **set to be kept up** (targeted on you or a member) but **isn't up** right now (worn off, or not cast yet) shows an empty bar labelled **not up**, so you can see at a glance which maintained buffs are missing. A configured buff that **isn't** set to recast on anyone shows a bar only while it's genuinely up: the instant its timer runs out the bar **drops** (only a buff set to recast lingers as an expired bar, since that's the one the engine will refresh) — and a not-recast buff with no live timer at all isn't listed, as it would just be clutter.
- A buff that's **actually up but isn't configured** — one you cast by hand, or a slot you just removed — still shows its live timer bar as a plain read-only entry, so clearing your config never hides a buff that's genuinely running. Nothing recasts it, so the bar just **clears itself when the buff wears off** — unlike a still-configured buff, whose row stays as **not up** because it's meant to be recast.
- A single-target row whose member is **hiding** (the cast came back *"You do not see … here!"*) shows **hidden — can't target**; it clears and retries when you move or they reappear.
- A small **✕** on a live bar **clears that timer** — marks the buff off (e.g. when a dispel you didn't see stripped it). A configured buff that's still due recasts on the next pass; a leftover timer (say an ex-member's) just disappears. The ✕ only shows while a timer is actually up.
- A configured buff your character **hasn't learned** is flagged **unlearned**.
- A **single-target** buff gets **one bar per member** it's cast on (each member is blessed individually, so each has its own recast timer). If you untick a member you've already blessed, their bar **stays until the buff actually expires** — unticking just stops future recasts, it doesn't cancel the running buff.
- A **whole-party** buff blankets everyone in the party **at the moment you cast it**, so it shows a bar under **your own section and each member who was present** — all reading the one recast timer (recast is driven by *your* timer). If a member **swaps out and someone new joins**, the newcomer shows **not up** under their section: they didn't get the party buff and won't until your next recast, which re-covers whoever's in the party then. So a glance tells you who's actually covered.
- A bar shows the same **⚠** conflict marker as the config row (see *Building the buff list*) whenever another configured buff removes it or it removes another. When a newly-cast buff **strips one you had up** (chant removes bless), casting it clobbers the other off you — so the Watchdog **clears the stripped buff's bar** the moment the clobbering buff lands, since it's no longer up. It works out which buff was removed from the spell's *removes* data (not the game's ambiguous shared wear-off line, which can't say which one faded), so the right bar goes away instead of lingering as if it's still running. The surviving buff keeps its normal countdown, its **⚠** still at the end as the usual "this removes another" heads-up. (In the brief moment before the clobbering buff confirms — or if that cast doesn't land at all — the stripped bar instead **stops counting and reads "conflict"**, its ⚠ moved to the front, until it clears.)
- **When two configured buffs can never coexist, the loser reads "covered by" and isn't maintained.** On **Paradigm**, an active buff re-strips everything its *removes* list names every few seconds, so if you've configured both a buff **and** something it permanently removes — e.g. **greater bless** (removes chant) alongside **chant** (does *not* remove greater bless) — greater bless always wins. The client recognises the one-directional loser, **stops casting it entirely** (on you and any party member the winner covers), and shows its bar as **"covered by ⟨winner⟩"** rather than burning rounds re-casting a buff about to be stripped again. This applies only to a **one-way** conflict; a mutual pair (bless ↔ greater bless) is left to last-cast-wins, and Stock isn't treated this way (it may only strip on cast, letting both stay up).

A **drag bar** sits between the config table and the timer bars — grab it to re-divide the space between the two. It stays where you leave it as you resize the window: the config table keeps its size and the timer bars flex to fill the rest. Where the config table sits relative to the bars — **above / below / left / right** — is set on **Settings → General → "Buff Watchdog layout"**; changing it reflows an open Buff Watchdog at once.

**What it counts as "up".** A buff's timer is armed by the **cast code** — whether the client cast it or **you typed it by hand** — so a manual cast shows up here the same as an automated one:

- A **single-target buff you hand-cast at a party member** (`gbls fuj`): you needn't type their whole name, and the buff's success line (wording from the game-data spell message) names the member in full, which the client matches back to whoever you targeted and lights up **their** bar. A whole-party or self buff you hand-cast (`unfa`, `bles`) registers the same way.
- The client deliberately **ignores the `stat` screen's buff list** (Paradigm's `You feel …! (Ns)` lines): those shared effect messages can't say which buff is which, so they're never treated as a cast.

**Death and disconnect are handled to match the game:**

- **Death** wipes all your magical effects, so your own death clears your self-buff timers, and a party member's death clears the timers you hold on that member.
- **A hangup / reconnect does not** — your buffs persist server-side through a brief link-drop, so on reconnect the watchdog **keeps** every timer (self and party) at its real remaining and does **not** rebuff. Only timers whose duration actually lapsed while you were offline drop and recast.

Switching characters starts the watchdog empty.

The window is a live view — it refreshes about once a second while open. The two timing gates that decide *when* the buff engine may cast — self-bless "while resting / during combat" and party-bless "while resting / during combat" — still live in Settings (**Settings → Spells** for self, **Settings → Party** for party).

## Wire Inspector (F5)

Press **F5** to open the **Wire Inspector** — a troubleshooting view of the data the server sends, in up to three panes you toggle with the **Raw / Stripped / Classified** checkboxes:

- **Raw** — control codes made visible (e.g. `^[` for escape).
- **Stripped** — the same stream with the ANSI escape sequences removed.
- **Classified** — each combat-window line tagged with how the combat engine read it (e.g. `[Combat: Monster Miss (you)]`, `[Combat: You Hit]`, `[Combat: Armor Block (you)]`). It also marks each **recognized monster death** with `[Monster Death: <name>]`, and an exp-inferred death whose message *wasn't* recognized as `[Monster Death: inferred from exp — message not recognized]`, so an unrecognized death line stands out.

**Raw and Classified are on by default** (Stripped off); unchecking a pane collapses its column so the others fill, and your choice sticks. It shows inbound server output only, and keeps the most recent 64 KB.

- **Pause / Resume** freezes the view so you can read it; **Clear** empties the buffer.
- **Auto-scroll** keeps the panes pinned to the newest bytes, and **Sync scroll** ties the Raw and Stripped panes' scrolling together.
- **Find next** locates a term in the Stripped pane, and **Export raw… / Export stripped… / Export classified…** save any pane to a file.

Reach for this when reporting a display or parsing glitch — it shows exactly what arrived on the wire. Because **Raw and Classified are on by default**, a **Bug Report** attaches the last 750 lines of each unless you turn them off — so a combat-recognition problem lands with the exact wire and the engine's read of every combat line and death.

---

# Settings Menu

MudPlay is a Telnet terminal client for MajorMUD / MegaMUD-style BBS door games. On top of a faithful terminal, it layers a large automation suite — auto-combat, auto-healing, auto-spellcasting, navigation/looping, party coordination, cash and item collection, and more — and almost every piece of that automation is tunable. This guide documents every one of those tunable settings: what it does, what happens when you change it, and where to find it.

**What these settings control.** Broadly: how your character fights, heals, casts spells, and buffs; how the client walks you around the map and loops between monster spawns; how it handles party coordination, chat, and remote `@`-commands from other players; how it manages coin and item pickup; how the terminal looks and behaves; and various connection/reconnection behaviors for the BBS itself.

**Where to find them.** Almost everything lives in one place: the **Settings window** (opened from the toolbar, the View menu, or its keybind — default varies by build). It's organized into tabs down the left side: General, Toolbar + Shortcuts, BBS + Display, Health, Spells, Combat, Party, Cash, Statline, Talk, Auto-Light, Auto-Lair, Auto-Trainer, Other, Events, and Sounds. A search box at the top of the window filters the tab list.

Two related editors live outside this window: the **keybind rebind dialog** (opened from a row on the Toolbar + Shortcuts tab) and the **macro editor** (a separate Game Data dialog).

**Where settings are stored.** MudPlay never stores a setting in one flat file. It uses a four-tier hierarchy — **Defaults → Global → BBS → Character** — and each tab's fields belong to one specific tier.

Every tab makes this visible: its controls sit under **banner-headed sections** naming the tier they save to (Global client settings / BBS settings / Character profile settings), so you can see at a glance where a change lands. A tab whose settings are all one tier shows a single banner; the mixed tabs (BBS + Display, General, Toolbar + Shortcuts, Other) split into a section per tier. The tiers:

- **Character-tier** (the vast majority of settings — Combat, Spells, Health, Party, Cash, Talk, Auto-Light, Auto-Lair, Auto-Trainer, most of General, keybinds, macros) live inside that character's own profile file and only apply to that one character.
- **BBS-tier** (connection info, reconnect behavior, terminal size, per-BBS realm quirks) live in that BBS's own file and are shared by every character who plays there.
- **Global-tier** (a handful of install-wide toggles — navigation-line colors, the Pyramid/Asylum puzzle solvers, confirmation prompts, the Help-menu website list, player-database cleanup) apply to every character on every BBS on this install.

All of this is stored under a single MudPlay data folder (`~/.local/share/MudPlay/` on Linux, `%AppData%\MudPlay\` on Windows, `~/Library/Application Support/MudPlay/` on macOS) as JSON files that only record *deltas* from the tier below them — so an unmodified setting isn't written to disk at all.

**Does MudPlay save automatically?** No — the Settings window uses an explicit **OK / Apply / Cancel** model. Edits are staged in memory; **OK** applies every changed tab and closes the window, **Apply** applies without closing, and **Cancel** (or the window's X button) discards everything you changed since opening it. A few things outside the main Settings tabs are the exception and save the instant you change them: keybind rebinds, macro edits, the Events tab's list, and the "Disable all events" toggle.

**Before you start changing things — a few things worth knowing:**
- Nearly every setting documented here takes effect **live**, with no restart or reconnect required — this guide calls out the exceptions explicitly (e.g. terminal scrollback size, a handful of BBS-connection fields that only apply on the *next* connect).
- A handful of controls exist in the UI but currently **do nothing** — they're either genuine stubs (the whole Sounds tab) or fields that were built but never wired into the automation engines (Combat's *Polite mode* and *Show combat round totals*). This guide flags every one of them explicitly rather than describing invented behavior.
- Many settings only matter once a corresponding **master switch** is on. For example, the entire Auto-Light tab only matters once the Auto-Light engine itself is enabled (Settings → General, or its toolbar toggle); Combat/Spells/Health settings only matter while Auto-Combat is on.

## Local control API

**Settings → General → Local control API**, off by default. When on, MudPlay serves a small HTTP API on **127.0.0.1** (default port **6683** — MMUD on a phone keypad) exposing what the client currently believes, so a stuck or misbehaving session can be inspected **while it's happening** rather than reconstructed from a bug report afterwards. That difference matters: the program log keeps only the most recent entries, so by the time a problem is noticed, the moment that explains it has often already scrolled away.

**What it exposes** (all read-only):

| Endpoint | What you get |
|---|---|
| `/health` | Whether MudPlay is up. The only endpoint that needs no token. |
| `/state` | Whether it's **connected** (and whether a redial is armed), live vitals, room and tracker confidence, engine states, **which pause gates are asserted**, combat target. |
| `/state/full` | Every section a bug report captures, as JSON — add `?format=markdown` for the familiar rendered form. Builds the whole report, so repeat calls within a second reuse the previous one; poll `/state` instead if you want a fast tick. |
| `/gates` | Recent pause/resume history: which gate, **who asserted it**, why, and when. |
| `/log` | The program log, filterable by `severity=` (comma-separated names) and `source=`, with a `since=` cursor for tailing. |
| `/scrollback` | The terminal transcript tail, with per-line timestamps. |
| `/events` | A live stream (Server-Sent Events) of log entries and gate changes as they happen. |
| `/loops` | Every saved loop with its area, room and lair-room counts, median and max exp of what spawns on it, the hardest-hitting monster, and the toughest three. |
| `/loops/{name}` | One loop in full — each waypoint with its room name and the monsters at that stop. |
| `/rooms/{map}/{room}` | A room: name, exits, its lair tag, and its monsters grouped as lair / placed / assigned, exactly as the map's ROOM INFO panel groups them. |
| `/monsters/{id}` | A monster's record — exp (with its multiplier applied), HP, AC, resists, attacks and drop table. |

**Access.** Two things are required, not one:

- **Loopback binding** — the socket is bound to loopback, so nothing outside your machine can reach it. But that alone isn't enough, because any program on your machine (or a web page you happen to be visiting) can also reach 127.0.0.1.
- **A bearer token** — every request must carry `Authorization: Bearer <token>`. Requiring a header is what stops a random web page forging a request.

The token lives in `.apitoken` in your app data folder, readable only by you, and **Show token** in Settings reveals it. **Regenerate** replaces it, immediately invalidating anything still using the old one — use that if it ends up somewhere it shouldn't. The token is never written to the program log, and a bug report records only whether the API was on and listening, never the token itself.

Reading the log with `curl`:

```
curl -H "Authorization: Bearer $(cat ~/.local/share/MudPlay/.apitoken)" \
     'http://127.0.0.1:6683/log?severity=warn,error&limit=50'
```

(On macOS the path is `~/Library/Application Support/MudPlay/.apitoken`.)

**Issuing commands.** Beyond reading, the API can act:

| Endpoint | What it does |
|---|---|
| `POST /command` | Runs any `@`-command — `{"command":"@goto","args":["Newhaven"]}`. Replies the command would have telepathed back come to you in the response instead of going out on chat. |
| `POST /send` | Types one line at the game exactly as if you'd typed it in the terminal. |
| `GET /commands` | Lists every dispatchable command with its permission category and whether it counts as destructive. |

These reuse the same handlers as the `@`-commands a party member can send you, so behaviour is identical — no second implementation to drift. The **per-player permission check is skipped**, because that gate answers "may this *other player*, over chat, do this to me?" and the answer is meaningless for whoever is holding the keyboard. Your own master switch (Settings → Talk → disallow remote commands) and the unconditional hard-blocks (reroll) still apply. Every local invocation is logged at Info, so the program log shows what drove the client even with Debug diagnostics off.

**Allow destructive commands** (the second checkbox, off by default) governs anything that ends the session or can't be undone — `@suicide`, `@hangup`, `@relog` — and the raw `POST /send`, which is destructive by nature since a raw line can carry any command the game accepts. While it's off those return *403* and nothing is sent.

Which commands count is derived from each command's permission category rather than a hand-kept list, so a command added later is classified automatically instead of defaulting to allowed; an unrecognised command name is refused too. `POST /send` also rejects multi-line input — one command per request, so nothing can smuggle a burst past the game's own rate limiting.

**Notes.** Requests are logged at Debug, so individual requests only appear in the program log while Debug diagnostics are on (the *actions* are logged at Info regardless). `/state/full` and `/scrollback` answer *503* until a terminal session exists.

The status line under the checkbox says whether the socket actually came up — if the port is already taken, that's where it tells you. A bug report records whether the API was listening **and whether destructive commands were allowed**, since that changes how the rest of the report should be read.
**Comparing loops.** `/loops` exists so "where should I be hunting?" is answerable without opening each one. Note that exp is reported with the **monster's exp multiplier already applied**, which is the number that actually matters and can differ from the raw table value by orders of magnitude — a loop showing a million-plus median is boss content, not a grind circuit. Lair monsters (campable, respawn on a timer) are reported separately from placed fixtures and assigned roamers (which wander in on their own schedule), because a room full of roamers is not a loop you can pace.

**Notes.** Requests are logged at Debug, so they only appear in the program log while Debug diagnostics are on. `/state/full` and `/scrollback` answer *503* until a terminal session exists.

Endpoints that read live state do so on the UI thread and give up after five seconds, answering *504 ui thread unresponsive* — which is itself worth knowing: if a client looks frozen and the API says 504, the freeze is the UI thread, not the connection. The status line under the checkbox says whether the socket actually came up — if the port is already taken, that's where it tells you. This release is **read-only**; issuing commands through the API is a separate feature.


---

## General

Settings → General. Everything here is character-tier (follows the loaded character) except a few install-wide (Global-tier) items — the navigation-line color block, the startup-animation toggle, window snapping, the recent-profiles count, and the Local control API block — which apply to every character on the install. No character loaded means this whole tab shows a "load or create a profile" banner instead of controls.

### Data files (directory display)

**What it does:** Shows the resolved path to MudPlay's data folder, with an "Open Data folder…" button (opens it in your file browser) and a "Change…" button (relocates every file under that folder to a new location and restarts the app).
**Important notes:** This is informational, not a saved setting. "Change…" triggers a full app restart at the new location; MudPlay validates the destination is empty, writable, and not nested inside the current folder before allowing the move.

### Terminal font (family + size)

**Default:** Family = bundled MX437 IBM VGA 8×16 CP437 bitmap font; Size = 12 pt.
**Available options:** MX437 (bundled), JetBrains Mono (bundled), plus every monospace font installed on your system. Sizes: 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28, 32.
**What it does:** Controls the font the main terminal canvas renders with. MX437 reproduces classic BBS CP437 output (box-drawing characters, line art); JetBrains Mono is a clean modern monospace alternative if you don't care about retro accuracy.
**When you might change it:** Switch to JetBrains Mono (or any installed system monospace font) if the block-drawing glyphs in MX437 look odd on your display, or if you want smoother, more modern-looking text — especially combined with "Scale terminal output to fill the window": a real font like JetBrains Mono renders crisp and antialiased at any zoom level, unlike MX437's bitmap glyphs, which upscale as blocky pixels to preserve their authentic retro look.
**Important notes:** The size is a true **point size** — the same unit MegaMUD and every Windows font dialog use, so picking "16" here matches MegaMUD's "16" glyph-for-glyph. Live-previews on the terminal canvas the moment you change the picker — no need to click Save first to see it. Clicking Cancel (or the title-bar X) reverts the canvas back to your saved font; only Save keeps the change.

### Navigation tooltip font (family + size)

**Default:** Family = MX437; Size = 13 pt.
**Available options:** Same font list as the terminal font; same size list, defaulting smaller.
**What it does:** Controls the font used only by the room-name tooltip that pops up when you hover over the Navigation map — independent of the main terminal font.
**Important notes:** Applies to the next tooltip hover after you save — no restart needed.

### Scale terminal output to fill the window

**Default:** Off
**What it does:** When on, the terminal's fixed 80×25 grid stretches to completely fill the window — width and height scale independently, so there's never a gray bar on any edge, on any window shape. On a window whose proportions don't match the grid's, characters stretch slightly wider or taller rather than leaving dead space.
**When you might change it:** Turn it on if you run MudPlay maximized or in a large window and don't want dead space around the text.
**Important notes:** Applies live and keeps re-fitting as you resize the window, so it doubles as an "auto-fit to window" for anyone wanting the terminal to always fill the current window size exactly.

The zoom never renders past an effective 32pt (the largest size in the Font size picker) regardless of your chosen Font size — this keeps a small chosen size from getting blown up to look identical to a large one, at the cost of possibly not quite filling an unusually large window when a small size is chosen.

With a real font selected (JetBrains Mono or a system font, not MX437), the zoomed text stays crisp and antialiased at any size — MX437's bitmap glyphs upscale as blocky pixels instead, on purpose, to keep them authentic. This setting resets to Off whenever you close a profile, since it's stored per-character.

### Keep typing directed at the terminal when other windows are open

**Default:** On
**What it does:** With this on, keys you press while a non-terminal window (Settings, an editor, etc.) is focused still reach the game — so you can keep sending commands with a dialog open — unless you're actually typing in a text box in that window, or the key is something the window itself needs (Tab, Escape, a menu shortcut). Turn it off to make keystrokes go only to whatever window is currently focused, like a normal application.
**When you might change it:** Turn it off if you notice game commands accidentally leaking through while you're trying to type into a settings field.
**Important notes:** Applies live, no restart needed.

### Show the mud-throwing startup animation

**Default:** On
**What it does:** Plays a small animated splash on the terminal while MudPlay is starting up, before you've connected or loaded a profile. Turning it off shows just a static title/byline instead.
**Important notes:** This is actually an install-wide preference (not per-character) even though it's edited from this character's tab — it's stored on the app's default profile so it survives switching characters. Applies immediately; a splash already playing stops the moment you turn it off.

### Snap windows together

**Default:** On
**What it does:** As you drag the panel windows (Conversation, Party, Buff Watchdog, Player Workshop, Navigation, Spell Book, Session Stats) they snap flush to each other's edges when brought close, and dragging the main window carries the whole snapped cluster with it. Turn it off to let every window float independently. See **The windows → Snapping windows together** for the full behavior.
**Important notes:** Applies live. Editors and dialogs opened from inside a panel don't snap.

### Recent profiles shown in File menu

**Default:** 5 **Range:** 0–10
**What it does:** How many recently-loaded characters the **File → Recent** submenu lists. The client always remembers the last ten, so raising this reveals more without your having to re-load them; lowering it just shows fewer.
**Important notes:** Install-wide (Global tier). Applies on Apply.

### Buff Watchdog layout

**Default:** Config above bars
**Available options:** Config above / below / left of / right of the timer bars.
**What it does:** Chooses where the Buff Watchdog's config panel sits relative to its timer-bar side. Changing it reflows an open Buff Watchdog immediately.
**Important notes:** Per-character. The splitter position, collapse state, and window size are remembered separately (see the **Buff Watchdog** section).

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
**Important notes:** Only checked once, right when a profile loads — not something that re-triggers mid-session. This covers **every** way a profile loads: opening one from File → Open / Recent, **launching** straight into it (a `--profile` start, or a new instance spawned by the Profile Management window's **Load**), and swapping between profiles. One case overrides it: a client restarted by **Update the Client** reconnects if you were connected when you kicked the update off, whether or not this box is ticked — it's restoring the session it interrupted, not auto-connecting.

### Load last ran loop

**Default:** On
**What it does:** When checked, hitting the Navigation window's **Loop mode** chip pre-loads the last loop you ran this session into the builder — ready to **Run** again or re-**Save**, or to wipe with **Clear all** and build fresh. This is what gets you back on a loop quickly after a stop / `@stop`, or lets you re-run an ad-hoc loop you never saved. Uncheck it to have the Loop chip always open an empty builder.
**Important notes:** Independent of the **`@loop last`** remote command, which re-runs the last loop regardless of this setting (the client always remembers the last loop run this session).

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

### Sprint Mode (toolbar toggle)

**Default:** Off
**What it does:** A transient "just get me there" movement toggle (running-figure icon, next to the movement Start/Pause/Stop buttons) — not a settings-tab checkbox. When on, movement **never pauses to rest or wait for HP/MA to recover**, no matter how low they get; your configured heal spells still fire normally on their usual thresholds while you keep moving.

Turning it on also **forces off Auto-Combat, Auto-Get Items, Auto-Search, and Auto-Get Cash** for the duration — a "never stop" run has nothing to fight, loot, or search for — and remembers exactly which of those were on so it can put them **back** when Sprint ends. Every other safety pause (avoid rooms, hazard/trap detours, teleport-maze solving, party sync, mortally-wounded) is untouched. The only thing that force-stops a sprinting character is death.
**It turns itself off** — restoring the engines it silenced — the moment it has done its job:
- a **go-to walk** reaches its destination;
- a **loop** begins looping — whether that's arriving at the loop's start after a walk-to, or wrapping into the next lap;
- an **auto-lair** circuit is about to enter the next lair (you sprint the travel there, then cross the threshold with combat back on to fight it).

Manually turning **any of those four engines back on** while Sprint is running also ends it (the two are mutually exclusive) — the engine you clicked stays on and the others it had silenced come back too.
**Important notes:** While active it's an "arrive or die" mode — use it for a route you're confident the character survives taking hits the whole way, since a hostile room is walked straight through rather than fought. It's designed to be flipped on for a single leg (a go-to, one loop lap, one hop between lairs) and clean up after itself.

---

## Keybindings

Not its own Settings tab — the rebind editor is a small popup dialog opened from a row on **Settings → Toolbar + Shortcuts**, one instance per action you want to rebind.

### What's rebindable

Every built-in action that has (or can have) a keyboard shortcut: connection toggle, opening the Navigation/Backscroll/Conversation windows, movement start/pause/stop, capture toggle, the function-key row (Player Workshop, Spell Book, Game Data Browser, Program Log, Wire Inspector), and the Ctrl-cluster actions (Save profile Ctrl+S, Profile Management Ctrl+P, Quit Ctrl+Q). A few actions (Open Party, Open Session Stats, Open Settings) ship with **no** default shortcut — you can only reach them via their toolbar button or menu until you assign one yourself.

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
| Save profile | Ctrl+S |
| Profile Management | Ctrl+P |
| Quit | Ctrl+Q |

### How rebinding works

**What it does:** Click **Capture**, then press the key combination you want — the dialog waits for you to release a non-modifier key (so you can hold Ctrl/Shift/Alt first, then land on the target key) to lock it in. Press **Esc** to cancel without changing anything. **Clear** removes the shortcut entirely, leaving that action with no keybind until you assign a new one.
**Important notes:** A captured combo is checked live. Some collisions show a red error and block Save:
1. Reserved keys — Enter, Escape, Tab, Backspace, Delete, the lock and system keys (Caps Lock, Num Lock, etc.), and specifically the main-row period key (`.`), which MudPlay reserves because a leading period is MajorMUD's say-precursor. (The numpad period stays bindable.)
2. System combos — Alt+F4, Ctrl+C, Ctrl+V can never be rebound.
3. A user-defined macro (see below) already using that combo — macros and keybinds share one conflict list, and a macro isn't in this list to be re-pointed, so a combo can never be assigned to both.

If the combo is already bound to **another built-in action**, that's *not* an error — the dialog shows an amber warning naming that action ("*X* is now unbound"), and saving **steals** the combo: it's unbound from that action and moved to the one you're editing. The stolen-from action's row drops to "unbound" in the list on the spot, so a key only ever drives one built-in action at a time (no need to manually clear the old one first).

Rebinding takes effect **immediately** on Save — it doesn't wait for the Toolbar tab's own Apply button.

### Scope

Per-character, not global — each character can have entirely different shortcuts. Closing a profile resets the in-memory bindings back to defaults for whichever profile loads next. "Reset all shortcuts" (on the Toolbar + Shortcuts tab) wipes every custom rebind on the current character back to the factory table in one click.

---

## Macros & Aliases

Two per-character typing shortcuts, both managed in the **Game Data Browser** (F3), not the main Settings window, and saved the moment you change them — there's no separate Apply step, unlike most Settings tabs. **Macros** bind a key combination to a command; **aliases** expand a typed word into a longer command. Macros share the built-in keybindings' "can't double-book a key" check; aliases are instead checked against MajorMUD chat commands so they can't hijack your chat. (For the step-by-step of building either — plus triggers — see the **Macros, aliases, and triggers** section.)

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

Settings → "BBS + Display" — despite the plain "BBS" name in some places, this tab also carries terminal-size/scrollback settings, the per-character credentials + logon steps, and the four global confirmation-prompt checkboxes. **Adding, removing, and renaming BBSes now lives in Profile Management** (View → Profile Management, or the button on this tab's left rail); this tab's list is for **selecting** a saved BBS to edit its details, and selecting one here only edits it — it never moves your loaded character (that's Profile Management's *Assign to BBS*).

To make each setting's persistence level obvious, the tab is split into three banner-headed sections:

- **BBS settings** — stored with the board, shared by every character on it: connection, retry/reconnect, display size + scrollback, game-menu commands, realm mechanics, board disconnect line, runic-currency name.
- **Character profile settings** — only for the loaded character: username/password, the read-only captured suicide password, SYSOP powers, the Sys Goto table, and the automated logon-menu steps.
- **Global client settings** — app-wide, regardless of BBS or character: the confirmation prompts, documented separately below.

You can edit **several boards in one visit**: click between them freely and everything you changed — connection fields *and* credentials/logon steps alike — is written when you press **OK**. **Cancel** (or the title-bar X) still throws away every board's pending edits, not just the one on screen.

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
**What it does:** Four independent triggers for automatic redialing:

- a failed initial connect attempt;
- the connection dropping mid-session;
- the server going silent long enough for the No-response check above to flag it dead;
- the BBS's scheduled nightly cleanup finishing.

"After Cleanup" is a two-part behavior: it also makes MudPlay proactively exit the realm and drop the connection *before* the BBS forcibly disconnects it, once a "shutting down soon" warning is seen. It waits for a safe room (no hostiles, not mid-fight), sends the exit command, and drops the carrier the moment the game confirms your character has been saved — so it disconnects cleanly regardless of which menu your board drops you to after leaving the realm.
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

### I have the following SYSOP powers

**Default:** all off
**What it does:** Declares which elevated sysop commands this character can actually use on this specific board — three independent checkboxes, saved per-character per-BBS. None of them touch `@goto`: that remote command is gated purely by the per-player **Move player** permission on the Players tab, not by anything here. Only tick a power if you genuinely have that sysop access on the board — the underlying command is refused on an ordinary account.

**Sysop status** — lets MudPlay use the game's **`sysop status`** command (`sys st`), which prints the server's own debug dump for a room — including its **true map and room number**. That exact number is the fastest possible answer to "where am I?": without it, a client that loses track has to walk you backwards one room at a time until only one room fits, and if that fails you're left right-clicking **I am here** on the map. With this power, one command replaces all of that.

MudPlay asks at every point it would otherwise start reversing moves or give up — the first sign of a mismatch, a wedged engine, the moment before backtracking, the last resort before declaring **Lost**, a loop blocked because it lost its place, and a `@where` re-fix — mirroring how the Paradigm `room` command is used on that realm. If the answer doesn't come back (refused, too slow, or naming a room your active game-data set doesn't contain) nothing changes: you get the same walk-backwards recovery and **Lost** dialog you'd get without it. It never guesses.

Because a `sys st` dump is much larger than Paradigm's one-line `room` reply (8+ lines, more with items on the floor), a few restraints keep it from flooding your screen:

- repeated asks are spaced out;
- it won't ask while a move you've already sent is still unconfirmed (the answer would describe the room you just left);
- it stays out of the way while a teleport maze is being solved, since the maze solver does its own position fixing.

**Sysop god lives** — when this character dies, MudPlay automatically sends **`sys god <your name> add life`** to restore the life just spent. One send per death; refused (and harmless) without real god access.

**Sysop goto** — lets you teleport to a named location with the game's **`sys goto <location>`** command, and adds a **Sys Gotos** flyout to the terminal right-click menu (and Walk menu), plus the room right-click menu on the Navigation map, listing your configured locations.

Ticking this reveals a **Sys Goto locations** table on the BBS tab where you edit the keyword→destination list:

- a **Location** keyword (sent to the game verbatim);
- the **Map** and **Room** it lands you in (MudPlay uses these only to work out where you ended up and show the room name it resolves to);
- an optional **Min level** gate that greys the entry out until your character is high enough.

A fresh install seeds the usual starter towns (newhaven, silvermere, rhudaur, khazarad, lostcity). A `sys goto` produces no message in-game — only a statline redisplay — so MudPlay sends a bare Enter afterward to pull up the room you landed in and re-fix your position on the map. You can `sys goto` out of a room full of hostiles, but **not while you're actively in combat**: if an attack is in progress MudPlay sends `break` first and tells you to run it again once the fight stops. Refused (and harmless) without real sysop access.

Beyond the menus, the **navigation engine routes through your goto locations automatically**: when you walk somewhere, if firing a `sys goto` and walking from the landing is shorter than the overland path (or the only way there), MudPlay takes the jump as part of the walk — no manual step.

It only does this for locations you can reach: a level-gated location is skipped by auto-routing whenever your level is unknown or below the gate (a manual fire still trusts you). If a hostile is engaged when the walk reaches the jump, it waits — the same as any other step during combat — and fires once the fight clears.

**When you might change it:** Only tick a power if you genuinely hold it on that board. MudPlay can't tell in advance — on an ordinary account the command is simply refused, so it tries once, gets nothing back, and stops asking for a few minutes before trying again.

Once `sys st` has answered even once, it's trusted for the rest of the session and never switches itself off again: if it works at all, it works. That costs you an occasional rejected command rather than a stream of them, and it means one slow reply can't switch the feature off for your whole session. There's still no benefit to ticking a power hopefully. Left off, that `sys` command is never sent.

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

Settings → Combat. Two switches live *outside* this tab and gate everything here: **Auto-Combat** (Settings → General, or its toolbar toggle) must be on for any of this to matter at all; **Auto-Nuke** separately gates **both multi-attack** slots and the **AoE-debuff** slot (single-target attack spells aren't considered "nukes" and stay available regardless). The **single-target debuff** is part of the attack rotation, so it follows **Auto-Combat**, not Auto-Nuke.

A debuff slot only accepts a **0-energy** between-round spell — an attack spell (which costs energy) can't be a debuff — with **slot-appropriate targeting**: a single-enemy scope for the single-target slot, an area/room scope for the AoE slot. A mismatch (an attack spell, or a targeted spell in the AoE slot / an AoE in the single slot) is flagged right under the slot on this tab and refused at cast time with a program-log note.

Only **one 0-energy between-round spell** fires per combat round (the game's own limit — a heal, cure, buff, or debuff, whichever your Settings → Spells priority ranks highest that round). So a room-entry round where a maintenance buff or heal is due can take that slot and leave the AoE debuff to the *next* round. By default the AoE debuff stays "owed" and keeps trying each round until it lands once for the room. The **"Only cast on the first round in a room"** checkbox under the AoE-debuff slot changes that: with it ticked, the AoE debuff is only attempted on entry (before the first combat round) and is **abandoned for the room** once the fight is underway — a debuff that lands on round 2+ (against a half-dead pack) is mostly wasted mana, so this skips it rather than spend the cast late.

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
**Available options:** `Default`, `AttackLastParty`, `AttackLastRoom`, `AttackAfter`, `AttackNotLast` (shown verbatim in the dropdown).
**What it does:** Pure timing — controls *when* you re-announce your own current action relative to other people's attacks, for coordinating who "goes" in what order. It re-issues whatever you're actually doing this round — a weapon swing, a single-target attack spell, or a bare room spell — so a caster lands last just like a fighter (re-announcing a combat spell costs no mana; mana is spent once when the round fires). It never changes *what* you're targeting — that's Target Priority's job. A party member's **room attack** counts as their commit too: when someone rooms (you see *"… moves to attack everyone in the room"*, or on Paradigm *"… is poised to assault the room"*), your own room spell re-announces after theirs so you room last.
- **AttackLastParty / AttackLastRoom** — re-announce after *every* qualifying commit, so you stay last (party members only, or anyone in the room).
- **AttackAfter** — re-announce only after the named player commits (set the name in **Attack-after player name**).
- **AttackNotLast** — the inverse: *hold* your pick on room entry, commit **once** right after the **first** party member announces, and never re-fire — so you slot in behind the first mover instead of chasing the last slot. Only functional in a **party of 3+**; in a party of 2 or fewer it behaves exactly like `Default`.
**When you might change it:** A tank who wants to always commit their attack last, after everyone else in the party has already gone — or a roomer who wants their AoE to land after the party's. Pick **AttackNotLast** when you'd rather go early, right behind whoever opens.

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

### Kill all engaged

**Default:** Off
**What it does:** Sits right below the Min/Max pickers. Once a room has been **engaged** because its hostile count met **Min. monsters**, it keeps fighting per your combat settings until the room is cleared — instead of moving on when kills drop the count below **Min. monsters**. Useful for areas where monsters have **different HP pools**, so the first wave leaves the tanky ones alive.

Off (the default) is the current behavior: if you engage a room of 8 with Min set to 3 and kill 6, it leaves the 2 survivors and moves on. On, the engine stays and clears the room to empty, then the walker continues.
**Important notes:** Only bypasses the **minimum** — the **maximum** (too-crowded room) and every other gate still apply — and only for rooms you actually **engaged** (a room whose count never met the floor is skipped as before). An HP/MA flee still overrides it, so a survivor that's beating on you will still trigger your run threshold.

### Do BS attacks (backstab)

**Default:** Off
**What it does:** When on, attempts a backstab as the very first action when you enter a room with a sneakable target. Backstab only ever lands on that opening action — once anything else has happened in the room (a spell, a swing, another backstab attempt), the surprise is gone for that room until you leave and re-approach freshly.
**Important notes:** A monster with the "see-hidden" ability reveals you before the opener, forcing a normal attack instead. A successful backstab is silent (no public "moves to attack" announcement) — you only know it worked from the "surprise" damage line.

### Don't BS if multi-attack room spell is firing

**Default:** On
**What it does:** Skips the backstab attempt whenever the room holds enough enemies to trigger your configured room-wide attack spell, so you don't waste a sneak opener on an AoE round. Keyed on the enemy count meeting the Multi-attack 1 slot's minimum (not on whether you currently have the mana), so the opener is skipped consistently in a room you mean to room-spell.

### Run if BS fails

**Default:** Off
**What it does:** Automatically triggers flee behavior if your backstab attempt clearly failed (no "surprise" in the result line) — on the theory that a failed backstab means the target is now fully alert and the fight is riskier than planned.

### Clear hostiles when sneak broken by see-hidden monster

**Default:** Off
**What it does:** A safety valve for stealth routes: while Auto-Sneak is on (you're trying to sneak through a route untouched) and you stumble into a room with a see-hidden monster, your stealth breaks. With this on, MudPlay fights and clears that one room instead of continuing to walk while exposed and dragging monsters behind you — bypassing the Min/Max room-skip gate for just that room, then re-sneaks and carries on. Because the room is now clear, any buff/cure the sneak-aware timing was holding fires there before you re-sneak.

This works whether **Auto-Combat is on or off**: the whole point is to clear the room and get moving again, so it force-clears regardless of your combat toggle (with Auto-Combat off it engages just for that room; with it on, it overrides the Min/Max gate so the room can't be skipped and left to drag).

### Run distance

**Default:** `2` rooms
**What it does:** How many rooms MudPlay flees before re-checking whether it's safe to stop.

### Go backwards if running

**Default:** On (backward)
**What it does:** When fleeing, `Backward` retraces the rooms you just came through (safer — you already know what's there); unchecked (`Forward`) instead keeps pushing along your planned route into unexplored territory (faster, riskier).

Either way a flee only ever sends plain compass moves, so it **stops short at anything that isn't one**: a lever or door step, or a teleport hop (the way in and out of somewhere like the Negative Power Plane). It retreats as far as the ordinary moves go and re-checks there rather than trying to cross it mid-fight — and if the very first step out is a teleport, it doesn't run at all and your other low-HP reactions take over. The program log names the step that cut the retreat short.

### Break combat before running

**Default:** On
**What it does:** Sends a `break` command before the first flee move so you disengage cleanly first. Turning it off starts fleeing immediately, which is faster but the game may reject the first move since you're technically still fighting, wasting a round.

### Minimum mana per cast — Percentage / Value

**Default:** `Percentage`
**What it does:** Decides how every "Min mana per cast" field on the five spell slots below is read — as a 0–100% share of your maximum mana, or as a flat number.

### Combat profiles (quick-swap loadouts)

**What it does:** Saves a whole combat posture under a name so you can keep **several** and switch between them in one click. This helps when different fights want different setups — a fire loadout for most monsters, a cold one for the fire-immune, a cautious "bossing" loadout with a two-hander and a lower flee threshold. Rather than re-tuning your Combat and Health tabs each time, you save each as a profile and flip between them.

**What a profile remembers (a full loadout):**
- the **six spell slots** + their per-slot gates, the mana-threshold mode, and the drain settings;
- the **attack commands** (normal / alternate);
- the **room thresholds** — min / max monsters and run distance;
- the **primary & alternate weapons** (+ their off-hands);
- the **entire Health tab** — rest / heal / flee / hangup thresholds, meditate / shadowrest, the emergency escape, and the pre-/post-rest commands;
- on the **Spells tab**, the **between-round spell-type priority order** and the **healing / regeneration picks** (Minor heal, Major heal, Emergency heal, HP Regen). The rest of the Spells tab (cures, bless timing, ailment gates) and the Buff Watchdog self-bless slots stay per-character.

Every group that swaps with the profile is wrapped in a **coloured "Combat profile: `<name>`" border** on the Combat, Health and Spells tabs, so you can see at a glance which settings are per-profile. On the Combat tab the per-profile settings — spells, weapons + verbs, **action order** (spells-first / physical-first / custom cycle), **backstab options**, room thresholds, and **kill-all-engaged** — sit at the **top**; the **shared** settings (targeting, display; and on the Spells tab the cures / bless timing / ailment gates) sit at the **bottom** under a **"Shared combat settings"** divider. Shared settings apply to every combat profile and don't swap with the chip.

**Each profile has its own colour.** Add a second profile and its chip picks up a distinct colour; the third another, and so on. That colour tints the profile's chip, all of its bordered groups (across the Combat, Health and Spells tabs), and the Workshop's Default-set weapon rows — so it's always obvious which profile you're looking at.

**Weapons — how they stay in sync:** a profile's weapons *are* the Workshop → Equipment Manager **Default** gear set's weapon slots (the surface the combat engine actually reads). So editing a profile's weapon pickers here and editing the Default set's Weapon / Off-Hand / Alt rows in the Workshop are the **same loadout, kept in sync** — and the Workshop shows a matching amber "Combat profile: `<name>`" marker over those rows. Switching a profile writes its stored weapons into the Default set, so your equipped weapon changes with the profile. (Backstab gear stays global on the Backstab set.)

**Setting them up (in Settings → Combat):** your current setup is already **Profile 1** — you always have at least one. The **Combat profile** selector sits near the top of the tab:
- Numbered **chips** (`1 2 3 …`) are your profiles; the **active one is gold**. Click a chip to load that profile into every bordered group (on both the Combat and Health tabs).
- **＋** adds a new, empty profile and switches to it, ready to fill in; **✕** removes the one you're on (the last one can't be removed).
- The **name box** just below the chips names the profile you're viewing.

This editor is **staged** — nothing is saved or used until you press **Apply** or **OK**. Switch chips, edit boxes (on either tab), add and remove freely; it's all held in memory and committed together on save. **Cancel** (or the title-bar ✕) throws every change away. Once applied, the active profile's combat settings take effect on the next round, its Health thresholds on the next rest cycle, and its weapons on the next combat weapon read.

**Switching during play** (these act on your *saved* profiles right away, without opening Settings):
- **Action menu → Combat Profiles** — a fly-out listing every profile; click one to switch.
- **Toolbar buttons** (add them under Settings → Toolbar + Shortcuts) — a **Combat Profile (cycle)** button that shows the active number (`P1`, `P2`, …) and steps through them (left-click = next, right-click = previous), or a **Combat Profile (menu)** button that pops the same fly-out.
- **`@profile`** — lets a trusted party member switch your profile remotely from chat (needs the **Alter my settings** permission). It accepts either:
  - the profile's **number** — the same chip number you see in Settings, so `@profile 2` selects the second profile; or
  - any part of the **name you gave it** in the name box — if you named a profile "Fire", then `@profile fire` (or even `@profile fi`) selects it. When the text could fit more than one name, it picks the closest match.
  - **no argument** — `@profile` on its own doesn't switch; it **reports the roster**: the active profile plus the others on standby, e.g. `{Current: 1)Fire, On Standby: 2)Cold, 3)Lightning}`.

**Every switch prints a one-line summary** to your terminal (and to the requester, for `@profile`) naming the profile now active and the spell in each slot, shown by its short **cast code** — the same code you would type to cast that spell. For example:

> `Combat profile 2 (Fire) — normal: fbl · alt: fs · drain: ll`

means profile 2 ("Fire") is live, casting `fbl` as the normal attack, `fs` as the alternate, and `ll` on the drain slot.

### Combat spell slots (Multi-attack 1 & 2 / Debuff AOE / Debuff single-target / Normal attack / Alternate attack)

**Default:** all unset (Multi-attack 2 also unchecked)
**What it does:** This is the heart of MudPlay's spell-combat automation — six rows, each assigned one role:
- **Multi-attack 1** — a room-wide damage spell, cast with no target (room spells hit everyone; naming a target gets it rejected by the game).
- **Multi-attack 2** — an optional second room spell that takes over once the first one is spent (see below).
- **Debuff (AOE)** — a room-wide debuff, also cast bare.
- **Debuff (single target)** — a single-target weakening spell.
- **Normal attack spell** — your primary single-target damage spell.
- **Alternate attack spell** — a backup single-target damage spell, used only once the primary can't fire that round.

Each row also has **Min enemies** (don't cast this slot below this many hostiles in the room — ignored on the three single-target rows *and* on Multi-attack 2, which shares row 1's), **Max casts** (a repeat cap — blank means unlimited, `0` means never, a number caps it; this counts combat *rounds* spent on the spell, not individual casts, and resets per-target for the three single-target rows but per-room for the three AoE rows), and **Min mana per cast** (a mana floor, read per the Percentage/Value toggle above).

**Multi-attack 2 (the cheap finisher):** off by default — tick the checkbox to enable it. It is **not** a rival to Multi-attack 1, it's its **successor**: the engine always tries row 1 first, and only reaches row 2 once row 1 is out of the running for the round — its **Max casts** cap is spent, or mana has dropped under its **Min mana per cast**. That's what lets you open with an expensive room nuke for a cast or two and then finish the pack with something far cheaper, instead of burning full price on every round.

A worked example: Multi-attack 1 = `blad` with Max casts `2`, Multi-attack 2 = `star` with no cap — the pack eats two rounds of dancing blades, then star finishes it for a fraction of the mana.

Two things are deliberately shared rather than duplicated. **Min enemies belongs to row 1 only** — the room has to qualify for row 1 before row 2 is ever considered, so a pack too small to be worth rooming doesn't get roomed by the second spell instead (that column shows `—` on row 2). And row 2 needs **row 1 to be filled in**; on its own it does nothing. Row 2 does keep its own **Max casts** and **Min mana per cast**, and its cast tally resets per room just like row 1's — every new room starts over from the opener.

**Picking a spell (learned-spell guard):** each slot is a typeahead — start typing a **cast-code or name** and it lists your class's spells (it commits the 4-letter code). Spells your character **hasn't learned yet** are shown **struck through and dimmed** in the list, and if a slot is pointed at one the box **outlines red** as a warning — so you can't quietly misconfigure a slot with a spell you can technically learn but haven't (the value is still saved; the red outline is only a heads-up). The same picker and guard are on **Settings → Spells** (heals, cures, bless).

The guard needs to know what you've learned: type `spells` (or `stat`) in the game once so it can read your spell list — until then nothing is flagged. It also updates the moment you learn a spell mid-session (reading a teaching item, e.g. *"You add agony to your spellbook!"*).

**How the cascade works each round:** A pending backstab always wins first. Then, whichever action type (spell or physical) your Action Order setting prefers gets tried; on the spell side, the order is Multi-attack 1 → Multi-attack 2 → Normal attack → Alternate attack, falling through to the weapon if nothing can fire.

Debuffing is a separate "extra" action that can land the same round as your main attack. Once you commit to a single-target spell against a specific monster and it later becomes unaffordable, MudPlay sticks with the weapon for the rest of that fight rather than flip-flopping back once mana regenerates.
**Important notes:** Once a spell is announced, it auto-repeats server-side every round exactly like a weapon swing — MudPlay does **not** re-send the cast command every round, only when the situation actually changes (target dies, cap hit, mana too low, target proves immune).

### Drain (life-steal) spell

**Default:** unset (HP trigger 50%, "Drains override AOE" off)
**What it does:** Some mage spells (e.g. `vamp`, `dtch`, high-level `nebo`) are **life-drain** spells — the damage they deal also **heals you**. This slot treats one as an *emergency heal that also attacks*:

- It takes the round in place of your normal attack **every round while your HP is at/under the Heal when ≤ HP percentage** (and you can pay its **Min mana per cast**), handing the round straight back to your normal pick the moment HP recovers **above** the trigger — no overshoot band, so it never keeps draining once you're healthy. Because a single life-drain heals a big chunk, one cast usually lifts you clear on its own.
- Its **Max casts** is a **per-target** cap (it resets when you switch targets, so each new target gets the full count) and is **uncapped by default** — leave it blank to let the drain keep healing you every round while you're hurt, or set a number to limit drains per target.
- **Min enemies** doesn't apply.

**Targeting:** a drain can only affect a **living, non-undead** target — there's no life to steal from a construct or a skeleton — so against a NonLiving or Undead monster the drain is skipped and MudPlay falls back to your normal attack cascade for that fight. (If game data is thin, the game's own "no effect" reply is caught as a backstop.)

**Drains override AOE:** by default the drain **yields to your room AoE** — if you have enough enemies present to trigger the Multi-attack spells, rooming is usually the safer play (and it yields to whichever of the two room slots is carrying the round), so the AoE keeps firing and the drain only overrides single-target / weapon rounds. Check this box to let the drain pre-empt the AoE too, when your loop calls for it.

A per-monster override configured in Game Data can substitute different spells (and a physical command) for a specific monster species — worth checking if a particular monster seems to diverge from your setup here. In the monster editor:

- **Debuff (single target)** / **Normal attack spell** / **Alternate attack spell** are spell **pickers** (type-ahead over your castable spells, committing the cast-code, same as the Settings → Combat spell slots) with a per-room **Max** cast cap and a **Mana** floor. Each substitutes into its matching rung and runs the *same* gates the configured slot does (Max cap, Mana floor, **and** the immunity / level / element-resist skip — it is **not** a bypass).
- A separate **Physical attack** box takes a raw verb (`attack`, `bash`) that replaces the weapon command only on a round the engine already chose physical.

The override applies to the monster record **placed or summoned in your current room**, so a name shared across zones (a "zombie" in the graveyard vs the tunnels) picks the right one — an override you set on the graveyard zombie won't bleed onto the tunnels zombie.

At **0 mana** a mana-costing action can't land (the server silently ignores it), so the engine falls back to your physical weapon and resumes casting once mana recovers.

### Show combat round totals ⚠️ Not currently functional

**Default:** Off
**What it's intended to do:** Append a running total of damage dealt to the terminal after each combat round.
**Important notes:** Like Polite mode above, this control is fully editable but **not consumed anywhere in the automation engine** — turning it on currently has no visible effect.

---

## Spells (+ Ailments)

Settings → Spells. This tab picks *which spell* fills each automated role and sets the *priority order* the caster walks through each tick. The actual HP/mana percentage *thresholds* that trigger a cast live on the **Health** tab, not here.

### Spell type priority

**Default order (highest priority first):** Emergency heal → Minor party heal → Major party heal → Downed-ally heal (rescue) → Minor self heal → Major self heal → Curing → Buffing → Debuffing.
**What it does:** Every tick, MudPlay checks all nine categories and casts the highest-priority one that has something ready to fire. Use the **▲ / ▼** arrows on each row to reorder them — higher in the list casts earlier.
**When you might change it:** Move Curing above self-heals if you'd rather cure a debilitating ailment before topping off HP; move Debuffing higher if landing your debuff matters more to you than proactive buffing. Emergency heal defaults to the top so a life-threat save leads, but you can move it like any other row.
**Important notes:** Emergency heal and the downed-ally rescue used to be hidden always-first casts; they're now ordinary rows in this list (defaulting to slots 1 and 4), so you can rank them wherever you like. Emergency heal keeps its special *gates* — it ignores the mana floor and fires in any state (see below) — but its *position* in the queue is now yours to set.

### Minor heal / Major heal

**Default:** unset
**What it does:** Your primary self-heal spell (Minor) and your bigger, life-threat self-heal spell (Major). Minor fires in the band between its own threshold and the Major threshold; once your HP drops into the (lower) Major band the Major heal **takes over** — Minor yields to it there by severity, so you don't have to re-order priorities to get the big heal at low HP.

If you can't afford the Major heal, it falls back to Minor rather than skipping the heal. If you haven't set a Major heal at all, MudPlay uses Minor heal at the Major threshold. The same severity rule applies to the party Minor/Major heal slots.

### Emergency heal

**Default:** unset
**What it does:** Your last-resort self-save, and a third configurable heal spell. It's a row in the **spell-type priority list** above, defaulting to slot **1** — so out of the box it fires ahead of every other between-round cast (Major/Minor heal, cures, blesses, debuffs, even a downed ally's rescue) the instant your HP drops to or below **Health → Emergency heal**. You can reorder it like any other category if you want something else to lead.

What stays special no matter where you rank it:

- It fires in **any** state — mid-fight, resting, or walking between rooms — where Minor heal only casts during combat or a rest.
- It **ignores the mana-floor gate** (Health → Heal if above MA) that holds Minor/Major back to conserve mana. It still won't attempt a spell you can't afford (nothing can), but it spends whatever's left rather than conserving, because there might not be a later.

If you leave Emergency heal blank, MudPlay falls back to Major heal, then Minor heal, at the Emergency threshold — so a low **Emergency heal** trigger with no spell set still gives your Major/Minor heal a true last-resort trigger point. Set the threshold below your Major heal (combat) trigger — Emergency is the "if all else has failed" band beneath it.

### HP Regen

**Default:** unset
**What it does:** A heal-over-time spell. When your Minor-heal threshold trips, this is cast *first*, ahead of an instant heal — but only while you're still above the Major/life-threat threshold, and only if it isn't already active. Inside the life-threat band, you always get an instant heal instead.
**When you might change it:** If you want this HoT kept up permanently rather than only cast reactively, add it as a maintained buff in the **Buff Watchdog** instead.

### Mana regen — moved to the Buff Watchdog

The mana-regen spell and its **reroll** knobs are no longer picked here. Add the spell in the **Buff Watchdog** (View → Buff Watchdog → ＋ Add buff) and set its conditions there:

- **Cast before resting for mana** — keep it up only while you're resting for mana (recast through the rest, including a combat interruption, until mana tops up), rather than maintaining it always;
- **Reroll below abil 145** — the threshold: reroll while the spell's rolled mana-regen contribution read off `abil 145` lands under it;
- **Max rerolls** — how many times to chase a better roll before accepting what landed.

Rerolling works on **Paradigm** (it reads the roll back from `abil 145`); each reroll still runs through the normal between-round priority, so a due heal or cure fires ahead of it. See the **Buff Watchdog** section for details.

### When HP full / When Mana full — moved to the Buff Watchdog

The "spend a maxed-out pool on something useful" casts are configured as ordinary buffs in the **Buff Watchdog** now — add the spell and tick **Only when HP is full** or **Only when MA is full** on the slot (they fire once you've rested up to your **rest-max** target). See the **Buff Watchdog** section.

### Cure Holds / Cure poison / Cure disease / Cure blindness

**Default:** unset
**What it does:** The specific spell used to cure each named ailment. These feed the Curing priority category (self first, then party members).

### Room light — moved to the Buff Watchdog

The room-light spell is configured in the **Buff Watchdog** now. Add it there and tick **Only when the room is dark** on the slot to keep the reactive cast-on-entering-a-dark-room behaviour (via the auto-light system); leave it unticked to maintain the light like an ordinary buff. See the **Buff Watchdog** section.

### Self-bless — moved to the Buff Watchdog

The self-buff slots (which spells, `#item`-cast buffs, per-slot recast timers) live in the **Buff Watchdog** now, folded into the one unified buff list alongside your party buffs — tick the **Self** box on a slot to cast it on yourself. See the **Buff Watchdog** section for how to add and target buffs. The two **timing gates** below — *when* the self-buff engine may cast — stay here on this tab.

### Bless self while resting / Bless self during combat

**Default:** both Off
**What it does:** Two opt-in overrides for your own buffs. With both off (the default), the engine buffs while you're **moving or standing idle** — including an idle rest — and holds off **during combat** and **during a triggered recovery rest** (HP or MA fell below your rest-if-below setting and you're resting back up). "Bless while resting" lets it also buff during that recovery rest; "Bless during combat" lets it also buff mid-fight (casting spends that round). Note "while resting" means a *triggered recovery rest* only — idle resting always buffs.
**When you might change it:** Turn on "during combat" for a fast hunting loop that rarely stays out of combat long enough to bless between fights; turn on "while resting" if you'd rather top off your buffs during recovery downtime than wait until you're back on your feet.

### Ignore poison / blindness / confusion / diseased

**Default:** all Off (i.e. every ailment pauses the party *and* is announced)
**What it does:** One toggle per ailment — the single "I don't care about this ailment" switch. Normally, catching one of these makes MudPlay ask the party leader to pause (`@wait`) until it clears **and** announces it on say (`.@blind`, so other MudPlay clients mirror it on their party display). Checking a box here suppresses **both** for that specific ailment — no pause request and no broadcast — useful for "push through it, don't stop the group" situations. (Poison is read from the party screen rather than said, so its checkbox only affects the pause.)
**Important notes:** Some conditions (over-encumbered, being held, being stunned) always pause regardless of these checkboxes — they can't be suppressed this way.

---

## Health

Settings → Health. Two stacked sections — **Health (HP)** on top, **Mana / Kai** below — each independently switchable between percentage and raw-number thresholds.

### Percentage / Value (mode picker)

**Default:** `Percentage` (both HP and Mana)
**What it does:** Switches whether every threshold in that section means "a percentage of your max pool" or "an absolute number." Switching modes doesn't rescale the numbers you've entered — each value is simply re-read against the new scale (and switching to Percentage clamps anything above 100 down to 100). A small live readout beside each field shows the equivalent in the other scale.

### Rest max (HP / MA)

**Default:** 95% (both)
**What it does:** Once resting, MudPlay stops and stands back up once the pool reaches this value. Both HP and Mana need to reach their own target before you stand (unless your class has no mana pool).

The percentage is read against your **Default gear set's** max HP / mana — so a Pre-rest HP/Mana set that swaps in an item which changes your max doesn't move the target you tuned — and it's capped at your current gear's real max, so a rest set that lowers your pool can never leave you resting for a level you can't physically reach. The **heal**, **flee (run)**, and **emergency-hangup** HP triggers anchor to the same Default-set max, so they fire at the HP you tuned regardless of what set is worn.

Only Default-set items you actually **have** (worn or carried) count toward that max — an item lost to a deathpile, sold, or never obtained is left out, and when you have none of them (or before your first inventory check) the **live** max is used instead. The figure beside each threshold says which basis it's using: **(def)** for the Default-set max, **(live)** for your current max.

### Rest if below (HP / MA)

**Default:** HP 60%, Mana 30%
**What it does:** The trigger for auto-resting. Once a pool drops to or below this, MudPlay pauses movement and starts resting the moment combat ends (never mid-fight).

### Heal (rest)

**Default:** 80%
**What it does:** While actually resting, cast the Minor heal spell if HP is still below this — a way to speed along recovery rather than waiting on the passive rest tick alone.

### Minor heal (combat) / Major heal (combat)

**Default:** Minor 70%, Major 40%
**What it does:** During a fight, cast the Minor heal spell once HP drops to this level, and the Major heal once it drops to the lower Major level.
**Important notes:** Both are also gated by a mana floor ("Heal if above," below) — if your mana is too low, the heal is skipped so mana can regenerate instead, unless that floor is set to 0. Emergency heal (below) ignores this floor.

### Emergency heal

**Default:** 20%
**What it does:** Cast Spells → Emergency heal (or, if that's blank, fall back to Major then Minor) the instant HP drops to or below this — in any state, not just combat, and ahead of everything else. See **Spells → Emergency heal** above for the full behavior.
**When you might change it:** Set it below your Major heal (combat) trigger, close enough to danger that it's a genuine last resort but with enough margin for the cast to land before the next hit. To opt out entirely, leave **Spells → Emergency heal** blank and set this trigger below Major's — with no Emergency spell configured, Major (then Minor) simply won't get a lower band to fall into.

### Run if below (HP / MA)

**Default:** HP 20%, Mana 10%
**What it does:** Triggers flee behavior when either pool (HP *or* mana) drops to or below its own trigger — an out-of-mana caster is treated the same as a low-HP fighter. MudPlay resumes normal activity only once **both** pools have recovered back above their triggers.
**Important notes:** Set either to `0` to disable that pool's flee trigger — useful for a class with no mana pool.

### Hang up if below

**Default:** 5%
**What it does:** The absolute last resort: disconnects the game outright once HP falls to or below this value. Since 0 HP only "drops" you in MajorMUD rather than killing you outright, this threshold can go negative, all the way down to (but never past) the point your BBS's realm actually treats as death.
**Important notes:** There's no "0 disables it" here — to fully disable the emergency hangup, use the toolbar's "Disable hangups" toggle instead.

### Sys goto wimpy instead of hanging

**Default:** Off
**What it does:** Changes what the emergency escape *does* when your HP crosses the "Hang up if below" threshold with a hostile present. Instead of dropping the connection, MudPlay breaks combat (if you're actively fighting) and fires **`sys goto <location>`** to jump you to a safe town — a "wimpy" escape that keeps you online. Pick which location from the **Wimpy goto location** dropdown right below the checkbox.
**Important notes:** This needs the **Sysop goto** power enabled on the BBS tab (the checkbox is greyed out until it is), and the location must be one of that BBS's Sys Goto entries. If the power is off, or the chosen location has been removed from the table, MudPlay falls back to the normal hangup — you're never left sitting in a fight. It rides the same trigger as the hangup, so the toolbar's "Disable hangups" toggle suppresses this too.

### Heal if above (rest/idle) / Heal if above (combat)

**Default:** Resting 50%, Combat 0% (disabled — always heal)
**What it does:** A mana floor that gates self-heal casts — below this, MudPlay skips the heal so mana can regenerate. `0` disables the gate entirely (always heal regardless of mana).
**Important notes:** This floor gates Minor and Major heal only. **Emergency heal ignores it** — a last-resort save spends whatever mana is left rather than conserving it (it still won't attempt a spell it can't afford the mana for). So even with a high floor set here, your Emergency heal still fires in its band.

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

### Party bless

**Where it's configured:** the party-buff **slots** (which spells, which members, recast timers) live in the **Buff Watchdog** — see the **Buff Watchdog** section under *Tools & Diagnostics*. This tab keeps only the two timing gates below.

**Targeting, and when they fire:**

- A **whole-party** spell (chant and the like) is sent once with no target and blankets the party, including you. Its **Party** checkbox is the master enable: unticking it stops the spell everywhere, clears Solo, and leaves both boxes visibly off. While that master is enabled, the **Solo** option allows it while you're alone (a whole-party cast still lands on a lone character); untick only Solo to make it party-only. In a party it obeys the party bless-timing gates below; **while solo it follows your self-bless timing gates** (Settings → Spells — the same combat / rest holds), since it's effectively a self-cast then.
- A **single-target** spell is cast on each selected member individually with its own recast timer, is **not** cast on your own character (self comes from the self-bless slots), and only fires for a member who's both in your party and in the room — so it genuinely needs a party.

Your self-bless slots always fire, party or not.

**Supersession:** if a whole-party buff *removes* a spell you have in a self-bless slot (the Spell Book shows it as "Removes …" — e.g. **chant removes bless**), then in a party the client stops self-casting the removed spell and lets the party buff cover you. The Buff Watchdog shows that self-buff row as **"covered by"** the party buff instead of a timer.

Any OTHER pair of configured buffs that remove each other this way — two self-cast buffs, two whole-party buffs, a whole-party buff removing a member's buff, and so on — aren't auto-resolved like this one case is; they instead get the **⚠** warning described in the **Buff Watchdog** section, so you know about the conflict without the client silently changing what it casts.

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

### Use @panic while leading

**Default:** Off
**What it does:** When you're leading a party and your HP crosses your Health-tab **"hang if below"** floor (with a hostile present), you say a bare `@panic` on the say channel before escaping — warning the whole party to bail with you. Without this, you still escape yourself, but the party isn't told. (MegaMUD parity — a MegaMUD leader's `@panic` reaches your party the same way, and yours reaches theirs.)
**When you might change it:** Leading a group through content where a leader going down means everyone should get out.

### Ignore @panics

**Default:** Off
**What it does:** When **unchecked** (the default), a partymate's `@panic` makes you bail the same way your own low-HP emergency would — hang up, or break + `sys goto <wimpy>` if you've set that up on the Health tab. Check this to ignore others' panics and stay put. (A received panic always respects the *Disable hangups* master switch: you'll still `sys goto` wimpy if configured, but you're never force-disconnected by someone else.)
**When you might change it:** Check it if you'd rather decide for yourself when to flee than have a partymate's panic drop you.

### Reset statistics on loop start

**Default:** On
**What it does:** At the start of every loop or Auto-Lair run, broadcasts a stat-reset request to the whole party so everyone's kill/exp counters start from zero together, for a clean comparison.

### Re-invite lost party members

**Default:** On
**What it does:** Leader-only. If a party member disconnects and reconnects within the grace window (see "If leading, wait only" below), you automatically re-invite them instead of having to notice and do it manually.

### Send @join nags to invited members

**Default:** On
**What it does:** After inviting someone, MudPlay follows up with reminder nags if they haven't joined yet, on a repeating cadence, until they join, decline, or the attempt window runs out.
**Important notes:** Only a typed reply counts as declining. Their client's automatic traffic doesn't: replies in `{…}` or `@`-commands such as the `@where` a follower sends when its party breaks. If someone flagged *Invite to party if seen* is left sitting in an `[Invited]` slot with no nag running (a follow that broke, a nag they cut off), seeing them again re-sends the invite and starts a fresh nag.

### First nag after (seconds) / Resend frequency (seconds) / Max attempt window (seconds)

**Default:** 5s / 10s / 55s
**What it does:** Controls the nag cadence described above — how long before the first nag, how often it repeats, and the total time before MudPlay gives up. This cadence is shared between the @join nag and the @health nag below.

### Send @health nags to party members

**Default:** On
**What it does:** When someone joins your party, MudPlay asks them for their current HP/mana so it can display real numbers rather than percent-only, retrying on the shared nag cadence above until it gets a real answer.

### Probe party members' level & version on the first party of the day

**Default:** On
**What it does:** The first time you party with a given player on a given day, MudPlay quietly asks for their level and client version to record on their player profile.
**Important notes:** If a player's name turns up on a different class than their profile remembers (they rerolled or remade the character), MudPlay drops the old character's title, level, race and gear from the profile and asks again straight away, even if you already partied with them today. A level reply that the stored title can't match (a level-1 answer against a level 10-14 title) also replaces that title, so an `@level` answer, asked automatically or by hand, always takes effect for party route planning.

### Max. monsters when partying

**Default:** `20`
**What it does:** While actively partied, this caps how many hostile monsters in a room MudPlay's combat engine will engage — overriding (only) the upper bound of the Combat tab's own room-monster cap while you're grouped up.
**Important notes:** Since the default (20) matches the Combat tab's own default, this is a no-op out of the box — you have to lower it for it to matter.

### Wait if members are below (%)

**Default:** `0` (disabled)
**What it does:** Pauses the whole party's automated movement while any observed member's HP is below this percentage, so the group holds position instead of leaving someone behind to recover.

### If leading, wait only (s)

**Default:** `90` seconds
**What it does:** As leader, how long you keep watching for a disconnected member to come back before giving up on them — and how long a member's `@wait` holds your automation before you give up and move on without their `@ok` (0 = wait until they send it).

**As a follower:** your client sends the leader `@wait` when you drop below a rest floor, and asks again whenever you drop below one afresh (HP or mana) or get walked on while still recovering — so if the leader's wait window runs out while you're resting, the next drop or the next room you're pulled into re-asks instead of leaving you dragged along. `@ok` goes once you're back to full rest-max.

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

### Minimum cash to keep on hand (deposit)

**Default:** `0`
**What it does:** The minimum cash to leave in your pocket after an **auto-deposit** — an amount plus a denomination, so you can type `1` and pick **Runic** to always keep 1 runic (1,000,000 copper) on hand. The deposit sends everything above this floor. `0` deposits everything. This applies to **banking only** — stashing is governed by the coin-type filter below.

### Only stash coin up to (stash)

**Default:** Everything (stash every denomination)
**What it does:** A dropdown that caps which coins a **stash** offloads. **Nothing** stashes no coin at all — a stash then only puts away your flagged items. **Everything** stashes all of your coin. In between, pick a denomination and a stash hides coins *up to* it and keeps the higher coins in your pocket — e.g. "Gold" hides copper / silver / gold and keeps platinum / runic. It's the stash-side counterpart to the deposit keep-on-hand floor: use it to shed bulky low-value coin as you pass a stash room while keeping your compact high-value coin. Applies to **stashing only**.

### Enable stashing as a follower

**Default:** off
**What it does:** When you're a **party follower** (in a party, not leading), lets you stash currency as the leader drags you through your marked stash rooms. Normally a follower's own movement is held by the leader's drag, so the usual "stash while looping through" trigger never fires for them; this opts their pass-through back in. Marking stash rooms and the coin-type filter above work the same as when you're solo.

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
**What it does:** Saves the Conversation window's chat history, and separately the Session Stats transaction history, to a log file so either survives an app restart. Each log is **per character** — switching characters re-scopes the Conversation window to that character's own history, so two characters on one BBS (e.g. a PVE and a PVP realm) never share a chat log.

### Log line limit

**Default:** `2000`
**What it does:** How many of the most recent lines each of the two logs above keeps — older lines roll off as new ones come in.

### Show emoji / emotes in the conversation window

**Default:** On
**What it does:** Substitutes emoji shortcodes and emoticons in the Conversation window as you read chat — `:lol:` / `:joy:` become 😂, `:)` / `:(` / `:D` become their emoji, and a bundled set of **image emotes** (the Pepe pack — `:sadge:`, `:copium:`, `:monkas:`, `:prayge:`, `:poggies:`, and more) render as small inline pictures. Matching is case-insensitive for named codes (`:monkaS:` = `:monkas:`); classic emoticons must be surrounded by spaces, so a time like `8:00` is never touched. Unknown `:codes:` are left as typed.
**Important notes:** Applies **live** — hit Apply and an already-open Conversation window re-renders on the spot. Unicode emoji rely on a **colour-emoji font** being installed on your system (e.g. Noto Color Emoji on Linux); the bundled image emotes always render regardless. Only the on-screen display changes — nothing you send to the game is altered.

### Custom emotes

Under the toggle, the **Custom emotes** area lists **every** emote — the built-in ones and your own — with a filter box. This is a **global, all-characters** library (not part of any one character's profile), and — like the rest of this tab — edits only take effect when you press **Apply / OK**; **Cancel** discards them.

**Define your own.** Type a **shortcode**, then set its value one of two ways:

- **An emoji** — paste an emoji character into the emoji box (from your OS emoji keyboard). The little preview shows what it'll look like. If pasted emoji show **blank**, your system is missing a colour-emoji font (Linux: install Noto Color Emoji) — use an image emote instead.
- **An image** — click **Select image…** and pick a picture (PNG / GIF / JPG); it's scaled to a uniform size.

Then click **Add to emoji list**. It renders inline wherever someone types `:yourcode:`.

**Change or remove.** **Click any row** to load it into the editor (highlighted); change its emoji / image and **Add to emoji list** again. Every row has an action:

- **Custom** emotes — **Remove** deletes them.
- **Default** emotes — **Remove** *hides* them, so you can trim the built-in set; a hidden default shows *"Hidden (default)"* with a **Restore** button.
- Adding a custom emote with a **built-in shortcode** overrides that default (tagged *"Custom (overrides default)"*).

**Share a set.** **Export set…** writes a single **`.mudpack`** package (your shortcodes + their images) to hand to a friend. To import:

- **Import pack / zip…** — a `.mudpack` (or any zip). Emotes with a definition come in named; any loose images without one are added as **red** rows.
- **Import folder…** — grabs every image in a folder. Since there are no shortcode definitions, each lands as a **red** row: **click it, set a shortcode, and Add to emoji list**. The filename is suggested as a starting point.

Applied emotes live under your app-data **Emotes** folder.

**The `:` picker.** In the Conversation window's input box, type `:` followed by a couple of letters and a **suggestion popup** flies out (built-ins + your own). **↑ / ↓** move the selection, **Enter** or **Tab** inserts the highlighted one, a **click** picks it, and **Esc** dismisses. It works even when your line starts with the `.` say-slow precursor.

### Font / Font size (Conversation window)

**Default:** JetBrains Mono, 12pt
**Available options:** Font — JetBrains Mono, IBM Plex Sans, MX437 IBM VGA; Size — 10–20pt in a fixed list.
**What it does:** The font used inside the Conversation window's chat log.
**Important notes:** Applies **live** — hit Apply and an already-open Conversation window re-fonts on the spot, no reopen needed.

### Channel colors (per-channel Accent / Text)

**Default:** theme defaults (no override)
**What it does:** Lets you pick a custom color for each chat channel's tag/speaker name (Accent) and separately its message body (Text).
**Important notes:** Applies **live** — hit Apply and an already-open Conversation window recolors on the spot, no reopen needed.

---

## Auto-Light

Settings → Auto-Light. Everything here only matters once the master **Auto-Light** engine toggle (Settings → General, or the toolbar) is turned on — these fields tune its behavior, they don't turn it on by themselves.

### Preferred light

**Default:** `Automatic (per route)`
**Available options:** `Automatic (per route)`, `Only use my room-light spell (no items)`, or the name of any purchasable light item.
**What it does:** Chooses what light source MudPlay buys and lights when it needs one. "Automatic" picks whatever's strong enough to cover the route ahead. Choosing a specific item pins it to that light (falling back to auto-pick if it's unavailable in your current game data). "Only use my room-light spell" tells MudPlay to never buy or ready a light item at all — it relies purely on your gear and the room-light spell you've configured in the Buff Watchdog.
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
**Available options:** `Default — closest ready lair (no idle waits)`, `Throughput — minimize wasted respawn only`
**What it does:** How the scheduler picks the next lair:

- **Default** maximizes hits per run: it goes to the **closest lair that's up by the time you arrive** — a closer lair still on cooldown that pops *during* the walk counts, and beats a farther already-up one — and **never idles in a wait-room** for a nearer lair unless it'd actually be ready when you get there; only when no lair is up by arrival does it wait for the soonest.
- **Throughput** only cares about never wasting a respawn and treats your idle time as free, so it will park and wait for the soonest-popping lair even when another is already up.
**When you might change it:** Pick Throughput if you'd rather never walk into an already-picked-clean lair and don't mind standing in a wait-room to time each respawn perfectly.

### Idle penalty weight

**Default:** `1.0`
**Available options:** 0–100
**What it does:** Legacy tuning knob from the old balance-scoring model; the current **Default** heuristic no longer uses it (it never idles when a lair is ready, so there's no idle-wait to weight). Left in place for compatibility; changing it has no effect under either heuristic.

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

Settings → Auto-Trainer. Controls *how* auto-training behaves once it runs — when to make the trip, how many levels to hold back, where to stop, and whether to announce.

**The on/off switches are not here.** **Auto-train**, **Auto-train stats** and **Auto-train party** live on the Player Workshop's **CP Allocation** tab, beside the plan they act on. They used to appear in both places, which was confusing and worse than cosmetic: this tab saves every setting on it at once, so pressing Apply here could quietly undo a toggle you had just flipped on the CP tab.

### Auto-train

**Where:** Player Workshop → CP Allocation tab (not this tab).
**Default:** Off
**What it does:** The master auto-leveling switch. When on, and you're running a Loop or Auto-Lair, the moment your banked experience makes a new level trainable, MudPlay automatically pauses, detours to an allowed trainer, trains every level you can, then resumes what it was doing.
**Solo only:** training briefly drops you out of and back into the realm, which disbands a party server-side — so an armed Auto-train never fires while you're grouped. To train while grouped, turn on **Auto-train party** (below). One exception: **leading** with Auto-train party on when nobody else in the party uses it, Auto-train runs as your normal solo trip — the members follow, and they're re-invited once you've trained.
**It checks it can pay first.** Before walking anywhere, MudPlay prices the whole run — including the second trainer when your banked levels span two level bands, since each charges its own markup — and compares it to the coin you're carrying.

If you're short it collects the difference first: your stash rooms, then your bank, or a combination, picking the bank branch nearest the trainer rather than nearest you. Pick up enough coin along the way and it abandons the errand and heads straight for the trainer. If everything you can reach still falls short, nothing is walked: it logs how far short you are and roughly how many laps of your loop will close the gap, and stays armed.
**About stashed coin.** MudPlay tracks what it has hidden in each stash room, but any player who searches that room can take it — so a stash is only ever a good guess. The run confirms by searching when it arrives, and if the room has been emptied it simply re-prices from where it's standing and carries on to the bank.
**Auto-Get Cash is borrowed, not changed.** A collection trip needs cash pickup on to work, so MudPlay switches it on for the duration and puts it back exactly as it found it. Your saved setting is never modified. Auto-stashing is suppressed for the same window, so the trip can't hide the coin it just came to collect.
**Banking on the way home.** If Auto-deposit is on, a run that trained something offers the purse to it once the loop is running again — so a withdraw-and-train trip banks the leftovers on the way back rather than carrying them round the circuit.

### Train once this many levels are stacked

**Default:** `0` (go as soon as one level is available)
**What it does:** Makes Auto-train wait until this many levels are trainable before making a trip, so one detour trains them all instead of one trip per level.
**When you might change it:** Set it to 3–5 when your trainer is a long walk from your grind spot — you trade a little delay for far fewer interruptions.
**Important notes:** Works alongside *Levels to keep banked*, which decides how many stay banked once the trip happens. A threshold at or below that reserve could never train anything, so MudPlay treats it as one above the reserve.

### Auto-train stats

**Where:** Player Workshop → CP Allocation tab (not this tab).
**Default:** Off
**What it does:** Independent of Auto-train. When on, every time a training happens (whether from Auto-train, a manual "Train Now," or a remote `@train`), MudPlay also applies your saved CP allocation plan's spending for the level you just reached. **It also fires the moment you open the `train stats` screen yourself** — type `train stats` at any trainer with the box checked and MudPlay applies the plan for you, no button press needed. (With it *off*, MudPlay stays out of the way and you allocate by hand.) In an **Auto-train party** trip it applies the same way — the leader's plan when it trains at the last stop, a member's when it trains on the leader's order.
**Important notes:** You need a saved CP plan (from the Player Workshop's CP Allocation tab) before this checkbox will actually stay checked — MudPlay reverts it and warns you if you try to enable it with no plan saved. Applying stats isn't level-gated like a level-up is, so "Train Now" applies your CP at whatever class-valid trainer you're standing in — it no longer walks you off to a level-band-matching one (or gives up) just to spend points.

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

### Auto-train party

**Where:** Player Workshop → CP Allocation tab (not this tab).
**Default:** Off
**What it does:** Makes auto-training work in a party. Every member who wants it ticks the box on their own client. A party trip needs **at least two** of you on MudPlay with the box on (the leader included); a leader that's the only one falls back to its own solo Auto-train settings, and a follower that's the only one simply follows.
- **As a member**, you don't walk off to train. Instead your client tells the leader where you stand — *ready* once your own settings above (levels stacked, levels to keep banked, do-not-train-above) say you'd make a trip, or *waiting*, with a rough time until you will be from your exp/hour. At the trainer you train when the leader says so, never walking on to another trainer by yourself, and let the leader know once you're back in the party.
- **As the leader**, with a Loop or Auto-Lair running, MudPlay collects everyone's report plus your own and decides when to go — once a set **number of party members** is ready (2 by default), and you count as one member like everyone else:
  - everyone ready → go;
  - at least that many ready → go; anyone not ready yet keeps their levels banked and just follows along;
  - otherwise keep grinding.

  Then it walks the whole party round: every member who's ready trains first — at the trainer that serves the most of them, then the next, when you're spread across level bands — and you train at the final stop, **at the same time** as the members there: once their train orders are delivered you train too, since every train drops that character from the party anyway. It then re-invites everyone once (so leave **Re-invite lost party members** on in Settings → Party — it also re-invites each member as they come back from their own train) and holds the loop until they're back, then carries on grinding.

**Seeing where everyone stands.** While you lead with the box on, the Party window shows a line under each member's bars from their last report — their total exp (between their reports, what they reported plus the exp **you've** gained since — party members gain the same from a kill, so it's an estimate), their time to next level at **your** exp/hour, and their state: `4,120,331 xp · TNL 1h 5m · not ready`, `… · ready +2 (12,345c)` (levels this trip trains and the fee), or `… · no party train` — and your own on your row. Members on another client (MegaMUD, or an older MudPlay) can't report, so the leader asks them `@level` instead and shows `1,000 to L23 · TNL 12m · other client`: their reply only counts to their **next** level, not past it the way MudPlay's banked-aware figure does, so the line names that level — and their own "will level in" estimate stands in until you have an exp/hour rate. A member with no line hasn't answered either. When a member broadcasts **"I can now train to level: N"** (the Auto-Trainer's *Announce level-ups*, on any channel), their line says so — `can train L12 · party train off`, `can train L12 · other client` — until they're seen at that level. It's display only: it shows who could train even though they won't be auto-trained (box off, still short of their own settings, another client). Whether a member comes on a party trip is still their own report. **Following** with the box on, your Party window shows the same lines: your own row is the status you report to the leader, and the leader's and other members' rows fill in from their `@level` / `@exp` replies (ask them yourself), timed at your own exp/hour, since the party shares the kills. Members' reports only ever go to the leader, so a follower sees readings, not their ready state. Each member's TNL counts down between readings the same way the status bar's does. Each member's level also shows with their class (`Level 20 - Druid`) — from their report, or from the last `@level` reading for members who don't report.
**The leader doesn't have to be training.** A high-level leader power-leveling the party still escorts everyone to the trainer and back — the trip goes whenever enough members are ready.
**Money.** Each member reports its purse and its biggest bank deposit. If someone is short, members with coin to spare give it to them before anyone walks (only what they can spare above their own fee and keep-on-hand amount). If the party's spare coin can't cover everyone, the trip first stops at a bank and short members withdraw their own fee; anyone who still can't pay sits the trip out.
**Who isn't waited for:** members with the box off, members on another client, and anyone who doesn't answer. They just follow the leader there and back.
**A member shut out of the trainer's room.** Some trainers' rooms can't be entered during a fight, so a member still fighting outside is left behind and their follow breaks. The train order carries the trainer's room, so that member walks in by itself once the fight is over, trains, and reports. Everyone at a stop trains at once. As soon as the members' train orders are delivered, the leader trains too, then re-invites everyone who set out on the trip (including a member left in an `[Invited]` slot) and waits for each member's `done` before moving on. Every train drops that character from the party, so there's just the one re-form.
**Kept quiet on the wire.** A telepath at the wrong moment costs the leader a little exp/hour, so the handshake is the bare minimum: the leader asks each member **once** when they join (after a short pause so the join-time `@version` check can say whether they're on MudPlay at all — members on another client are never asked), and from then on a member only speaks up when something that matters changes (ready, level, levels to train, or switching the box off). A member only ever reports to a leader that asked. If a member that's still waiting hasn't spoken up by **10 minutes past** its projected ready time, the leader sends it one "ready yet?" ask — its own report normally beats that, so it's rarely needed. Members on another client get one `@level` if the join-time check didn't already supply it, and one more 10 minutes past their projected level-up. Asking a member `@level` or `@exp` yourself (by telepath, or `.@level` on say) also refreshes their line. Outside a trip, nothing is sent mid-combat.
**Important notes:** Orders are only taken from your current party leader, and only while your own box is on — nobody can make your character train, give or withdraw otherwise. After a trip, the leader waits a few minutes before deciding again.

### Party options

These shape how the **leader** runs an Auto-train party trip (the level-11 rule applies to everyone).

- **Go once at least N party members are ready to train** — default `2`, range 1–6. The leader counts as one. Members who don't report, or are skipped by the level gap, don't count — and if everyone who does count is ready, the trip goes even when that's fewer than N.
- **Don't wait for anyone more than N levels above the party** — default `5`, `0` = off. A member who isn't ready and is this far above the rest of the party (a power-leveler — the leader included) is never waited for.
- **Leave the level 11 train to a solo trip** — default on. Party trips train no higher than level 10; the step to 11 is a solo effort, so take it on your own.

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

### Door max pick

**Default:** 10
**What it does:** Caps how many times the walker retries **picking** a locked door before giving up (picking is probabilistic — it can fail even when your skill meets the requirement). **Bashing has no cap**: bashing a door drains HP, so instead of a fixed retry count the walker bashes a genuinely bashable door until it opens, pausing to **rest to your rest-max** whenever HP dips to your Health-tab rest trigger, then resuming. A door that isn't actually bashable (strength/requirement too high) still falls through to picking or a key rather than bashing forever.

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
**What it does:** Two Global-tier toggles for automated navigation through two of MajorMUD's notoriously tricky areas — the Great Pyramid's climbing puzzle and the Warped Asylum's random-teleport maze. On means walking to a destination inside either area drives the puzzle-solving automatically; off means a walk there just fails like any other unreachable spot, and you navigate manually. On the pyramid's door-maze floor the climb checks each step against the room MudPlay has you in, so a door move that didn't go through (or a climb started partway across the floor) picks up from where you actually are instead of working the wrong door.
**Important notes:** These apply to every character on the install, not just the current one.

### Paradigm transport tokens (route offering + rooms-saved threshold)

**Default:** On, threshold 3 (Paradigm realms only — the rows are hidden otherwise)
**What it does:** When on, a walk-to whose destination a held transport token reaches faster surfaces a blue **"use token"** card in the route picker (see *Use a transport token* under navigation). The threshold sets how many rooms a token must save over walking before the card appears — a one- or two-room saving isn't worth a token's gold, daily charge, and buff-wipe. Turn the offering off entirely if you never want token routes suggested.
**Important notes:** Global-tier — applies to every character on the install. A token is only ever used when you pick its card; it's never taken automatically.

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

**What it does:** Shows every scheduled event you've defined, with its **Name**, its trigger (**When**), a live countdown to its next fire (**Next**), and its action (**What**). **New…** and **Modify…** open the event editor; **Remove** deletes the selected event. Changes save to the profile immediately.
**Important notes:** Each event has a **Name** and a **Disabled** checkbox in its editor — untick Disabled to make it live. A row can show a "target missing" warning if it points at a saved Loop or Auto-Lair setup that's since been deleted or renamed — the event auto-disables itself in that case, and you'll need to clear its **Disabled** box again once you've fixed the reference. The **Next** column only counts down for **At time** and **Every** events while you're connected and in-game (those timers don't run otherwise); lifecycle events (Logon/Logoff/Re-log) fire on connection, not a clock, so they show a dash.

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

Not a Settings tab — these five toggles live in the **Program Log** window (default shortcut F4), and are documented here for completeness since they're genuine per-character saved preferences. They control how much detail MudPlay records about its own decisions, mainly useful for troubleshooting or preparing a bug report.

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

### Capture unrecognized messages

**Default:** On
**What it does:** Stages any wire line the Messages catalogue doesn't recognize (and no other known line type matches) as a review candidate — logging a Warn row the first time that exact text is seen, and tagging it with the map and room you were in so you can trace where it came from. Capture only runs **once you're in the realm** — the startup splash, the BBS login menu, and connect banners never stage candidates.

The catalogue stores most messages as *templates* (`{source} casts {spellname} on {target}!`), so recognition matches those templates against the line rather than comparing text — a known cast like `Raijin casts minor healing on Raijin!` is recognized and never staged. A handful of shipped templates pin so little fixed wording (`The {source} {spellname}!`) that they would match almost any sentence; those are skipped for recognition so they can't swallow a genuine unknown message. Buff wear-off lines are matched as phrases, so the server's trailing punctuation doesn't matter.

Beyond the catalogue, the queue skips:

- the client's own bracketed status notices (e.g. `[… Quest is Now Available]`);
- the echo of a command you or an automation just sent;
- room-display title lines (matched against the Rooms table, since a room name is read by the room parser rather than the message catalogue);
- **room-light announcements** (`The room is dimly lit` and the other bands — all known from game data);
- **`par` party-screen rows**, the **`stat` / `exp` / `health` sheet**, and the **`spells` / `pow` listing** — each read directly by its own parser, so a poll of any of them no longer floods the queue with its rows;
- third-party **physical** attacks (a partymate swinging at a monster, or a monster swinging at a partymate with a named weapon or body part);
- **monster death messages** — realms write those per species and don't publish them, so they're identified by position instead: the line immediately before an experience gain is treated as death flavour and dropped (any row an earlier session captured for that same text is cleared out too).

One shape can't be settled by wording at all: `Suijin shoots an arrow at bandit!` and `Suijin hurls a fireball at bandit!` are the same sentence. That one is decided by **who acted** — if the named player's class has no magery (Warrior, Witchunter, Ninja, Thief), they cannot be casting, so the line is dropped; from a class that *can* cast, or from a name the client doesn't know, it's kept. Class comes from the party screen, your own `stat` line, or the Players database (an observed class, else an unambiguous title).

A few line shapes are deliberately still captured:

- **a monster *casting* at a partymate** — uncatalogued monster spell messages are the main thing this is for;
- **any line where a non-caster is the *subject*** rather than the actor (`Suijin convulses violently!`), since that's a monster's spell landing on them;
- **ambient room flavour**, which in many areas is a room-spell trigger rather than scenery.

Double-click the row to open the same editor the Messages tab uses, pre-filled with the raw text, so you can turn it into a real catalogue entry on the spot. Repeated candidates are also listed in Game Data → **Unrecognized Lines** (with a **Seen In** map:room column) for batch review later; dismissing one there is sticky, so it won't quietly resurface as "new" if it recurs.
**Important notes:** On by default — the point of this toggle is catching the game's devs changing or adding message wording before it silently breaks something else (navigation, combat, condition tracking) that depends on recognizing that line.

---

## Roomba (Player Workshop)

Not a Settings-window tab (the **Roomba** tab in the Player Workshop). An automated gang-house (GH) item sorter, built on the same loop engine every saved Loop runs on rather than a separate navigation system.

**Shared per BBS, not per character.** Room labels, the hidden-search settings, and the item-location log below are all saved against the BBS you're connected to, not your character — a BBS ties to one game-data set and every character on it shares the same gang house, so labeling rooms (or running a sweep) on any one character makes them available to every other character on that board.

**Setup:** mark your gang-house rooms one of two ways. On the Navigation map, right-click a room and choose **Toggle: Roomba Room** — the room gets a small **robot marker**; right-clicking it again removes it. Or, on the Roomba tab, type a room's **map/room number** into the box and click **Add Room**. Either way opens the rule picker (titled *Set 1/384 <room name> as Roomba Room*). A room's rules are OR'd together, so a single room can sort for several categories at once (e.g. a "Chain Scale" room admitting both Chainmail and Scalemail). Each rule is either:
- an **item category** — Weapon, Armour, Food, etc. (the same categories the imported item data already carries), optionally narrowed to a specific weapon or armour subtype; or
- an **equip slot** — Neck, Wrist, Finger, Off-Hand, etc. — for jewelry-style rooms that aren't classified by material or weapon type at all (a necklace has no "armour type"). A slot rule matches any item worn there regardless of its category.

Use **+ Add rule** to add another rule to the room, and the ✕ on a rule row to remove it. Any number of rooms may be flagged **"Make this the gang house's catch-all room"** — anything matching no explicit rule anywhere gets swept there instead of being left in place. Several catch-alls form an **overflow chain**, tried in order, so when the first fills up the next one takes over; flag as many as you like. Right-clicking **Toggle: Roomba Room** on an already-marked room removes it. At least two labeled rooms are required to start a sweep.

**Running a sweep:** open the **Player Workshop → Roomba** tab to review your labeled destinations and click **Start Sweep**. **The table lists one row per sort rule**, so a room admitting both Chainmail and Scalemail shows as two rows sharing a name, map/room and tick — "[catch-all]" repeats on each of that room's rows. Each row has a **Goto** button that opens the map and walks you straight to that room — handy for checking a house by hand without starting a full sweep.

**Editing and removing rules** — above the table sit **Edit** and **Remove**, and both act on whatever rows you've *highlighted* (ctrl/shift-click to pick several):

- **Edit** takes exactly one row and reopens that room in the rule picker, prefilled — so a rule set to the wrong category is corrected in place instead of being removed and re-added. It's also where you add a further rule to an existing room or flag it catch-all.
- **Remove** drops the highlighted **rules**, not their rooms: a room with three rules loses only the one you picked and keeps the other two. A room leaves the list entirely only once it has nothing left to sort by — its last rule removed and no catch-all flag (removing a catch-all room's "(no rules)" row takes the room off too).

Both buttons grey out while a sweep is running, since the circuit it's walking was planned from these labels.

**Only rooms with the "Actively Manage" checkbox ticked are visited.** This tick is **per character** — the room labels themselves are shared by every character on the BBS, but *which* of them a character sweeps is its own choice, so alts who belong to **different gang houses on the same BBS** each manage their own house without stepping on each other.

- A room you add yourself (the Add Room box, or the map's right-click *Toggle: Roomba Room*) is checked for you by default.
- A room adopted from someone else's **`@roomba sync`** arrives **unchecked** — because a shared label set can span *several* gang houses, and Roomba must never route from house to house or into one you lack the emblem for. Tick a synced room only once you're sure it belongs to the house *this* character sweeps.
- If you press Start Sweep (or Start Inventory) with **no** room checked, the phase label turns red with **"Select rooms to actively manage"** instead of starting; you still need **2+** checked rooms to run.

The **Search rooms for hidden items** checkbox is **off by default** — Roomba sorts only what's plainly visible on the floor; tick it to also send `sea` in each room while scanning and sort what's hidden (its **Searches per room** count, default 3, applies only then, and greys out while the box is unticked).

A sweep runs in three phases:
- **Scan (one lap):** the circuit is walked once — including unlabeled rooms between destinations — purely observing; nothing is picked up or moved yet. (There's no lap-count setting: one scan of the same rooms tells Roomba everything it needs.) With hidden-item search on, each room is also `sea`'d.
- **Sorting:** once the scan has mapped which items are misplaced, Roomba moves them all in the **fewest trips between rooms** — visible items picked up immediately, an item found only by a `sea` re-searched before its `get`, an item matching no rule sent to the catch-all (or left in place if there's none). It **fills the pack toward your carry limit before delivering**, collecting several items bound for nearby rooms on one trip rather than shuttling each on its own (the carry-budget mechanics are detailed just below the phases).
- **Command pacing:** the game limits how fast a client may send commands, and on a stock realm it simply *drops* anything past the limit (`Why don't you slow down for a few seconds?`, then `You are typing too quickly - command ignored`). A room holding twenty-odd items is more than enough to trip it, and when that happens the whole batch is lost — along with whatever Roomba tried to do next, which is how a sweep used to end up stuck. Roomba now sends its `get`/`drop` commands **one at a time, each released by the game's own prompt**, so it runs as fast as the server will actually accept and no faster. If the game complains anyway, the command it dropped is re-sent after a short pause rather than lost. You don't need to configure any of this.
- **Auto-collect and auto-discard pause while a sweep runs.** For the whole sweep Roomba holds off both the auto-get and the auto-discard engines. An auto-get would grab loot that eats the carry headroom Roomba budgets for its moves — left unchecked it shrinks the budget until planned sorts no longer fit and the sweep can never finish — and an auto-discard would bin an item Roomba is in the middle of relocating. With both held off there's no tug-of-war, so Roomba sorts auto-get- and auto-discard-flagged items to their labeled rooms like anything else. Your normal auto-collect/discard resumes the instant the sweep ends (clearing anything that piled up in the meantime).
- **An item Roomba thinks it's carrying but isn't:** if something else empties your hands mid-sweep — Auto-discard binning loot, or a manual drop — Roomba notices and forgets the item rather than planning a delivery for something it no longer holds. It matters more than it sounds: the game matches a drop's item name against what you're *actually* carrying, so a `drop` for a missing item can latch onto a **different** item you still have. If a drop is refused anyway (`Syntax: DROP {Amount} {Currency}` or `You may not drop that item!`), Roomba reads your inventory to check rather than assuming — the move is discarded only if the item genuinely isn't there, and kept and retried if it is.
- **The Roomba Log has an "Out of space" section**, and the map marks it. It leads with what to do rather than what happened: for each category that ran out of room, the *rooms that could have taken it*, confirmation they're all full, and the items left stranded — so the fix is obvious, label another room for that category (or flag another catch-all). A flat list of items that went nowhere doesn't tell you what to do about it; this does. Below that it lists every room that refused a drop this sweep. On the Navigation map, a Roomba room that's out of space also gets an **amber ring** round its robot marker, so "which of my rooms are full" is a glance rather than a log read.
- **Full rooms are re-checked each lap.** A room is only known to be full by being refused, and that reading goes stale — Roomba spends a lap emptying rooms, and you may clear space yourself. So the full list is forgotten at each lap boundary and re-learned, which costs one refused drop per room still full and buys back every room that isn't.
- **When a destination room fills up:** rooms hold a limited number of items, and the game refuses a drop into a full one with **"There is no room to drop *X* here."** Roomba treats that as the room being full for the rest of the sweep and immediately re-routes **everything** bound for it — what it's carrying and what it hasn't collected yet — to the next room that accepts the same category, then to the catch-all. **A backup room is just another room labeled for the same category** (no separate setting), so if your Gems room keeps filling up, label a second Gems room and Roomba starts using it the moment the first refuses. Only if every matching room *and* the catch-all are full does an item stay where it is, recorded with the reason rather than retried, so a full house can't leave a sweep re-sending the same refused drops each lap. A full room is also **prioritized as a place to collect *from***, since the out-of-place items in it are the only ones whose removal frees space. The "full" mark lasts for that sweep only. The end-of-sweep summary names any rooms that filled up.
- **Final scan:** after everything is delivered, Roomba walks the circuit one last time to refresh each room's inventory, then finishes.

**How Roomba manages your carry budget.** Roomba's budget is your carry limit minus whatever else is already in your pack, so anything you're holding that Roomba didn't collect — auto-get loot especially — eats into it:

- **Too-heavy is judged at emptiest pack** — an item is only written off as *too heavy* if it wouldn't fit even with your pack at its emptiest during the sweep, so a temporarily-full pack never permanently discards anything.
- **Barely-room stall** — if your pack gets so full that Roomba has room for barely one item at a time, it delivers what it's carrying and stops with an explanation rather than shuttling one item per trip. Free some space and hit **Resume**.
- **80% / 40% unload run** — once the pack passes 80% of its budget it stops collecting and delivers heaviest-destination-first (that frees the most space) until it's back under 40%, then resumes filling. The two thresholds are deliberately far apart: a single cut-off would leave the pack pinned at the limit, walking the same long leg twice for every item.
- **No re-read after each move** — because it tracks every pickup and drop and knows each item's weight, it plans against your working capacity without re-reading inventory, trusting the game's `You took` / `You dropped` lines. The one exception is a `You cannot carry that much!` refusal, which proves the estimate drifted: it re-checks inventory once (`i`) to resync, then re-plans.

**If a sweep stops early.** A sweep can end before it's done — it loses track of where you are, you stop it, or you close the client. Two things make that recoverable rather than your problem to clean up:

- **Whatever it was carrying is remembered.** The items stay in your pack, and Roomba writes down what it was holding and where each piece was going. The next sweep checks that list against your real inventory first (so anything you've since dropped, sold or worn is quietly forgotten) and delivers what's left before it scans anything. The list is saved per character, so it survives closing the client or relogging.
- **Resume picks up where it left off.** The **Resume** button on the Roomba tab lights up whenever a sweep stopped with work outstanding (hover it to see how much), and stays greyed out otherwise. It carries on from that sweep's queue and skips the scan entirely — in a large gang house the scan is most of the time a sweep takes, and stopping early doesn't make what it already found wrong. Items someone else has taken in the meantime simply fail their pickup and drop out of the queue, exactly as they would mid-sweep. The unfinished queue is saved per character, so Resume still works after closing and reopening the client — which is exactly when you least want to re-walk the whole circuit. Use **Start Sweep** instead when you want a fresh look at every room.

**Start Inventory — scan and log without moving anything.** Next to Start Sweep is **Start Inventory**: it walks the exact same labeled circuit, observes each room's floor, and honors **Search rooms for hidden items** exactly like a sweep's scan phase — but it never dispatches a single `get` or `drop`. It finishes automatically the moment its one lap completes (no sorting, no final scan — the lap it just took already reflects the true state).

Use it if you've already got your own manual way of organizing the gang house and just want `@roomba`'s item-location log kept current without Roomba touching anything. The Roomba Log and the tab's completion summary both call this out explicitly so it's never mistaken for a sweep that sorted nothing.

**Gangpath announcements.** Starting either mode gangpaths the gang house that it's underway, with the start date/time in your client's own timezone (`Roomba sorting starting - 2026-08-30 09:15 MST.`), and finishing announces the same way with item counts plus the start and finish time (`Roomba sorting complete - sorted N item(s), inventoried M item(s). started … finished …`). A few details:

- Each gang member reading the message sees the **sender's** own timezone (a short name like PST/MST/EST, or a numeric UTC offset for anything else), not their own — useful for gangs spread across zones.
- A sort's recon and final scan observe every room's floor exactly like an Inventory-only lap, so a sweep's completion reports BOTH how many items it sorted and how many it inventoried along the way — Sorting keeps the item-location log just as current as a dedicated Inventory run.
- "Sorted" and "inventoried" both count individual units, not stacks — a `35 orc-head` pile sorted or scanned in one go counts as 35.
- Manually stopping a sweep early (or a navigation failure interrupting it) doesn't send a completion announce — only a genuine finish does, so the gang isn't told a sweep "completed" when it didn't.

**Reading the tab.** Each room's **Status** column tracks it live — *Scanning* during the scan, *Cleaning* while it still holds items to move out, *Complete* once its movable clutter is gone. **Double-click a room** to see its current floor contents (from the final scan).

The **Roomba Log** button opens a window with the full per-move record, everything left in place (tagged with why — *no matching room*, *every room that takes it is full*, *gone by sort time*, *too heavy to carry*, *couldn't be sorted this sweep — no room or headroom*, or *the pickup never landed*), and an end-of-run summary: rooms sorted, items sorted, and the explicit list of unmovable items. **Stop** ends a sweep early.

**Master List** — a separate button that opens a full, **sortable** table (click any column header — Item, Qty, Seen In, Market) of everything the item-location log currently knows: one row per item per room it was seen in (quantity included). Its parts:

- **Market column** — cross-references that item's `Obtained From` shop data: every shop that buys or sells it, priced at a fixed 50 charm (MajorMUD's neutral "retail" point), **excluding any shop inside one of this gang house's own labeled rooms** (you don't need a reminder that your own stash room "sells" what you just put there). An item with no market outside the gang house reads "(no outside market)".
- **Filter box** — narrows the list live by item name, quantity, or the seen-in map/room (type `15/12` to see just that room's finds). **Double-clicking a row opens that item's full record** (the same Item edit dialog the Game Data Browser opens).
- **Export List…** — saves the whole log to a text file grouped **by room** (one header per map/room with its name, then that room's items alphabetically with quantity) — a shareable gang-house manifest. The export always covers the full log, regardless of the filter.

Even on a big synced log it opens instantly — each item's Market value is only priced when its row scrolls into view. Updates live as new scans (sweep, Inventory-only, or an incoming `@roomba sync`) come in, same as `@roomba`'s log — they're the same data.

**Item-location log + `@roomba`.** Every room floor Roomba observes during a scan (recon, an Inventory-only lap, or the post-sort final scan) is recorded as that room's known contents. A gang house can stock the same item in more than one room at once, so the log tracks sightings **per room**, not just one "last seen" spot per item — re-scanning a room updates only that room's own entries (an item no longer on its floor drops off, without touching that same item's sighting elsewhere).

Grant a gang member the **Query Roomba** remote-control permission (on the Players tab) and their gangpath'd `@roomba <item name>` gets back **one reply line per matching item**: the total quantity summed across every room holding it, followed by each room's own locator AND quantity (e.g. `15/12 (3), 15/13 (2)`, capped at 10 with a "+N more" tail), and the **last scanned** date/time of the freshest sighting.

- **Per-room quantity** tells a genuinely scattered stash apart from one room's count looking wrong — the total alone can't distinguish "12 real items across 3 rooms" from "one room's search-derived count came out too high".
- **The last-scanned stamp** tells a fresh sighting from a stale one — a room nobody's swept in weeks is a much weaker signal than one scanned this session.
- **A loose query matching several distinct items** (names often share words — "severed head of goru-nezar" and "severed head of darksong" both match "head") gets a line for each, capped at 5 with its own overflow tail, rather than refusing to answer.

That per-player permission is the only gate — there's no separate on/off checkbox; a member you haven't granted it to gets nothing. The log itself is shared BBS-wide (every character on the board sees the same sightings). The Roomba tab shows a **Roomba Data Timestamp** next to *Searches per room* — the time of the newest sighting anywhere in the log — so you can tell at a glance whether the gang-house data is current or stale (it reads "no data yet" before the first scan).

**`@roomba sync`** — the no-hassle way to hand your item-location log to a gang member starting fresh on their own MudPlay install, no file/Discord/import-export needed.

They gangpath (or telepath) `@roomba sync`; your client (with them granted the **Query Roomba** permission) replies with your whole log — **both the labeled gang-house rooms and the item sightings** — compressed into chat lines that merge straight into theirs, so their Roomba tab fills with the same rooms (ready to sweep) and their `@roomba` / Master List has all your item locations. A room they've already labeled themselves is left as-is.

- **Paced out** — a big gang house is a couple dozen lines, so the reply is released about 0.8s per line in the background: it never floods the channel or stalls your own combat/healing/movement, and a line dropped to the typing-rate limit is automatically re-sent.
- **No review window** — unlike `@timer sync`'s boss-timer merge, a room-contents sighting has no "conflict" to weigh, so whichever side saw an item more recently just wins, silently. The reply finishes with a `Sync Complete` marker so you can see it landed in full.

**The grant is one-way, in the direction the data flows.** To *receive* someone's log you send `@roomba sync` to them and **they** grant *you* "Query Roomba" — nothing else. Your own client adopts their reply simply because you asked for it (any `@roombadata` reply is accepted for a short window after your outbound `@roomba sync`); you don't also need to grant them anything, and a stray sync line you never requested is ignored.

Conversely, if you *haven't* granted a sender "Query Roomba", their `@roomba` query or `@roomba sync` to you is denied. So if a sync seems to send but nothing updates, the usual cause is the *sender* not having granted you: check that they've given your character "Query Roomba" on their Players tab.

**Important notes:**

- Roomba Mode **refuses to start while another movement engine** (a manual walk, a Loop, or Auto-Lair) is active, and while running it behaves like any other Loop for the toolbar Pause/Stop buttons and the manual-move-pauses-navigation rule.
- It **fills to your carry limit** and will happily make you Heavy if that saves trips. A pile heavier than your whole working capacity (say 140 torches) is **split across several trips** rather than abandoned; the only pickup it won't attempt is a *single* item too heavy to ever carry, which it leaves in place and surfaces as *too heavy to carry*.
- The sweep **ends on its own** once every move it has headroom and a free destination for is done — it doesn't keep circling. A lap that moves nothing is a warning; a verification lap confirms it; and if that lap also moves nothing the sweep finishes rather than looping. Anything that couldn't be placed — a full destination, an unfound hidden item, or too-heavy-for-your-budget — is surfaced in the Roomba Log as *couldn't be sorted this sweep*. You can still stop it manually at any time.
- Your **working capacity** is your carry limit minus the gear and pack you're already holding when sorting begins. On Paradigm a whole stack is grabbed in one `get 20 torch`; on Stock, with no batched get, that's sent as 20 individual gets — either way it's handled for you.
- **Gang-house guard emblems** (items named like "Gold Emblem", the ones that keep that house's guards from attacking you) are never swept as clutter, and a sweep only ever acts on items it found on a circuit-room floor during its own recon — never anything already in your pack.

---

## Equipment Sets (Player Workshop)

Not a Settings-window tab. Your character's gear loadouts — the four fixed sets **Default**, **Backstab**, **Pre-rest HP**, and **Pre-rest Mana** — are configured in the **Player Workshop**'s Equipment Manager, not in Settings. They're mentioned here because the Combat tab's weapon fields (Normal/Alternate/Backstab weapon) are actually populated from the Default and Backstab sets rather than being typed in directly — see the note under the Combat tab's weapon slots above. See the **Player Workshop** section for how to build and enable a set.

---

## Command-Line / Environment

MudPlay has a small command-line interface:

- **`--profile`** — launches straight into one or more saved profiles (see *Launch straight into a profile* under **Profiles** for the full syntax, quoting, and multi-instance behavior).
- **`--reconnect`** — connects on startup even when *Auto-connect when profile loads* is off. It exists for **Update the Client** to restore a session it interrupted, and it's only honoured for the profile loaded at startup.
- There is **no** `--data-dir` flag — to relocate the data folder use the `MUDPLAY_DATA_ROOT` environment variable below.

Any other startup arguments are the standard ones Avalonia consumes; the app does nothing further with them.

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
| Terminal font size | `null` (12) | 8–32 pt (fixed list) | `TerminalFontSize` | Models/Profile/GeneralSettings.cs |
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
| Sprint Mode (toolbar toggle) | `false` | bool | `SprintMode` | Models/Profile/GeneralSettings.cs |
| Auto-load last profile (edited on MainWindow, not Settings) | `false` | bool | `GlobalSettings.AutoLoadLastProfile` | Models/Settings/GlobalSettings.cs |
| Check for updates automatically | `true` | bool | `GlobalSettings.AutoCheckForUpdates` | Models/Settings/GlobalSettings.cs |
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
| Sysop powers — status / god lives (per-char) | `false` | bool ×2 | `SysopStatus` / `SysopGodLives` | Models/Profile/BbsCredentials.cs |
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
| Multi-attack 2 (enable + slot) | off, unset | bool + spell code + MaxCastsPerRoom(null/0-100) + MinManaPerCast (MinEnemies shared with slot 1) | `MultiAttack2Enabled`, `MultiAttack2Spell` | Models/Profile/CombatSettings.cs |
| Drain (life-steal) spell + HP trigger + Drains override AOE | unset / 50% / off | spell code + MaxCastsPerRoom + MinManaPerCast; DrainHpTrigger(0-100); DrainsOverrideAoe(bool) | `DrainSpell`, `DrainHpTrigger`, `DrainsOverrideAoe` | Models/Profile/CombatSettings.cs |
| Show combat round totals ⚠️ unwired | `false` | bool | `ShowCombatRoundTotals` | Models/Profile/CombatSettings.cs |

### Spells / Health

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Spell type priority (9 categories) | Emergency heal(1)…Debuffing(9) | 1–9 permutation | `PriorityEmergencyHeal` … `PriorityDebuffing` | Models/Profile/SpellsSettings.cs |
| Minor / Major / Emergency heal, HP Regen | unset | spell code | `MinorHealSpell`, `MajorHealSpell`, `EmergencyHealSpell`, `HpRegenSpell` | Models/Profile/SpellsSettings.cs |
| Cure Holds/Poison/Disease/Blindness | unset | spell code | `CureHoldsSpell` etc. | Models/Profile/SpellsSettings.cs |
| Unified buff list (self + party bless, room light, mana-regen + reroll, when-HP/MA-full) | empty | spell / `#item` + targets + recast + conditions | `PartyBuffs` (`BuffSettings`) | Models/Profile/BuffSettings.cs (Buff Watchdog) |
| Bless self while resting / during combat | false / false | bool | `SelfBlessWhileResting` / `SelfBlessDuringCombat` | Models/Profile/SpellsSettings.cs |
| Ignore poison, blindness, confusion, diseased (each suppresses both @wait + say) | false (all) | bool | `IgnorePoison` etc. | Models/Profile/SpellsSettings.cs |
| HP/MA threshold mode | `Percentage` (both) | Percentage / Absolute | `HpThresholdMode` / `MaThresholdMode` | Models/Profile/HealthSettings.cs |
| Rest max / Rest if below (HP, MA) | 95/60/95/30 (%) | 0–100,000 | `RestMaxHp`, `RestIfBelowHp`, `RestMaxMa`, `RestIfBelowMa` | Models/Profile/HealthSettings.cs |
| Run if below (HP, MA) | 20 / 10 (%) | 0–100,000 (0=off) | `RunIfBelowHp` / `RunIfBelowMa` | Models/Profile/HealthSettings.cs |
| Hang up if below | `5` (%) | death-floor minimum–100,000 | `HangIfBelowHp` | Models/Profile/HealthSettings.cs |
| Sys goto wimpy instead of hanging (+ location) | false / unset | bool / Sys Goto keyword | `SysGotoWimpyInsteadOfHanging` / `SysGotoWimpyLocation` | Models/Profile/HealthSettings.cs |
| Heal (rest) / Minor / Major / Emergency heal (combat) | 80/70/40/20 (%) | 0–100,000 | `HealRestTrigger`, `MinorHealCombatTrigger`, `MajorHealCombatTrigger`, `EmergencyHealTrigger` | Models/Profile/HealthSettings.cs |
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
| Party bless slots (part of the unified buff list — see Spells/Health above) | empty | configured in the Buff Watchdog | `PartyBuffs` | Models/Profile/BuffSettings.cs (Buff Watchdog) |
| Bless while resting / during combat | false / false | bool | `BlessWhileResting` / `BlessDuringCombat` | Models/Profile/PartySettings.cs |
| Help leader open doors / Ignore @wait when leading / Reset stats on loop start | false/false/true | bool | `HelpLeaderOpenDoors`, `IgnoreWaitWhenLeading`, `ResetStatisticsOnLoopStart` | Models/Profile/PartySettings.cs |
| Use @panic while leading / Ignore @panics | false / false | bool | `UsePanicWhileLeading`, `IgnorePanics` | Models/Profile/PartySettings.cs |
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
| Auto-train party | false | bool | `AutoTrainParty` | Models/Profile/AutoTrainerSettings.cs |
| Party members ready / level gap / leave level 11 solo | 2 / 5 / true | 1–6 / 0–200 / bool | `PartyMinReady` / `PartyLevelGap` / `PartySkipLevel11` | Models/Profile/AutoTrainerSettings.cs |
| Levels to keep banked / Do not train above level | 0 / 0 | ≥0 (UI 0–60 / 0–200) | `LevelsToKeep` / `DoNotTrainAbove` | Models/Profile/AutoTrainerSettings.cs |
| Announce level-ups / channel | false / Gangpath | bool / Gangpath,Gossip,Yell,Say | `AnnounceLevelUps` / `AnnounceChannel` | Models/Profile/AutoTrainerSettings.cs |
| Discovered trainers "Use?" | all allowed | bool per trainer (disabled-list) | `DisabledTrainers` | Models/Profile/AutoTrainerSettings.cs |
| Block @suicide when lives ≤ | `5` | 0–9 | `OtherSettings.MaxSuicideLivesThreshold` | Models/Profile/OtherSettings.cs |
| Utilize disarm traps | `true` | bool | `OtherSettings.UtilizeDisarmTrapsIfAble` | Models/Profile/OtherSettings.cs |
| @trap max searches / disarms | 20 / 5 | 1–100 / 1–50 | `MaxTrapSearchAttempts` / `MaxTrapDisarmAttempts` | Models/Profile/OtherSettings.cs |
| Door max bash / pick / Pick over bash | 10/10/false | 1–100 / 1–100 / bool | `MaxBashAttempts`, `MaxPickAttempts`, `PicklocksOverBash` | Models/Profile/OtherSettings.cs |
| Hide items when discarding | false | bool | `HideWhenDiscarding` | Models/Profile/OtherSettings.cs |
| @comeback backtrack rooms / auto-request | 10 / true | 1–50 / bool | `MaxComebackBacktrackRooms` / `AutoRequestComebackWhenLeftBehind` | Models/Profile/OtherSettings.cs |
| Pyramid / Asylum solver enabled | true / true | bool (Global) | `GlobalSettings.PyramidSolverEnabled` / `AsylumSolverEnabled` | Models/Settings/GlobalSettings.cs |
| Token routes: offer / min rooms saved | true / 3 | bool + 1–50 (Global, Paradigm) | `GlobalSettings.EnableTokenRoutes` / `TokenRouteMinRoomsShorter` | Models/Settings/GlobalSettings.cs |
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

The following were traced and confirmed to have **no** exposed setting — listed so it's clear they were checked, not missed: Telnet terminal-type string (fixed `"ansi-bbs"`), the Telnet option negotiation whitelist, TCP keepalive probe interval/retry count, outgoing text encoding (fixed Latin-1), and IAC byte-escaping. (The one command-line flag that *does* exist, `--profile`, is documented under **Profiles** and **Command-Line / Environment**.)

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

**Help → Report an issue…** opens the project's GitHub issues page in your browser, where you file the report and attach that snapshot. **Help → About MudPlay** shows the version, license, and bundled-component credits — handy when a report needs the exact build you're on.
