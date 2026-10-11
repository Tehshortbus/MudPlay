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

## Using this help

Pick a topic from the contents on the left; it opens on the right. The **Search help…** box above the contents doesn't hide anything — every topic stays where it is, and the ones whose title or text holds what you typed are **highlighted in amber**, with their sections opened so each hit is in view. The count under the contents says how many topics match. Open one and every occurrence in its text is highlighted too, with the page scrolled to the first. Clear the box and everything folds back up, leaving the topic you were reading open.

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

Open **Profile Management** from **File**, the **View** menu, or its toolbar button — it's the single home for your characters, your BBSes and their realms. If it's already open, re-selecting the menu item (or toolbar button) brings it back to the front (handy when it's hidden behind another window, or another running client). Below the current-profile strip are three linked columns — pick a **BBS**, then one of its **realms**, and the right column lists the **characters** playing that realm:

- **The current profile** (top strip) shows which character is loaded and gives you **New…**, **Save**, and **Save As…** — the same actions the File menu used to carry. **New…** starts a blank draft on the BBS selected on the left (so Connect and that board's settings apply to it), and **Save As…** names a draft under whichever BBS is selected. A quick **Save profile** stayed on the File menu as well (**Ctrl+S**, or the floppy-disk toolbar button), and **Ctrl+P** opens this window from anywhere.
- **BBSes** (left) — every saved board. **Add** creates a new one and takes you straight to its settings, since a fresh board has no host yet; **BBS settings…** (or a double-click on the row) reopens those settings for whichever board is selected; **Rename** retitles it (carrying its characters and your saved logins with it); **Remove** deletes it. Removing a BBS deletes **every character saved under it**, so the confirm names how many will go.
- **Realms** (middle) — the versions of the game the selected BBS hosts (see *Realms* under BBS + Display). **Add** makes a new one (default settings, no collected data), **Rename** retitles it (its data and characters come along), and **Remove** deletes it — **every character playing that realm is deleted with it**, along with its collected data, after a confirmation that names them (a BBS always keeps one realm). The **Game data** dropdown picks the realm's imported MDB and saves straight away; **Realm settings…** opens Settings → BBS on that realm for its game-menu commands, death floor, hang-up penalties, boss cleanup time and runic currency name.
- **Characters** (right) — the characters playing the selected realm (multi-select). **Add** creates a new one on this BBS playing that realm, **Rename** retitles it, **Copy** duplicates the selected character under a new name on the same BBS (settings, macros and its own game-data overrides all come along; the loaded character is saved first so the copy includes your latest changes). What the client read off the game for the original doesn't come along: stats, max HP and mana, carry weight, last position, learned spells, death history and the items in its gear sets (the sets come back empty; the Equipment Manager's checkboxes are kept). The copy is treated as a different character (another realm, level, race or gear), so on its first entry MudPlay reads `stat` and `i` for it, sending them itself if the login didn't. Then **Delete** removes it, and **Load** brings the selection online and closes the window. **Move to realm** puts the selected character on another realm of its BBS, and **Move to BBS** moves it to a different board (if that board already has a character by the same name you're asked for a new one; it starts on that board's first realm). The character you currently have loaded is shown in **bold** and marked *loaded*.
- **Load** is selection-aware, so you can bring a whole stable up at once: tick several characters and hit Load and you end up with one running client per character. The **first** selected character loads into **this** client when it's idle (a straight swap); each of the **rest** opens in its own new client instance. If this client is **actively connected**, it's left alone entirely — *every* selected character opens in a new client, so you never get the jarring disconnect → swap → reconnect just to launch alts. (Launching new clients uses the same multi-instance mechanism as the `--profile` command line.)

Because deleting, renaming, or moving the **loaded** character (to another realm or BBS), or removing the BBS or realm it plays on, would pull the rug out from under your live session, those actions ask you to **disconnect first**. Everything you do to *other* characters works while you're still connected. If you move a character to another realm while it's open in a different MudPlay client, that client follows the move the next time it saves the character, and switches to the new realm's data instead of writing the old realm back.

BBS **connection details** (host, port, redial, display, credentials) and each realm's settings are edited over in **Settings → BBS + Display**, and **BBS settings…** / **Realm settings…** in Profile Management are the shortcuts there — they open that tab with the selected board (and realm) already picked, so you don't have to find it in the Settings list.

The division of labour: **Profile Management** is where BBSes and realms are created, renamed, removed, and where characters are put on them; **Settings** is where a selected BBS's and realm's details are edited (realms can be added and renamed there too). Selecting a BBS in Settings **only** edits that board — it never moves your character (use **Move to BBS** for that).

## Setting up a BBS

First **add the board** in **Profile Management** (File → Profile Management → BBSes → Add) — that's where BBSes are created, renamed, and removed now. Add drops you straight into **Settings → BBS + Display** for the new board (you can get back there any time with **BBS settings…**, or by double-clicking the board in the list). Fill in:

- its **host** and **port**;
- your **username / password**;
- and — if the board needs it — the **automated logon** steps that walk you from the BBS menu into the game.

Reconnect behavior and terminal size live here too. These are **BBS-tier**: shared by every character on that board. (The board's **name** is set when you add/rename it in Profile Management.)

Each logon step is a **Message** to wait for and a **Response** to send when it appears (with `{username}` / `{password}` tokens for your saved credentials). Two rules keep the sequence healthy:

- **Add only steps that LOG YOU IN** — never a log-out or quit step (e.g. a *"Are you sure you want to log off? (Y/N)"* confirmation, a common MegaMUD holdover). That prompt never appears on the login path, so a logout step just sits there unmatched and stalls the sequence.
- **You don't need a final "enter the realm" step.** Once your steps reach the game's entry menu, MudPlay sends the entry command for you — and it does so even if your steps don't perfectly reach the end, so an automatic reconnect after a drop still lands you back in the game.
- **You don't need a step for the bulletin pager.** If the board pages news or bulletins with *"(N)onstop, (Q)uit, or (C)ontinue?"* during login, MudPlay presses Enter for you each time it appears and carries on with your steps — useful since that prompt only shows up on days there's something new to read.

(The one time it won't auto-enter is right after you hang up on purpose — a manual `@hangup`, a hang-up-on-low-HP / hang-up-when-naked rule, or a monster whose relationship is **Hangup** — so you can read the screen and enter manually.)

With that saved, **Connect** (Alt+H, or File → Connect) and MudPlay logs you in.

## Playing, and turning on automation

In the game, the terminal works like any MUD client — type a command and it's sent to the game. The **numpad is pre-wired to compass movement**, and you can add your own **macros** (key → command) and **aliases** (typed shortcuts). The startup splash plays until you connect or load a profile.

**The first time you enter the game on a new profile**, MudPlay reads your character on its own: `stat` for your stats, `i` for your inventory, and `sp` for the spells you've learned. Nearly everything automated leans on those three — the **Buff Watchdog** can't offer a buff until it knows you've learned it, for one. If a window says it has nothing to work with, typing `stat`, `i` or `sp` in the game is the first thing to try.

The automation engines — Auto-Combat, Auto-Heal, Auto-Nuke, navigation looping, and more — are **toolbar toggles** whose behavior is tuned on the matching Settings tabs. Flip them on to let MudPlay fight, heal, and travel for you. The **Combat**, **Navigation & Looping**, **Party Play**, and **Healing & Spells** sections explain how each engine decides what to do.

---

# The Interface

The **terminal** is the center of MudPlay — everything the game sends, rendered as a CP437/ANSI screen, and where everything you type is sent. Around it, every other panel is a **modeless window**: open it from the **View** menu, a **toolbar** icon, its **hotkey**, or the **terminal's right-click menu**. Every window's control works the same way: if the window **isn't open**, it opens; if it's open but **buried** (behind another window, or minimized), it comes **to the front**; if it's **already in front** (focused, or on top with nothing covering it), it **closes** — so the same key both summons and dismisses. **Settings** saves your pending changes as it closes this way, just like **OK**; the title-bar **X** and **Cancel** still throw them away. Closing MudPlay itself, or restarting it for an update, with Settings still open also saves them. A menu entry that opens a window at a particular tab (e.g. *Settings → Events*) switches an open window to that tab instead of closing it, unless it's already showing it. The terminal always stays live while you configure or check anything.

## The terminal and status bar

Type, and your keystrokes go straight to the game. The **numpad** is pre-wired to compass movement out of the box. The terminal has a few input conveniences:

- **Paste** — **Ctrl+V** or **Shift+Insert**. A single line drops onto your input; a multi-line paste is sent as one command per line.
- **Tab-complete** — completes what you're typing against the START of your carried, worn, and key-ring item names (`drop emerald-` + **Tab** → `drop emerald-hilted rapier`; a "bronze emblem" needs `bro`/`bronze`, not `e`). Keep typing to narrow it — `drop padded h` + **Tab** → `drop padded helm`. A stack's count (`2 padded helm`) and a leading "a"/"an" are skipped, so you match on the name; a count you type yourself (`drop 5 padded h`) is left as is. Press it again, or **Shift+Tab**, to step through other matches; toggle it off in **Settings → General**.
- **Right-click menu** — your starred GOTO **Favorites** and **Recent destinations** (the last 10 places you walked — click either to walk there) lead the menu, followed by a set of entries you fully control: by default quick-opens for Backscroll / Player Workshop / Party / Spell Book / Conversation / Navigation / Session Stats, **Reset States** (the recovery escape hatch — see Automation), and **Bug report…**. Rebuild that lower section — add commands, direct links to a Workshop tab or a calculator, your own fly-out folders, and rename anything — under **Settings → Toolbar + Shortcuts** (see *Customizing the terminal right-click menu*).

The status bar along the bottom packs several live readouts. This is the default layout; you can change what it shows, add more rows, or make a row crawl like a ticker under **Settings → BBS + Display → Status bar** (see *Status Bar*):

- The **connection light** — **red** idle · **yellow** connecting · **green** connected (a reconnect countdown shows beside it while reconnecting). It's just the dot; hover it for the state text.
- An **engine-state badge** mirroring the Navigation one — **IDLE / WALKING / LOOPING / AUTO-LAIR** — whose border turns **yellow** then **red** as the engine-recovery gate escalates.
- Your **location** (the map/room key), then the session's **exp/hr** rate, then **TNL:** — the estimated time to next level at that rate, followed by a bracketed **(+N.NN lvls)** ratio. TNL runs as a **countdown**: once estimated it ticks down second by second, and only resets when a fresh estimate differs by more than a little (a much better or worse stretch, a level gained), not on every kill. Under 10 minutes it shows minutes and seconds (`4m 12s`), under a minute just seconds. Session Stats and your own Party-window row read the **same** clock, so all three always agree. Because TNL counts to the next level you can still *earn* (banked-but-untrained levels are already skipped), that bracket says how far past your current trained level your exp already sits — e.g. `(+2.91 lvls)` means you've banked two full levels and you're 91% of the way to a third, so a large TNL time on a lower level reads clearly. It rounds down, so 99.6% of the way to a level still reads `0.99`, not `1.00`. It's the same figure a website toplist shows in brackets beside a player's level.
- A **TGT HP:** readout that appears after you `look <monster>` — a coarse wound band × the monster's max HP, so you get an absolute HP range (invaluable on fast-regen bosses), with a bracketed best guess from the damage it's taken since (see *Print monster HP in the terminal when I look*). The same estimate is also printed as a yellow line in the terminal, which **Settings → Other** can switch off. To take the readout off the bar, remove the **Looked-at target HP** item under **Settings → BBS + Display → Status bar**. The max HP is read from the monster record **placed or summoned in your current room**, so a display name shared across zones (an "orc lieutenant" in the barracks vs the slums) resolves to the one you're actually fighting.
- **Tick countdowns** — the combat round tick, the natural HP-regen tick, and the mana / meditate tick. They run on one clock: the game pays regen on a combat-round boundary, so a round seen on the wire sets the regen countdowns and a regen gain keeps the round countdown true between fights. A room's own damage (the volcano's *You are seared by the flames*) runs on a timer of its own, every 6 seconds or so, and leaves the round countdown alone: the client knows the line from the spell on the room you stand in, or from its naming nobody who dealt it. Four damaging room spells on Paradigm have no damage line on record yet (freezing cold, ocean drowning, bog poison, murky drown); if one of them words its damage like a monster's hit, the round countdown can still jump there. The intervals follow the realm — Stock: HP and mana every 30 s, a rest tick 21 s after lying down, a meditate tick 15 s after kneeling; Paradigm: HP every 10 s, mana every 30 s, a rest gain every 5 s on the round, a meditate gain every 15 s in step with the mana tick. The HP countdown follows passive regen only: it works out what regen can pay your character (from your level, Health and HP-regen gear) and leaves out a heal, or a buff that heals a little every few seconds (regeneration, righteousness and the like), by its size and by where it falls between regen's own ticks.

## The toolbar and menus

A customizable **toolbar** of icon buttons sits under the menu bar. The full bar is **File · View · Action · Game Data · Tools · Help · Bug Report** — **Action** is your in-play on/off surface for the auto-engines and the manual one-shots (see **The auto-engines** and **Manual one-shots and Reset States**), **Game Data** switches imported data sets, and **Bug Report** captures client state to a file on your Desktop. You choose which toolbar buttons appear — and rebind every shortcut — in **Settings → Toolbar + Shortcuts**.

### Customizing the terminal right-click menu

The bottom of **Settings → Toolbar + Shortcuts** also lets you build the **terminal right-click menu** — everything that appears when you right-click the terminal. The whole menu is yours to arrange from a pool of everything addable:

- **Favorites / Recent destinations** — the GOTO walk fly-outs (your starred locations and the last places you walked, click one to walk there). They're at the top by default but you can move, rename, or remove them like anything else. Once placed they always show; on a new profile with an empty list the submenu just reads "(none yet)".
- **Commands** — any individual command from the File / View / Action / Tools menus (window opens, one-shots like Get All / Reset States, utilities like Bug report / Program Log / Wire Inspector). Auto-engine toggles are deliberately left out — those belong on the toolbar / Action menu.
- **Drop ▸ / Hide ▸ / Equip ▸** — ready-made submenus of the drop, hide and gear-set actions. Add the whole submenu, or just the single action you use (e.g. only **Drop Coins**, or **Equip Backstab set**) — both are in the pool.
- **Workshop tabs** — a direct link that opens the Player Workshop straight to a chosen tab or sub-tab (Character Info, Equipment Manager, Item Finder, Calculators, Bosses, Chest Offload, Roomba, Realm Rankings, …).
- **Calculators** — a direct link that opens the Workshop on the **Calculators** tab with a chosen calculator (Hit / Movement / Swing / Backstab / Mana Regen / Monster Aggro) **expanded and centered** on screen. (Realm Rankings used to be listed here; it is a Workshop tab now, and a menu that already had it keeps the entry.)
- **Settings tabs** — a direct link that opens the Settings window straight to a chosen tab (General, Combat, Health, Party, Statline, Auto-Lair, …) instead of wherever it was last. (The plain **Settings…** command opens the window on its last tab.)
- **Game Data** — a direct link that opens the Game Data Browser on a chosen table (Monsters, Items, Spells, Rooms, Shops, Classes, Races, Messages, Players, Macros, Triggers, Aliases, and the rest). (The plain **Game Data Browser…** entry opens the window on its last tab.)
- **Folders** — click **Add folder** to add your own named submenu that flies out to the side. To fill it: select the folder and add items from the pool (the **Add** button reads **Add into folder** while a folder is selected). To move an item that's *already* in the menu into or out of a folder, just use **Move up / down** — an item stepping toward a folder moves *into* it, and the first/last item in a folder steps *out* of it when you move it up/down past the edge. Reordering a folder moves its contents with it.
- **Separators** to group things.

Select a placed entry (or folder) and type a **Name** to rename it however you like — an entry still links to the same action; leave an entry's name blank to use its default. **Move up / down** to reorder (a folder moves with its contents), **Remove** to drop one (removing a folder removes its contents), **Reset** to restore the built-in menu. Changes save when you click **Apply** (per character). You can also **Import from profile…** to copy another character's menu, or **Import from file… / Export to file…** to share a menu (a small `.json`) with a friend.

## The windows

Each is modeless; pressing its key again brings it to the front if it's buried, or closes it if it's already in front. Default hotkeys are shown; all are rebindable.

**Whose window is it?** Every window's title ends the same way as the main window's: its own name, then your **profile**, then the **BBS and realm** — `Settings — Bob — Paradigm:Paradigm PVE`. Running several clients at once, that tells their Settings, Navigation and other windows apart in the taskbar and on screen. The tail follows the profile and BBS you have loaded, changing in every open window when you switch.

- **Navigation** (Alt+M) — the room map: where you are, your route lines, and the controls for GOTO, loops, and Auto-Lair.
- **Backscroll** (Alt+L) — scroll back through terminal history, with search and export. See **Tools & Diagnostics** for how to use it.
- **Conversation** (Alt+C) — chat, gossip, and telepaths collected in one window with their own input box, per-channel colors, and optional logging. See the **Conversation** section for how to use it.
- **Party** (no default hotkey — View → Party, a toolbar button, or right-click → Open Party) — your live view of the party: each member's rank, health, status, and an uninvite button. See the **Party Play** section for how to use it.
- **Program Log** (F4) — a running diagnostic of what the engines are doing; the first place to look when something automated didn't behave. See **Tools & Diagnostics** for its filters and toggles.
- **Player Workshop** (F1) — your gear sets and the Item Finder, CP allocation and level projection, quest log, boss timers, and death history. See the **Player Workshop** section for how to use it.
- **Game Data Browser** (F3) — the imported game-data tables (rooms, items, monsters, spells) you can browse and override per-character. See the **Game Data** section for how to use it.
- **Buff Watchdog** (View → Buff Watchdog, or a toolbar button — no default hotkey) — the one place you configure every automated buff and watch each one's live recast timer. See the **Buff Watchdog** section for how to use it.
- **Spell Book** (F2), **Monster Intel** (View menu, or the toolbar's *Monster Intel* button — no default hotkey), **Session Stats**, **Round Totals** (View menu — each round's damage table in a small window of its own) and **Wire Inspector** (F5) round out the set — a read-only spell reference, a monster reference, session counters, the round table, and raw wire I/O for troubleshooting. The Spell Book is covered under **Healing & Spells**; Monster Intel under **Game Data**; Session Stats and the Wire Inspector under **Tools & Diagnostics**.

The **Settings** window follows the same modeless rule — the terminal stays interactive while it's open — and **OK / Apply / Cancel** decide whether your edits stick.

**Snapping windows together.** As you drag the panel windows — Conversation, Party, Buff Watchdog, Player Workshop, Navigation, Spell Book, and Session Stats — they **snap flush to each other's edges** when you bring one within about a finger's width of another, so you can build a tidy layout without lining anything up by hand.

Dragging the **main window** then carries the whole snapped cluster with it, keeping your arrangement intact; grab any of the other panels and it **pulls off freely**. Turn this off with **Settings → General → "Snap windows together"** if you'd rather every window float on its own. (Windows opened from *inside* a panel — editors and dialogs — don't snap.)

**Your profile remembers your windows.** Saving a profile, which it also does when you close the client, records where each window is and **which ones are open**. Loading that profile reopens them in the same spots: Buff Watchdog, Navigation, Party, Spell Book, Settings, Player Workshop, Game Data Browser and the rest. Loading a *different* profile closes the windows it didn't have open and opens the ones it did, so each character comes back to its own layout. Windows reopen fresh; changes you had made in the Settings window but not yet saved are saved as the client closes (see *Does MudPlay save automatically?*). The two Roomba windows open from the Player Workshop's Roomba section, so they don't reopen on their own.

If a panel ever drifts off-screen or the layout gets untidy, **View → Reset layout** returns every window to its default position and size.

## Keeping MudPlay up to date

MudPlay can update itself from its GitHub releases — but only when you ask it to. Nothing is ever downloaded or installed in the background.

- **When it checks.** On launch, MudPlay quietly asks GitHub whether a newer build has been published for your platform — and then again at **9am and 9pm**, so a client you leave running for days doesn't keep reporting whatever was true when you started it. Both are governed by **Settings → General → "Check for updates automatically"** (on by default). With it off, nothing checks on its own and you can still check by hand any time. Opening the update window also checks if nothing has been checked yet this session.
- **How you're told.** Two notices, and that's the whole effect — nothing downloads or installs on its own. A red **UPDATE AVAILABLE!!!** banner flanks the title on the startup splash screen, and for as long as the update is waiting the same message **scrolls across the window's title bar**, highway-sign style. The character and BBS stay pinned on the end of the title while it scrolls, so running several clients at once you can still tell them apart in the taskbar. The crawl stops on its own once you've updated.
- **Update the Client.** **Help → Update the Client** (also on the **Tools** menu) opens a small window that tells you whether you're up to date or what the latest version is, shows the download size and the new version's changelog, and offers a **View release notes** link. If a newer build exists, click **Check again** to re-query, or **Download & Restart** to install it.
- **What "Download & Restart" does.** MudPlay downloads the release archive for your exact platform, **verifies its SHA-256 checksum** against the release's published sums (it refuses to install anything that doesn't match), unpacks it, then closes, swaps the new build into place — **replacing whatever folder you launched from, however custom** (keeping a one-time backup so a botched swap rolls back; a swap that fails restores your current version, relaunches it, and leaves **`MudPlay-update-failed.log`** beside the MudPlay program saying which step failed. The relaunched client points you to it; attach it to a bug report) — and relaunches into the new version. On Windows the program file is swapped in by renaming, so another MudPlay client that's still closing, or one you open while the update runs, keeps working and never catches a half-copied program — clients already open stay on the old version until they restart. Any permissions you set on the MudPlay program file by hand (Properties → Security) are carried over to the new one. When the swap succeeds it cleans up after itself: the download, the backup, and the helper script are all removed. Your profiles, settings, and data folder are untouched — only the program files are replaced.
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

**Taking a round yourself.** If you hand-type an attack mid-fight — a **combat spell** (any spell that costs round energy, as opposed to a 0-energy heal/buff) or a **physical attack** (`a`…`attack`, `aa`/`bash`, `sma`/`smash`, `bs`/`backstab`, `pu`/`punch`, `kic`/`kick`, `ju`/`jumpkick`: every spelling the game takes) — the engine treats it as a **user override** and holds its own auto-attack for that round, so it won't fight you by re-sending its action on top of yours. Control returns automatically on the next combat round: if one of the engine's own heals or buffs stopped the fight during the round you took, it re-attacks then. `bash` or `aa` followed by a direction (`bash n`) is a door, not an attack, and doesn't count. `sm` isn't a command the game knows (smash starts at `sma`), so it doesn't count either. Nor does a line that is a text exit of the room you are standing in: `jump pool`, where the room has that exit, is a move, though `jump` is otherwise a jumpkick.

A hand-cast **heal/buff/cure** (0 energy) is *not* an override — after it lands the engine resumes attacking right away, same as before.

**Changing gear by hand mid-fight.** Putting a piece on or taking one off (`eq`, `wear`, `wield`, `rem`) makes the game stop your attack, the same as a cast does. Type one during a fight and the engine attacks again as soon as the game says the fight has stopped, as it does after a gear swap of its own. Several in a row get one re-attack, and the fight is picked up again with the next round if the last of them stopped it. It is the same for a gear command of yours that the client sends for you: one in a macro, an alias, a trigger's response or an event's Command action. All of it needs Auto-Combat on; with it off nothing is re-attacked. (The Action menu's Equip entries swap a whole gear set through the Equipment Manager, which re-attacks after its own swap.)

**Stopping the fight yourself: `break`.** Type `break` (or `bre`, `brea`: the game takes it from three letters; `br` is broadcast) while Auto-Combat is fighting and the engine **holds its attack** on the monster it was fighting. The terminal says so: `[Attack on <monster> held by your break: attack to carry on]`. While the hold is up the engine sends no attack at all, at that monster or any other in the room: no swing, no attack spell, no room spell, no backstab. Heals, buffs and cures carry on. A monster hitting you does not end it; you chose to stand.

A loop or walk **waits in the room** while the hold is up, as it does for any fight. A **flee still runs**: the low-HP or low-mana run, the run from a monster set to Flee, and the run from a player all move you as usual, and leaving the room ends the hold. (A party follower never flees, hold or no hold.)

The hold ends, and the terminal says why, when:

- **You attack.** Any attack command (`a`, `aa`/`bash`, `sma`, `bs`, `pu`, `kic`, `ju`, …) or an attack spell, at that monster or another. The engine carries on with what you attacked. An attack in a macro, an alias, a trigger's response or an event's Command action counts as yours. A text exit of the room (`jump pool`) is a move and does not.
- **The monster dies or leaves,** whoever killed it. The engine then picks its next target as usual. The game tells the client nothing when a monster it isn't attacking dies, so it goes by three things:
  - **One fewer monster of that name in the room.** The client only knows monsters by name, so with several of the same name, the death (or the leaving) of any one of them ends the hold.
  - **Experience from a kill of your own** in the round you typed `break`.
  - **A full round with no attack from it.** A hostile monster attacks every round, so a whole round with no attack from it (a hit or a miss, on you or on anyone else in the room) may mean it is dead. **Silence for a round makes the client look; the look decides.** It looks at the room once (a bare Enter), one to two rounds after the monster's last attack:
    - the room no longer lists it (or lists fewer of its name): the hold ends and the next target is picked;
    - the room still lists it: it is alive, and **the hold stays**. That is a neutral that never fought back, a monster that is stunned or held, or one whose attack printed nothing the client reads. While it stays silent and listed the client looks again no more often than every 30 seconds.
    - **In a dark room, silence alone decides:** a look lists nobody there, so after the silent round the hold simply ends.
    - With Auto-Combat or Auto-All off the client sends no look, and the hold just stays until the engine is back on.
- **You leave the room,** the connection drops, another profile is loaded, or you press **Reset States**. The map correcting where it thinks you are (in a grid of rooms with the same name, say) is not leaving, and the hold stays.

Turning **Auto-Combat** off and on again does not end the hold, and neither does the **Auto-All** master switch: with the engine off there is nothing to hold back, and the hold is still there when it comes back on.

**A neutral you attacked and then broke from** is left alone for good: even when the hold ends some other way (another monster of its name dies, say), Auto-Combat doesn't go back to it until you attack it again. If it attacks you, self-defense fights back as usual.

**Whose `break` counts.** Yours, and one a party member sends you with **`@do break`** or, said aloud, **`@party break`**: either holds the attack exactly as yours does, and the terminal names who asked (`[Attack on <monster> held by <name>'s @do break: attack to carry on]`). An attack they send the same way lifts it again. Each needs what it always needs (the Execute commands grant for `@do`, being in your party for `@party`), and with **Auto-All** off no remote command is followed, so nothing is held. A `break` the client sends for itself (before fleeing, for hit and run, before a rest, from the Sys Goto menu) holds nothing. With Auto-Combat off, a `break` after an attack you typed yourself is remembered the same way, without the terminal line.

## Fighting back (self-defense)

MudPlay normally leaves **Friend** and passive **Neutral** monsters alone. But if one turns hostile and starts swinging at you — you provoked it, or it just attacks — the engine **fights back**: any monster actively attacking you is engaged and finished, even one it would otherwise walk past. This is always on whenever **Auto-Combat** is on.

Exemptions: monsters whose Game Data relationship is **Flee** or **Hangup**, and any room you've marked **"do not attack"** (self-defense honours that too). A **Hangup** monster draws a hang-up the moment it is seen, before it can attack (see *What each Relationship does* under Game Data → the monster record). The exception is when no hang-up is coming for it, with the toolbar's **Disable hangups** on or in the minute after a reconnect: then a Hangup monster that attacks you is fought back like any other. A **Flee** monster is run from the moment it is seen, when a walk or loop is running (see *How Flee works* under Game Data → the monster record). It is never attacked on sight, and while a run is under way it is not fought. The exception is when no run is coming for it (nothing is running to run along or it is paused, there is no way out, you are following a party leader, the flee's own switches are off, or a run is over and the monster is still beside you): then a Flee monster that attacks you is fought back like any other. Everything else — Friend, Enemy, Neutral — you defend against.

Self-defense is also suppressed while you're on a **walk-to** — an evil character crossing a guarded town, say, keeps running to their destination rather than stopping to fight the guards (a losing trade at low levels). It stays active when you're **idle, looping, or Auto-Lairing** (all farming/holding, where fighting back is what you want).

**Monsters that pass you in a doorway.** A monster can enter the room you're just leaving after you've sent your move but before the next room appears. That monster is in the room behind you, so MudPlay doesn't attack it, and the room you arrive in decides what you fight. If your move is refused and you stay put, it attacks the arrival as normal.

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

The status line spells out **what the engine is doing** — e.g. *"Looping Ring - step 4 of 12 on lap 3"* or *"Walking to (12/431) Tower"*. A small colour-coded chip before it shows the state — Moving / Fighting / Waiting / Paused. While you have a running walk, loop or Auto-Lair paused, the engine badge itself reads **PAUSED**.

**Why it's held** shows as amber chips after the line, one per hold in force: *Mortally Wounded*, *Confused*, *Held*, *Feared*, *Low HP*, *Low MANA*, *Corpse Recovery*, *@Wait <name>* (a party member asked you to wait), *Downed Ally <name>*, *<name> disconnected*, *Waiting on <name> to join*, *Asking who holds <item>* (a walk in a party waiting at its start to hear who has an item it is to fetch; it shows for a moment even when a route card's count is reused), *Auto-all is off*, *Searching Room*, *Roomba*, *Waiting to Sneak*, *Waiting to Debuff*, and *Buffing* / *Curing* / *Healing* when a sneaked walk stops in a clear room to cast. Brief everyday holds (looting, sneaking, gear swaps, the quick room checks) don't get a chip. A *Held* hold normally ends on the game's own "you can move again" line; if that line is missed, it ends the next time a move of yours goes through (typed, or dragged by your party leader), since a held character can't move. An **errand trip** that pauses the run and walks somewhere else gets a cyan chip for as long as it lasts — *Bank Trip* (auto-deposit), *Auto-Selling*, *Auto-Training*, *@Comeback* (going back for a party member, until they're picked up or given up on) — since the line itself only names where the walk is headed. Once the errand is done, the walk back reads *Back to Loop* (*Back to Lairs* for Auto-Lair). Many holds last a split second, so when one ends its chip fades out over three seconds instead of vanishing — long enough to read. The engine doesn't wait for the fade; only the display lingers.

A route that's **queued but not moving** says so and names the hold (a common one is **auto-engines off (Auto-All)** — the master switch went off while it was running, so it stands until you turn it back on; a run you try to *start* with the switch off is refused with a terminal notice instead).

When a walk / loop / Auto-Lair **can't continue**, the reason is named rather than a bare "lost": a blocked loop shows the offending door / winch / hidden exit and room, an Auto-Lair whose approach keeps failing shows *"retrying: …"*, and the **Lost — couldn't recover** dialog names the last room the engine was sure of so you have a concrete place to right-click **"I am here"**.

**When the game turns a step away.** A gated exit (level, alignment, item, a timed portal) or a room command whose conditions you don't meet (*"You cannot do that right now!"*, *"A strange power holds you back!"*, an NPC who won't transport you) is recognised the moment the game says so. The route retries that step once, then re-plans, instead of waiting for its stall timer. A toll or a fare you're refused at is never retried (see **Tolls and paid transports**, next). An NPC who refuses a transport isn't asked again, since a refusal isn't an unlucky roll. A **fall** ("You fall to the ground with a thud…", "…damage from the fall!") — a failed jump — usually lands you somewhere else, so the map re-checks where you are from the next room display instead of assuming the jump's destination, and the route replans from there.

**Tolls and paid transports.** A walk, a loop or an Auto-Lair trip goes through a toll exit, asks a paid NPC transport or boards a sea captain's sailing only when the coin you carry covers it, and goes round it otherwise. Leading a party, a paid NPC transport or a sailing is also left alone when a member whose purse MudPlay has just read (its `@wealth` answer) is short of the fare; a member who hasn't answered isn't counted. A toll with a party in tow has rules of its own (*A party at a toll*, below). The coin you carry is what your last inventory read showed, plus what you've picked up and less what you've spent since. Two things make MudPlay stop believing that figure until the inventory is read again, and while it does no toll or fare is taken:

- **A death.** Your coin goes with your deathpile, so until the `i` MudPlay sends at the graveyard is answered your purse counts as empty, whatever you carried before. An arena death takes nothing and closes no toll: on Paradigm that is a death in an arena room (the Training Grounds, the Arena Practice Rooms, the Dwarven Arena, the arena passages of map 11); on Stock it is a death the game itself answers with *But, because you were in a colliseum, you have been saved.* (those same rooms, while the board has its arenas switched on).
- **A toll or fare the game refuses although the figure said you could pay.** The figure is wrong (coin can leave your purse with nothing printed). MudPlay sends one `i`. With Auto-All off it sends nothing, and the `i` goes out when you switch Auto-All back on.

**A refusal is the game's word.** The exit you were refused at is closed to every route from that moment, whatever MudPlay made of your purse or of the price, and the step is never sent a second time. A walk or a loop that was refused waits for the `i` above to be answered (three seconds at most) and then re-plans on what you really carry: round that exit, and still through any other toll you can pay. **The exit opens again once MudPlay has read your inventory and you carry the price the game named**; coin you pick up after that read counts at once, with no further `i`. If the figure already said you were short (you walked into the toll by hand), nothing needs reading, and it opens as soon as you carry the price. (A game line that names the price in some coin other than gold crowns can't be compared with your purse; that exit opens again at an inventory read once the trip is over.) An NPC's refusal of a fare is read from the one wording on record, *He says, "I may be old, but I count quite well and you are short!"*; a captain's refusal has no wording MudPlay knows, so a sailing you turn out not to be able to pay for ends when its arrival never comes, as before.

When the only way there crosses a toll, a fare or a sailing you can't pay, the walk doesn't set out for the gate. (Leading a party, a sailing that only a follower can't pay for is still sailed, with a warning in the program log, as it always was.) The terminal says `[Navigation: no walk to … - …]`, naming the crossing, its price and what you carry against it (*you carry 3 gold, 2 gold short*, or that your coin went with the deathpile and the inventory hasn't been read since). The Navigation window and the program log carry the same line. A route card that offers to **run to the blocked room anyway** states the same. **A loop** one of whose legs has no way but through a toll or fare you can't pay is not started, and one refused part-way round stops, with the same line (`[Navigation: loop '…': no way from … to … without …]`). When a walk does pay its way, the program log says which toll, fare or sailing it pays and on what purse.

**A party at a toll.** The game charges a toll to everyone who steps through and leaves behind whoever can't pay, so when you lead a party MudPlay makes sure of the whole party before it takes one:

- **It asks every member's purse** (`@wealth`) when a walk, loop or Auto-Lair trip sets out on a route that crosses a toll, and holds the step at the toll itself until the answers are in. Answers stay good for 30 seconds: on a longer way to the toll it asks again at the gate, and a toll you have just been let through voids them all, so a second toll right behind the first is asked afresh. It never asks at every step. A member who doesn't answer within the 4 seconds a round is given, or whose client says its purse is unknown, counts as unable to pay. Only an answer counts: a telepath that merely mentions coins is not read as a purse. (A member's client answers `@wealth` only if it lets you ask about its inventory: the *Query inventory* remote-control permission.)
- **Everyone can pay:** you go through.
- **Someone is short or didn't answer, and you can spare it:** MudPlay hands each of them what they lack with `give` (the whole toll to a member who didn't answer), waits for the game to confirm each hand-over, and then goes through. What it spares is what you carry above your own toll and above the fees a training trip has set aside; your Keep on hand amount is a banking setting and isn't held back, just as it isn't for your own toll. **It hands over exact change only**: the coins you carry have to make the amount, largest coins first, and it never rounds up to a bigger coin (a platinum piece or a runic for a gap of a few gold). A member whose shortfall your coins can't make exactly counts as one you can't cover.
- **Otherwise the toll isn't taken:** it is closed to the party for the rest of that walk, loop or Auto-Lair run, and the route turns as soon as the answers show it, without walking up to the gate first. It opens again when the trip ends, when the party's membership changes, when a member's new answer shows they can pay, or when your own purse grows enough to cover them. The route goes round it when there is a way round. When there is none the walk ends, and the terminal names the toll, who is short and by how much, who didn't answer, and what you could spare (`[Navigation: no walk to … - … a toll east from … (5 gold) the party can't all pay: …]`). A loop does the same at its toll leg and stops with that line when it has no other way.
- **A hand-over that fails** (the member has receiving switched off or can't carry it, the game counts over less than was sent, or no confirmation comes within 4 seconds) makes that member unable to pay for now: nothing is sent to them again, and the route goes round or the walk ends as above. Coin already handed to other members stays with them. If the game's line for a hand-over comes late (up to two minutes), the coin is still counted as theirs and as paid, so they are not handed it again; and a step you were let through that the game bounces is retried without paying anyone a second time. A selling detour that can't be paid for is passed over that time; the shop is not written off.
- **A member who isn't with you** is left out of the count only when MudPlay knows it: the game told you they stopped following, or you are going back for them (or will) after an exit left them behind. The decision in the program log says who was left out. A member who is merely not shown in the room is not left out: they may be hidden, or have arrived after the room was drawn, so a hand-over to them that gets no answer makes them unable to pay like any other failed hand-over, and the party is not taken through without them. On Stock the game's *Why would you want to give to that?* (its answer to a give at nobody it can see for you) is read as that refusal at once.
- **A member paid for is not paid for twice at a toll that still turned them away.** If you went back for them from that toll and they are short again, the route goes round or the walk ends. One who got through on your coin and is short again on a later lap is paid for again.
- **With Auto-All off** nothing is asked and nothing is given.
- This is for tolls only. Paid NPC transports and sailings keep the rule above, and level and class gates are untouched.

While it asks or hands over, the Navigation window shows *checking the party can pay a toll*. Every decision is in the program log under `PartyToll`, and a bug report lists the latest ones with the purses they were made on.

**Commands that only work in an empty room.** Some room commands are refused while any monster is in the room, an NPC as much as a hostile: the Dark Alley's `go hole`, a fortress winch (*"You cannot do that while there are enemies present!"*), a number of portals and altars. A few of them refuse without printing anything at all. MudPlay reads this from the game data, so a walk or a loop doesn't send such a command into a refusal. It holds the step, clears the room first (**even with Auto-Combat off**), and sends the command once the room is empty. If a monster walks in after the command has gone out and it comes back refused, the same thing happens. When the monster is one MudPlay won't fight and it is still there a minute later with nothing else going on, the walk or loop stops and says why.

On **Paradigm**, that dialog should almost never appear: whenever the tracker drifts, the client asks the game `rm` for your authoritative room and re-anchors from the answer — before it ever falls back to the blind reverse-walk recovery, and again as a last resort before giving up — so it only truly gives up when `rm` itself can't answer (and a `rm` that a confusion fumble eats is simply re-asked until the confusion passes).

Even with **no automation running** — you've stopped the engines and are walking a block of identically-named rooms (a grid of same-name "Soldier's Quarters" cells, where no single room display can tell one cell from the next) by hand or being dragged by a party leader — the client keeps trying to place you: it narrows down which look-alike room you're in from the *sequence* of moves you make and the rooms they reveal, and silently re-anchors the moment that sequence fits exactly one room.

It sends nothing to the game to do this — it's pure inference from what you're already doing — and if the walk stays genuinely ambiguous it just stays Lost rather than guess.

The status line is also **colour-coded**: **amber** while a queued route is held for a reason (Auto-All off…) or Auto-Lair is retrying an approach, and **red** when a nav action fails or the tracker loses your position — so a problem is glaring rather than buried in grey.

A row of action chips — **Save**, **Go**, **Loop mode**, **Lair mode** — sits just above the map.

**Go, Run and Sprint.** **Go** starts what's queued: a walk-to, the loop you built, or the lairs you marked. Whenever Go would start something new, a small **▾** beside it offers two other ways to set out:
- **Run** — Go with **Auto-Combat off** for the trip.
- **Sprint** — Go in **Sprint Mode** (no resting stops, no fighting or looting).

Either lasts only for the trip there. Combat comes back on, or Sprint ends, the moment a walk-to arrives, a loop reaches its first waypoint and begins its circuit, or an Auto-Lair steps into its first lair. Stop the run yourself before then and they stay off, unless you've ticked *Stopping a Run turns Auto-Combat back on* / *Stopping a Sprint ends Sprint Mode* under Settings → Other. The same **Run** and **Sprint** choices sit beside **Go** in the route picker, on each loop, lair, Go To and the walk-to search in **Navigation Management**, and in the right-click menu of the rail's loops and lairs. While a loop runs, the chip reads **Pause**, and only turns back to **Go** when you pause it yourself — not every time a fight or a rest holds the loop. While you're in Loop mode a **Clear all** chip appears to the left of **Save**; it wipes every step from the loop you're building so you can start fresh.

Any label too long for a narrow rail is trimmed with an ellipsis — **hover it to read the full text**. This covers the status line, the GOTO / loop / lair / favourite rows, the live CURRENT NAV step list, search results, folder names, and the EXP/HR estimator rows.

## Walking somewhere (GOTO)

To send your character to a room:

- **Search** — type a room name or a map/room key (e.g. `1/297`) in the top search box, pick the match, then click the green **Go** chip (or **▾ → Run / Sprint**).
- **Ctrl+click a room** on the map — with nothing running, or while you're building a loop, it's queued just as if you'd searched for it: click **Go** (or **▾ → Run / Sprint**) to walk there. In the loop builder the click queues the walk instead of adding a waypoint, and your sketch is kept.
- **Right-click a room** on the map → **Walk here**.
- **Favourites** — save rooms you visit often (right-click a room → **Add to favorites**, or the Management dialog's **Go To** tab), then click one in the **GOTO** rail to walk there. In the Management dialog's **Go To** tab each saved room is listed as its label followed by its **(map/room)** number, so identically-named rooms are easy to tell apart. **Right-click a Go To** in the rail for **Walk here**, **Edit…**, **Move to folder…**, an **Add to / Remove from favourites** toggle (stars it — the ★ that promotes it to the terminal's right-click **Favorites** flyout — without deleting it), and **Delete this Go To** (removes the saved location entirely).
Type **"favourite"** (or any 3+ character part of the word) into the GOTO or loop filter box to surface your starred Go Tos and favourited loops.

MudPlay plots the shortest route and walks it, opening doors, disarming traps, and revealing hidden exits along the way. Click the red **Stop** chip to stop, or the **Pause / Resume** chip to hold and continue.

If you **type a movement command yourself** while a walk, loop, or auto-lair is running — a direction (`n`, `sw`, …) or a text-exit step (`go path`) — navigation **pauses automatically** so the automation never fights your hand-driven step. It's a user pause, just like clicking **Pause**: press **Start** (Alt+V) when you're ready to hand control back. Since nobody clicked Pause, MudPlay says what happened: the terminal prints `[Navigation paused: you typed 'u' - Resume to carry on]`, the engine badge reads **PAUSED** instead of LOOPING / WALKING, and the Navigation window shows a hold chip naming the command you typed. A move the game refuses (no exit that way) pauses navigation just the same. (Peeking with `l <dir>` doesn't count — that's a look, not a move.) A command a loop sends for one of its own waypoints doesn't count either, even when it's worded like a move. And when a waypoint's command is the very text exit the loop leaves that room by (`go path` on a room whose way on is `go path`), the loop sends it once rather than crossing and walking back; the command is redundant there and can be cleared from the waypoint.

**Stop during a money or training trip holds it.** While MudPlay is on a trip of its own — an auto-train run (fetching the coin, training, buying spell scrolls), a **Transfer Stash to Bank**, a bank or stash deposit trip, or a trip to sell — **Stop** does not throw the trip away. It holds it where it stands, the same as Pause, and the terminal says so: `[Stop is holding the training trip - Resume carries it on, Stop again ends it]`.

- **Stop again** ends the trip for good. (A double-click counts as one press.)
- **Resume** (the toolbar's Start / Pause button, or the Navigation window's) carries the trip on from where it stopped.
- **Start a walk, a loop or an Auto-Lair** and MudPlay asks **Resume it first?**
  - **Resume it first** finishes the trip, then starts what you asked for.
  - **No, drop it** ends the trip and starts what you asked for straight away. Coin already fetched stays in your pocket.

An ended trip is not undone and not remembered. Auto-train and the bank or sell trips come due again by your settings, so a loop you start afterwards may be interrupted for them again; turn the setting off if you don't want that. Spell scrolls are the exception: they are only bought after a train, so dropping that trip skips them until the next one.

Trips that are over in a few steps (a key or light purchase on the way, fetching a party member) are not held; Stop ends those as before. Reset States and a death always end the trip outright.

## Building and running a loop

A **loop** is a saved circuit of rooms MudPlay walks over and over, fighting and looting as it goes. To build one the quick way:

1. Click the **Loop mode** chip (it changes to **Building**). If you've run a loop this session, it's **pre-loaded** into the builder — with **all its settings intact** (per-room command / delay / no-rest / no-attack **and** the loop-wide Only-attack-in-lair flag), so you can **Go** straight away or re-**Save** it — handy when a stop / `@stop` dropped you off one, or you ran an ad-hoc loop you never saved. Hit **Clear all** to wipe it and build a fresh one instead. (Turn the pre-load off under **Settings → General → "Load last ran loop"** if you'd rather always start empty.)
2. **Left-click the rooms on the map, in order** — each becomes a waypoint. **Alt+click** a room to take it back out (a room you've added more than once loses its most recent click). To **move** a waypoint, press on its numbered chip and **drag it onto another room**. It keeps its number, command and flags, and the line re-plans through the new room. A drop on empty map, or on a room the loop can't reach, puts it back. Room tooltips stay hidden while you're holding a chip. Reorder or remove them in the **CURRENT NAV** rail too.
3. Click **Go** to start it, or **Save** to keep it without running.

**Starting a loop from somewhere else.** If you aren't standing on the loop when you start it, MudPlay first walks you to the nearest room on it. That walk is an ordinary walk-to: you get the same route cards a walk-to shows (walk or teleport, traps, avoided rooms, a gate that needs an item), and the Navigation window shows a walk until you arrive. The moment you reach the loop, the loop takes over and starts lapping. If you close the cards without picking, press Stop, or pick a route that ends somewhere else (running up to a blocked room, a stop at a shop), the loop doesn't start. While that walk is under way the map shows both: the walk's route line and the loop's ring it is heading for (with the **Loop lines** chip on), and the status reads *Walking to … then looping `<loop>`*.

You can start building a loop **while a walk-to is running** — building only collects rooms, it never moves you, so your walk continues uninterrupted. Clicking **Go** in the Navigation menu then hands movement over: it stops the walk and starts the loop. The **toolbar** Start / Stop / Pause buttons still control the *walk* itself, so reach for those to stop (or pause) the walk without starting the loop.

The builder closes by itself when the loop it holds (or an empty builder) is started from somewhere else: the toolbar's Start, the Manage dialog's Run, or a remote `@loop`. The map then shows the running loop, not the red build line. A different loop you were in the middle of building is kept.

To put a party member back on the **last loop they ran this session** without anyone reopening the builder, send them the **`@loop last`** remote command (and they can send it to you) — it re-runs that loop even if it was an ad-hoc one that was never saved. This works regardless of the "Load last ran loop" setting above. How `@loop` reads names and room lists, and what it answers, is under **@loop** in *Remote @-commands*.

Or build it off the map: **Navigation Management → New Loop** opens an editor where you add rooms by name or key, name and annotate the loop, and set per-waypoint options. While that editor is open, you can also **left-click rooms on the Navigation map** to append them to the waypoint list — the same way the on-map builder works, without typing keys.

**Run a saved loop** from the **LOOPS + AUTO-LAIRS** rail (or the Management dialog) — each has **Load** (stage it) and **Go** (start now; right-click, or the ▾ in Management, for **Run** / **Sprint**). Queue one and MudPlay joins the circuit at whichever of its rooms is nearest — a room partway along a leg as readily as a waypoint, and right where you stand if you're already on it — then carries on round from there (a lap begun partway along a leg isn't counted as a lap); combat, healing, and pickup keep running throughout. While it runs the badge reads **LOOPING** with "step X of Y on lap Z", and the **CURRENT NAV** rail shows the loop's rooms in **green** — click any room to tune it live without stopping (see *Live-editing a running loop* below). **Stop** ends the loop.

**Right-click a loop or Auto-Lair setup** in the rail for **Load**, **Go**, **Run**, **Sprint**, **Edit…** (opens its editor), **Move to folder…**, and **Add / Remove from favourites** — favouriting a loop or lair adds it to *both* right-click Favorites flyouts (the terminal's and the map's, green for loops, amber for lairs) for this character only alongside your starred GOTO rooms, so you can start it from anywhere.

Each waypoint can carry its own per-room settings, edited **inline in the Edit Loop table**:

- a **command** + **delay** (e.g. `rest`, `dep 100`, `ask barmaid pie`);
- a **"No rest"** flag;
- a **"No atk"** flag;
- **"Rest HP"** / **"Rest MA"** (rest up here) flags.

Chain several commands in one waypoint with `;` or `^M` — each is sent as its own line (e.g. `get all;drop coins`), the same convention macros and the pre-/post-rest commands use. With **no delay**, the loop waits for the game to answer every command in the block before it moves on (three seconds at most), and if the block started a fight it stays in the room until the fight is over. With a **delay**, the loop simply waits that long. Put a blank between two separators to send a bare Enter (`pull book;^M`): the game shows the room again, the loop waits for that too, and anything the command brought out is then in the room list for the combat engine to act on. A waypoint's command is the loop's, not your typing: a move in it doesn't pause navigation, and a cast or attack in it doesn't take the round from the combat engine. (You can set the same options by clicking a waypoint row in the CURRENT NAV strip — both while *building* a loop and while one is *running*; see *Live-editing a running loop* below.) If a route crosses a locked gate or a hazard room, a **Choose a route** prompt lets you take the free way around or push through.

**Packaged loops that get corrected.** The loops MudPlay ships are copied into a game-data set once and then left alone, so your edits and deletions stick. The exception is a packaged loop MudPlay itself had to correct: an update replaces your copy of that loop with the corrected one, once, and keeps your previous copy beside it as `<name>.loop.bak`. A packaged loop you deleted, renamed or moved to another folder isn't touched.

- **No rest** — the loop won't rest in this room even when HP/MA drop below your "rest if below" gates; it advances instead. Only this exact room is protected.
- **No atk (do not attack here)** — the loop skips combat in this room *as if auto-combat were off*, walking on even when the Min/Max monster count is met. The one exception: if a **rest** is triggered here (HP or MA below its gate), it still clears the room so the rest can proceed. Only this exact room is affected.
- **Rest HP / Rest MA (rest up here)** — on reaching this room, the loop rests until HP (or mana) is back to its **rest-max** (the *rest to* value on the Health tab) before moving on, even when it's above your *rest if below* trigger. If it's already at rest-max, the loop walks straight on. Tick both to top up both pools. **No rest** on the same room wins. The rail marks these rooms with 💤.

### Live-editing a running loop

While a loop is **running**, the CURRENT NAV rail shows its rooms as **green** rows — the running counterpart of the builder's red list. **Click any room** to change its **command**, **delay**, **No rest**, **No atk** or **Rest up here** right there, and use **⚙ Entire Loop Settings** to toggle **Only attack in lair rooms** — all applied **live**, with no stop/restart. The room the loop is currently in is **highlighted**, and the map draws matching **numbered green bubbles** on each waypoint (the running twin of the builder's red pins) so you can tell which rail row is which room. Flag changes take effect on the loop's next decision and a delay change on that step's next run; **adding or removing a command** re-plans the circuit on the **next lap**. You **can't add, remove, or reorder rooms** while running — for that, **Pause** in the Navigation window, which opens the builder (the red list) seeded from the loop; edit it, then press **Go** there to restart with your changes. Resuming any other way (the toolbar, a hotkey) closes an unedited builder and the loop's line turns green again; an edited one stays open so your changes aren't lost, while the running loop still shows green.

### Importing a MegaMUD loop

**Game Data → Import loops (MegaMUD .mp)…** (or **Import .mp** in Manage Loops) opens a `.mp` file in the **import review** window. Nothing is saved until you accept.

- **Left — what the file says.**
  - The loop's name and author.
  - Its start (and end) room, and the path details: steps, gold and item needed, the paths MegaMUD runs if it fails or when it's finished.
  - **Rooms.md** (MegaMUD's named-rooms file) is read from the same folder when it's there; **Load Rooms.md…** points at it when it isn't. Its room names end in their map/room numbers, so it pins down rooms the hashes alone can't. When the loop's name doesn't say where it starts, a banner asks for it.
  - Anything wrong with the file (a goto path rather than a loop, a step count that disagrees, a broken row) is listed with a ⚠.
- **Right — the MudPlay loop it becomes.**
  - Loop name and notes. When several rooms match the start, a **Start room** list picks which to walk from, best translation first.
- **The step table** below lines up the two, one line per step: the MegaMUD step on the left (room hash, its Rooms.md name, the move, extra commands in brackets like `s[search s]`, and the step's options) and the MudPlay room on the right (its name and map/room).
  - ✓ matches the recording; ≈ we walked there but the room's name or exits differ; ↺ found again after a gap; ✎ set by you; ✗ **untranslated** — the step couldn't be followed (a missing exit, a passage our map doesn't have), so its line is **left blank** rather than failing the whole import.
  - Type a map/room into a step's **Set room** box (left of the room) to put a different room there (clear it to go back to the translation).
  - **Reshape the MudPlay side** with the buttons at the end of each line:
    - **✕** leaves a step out of the loop. It stays in the table, greyed, beside its MegaMUD step, and **↺** puts it back.
    - **+** adds a room below the line. Type its map/room into the new line's **Set room** box; the new line has an empty MegaMUD side.
    - **▲ ▼** move a line up or down.

    The map redraws after each change (when **Display on map** is on), and your changes stay through **Verify** and a change of start room. Press **Verify** to check the reshaped loop still walks.
  - **Stash** adds that step's room to your stash rooms when you accept — stash rooms are a character setting, not part of the loop. It's ticked already on the steps MegaMUD marked as stash points; untick to skip, tick any other room to add it.
  - Each step's **command**, **delay** (ms to wait after the command) and **NR / NA / RH / RM** (no rest, no attack, rest up here HP / mana) can be edited. *Don't rest*, *don't attack* and *rest up here* carry over from the file; dark rooms, traps, locked doors and searches are handled from the map data as you walk, so they don't — ⓘ shows what wasn't carried over.
- **Verify loop in MudPlay** puts in the rooms you typed, translates the steps after them again, and checks MudPlay's navigation can walk the result as a loop — every leg planned the way the loop runner would, back round to the start. A leg it can't route is marked ⚠ on the step it leaves from.
- **Display on map** draws both versions of the loop on the Navigation map (it opens the map if it's closed and centres on the start room), so you can see where they part. **Orange (wide)** is MegaMUD's recording — its moves followed literally from the start room on our map, with no correction; a **red ✕** marks a room where a recorded step has no way on in our data, and the line carries on from there. **Cyan (thin)** is the loop as you've converted it, walked the way MudPlay would run it. Where they agree the cyan runs inside the orange; where they differ you see two separate lines. The picture updates as you change rooms and Verify, and it clears when the review window closes.
- **Accept** verifies again, saves the loop (blank steps are left out, and the loop routes between the rooms either side of them) and adds the rooms ticked **Stash**. **Reject** closes without saving. The loop's notes record what couldn't carry over (gold, item, fail/finish paths).

### Entire Loop Settings

Some settings apply to the **whole loop**, not one room. Reach them from **⚙ Entire Loop Settings** — it appears at the top of the CURRENT NAV area **while you're building a loop** and again at the top of the rail **while a loop is running** (click it for a flyout), and it's mirrored by a checkbox next to **Set as favorite** in the Edit Loop window.

- **Only attack in lair rooms** — the loop only engages hostiles in **lair rooms** (rooms the game data tags as monster lairs); every other room is walked through as if auto-combat were off. The inverse of per-room "no atk": instead of opting rooms out one by one, you opt the whole loop *in* to lair rooms only. A per-room **"do not attack"** still skips even a lair room (it wins), and a triggered rest still clears any suppressed room so it can rest. Note the suppressed rooms include the walked-through connectors between waypoints, not just the marked waypoints. The rule (and a room's **no atk**) is about walking on instead of fighting, so it only applies while the loop is moving you: stop the loop (Pause, or a party member's `@stop`) or start following a party leader and the client fights what attacks you as usual.
- **Wait to enter lairs until I can debuff** — for a combat profile with a **debuff** (the AoE debuff or the single-target one). The game allows one between-round cast a round, shared by buffs, heals, cures and that debuff. A buff cast a moment before you walk into a lair uses it up, so the debuff can't be cast on entry and your attack goes out first. With this ticked, the loop stops **one step short of each lair** until that cast is free, then walks in, and the debuff goes out on arrival (the nav line shows a *Waiting to Debuff* chip meanwhile). Pick how it waits:
  - **Wait for spells** — whatever is due is cast first, one a round, **three rounds at most**; then the loop enters. Your buffs stay up, at the cost of the wait. Past three rounds (or about 18 seconds), the rest is held back and the loop goes in as soon as the cast is free.
  - **Block spells** — buffs are held back from the moment the loop reaches the lair's doorstep, and it enters as soon as the cast is free (**a round at most**). The buffs go out after the fight. Heals and cures are never held.

  Either way buffs are held for the step in, so nothing takes the cast on the way through the door. It does nothing when the profile has no debuff set, when Auto-Combat is off, or for a lair marked **no atk**. Loops only: walk-to and Auto-Lair don't use it.

## Estimating a loop's exp/hour

The **EXP/HR ESTIMATOR** panel in the right rail projects how much experience a prospective circuit would earn per hour *before* you commit to it — factoring in boss respawn timers and room summon rates, not just a flat monster count. It simulates the loop's *actual room order* against each lair's respawn timer, so the **shape** of the circuit matters: an out-and-back line that re-crosses just-cleared lairs on the way back reads lower than the same rooms walked as a ring, because those return steps waste combat time — exactly how it plays out in game.

The estimate's assumptions live in the **⚙ Estimate Settings** flyout at the top of the panel — the **I'm Rooming** toggle (on = you hit the whole room at once; off = one mob at a time), **Rounds to kill a mob**, and the two biggest levers below. Two tunables drive the estimate most:

- **Seconds per room** is the single biggest lever, and the easiest to set wrong: it's your **effective time per room while looping and fighting**, not your raw walk speed. Each room is a move command plus its server round-trip plus an attack plus the 5-second combat tick, so the real pace is ~**1.2–1.4s** even when your bare movespeed is 1.0 (which is why it defaults to 1.4). Set it to your raw movespeed and a tight backtracking loop reads noticeably high, because the model then under-charges the wasted ticks in those empty return rooms.
- **Real-world multiplier** (0–1) scales the result down for the friction the room-by-room model can't simulate — the odd late combat round, a missed pull, imperfect pacing. **0.9–0.95** is the usual band for clean, attentive play, lower for a distracted session. It's a discount from the modeled ceiling, not a fudge factor — set it to the fraction of the ideal pace you actually sustain.

Click **Start estimating**, then **click the rooms** on the map to sketch the circuit (**Alt+click** a room to take it back out, or **drag** a numbered chip onto another room to move it); the panel shows a running **exp/hr** figure as you add rooms. **Save as loop** turns the sketch into a real loop, **Load loop…** pulls an existing loop in to evaluate it, **Clear rooms** starts over, and **Stop Estimating** exits the mode.

**Room summons** lists the rooms on the circuit whose room spell can summon monsters, with the exp those rolls are expected to add. Every monster a roll brings is counted, and a summon that only happens in a room with no monsters in it is counted only when you walk into the room empty. Two kinds of summon are left out, in the estimate and in the loop simulator alike:

- **A boss only one of which can be alive in the game, and which waits a while to come back** (Lord Skorne from the Farnholme portal, the Angelic Hunter in the Ancient Fortress). Once killed it is gone for its regen time, so later rolls of its line bring nothing and counting it on every roll would wildly overstate the loop. A one-at-a-time monster with no regen wait, like the tyrannosaurs on Paradigm's dinosaur forest trails, can be summoned again as soon as it is dead and is counted like any other. A room spell that summons nothing else, like the Ancient Fortress's, shows no summon at all.
- **A summon that needs an item in the room.** The graveyard's Death Shrieker appears only where a weeping statue stands, in 7 of the graveyard's 110 rooms, and takes the statue until the next cleanup. The graveyard counts its vampire fledglings and weeping apparitions alone.

The program log names each summon left out, the first time a set's room spells are read.

A small **Realm:** line under the headline notes which game-data realm is active — it never changes your kill rate, but it changes two things:

- **How a lair respawns.** On **Stock** a lair room keeps one clock, restarted by every kill in it (its placed fixture's too), and the whole room comes back together **`Delay` to `Delay + 1` minutes after its last kill** — the estimate uses the middle of that window. Killing a room's fixture on every pass can hold its lair empty. On **Paradigm** each monster comes back on its own, **`(Delay − 1)` minutes + 30 s** after it was killed.
- **How often a room's summon spell re-rolls** — on entry, then every 6 seconds while you stand there (about 6.05 s on Paradigm, measured; Stock's 6-second medium tick). It is the same timer a damaging room's heat or cold runs on, and it is a little slower than the 5-second combat round. A summon spell with a `nomonsters:` gate only fires while the room is empty, so it contributes a roll on a clear pass-through but nothing when you arrive to a full lair.

Use it to compare two hunting circuits without walking either one.

### Simulating your character on the loop

The estimate above assumes a fixed **Rounds to kill a mob**. The **Simulator** instead plays **your** character around a loop in simulated time. Open it with **Start simulating** in the EXP/HR ESTIMATOR section — you don't need to be estimating. Pressing it again brings the window to the front, or closes it when it's already in front. Its results stay put while the Navigation window is open, so closing and reopening the Simulator loses nothing.

At the top, **Route** picks what to play: any of your **saved loops**, or **Estimator sketch** while you're estimating and the sketch has at least two rooms. The run settings sit beside it, and three tabs hold the three tools: **Simulation**, **Check against my play** and **Rank areas**. **▶ Simulate my character** plays the chosen route and the **Simulation** tab reports what it earned:

- **exp/hr** (averaged over several runs, with the range between the luckiest and unluckiest), kills/hr and seconds per lap — a run that dies earns nothing for the rest of its hours, so a deadly loop's exp/hr drops accordingly;
- **where the time goes** — attacking, moving, resting, meditating, waiting — the same split Session Stats shows live;
- the **lowest HP and mana** it reached, and whether it **died** (a run that dies stops there);
- which spells it cast, per hour (`bs` counts your backstab openers).

It plays by everything the client already knows about you: your stat screen, the gear you're wearing, the spells you've learned, your **Combat** attack spells, cast caps and debuffs, your **per-monster overrides** (e.g. exor on undead), your **Health** rest / meditate / run / hang-up triggers, your heal tiers, and your **Buffs** list (the solo self-buffs, recast at their margin, with a mana-regen roll spell rerolled below its threshold — held until the fight ends and paused at your buff mana floor, as live — and its roll feeding your mana regen while it's up). Each round is decided by the same code the live combat and heal engines use, so it picks what the client would pick. The monsters fight back with their real attacks, energy and between-rounds spells; lairs refill on their real respawn timers — at once when you walk in after the timer, a few seconds later when you're already standing in the room; a monster's death spell summons its next tier, and a summoning room rolls its summon table on entry and every round (Paradigm) or 6 seconds (Stock). A boss is credited at its exp ÷ regen hours rather than fought, the way the estimate counts it. Rest and heal thresholds are read against your **Default** gear's pools, as the live client reads them. Type `stat` once after logging in so the client knows your level and pools; the button tells you if it doesn't yet.

**Backstab openers.** With **Settings → Combat → Do BS attacks** on, **Auto-Sneak** on and some Stealth, the simulated character walks sneaked and opens each fight it sneaks into with a surprise **backstab**. The stab uses your Backstab gear set's weapon and the same to-hit and damage Monster Intel's backstab line shows. A stab that kills ends the fight before the monster swings. One that misses or doesn't kill is that round's whole attack, and later rounds are your usual attacks. The simulator assumes your sneak **always holds** from room to room: a see-hidden monster spots it, and anything that breaks a sneak (attacking, casting, resting) ends it. The sneak is taken again, with no wait, as you walk out of a room with nothing alive left in it. A monster's **don't backstab** override and **Don't BS if multi-attack room spell is firing** apply as they do live. **Run if BS fails** and **Hit and Run tactics** aren't simulated.

The run settings beside the route:

- **Walk pace (seconds per room)** — your bare walking pace between rooms. Leave it at **0** and it's worked out for you: on Paradigm, the server's move timer from your quickness and carry weight (never faster than 1.0 s) plus **Lag per move** (default 100 ms — a "1.0" mover really walks 1.08–1.15 s); on Stock, your **Auto-Lair** hop time for your encumbrance (or its flat pace, if you set one). Fighting is simulated separately, so this is *not* the all-in **Seconds per room** the estimate uses. The readout shows the pace it used.
- **Hours per run** and **Runs** — each run rolls different luck; more runs narrow the range.

When a monster's physical attack lands and carries a **hit spell**, the hit spell fires too, at its base values (the game casts it with no level): its damage ("Your life is drained…"), or for a burn its rolled damage every 3 seconds until it wears off, and its effect for its duration — knockdown's AC / Dodge / Accuracy loss, and a hold that keeps you from walking off (or running) until it ends. Another landing of the same spell while it's on you only replaces it with a higher roll, and otherwise does nothing; a different burning spell burns alongside it. The run and hang-up triggers fire at or below their settings whenever a monster is attacking you (one you can't hurt included), as they do live; after running you rest away and walk back once HP and mana are both above their run triggers. A loop opened with **Load loop…** keeps its waypoint commands, whose delays count in their rooms (any edit to the route drops them). Which hit spells burn depends on the realm. On Stock only a plain damage spell with a duration burns; a spell whose damage ignores magic resistance hits once as it lands. On Paradigm a damage spell with a duration burns, like envelops' "You are on fire!". A death spell's summons follow the realm's room limit: on Paradigm they appear only when they all fit under 20 monsters, and on Stock each is placed while the room has fewer than 15. A death spell that targets no one (such as "calls for aid") summons nothing when its monster dies, in the simulator and the estimate alike. The readout adds the damage you took per hour, how often you fled, and any hang-ups. What it doesn't simulate yet: item-cast buffs, the running backstab options, the extra time doors and searches take, poison from a hit spell, and your magic resistance against burns and monster spells. A debuff you land on a monster stays on it for the rest of the fight (it never wears off or gets resisted), and casting it again adds nothing. Results are cleared (and a run still going is stopped) whenever you pick another route, edit the sketch you simulated, change a simulation setting, or switch character or game-data set. Closing the Navigation window stops a run too, and **Cancel** beside the button stops one on demand. The estimate's own knobs leave results alone. The Simulator's last result, check and ranking are included in a bug report.

### Checking the simulator against your own play

**Check against my play** (the Simulator's second tab) answers "how far can I trust a simulated number?". It reads your program logs for every loop you've run an **hour or more at one level**, simulates each of them **at the level you played it**, and lists them in a table: the loop, the level, your hours, your real exp/hr, kills/hr and deaths beside the simulated ones, and the difference in percent. A line above the table counts how many land within 10%. Each loop is simulated with the run settings at the top (pace, **Hours per run**, **Runs**), and changing one clears the check.

It needs program logs, and those are only written while **Program Log (F4) → Auto-collect logs** is on — it's **off by default**, so turn it on and play your loops before the check has anything to read. Logs older than **30 days** are deleted at startup, so the check covers roughly the last month. A loop session ends at its stop, a failure, or a level gained mid-loop (the exp after the train counts toward the new level). It only needs your logs and your saved loops; the route you picked doesn't matter. A loop you've since deleted or renamed is listed as no longer saved.

Another level is simulated by moving **today's** character there: max HP, mana and Spellcasting shift by your class's per-level growth (a Mystic's kai is left as it is now), the level-scaled numbers (accuracy, swings, regen, spell damage) follow, and spells above that level are dropped — but your stats, gear and quest bonuses stay as they are now. So a session from before a gear upgrade or a stat train reads high for reasons the simulator can't see; the rows at your current level are the fair test.

### Ranking hunting areas at a level

**Rank areas at level** (the Simulator's third tab, with a level beside it — 0 means your current level, and follows you as you level) answers "where should I hunt?". It builds a lair tour for **every hunting area you can reach from the room you're standing in at that level** — each lair room goes to the area its monsters are filed under (the **Region / Area** labels on the Game Data Monsters tab; change a monster's area there and the ranking follows), and the tour walks from the area's first lair to the nearest unvisited one (a very large area is walked outward from the start instead) — then plays your character through each at that level and lists them in a table:

- **safe areas first, best exp/hr first**, with kills/hr, your lowest HP and the number of lairs;
- **areas where you died** (or hung up) after them, however high their number — a run that dies only counts the minutes before it did.
- **your own saved loops**, ranked right alongside the areas and marked ★, each simulated at that level too — with what **you actually made** on it from your program logs (your record at that level, and your biggest sample a few levels either side). A loop you've played for hours **at the ranked level or below** without dying counts as safe even if a simulated run died; a record from a higher level is shown but doesn't vouch for it.

Each option gets the Simulator's **Runs** and **Hours per run** settings (runs × hours of simulated play), so a ranking takes longer the higher those are. Mapping the areas and simulating them can take a while on a big map: the status line shows how far it has got, the client stays usable meanwhile, and **Cancel** beside the button stops it (switching character, or closing the Navigation window, stops it too).

**Reach is judged at the chosen level.** A level-gated way in — a `(Level 50+)` exit, a boat or portal with a minimum level — only counts once the ranked level clears it, so ranking below that level leaves the areas behind it out and ranking at or above it brings them in. Reach is judged for you alone: a party's level window isn't considered. Every other gate (doors, keys and items, tolls and fares, class, alignment) and your avoided rooms count as they do for your walks right now. The status line says how many areas were left out as unreachable, and how many were skipped because they have only one lair you can reach or no walkable lap (the same for a saved loop that no longer walks). A few runs is few: an area on the edge (lowest HP in the teens) can land on either side of "died".

Pick a row to see its full result under the table and its route on the map. The route goes on the map as the Exp/Hr Estimator's sketch. If you weren't estimating, the estimator opens for it, unless you're building a loop or a walk, loop or Auto-Lair is running; stop that first. It **replaces whatever you'd sketched**, without asking, so save a sketch you want to keep first. The Simulator's route switches to that sketch, so from there you can trim it and **Save as loop**, or **Simulate** it. Simulate plays it at **your current level and today's gates**, not the level you ranked at.

The detail under the table shows **how the row was tested**: its **Route** (the rooms the run walked between, in order, and how many rooms a lap takes) and **Tested** (the level, runs × hours, walk pace, realm, and the room you ranked from — the settings the ranking ran with, even if you've changed them since). An area row has a **Save as loop** button beside its headline: it saves that exact tour as one of your loops, named after the area and level (for example `Hills - Caves (L40 sim)`, with a number added rather than overwriting a loop of that name), with the simulated result and how it was tested in the loop's **notes**. The new loop appears in the Navigation window's Loops list, ready to run, and becomes the Simulator's **Route**. It works whatever the map is doing, building a loop or running one included. The loop walks between its rooms with your gates as they are when you start it, so run it at the level you ranked; ranked above your level, a level-gated way in may still be shut to you. A ★ row is already one of your loops, so it has no button.

A whole-area tour walks *every* lair in the area, including the rooms a hand-built loop would skip — so a loop you've tuned inside a good area will usually beat its area's number (your loops in the list show it), and an area whose tour dies may still hold a safe corner. Use an area row to find where to look, then build the loop there. Another level moves today's character there, the same way **Check against my play** does.

## Auto-Lair

**Auto-Lair** camps a monster's lair: travel there, wait out the respawn timer, enter to kill the spawn, then repeat. Mark lairs with the **Lair mode** chip (left-click the lair rooms, then **Save**; clicking a marked room again, or **Alt+clicking** it, unmarks it), or build a setup in **Navigation Management → New Lair** (where you can override each lair's respawn timer). Start one from the **LOOPS + AUTO-LAIRS** rail's **Go** button — it cycles the marked lairs. Its routing heuristic and travel-cost model live in **Settings → Auto-Lair**.

**When a lair counts as ready.** Its respawn timer (from the room's **Max Regen** time — the middle of the window on Stock — or your override) runs on **Stock** from the **last kill** in the lair — the clock the game itself restarts on every kill, so a long fight pushes the next visit back by its length — and on **Paradigm** from when you **entered** it. A Stock lair you haven't seen a kill in yet times from your entry. The **CURRENT NAV** countdown for a marked lair follows the same clock.

**Where it waits.** It waits out the timer one step short of the lair: in the last room on the route it will walk in by. That is the route your character can actually take, so a door you can't open, or could only pick at poor odds (see **Doors** under *The map and obstacles*), isn't the step in, and the wait room is on the way round it. (If that room is itself a marked lair, it waits in the nearest room before it on that same route that isn't one.)

**A lair it can't get into.** If the walk in fails at a door that beats you, Auto-Lair doesn't stand there. A door you can't open, or could only pick at under 25% a try, is given up for the rest of the run: it goes round it where there is a way, and otherwise leaves that lair out and carries on with the others. A door the picks ran out on at fair odds (25% or better) was bad luck: it is tried again on the lair's next visit. With no marked lair in reach at all, the Auto-Lair status line says so and it waits for that to change. Any other failed walk in is tried again, waiting twice as long after each failure at the same lair (2 seconds, then 4, up to 60).

**How long it stays in a lair.** It leaves for the next one as soon as the fight is over *and* the drops are picked up — it won't walk off and abandon loot it just fought for.

"Fight over" means the room re-displays with no monster you'd engage left in it, which is the reliable signal; the game's own `*Combat Off*` line isn't usable on its own, since it also fires every time you cast and once per strike for thrown weapons and the like. **Engage timeout** (Settings → Auto-Lair, default 30s) is only the upper bound, for a fight that never resolves — something you can't kill, or one that ran away.

**A lair that hasn't respawned costs a few seconds, not the full timeout.** If nothing turns up within a moment of stepping in, Auto-Lair takes that as "not back yet" and moves on to the next lair rather than standing in an empty room. If it still looks like it's idling, the usual reason is that the monsters you're after don't actually *spawn* in that room: some wander in from elsewhere on their own schedule, and a room they merely pass through isn't a lair Auto-Lair can time.

## Fighting in a dark room

A room too dark to see in prints no name, no exits and no **Also here:**, so
the usual way of learning what shares the room with you is gone. Auto-combat
falls back on what still reaches you — the attack itself:

- **something damages you** — `... you for N damage!`
- **something misses you** — `The <monster> swings at you!`, acted on the
  first time it's seen
- **a monster with a proper name swings at you twice in the same round**:
  - with no leading `The`, one swing isn't enough, because an emote such as
    `The barmaid smiles at you.` has the same shape
  - a monster names itself on every swing, so a repeat is the tell
- **a party member announces an attack** — `<player> moves to attack <mob>.`

The attacker's name is read off the line and matched against your game data,
taking the longest match — so `The bugbear captain all-out cleaves you for 20
damage!` resolves to **bugbear captain**, not `bugbear`. A leading `The` is
optional, since a monster with a proper name is printed without one.

What it finds is added to the room list, so the fight starts the ordinary way:
by name, honouring **Relationship**, **Attack Priority** and **Target Order**.
Two consequences worth knowing:

- a monster your game data doesn't have is **not** attacked. There is nothing
  to name, and guessing would mean swinging at whatever the room holds.
- something set to **Neutral** or **Friendly** is still left alone, even if it
  is the thing hitting you.

A monster found this way is taken off the list again when the server refuses
the attack:
- `Your command had no effect.`
- `You don't see <monster> here!` or `You do not see <monster> here!`

That covers a monster that died unseen or left, so whatever is actually
attacking you can be found next. A refusal naming a party member (a cast at
someone hiding) or an item doesn't count.

This only runs while the room is dark. In a lit room **Also here:** is
authoritative and is used instead.

**A monster that summons help mid-fight.** Some monsters cast a summon between
rounds. The half-orc sentry's `The fat half-orc sentry shouts for aid!` brings in
an orc warrior. The new monster arrives with no line of its own, so MudPlay
re-displays the room (a bare Enter) as soon as it reads the summon line. The
summoned monster joins the room list and gets fought next, so killing the
summoner no longer ends the fight while its helper is still swinging at you. The
summon wordings come from the Spells table and the message catalogue, so an
edited or new summon message is picked up too. Nothing is sent while the combat
engine is off or the room is dark, and at most one re-display goes out every
few seconds.

## The map and obstacles

**Right-click any room** for its menu: **Favorites** and **Recent destinations** sub-lists at the top (the Favorites list holds your starred GOTO rooms *and* your favourited loops + auto-lairs — click a room to walk there, a loop or lair to start it — and Recent destinations walks to a recent GOTO target), then **Walk here**, **Transfer Stash to Bank** (on a stash room only — see [Banking](#banking)), **I am here** (re-anchor if the map loses track of you), **Save as Go To** (saves the room to your Go To list), **Use Teleport**, **Center on Player**, **Center on this room** (redraws the map from that room as if you stood there: its floor bright, the floors above and below shadowed around it), **Center on Destination** (only while a walk is under way — jumps the view to where the walk ends: the walk-to target, or the loop's start room / the next lair when a loop or Auto-Lair is walking there first), **Center on…**, and toggles to mark a room **Avoid** or **Stash**. Like a manual pan, a re-centre holds the view for a while before it follows you again — 15 seconds by default, set in **Settings → Other → Navigation map: hold a browsed view for N seconds**.

**Shift+right-click** skips the menu when a room's only jump is unambiguous — a room with just an up exit, just a down exit, or a single teleport destination immediately follows it (recentres the map there) instead of opening the menu.

**Left-click any room** to load it into the **ROOM INFO** rail panel. A room also lands here when you click a monster's **lair / placed / summoned** room chip in its Game Data record, or double-click a row on the **Rooms** browser tab — in those cases the map opens (if it was closed), centres on the room, selects it, and expands ROOM INFO. A plain map left-click never forces the panel open; it just refreshes its contents to the room you clicked, so expand ROOM INFO whenever you like and it shows the last room clicked.

The panel lists clickable links to everything attached to the room:

- **Room name** — click to open the room's record (or, for a shop room, its shop stock popup), with the map/room number and illumination beneath it.
- **Illumination** — **`Room Illu:`** shows the room's own light. If you carry any light — worn +illu gear, a readied light, or a light spell in the Buff Watchdog — a **`Your Illu:`** line appears with your effective value, and the visibility phrase moves onto it. The phrase reads the room's state — *pitch black*, *very dark*, *barely visible*, *dimly lit* — or **"You can see."** once fully lit.
- **Monsters** — grouped (like the map tooltip) into **Placed** (a boss / NPC fixture), **Assigned** (roams there / rarely spawns), and **Lair** (consistent lair spawners, with the lair's **Max Regen** beneath — how many it spawns and how long it takes to come back — for a `Delay` of 5, `5-6m` on Stock, counted from the room's last kill, or `4m 30s` on Paradigm). A monster can appear in more than one group.
- **Obvious exits** — click one to re-root the map on that neighbour. An exit that needs something shows what: a door's skill, a key, a toll, the one class or race it lets through (*Druid only*, *Gaunt One only*), or the command that opens it. That command can be one you type in the room (`clear rubble`) or one you ask an NPC standing there (`ask stone sphinx e` for the way up out of the Great Pyramid's top room). A long requirement goes on its own line under the exit.
- **Floor items** — everything the room drops on the ground (static placements plus anything its `roomitem` command scatters).
- **Shop and room spell** — when the room hosts a shop, and its cast-on-enter room spell.
- **NPC transports** — teleports a monster standing in the room offers when you ask it a keyword, e.g. `ask Seher'Sahham activate → Damp Cavern, Wellspring (16/637) — 1 runic`. These live on the monster, not the room, so they're listed apart from Room commands. Click one to re-root the map on the destination. The walker routes through a paid transport only when **everyone** can pay: each person who asks is charged the fare, so in a party it checks the poorest member's cash (the same `@wealth` check a toll uses) and walks around it otherwise.

**Room commands** lists what you can type in that room and what it does — `touch statue / move statue — summons obsidian statue`, `give crane totem — teaches form of the crane`, `pull lever — drops frozen hydra in the room`, `hand over totem — takes crane totem`, `break apparatus — grants an ability`, `pray — casts minor healing` — with the cost appended when the command charges (`summon healer — summons healer — costs 100 Gold`). Synonyms that do the same thing share one row. A command that does several things reads by the most consequential one (a monster it summons ahead of a spell it casts), and a command that casts a spell which moves you is listed as the teleport it is. It carries the same lines as the map tooltip's Room commands — teleports and sailings included (a captain's `secure passage` lists each port it sails to); click a teleport or sailing line to re-root the map on its destination. Long rows wrap to the panel width.

**Everything is clickable.** A **monster** or the **room spell** opens its full record in a dialog; the **shop** (and a shop room's name) opens the shop stock popup with buy/sell prices and the live Charm picker; a **room-command** row opens the record its effect names (the monster it summons, the spell it teaches, the item it drops or takes); and floor-item links open that record in the **Game Data Browser**. Either way it's a quick jump from "what's in this room" to the full record without hunting through the browser's tables.

**The illumination scale.** A room's illumination is a signed number — **0 is fully lit**, and the more negative it gets the darker the room. `Your Illu` folds your carried light (worn +illu gear, a readied light, and any configured light spells) into the room's own value, so it's the figure that decides what *you* actually see. Where a value lands, and the phrase it shows:

- **0 or higher** — *You can see.*
- **-1 to -100** — *The room is dimly lit*
- **-101 to -150** — *The room is barely visible*
- **-151 to -200** — *The room is very dark — you can't see anything*
- **-201 or lower** — *The room is pitch black*

**-150 is the cut-off**: at -150 or above you can make out a room's contents; below it (very dark / pitch black) the game hides them, so you need enough carried light to lift `Your Illu` to -150 or better.

**`@where` on the map.** When you `@where` another MudPlay user and their client answers with its location (a telepath like `Fujin telepaths: {Adventurer's Guild, Universal Trainer (map 1, room 1376); exits: west}`), the map — if it's open — **flashes that room green and centres on it** for about 15 seconds, then drifts back to following you.

`@where` several people and **each answered square lights up at once**, fading out on its own 15-second timer; the map re-centres on the **newest** reply as it lands, leaving the earlier flashes where they are. It only reacts while the Navigation window is open; a reply that lands with the map closed is ignored.

**`@path` on the map.** Ask your party leader (or any MudPlay user) `@path` and their reply — `{walking to 6/1249; Rocky Path, Valley View (map 9, room 747); step 94/166}` — is drawn as **their route** while the Navigation window is open. Nothing extra is sent: MudPlay plans from the room they're in to where they're going and shows it where your own walk would show — the map line, the **CURRENT NAV** steps, the status line at the top, and **Details…** — all in **cyan**, with a cyan **FOLLOWING** badge in place of WALKING / LOOPING, so a route you're watching never reads as one you're driving. (The line's colour and thickness are the **Following line** under Settings → General, with the other nav lines.) It works whichever way the reply comes back (telepath, gangpath, say or a directed say), and it flashes the room they're standing in like `@where`.
- **Matching their steps.** Your character isn't theirs — they may carry a key you don't, be above a level gate you're below, allow teleports, or skip rooms you avoid — so MudPlay tries each of those planning choices and keeps the route whose step count matches the steps they have left. The status line names the choice when it isn't your usual route (e.g. *route with teleports*); if nothing matches it draws the closest and says so (*closest route we can plan is 70 steps … vs their 73*).
- **A loop** is drawn if you have a loop with the same name; otherwise the status line says you don't have it. Auto-lair and a boat leg have no destination in the reply, so there's nothing to draw beyond the room flash.
- **An `@goto` your party leader accepts** is drawn the same way. Their reply (`{walking to Grassy Cart Path, Dead End (1/2447)}`) names only where they're going, so MudPlay plans your usual route from the room you're in — you're following them, so you set out together. Only your party leader's reply counts.
- **While you follow**, a walk-to shortens behind you and the CURRENT NAV rows tick off; it clears when you arrive, when a newer `@path` or `@goto` reply lands, after 10 minutes without progress, or with **Clear** in the CURRENT NAV header. Start a walk or loop of your own and it takes those surfaces back.

The **Overlays ▾** button layers lairs, shops, spell rooms, **level gates** and a running loop's **loop lines** onto the map and toggles the **Legend** — which you can **drag anywhere on the map** (it remembers where you put it; toggle it off and back on and it snaps back into view if the window has since shrunk).

The **Spells** chip under it cycles how **spell rooms** are painted — rooms that cast a spell on you while you stand in them: one flat purple → **by name** (a colour per spell; hover a room for the spell's name) → **by teleport** → off. **By teleport** colours each spell room by whether its spell moves you:

- **Red** — the spell teleports you and you can't tell beforehand: either nothing stands in its way, or a random roll decides it (the desert's sandstorm, the open sea). A roll makes it red even when a condition applies as well.
- **Yellow** — the spell teleports you only on a condition, with no luck in it: an item you carry or lack (the ice slide without a rope and grapple), your class, level or alignment, a quest you have or haven't done, or the room being empty of monsters. The map paints a spell the same for every character, so it doesn't work out whether the condition holds for yours.
- **Green** — the spell has no teleport.

A spell the game data doesn't let MudPlay read to the end stays the flat purple instead of being called green, and the program log names it when the game data loads.

In that mode the hover tooltip's **Room Spell** line ends in *(teleports: always, or at random)*, *(teleports on a condition)*, *(no teleport)* or *(teleport unknown)*, and the Legend lists the colours in place of its one **Spell room** swatch. A spell's resist roll isn't counted as a roll. A spell room that is also a lair or a shop keeps its lair or shop colour while that overlay is on — switch it off to see the spell colour. With the **Lairs** chip on its heat-map, the lair colours run red through yellow to green as well, so the two overlays share a palette: a lair is never painted by its spell, and the tooltip tells which a room is (a lair's has a **Lair** line; the **Room Spell** line carries the teleport note). The chip's setting is saved per character.

The **Legend** keys every room-cell marker the map draws: the amber-ringed **current room**, the blue-ringed **walk-to destination**, room fills (lair, shop/bank, spell, auto-lair, up/down/up+down exit rooms), and the overlay glyphs — **deathpile** skull, **boss** crown (with a red halt ring when it's a *stop-before* boss), **trainer** chevrons, **gang-house** robot, **avoid** (red X), **stash** (gold X), the amber **level-gate** wedge, and the fading green **@where** result.

**Route lines** are colour-coded — walk-to **blue**, a running loop **green**, a loop you're previewing **red**, an Auto-Lair approach **orange** (these four are recolourable under Settings → General, so they're described here rather than pinned in the Legend).

**Exit stubs** carry their own colours (shown in the Legend):

- **red** — a trapped exit;
- **magenta** "Action required" — an exit you can't just walk, one that needs a command or in-room action to cross (a `go path`-style named exit, a lever, or an ask-a-guard door);
- **cyan** — a hidden exit revealed with `sea`.

Traps are **directional**, so a connecting line is only red on the trapped side: a line red for its **whole** length is trapped **both** ways, while one red for **half** its length (the half against the room whose exit is trapped) is a **one-way** trap — safe to walk back the other direction.

**Level gates** (on by default) marks every room that **holds a level gate** with a small **amber wedge in the top-left corner** — a room you can walk into and stand in, whose way onward is shut unless you're inside the gate's level window. It covers gated exits, **level-gated room teleports** (a vortex that won't take you until level 20), and **level-restricted boat sailings** (a captain who won't board you until level 50). That's deliberately a different mark from the **red exit stubs**, which mean a trap: a level gate is a locked door, not a hazard.

It describes the **map**, not your character — it marks where the gates *are*, not which ones happen to refuse you today. So it reads the same at level 5, at level 99, and while you're browsing game data with nothing connected, which is when "where are the gates?" is most often the question. Hover a marked room to see the gate's actual level window in the tooltip. Your choice is saved per character.

**Loop lines** sets how a running loop is drawn. Click it to cycle:

- **Loop lines** (the default) — the loop's line with a numbered circle on each step, matching the CURRENT NAV rows.
- **Loop lines: no steps** — the line alone: one unbroken route through the whole loop, with no circles over it.
- **Loop lines: off** — the running loop isn't drawn. The red loop preview shown while you walk to a loop's start goes with it; the walk-to line itself stays.

A loop you're **building** always shows its line and numbered steps, and a saved loop you **Preview** from the Loops list is drawn whatever this is set to. The choice is saved per character.

**Other floors** (all floors by default) draws the floors you reach by **up and down exits**, dimmed, around the floor the map shows. Each one sits where the game puts it, straight above or below the stairs that lead to it, so a mountain path that climbs a floor at a time or a dungeon that drops level by level reads as **one path**: you can see where it goes and walk straight to the end instead of stepping the map floor by floor. Shadowed rooms work like any other: hover for the tooltip, click to select, **Walk here** to go.

- **Click the chip to cycle** all floors → **up** (only the floors above the one shown) → **down** (only the floors below) → off. The chip's label names the current choice. A floor level with the one shown, reached by going up and back down somewhere else, is drawn under both up and down. Applies to every character.
- **The floor shown always wins.** Shadows only fill cells it leaves empty. Where two other floors want the same cell, it's a view from above: the **higher** floor is drawn and the deeper one is covered. Going up, the floor two up covers the floor one up; going down, the floor one down covers the floor two down; and anything above covers anything below, so a cave under a trail never covers the trail.
- **A floor reached two ways sits by the straighter one.** The game's geography doesn't add up: two chains of stairs to the same place rarely agree on where it is. Every floor walked across on the way adds its own bends, so a floor is placed by the chain that crosses the least ground on the floors between — a shaft of single rooms straight down counts for nothing however deep it is, a route across a reef and an underground lake counts their width. That is why the old world sits beside the lands under the Frozen Cavern's shaft rather than on top of them. It only changes *where* a floor is drawn; which floors are drawn, and how many steps up or down each counts as, still go by the fewest stairs.
- **Crowded floors are left out.** A floor that would land mostly on rooms already drawn (a volcano under the hills, barracks under a trail) is a different place stacked on this one, so it isn't drawn. A floor with three or fewer rooms covered is always drawn, so a climb's small landings aren't lost.
- **How far it reaches** — how many floors up and down, and how crowded a floor may be before it's left out — is set in **Settings → General → Navigation map: other floors**.

**Zoom out** far enough (mouse wheel) and rooms shrink to dots, small enough to take in a whole region and its shadowed floors at once. At that size the room markers (crowns, skulls, X's, wedges) and one-way arrowheads are left off; they come back as you zoom in. While the wheel is turning the map is stretched from the last drawing, so it can look soft for a moment; it sharpens as soon as the wheel rests.

Hovering a room shows its details in a tooltip:

- **Monsters** — split into **Placed** (a boss / NPC fixture), **Assigned** (roams / rarely spawns there), and **Lair** (a consistent lair spawner), each with its game-data record number (e.g. `Dark Goblin Archer(#48)`). The lair's **Max Regen** sits directly beneath the Lair line.
- **Floor items, shop / room spell, exits, and lighting** — everything else attached to the room.
- **NPC transports** — the ask-a-keyword teleports of a monster placed there, with destination and any price.
- **Room commands** — anything you can type there: teleports and paid services, and the commands that act on the room itself — what they **summon**, the spell they **teach**, the ability they **grant**, and the item they **drop** or **take**.

(A locked door whose key id doesn't match any item in the set — a game-data typo, e.g. 8/462's north gate recording `Key: 1` — is shown as the plain door it behaves like, listing the picklocks/strength that actually opens it, rather than naming a key that doesn't exist.)

**Getting past obstacles.** En route, MudPlay clears most of what stands between you and a destination, stopping only when it hits something it genuinely can't solve:

- **Doors** — closed or locked, handled by key, pick, or bash. If the game won't let you bash at all (no weapon in hand, or no bash skill) it tries picking, then the key, instead. It follows doors other people open and close, or that lock again by themselves, in the room you're standing in, so it opens a door that's been shut on you before walking into it. If the game answers a bash or a pick with *Your command had no effect.*, or an open with *That is not a door or a gate!* (there's no door that way where you're standing), or the map shows you've ended up in another room while the door was being worked on, it stops working the door, re-checks where you are and re-routes from there instead of trying the same door again. A pause that comes and goes mid-door (a party `@wait` then `@ok`, a short fight) doesn't restart the door either: the walk carries on from the bash it already sent. Once a door is open it steps through like any other move: bashing or opening a door ends a sneak, so with auto-sneak on it re-sneaks first, and a monster that walks in meanwhile is dealt with before it leaves.
  - **A door you can't open is never on a route.** Once your stat screen has been read, a door whose Strength you don't have and whose lock your Picklocks doesn't meet is closed to every plan, so the route goes round it when there is a way round. Until a stat screen is read nothing is ruled out, and the door is tried when the walk reaches it.
  - **A lock you could only pick at poor odds is gone round when that is short.** Where picking is your only way through and each try has under a **25%** chance, the route takes a way round that is at most **10 steps** longer; with no such way round it goes to the door and picks. The same on Stock and Paradigm. The map's door hint shows your chance.
  - **A door that beats you is given up on for that trip.** When the picks run out (*Door max pick*) and bashing isn't open to you, the walk no longer stops there: it re-routes round that door and doesn't plan through it again until the next walk. A loop does the same for the rest of its run. Auto-Lair does the same for the rest of its run for a door you can't open or could only pick at poor odds; a door it lost at fair odds is tried again on the lair's next visit. Only when there is no other way does the walk or loop end, naming the door. A key tried as the last resort after the picks, and not in your pack, counts the same way. A key the walk went to fetch that turns out missing or wrong still stops the walk as before.
- **Traps** — disarmed before you step through, on a walk-to, a loop or an Auto-Lair run alike, or delegated to a capable party member when you can't disarm. With *Utilize self or party members to disarm traps* off, nobody able, or nobody in the party taking the trap when asked, it walks through. A disarm ends a sneak, so with auto-sneak on it re-sneaks before crossing. A failed disarm stops the walk or loop rather than walking into a trap that's there.
- **Hidden exits** — searched out and revealed. The game won't search while you're blind (`sea` just answers *You are blind.*), so the walker waits there and searches once you can see again.
- **NPC ask-transport** — a sealed room whose only way out is asking a resident NPC to port you elsewhere (the Floating Citadel's Grey Lord ports you to Town Square). It sends the `ask <npc> <keyword>` for you, so those pockets aren't dead-ends. A **class-restricted** one (the barmaid's bard-only jump) is offered only to the right class; everyone else is routed around it. And because some are a **skill roll** that can quietly fail, the walker confirms it actually arrived and **re-asks until it does**.
- **Action-gated exits** — a lever or switch in *another* room (the magenta "Action required" stubs). If the exit is already open where you stand — someone else pulled the levers, or the game says *The exit to the west just opened!* — it simply walks through. Otherwise it drives a go-pull-return detour, visiting each lever room on the way past then crossing the primed exit — even when a lever alcove is itself behind another action-gated door (it opens each inner door first). When a lever room on your route can't be walked back to, the detour runs one-way instead: it leaves the route there, works through the remaining lever rooms and comes out at the exit. Long detours are fine — the two-lever gate on the way to the new master assassin is a ~230-step round trip. Only a very deep (4+ levels) or self-referential puzzle, or a single detour over 400 steps, is left unsolved: those fail cleanly at plan time (*"route needs an action-gated exit the walker can't auto-solve"*) and log the exit that stopped it. When the way to a lever room crosses a gate you could get past (a room hazard you carry no counter for, an item or key gate), a walk you start yourself opens the **route picker** with that walk as the only route, exactly as it does for a gate on the route itself: obtain the counter and cross, or cross unprotected where the hazard is survivable. Pick one and the walk runs the whole detour, levers included; **Details…** lists every step of it. Where the picker has nothing to offer (a door you can't open, a level gate) or isn't in play (a loop, a party `@goto`), the walk fails and names the exit, the lever room and what's in the way.
- **Room-command reveals** — a hidden passage opened by typing a command *in the room itself* (e.g. `clear rubble`), sent before stepping through. Some of these roll against one of your stats and can miss; when the step after it finds the way still shut, the command is sent again (up to ten more times for that step, however often the walk re-plans) before the step counts as blocked. The bug report shows how many of those tries the current step has used.
- **Room teleports with a party** — most room and NPC teleports move only the person who uses them and drop their followers, so a leader first relays the command to the party (`.@party <command>`), takes it, and then re-invites everyone on the other side, holding the walk until they've all rejoined. It re-invites each member only once they appear beside it after the jump, so a teleport that waits a few seconds before moving anyone (Darkwood's vortex) doesn't fool it into regrouping in the room it's leaving. A teleport that's a spell aimed at the whole party moves everyone together, so the leader just uses it — no relay, no re-invite.
- **Exits that teleport you on** — a few exits cast a spell on whoever steps through that sends them somewhere else, and where can depend on an item: in the Earthen Catacombs, the passage toward the Lost City puts you in the part that leads there only if you have the **golden idol**; without it you come out on a look-alike side that only leads back round. So the idol is treated as that exit's gate item: a walk-to that needs the passage tells you it requires the golden idol (and offers the route on the route card like any other item gate) instead of walking you through to the wrong side, and a loop through the passage won't run without it. A walk that set out to fetch the idol on the way and reaches the passage still without it stops there, naming the idol, instead of stepping through. The game shows you two rooms on the way: the one the exit leads to, where you are for an instant, and then the room you land in. Routes, loops and the map still go by the first; your position is put in the room you land in as soon as the first is shown, so a walk or loop steps on without waiting, and nothing is picked up, attacked or invited from what the first listed. What is in the room you landed in is read from the second. Only when that second room hasn't been shown after a moment, and you are still standing there, does MudPlay send one Enter to have it shown (the same when no room is shown at all, which can happen to a follower pulled through). Which landing it is follows what you carry (worn and key-ring items count); if the room shown afterwards fits only the other side, your position is moved there, and where the two sides look alike and nothing is walking you, Paradigm is asked with `rm`. Everyone is sent on by themselves, so the step drops a party apart: once a leader's walk or loop has gone through, it waits in the landing room, re-invites each member as they're listed there, and only then goes on. Nothing is relayed — the followers are pulled through behind the leader. The members it drops aren't gone back for.
- **Item-use teleports** — where *using* an item transports you across (e.g. `use potion of levitation`); it uses the item for you.
- **Winch gates** — a fortress-style gate opened by pulling a winch: it pulls (re-pulling if it "does not budge") and waits for the gate to turn fully open before stepping through, so it never walks into a still-closed gate.

**Obstacles wait for a rest.** If you start resting or meditating while a door, hidden exit, trap or winch is being worked on, the next bash, pick, search, disarm or pull waits until the rest is over. Those commands stand you up, so sending one mid-rest would only break the rest and start it again. For a trap that just went off, it also means the retry happens after you've healed rather than on what's left. If the obstacle clears while the loop is held (someone opens the door, or your last try lands just as the rest starts), the loop simply takes the step when it resumes.

When a route is blocked *only* because you lack a required item for one of these gates — often a quest item that can't be auto-fetched — the walk fails with a message that **names the item to go obtain**, rather than a bare "no path". A **hidden exit whose opener needs a held item** counts the same way: the picker names that item up front (the Lower Caverns bloodstone orb, the fortress amber talisman), and if the item is flagged **Auto-obtain for path** the run fetches it before setting out.

The picker also distinguishes a **genuinely-required** gate (no way to the destination without it) from one that merely unlocks an **optional shortcut** (a longer route reaches the destination anyway). It commits the reliable route, lists only the required items — noting any you **already carry** ("— you have it"; an item another player has just handed you counts at once, with no `i` needed) — and surfaces the shortcut separately with the rooms it would save, **without** ever fetching the shortcut item for you. The walk that card starts is the route it shows: it goes round the shortcut's exit for the whole trip, unless you come to be carrying the shortcut item. The whole trip includes a re-plan, a detour to fetch an item or buy a light, and the walk picking up again after a sell trip, a flee, going back for a party member, or an event's **Resume**. Pressing **Stop** while the walk is standing at a giver or a shop between two legs ends it there; nothing walks on afterwards. A locked door you can pick or bash isn't a gate at all, so its key is never listed.

**Leading a party past an item gate.** An exit that needs an item (a darkwood ring for the stone arches, a rope and grapple) takes across only the people holding one, so a party of three needs three. When you lead a party and the way there crosses such a gate that *you* can pass, MudPlay first asks the party how many each member holds (a few seconds, while the picker says it's calculating). If there's one each, nothing changes. If the party is short, the gate counts as closed for planning: the route card names the item and how many the party holds ("darkwood ring (one each for your party of 3; 2 held)"), and you choose between a route that avoids the gate, getting the missing copies on the way (they're handed out once there are enough), or crossing as you are. A member who doesn't answer (not running MudPlay, or remote commands off) counts as holding none, on the card and on the walk alike: a copy is fetched for them too, and you hand it to them yourself (or a member who did answer and has a spare is told to). If that give doesn't go through you keep the copy, and it is given again the next time the party is asked. **A copy you handed over yourself is remembered.** When the game confirms your give, MudPlay notes that the member holds it, and while they stay silent they count as holding what you handed them, so a later trip across the gate fetches nothing more for them. The cost: a copy they have since lost is not replaced, because nothing tells MudPlay it is gone. That mends itself at the gate: it refuses them and they drop out of your party there. When the game tells you they stopped following, as Paradigm does, what was remembered for them goes with them, and, with **Re-invite lost party members** on, you go back for them. When it tells you nothing, MudPlay finds out for itself: once you are through a gate with a member it is crediting this way, it sends one `par`, and a member the reply no longer lists as following is forgotten (a member `par` lists as following is in your room). That `par` goes out with Auto-Heal off and with every `par` box on Settings → Party unticked, and not with Auto-All off. Either way the next trip fetches them a copy, so the cost is one split party, not a member left behind on every trip. It is forgotten when that member leaves the party, the party disbands, you stop leading it, you disconnect or load another character, or you press **Reset States** (the way to have a copy fetched and handed over again). A member who answers is always taken at their word, whatever was remembered. An item with a limited number of uses (a room ticket) is remembered only for the trip it was handed over on, and no longer than your own crossing of an exit that needs it, since it may be used up. A give that a member with a spare was told to make is not remembered, as its confirmation goes to them and not to you: a silent member given a copy that way still counts as holding none the next time. Someone invited who hasn't joined yet isn't counted at all. The count is dropped when the party changes and taken again for the next walk you start. It only applies to walks you start yourself. **A walk that may be sent to a shop waits for the party first.** A walk in a party that was told to fetch an item on its way (the copies a route card said it would get, or an item ticked **Auto-obtain for path** that is fetched without a card) asks the party who holds it as it starts, so it knows how many to fetch and who gets them. It stands where it is until the answers are in, since they may send it to a shop or a giver first, and a walk already headed for the gate would have to turn round. With everyone answering that is a moment. **A member who doesn't answer (not running MudPlay, or remote commands off) costs the full four seconds each time the walk waits**: once at the start, and once more after a fetch for as long as something else is still to be fetched. Someone invited who hasn't joined yet is neither asked nor waited for. The Navigation bar reads *Asking who holds <item>* meanwhile. A count a route card took within the last two minutes is used as it stands by the first walk that asks about that item, which is normally the walk the card starts, so the same question isn't put twice. Closing the card without picking drops it. After that the answer holds for the trip's later legs and its re-plans, for up to two minutes, unless your own copies of the item change or copies are handed out; then the next leg asks again. A trip picked up again after a sell trip, a flee or going back for a party member also asks once more. **A leader carrying spare copies waits too**, on any walk: the answer is what hands them out, and that has to happen before the walk reaches the gate, which takes across only those holding one. Otherwise a walk that fetches nothing is not held: a bank run, a sell trip, a trainer trip or Auto-Lair that crosses such a gate walks on, and any count it takes runs meanwhile. A PvP flee asks nothing at all. An item only a monster drops is hunted after a question to you, and the walk goes on while that question is open.

That shortcut is its own **selectable card**, with its own **Requires** line listing everything its route needs. That is usually more than the shortcut item, since the shortcut tends to rejoin the long route before the gates both must cross (*"Requires amber talisman; bloodstone orb"*). It names the items only: this card fetches nothing but the shortcut item, so bring the rest yourself or take the main card, which says where each one comes from. Pick it and — if you're not carrying the item — the walker heads to the item's **source** (a dropping monster's lair, a shop, a giver), tries to get it, then takes the shortcut if it turned up or falls back to the long reliable route if the source was dead or empty. The fall-back is the main card's route, taken the way that card showed it (the same teleport, round the same gates). So a shortcut whose item comes off a monster that may be gone is one click, not a manual side-trip.

**Door keys are a special case.** Normally a locked door's key is *not* something MudPlay goes looking for — pick and bash are the usual openers, so a key you simply don't have makes the exit fail in place. The exception is a key whose whole acquisition chain is certain: a **room command that summons a monster which drops it every time**, an **NPC who hands it over when asked**, or a **shop that sells it** (the Thieves' Guild's skeleton key). A key an NPC only **trades** for is the one case that needs your say-so on a route card (below). For a summon, the run detours to the summoning room, types the command, lets the fight resolve, re-surveys the floor, collects the key, and carries on to where you were going — no prompt, because nothing about it is a gamble. For an NPC, it walks to them, asks, and carries on. For a shop, the route card names the shop, and picking that route buys the key on the way. This only applies to a door you can neither pick nor bash; one you can open yourself never sends you for its key.

- **The gate key behind the Black Steel Gate** works this way — `touch statue` summons the obsidian statue, which always drops it.
- **The jagged bone key for the Library in Arlysia** comes from the old hermit on the Library Steps, who hands another one over (`ask hermit remind`) to anyone far enough along their fifth alignment quest. The key crumbles when used, so one is fetched each time the door needs it. MudPlay doesn't work out your quest step first: it asks, and if he hands nothing over the walk carries on to the door anyway — it may be standing open — and stops there, naming the key, if it isn't. He isn't asked twice on one trip. The key ships ticked **Auto-obtain for path**; untick it in Game Data → Items to stop the detour.
- **A key an NPC trades for another item** is fetched by trade only when you pick a route card that says so. The glowing key for the dark-elf archmage's tower is the example: the sleazy shopkeeper in the dark-elf city hands one over for the opal brooch the captain of the guard drops. With the brooch in your pack the route card reads *glowing key (ask sleazy shopkeeper, in trade for your opal brooch)*; picking that route walks to him, makes the trade and carries on to the tower. The same goes for the dark blue orb the archmage drops, which the shopkeeper trades for the adamantite key to the queen's door and the moldy key to the castle moat.
  - **A trade spends your item, so it is never made unasked.** An item ticked **Auto-obtain for path** is not traded for on a walk that shows no card, and a loop's approach never trades; those routes come up as a card instead. Picking **Send it** on the card makes no trade.
  - **It is the last resort.** A key that is also handed over free, sold in a shop or dropped by a summoned monster is fetched that way, and the trade isn't offered.
  - **The trip to the trader comes first.** Picking the card sets off for the trader with the first step, in a party too: the party isn't asked who holds the key first, since you have already chosen where it comes from.
  - **The item must be in your pack.** MudPlay never offers one you are wearing: take it off first if you want the walk to trade it. The game itself is not so careful: if you type the `ask` yourself it takes the item worn or not (seen in the Stock game; not confirmed on Paradigm).
  - **The agreement is for that one walk.** It covers the key and the item the card named and nothing else. It stays with the walk through its side trips and through an errand that interrupts it and hands it back (buying a light, a sell detour), and it ends with the walk, however the walk ends: arrived, stopped, failed, replaced by another walk, or your death. A dropped connection ends it too, even if the walk itself carries on after you reconnect. A later walk through the same door trades nothing unless you pick a card for it again.
  - **Without the item** nothing is fetched: the card names the trade and where the item comes from (*sleazy shopkeeper trades one for opal brooch, which captain of the guard drops*), and the kill is yours to make.
  - **Only a plain swap of one item for a door key counts.** A trade that takes several items, or that also depends on a quest step, your class or your level, is left to you, and so is a trade for anything that isn't a door key.
  - If he doesn't make the trade, the walk waits a few seconds, goes on to the door and stops there, naming the key. He isn't asked twice on one trip.
- **A low-percentage lair drop** — the black star key, for instance — is deliberately left alone: there's no way to promise it, so the walk won't commit you to an open-ended hunt for one.
- **A key you can pick or bash** is left alone too: the detour only arms when the door is genuinely shut to you.

A path item — key or otherwise — is fetched for you when you **consent to it for that walk**, which is what **accepting a gated route in the route picker** does: the pick itself arms the shop / give / drop acquisition for that one trip (an item flagged **Auto-obtain for path** in Game Data → Items is also fetched automatically on a sole route). Each requirement on the card says what **that pick** will do about it — *ask …*, *buy at …*, *dropped by …* — whether or not the item is ticked Auto-obtain, and a card whose pick only walks somewhere and stops (to a hazard's edge, to a shop you can't pay at) names no source. **Starting a loop by hand** works the same way: when none of the loop's rooms can be reached as things stand, the approach is planned through the gate and a flagged item an NPC hands over is fetched on the way in, instead of the start failing with "no reachable waypoint". While that fetch detour is running the map route line and **Details…** show the **whole journey** — to the shop / giver / summon room, then on to where you actually asked to go — rather than stopping at the fetch stop.

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

- **"Obtain, then cross"** — offered when the client can source the counter nearby; it fetches the raft / feather / waterskin, then crosses safely. **Any counter the hazard accepts counts:** the card names the one it would fetch (usually the cheapest) and lists the alternatives, so a river it would buy a log raft for is just as crossable with a wooden skiff, silverbark canoe or river punt already in your pack — and one picked up or handed to you on the way cancels the purchase. Crystal Lake is the exception: no boat makes its teleport rooms safe, so none is offered for it (see *Only moves you type go into Crystal Lake's teleport rooms* below).
- **"Cross unprotected — take the damage"** — always offered for a **survivable-damage** hazard (a river, heat): walk straight through and eat the hit, on your say-so. A hazard that would *kill or displace* you (a drowning / freezing death, a forced teleport) never offers this — a counter is the only safe way past, so the picker only offers to obtain it or stops you at the edge.

**Routes never enter a room nothing protects from.** A hazard room you can be protected from is crossed with its counter, as before: a route whose only way there needs a door key takes the shortest way and asks for the counter beside the key, however many steps going round would add. Rooms that nothing protects from (Crystal Lake's teleport rooms, below) are the exception: a route that needs a key goes round them and asks for the key alone, not for a boat as well. The route card's line in the program log says how many hazard rooms it crosses.

**Only moves you type go into Crystal Lake's teleport rooms.** Most of the lake's rooms can teleport you away as you walk in, and a log raft or wooden skiff does not stop it. The lake's other rooms carry no such spell: they are ordinary ground, need no raft, and routes use them freely. So no route is planned into the teleport rooms: walks, loop approaches, Auto-Lair travel and the automatic trips all go round, the picker never offers a boat as the way across, and no boat is fetched, bought or asked of the party for it. A walk whose way there crosses them has no route, yours as much as the client's, and says so in one line: *the way there crosses N teleporting room(s) … only typed moves go into those*. Two crossings are the exception, and both ask **level 50 and a log raft or wooden skiff in your pack, together** (with either missing there is no route, and the line names what is missing):

- **Stock: White Forest.** On Stock the teleport rooms cover the coast, and White Forest lies beyond them with no other way in. Any walk there, automatic or your own, is taken across with no card, by the crossing with the fewest teleport rooms (8 from Arlysia's docks) rather than the fewest steps, and the program log says once how many it crosses and why it was allowed. If another gate on the way (an item, a key) puts a card up, the card lists the teleport rooms the route crosses. The way back out needs no crossing. On Paradigm White Forest is reached through the lake's ordinary rooms, so nothing is crossed.
- **Paradigm: the room that teleports to the Bloodwood Weald.** One lake room, ringed by teleport rooms, sends you on to the Weald. A walk **you** start with that room as its destination shows one card, *Cross the teleport rooms*, which names the rooms it crosses and what the room is; picking it crosses and fetches nothing. A walk the client starts is never sent there.
- **Nowhere else.** The Isle of Bones is reached from inside the library, never across the lake, on either realm. The Ancient Galleon has no walk-to: a walk pointed at it has no route.
- **Standing in one** (teleported there, or part-way across), you are always planned out: the route takes the nearest way out of the teleport rooms that leads on to where you are going, and never back in. The Bloodwood Weald's room is planned the same way: standing in it, with level 50 and a boat, you are taken out across its two teleport rooms (any walk, automatic ones included), and with either missing there is no route out.
- **Loops are no exception.** A loop's legs are planned like any route, so they go round the teleport rooms, boat or no boat. A loop with a waypoint *in* one of those rooms is not started: it is refused with the reason, before any walk to it.
- **Moves you type are yours.** Walking into the lake by hand (to be taken to the Ancient Galleon, say) is untouched; the client just never does it for you.

**What you agree to is the hazard on the card.** Picking a card that crosses a hazard lets the walk into the hazard rooms on *that card's route* and no others. If the walk is thrown off its route, or stops for a sell trip, a flee, a party member or an event and picks up again from somewhere else, it may go back through the hazard you picked but never through a different one you weren't shown: if that is the only way left, it stops and says so. A walk that showed no card at all (one whose every gate item it fetches by itself) never walks into an uncountered hazard room.

Either way the previewed route now **draws its line on the map** even though you can't currently pass it, so you can see where it goes before you commit.

**Automated trips never cross a hazard on their own.** A stash transfer, a bank run, a trainer trip or a sell detour plans its own route, and nobody is asked. Such a walk only goes through a hazard room when you carry its counter or the trip is going to fetch it; otherwise it takes another way, and if there is none it stops and says what blocked it. Only a route you picked yourself in the route picker walks into a hazard unprotected.

**Keeping a hazard buff up.** Some hazards are survived by *using* an item rather than just carrying it — the desert heat is countered by drinking a **waterskin** (`use waterskin`), which holds only while its buff lasts. MudPlay keeps quiet track of that buff: when it went on (its own line, *You take a swig of water…*, also counts a drink you take by hand) and how long it lasts by the game data (30 minutes for the waterskin). It drinks again **shortly before the buff runs out and not sooner**, so a crossing spends as few charges as it can, and only where it matters. Away from the desert it never drinks, whatever the clock says.
- **With Auto-Sneak off, it drinks in the last 15 seconds** of the buff (or when the buff is off or unknown), right where you stand: at the step into a hazard room, or while you are in one. The first drink goes out at the desert's edge.
- **With Auto-Sneak on, it starts looking 60 seconds before the end**, and when a hazard room is within the next 5 steps of your walk or loop. The extra time is for finding a room with no NPCs to drink in (see *Drinking ends a sneak* below). The last 15 seconds are the last call here too.
- **The drink is the round's one in-between cast**, like a buff: a second cast in the same round would be refused. An emergency heal that is due goes first.
- **The step into a hazard room waits for the drink to be answered** (a few seconds at most), so you go in with the buff on.
- **Standing in a hazard room with nothing running** (no walk, no loop, no leader to follow) it keeps the buff up the same way, as long as Auto-All is on.
- **Following a party leader** it drinks on arriving in a hazard room with the buff off (it can't see the leader's next step), and by the same clock after that.
- **A dropped link pauses the clock.** The buff stays on your character while you are off the board, so the time away is not counted: the clock starts again, with what it had left, at the first prompt back in the game. It is not kept across closing MudPlay.
- **It forgets the buff** when you die (a hang-up death found at the login too), when you enter a room whose own spell strips buffs (negate magic), when you send a room command that casts one (`enter tapestry`, `enter portal`, `go courtyard`), when you use a transport token, and on a new profile. It then treats the buff as off the next time a hazard room is ahead. A room command is judged when it is sent, so one the game then turns away can cost a drink too early.
- **A drink the game refuses** is logged with the reason. *You have already cast a spell this round!* is tried again next round, and only then: the step into the hazard room waits out the round for it. An item the game says you don't have, or that is out of uses, is left alone until your pack changes (a new one bought, the spent one sold or dropped). An item MudPlay's own charge count shows as empty is not sent at all.

There is no setting for any of this: the timing is fixed, and the only switch is the master switch (Auto-All). If the heat still reaches you — *You suffer in the desert heat…* — the buff is off whatever the clock said: on a walk it drinks again at once, and standing still it drinks as soon as it has the round's cast. If that shows you've **run out of waterskins**, it says so in the room (`I'm out of waterskins!`, once) so the party knows, and on your own walk it stops rather than marching deeper into the heat. None of this is done while the **master switch (Auto-All)** is off: no drink, no say and no stop, so you take the heat unless you drink by hand; switched back on, it drinks at once if you are in a hazard room.

**Drinking ends a sneak** (the waterskin casts a spell). With **Auto-Sneak on**, inside the 60 seconds the drink waits for a room with no NPCs, exactly as a buff does (*Keeping the sneak*): your walk or loop stops in the next such room, drinks, sneaks again, waits for the sneak to take, and goes on. If no such room turns up before the last call, it drinks where you stand, sneak or not: staying alive comes first. A sneak spent that way (only if you were in fact sneaking or hidden) counts as a sneak lost in that room, so with *Clear hostiles when sneak fails* ticked (and the room inside your Min/Max monsters) MudPlay clears the room and then carries on; without it the walk goes on unsneaked until it can sneak again. The program log says each step at Info under *HazardCounter* (`due`, `waiting for a room with no NPCs`, `used`, `forced`, `refused`, `buff clock paused`), and a bug report shows the tracked buff under *Hazard buff*.

When a route crosses a survivable hazard **and** a hard gate past it — a keyed door you don't have the key for, like the walk to the Iceforge (across the Silver River, then through a locked door) — the picker offers the same hazard choices, but each one **stops at the hard gate** you must clear by hand:

- **"Obtain, then cross"** — fetches the counter and crosses, then halts at the door.
- **"Cross unprotected"** — takes the river damage and pushes on to the door.
- **"Walk to the hazard and stop"** — offered when no counter can be sourced nearby; walks you only to the room just short of the river, so you can fetch a raft (or clear the gate) from there rather than crossing blindly.

The requirement line names everything you'll need — and for a counter it can source, the **specific** item it'll fetch and where, e.g. *"Requires log raft (buy at Pier); the dragon key"* (picking the cheapest when several rafts are buyable) — so you know exactly what to gather before setting out.

**Avoiding traps on a walk-to.** If the shortest route to your destination crosses a trap and a route that crosses **fewer** traps exists, the route picker surfaces the choice:

- **Fewest-traps route** (pre-selected) — avoids every trap it *can* and crosses only the **unavoidable** ones (so a path with one dodgeable trap and one you can't get around routes past the dodgeable one and accepts the other). It's the default because a step-time disarm can fail (no lockpicks, no capable party member) and spring the trap.
- **Shortest route** — one click away when you'd rather take it; it disarms en route.

Both cards show their trap count. Click either route to preview its line on the map, then **Go**. When no route crosses fewer traps than the shortest, there's nothing to weigh, so the walk just proceeds and disarms en route as before.

When a route (the one you're walking, or a queued preview) crosses a trap, its **Details…** view flags that step in **red** with the trap's damage related to your HP — e.g. *trap: 36 dmg (~11% of HP)* — so you can see the hit each trapped step on the path would land. If you have the Traps skill, the step also shows your odds of disarming it: *trap: 36 dmg (~11% of HP) · disarm ~71%, failure (no dmg) 10%, failure (dmg) 19%*. The map's room tooltip and the room info panel show the same odds on a trapped exit (*Trap: 40 dmg, disarm ~71%, failure (no dmg) 10%, failure (dmg) 19%*). With **Picklocks**, a locked door shows your chance to pick it the same way — about your Picklocks minus the door's figure, plus one (*Door: 41 picklocks/strength, pick ~47%*; an "any" door reads *pick ≥…%*, since its lock only adds to your chance).

**How Traps and disarming work.** Finding a trap and disarming it are two separate skills that start from the same number:

- **The base** is `(INT + AGL + CHM×2 + level×28) ÷ 7`. Charm counts double, and past level 15 each level counts half. Only a class or race with the trap skill has it (Missionary, Ninja, Thief, Bard, Gypsy; Gnome on Paradigm).
- **Traps**, the number `stat` shows, is your **find** skill: the base plus any +Traps gear. Searching an exit (`sea <dir>`) finds a trap if a roll of 0–100 comes in under it; otherwise you *notice nothing different*, even though the trap is there.
- **Your disarm skill** is never shown. It's the base plus any +Disarm Traps gear. **+Traps gear (the thief's kit, dark onyx ring and similar) helps you find traps, not disarm them.** Without trap gear, the two are the same number.
- **A disarm** (`disarm trap <dir>`) rolls 0–100 against your disarm skill:
  - **under it:** the trap is disarmed;
  - **the next 10 points above it: failure (no dmg).** *You failed to disarm any trap…*, nothing happens, and you can try again;
  - **anything higher: failure (dmg).** The trap goes off, for half to all of its damage.

  So with a skill of 71: about 71% disarm, 10% failure (no dmg), 19% failure (dmg). At 90 and up, a failure never does damage.
- **Searching first doesn't help.** A search only tells you the trap is there; it gives no bonus to the disarm, and it ends a sneak. MudPlay knows every trapped exit from the game data, so it never searches and goes straight to the disarm, retrying a safe miss up to **@trap max disarms** times.
- **A disarm ends a sneak too**, so with auto-sneak on MudPlay re-sneaks before stepping through.

These rules were read from the Stock game engine. Paradigm is assumed to work the same way until it's confirmed.

**Walk it or teleport.** When the shortest route somewhere takes a **teleport** (a cast, an item-use portal, a CMD jump) and a plain **walking** route also exists, the picker asks which you want — **"Walk it"** (the safe overland route) or **"Teleport"** (the shortcut). A teleport can drop you somewhere lethal, so the client won't make that call for you. When the shortcut goes through a paid NPC transport, the card states its fare ("Costs 1 runic per person").

**A card walks the route it shows.** The picker only asks *Walk it or Teleport* when both ways exist for the same trip. Any other card whose route uses a teleport (the only way there is down a hole, or the way through your avoided rooms is by vortex) says so on the card: *"Respect your avoids — 47 steps — takes the teleport to Stone Tunnel, Hole Up (2/1306)"*. Picking that card takes that teleport. It doesn't swap the route for a longer one on foot that no card showed. A card that stops short of the route drawn (*Walk to the hazard and stop*, a stop at the shop to provision by hand) walks to that room on foot and names no teleport.

And once you're walking, a walk whose route **didn't** use a teleport won't quietly switch to one: if the route has to re-plan mid-trip — say a counter you were searching for turns up and the destination is recomputed — it **keeps to the walking route** and only falls back to a teleport if walking has become genuinely impossible. So picking "Walk it" (or any card that doesn't mention a teleport, or a walk-to that showed no cards) means you stay on foot the whole way, never surprised onto a vortex you didn't choose. A walk whose card did name a teleport re-plans by the shortest way, teleports included.

A **side trip** the walk makes to fetch an item its route needs (to an NPC who hands it over, a shop) is on no card, so it goes **on foot whenever it can**, and takes a teleport only when there is no way there on foot. That holds even when the card you picked names a teleport: the teleport you agreed to is the one on that card's route, and the walk takes it when it gets back to that route. After **"Walk it"** a side trip never teleports at all: with no way there on foot it is skipped, and the walk goes on without the item.

**Use a transport token (Paradigm).** If you're carrying a Paradigm **transport token** whose town reaches your destination meaningfully faster than walking, the picker adds a **blue token card** beside the plain overland walk — **"Use token of X — saves N rooms"**. It's never taken for you: using a token spends gold, one of its daily charges, and **wipes your buffs** (it casts negate magic), so it's always your click. Pick it and MudPlay uses the token and resumes the walk from where it drops you. If the room you're in isn't clear (a token can't be used with monsters present), it walks the overland route toward the destination and uses the token at the first monster-free room instead — a genuinely-shorter token route always reaches one before you arrive. Two toggles under **Settings → Other** (Paradigm only) control this: turn token routing off entirely, or set how many rooms a token must save before the card appears.

Because using a token casts negate magic and **wipes every buff**, MudPlay pauses buffing the moment a token use is on its way — whether you use it yourself or a party leader sends you across (it recognizes the relayed `use`, full name or shorthand). The hold lifts as soon as the token actually fires (buffs recast after you land) or after 30 seconds if the use never went through; while it's active the **Buff Watchdog** shows a *"Paused by token usage"* line.

**In a party**, only the leader can take a token route, and the leader goes **last**. From a monster-free room it sends the party across first with `.@party use token of <place>` (a party-relay every follower acts on — no special permission needed), then watches its own room as each member gryphons out. If someone hasn't gone, it checks who's still in the room and re-broadcasts `.@party use token of <place>` — a room-local relay, so only the members still standing there are re-told (no special permission needed) — up to three tries a few seconds apart. Once the whole party is across, the leader tokens over itself and the walk continues to the destination. If a member still can't follow, what happens depends on **Settings → Other → "Take a token route even if a party member can't follow"**: off (default) the leader stays put and **fails out with the reason in the nav header** so you can sort it out; on, the leader tokens across anyway and leaves them. (A party follower who tries to take a token route just walks the normal way instead.)

**Stopping or retargeting a token route.** While a token route is between walks — the party tokening across, your own token use, the landing — the Navigation window shows what it's waiting on and Run/Stop reads **Stop**. Pressing Stop (or the toolbar Stop), or starting a walk somewhere else, abandons the token route where you stand; nothing walks on to the old destination afterwards.

**Using a token yourself.** If you use a transport token by hand (not from a token card), MudPlay treats it as you taking over: any walk, loop or Auto-Lair stops, and nothing walks on from where the token drops you. Party members your teleport leaves behind aren't gone back for — regroup them yourself. (This is only for transport tokens; room-command, monster-keyword and item teleports are unaffected.)

**Routing through a room you marked "Avoid".** Rooms you flag **Avoid** (nav-map right-click → *Toggle: Avoid this room*, or the Avoid/Stash editor) are normally treated as walls — the walker never routes into them. When a destination is reachable **only** by passing through one, the route picker surfaces a choice rather than just failing:

- **"Route through N avoided room(s)"** — walk it this once, or cancel.
- **Two-card fork** — when an avoid-respecting route *does* exist but a route through an avoided room is meaningfully shorter: **"Respect your avoids"** (the longer clean route, pre-selected) vs **"Shorter — through N avoided room(s)"**.

Either way the card warns how many marked rooms it crosses and **names them** with their map/room numbers (an avoided room is often on another map or floor than the stretch you're looking at), and your **avoid list is left untouched** — only that one walk ignores it.

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

Open it from **View → Party**, a toolbar button, or **right-click the terminal → Open Party** (it has no default hotkey — you can assign one in Settings → Toolbar + Shortcuts). It's your live roster: one row per member, updated as their health and status broadcasts arrive. Its title names your own character and HP (`Party — Cidir (100%)`), so with several clients open you can tell whose window is whose; the leader is the row with the ★. Each row shows —

- a **★** on the party leader;
- a colour-coded **rank chip** — **F** front, **M** mid, **B** back — the member's combat rank;
- the member's **name and class**, and **HP / MA bars**;
- **status chips** that light up as conditions apply — **REST** resting · **MED** meditating · **BLD** blinded · **PSN** poisoned · **DIS** diseased · **CNF** confused · **HELD** held · **WAIT** waiting · **INVITED** invite pending;
- an **uninvite (⨯)** button — active only when *you* lead — that kicks a follower or withdraws a pending invitation.

**Even while solo**, the window shows **your own entry** — the same row, live-updating your HP / MA and status chips from your state — so you can watch the client recognize an ailment applying and clearing in real time without needing a party. It's display-only: your lone self row is never treated as a party (automation that only runs in a real party stays off), and the row folds into the roster seamlessly the moment a party forms.

The healing, ranks, nags, and re-invite behaviour the window reflects are all configured on **Settings → Party**.

### HP between `par` polls

The game only tells MudPlay a partymate's HP through the `par` party screen, as a percentage, each time `par` is sent (Settings → Party → *Send `par`*: on a timer, after a combat round, on unknown round damage, or never). In between, MudPlay keeps each member's HP moving from what it sees:

- **Damage** — every hit the round ledger credits to that member (the same reading behind *Show combat round totals*) comes off their HP.
- **Heals** — an instant heal seen landing on them goes on: yours, another member's, or a stranger's. When the line prints the amount (*"You cast minor healing on Raijin, healing 12 damage!"*) that amount is used; when it doesn't (the room's *"Raijin casts minor healing on Bob!"*) MudPlay uses the spell's **average** heal at the caster's level — your level for your own casts, a member's known level for theirs, and the spell's **lowest** level when the caster's level is unknown, so a guess never runs ahead of the member's real HP. A **party heal** adds its amount to every member, but only when it was your party's (you or a member cast it, or you felt it too). Heals over time (regeneration and the like) aren't counted — the next `par` picks them up.
- **Drains** — a drain a member lands (their necromantic bolt's *"goblin's life is drained for 20 damage!"*) heals them by the damage it did.
- **`par` is the truth.** Each `par` row (and a member's `@health` reply) replaces the running figure with what the game says, so any drift lasts a few seconds at most.

The point is party healing: the heal picker reacts to a member's dip in the same round instead of waiting for the next poll. It only applies to members whose **maximum HP is known** — learned from the `@health` exchange when a MudPlay member joins — so other clients' members stay on `par` alone. A member's bar can read as low as 1% from an estimate but never 0% (0% means "no reading yet").

### Configuring party buffs

Party buffs are no longer set up here in the Party window. **All** automated buffing — self bless, party bless, room light, mana-regen, and the "when HP/MA full" utility casts — is now configured in **one unified list inside the Buff Watchdog** (View → Buff Watchdog): click **＋ Add buff**, pick a spell, and tick the party members (or **All**) it should be cast on. See **Buff Watchdog** under *Tools & Diagnostics* for the full walkthrough.

Two things about party buffs stay worth knowing here:

- **Who's targeted** — a single-target buff fires for any member who's **currently in your party** (a MajorMUD party is always in one room, so being in `par` means being in the room; a member who leaves or is uninvited drops out and is no longer targeted). The one exception is a member who's **hiding**: the cast comes back *"You do not see … here!"*, so the client backs off that member — the Buff Watchdog marks them **"hidden — can't target"** — and retries the next time you **move** or they **reappear**. Targets are remembered by name, so your setup survives parties dissolving and reforming.
- **When it casts** — each buff carries its own conditions, set in its edit dialog in the Buff Watchdog: **Cast if mana ≥**, **Cast while resting** and **Cast during combat**. They apply to every cast of that buff, on you or on the party.

## Leaders and followers

One character leads; the rest follow. A follower tracks the leader's movement and holds position; if the leader disconnects, the party disbands. A party is 2–6 characters.

**Follow mode.** The game's `set follow blind` makes a follow move print only the *Following your Party leader* line, with no room after it. MudPlay's map confirms each follow move against that room display, so in Blind mode it can't keep a follower's place. When a `pro` sheet shows `Follow Mode: Blind`, or you turn it on with `set follow blind`, MudPlay says so once in the terminal and in the program log; `set follow normal` puts the room displays back. MudPlay never changes the setting itself and doesn't ask for the `pro` sheet to check it. The mode last seen is in the bug report.

**Leader reconnect re-invites the party.** A leader-drop dissolves the party, but the followers keep sitting in the room (they've no leader to follow). When the leader reconnects, MudPlay re-invites the ones still there — waiting until it actually sees each in the room before sending `invite`, since the game drops an invite aimed at someone who isn't present. A `look <direction>` into the next room doesn't count as seeing them: whoever it lists is a room away, and no invite is sent on it (the everyday invite-on-seen ignores it too). If you step into a room and look on from it in one breath, the room you stepped into still counts. A loop you were running restarts once you're back in the game, but takes no step before this re-invite has had its look at the room: it shows as paused, then waits behind the re-invite until the party has rejoined or the wait runs out. Stop stops it as usual. If no room is shown within 5 seconds (a room too dark to display), the loop goes on anyway. (Gated by *auto-invite on reconnect*, same as the follower-reconnect case.)

## Party healing

With party heal spells configured (Settings → Party), members watch each other's health broadcasts and heal whoever drops below the minor/major thresholds — single-target, or an area heal once enough members qualify.

**A partymate who drops to the ground** is aided at once, and movement holds so the party doesn't walk off (or drag them into a lair) while they're down. Aid only stops the bleeding: they climb back 1 HP every 30 s and can't act until their HP is positive. So the hold lasts as long as that climb can take — worked out from their HP if their client answers, otherwise from the realm's death floor (Settings → BBS + Display) as the worst case. Your downed-ally heal speeds it up. Once they should be up, MudPlay checks their health; when they answer standing, a leader re-invites them, and the hold releases once they're back to the party-heal bar.

## Remote @-commands

A remote command is a line of chat that starts with `@`. Another player sends it to your character; MudPlay reads it, checks that this player may ask for it, does it, and answers on the channel it came in on. It is how a leader steers followers, how a party asks its healer for a heal, and how two MudPlay clients hand each other loops, boss timers and Roomba logs.

Everything here is written from the side of the character that **receives** the command: "you" are the one being asked, and "the sender" is the player who typed it.

To learn how one command works, open its own topic in the groups below this one. Each topic lists the forms you can type, how names are matched, who may send it, every reply it can give, and what changes its behaviour. **Quick reference** has every command on one page.

### Sending a command

Type the command as the whole message, with `@` as its first character. Three channels carry remote commands:

- **Telepath:** `/Healer @health`. Only that player's client reads it, and the answer comes back to you by telepath. Use this unless you mean to ask several players at once.
- **Gangpath:** `bg @timer dragon`. Every MudPlay client in the gang that grants you the command answers, on gangpath, where the whole gang reads it.
- **Say:** `.@party rest`. Every MudPlay client in the room that grants you the command acts on it. An answer comes back as a say directed at you (`>Leader {reply}`), so in a full room you can tell it is yours.

Gossip, auction, yell and broadcast lines are never read as commands.

How the line is read:

- **The command word ignores case.** `@Reset` and `@reset` are the same command.
- **Words are split at spaces.** Extra spaces are dropped; a name of several words is put back together with single spaces.
- **Your own lines don't count.** Your client never obeys something you said, telepathed or gangpathed yourself.
- **An `@` word that is no command is left alone.** Someone saying `@because` in gang chat gets no answer from anyone.

You can't telepath a command to yourself. To run one on your own client, use `POST /command` of the **Local control API** (under *Settings Menu*): it skips the permission check and hands you the replies. To make another player's client send a command to you, put `&` in front of it: see **Sending a command back to yourself (`&@`)**.

### Who is obeyed

Your client goes through these checks in this order. The first one that stops a command ends it.

1. **Disallow all remote control commands** (Settings → Talk). While it is on, no command is read and nothing is answered.
2. **The channel.** Settings → Talk has one switch each for telepaths, gangpaths and say. A command on a switched-off channel is dropped without an answer.
3. **The master switch (Auto-All).** While it is off, `@auto-all` is the only remote command followed. Every other one is dropped without an answer, whoever sent it. A follower's `@wait` and `@ok` are still noted, and a party member's `@comeback` is kept and answered when the switch comes back on. See **The master switch (Auto-All)** under *Automation*.
4. **The words that are always refused.** See **Always refused** below.
5. **The permission.** The sender must hold the permission the command needs, or the command must be one any party member may send.

**Permissions** are granted per player, and saved with your character. Open **Game Data Browser → Players**, open the player, and tick boxes in the remote-control grid. There are sixteen:

| Permission | Commands it allows |
|---|---|
| Query version | `@version`, `@help` |
| Query experience | `@exp`, `@level` |
| Query health/status | `@health`, `@status`, `@lives`, a bare `@party` |
| Query location | `@where`, `@path`, `@who` |
| Query inventory | `@inv`, `@have`, `@what`, `@wealth`, `@enc`, `@uses`, `@token` |
| Query boss timers | `@timer`, `@timer sync` |
| Query deaths | `@death` |
| Query Roomba | `@roomba`, `@roomba sync` |
| Query quests | `@quest` |
| Request invite | `@invite`, `@join` |
| Move player | `@goto`, `@loop`, `@loop send`, `@lair`, `@stop`, `@rego` |
| Execute commands | `@do`, `@kill`, `@heal`, `@trap`, `@train`, `@equip`, `@get-all`, `@get-stash`, `@drop-all`, `@hide-all`, `@deposit-all`, and relaying a `&@` command |
| Hangup/disconnect | `@hangup`, `@relog` |
| Alter settings | `@auto-all`, the eleven `@auto-…` toggles, `@settings`, `@atkprio`, `@atkorder`, `@profile`, `@reset` |
| Divert conversations | `@divert` |
| Elevated Commands | `@suicide`, `@dupe` |

A player your client has never seen has no box ticked, so every command in the table is refused. The Players batch editor's **Set all permissions** ticks or clears all sixteen for the selected players.

**Party members get some commands with no box ticked.** Anyone on your party roster (matched by first name) may send:

- `@health`, `@status` and `@lives`;
- `@reset`;
- `@party`, unless **Disallow @party commands** is on (Settings → Talk);
- the party signals `@wait`, `@ok`, `@waiting`, `@comeback`, `@forget`, `@share` and `@ptrain`. These have no box at all: only party membership allows them, so a player outside your party can't be granted them. (A member who has just fallen out of the party can still send `@comeback`, `@forget` and `@ptrain` in the cases their topics describe.)

Everything else a party member sends needs its box, like anyone else's. In particular `@heal`, `@kill` and `@do` need **Execute commands**, and `@goto`, `@loop` and `@stop` need **Move player**.

### When you get an answer

**Every answer is wrapped in braces**, like `{HP=120/150,MA=40/60}`, so it reads as a client's answer and not as something the player typed.

**It comes back on the channel the command came in on:**

- a telepath is answered by telepath;
- a gangpath is answered on gangpath, for the whole gang to read;
- a say is answered with a say directed at the sender. A character that is sneaking or hidden answers by telepath instead, because saying anything would end the sneak.

**Refusals follow one setting.** Settings → Talk → **Warn sender on invalid / denied remote command** (on by default) decides whether a refused command is answered at all:

- On: the sender gets the reason when the client has a specific one, otherwise the **Failure message** from the same tab (default `command invalid or not allowed`). An empty Failure message sends nothing.
- Off: no refusal is sent.

In the reply tables of the command topics, a row that begins **Refusal:** is sent only while that setting is on. Every other row is sent whatever it is set to. The usual refusal, "the sender doesn't hold the permission", gets the Failure message.

**Some commands answer later, not at once:**

- `@train` answers when the training run has finished.
- `@trap` answers twice: when it takes the job, and with the result.
- `@get-stash` answers a few seconds later, once the coin is picked up.
- `@quest` with a quest name answers after the flag has been read from the game.
- `@where` on Paradigm, with your room unknown, answers after your client has asked the game.
- `@comeback` sends several answers as the pickup goes on.

**Pacing.** The server throttles telepaths: fire several at once and the later ones come back `--- Telepath Not Sent ---`. MudPlay sends every telepath (its own command traffic and replies, and the ones you type) at least 100 ms apart, and resends any the server refuses, up to three tries. Other commands (movement, attacks, casts) are never held behind a telepath. A long list (`@inv`, `@help`, `@timer`) is split over several replies. The big data replies (`@loop send`, `@roomba sync`) go out one line every 0.8 seconds; if the game says you are typing too quickly, the client waits three seconds and sends the last line again.

**The long dash.** Some replies are written with a long dash (—). The game's character set has none, so on your screen it arrives as `?`: `room 599 needs a map ? try e.g. 1/599`.

**On the receiving side** nothing pops up. A plain query leaves no trace but the reply itself, which shows in your terminal as the telepath or say your client sent. A command that makes your client act shows there too, as whatever it sent to the game. Commands that change something are written to the program log with the sender's name (`@do`, `@kill`, the `@auto-…` toggles, `@atkprio`, `@atkorder`, `@reset`, `@dupe`, `@get-stash`, a loop send, a `&@` relay). A refused command is logged only with Debug diagnostics on.

### Commands that never answer

- `@hangup` and `@relog`: the connection is on its way down.
- `@kill`, `@invite`, `@join` and `@suicide`, when they work: the action is the answer.
- `@party <command>` on say: the command your client sends to the game is the answer.
- `@wait`, `@ok`, `@waiting` and `@ptrain`: signals between clients.
- `@heal`, sent to a character with no party heal spell set up: only healers answer.
- An `@` word that is no command, a `@dupe` said aloud, any command while the master switch is off, while **Disallow all remote control commands** is on, or on a switched-off channel.

### Always refused

These are checked before the permission, so no grant gets around them.

- **`reroll`.** A command with `reroll` anywhere in it, in the command word or in what follows, is dropped. No answer is sent, whatever the Warn setting says, so nobody can probe for it.
- **`@party set suicide`** is dropped the same way.
- **`@do` with `suicide` anywhere after it** is answered `@do suicide is not allowed, use @suicide` (a **Refusal**) and nothing is sent to the game.
- **Any other command with `suicide` in it**, apart from `@help` and `@party`, is held to the lives rule of `@suicide` before anything else is looked at: see **@suicide**.

### Sending a command back to yourself (`&@`)

Put `&` in front of a remote command (`&@invite`, `&@where`, `&@wealth`) and the player you send it to **sends that command back to you**, on the channel it arrived on: a telepath, a gangpath line or a say directed at you. Your own client then takes it as a command from them, under the permissions *you* grant *them*. It is the way to make another player ask something of you: telling a party member `&@invite` makes them send you `@invite`, and your client invites them.

- **Who may ask:** the player relaying it must grant you **Execute commands**, the box `@do` needs. A `@do` could already put the same line on the wire.
- **What is relayed:** only a command their client knows, with whatever follows it, sent bare (no braces). Anything else, or a bare `&@`, is ignored without an answer. `reroll` and `@party set suicide` are dropped here too.
- **Replies:** none when it works: the relayed command is the answer, and your client's reply to it goes to them. Without the grant: the Failure message (a **Refusal**). With their master switch off: nothing, `&@auto-all` included, since it asks their client to send something.
- **On gangpath or say** everyone there sees both lines: every MudPlay client that grants you Execute commands relays the command, and any client that grants the relaying player that command runs it too. Use telepath when you want one player to relay it, and only to you.
- A bare `@help` lists `&@<command>` when you hold that grant, and `@help &@` describes it.

### Quick reference

One row per command. **Needs** is the box the sender must have ticked on your Players record; "party member" means anyone on your party roster, with no box. Each command has its own topic in the groups that follow.

| Command | Forms | Needs | What it does |
|---|---|---|---|
| `@goto` | `@goto <place>` | Move player | Walks you to a GOTO favourite, a room or a boss. |
| `@loop` | `@loop <name>` · `@loop <map/room>, <map/room>, …` · `@loop last` | Move player | Starts a saved loop, a loop through the rooms listed, or the last loop again. |
| `@loop send` | `@loop send` · `@loop send <name>` · `@loop send yes` · `@loop send no` | Move player | Gives the sender a copy of one of your loops. |
| `@lair` | `@lair <setup>` · `@lair <map/room>, <map/room>, …` | Move player | Starts Auto-Lair on a saved setup or on the rooms listed. |
| `@stop` | `@stop` | Move player | Pauses your movement. |
| `@rego` | `@rego` | Move player | Lifts that pause. |
| `@where` | `@where` | Query location | Your room, its map/room and its exits. |
| `@path` | `@path` | Query location | What is moving you, where you are, how far along. |
| `@who` | `@who` | Query location | Who else is in your room. |
| `@trap` | `@trap <direction>` · `@trap stop` | Execute commands | Disarms a trap that way; `stop` calls it off. |
| `@party` | `@party` · on say: `@party <command>` | Party member; the bare form also with Query health/status | Bare: solo, following or leading. On say with a command: sends that command to the game. |
| `@invite` | `@invite` | Request invite | You invite the sender to your party. |
| `@join` | `@join` | Request invite | You join the sender's party. |
| `@wait` | `@wait` | Party member | Your movement holds for the sender. |
| `@ok` | `@ok` | Party member | Releases that hold. |
| `@waiting` | `@waiting` | The leader you follow | Your client answers `@ok` once nothing holds you. |
| `@comeback` | `@comeback` · `@comeback <map/room>` | Party member, or one who just dropped | You go back for the sender. |
| `@forget` | `@forget` | Party member, one who just dropped, or the leader you were following | Calls a pickup off. |
| `@kill` | `@kill <target>` | Execute commands | You attack that target now. |
| `@atkprio` | `@atkprio` · `@atkprio 1` · `@atkprio 2` · `@atkprio 3 <player>` | Alter settings | Reports or sets your Target Priority. |
| `@atkorder` | `@atkorder` · `@atkorder 1` to `@atkorder 5` · `@atkorder 4 <player>` | Alter settings | Reports or sets your Attack Order. |
| `@profile` | `@profile` · `@profile <number>` · `@profile <name>` | Alter settings | Reports your combat profiles, or switches to one. |
| `@health` | `@health` | Query health/status, or party member | HP, mana, and whether you are resting. |
| `@status` | `@status` | Query health/status, or party member | What you are doing, where, and your ailments. |
| `@lives` | `@lives` | Query health/status, or party member | Lives left. |
| `@heal` | `@heal` | Execute commands | A healer re-reads the party's health and heals whoever is low. |
| `@hangup` | `@hangup` | Hangup/disconnect | Logs you off; you stay off. |
| `@relog` | `@relog` | Hangup/disconnect | Logs you off and straight back in. |
| `@inv` | `@inv` | Query inventory | Your pack and your keys. |
| `@have` | `@have <item>` | Query inventory | Whether you hold a matching item, and how many. |
| `@what` | `@what` | Query inventory | The items on the floor of your room. |
| `@wealth` | `@wealth` | Query inventory | Your coins. |
| `@enc` | `@enc` | Query inventory | Your encumbrance. |
| `@uses` | `@uses` · `@uses <item>` | Query inventory | Charges left on your limited-use items. |
| `@token` | `@token` · `@token <place>` | Query inventory | Charges left on your transport tokens (Paradigm). |
| `@get-all` | `@get-all` | Execute commands | Picks up what is on the floor. |
| `@drop-all` | `@drop-all` · `@drop-all full` · `@drop-all coins` · `@drop-all keys` | Execute commands | Drops your unworn pack, everything, your coins, or your keys. |
| `@hide-all` | `@hide-all` · `@hide-all full` · `@hide-all coins` · `@hide-all keys` | Execute commands | The same, hidden in the room instead of dropped. |
| `@deposit-all` | `@deposit-all` | Execute commands | Banks down (or withdraws up) to your keep-on-hand amount. |
| `@get-stash` | `@get-stash` | Execute commands | Searches the room and takes the coin it shows. |
| `@equip` | `@equip <set>` · `@equip <set> update` · `@equip-all` | Execute commands | Wears a gear set, or saves what you are wearing into it. |
| `@share` | `@share` | Party member | Splits your coins across the party. |
| `@train` | `@train` | Execute commands | Trains where you stand. |
| `@ptrain` | sent by clients, not typed | Party member | The Auto-train party handshake. |
| `@version` | `@version` | Query version | The client's name and version. |
| `@help` | `@help` · `@help <command>` | Query version | The commands the sender may use, or one command's form. |
| `@exp` | `@exp` | Query experience | Exp made this session, exp needed, rate, time to level. |
| `@level` | `@level` | Query experience | Level, exp, and exp to the next level. |
| `@death` | `@death` · `@death all` | Query deaths | Your deaths that are not yet recovered. |
| `@quest` | `@quest` · `@quest <name or flag>` · `@quest update` | Query quests | Quest progress. |
| `@settings` | `@settings` | Alter settings | Your eleven auto toggles, on or off. |
| `@timer` | `@timer` · `@timer <name>` · `@timer sync` | Query boss timers | Your boss timers; `sync` hands them to the sender's client. |
| `@roomba` | `@roomba <item>` · `@roomba sync` | Query Roomba | Where an item was last seen; `sync` hands over your whole log. |
| `@auto-all` | `@auto-all` · `@auto-all on` · `@auto-all off` | Alter settings | The master switch. |
| `@auto-combat` `@auto-nuke` `@auto-heal` `@auto-rest` `@auto-bless` `@auto-light` `@auto-cash` `@auto-get` `@auto-sneak` `@auto-hide` `@auto-search` | each one bare, or with `on` or `off` | Alter settings | Flips or sets that one auto toggle. |
| `@reset` | `@reset` | Alter settings, or party member | Zeroes your session statistics. |
| `@divert` | `@divert <player>` · `@divert` | Divert conversations | Forwards your incoming telepaths to that player; bare stops it. |
| `@do` | `@do <command>` | Execute commands | Sends the command to the game as if you typed it. |
| `@suicide` | `@suicide` | Elevated Commands | Kills your character. |
| `@dupe` | `@dupe <player>` | Elevated Commands | Copies the sender's query permissions onto that player. |

`@panic` and the ailment words (`@held`, `@blind` and the rest) look like commands but are party signals with rules of their own: see **@panic** and **Not commands: the ailment words**.

### Movement and position

The six movement commands (`@goto`, `@loop`, `@loop send`, `@lair`, `@stop`, `@rego`) need **Move player**. The three position queries (`@where`, `@path`, `@who`) need **Query location**. `@trap` needs **Execute commands**. All are taken on telepath, gangpath and say.

Three rules hold for `@goto`, `@loop` and `@lair` alike:

- **A new movement command replaces what is running.** A loop, an Auto-Lair run or a walk-to already under way is stopped for it, and the program log names the sender (`superseded by remote @ from <sender>`).
- **It overrides an `@stop`.** After `@stop`, an `@goto`, `@loop` or `@lair` drops the pause and starts moving at once. Use `@rego` only to carry on with the same thing you paused.
- **The reply says the movement was started, not that it arrived.** Ask `@path` or `@status` to follow it.

#### @goto

Walks you to a place: one of your GOTO favourites, a room, or a boss.

**Forms:**

- `@goto arlysia`: a GOTO favourite, by its label.
- `@goto 3/599`: a room by map and room number. `3,599` and `3 599` read the same.
- `@goto town square`: a room by its name. `@goto fcco` finds it by its initials (*Frozen Cavern, Cave Opening*).
- `@goto ogre king`: a boss from your boss list.

**How the place is found.** Your client tries these in order and stops at the first that answers:

1. **A bare number is refused.** `@goto 599` can't pick a room, because room 599 exists on every map.
2. **A full map/room** is that room and nothing else. A favourite or boss is never looked up for it.
3. **A GOTO favourite.** A label that equals what was typed (ignoring case) wins. Otherwise a favourite matches when every typed word appears in its label. One match is walked to; several are listed and nothing moves.
4. **A room.** By map/room, by initials (the first letter of every word of the room name, typed as one word), or by name: every typed word must appear in the room's name. One match is walked to. Two or three are offered back as a question. More than three is refused.
5. **A boss.** A name that equals what was typed wins; otherwise the only boss whose name holds every typed word. Your client walks to the nearest of that boss's rooms, and stops one room short when the boss is marked to stop before its room.

Words are split at spaces and at `, / - ' . ; : ( ) [ ]`. A word matches as part of a longer one (`squ` finds *Square*), in any order, ignoring case.

**Who may send it:** a player with **Move player**, on any of the three channels.

| Reply | When |
|---|---|
| `walking to GOTO '<label>' (<map>/<room>)` | A favourite matched and the walk started. |
| `walking to <room name> (<map>/<room>)` | One room matched and the walk started. |
| `walking to boss <name> (<map>/<room>)` | A boss matched and the walk started. |
| `walking to boss <name>, stopping just outside (<map>/<room>)` | The same, for a boss marked to stop before its room. |
| `did you mean: <room> (<map>/<room>), <room> (<map>/<room>)?` | Two or three rooms matched. Send again with the map/room. |
| `too many room matches (<n>) for '<text>'` | More than three rooms matched. |
| `'<text>' matches <n> GOTO locations: '<label>', '<label>'` | Several favourites matched. Up to four are named. |
| `'<text>' matches <n> bosses: <name>, <name>` | Several bosses matched. Up to four are named. |
| `no path to GOTO '<label>'` · `no path to <room name>` · `no path to <boss>` | The place was found but no route could be planned. |
| `<boss> has no known room on this map` | None of the boss's rooms is in your map data. |
| `room <n> needs a map — try e.g. 1/<n>` | A bare room number was sent. |
| `no match for '<text>'` | Nothing matched. |
| `@goto requires a destination` | Nothing followed `@goto`. |
| the Failure message | **Refusal:** the sender doesn't hold Move player. |

**Situations:**

- **Already walking somewhere:** the new destination replaces the old one.
- **Looping or in Auto-Lair:** that run is stopped, then the walk starts. It is not put back afterwards.
- **Paused by `@stop`:** the pause is dropped.
- **No route picker.** The walk is planned like the client's own automatic walks; nobody is asked to choose a route.
- **A follower asking their leader:** the follower's map draws the leader's route from the reply (see **`@path` on the map** under *Navigation & Looping*).
- **Master switch off:** ignored, no answer.

#### @loop

Starts one of your saved loops, a loop through a list of rooms, or the last loop again. (`@loop send`, which copies a loop to the sender, has its own topic next.)

**Forms:**

- `@loop black fortress`: a saved loop, by name.
- `@loop 5/10, 5/11, 5/12`: a loop through two or more rooms, given as map/room and separated by **commas or semicolons**. It is run under the name `@loop from <sender>` and is not saved.
- `@loop last`: the last loop that was started on your client this session, however it was started, including a room-list loop that was never saved.

**How what you typed is read**, in this order:

1. First word `send`: it is a `@loop send`.
2. The whole text is `last`: the last loop.
3. **A room list.** The text is cut at commas and semicolons. If every piece is a full map/room (`5/10` or `5 10`) and there are at least two, it is a room list.
4. Anything else is a **name**.

So a list written with spaces only, `@loop 5/10 5/11 5/12`, is not a room list: it is looked up as a loop name and answered `no saved loop named '5/10 5/11 5/12'`. A single room is not a list either.

**How a name finds a loop.** This is the rule, in full:

1. **An exact name wins.** If a saved loop is called exactly what you typed (ignoring case), that loop is taken, even when other loops also contain those words.
2. **Otherwise every typed word must appear in the loop's name.** The text is split into words at spaces and at `, / - ' . ; : ( ) [ ]`. A loop matches when each word is found somewhere in its name: in any order, ignoring case, and as part of a longer word.
3. **If that finds nothing, apostrophes are dropped** from what you typed and from the loop names, and step 2 is tried once more.
4. **Exactly one loop must match.** One match starts. No match and several matches both start nothing, and say so.

Worked examples, with these loops saved: *Bank of Godfrey Loop*, *Godfrey Sewers*, *King's Road*, *Black Fortress*, *Black Fortress East*.

| You send | What happens |
|---|---|
| `@loop godfrey sewers` | Exact name: *Godfrey Sewers* starts. |
| `@loop godfrey bank` | Only *Bank of Godfrey Loop* holds both words, in either order: it starts. |
| `@loop godfrey` | Two loops hold the word. Nothing starts: `'godfrey' matches 2 loops: 'Bank of Godfrey Loop', 'Godfrey Sewers'`. |
| `@loop black fortress` | Exact name: *Black Fortress* starts, although *Black Fortress East* holds both words too. |
| `@loop fort east` | Parts of words are enough, and only *Black Fortress East* holds both: it starts. |
| `@loop kings road` | No loop holds `kings`. With apostrophes dropped, *King's Road* reads *Kings Road*: it starts. |
| `@loop dragon` | Nothing holds the word: `no saved loop named 'dragon'`. |

Only the loops in your own Loops list (for the game data set you have loaded) are searched. A loop named exactly `last`, or one whose name begins with the word `send`, can't be started by that name, because those words are read first.

**Who may send it:** a player with **Move player**, on any of the three channels. On gangpath or say, every client there that grants you Move player and has a matching loop starts it.

| Reply | When |
|---|---|
| `starting loop '<name>' (<n> rooms)` | One saved loop matched. `<n>` is the number of rooms saved in the loop. |
| `looping <n> rooms` | A room list was given. |
| `restarting last loop '<name>' (<n> rooms)` | `@loop last`, and a loop has run this session. |
| `no loop has run yet this session` | `@loop last`, and none has. |
| `no saved loop named '<text>'` | No loop matched the name. |
| `'<text>' matches <n> loops: '<name>', '<name>'` | Several loops matched. Up to four are named; nothing starts. |
| `@loop requires a name, coordinate list, or 'last'` | Nothing followed `@loop`. |
| the Failure message | **Refusal:** the sender doesn't hold Move player. |

**The reply is sent as soon as the loop is found.** It does not wait to see whether the loop could really begin, and nothing more is sent if it couldn't. A loop is not run when it has fewer than two rooms, when one of its rooms can't be entered by any route, when one of its legs can only be crossed by paying a toll or fare you can't pay, or when none of it can be reached from where you stand. The receiver's program log says which. To check, send `@path` a moment later: a loop that started answers `running loop '<name>'; …`, one that didn't answers `not moving`, with or without a `last ran` tail.

**When something is already running:**

- **Another loop, or the same one:** it is stopped and the new one starts fresh. Its lap count starts over, and your session statistics are reset again if **Reset statistics on loop start** is on.
- **Auto-Lair or a walk-to:** stopped, and not put back afterwards.
- **Paused by `@stop` or the Pause button:** the pause is dropped and the loop moves at once.
- **A fight, a rest, a party wait:** these still hold the new loop, as they hold any loop. It starts when they clear.

**Where it starts.** Standing on the loop, it starts from that room. Anywhere else, your client first walks to the nearest room of the loop, through a door or hidden exit it can open on the way if there is no free way in.

**Other situations:**

- **Master switch off:** ignored, no answer.
- **On your own screen:** the Navigation window shows the loop as running. The loop builder closes if it held that loop or was empty; a different loop you were building is kept.

#### @loop send

Gives the sender a copy of one of your saved loops, over chat. Two MudPlay clients are needed: yours hands the loop out, theirs puts it back together and saves it. It rides the **Move player** permission: a player you trust to run your loops may also have a copy of one.

**Forms:**

- `@loop send kings road`: ask for a saved loop by name. The name is matched exactly as `@loop` matches it.
- `@loop send`: ask for the loop you are running right now (running, paused or still walking to it).
- `@loop send yes` (or `y`): take the loop just offered.
- `@loop send no` (or `n`): turn it down.

**From asking to saved**, with Leader asking Scout for a loop:

1. Leader sends `/Scout @loop send kings road`. Scout's client finds the loop and answers `{preparing to send: King's Road, yes to confirm, no to deny}`. Nothing has been sent yet. The offer is Leader's alone, and a new ask from Leader replaces it.
2. Leader answers `/Scout @loop send yes` within two minutes. After that the offer has lapsed and a `yes` is answered `no loop send pending — use @loop send <name> first`.
3. Scout's client answers `{sending loop 'King's Road' (42 rooms, 3 lines)}` and then sends the loop as that many `{@loopdata <id> <i>/<k> <data>}` lines, 0.8 seconds apart.
4. Leader's client collects the lines. When the last one is in, it rebuilds the loop, saves it to Leader's Loops list, and prints a notice in Leader's terminal.

| Reply from the client asked | When |
|---|---|
| `preparing to send: <loop name>, yes to confirm, no to deny` | A loop was found by name, or you are running one and the bare form was sent. |
| `not running a loop — @loop send <name> for a saved one` | The bare form, and no loop is running. |
| `no saved loop named '<text>'` | The name matched nothing. |
| `'<text>' matches <n> loops: '<name>', '<name>'` | The name matched several loops. |
| `sending loop '<name>' (<n> rooms, <k> lines)` | `yes`, with an offer still open. `1 line` when the loop fits one. |
| `@loopdata <id> <i>/<k> <data>` | The loop itself, line `<i>` of `<k>`. |
| `no loop send pending — use @loop send <name> first` | `yes`, with no offer open or one older than two minutes. |
| `loop send of '<name>' cancelled` | `no`, with an offer open. |
| `no loop send pending` | `no`, with none open. |
| the Failure message | **Refusal:** the sender doesn't hold Move player. |

| Notice in the asker's terminal | When |
|---|---|
| `[Received loop '<name>' <n> rooms from <player>]` | Saved under its own name. |
| `[Received loop '<name>' <n> rooms from <player>, saved as '<name> (from <player>)']` | You already had a different loop by that name. Yours is kept; the new one gets the longer name (and a number after it if that name is taken too). |
| `[Received loop '<name>' <n> rooms from <player>, already have it]` | You have the identical loop under that name. Nothing is saved. |
| `[Received loop '<name>' <n> rooms from <player>, not saved: no game data set is loaded]` | There was nowhere to save it. |
| `[Loop from <player> arrived damaged, not saved]` | The lines could not be put back together. |

**What your client accepts.** Loop lines are taken only within two minutes of your own `@loop send yes` (or `y`) going out, and one loop per `yes`. When you said yes by telepath, only lines from that player are taken. A loop needs no permission on the receiving side: you asked for it.

**What travels:** the route itself. Every room, its command and delay, its no-rest, no-attack and rest-here marks, the loop's notes, its *Only attack in lair rooms* setting and its lair-entry debuff setting. Whether it is a favourite, and which folder it sits in, stay your own choice.

**Situations:**

- **On gangpath or say:** every MudPlay client there that grants you Move player offers its own match, and after a `yes` on those channels your client takes lines from any of them. Use telepath.
- **`yes`, `y`, `no` and `n` are read as answers**, so a loop with one of those names can't be asked for by name.
- **The game says the sender is typing too quickly:** the sending client waits three seconds and sends the last line again.
- **Master switch off on the client asked:** ignored, no answer.
- **On the sending client's screen:** nothing but the outgoing lines. Its program log notes who asked for which loop, the answer, and the send.

#### @lair

Starts Auto-Lair on one of your saved lair setups, or on a list of rooms.

**Forms:**

- `@lair mud men`: a saved lair setup. The name must be the setup's **whole name** (ignoring case). There is no partial match here.
- `@lair 5/10, 5/11, 5/12`: two or more rooms as map/room, separated by commas or semicolons, read the way `@loop` reads a room list.

**Who may send it:** a player with **Move player**, on any of the three channels.

| Reply | When |
|---|---|
| `auto-lair '<name>': <n> lairs` | The saved setup was found and Auto-Lair started. |
| `auto-lair: <n> lairs` | A room list was given and Auto-Lair started. |
| `auto-lair failed to start` | The rooms were marked but Auto-Lair would not start on them. |
| `no saved lair setup named '<text>'` | No setup has exactly that name. A single map/room, or a list written with spaces only, ends here too. |
| `@lair requires a name or coordinate list` | Nothing followed `@lair`. |
| the Failure message | **Refusal:** the sender doesn't hold Move player. |

**Situations:**

- **Your marked lairs are replaced.** The lairs you had marked are cleared and the new ones marked in their place. A saved setup brings its own respawn times with it.
- **Looping, walking, or in another Auto-Lair run:** that is stopped first.
- **Paused by `@stop`:** the pause is dropped.
- **Master switch off:** ignored, no answer.

#### @stop

Pauses your movement, exactly as the toolbar's Pause button does. Fighting, healing and the other engines go on.

**Who may send it:** a player with **Move player**, on any of the three channels.

| Reply | When |
|---|---|
| `movement paused, I'm in <room name> (<map>/<room>)` | The pause was set. |
| `movement paused` | The pause was set and your room isn't known. |
| `already @stopped` | A pause of yours was already in place, from an earlier `@stop` or from the Pause button. |
| the Failure message | **Refusal:** the sender doesn't hold Move player. |

**Situations:**

- **In a fight or resting:** the pause is added on top. When the fight or rest ends, you stay paused.
- **Nothing running:** the pause is set all the same, so `@rego` has something to lift.
- **Your auto toggles go back to their base modes** when the pause takes hold (see **Base modes** under *Automation*). A repeated `@stop` that answers `already @stopped` changes nothing.
- **A later `@goto`, `@loop` or `@lair`** drops the pause and starts the new movement.
- **Master switch off:** ignored, no answer.

#### @rego

Lifts the pause that `@stop` or the Pause button set, and carries on with what was paused.

**Who may send it:** a player with **Move player**, on any of the three channels.

| Reply | When |
|---|---|
| `resuming loop '<name>'` | A paused loop carries on. |
| `resuming walking to <room name> (<map>/<room>)` | A paused walk-to carries on. |
| `resuming auto-lair (<n> lairs)` | A paused Auto-Lair run carries on. |
| `resuming movement` | The pause was lifted and neither a loop nor a walk was waiting behind it. |
| `loop '<name>' already running` | No pause of yours was in place, and a loop is running. |
| `auto-lair already running (<n> lairs)` | The same, in Auto-Lair. |
| `already walking to <room name> (<map>/<room>)` | The same, on a walk-to. |
| `nothing to resume` | No pause, and nothing running. |
| the Failure message | **Refusal:** the sender doesn't hold Move player. |

**Situations:**

- **Only your own pause is lifted.** A fight, a rest or a party wait still holds the movement, and it carries on by itself when that clears.
- **Auto-Lair** picks its next lair afresh on resume, since respawn timers kept running while you stood.
- **Master switch off:** ignored, no answer.

#### @where

Reports the room you are in.

**Who may send it:** a player with **Query location**, on any of the three channels.

| Reply | When |
|---|---|
| `<room name> (map <m>, room <r>); exits: <exit>, <exit>` | Your room is known. Exits are spelled out (`north`, `southeast`, `up`). |
| `<room name> (map <m>, room <r>); exits: none` | The room has no exits in your map data. |
| `map <m>, room <r> (not in map data)` | Paradigm: the game gave your position, and that room isn't in your map data. |
| `Location unknown` | Your client doesn't know where you are. |
| the Failure message | **Refusal:** the sender doesn't hold Query location. |

**Situations:**

- **Paradigm, room unknown:** your client asks the game for your position (`rm`) and answers once it has it, so the reply comes a moment later. If the game doesn't answer, the reply is `Location unknown`.
- **Stock, room unknown:** `Location unknown` at once.
- **The asker's client** flashes the room on its map when the reply lands (see **`@where` on the map** under *Navigation & Looping*).

#### @path

Reports what is moving you, where you are, and how far along you are.

**Who may send it:** a player with **Query location**, on any of the three channels.

The reply has up to three parts, separated by semicolons: what is moving you, your room, and the step count.

| First part | When |
|---|---|
| `walking to <map>/<room>` | A walk-to is under way. |
| `running loop '<name>'` | A loop is running. |
| `auto-lair` | Auto-Lair is running. |
| `sailing to <port>` (or `sailing`) | You are on a boat, whichever of the three put you there. |
| `<reason> en route to <map>/<room>` · `<reason> on loop '<name>'` · `<reason> (auto-lair)` | The movement is standing still. `<reason>` is `paused` (your Pause, or an `@stop`), `fighting`, or the hold the Navigation window names: `Low HP`, `Low MANA`, `Held`, `@Wait`, `Too heavy`, `Searching Room` and so on. |

| Whole reply | When |
|---|---|
| `running loop 'Black Fortress'; Dark Hall (map 5, room 10); step 12/40` | Something is moving you. The room part reads `location unknown` when your room isn't known, and the step part is left out when no route is loaded. |
| `not moving; last ran loop '<name>'` | Nothing is moving you now; a loop was the last thing run this session. |
| `not moving; last ran auto-lair '<name>'` (or `not moving; last ran auto-lair`) | The same, when Auto-Lair was the last thing run. |
| `not moving` | Nothing is moving you, and neither a loop nor Auto-Lair has run this session. |
| the Failure message | **Refusal:** the sender doesn't hold Query location. |

**Situations:**

- **After a death or an `@stop`** the `last ran` form tells a party member which loop to put you back on (`@loop last` does it).
- **The asker's client** draws your route on its map from the reply (see **`@path` on the map** under *Navigation & Looping*). A follower's client also asks its leader `@path` by itself when it leaves a boss room wearing the Bossing gear set.

#### @who

Lists the other players and monsters in your room, from the room's last *Also here* line.

**Who may send it:** a player with **Query location**, on any of the three channels.

| Reply | When |
|---|---|
| `<name>, <name>, <name>` | Someone or something is in the room with you. You are not in the list. |
| `no one` | Nobody else is there, or the room has not been read yet. |
| the Failure message | **Refusal:** the sender doesn't hold Query location. |

#### @trap

Has you disarm a trap in one direction, for the sender.

**Forms:**

- `@trap n`: disarm the trap to the north. The directions are `n`, `s`, `e`, `w`, `ne`, `nw`, `se`, `sw`, `u` and `d`, or the full words (`north`, `northeast`, `up`).
- `@trap stop`: drop the disarm in hand and every request waiting behind it.

**Who may send it:** a player with **Execute commands**, on any of the three channels. Anyone with that permission may send `stop`, not only the player who asked for the disarm.

| Reply | When |
|---|---|
| `Attempting to disarm trap <dir>.` | Your client took the job. The result follows in one of the next rows. `<dir>` is the short form (`n`, `ne`). |
| `Trap to the <dir> disarmed.` | Disarmed. |
| `Trap to the <dir> disarmed <n>s ago.` | You disarmed that exit a moment ago and it has not re-armed (two minutes on Paradigm, five on Stock). Nothing is sent to the game. |
| `Trap to the <dir> already disarmed.` | The game says it is already down. |
| `No trap to the <dir> to disarm.` | The game says there is no trap that way. |
| `No trap to the <dir> to disarm (failed <n> times; taking it as clear).` | Every try got the answer that can mean either a failed disarm or no trap, so it is taken as clear. |
| `Couldn't disarm the trap to the <dir> (<n> attempts).` | The tries ran out. |
| `Trap flow stopped.` | Someone sent `@trap stop` while your request was in hand or waiting. |
| `ok` | The answer to `@trap stop`. |
| `missing direction (e.g. @trap n)` | **Refusal:** nothing followed `@trap`. |
| `unknown direction '<text>'` | **Refusal:** the word is not a direction. |
| `can't disarm — no Traps skill` | **Refusal:** you have no Traps skill. Sent on telepath and gangpath only. |
| the Failure message | **Refusal:** the sender doesn't hold Execute commands. |

**Situations:**

- **Asked on say with no Traps skill:** no answer at all, so a room full of clients doesn't answer in chorus. Only a character that can disarm speaks up.
- **Several requests:** they are taken one at a time, in the order they came.
- **Resting:** the disarm waits until the rest is over, then goes on.
- **How many tries:** Settings → Other → **@trap max disarms**.
- **Master switch off:** ignored, no answer.
- **The asking side:** a MudPlay leader sends `@trap <direction>` by itself when its walk meets a trap it can't disarm. See **Utilize self or party members to disarm traps** under Settings → Other.

### Party and following

These are the commands a party runs on. Most of them need no box ticked: being on the receiver's party roster is enough.

#### @party

Two commands under one name. Bare, it asks what your party standing is. Said aloud with a command after it, it makes your character send that command to the game: the party's version of `@do`.

**Forms:**

- `@party`: are you solo, following or leading?
- `.@party rest` (on **say** only): your client sends `rest` to the game. Whatever follows `@party` is sent as typed, so `.@party use chime` sends `use chime`.

Two rewrites are made before sending. `@party meditate` sends `medi`. `@party go <direction>` sends the direction alone (`@party go n` sends `n`); `@party go hole` keeps its `go` and sends `go hole`.

**Who may send it:**

- **The bare form:** anyone on your party roster, or a player with **Query health/status**. On any of the three channels.
- **With a command:** only on say, and only from someone on your party roster. Sent by telepath or gangpath, the words after `@party` are ignored and you get the bare form's answer.
- **Disallow @party commands** (Settings → Talk) stops the relay for everyone, with no answer. It also takes away the party member's free pass to the bare form: a member then needs Query health/status to get an answer.

| Reply | When |
|---|---|
| `no active party` | You are not in a party. |
| `I'm following <leader>` | You follow someone. |
| `I'm following an unknown leader` | You follow someone and your client doesn't know the leader's name. |
| `I'm leading: <name>, <name>` | You lead. The followers are listed by first name. |
| `I'm leading: (no followers)` | You lead and nobody else is on the roster. |
| nothing | A command was relayed. What your client sent to the game is the answer. |
| nothing | A command said by someone who isn't on your party roster, or while Disallow @party commands is on. |
| the Failure message | **Refusal:** the bare form, from a player who is neither on your roster nor holds Query health/status. |

**Situations:**

- **Always refused, without an answer:** `@party set suicide`, and any `@party` line with `reroll` in it. Everything else is relayed, a plain `suicide` included.
- **Master switch off:** ignored, no answer.
- **`.@party break` holds your attack.** A `break` relayed this way puts the attack hold on your character exactly as a `break` you type does: Auto-Combat stops attacking the monster it was fighting and attacks nothing else in the room. An attack relayed the same way (`.@party a orc`) lifts the hold, as an attack of your own would. Your terminal shows `[Attack on <monster> held by Leader's @party break: attack to carry on]` when the hold goes on and `[Attack hold on <monster> ended: …]` when it ends, and the program log and a bug report name Leader as the one who asked. What the hold does and how it ends is under *Combat → The round loop*, **Stopping the fight yourself**.
- **The sending side:** a MudPlay leader says `.@party <command>` by itself to bring the party through a gate or a teleport that only takes the one who uses it. See *Navigation & Looping*.

#### @invite and @join

`@invite` asks you to invite the sender into your party. `@join` asks you to join theirs. When it works there is no reply: your client sends `invite <sender>` or `join <sender>` to the game, and that is the answer.

**Who may send it:** a player with **Request invite**, on any of the three channels.

| Reply | When |
|---|---|
| nothing | The `invite` or `join` was sent to the game. |
| `Can't invite — I'm mortally wounded; being dragged by <name>.` | `@invite`, and you are down. Sent whatever the Warn setting says. |
| `Can't invite — I'm mortally wounded; nobody is dragging me.` | The same, when nobody is dragging you. |
| `Can't join — I'm mortally wounded; being dragged by <name>.` (or `…; nobody is dragging me.`) | `@join`, and you are down. |
| `My Party is full, <name>, <name> are following me.` | `@invite`, and your roster already holds six. |
| `I'm following <leader>; denied.` | **Refusal:** you are following someone. |
| `I'm following someone; denied.` | **Refusal:** the same, when the leader's name isn't known. |
| the Failure message | **Refusal:** the sender doesn't hold Request invite. |

**Situations:**

- **`@join` from the leader you already follow** is not refused: your client sends `join` again. A leader never asks a current follower to join, so the request means your client's idea of the party is out of date.
- **Not checked:** whether the sender is in your room. The game answers the `invite` or `join` itself.
- **Master switch off:** ignored, no answer.

#### @wait, @ok and @waiting

The hold signals. A member who can't move sends `@wait`; `@ok` says they can again; `@waiting` is a leader telling a follower it is standing by for that `@ok`. MudPlay sends all three by itself, and none of them is answered with a reply.

**Forms:**

- `@wait`: hold for me. A MudPlay follower adds its reason for the leader to read, in MegaMUD's wording where MegaMUD has one: `@wait (HP's too low)`, `(blinded)`, `(confused)`, `(can't move)`, `(waiting on message condition)` for poison or disease, and MudPlay's own `(mana's too low)` and `(too heavy to move)`. The reason changes nothing in what the receiving client does.
- `@ok`: I can move again.
- `@waiting`: sent by a leader that went back for a follower and is now holding for their `@ok`.

**Who may send it:** anyone on your party roster, on any of the three channels. `@waiting` is acted on only when it comes from the leader you follow.

**What each one does:**

- **`@wait`** puts the sender on your waiting list. Your movement holds, and their row in the Party window shows **WAIT**, until every waiting member has sent `@ok` or the time in **If leading, wait only (s)** (Settings → Party) runs out.
- **`@ok`** takes the sender off the list. When the list is empty, your movement carries on.
- **`@waiting`** makes your client telepath `@ok` to that leader at once if nothing holds you. If something still does, the `@ok` goes when it clears.

| Reply | When |
|---|---|
| nothing | Always. These are signals, not questions. |
| the Failure message | **Refusal:** the sender is not on your party roster. |

**Situations:**

- **Ignore @wait when leading** (Settings → Party): while you lead, a follower's `@wait` is dropped.
- **An `@ok` that came too early.** If a follower was left behind a moment after sending `@ok`, your client stops trusting their `@ok` for that wait and sits out the whole wait time.
- **Master switch off:** `@wait` and `@ok` are still noted, but no hold is raised. When the switch comes back on you hold for whoever last asked. `@waiting` is ignored.

#### @comeback and @forget

`@comeback` is a member who was left behind asking you, the leader, to come back for them. `@forget` calls a pickup off, from either side. A MudPlay follower sends `@comeback` by itself (see **Reconnecting** and, under Settings → Other, **Auto-request @comeback when left behind**).

**Forms:**

- `@comeback 9/1012`: come to this room. The room is written `map/room`, with a slash.
- `@comeback`: I don't know where I am. You walk back along the way you came, looking for them.
- `@forget`: stop coming for me (from the member), or I am not coming (from the leader).

**Who may send it:**

- **`@comeback`:** anyone on your party roster, and a member who dropped out of your party within the time in **If leading, accept @comeback for (min)** (Settings → Party) and whom you did not uninvite.
- **`@forget`:** the same, and also the leader you were following when you dropped.

Both are taken on any of the three channels.

| Reply to `@comeback` | When |
|---|---|
| `coming to your location for pickup` | A room was named, it is within reach, and the walk has started. Also when you were already backtracking for them and they now name their room. |
| `backtracking up to <n> room(s) to find you` | No room was named. You walk back along your own path, up to **Return distance (rooms)**. |
| `found you — re-inviting <name>` | You reached them and sent the invite. |
| `got you — resuming` | They follow you again. What you were doing is put back. |
| `got you — waiting for your @ok` | They follow again, and they were left behind because they couldn't move. Your client also telepaths them `@waiting`. |
| `got you — your last @ok was too early, waiting the full <n>s` | The same, when their last `@ok` came just before they failed to move. |
| `follow timed out — resuming anyway` | They never followed after the invite. |
| `comeback already in progress` | You are already on your way to them. |
| `comeback already in progress — fetching <name> first, then you` | You are fetching someone else. They are next. |
| `I can't I'm idle` | You have no walk, loop or Auto-Lair running to come back from. |
| `my party is full — can't take you back` | Your roster holds six. Your client also telepaths them `@forget`. |
| `you're <n> rooms off (limit <m>) — can't come, forget me` | The room they named is farther than **Return distance (rooms)**. `@forget` follows. |
| `can't find a path to you — you're on your own` | No route to the room they named. `@forget` follows. |
| `tried reaching you 2x and couldn't — forget me` | Two walks to them failed already. `@forget` follows. |
| `can't reach <map>/<room> — resuming` | The walk to them could not be started. |
| `path failed — resuming` | The walk to them failed on the way. |
| `no path history to backtrack — going idle` | No room was named and your client has no trail to walk back along. |
| `backtrack path failed — going idle` | The walk back failed on the way. |
| `couldn't find you after backtracking — going idle` | You walked the whole trail back and did not see them. |
| `didn't find you where the trip left you — @comeback with your room and I'll come` | After a train trip, they were not in the room it left them in. |
| `I can't, my loop goes through an exit you can't pass` | **Refusal:** your loop goes through an exit that turned them away. The loop carries on. |
| `I can't yet, an exit on my way turned you away. I'll invite you when my next pass finds you` | **Refusal:** the same in Auto-Lair. Said once. |
| `I can't yet, I'm on a train trip. I'll come for you when the training is done` | **Refusal:** you are on a train trip. Said once; they are fetched when it ends. |
| the Failure message | **Refusal:** the sender is not one of the players listed above. |

| Reply to `@forget` | When |
|---|---|
| `forgetting <name> — resuming` | You were on your way to them. The walk is dropped and what you were doing is put back. |
| `forgetting <name>` | You were not. |
| the Failure message | **Refusal:** the sender is not one of the players listed above. |

**Situations:**

- **What the pickup does to your own movement.** Your loop, Auto-Lair or walk-to is stopped for the walk back and started again afterwards. An `I can't yet` is a wait, not a no: a MudPlay follower's client keeps waiting on it.
- **`@forget` clears both sides.** Each drops the other from its roster memory, so the leader stops inviting them on sight and the follower stops asking.
- **Answers to a pickup the leader started by itself** (a member the game dropped, or one who came back into the game) go by telepath. Answers to a `@comeback` go on the channel it came in on.
- **Master switch off:** a `@comeback` from someone you would go back for is kept, and answered when the switch comes back on if it is no older than **If leading, accept @comeback for (min)**. `@forget` is ignored.
- **The rules a leader follows** (how far, how long, train trips, exits that turn a member away) are under Settings → Party: **If leading, wait only (s)**, **Return distance (rooms)** and **If leading, accept @comeback for (min)**.

### Combat and targeting

#### @kill

Has you attack a named target now, for the sender.

**Forms:**

- `@kill goblin shaman`: everything after `@kill` is the target's name.

**How the target is found:**

- **A player who is in your room:** on a realm with PvP switched on, a fight with that player starts, by your Settings → PvP rules. It doesn't start when PvP is off, when that player is in your party, or when you are already in a fight with another player.
- **Anything else** goes to your combat engine. If a monster in your room has exactly that name (ignoring case), it becomes your target and this round is fought the way your combat settings would fight any single target (weapon, attack spell or backstab). If no monster in the room, as your client last read it, has that full name (a shortened name counts as no match), your normal attack command is sent with the text as typed, and the game decides what it means.

**Who may send it:** a player with **Execute commands**, on any of the three channels. Party membership alone is not enough.

| Reply | When |
|---|---|
| nothing | The target was taken. The attack is the answer. |
| the Failure message | **Refusal:** nothing followed `@kill`. |
| the Failure message | **Refusal:** the sender doesn't hold Execute commands. |

**Situations:**

- **Auto-Combat off:** the attack is made all the same. An order to attack doesn't wait for the toggle.
- **Master switch off:** ignored, no answer.
- **The sending side:** a MudPlay leader who starts a fight with a player says `.@kill <player>` by itself when that is set under Settings → PvP. A fight your client starts on such an order can also switch your evil warnings off first, if you set it to.
- **On your own screen:** the program log records the order with the sender's name.

#### @atkprio and @atkorder

Report or set the two targeting dropdowns of Settings → Combat: **Target Priority** (`@atkprio`, whom you attack) and **Attack Order** (`@atkorder`, when you attack).

**Forms:**

- `@atkprio` or `@atkorder`, bare: report the current choice and list the options.
- `@atkprio 1`: Default. `@atkprio 2`: attack what the party leader attacks. `@atkprio 3 Tank`: attack what the player Tank attacks.
- `@atkorder 1`: Default. `@atkorder 2`: attack last in the party. `@atkorder 3`: attack last in the room. `@atkorder 4 Tank`: attack after Tank. `@atkorder 5`: attack, but not last.

The number must be one of the options. Options `@atkprio 3` and `@atkorder 4` need exactly one more word, a player's name; the others take none. The name is saved as typed and is not checked against anything.

**Who may send it:** a player with **Alter settings**, on any of the three channels.

| Reply | When |
|---|---|
| `@atkprio: <n> <option>. Options: 1=Default, 2=Attack what party leader attacks, 3=Attack what player attacks <name>` | Bare `@atkprio`. With option 3 chosen, the player follows in brackets: `3 Attack what player attacks (Tank)`. |
| `@atkprio: <n> <option>` | The option was set. Option 3 reads `@atkprio: 3 Attack what player attacks (Tank)`. |
| `@atkorder: <n> <option>. Options: 1=Default, 2=Attack Last Party, 3=Attack Last Room, 4=Attack After <name>, 5=Attack Not Last` | Bare `@atkorder`. |
| `@atkorder: <n> <option>` | The option was set. Option 4 reads `@atkorder: 4 Attack After (Tank)`. |
| `@atkprio: command failed. Options: …` (or `@atkorder: command failed. Options: …`) | **Refusal:** not a number, a number that is no option, a missing or extra word, or no character loaded. Nothing is changed. |
| the Failure message | **Refusal:** the sender doesn't hold Alter settings. |

**Situations:**

- **It is saved** to your character at once, the same as changing the dropdown in Settings → Combat, and your combat engine uses it from the next round.
- **Master switch off:** ignored, no answer.

#### @profile

Reports your combat profiles, or switches to one. See **Combat profiles (quick-swap loadouts)** under Settings → Combat for what a profile holds.

**Forms:**

- `@profile`: report which profile is active and which are on standby. Nothing changes.
- `@profile 2`: switch to profile number 2, the number shown on its chip in Settings.
- `@profile fire`: switch to the profile whose name fits best.

**How a name finds a profile.** A number from 1 to the number of profiles is always the profile in that position. Anything else is matched against the profile names, and the closest one wins: a name that equals what you typed, then a name that starts with it, then a name that contains it, then a name that holds every typed word. Between equally good matches the shorter name wins. There is never an "ambiguous" answer: if any name fits at all, one profile is picked.

**Who may send it:** a player with **Alter settings**, on any of the three channels.

| Reply | When |
|---|---|
| `{Current: 1)Fire, On Standby: 2)Cold, 3)Lightning}` | Bare `@profile`. A profile with no name reads `unnamed`. This one reply arrives inside a second pair of braces. |
| `Combat profile <n> (<name>) — multi: <code> · debuff: <code> · normal: <code>` | The switch was made. Each spell slot that is set is shown by its cast code, from `multi`, `multi2`, `AoE-debuff`, `debuff`, `normal`, `alt` and `drain`. With no slot set it ends `no spells set`; a profile with no name reads `Combat profile <n> — …`. |
| `no combat profiles configured` | **Refusal:** bare `@profile`, and you have none. |
| `no combat profile matches '<text>'` | **Refusal:** no profile name holds what was typed. |
| the Failure message | **Refusal:** the sender doesn't hold Alter settings. |

**Situations:**

- **On your own screen:** every switch prints the same one-line summary in your terminal.
- **Master switch off:** ignored, no answer.

### Health, rest and escape

`@health`, `@status` and `@lives` are open to anyone on your party roster with no box ticked, and to any other player with **Query health/status**. All three are taken on telepath, gangpath and say.

#### @health

Reports your hit points and mana as your statline last showed them.

| Reply | When |
|---|---|
| `HP=<now>/<max>,MA=<now>/<max>` | A character with mana, standing. |
| `HP=<now>/<max>,KAI=<now>/<max>` | A kai user. |
| `HP=<now>/<max>` | A character with neither. |
| the same, ending `, Resting` or `, Meditating` | You are resting or meditating. |
| `HP unknown — no prompt observed yet` | Your client hasn't read a statline yet this session. |
| the Failure message | **Refusal:** the sender is neither on your party roster nor holds Query health/status. |

**Situations:**

- **The asking side:** MudPlay telepaths `@health` to each member who joins the party. The answer gives it that member's maximum HP, which the Party window needs to keep their bar moving between `par` readings (see **HP between `par` polls**).

#### @status

Reports what you are doing, where you are, and your ailments, in one line. The parts are separated by semicolons.

| Part | Reads |
|---|---|
| What you are doing | The first part of an `@path` reply (`walking to 6/1249`, `running loop '<name>'`, `auto-lair`, `sailing to <port>`, or a hold such as `Low HP on loop '<name>'`), then `, fleeing`, `, fighting`, `, resting` or `, meditating` when one of them applies and the movement isn't standing still. `idle` when nothing applies. |
| Where | `<room name> (map <m>, room <r>)`, or `location unknown`. |
| How far to go | Only while travelling: `ETA ~<n>s` on a boat, `<n> steps left` (or `1 step left`) on a walk-to. Left out otherwise. |
| Ailments | `no ailments`, or `ailments:` and any of `poisoned`, `blind`, `confused`, `diseased`. |

| Reply | When |
|---|---|
| `idle; Town Square (map 1, room 1); no ailments` | Standing about. |
| `running loop 'Black Fortress', fighting; Dark Hall (map 5, room 10); ailments: poisoned` | In a fight on a loop, poisoned. |
| `walking to 6/1249; Rocky Path (map 9, room 747); 72 steps left; no ailments` | On a walk-to. |
| `Status unknown` | Your client hasn't read a statline yet this session. |
| the Failure message | **Refusal:** the sender is neither on your party roster nor holds Query health/status. |

**Situations:**

- **The asking side:** a MudPlay client that asks sets the sender's ailment chips in its Party window from the last part, so a chip it missed is put right the next time someone asks. Being held is not reported here; that chip follows `@held` and `@ok`.

#### @lives

Reports how many lives you have left.

| Reply | When |
|---|---|
| `<n> lives remaining` | Your stat screen has been read this session. `1 life remaining` for one. |
| `lives unknown` | It hasn't, so there is no number your client would vouch for. |
| the Failure message | **Refusal:** the sender is neither on your party roster nor holds Query health/status. |

#### @heal

Asks a party healer to look at the party's health now and heal whoever is low.

**What it does.** A character with a party heal spell set up sends `par` at once and answers `healing`. The fresh `par` gives your healing engine everyone's current HP, and it then heals by your own thresholds, as it would have at the next poll. The command brings that poll forward; it does not force a cast, and it does not pick the sender as the target.

**Who may send it:** a player with **Execute commands**, on any of the three channels. Being in the party is not enough: a healer has to tick that box for the members who should be able to ask.

| Reply | When |
|---|---|
| `healing` | You have a party heal spell set up and `par` was sent. |
| nothing | You have none of the four party heal spells set (Settings → Party: minor or major, single-target or party). Only healers answer, so a `@heal` said aloud doesn't draw a "can't" from everyone else. |
| nothing | You are on a trainer's or character-creation screen, where a `par` would be typed into the form. |
| the Failure message | **Refusal:** the sender doesn't hold Execute commands. |

**Situations:**

- **The cast is your healing engine's, not this command's.** `par` is sent and `healing` is answered whatever your toggles say; whether a heal is then cast follows Auto-Heal and your party heal thresholds.
- **Master switch off:** ignored, no answer.
- **The sending side:** a MudPlay follower at low HP asks the party with `@heal` in place of running off alone (see **Health: rest, heal, flee**).

#### @hangup and @relog

Both log your character off by sending your **Game exit command** (Settings → BBS + Display, default `=x`). They differ in what happens next:

- **`@hangup`:** you stay off. MudPlay does not reconnect, and when you connect again by hand it does not enter the game for you, so you can read the screen first.
- **`@relog`:** MudPlay dials straight back in and logs the character in again, whatever your reconnect settings say.

**Who may send it:** a player with **Hangup/disconnect**, on any of the three channels.

| Reply | When |
|---|---|
| nothing | Always: the connection is going down. |
| the Failure message | **Refusal:** the sender doesn't hold Hangup/disconnect. |

**Situations:**

- **Disable hangups** (the toolbar toggle) does not stop these two. It stops the client hanging up by itself; a player you gave the grant is obeyed.
- **No Game exit command set:** nothing happens.
- **A realm with a hang-up penalty recorded** (Settings → BBS + Display): the program log notes what the penalty makes of it.
- **Master switch off:** ignored, no answer.

#### @panic

The party-wide bail-out (MegaMUD parity). It is not one of the remote commands: no box in the Players grid covers it and the Talk tab's switches don't touch it. It has two switches of its own under Settings → Party, both off by default.

- **Sending it.** With **Use @panic while leading** on, a leader whose HP crosses its *hang if below* floor says a bare `@panic` on say and then escapes (hangs up, or breaks and uses `sys goto <wimpy>`, as the Health tab says).
- **Receiving it.** A `@panic` said by someone on your party roster makes you escape exactly as your own low-HP emergency would, unless **Ignore @panics** is on. A `@panic` from anyone else is ignored.
- **Disable hangups** still holds: you will `sys goto` wimpy if that is set up, but you are never disconnected by someone else's panic.
- **Master switch off:** a received `@panic` does nothing unless General → **Allow hangup in all-off mode** is ticked.

### Inventory, gear and money

The seven queries (`@inv`, `@have`, `@what`, `@wealth`, `@enc`, `@uses`, `@token`) need **Query inventory**. The actions (`@get-all`, `@drop-all`, `@hide-all`, `@deposit-all`, `@get-stash`, `@equip`) need **Execute commands**. `@share` is for party members. All are taken on telepath, gangpath and say.

Most of them read your client's last copy of your inventory. Until your inventory has been listed once this session (an `i`), they answer that it has not been read.

#### @inv

Lists what you carry that someone looking at you can't see: your pack and your key ring. Worn and wielded gear, your readied light and your coins are left out.

| Reply | When |
|---|---|
| `carrying: <item>, <item>; keys: <key>, <key>` | It all fits one line. With no keys the `keys:` part is left out, and the other way round. |
| `carrying (1/3): <item>, <item>` and so on, then `keys: <key>, <key>` | Too long for one line: the full list is sent over several replies, the pack first and then the keys. |
| `carrying nothing` | An empty pack and no keys. |
| `inventory not parsed yet (type i)` | Your inventory has not been read yet. |
| the Failure message | **Refusal:** the sender doesn't hold Query inventory. |

#### @have

Says whether you hold an item, and how many.

**Forms:**

- `@have rope and grapple`: everything after `@have` is the text to look for.

**How the item is matched.** An item counts when the text appears anywhere in its name, ignoring case: `@have dagger` finds *a rusty dagger*. Your pack, your worn and wielded gear and your key ring are all searched, and every match is added up. A stack counts as its number (25 black diamonds are 25, not 1).

| Reply | When |
|---|---|
| `yes - <n>x '<text>'` | At least one match. `<n>` is the total across everything that matched. |
| `no - nothing matching '<text>'` | No match. |
| `usage: @have <item name>` | Nothing followed `@have`. |
| `inventory not parsed yet (type i)` | Your inventory has not been read yet. |
| the Failure message | **Refusal:** the sender doesn't hold Query inventory. |

**Situations:**

- **The asking side:** a MudPlay leader whose route needs an item asks the party `@have <item>` by itself and has a member with a spare hand it over.

#### @what

Lists the items on the floor of your room, as the room's last *You notice* line showed them. Coins are not listed.

| Reply | When |
|---|---|
| `on the ground: <item>, <item>` | Items were noticed here. This is what `@get-all` would pick up. |
| `nothing on the ground here` | None were. The list is emptied each time you move. |
| the Failure message | **Refusal:** the sender doesn't hold Query inventory. |

#### @wealth

Reports the coins you carry.

| Reply | When |
|---|---|
| `<n> platinum, <n> gold, <n> silver, <n> copper (= <total> copper)` | You carry coins. Only the kinds you hold are named, largest first (runic first of all, under your board's name for it). The total is their worth in copper. |
| `no coins on hand` | You carry none. |
| `wealth unknown - parse inventory first (type i)` | Your inventory has not been read yet. |
| the Failure message | **Refusal:** the sender doesn't hold Query inventory. |

**Situations:**

- **The asking side:** a MudPlay leader about to take the party through a toll asks every member `@wealth` by itself, and routes around the toll when a member can't pay.

#### @enc

Reports your encumbrance.

| Reply | When |
|---|---|
| `Encumbrance <now>/<max> (<n>%) - <bracket>` | Your inventory has been read. `<bracket>` is the game's word for how loaded you are. |
| `encumbrance unknown - parse inventory first (type i)` | It has not. |
| the Failure message | **Refusal:** the sender doesn't hold Query inventory. |

#### @uses

Reports the charges left on your limited-use items.

**Forms:**

- `@uses`: every limited-use item you hold.
- `@uses silvery skullcap`: one item.

**How the item is matched.** Against everything you hold (pack, worn gear, keys): a name that equals the text, otherwise the first name that starts with it, otherwise the first that contains it. Case is ignored.

| Reply | When |
|---|---|
| `item uses - <item>: <n>, <item>: ?` | Bare. A `?` is an item known to be limited-use whose charges have not been read yet. |
| `no limited-use items carried` | Bare, and you hold none. |
| `<item>: <n> use(s) remaining` | The named item, with its charges known. |
| `<item>: charges not read yet` | A limited-use item whose charges have not been read. |
| `<item> isn't a limited-use item` | The item matched but has no charges to count. |
| `no carried item matches '<text>'` | Nothing you hold matches. |
| the Failure message | **Refusal:** the sender doesn't hold Query inventory. |

**Situations:**

- **Paradigm:** the count is the one the game shows when the item is looked at, so an item never looked at reads `charges not read yet`.
- **Stock:** the count is the item's full charges less the uses your client has counted.

#### @token

Reports the daily charges left on your transport tokens (Paradigm).

**Forms:**

- `@token`: every token whose charges your client has read this session.
- `@token arlysia` or `@token token of arlysia`: one token, by the place it goes to.

**How the place is matched.** The whole place name, ignoring case and a leading `the`. There is no partial match: `@token arly` finds nothing.

| Reply | When |
|---|---|
| `token charges — <place>: <n>, <place>: <n>` | Bare. |
| `no token charges read yet (hold transport tokens and log in on Paradigm)` | Bare, and none has been read. |
| `token of <place>: <n> use(s) remaining` | The named token's charges are known. |
| `no charge count for a token of <place> — not held, or not read yet` | They are not. The place is echoed as typed. |
| the Failure message | **Refusal:** the sender doesn't hold Query inventory. |

#### @get-all

Picks up everything on the floor of your room.

**What it does.** One `get <item>` is sent for each item in the room's last *You notice* line, paced so the game takes them all. Coins are left to your cash settings. A cursed item is left where it lies.

| Reply | When |
|---|---|
| `getting <n> ground items` | The pickups were sent. `1 ground item` for one. |
| `getting <n> ground items (leaving <k> cursed: <item>, <item>)` | The same, with cursed items skipped. |
| `re-surveying the floor for get-all` | Your client knew of nothing on the floor, so it shows the room again. Whatever that fresh look notices is picked up, with no second reply. |
| `nothing on the ground to get` | Asked again while that fresh look is still awaited. |
| the Failure message | **Refusal:** the sender doesn't hold Execute commands. |

**Situations:**

- **Too heavy:** your client doesn't check your weight. The game refuses what you can't carry.
- **Master switch off:** ignored, no answer.

#### @drop-all and @hide-all

Empty your character out. `@drop-all` drops things on the floor; `@hide-all` hides them in the room, where only someone who searches will find them. Both take the same four forms.

**Forms:**

- `@drop-all`: everything in your pack that you are not wearing.
- `@drop-all full`: everything you hold: the pack, worn and wielded gear, your readied light, your keys and every coin.
- `@drop-all coins`: your coins only.
- `@drop-all keys`: your key ring only.
- `@hide-all`, `@hide-all full`, `@hide-all coins`, `@hide-all keys`: the same, hidden.

| Reply | When |
|---|---|
| `dropping <n> carried items` | Bare. `<n>` counts every copy in a stack. |
| `dropping everything: <n> items and all coins (<k> denominations)` | `full`. The coin part is left out when you carry none. |
| `dropping all coins (<k> denominations)` | `coins`. |
| `dropping <n> keys` | `keys`. |
| any of the above, ending `(keeping <k> that can't be dropped: <item>, <item>)` | Some items were left out because the game won't let go of them. |
| `nothing to drop` | There was nothing of that kind to drop. |
| `inventory not parsed yet (type i)` | Your inventory has not been read yet. |
| the Failure message | **Refusal:** the sender doesn't hold Execute commands. |

`@hide-all` answers with the same lines, reading `hiding` for `dropping` and `nothing to hide` for `nothing to drop`. Its "keeping" tail reads `can't be hideped`. A word after the command that is not `full`, `coins` or `keys`, or more than one word, is answered with the usage line: `usage: @drop-all [full|coins|keys] (bare = unworn items)`, or the same for `@hide-all`.

**Situations:**

- **Worn gear** is dropped or hidden directly; it is not removed first.
- **A stack** goes in one counted command on Paradigm (`drop 3 black star key`) and one command per copy on Stock.
- **Items the game won't release** (no-drop, loyal, or a cursed item you are wearing) are not sent at all, and are named in the reply.
- **`hide` always names the item.** A bare `hide` would hide you, so it is never sent.
- **Master switch off:** ignored, no answer.

#### @deposit-all

Levels the coin you carry to your keep-on-hand amount (Settings → Cash + Items → **Minimum cash to keep on hand (deposit)**): the excess is deposited, or the shortfall withdrawn.

| Reply | When |
|---|---|
| `depositing <n> copper (keeping <k>)` | You carry more than the amount. `dep <n>` was sent. |
| `withdrawing <n> copper (up to <k> on hand)` | You carry less. `with <n>` was sent. |
| `already at keep-on-hand (<k> copper)` | You carry exactly the amount. Nothing was sent. |
| `wealth unknown - parse inventory first (type i)` | Your inventory has not been read yet. |
| the Failure message | **Refusal:** the sender doesn't hold Execute commands. |

**Situations:**

- **Not in a bank:** your client doesn't check where you are. It sends the command and answers `depositing …` or `withdrawing …` all the same; what the game makes of it is not reported back.
- **The sending side:** a MudPlay leader's stash transfer telepaths this to each member at the bank (see **Moving a stash into a bank**).
- **Master switch off:** ignored, no answer.

#### @get-stash

Searches the room you are in and takes the coin the search shows, up to your own coin weight limits (Settings → Cash + Items). Your per-coin Collect, Ignore and Discard choices don't decide what is taken.

**What it does.** Your client sends `sea`, waits a second and a half for the search to show its coin, picks up as much as your limits allow, and answers about three seconds later with what it took. The reply comes only once the coin is in hand, because the leader that asked moves on when every member has answered.

| Reply | When |
|---|---|
| `ok - took <amount>` | Coin was taken. The amount is spelled out by coin (`3 platinum 20 gold`). |
| `ok - took <amount>, left <amount>` | Some was taken and some left behind. |
| `ok - found no coin here` | The search showed none. |
| `ok - at my coin weight limit, took nothing` | Coin was shown but your limits allowed none of it. |
| `busy with a coin errand of my own - try again shortly` | **Refusal:** you are already on a `@get-stash`, a stash transfer or a train-funding stop of your own. |
| the Failure message | **Refusal:** the sender doesn't hold Execute commands. |

**Situations:**

- **The sending side:** a MudPlay leader's stash transfer sends this to each member, because a search shows hidden coin only to the one who searched (see **Moving a stash into a bank**).
- **Master switch off:** ignored, no answer.

#### @equip

Wears one of your saved gear sets, or saves what you are wearing into one. Gear sets are made in Player Workshop → Equipment Sets.

**Forms:**

- `@equip backstab`: wear that set.
- `@equip restma update`: rewrite that set to exactly what you are wearing now.
- `@equip-all` (or `@equip all`): wear your Default set.
- `@equip-backstab`: the older dashed form, still taken for party members on earlier versions. `@equip-backstab update` works too.

**How a set is found.** The set is named by **one word**, tried in this order: a set whose **keyword** equals it, then a set whose **name** equals it, then the short names `default`, `backstab`, `resthp`, `restma`, `moving` and `bossing`, each standing for the set tied to that trigger. Case is ignored. There is no partial match, and a set whose name is two words can only be reached by its keyword or short name.

**Who may send it:** a player with **Execute commands**, on any of the three channels.

| Reply | When |
|---|---|
| `equipping gear set '<word>'` | The set was found and the swap started. |
| `gear set '<word>' already worn` | The set was found and nothing needed changing. |
| `equipping all (default gear set)` | `@equip-all`, and the swap started. |
| `default gear set already worn` | `@equip-all`, and nothing needed changing. |
| `gear set '<set name>' updated to what I'm wearing (<n> slots)` | `update` worked. `1 slot` for one. |
| `no gear set '<word>'` | **Refusal:** no set answers to that word. |
| `no default gear set configured` | **Refusal:** `@equip-all`, and you have no Default set. |
| `busy equipping` | **Refusal:** a gear swap is already running. |
| `busy equipping — try again when the swap finishes` | **Refusal:** `update`, in the middle of a swap. |
| `haven't read my inventory yet — try again after an 'i'` | **Refusal:** `update`, before your inventory has been read. An unread inventory would empty the set. |
| `usage: @equip <set> [update]` | **Refusal:** nothing followed `@equip`. |
| the Failure message | **Refusal:** the sender doesn't hold Execute commands. |

**Situations:**

- **`update` writes every slot.** Each piece you wear fills its slot (a second ring or bracelet takes slot 2), every slot you wear nothing in goes back to *no change*, and the set's alternate-weapon entries are left as they were. It is saved to your character at once, and an open Equipment tab shows it.
- **Wearing a set this way is a manual gear-up:** it ends a set held from the Equip menu, like using that menu yourself.
- **Master switch off:** ignored, no answer.

#### @share

Splits the coins you carry evenly across your party.

**What it does.** For each kind of coin, the share is your count divided by the number of party members, you included, rounded down. Your client sends `give <n> <coin> to <member>` to every other member on your roster. You keep your own share and whatever doesn't divide.

**Who may send it:** anyone on your party roster, on any of the three channels. No box grants it to anyone else.

| Reply | When |
|---|---|
| `sharing coins among <n> party members` | At least one kind of coin was shared out. |
| `nothing to share (too few coins to split)` | You hold fewer coins of every kind than there are members. |
| `no party members to share with` | Nobody else is on your roster. |
| `wealth unknown - parse inventory first (type i)` | Your inventory has not been read yet. |
| the Failure message | **Refusal:** the sender is not on your party roster. |

**Situations:**

- **Master switch off:** ignored, no answer.

### Training

#### @train

Has you train where you stand. It never walks: it takes for granted that you are already at a trainer. What it trains follows your own Settings → Auto-Trainer boxes:

- **Neither box ticked:** one `train`.
- **Auto-train ticked:** it trains level after level until the trainer turns you away, the money runs out or you have no banked level left.
- **Auto-train stats ticked:** your saved CP plan is applied for the level reached, with or without the box above.

**Who may send it:** a player with **Execute commands**, on any of the three channels.

The reply is sent when the run has finished, not when it starts.

| Reply | When |
|---|---|
| `Trained to level <n>.` | One `train`, and it worked. |
| `Can't train — you've progressed too far for this trainer.` | One `train`, refused by this trainer. |
| `Can't train — not enough money for training.` | One `train`, and you can't pay. |
| `Nothing to train.` | One `train`, and no level was gained. |
| `I've trained <n> levels.` | Auto-train, with at least one level gained. `1 level` for one. |
| `Couldn't train any levels.` | Auto-train, with none gained. |
| either of the two above, with `, unable to train further`, `, out of money` or `, training stalled` before the full stop | Why the run ended. |
| either, with `, <n> remain` | You still have that many banked levels. |
| either, with `, CP allocated through level <n>` | Your CP plan was applied. |
| `busy training` | **Refusal:** a training run of yours is already under way. |
| the Failure message | **Refusal:** the sender doesn't hold Execute commands. |

**Situations:**

- **Not at a trainer:** your client doesn't check. It sends `train` and reports what came of it.
- **No shopping:** a remote `@train` never goes on to the spell shops, as your own training trip can.
- **Master switch off:** ignored, no answer.

#### @ptrain

The **Auto-train party** handshake between MudPlay clients. You never type it. A client acts on it only while its own **Auto-train party** box is on, and takes orders only from its current leader. See **Auto-train party** under Settings → Auto-Trainer for what the trip does.

**Forms**, as the clients send them:

- `@ptrain st <report>`: a member reports its readiness to the leader.
- `@ptrain ask`: the leader asks a member for that report.
- `@ptrain train <level> <map/room>`: the leader orders a member to train up to that level, at the trainer in that room (the room is optional).
- `@ptrain give <copper> <name>`: the leader orders a member to cover that much of another member's fee.
- `@ptrain with <copper>`: the leader orders a member to withdraw its fee at this bank.
- `@ptrain done <levels>`: a member reports it has trained and is back.
- `@ptrain trip on` and `@ptrain trip off`: the leader says a party train trip has set out, or is over.

**Who may send it:** anyone on your party roster, and the leader whose train trip you set out on, even after an exit on the way dropped you from that party. Taken on any of the three channels.

| Reply | When |
|---|---|
| nothing | Always. An order your client can't read is dropped without a word. |
| the Failure message | **Refusal:** the sender is neither on your roster nor the leader of the trip you are on. |

**Situations:**

- **Master switch off:** ignored, no answer.

### Information queries

#### @version

Reports the client's name and version.

**Who may send it:** a player with **Query version**, on any of the three channels.

| Reply | When |
|---|---|
| `MudPlay <version>` | Always. |
| the Failure message | **Refusal:** the sender doesn't hold Query version. |

**Situations:**

- **The asking side:** MudPlay asks each member `@version` and `@level` the first time it parties with them on a given day, to learn whether they run MudPlay and what level they are.

#### @help

Lists the commands the sender may use on you, or describes one command.

**Forms:**

- `@help`: the commands this sender is allowed.
- `@help goto` or `@help @goto`: one command's form and a one-line description. The `@` on the command asked about is optional.
- `@help &@`: describes the relay-back form.

The `@` on `@help` itself is needed, like on every remote command: a plain `help` in chat does nothing.

**Who may send it:** a player with **Query version**, on any of the three channels.

| Reply | When |
|---|---|
| `@version, @health, @status, @lives, @help, …` | Bare. Only the commands this sender would be obeyed on are listed. A long list is sent as several replies of up to 200 characters. `&@<command>` is added when the sender holds Execute commands. |
| `(type @help <command> for its syntax)` | Bare, after the list. |
| `<form> — <description>` | A command was named. |
| `&@<command> — sends @<command> back to you, so your client runs it as if I'd sent it (e.g. &@invite makes me ask you for a party invite)` | `@help &@`. |
| `no such command '<text>' — try @help for the list` | The word is not a command. |
| the Failure message | **Refusal:** the sender doesn't hold Query version. |

**Situations:**

- **The party signals are never in the list.** `@wait`, `@ok`, `@waiting`, `@comeback`, `@forget`, `@share` and `@ptrain` have no box, so the bare list leaves them out. `@help wait` still describes them.
- **The list is the sender's own.** A party member who holds nothing but Query version still sees `@health`, `@status`, `@lives`, `@party` and `@reset` in it, because a party member is obeyed on those.
- **Describing is not doing.** `@help suicide` is answered like any other; the always-refused words apply to commands, not to questions about them.

#### @exp

Reports how the session is going: exp made, exp needed, the rate, and the time to the next level at that rate.

**Who may send it:** a player with **Query experience**, on any of the three channels.

| Reply | When |
|---|---|
| `Made: 474,216,179  Needed: 545,045,125 (L72, +2.14 lvls)  Rate: 14.3 m/hr  Will level in: 1d 14h 12m` | Everything is known. `Made` is the exp earned since the session counters were last reset. `L72` is the level you are working toward, and `+2.14 lvls` is how many levels your exp already covers. |
| the same, ending `Will level in: ready to level` | You already have the exp for the level. |
| `Made: <n>  Needed: <n> (L<level>, +<x> lvls)  Rate: unknown` | There is no rate yet. |
| `Made: <n>  Rate: <rate>/hr (type exp for needed + time to level)` | Your client has not read the game's `exp` line yet. The rate reads `unknown` when there is none. |
| the Failure message | **Refusal:** the sender doesn't hold Query experience. |

The rate is written exactly below 100,000 an hour, in thousands up to a million (`853 k`) and in millions above (`14.3 m`).

**Situations:**

- **`Made` goes back to zero** on `@reset`, on your own Reset session, and at a loop start when **Reset statistics on loop start** is on.

#### @level

Reports your level and experience.

**Who may send it:** a player with **Query experience**, on any of the three channels.

| Reply | When |
|---|---|
| `Level <n>, <exp> exp, <n> to next level` | Your stat screen and the `exp` line have both been read. |
| `Level <n>, <exp> exp, exp-to-next unknown (type exp)` | The `exp` line has not. |
| `level unknown - parse a stat screen first (type stat)` | Your stat screen has not been read yet. |
| the Failure message | **Refusal:** the sender doesn't hold Query experience. |

**Situations:**

- **The asking side:** a MudPlay leader asks the party `@level` by itself before a route through a level gate, and the answer is kept as that player's level on your Players list.

#### @death

Reports your deaths that are not fully recovered, so a party member can help you get your things back.

**Forms:**

- `@death`: the most recent one.
- `@death all`: every one, newest first, up to five lines.

**Who may send it:** a player with **Query deaths**, on any of the three channels.

| Reply | When |
|---|---|
| `death #<n> <date and time>: <status> at <room name> (<map>/<room>), <n> lives left` | One line per death. `<status>` is `active`, `partial` or `missing`. The time is your computer's local time. |
| `<n> more unrecovered deaths` | `@death all`, after the fifth line. |
| `no unrecovered deaths` | Every death on record is recovered, or there are none. |
| the Failure message | **Refusal:** the sender doesn't hold Query deaths. |

#### @quest

Reports quest progress: which quests are marked complete on your Quests tab and, where the game can be asked, how far a quest's flag has got.

**Forms:**

- `@quest`: every quest with something marked complete.
- `@quest good align`, `@quest goodquest`, `@quest 126`: one quest, by name or by flag number.
- `@quest update`: read every flag your current quests use, mark what the flags prove, and report.

**How a name finds a quest.** Tried in this order: a number is taken as the flag number; then the built-in names for the three alignment quests (`good`, `good align`, `good alignment`, `good quest`, `goodquest`, and the same for `neutral` and `evil`); then a quest whose name equals the text; then the first quest whose name contains the text, or is contained in it. Case and extra spaces are ignored.

**Who may send it:** a player with **Query quests**, on any of the three channels.

| Reply | When |
|---|---|
| `Good align 1, 2, 3; <quest> 1` | Bare. Each quest is followed by the numbers of its parts that are marked complete. After twelve quests the rest are counted: `; +<n> more`. |
| `no quests marked complete` | Bare, with nothing marked. |
| `<quest> 1, 2, 3 marked complete` | One quest, where the game can't be asked. |
| `<quest>: none marked complete` | The same, with nothing marked. |
| `<quest> 1, 2, 3 marked complete. Abil: <flag> step <n>` | One quest, with the flag read from the game. |
| the same, ending `(<k> newly marked)` | That reading proved more parts done, and they were marked. |
| `<quest> 1, 2 marked complete. Abil: <flag> unavailable` | The game was asked and gave no value for that flag. |
| `no quest matches "<text>"` | The name matched nothing. |
| `<n> flag(s) read, <k> newly marked — <list>` | `@quest update` worked. `, <j> in progress advanced` is added when part-finished quests moved on. The list is the bare form's. |
| `nothing to check — <list>` | `@quest update`, with no flag left to read. |
| `can't read quest flags here (stock needs sys-god access)` | **Refusal:** `@quest update` on Stock without sys-god access. |
| `a quest flag read is already running` | **Refusal:** `@quest update` while another read is under way. |
| `quest flag read failed` | **Refusal:** `@quest update`, and the read broke off. |
| the Failure message | **Refusal:** the sender doesn't hold Query quests. |

**Situations:**

- **Paradigm:** a named quest's flag is read with `abil`, so the reply comes a moment later and carries the `Abil:` part.
- **Stock:** the flag can be read only with sys-god access for this board (tick Settings → BBS + Display → **Sysop god lives**); it uses `sys god <name> abil`. Without it the reply is the marked state alone.
- **A reading marks what it proves.** A flag that has reached the value a part completes at means that part is done: it is ticked on your Quests tab and the reply shows the updated marks.
- **`@quest update` is not held to once a day**, unlike the login sync, since someone asked.

#### @settings

Reports your eleven auto toggles in one line. It changes nothing.

**Who may send it:** a player with **Alter settings**, on any of the three channels.

| Reply | When |
|---|---|
| `Auto-Combat: On, Auto-Nuke: Off, Auto-Heal: On, Auto-Rest: On, Auto-Bless: Off, Auto-Light: On, Auto-Cash: On, Auto-Get: On, Auto-Sneak: Off, Auto-Hide: Off, Auto-Search: Off` | Always, in this order. With no character loaded, every toggle reads `Off`. |
| the Failure message | **Refusal:** the sender doesn't hold Alter settings. |

**Situations:**

- **It reports the toggles, not the master switch.** There is no remote query for the master switch. While it is off, this command gets no answer, like every other.

### Sharing data between clients

Three commands hand whole data sets from one MudPlay client to another over chat. In each, the client that **gives** checks the permission; the client that **asks** takes the answer because it asked, and only for a short while after its own request went out. So to receive from someone, they must grant you; you don't need to grant them.

- **Loops:** `@loop send`, under **Movement and position**.
- **Boss timers:** `@timer sync`, below.
- **The Roomba item log:** `@roomba sync`, below.

#### @timer

Reports the boss respawn timers you are tracking, or hands them to the sender's client.

**Forms:**

- `@timer`: every running timer.
- `@timer dragon`: the timers of bosses whose name contains the text, ignoring case.
- `@timer sync`: send the timers as data, for another MudPlay client to merge.

**Who may send it:** a player with **Query boss timers**, on any of the three channels.

| Reply | When |
|---|---|
| `<boss> - full 2h14m, next -20% 1h47m, -10% 2h01m, -5% 2h08m` | One line per boss, up to five. `full` is the time to the full respawn; `next` lists each early-spawn window still ahead, soonest first. Paradigm has three windows (`-20%`, `-10%`, `-5%`), Stock one (`87.5%`). |
| `<boss> - full <time>` | Every early window has passed. |
| `<boss> - dead, cleanup in <time>` | A boss that comes back at cleanup. |
| `<n> more active timers - add a keyword to filter` | Bare, with more than five running. |
| `<n> more timers matching '<text>' - refine your search` | A name, with more than five matching. |
| `no boss timers active` | Bare, and none is running. |
| `expired` | A name was given and no running timer matches it. |
| `@timerdata <i>/<k> <data>` | `@timer sync`: up to sixty timers, packed into as many lines as it takes. |
| the Failure message | **Refusal:** the sender doesn't hold Query boss timers. |

Times read `2h14m`, or `45m` under an hour.

**Situations:**

- **The asking side of `@timer sync`:** sending it, by hand or with **Request Timers** on the Bosses tab, opens the merge window that collects the answers. See **Bosses** under *Quests, Bosses, and Deaths*.
- **On gangpath or say** every client that grants you the permission answers, each with its own set.

#### @roomba

Reports where an item was last seen in your gang house, from the Roomba item log, or hands the whole log to the sender's client.

**Forms:**

- `@roomba severed head`: where that item is.
- `@roomba sync`: send the whole log and your labelled gang-house rooms as data.

**How the item is matched.** First the text is looked up as an item name in the game data, so any wording the game data knows finds that one item. Failing that, an item logged under exactly that name. Failing that, **every** logged item whose name contains the text, so a loose word such as `head` can bring back several items.

**Who may send it:** a player with **Query Roomba**, on any of the three channels.

| Reply | When |
|---|---|
| `total: 5x rope and grapple - seen in 15/12 (3), 15/13 (2) - last scanned 2026-08-30 09:22 MST` | One line per matching item, up to five. Each room is followed by the number seen there; after ten rooms the rest are counted (`, +<n> more`). The time is that of the newest sighting, in your own time zone. |
| `<n> more matching item(s) — refine your search` | More than five items matched. |
| `no record of "<text>"` | Nothing in the log matches. |
| `usage: @roomba <item name>` | Nothing followed `@roomba`. |
| `@roombadata <data>` | `@roomba sync`: room labels first, then the sightings, then a last line reading `@roombadata Sync Complete`. One line every 0.8 seconds. |
| the Failure message | **Refusal:** the sender doesn't hold Query Roomba. |

**Situations:**

- **The asking side of `@roomba sync`:** the lines are merged as they arrive, the newer sighting winning, with no review window. A line the game drops costs only the rooms it carried.
- **More:** **Roomba (Player Workshop)** has the log, the sync and what each side must grant.

### Session and profile

#### @auto-all

Works the master switch: the same switch as the **All auto-responses** toggle in the Action menu and its toolbar button. It is the one remote command still followed while the switch is off, which is how a party member switches you back on.

**Forms:**

- `@auto-all off`: switch it off, even when every toggle was already unticked by hand.
- `@auto-all on`: switch it on, giving back the toggles that were ticked when it went off.
- `@auto-all`: flip it.

**Who may send it:** a player with **Alter settings**, on any of the three channels.

| Reply | When |
|---|---|
| `@auto-all: on` | The switch is on after the command. |
| `@auto-all: off` | The switch is off after the command. |
| `?` | **Refusal:** the word after `@auto-all` is neither `on` nor `off`, or no character is loaded. |
| the Failure message | **Refusal:** the sender doesn't hold Alter settings, and the switch is on. |
| nothing | The sender doesn't hold Alter settings, and the switch is off. |

**Situations:**

- **The reply names the switch, not the toggles.** `@auto-all: on` with every toggle unticked is still on.
- **`@auto-all on` with the switch already on and no toggle ticked** switches on your base modes. The button can't do that.
- **Everything the switch stops and starts** is under **The master switch (Auto-All)** in *Automation*.

#### The @auto-… toggles

Eleven commands, one for each auto toggle on your toolbar and in Settings → General: `@auto-combat`, `@auto-nuke`, `@auto-heal`, `@auto-rest`, `@auto-bless`, `@auto-light`, `@auto-cash`, `@auto-get`, `@auto-sneak`, `@auto-hide` and `@auto-search`. Each works the same way.

**Forms:**

- `@auto-combat`: flip the toggle.
- `@auto-combat on`: tick it.
- `@auto-combat off`: untick it.

**Who may send it:** a player with **Alter settings**, on any of the three channels.

| Reply | When |
|---|---|
| `@auto-combat: on` | The toggle is ticked after the command, whether or not it changed. The reply names the command that was sent. |
| `@auto-combat: off` | The toggle is unticked after the command. |
| `?` | **Refusal:** the word after the command is neither `on` nor `off`, or no character is loaded. |
| the Failure message | **Refusal:** the sender doesn't hold Alter settings. |

**Situations:**

- **`@auto-heal` and `@auto-rest` are two toggles.** One works your heal and cure casts, the other your resting. `@auto-cash` is the get-cash toggle and `@auto-get` the get-items toggle.
- **It is saved** to your character, like ticking the box yourself.
- **Master switch off:** ignored, no answer. Only `@auto-all` is followed then.
- **On your own screen:** a change is written to the program log with the sender's name.

#### @reset

Zeroes your session statistics: the same wipe as **Reset session** in the Session Stats window. Your transaction history is left alone.

**Who may send it:** anyone on your party roster, or a player with **Alter settings**. On any of the three channels.

| Reply | When |
|---|---|
| `session counters reset` | Always. |
| the Failure message | **Refusal:** the sender is neither on your party roster nor holds Alter settings. |

**Situations:**

- **The sending side:** a MudPlay leader telepaths `@Reset` to every member when its loop reaches its first room or Auto-Lair starts, if **Reset statistics on loop start** is on, so the whole party's rates count from the same moment.
- **Master switch off:** ignored, no answer.

#### @divert

Forwards your incoming telepaths to another player, so someone else can read your mail while you are away.

**Forms:**

- `@divert Healer`: start forwarding to Healer. Only the first word is used.
- `@divert`: stop.

**Who may send it:** a player with **Divert conversations**, on any of the three channels.

| Reply | When |
|---|---|
| `Now diverting telepaths to: <player>` | Forwarding started, or moved to a new player. |
| `No longer diverting telepaths` | Bare `@divert`, whether or not anything was being forwarded. |
| the Failure message | **Refusal:** the sender doesn't hold Divert conversations. |

**Situations:**

- **What is forwarded:** every telepath that reaches you, sent on as `/<player> <sender> telepathed: <message>`. A remote command that arrives by telepath is forwarded too, and still obeyed.
- **What is not:** telepaths from the player you forward to, and `@divert` lines themselves.
- **The name is not checked.** It is used as typed for every forward.
- **It is not saved:** it ends with a bare `@divert`, or when the client closes.
- **Master switch off:** `@divert` itself is ignored, and a divert already set forwards nothing until the switch is back on. Telepaths that arrive meanwhile are not forwarded later.

### Safety and destructive commands

Three commands can do real harm, and each has its own fences.

#### @do

Sends a command to the game as if you had typed it. This is the highest-trust command: whoever holds **Execute commands** can make your character do nearly anything.

**Forms:**

- `@do rest`: your client sends `rest`. Everything after `@do` is sent, with single spaces between the words.

**Who may send it:** a player with **Execute commands**, on any of the three channels.

| Reply | When |
|---|---|
| `ok` | The command was sent to the game. It says nothing about what the game made of it. |
| nothing | A bare `@do`. |
| `@do suicide is not allowed, use @suicide` | **Refusal:** `suicide` appears anywhere after `@do`. Nothing is sent to the game. |
| nothing | `reroll` appears anywhere in the line. Nothing is sent, and nothing is answered. |
| the Failure message | **Refusal:** the sender doesn't hold Execute commands. |

**Situations:**

- **The two refusals come before the permission check,** so they apply to anyone, with or without the box.
- **Master switch off:** ignored, no answer.
- **`@do break` holds your attack.** A `break` sent this way puts the attack hold on your character exactly as a `break` you type does: Auto-Combat stops attacking the monster it was fighting and attacks nothing else in the room. An attack sent the same way (`@do a orc`) lifts the hold, as an attack of your own would. Your terminal shows `[Attack on <monster> held by Leader's @do break: attack to carry on]` when the hold goes on and `[Attack hold on <monster> ended: …]` when it ends, and the program log and a bug report name Leader as the one who asked. What the hold does and how it ends is under *Combat → The round loop*, **Stopping the fight yourself**.
- **On your own screen:** each `@do` is written to the program log with the sender's name and the command.

#### @suicide

Kills your character, using the suicide password MudPlay captured from your own `set suicide` (Settings → BBS + Display shows it under **Suicide password**).

**Who may send it:** a player with **Elevated Commands**, on any of the three channels.

**The lives rule comes first.** Settings → Other → **Block @suicide commands when lives ≤** (default 5) is checked before anything else, the permission included:

| Reply | When |
|---|---|
| `suicide blocked, <n> lives <= threshold <m>` | **Refusal:** your lives are at or below the number set. |
| `suicide blocked, lives unknown to client` | **Refusal:** your stat screen has not been read this session, so your client can't tell. |

Past that rule, and with the permission held:

| Reply | When |
|---|---|
| nothing | It worked. Your client sent `suicide` and your stored password. |
| `invalid suicide password is stored, unable` | **Refusal:** the game turned the stored password down. |
| nothing | No password is stored and the game confirms none is set: `suicide` is sent. |
| `Suicide failed, password set in game but not stored.` | **Refusal:** no password is stored, but the game has one. Run `set suicide` yourself so MudPlay can capture it. |
| `@suicide already in-flight, try again shortly` | **Refusal:** a second `@suicide` arrived while the first was still checking for a password. |
| the Failure message | **Refusal:** the sender doesn't hold Elevated Commands. |

**Situations:**

- **With no password stored,** your client sends `pro` first and reads the answer up to the next statline to learn whether the game has a suicide password set.
- **The lives rule answers anyone.** Because it is checked first, a player with no permission at all who sends `@suicide` while you are at or below the number is told your lives count, if the Warn setting is on. With the number at `0`, the rule stops only a character whose stat screen has not been read yet.
- **The same rule covers any command with `suicide` in it,** other than `@help` and `@party`: `@have suicide note` is refused the same way while your lives are low or unknown.
- **Master switch off:** ignored, no answer.

#### @dupe

Copies **the sender's own query, Roomba and quest permissions** onto another player, so a trusted player can bring an alt up to speed without you ticking every box.

**Forms:**

- `@dupe Scout`: give Scout the query permissions the sender holds. Exactly one name.

**Who may send it:** a player with **Elevated Commands**, on **telepath or gangpath only**. Said aloud it is dropped without an answer, and the Local control API can't run it.

| Reply | When |
|---|---|
| `<player> now has your query, roomba, and quest permissions` | The copy was made. The sender's one use is spent. |
| `<player> already has all your query, roomba, and quest permissions` | Nothing to add. The use is not spent. |
| `usage: @dupe <player>` | **Refusal:** no name, or more than one word. |
| `your @dupe has already been used` | **Refusal:** the sender has used it before. |
| `you already have your own permissions` | **Refusal:** the sender named themselves. |
| `can't change my own permissions` | **Refusal:** the sender named your character. |
| `unknown player <name>` | **Refusal:** your client has never seen that player. |
| `you have no query, roomba, or quest permissions to copy` | **Refusal:** the sender holds none of the permissions that can be copied. |
| `your player record wasn't found` | **Refusal:** the sender is not on your Players list. |
| the Failure message | **Refusal:** the sender doesn't hold Elevated Commands. |

**Situations:**

- **Only queries move.** What can be copied: Query version, experience, health/status, location, inventory, boss timers, deaths, Query Roomba and Query quests. Nothing that acts on your character (move, execute, alter settings, request invite, hangup, divert) and **never Elevated Commands**, so a player who was duplicated onto can't `@dupe` onward.
- **One use per player.** Each player with Elevated Commands can `@dupe` once. After that it is refused until **you** re-arm it: open that player in Game Data Browser → Players and press **Reset @dupe** under Elevated Commands. The dialog shows when it was spent and on whom. Nothing sent over chat can reset it, and a refused attempt doesn't spend it.
- **It only adds.** The target keeps what they had and gains the rest. Only the permission grid moves, not their party behaviours or notes.
- **An unknown name is refused** so that a typo can't grant trust to whoever later takes that name.
- **It is logged.** Every use and every refusal is written to the program log (who, onto whom, what was granted), and the sender's record keeps who they duplicated onto and when.
- **Master switch off:** ignored, no answer.

### Not commands: the ailment words

The ailment broadcasts `@blind` / `@confused` / `@diseased` / `@held` look like `@`-commands but aren't — they're state announcements the party window reads to mirror a member's condition, governed by your cure/ailment settings rather than the remote-control grid. (Poison isn't broadcast — a member's **poison** chip is read from the `par` party screen's `P` flag, so it lights even for a partymate on another client.)

A member's chip clears on the first of:

- the member broadcasting they're clear (`@ok`);
- the effect's duration lapsing, counted from the moment you see it land on them. The length comes from the game's spell data for the monster that did it (the one the line names when it names one), and covers effects that ride a physical hit, such as a knockdown. When the client can't work out a length it falls back to three minutes;
- the `par` `P` flag dropping (poison);
- **you witnessing any cure land on them** — including one cast by a party-mate using a spell your own class can't cast (a Priest's cure poison, antidote, freedom, cure disease, and heal+cures like curing wind). Cure recognition reads the game's own spell data, so it doesn't depend on you having that cure configured. The reverse holds too: a spell the game data doesn't list as curing an ailment won't clear that chip, even if you put it in that ailment's cure slot.

## Reconnecting

If a member drops, the party can auto-re-invite and reform on reconnect, and a member left behind can `@comeback` to rejoin the leader.

- **A follower who reconnects** within the *If leading, accept @comeback for* time (default 2 minutes) telepaths `@comeback <map/room>` to their leader, so the leader walks straight to them. After a longer drop the party has moved on, and no `@comeback` goes out. MudPlay waits up to 5 seconds after re-entering for your room to be confirmed, since the game can put you back somewhere other than where you dropped. Only if it can't confirm your room does a bare `@comeback` go out, and the leader backtracks along their own path instead, up to its *Return distance* in rooms.
- **A follower the leader moved on without** (held, too heavy, or turned away at an exit the party went through) asks the same way, once. When it asks and when it doesn't is under *Auto-request @comeback when left behind*.
- **A leader** takes that `@comeback` for up to *If leading, accept @comeback for* minutes after the member dropped, even once they've re-entered the realm.
- **If the leader is already backtracking** for that member and their `@comeback` names a room, the leader heads for that room instead.
- **When a member drops, the leader holds in place** for their reconnect. That hold ends once the leader sets off to pick them up (or turns them down), so a leader waiting on a returning member still walks to them.
- **If the leader gave up looking** and went idle, a `@comeback` from that member within the same number of minutes still recovers them, and the leader then resumes the walk, loop or Auto-Lair the search interrupted.
- **If the member is following again before the leader reaches them** — they caught up, or you invited them by hand — the pickup ends there and the walk, loop or Auto-Lair it interrupted is put back. That holds while you have movement paused too: a pickup left waiting behind your pause is dropped the moment they rejoin, so pressing Resume later continues your own walk instead of setting off for them.

---

# Healing & Spells

The health and spellcasting engines keep you alive and buffed — resting, healing, curing, and blessing on their own.

## Health: rest, heal, flee

Healing and resting are two switches. **Auto-Heal** casts your heal and cure spells (on you and your party), your between-round debuffs, and aids a downed party member. **Auto-Rest** watches your HP and mana, and below your rest thresholds sits and rests (or meditates) back up. Each has its own toolbar button, Action-menu entry and Settings → General checkbox, and the combined **Auto Rest / Heal** button turns both on or both off together (it shows lit only while both are on). So you can keep healing by spell while walking on instead of stopping to rest, or rest without spending mana on heals. Fleeing and the emergency hang-up below work while either switch is on. Poison stops a rest; on Paradigm you can still meditate while poisoned, so with mana to recover it meditates instead (on Stock, poison stops both); below your run thresholds it flees; below your hang-up threshold it can drop the connection as a last resort. Every threshold is set on Settings → Health, as a percentage or an absolute value.

**A monster set to Flee starts the same flee.** A monster whose Game Data **Relationship** is **Flee** is run from as soon as it is seen in your room, at any HP, while either switch is on and a walk or loop is running. See *How Flee works* under Game Data → the monster record.

**A hostile blocking your rest, even with Auto-Combat off.** A monster in the room keeps you *in combat*, and you can't rest while it's swinging at you. So when a rest is due (HP **or** mana below its *rest if below*) and an enemy is blocking it — but your HP is still **above** *run if below* — the engine will **fight it to clear the room even if Auto-Combat is off**, then rest once it's dead. If your HP then falls to *run if below* during that fight, it stops and **flees** instead (breaking combat first when *break before running* is set).

This is automatic and needs no toggle — it's the only thing that reaches through an off Auto-Combat, and only to escape the sit-there-and-die deadlock; a healthy character just walks past monsters as before.

**No resting in a room that hurts.** Some rooms damage you every six seconds for as long as you stand in them: the volcano's heat, a swamp's poison, a river without a raft, the frozen north without furs. Every hit breaks a rest or a meditation, so resting there recovers nothing. In such a room MudPlay does not rest or meditate, even below *rest if below*:

- A walk, a loop or a followed leader **carries on**, and the rest starts in the **next room that isn't barred**. Nothing holds the walk in the damaging room, and a follower sends no `@wait` there: it keeps following and asks in the next room it can rest in. A follower dragged into such a room with a `@wait` already out (the leader's wait ran out and it walked on) releases the leader with `@ok` and asks again in the next room that isn't barred.
- **Healing goes on.** The *Heal (rest)* spell is normally cast only while resting; while a rest is owed in such a room it is cast standing instead, at the same threshold and mana floor. Your combat and emergency heals work as always. So a character with healing spells set up heals and moves on; one without them just moves on.
- A character with nothing moving it **stands and heals**. It is not walked anywhere.
- **With the room's counter in effect it is an ordinary room**, and you rest as normal. How the counter has to be had depends on how it works. An item that *negates* the room's spell (the phoenix feather or magma amulet in the volcano, the swamp boots, the fish-helm) protects only while it is **worn**; MudPlay puts a carried one on before you step in (see *A counter that has to be worn is put on for you* under Equipment Manager). An item the room simply checks you have (a raft on the river, the rope and grapple in the ice cavern, the sunstone wristband in the desert) only has to be **held**: in the pack is enough, worn or not. A buff (the desert's waterskin) counts while you carry its source, since MudPlay keeps it raised in those rooms.
- A loop room flagged *rest up here* and a Rest-up event give way to it too.

**Which rooms count is yours to say**, spell by spell, in **Settings → Periodic Damage Room Spells**. To start with, the rooms that hurt on every tick bar resting, and so does the one whose spell starts a timer that ends in death (held breath under water). A room that hurts on a roll now and then (the fungus caves, the Great Pyramid's traps), or only some characters (a level cap, an alignment), is rested in: its damage breaks a rest only on the ticks the roll comes up. The program log says once per room that a rest is being put off and why, and where it finally starts.

**A room's own damage is not a fight.** A line such as *You are seared by the flames for 46 damage!* doesn't put you in combat, doesn't interrupt a rest in the client's bookkeeping, doesn't swap your gear to the fighting set, doesn't send the Enter that looks for an unseen attacker, and isn't a round in Round Totals.

## Casting priorities

When more than one spell wants to fire, the caster follows the priority order on Settings → Spells — by default emergency heal, party heals, downed-ally rescue, self heals, curing, buffing, then debuffing — and won't cast if it would drop you below your mana floors. The one exception is **emergency heal**: it leads the order by default and ignores the mana floor entirely, spending whatever mana is left to save you (see Emergency heal, below).

Because a round's damage lines arrive a beat before the prompt that reports your new HP, the client waits for that confirmed HP before it will spend the round's one between-round cast on a **cure, buff, or debuff** right after a hit lands. Healing is never held this way — so if a round chunks you low, the client won't burn that round buffing on a stale "you look fine" reading and skip the heal; the heal fires the moment your real HP is confirmed.

## Curing and blessing

Configure cure spells for holds, poison, disease, and blindness on **Settings → Spells**; the bless (buff) slots that recast as they expire now live in the **Buff Watchdog** (View → Buff Watchdog — one unified list for self *and* party buffs).

A cure that doesn't take isn't cast over and over. On yourself, the same cure goes out at most once every 15 seconds while the ailment stays. On a party member, each cast that leaves the ailment in place doubles the wait before the next: 15 seconds, then 30, 60, and 2 minutes from there on. That matters most for poison, where a cure spell only takes its own strength off the poison, so a heavy one outlasts several casts. Once the member has stayed clear for 12 seconds the count starts over, and other members are cured as usual in the meantime.

Auto-blessing — self *and* party — is controlled by the **Auto-Bless** toggle and nothing else (it's independent of Auto-Combat and Auto-Rest/Heal). By default the engine buffs while you're **moving or standing idle** (including an idle rest) and holds off **during combat** and **during a triggered recovery rest** (when HP or MA fell below your rest-if-below setting).

Each buff has two opt-in tick boxes that override those holds — **Cast while resting** to also cast it during a recovery rest, **Cast during combat** to also cast it mid-fight — and its own mana floor, **Cast if mana ≥**. They are set in the buff's edit dialog in the **Buff Watchdog** and apply to every cast of that buff, on you or on the party. You can also tell it to ignore, or not announce, specific ailments.

## Mana regen

For mana-regen classes, the caster can rest to regen and — if configured — reroll a poor regen result up to a cap. The mana-regen spell and its reroll settings are configured in the **Buff Watchdog** (View → Buff Watchdog); rerolling works on Paradigm.

## The Spell Book (F2)

Press **F2** to open the **Spell Book** — a read-only reference to your class's spells. It's a lookup companion for the Spells settings, not a place you configure automation: use it to find a spell's cast-code and effect, then type that code into the pickers on **Settings → Spells**. F2 again closes it (or brings it forward if it's buried), and it updates itself as you play (type `spells` or `stat` in the game to refresh what it knows).

The Spell Book lists **every spell your class can learn**, whatever your alignment. Three boxes in the header — **Good**, **Neutral**, **Evil** — filter it: a spell shows when a character of any ticked alignment could use it, and a spell with no alignment requirement shows under all three. All three start ticked. An Evil-only spell needs you to be Outlaw or worse to cast, so a Seedy character can't use one yet; on Stock, Seedy counts as Neutral for spells as it does for gear.

Elsewhere the client still goes by your alignment: the **Settings → Spells** pickers and the casting engines only offer an alignment-gated spell you haven't learned once your alignment matches it. Alignment isn't part of `stat`'s output, so it comes from your own row in the realm's player list, which every `who` that shows you updates (in either of Stock's two `who` layouts, the usual one or the `set style technical` table); until a `who` has shown you on that realm, nothing is hidden on a guess. That reading can lag behind the game between `who`s, so the game's own word wins: any spell your `spells` list (`pow` for a mystic) shows, or that the game says you just learned, is marked learned and offered — in the Buff Watchdog too — even when your last-seen alignment would have hidden it. A spell you've **already** learned never disappears, even if your alignment later drifts away from it — though the game won't let you cast it until your alignment fits again. Where several spells share one cast code — a priest's *balanced*, *exalted* and *tainted word* all cast as `word`, and the alignment quest you did decides which you get — the one you've learned is the one the client uses, and the other two stop being offered as spells still to learn.

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

The **Success %** column is your **chance to land the cast** (as opposed to fizzling) — computed from your **Spellcasting** stat plus the spell's own difficulty, capped at 98% on Stock (100% on Paradigm, and for Kai spells on either realm). (It reads "Success %", not "Difficulty", because the number *is* your success chance — a higher value is better.) It's independent of your level: raising Spellcasting (or gear that boosts it) is what lifts it.

A spell shows **—** when no chance can be stated — you're not a caster class, or your stats haven't been read yet (type `stat` in the game to populate them). Reopen the book after a `stat` to refresh it.

If your class has gear that casts a spell when you `use` it, a **Cast-on-use items** section at the bottom lists what each one casts, its mana, and its charges. It covers items you wear or wield (a wand, a staff, a charged ring) and class items that are carried rather than worn and aren't used up, marked *carried, not worn* (Paradigm's Gypsy deck of cards, which the Buff Watchdog can keep up for you — see *Buff Watchdog*). One-shot consumables such as potions and scrolls aren't listed.

---

# Cash & Items

MudPlay collects coin and loot, banks your wealth, and manages your gear.

## Collecting coin and loot

With the collection engines on, MudPlay picks up coin and flagged items off the ground after a fight, following your per-currency rules (Settings → Cash) and the per-item flags in Game Data. It can skip a pickup that would push you into a heavier encumbrance band, and drop smaller coin to make room for larger. Between inventory reads MudPlay keeps its own running count of your coins and weight (pickups, drops, stashes, deposits, purchases, training fees). When the game shows that count is off — it refuses a coin stash or drop, or a pickup is skipped because you look full — MudPlay sends one `i` to re-read the real figures.

**Monster drops.** The game doesn't announce an item a monster drops; it just lands on the floor. So when you kill a monster whose drop list (Game Data → Monsters) holds an item you've flagged **Auto-collect**, MudPlay re-displays the room (a bare Enter) to see whether it dropped, and a loop or walk waits for that display before moving on. Both halves are needed: **Auto-Get Items** on, and the item flagged Auto-collect in Game Data → Items. Kills of monsters that can't drop a flagged item send nothing extra. A room spell's kill doesn't say which monster died, so the room is re-displayed when any kind of monster it listed could have dropped one.

In a **stash room** the client stashes your excess coin (and any auto-stash items) as you pass through. An **Auto-stash** item goes a whole stack at a time (`hide 9 green dragon hide`), keys on your key ring included; with **Must have minimum** ticked, **Min. to keep** copies stay with you and only the rest are hidden. Having just hidden it, the client deliberately does **not** re-grab that pile — but only the coin a `search` *re-reveals* is skipped. Coin that's plainly visible when you walk in, or that a kill drops on the floor, is still collected there (and, of course, in every ordinary room, including the room right after a stash room).

You don't have to wait for the engines, either: the **Action menu** (and the matching toolbar buttons) has **Get All** and the **Drop ▸**, **Hide ▸** and **Equip ▸** submenus to grab everything on the floor, drop or hide what you carry, or put on any gear set on demand — the local twins of the `@get-all` / `@drop-all` / `@hide-all` / `@equip` remote commands.

## Banking

When your wealth crosses a threshold, MudPlay routes to a configured bank and deposits, keeping a set amount on hand, then **walks back and resumes the loop / Auto-Lair** it interrupted — a loop at whichever of its rooms is nearest (the same as a sell detour), an Auto-Lair where it left off. The trip home uses the **full pathfinder** — the same one that handles your GOTOs — so if the grind area is walled behind a key-door, a hidden exit, a summon-drop key, or a lever/ask-NPC gate, it plans and crosses back *in* rather than stranding at the bank (getting *out* of such an area is easy; getting back *in* needs the gate-aware routing).

Set the bank and thresholds on Settings → Cash. To bank right now regardless of the threshold, use **Action → Deposit All** (or its toolbar button / the `@deposit-all` remote command), which banks down to your keep-on-hand floor.

### Moving a stash into a bank

Right-click a **stash room** on the Navigation map and open **Transfer Stash to Bank**. The fly-out lists every bank in the game data, nearest to where you are standing first, each by the **room it's in** with the bank's own shop name in brackets — `Bank of Khazarad (Bank of Godfrey) — 283 steps` — since several towns' banks share one shop name (a room named the same as its shop is shown once). A bank with no route from where you stand says so. Or start from the other end: right-click a **bank room** and open **Transfer Stash to This Bank**, which lists your stash rooms nearest that bank first, each with its steps from the bank and the coin MudPlay believes it holds. Steps are counted the way the trip will travel, through doors and gates whose key or item can be obtained and across boat crossings, so a bank behind one of those still shows its distance; only a bank with no route at all reads *no route found*. Pick one and MudPlay shuttles the stash's coin to it:

- It stops any loop or Auto-Lair that is running (it does not resume it afterwards), walks to the stash room and searches. If you start it already loaded — carrying more coin than you have room left for, as after a transfer that was cut off on its way to the bank — it goes to the bank and deposits that first.
- It reads the pile the search shows, then takes as much as your coin weight limits allow (**Settings → Cash**: *Don't collect coin if it makes you Light / Medium / Heavy*, *Don't collect coin past 90% encumbrance*, and *Drop smaller currency to make room for larger*). With no limit ticked that is everything you can physically carry. The per-coin Collect / Ignore / Discard choices don't decide what it takes — this is your own stash — but a coin set to **Discard** will still be dropped again, so set it to Ignore or Collect first if your stash holds any.
- It walks to the bank and deposits everything you are carrying above your **Minimum cash to keep on hand** (Settings → Cash) — the stash's coin, anything picked up off the ground on the way, and whatever was already in your pocket. With that setting at 0 it deposits all of it. If your pocket was below the keep-on-hand amount, the stash's coin tops it up first.
- If a search finds the stash already empty while you are still carrying coin above your keep-on-hand amount — the last load of a transfer that was cut off, say — it takes that to the bank, deposits it and ends there, rather than stopping at the stash.
- It goes back for more and repeats until a search shows nothing left, and ends standing in the bank. A notice in the terminal says how much moved and in how many trips.

**Checking on it.** While a transfer runs, the Navigation top bar shows a blue **Stash Transfer** chip. Hover it for where things stand:

- what the transfer is doing right now (walking to the stash, searching, walking to the bank, depositing);
- **what is left in the stash** — what the last search showed, less the load you are carrying to the bank, both as a value and **by coin** (e.g. *6,002 silver*), since it is the number of coins that decides how much a trip carries;
- **about how many more trips** it takes to empty it, at the rate the last trip did, and **roughly how long** that is;
- what has been banked so far.

Before the first search of a transfer it shows the amount MudPlay last knew to be there instead, and the trip count and time appear once the stash has been searched. The time is rough: the first estimate comes from the walk between the two rooms, and from the second load on it uses how long the last full round actually took.

To end it early, press **Stop** twice. The first Stop holds the transfer: Resume carries it on, and starting a walk, loop or Auto-Lair asks whether to finish it first (see [Walking somewhere](#walking-somewhere-goto)). The second Stop ends it, as does **Stop Stash Transfer** on the map's right-click menu while a transfer is running; both stop the walk as well. It also ends on its own, and says why, if nothing can be picked up (you are already at your weight limit), if the bank takes no deposit, or if a walk fails. Whatever you are carrying from the stash at that point stays in your pocket. While it runs the Navigation window shows a **Stash Transfer** chip. On the walks between the two rooms coin on the ground is picked up exactly as your cash settings say. Auto-Get Cash is borrowed for the stash stop only — your saved setting isn't changed.

**With a party.** If you lead a party and tick **Settings → Cash → Stash transfers: party members carry a share too**, the members carry as well. A search shows hidden coin only to the one who searched, so each member has to search for themselves: once you have taken your own load, MudPlay telepaths each member `@get-stash`, which makes their client search and take coin up to *their own* coin weight limits. Each replies when they are loaded; as soon as all have replied (or after 12 seconds, for a member who never answers) MudPlay searches again to count what is really left and heads for the bank. There, after your own deposit, it telepaths each of them `@deposit-all`, so they deposit into their own accounts at that bank, and waits for those replies the same way. The members must be running a MudPlay version that knows `@get-stash` and have given you permission to run commands on them; whatever a member doesn't take, you carry on a later trip.

**From an Event.** The **Stash transfer** event action runs the same transfer on a schedule or a condition — see *Event editor — action types*.

The stash room stays marked as a stash, so a loop that passes through it later will stash there again.

## Equipment sets

Gear is organized into named equipment sets in the **Player Workshop** — a Default set feeds your normal/alternate weapons and armor, a Backstab set feeds your stealth gear — and MudPlay swaps to the right set automatically (and re-equips after recovering a death pile). The **Item Finder** helps you build sets by browsing every equippable item with full stats.

---

## Importing a MegaMUD profile

**Profile Management → Import MegaMUD profile…** (on the top line, left of **New…**) makes a new MudPlay character from a MegaMUD character file, the `.ini` MegaMUD keeps for each character. Pick the BBS and realm it belongs to first, then the file.

Before anything is created you get a **review**:

- **Coming across**: every setting that is carried over, under its MudPlay name, with the value it will have. **Each value can be changed right there** before you import: tick boxes for the switches, number boxes for thresholds and counts, text boxes for spells and commands, a drop-down for party rank. Clear a spell or command box to leave that setting unset. A number outside what MudPlay accepts is brought into range when the character is made.
  - Where MegaMUD had several profiles, the Combat, Health and Spells lines are the active profile's, and a change to one of them changes that profile only.
- **Not coming across**: every setting that isn't, with the reason. Nothing in the file is guessed at: a setting only comes across when it means the same thing in both clients.

What comes across:

- **The auto switches** (Auto-Combat, Auto-Nuke, Auto-Heal and Rest, Auto-Bless, Auto-Light, Auto-Get Cash and Items, Auto-Search, Auto-Sneak, Auto-Hide).
- **Health**: the rest, heal, run and hang-up thresholds for HP and mana, meditate, and the pre / post rest commands.
  - **The pre / post rest commands are highlighted** so you look at them before importing. In MegaMUD they are very often a hand-typed gear swap. When one contains `rem`, `eq`, `wear` or `wea` the line turns red and says so: in MudPlay, gear swaps for resting belong to the **Equipment Manager's Pre-rest HP / Pre-rest Mana sets** (Workshop → My Equipment). Build the set there and clear the box in the review, or the typed command and the gear sets will both be changing what you wear. The highlight follows the box as you edit it.
- **Spells**: the heal, regen, cure, light and when-full spells, by the same short codes MegaMUD uses.
- **Combat**: the attack command, the multi-attack, debuff and attack spells with their mana and cast limits, backstab switches, monster limits and the run settings.
- **MegaMUD's profiles** (Smash / Bash / Attack and the like) each become a MudPlay combat profile, with the one MegaMUD had active made active.
- **Blesses**: self and party blesses become Buff Watchdog slots. A spell listed for both is one slot that does both.
- **Party, Cash, Talk and Other**: the settings with a direct equivalent, including the bank room and your **party rank** (MegaMUD's 0 / 1 / 2 is Front / Mid / Back).

What doesn't, and why:

- **Weapons**: pick them in the Workshop's Equipment Manager, from the game's item list.
- **Wealth limits**: the two clients count wealth in different units.
- **PvP, alert sounds, scheduled events, auto-roam, favourite rooms**: laid out differently or not read yet; set them up in MudPlay.
- **Realm entry / exit commands**: these belong to the realm, not the character (Profile Management → Realm settings).
- **Stats and level**: read from the game with `stat` when the character logs in.
- **Loops**: import those separately (Navigation Management → Import .mp).

**Redial and cleanup settings.** How many times to redial, the pause between tries, what to redial on (failed connect, carrier lost, no response, after cleanup) and the cleanup period belong to the **BBS** in MudPlay, shared by every character on it. They are listed in the review under **BBS (tick box)** and are only written when you tick **Also set the BBS's redial and cleanup settings from the file**. The box starts ticked only when the BBS has no characters yet, so importing a second character doesn't quietly change the board's settings for the first. The numbers are held to what BBS settings accepts (1 to 9999 redials, 1 to 300 seconds, 0 to 600 minutes).

**The login.** MegaMUD keeps the BBS user ID and password in the file as plain text. The review has a tick box to store them as the new character's login for that BBS, encrypted like any MudPlay login. Unticked, they aren't kept.

The import only ever makes a new character. Look its settings over before you play it.

# Player Workshop

Press **F1** (or View → Player Workshop) to open the **Player Workshop** — a tabbed window for managing your character: gear, leveling, quests, bosses, deaths, your character sheet and calculators, and gang-house item sorting (**Roomba**). There are no Save buttons anywhere in it; every edit auto-saves to your profile.

**The tabs.** Related tabs share one entry on the top strip and open as sub-tabs under it:

- **Character Info**
- **Death Recovery**
- **Auto-Train**: **CP Allocation** and **Level Projection**
- **Quest Status**
- **My Equipment**: **Equipment Manager** and **Item Finder**
- **Calculators**
- **Record Keeping**: **Bosses**, **Chest Offload**, **Roomba** and **Realm Rankings**

Each group remembers the sub-tab you left it on while the window is open. Menu entries and shortcuts that open the Workshop on a tab (*Workshop: Bosses*, say) go straight to the sub-tab. The sections below take them most-used first:

## Equipment Manager — gear sets

Your gear lives in **six fixed sets**, each auto-equipped at a specific moment:

- **Default** — your baseline loadout (and backstab fallback). It auto-equips when a **loop or Auto-Lair run starts** (unless you've set up a While Moving set, which then owns your travel gear instead), when you've **finished resting** (recovered to your rest-max — but only if you actually use the pre-rest swap sets below, so a rest that never left Default isn't disturbed), and on **death-pile recovery** if *Auto-Equip on recovery* is on. By default it does **not** swap on combat entry — if a fight interrupts a rest, you keep your pre-rest loadout until you've recovered. You can change that with the **"Don't swap to default upon entering combat"** checkbox (see below). When any set swaps, the loop holds in place until every wear/remove has streamed, so the swap always finishes **before** you step out — you never walk into the next room and change gear mid-fight.
- **Backstab** — worn for the opening backstab round.
- **Pre-rest HP** / **Pre-rest Mana** — swapped in out of combat before resting, and kept on for the whole rest. For **rest** the set goes on first and `rest` follows once it's worn, because every wear stands you up and resting again restarts the rest timer; for **meditate** the gear goes on after you sit, since a swap doesn't break meditation. Either way, MudPlay won't revert to your Default set until you've actually recovered to your rest-max (so a between-round buff or a loot grab that briefly stands you up doesn't flip your gear back and forth). If a regen tick finishes the rest before the game even confirms you sat down, the set isn't put on at all, so you don't walk into the next room in rest gear. A `rest` or `meditate` you type yourself puts the set on too, and it stays on until you stand, when Default goes back on. Slots a Pre-rest set leaves blank keep whatever you're wearing for the whole rest, including a piece you put on by hand; it's the swap back to Default afterwards that re-wears your Default pieces. For gear that has to stay on in one place (a phoenix feather in the lava tunnels, say), use **Location-based auto-equip** below rather than wearing it by hand, and no set will take it off. Two-handed weapons and off-hand items can't coexist, so a swap that changes your hands clears the conflicting piece first either way — a readied two-hander comes off before an off-hand goes on, and a worn off-hand comes off before a two-hander is wielded.
- **While Moving** — worn while you're travelling (a loop, Auto-Lair, or a walk-to) and *not* fighting or resting, so you can carry **+quickness / movement-speed gear** (simple sandals, brown leather boots, the white wolf mantle, and the like). The moment hostiles are recognized it swaps to **Default** and engages; when a walk-to reaches its destination it reverts to Default. **Off by default** — build and enable it to use it. Its own **"Swap to default before entering lairs"** checkbox controls lairs: checked, it swaps to Default the step *before* you enter a known lair (you arrive combat-ready) and swaps back to the While Moving set on the way out into the next non-lair room — a run of adjacent lairs stays in Default the whole way through, without flapping between the two; unchecked (default), you enter in movement gear and swap when monsters appear. By default only a loop, Auto-Lair or walk-to wears it; tick **"Also when moving by hand"** to wear it for typed moves too (`n`, `e`, `sw` … with nothing else running). A typed move has no arrival, so it comes off after **"Seconds without a move before going back to Default"** (default 10) — and a fight still swaps to Default the moment hostiles show up.
- **Bossing** — worn just **before you enter a room the Bosses table marks as a boss room**, so you fight the boss in dedicated gear; when you step out it reverts to **Default first**, then re-layers your **While Moving** set if it's enabled and you're still travelling. **Off by default.** Its own **"Keep on while heading to another boss"** checkbox (shown when the Bossing set is selected) skips that revert on a **walk-to whose destination is another boss room**, so the set stays on from boss to boss. It applies to walk-to's only: a loop or an Auto-Lair run goes back to Default between bosses as before.
  - **On your own, or leading a party:** a walk-to (Go To, a favourite, `@goto`) whose destination is a boss room. If you pick a different destination on the way, or the walk stops short, the set comes off then.
  - **Following a leader:** you have no route of your own, so as you leave the boss room MudPlay telepaths the leader `@path` and keeps the set on only if the answer is a walk to a boss room. If the leader says it **isn't moving yet**, or doesn't answer within 15 seconds, nothing is decided: the set stays on until you next move, then MudPlay asks once more. If that second answer is still "not moving", or doesn't come within 15 seconds, the set comes off. Whenever an answer says the leader is going somewhere that isn't a boss room (another walk, a loop, an Auto-Lair run), the set comes off straight away. It asks again only when you leave the next boss room.
  - A rest on the way still swaps to your pre-rest set, and after it you travel in your usual gear until the next boss room.

You don't create sets, you fill them. Pick a set on the left, then either click **Update from live** (fills it from what you're wearing) or type items into the **Item** boxes on the slot grid — each box only suggests gear your character can actually wear in that slot, and a blank slot means *{no change}* (left as-is). Click **Enable** so automation may use the set, and **Equip Now** to wear the selected set at once. **Clear all** empties every slot of the selected set (weapons and alternates too), after a confirm — handy after copying a character. A ⚠ on a slot means the item picked there is one this character can't wear; it goes away as soon as the slot is emptied or re-picked, including by **Update from live** or **Clear all**.

**Hazard protection is never swapped off.** Some rooms hurt you unless you are wearing their counter: the phoenix feather or magma amulet against magma heat in the volcano, for example. While you stand in such a room, or in a room next to one, a gear-set swap leaves that worn item alone. It doesn't remove it, and it doesn't wear the set's own piece over it; both slots of a ring or wrist pair are held if the counter is one of the pair. The rest of the set goes on as usual, and the Program Log says what was left on. The same goes for a Location rule that ends while you are still in the hazard. Once you are clear, the next set change dresses that slot normally. Without the counter on, MudPlay also won't rest in those rooms (see *No resting in a room that hurts* under Health).

**A counter that has to be worn is put on for you.** An item that *negates* a room's spell does nothing from the pack. When you carry one and aren't wearing it, MudPlay wears it when you come next to such a room and puts the usual piece back once you are clear:

- **Which item.** Any item your game data says negates the room's spell and your character can wear (class, level and alignment are checked). When you carry more than one, a piece that fills a free place goes on before one that pushes something out; after that, the piece that leaves you best armoured, counting what it pushes out. Against magma heat that is the **phoenix feather**, and the **magma amulet** when you have no feather. One you are already wearing is left alone.
- **Where it is on.** In a room whose spell it negates, and in any room with an exit into one. It goes on when you arrive next to such a room, so it is already worn when you step in; it stays on inside, and when you step out but are still next to one; it comes off one room further. The rule is the same whether a walk, a loop or a leader is moving you or you are standing still, so a loop that runs alongside such rooms keeps it on without swapping. One step of the route also keeps it on: where it would come off, it stays on when the very next step a walk or a loop is about to take lands in or next to such a room. A corridor with such a room off every second room is walked with one wear and one restore. The look is one step deep, so with such a room off every third room it still comes off and goes on again, and with no route (steps you type, following a leader) only the plain rule applies. Standing in or next to such a room, it also goes on the moment the item reaches your pack, when the pack is first read, and when you tick the setting.
- **Arrivals with no room next door first** (a teleport, a leader's drag, an exit the map can't follow, a step you type): before a step a walk, a loop, Auto-Lair or an errand takes into such a room the wear is sent and the step waits for the game's answer. A wear that draws no answer holds the step three seconds, once; a second candidate tried for the same step goes out with no wait. A step you type gets the wear ahead of it with no wait, and any other arrival is countered on landing.
- **Sneaking.** A gear command ends a sneak. With **Auto-Sneak on and Auto-Combat off** (sneaking past things you won't fight) MudPlay reads the next three steps of the route it is walking, and puts the counter on in a room with no NPC in it when one comes up before the hazard; the `sn` before the next step takes the sneak again. When no empty room turns up it goes on next to the hazard anyway. With **Auto-Sneak on and Auto-Combat on**, or Auto-Sneak off, there is no such search: it goes on next to the hazard, through a sneak that is being kept, because the room's damage is the worse loss. *Giving the slot back* never breaks a sneak MudPlay is keeping, in any of these cases: nothing is lost by waiting, so it waits (for the backstab to go out, or for a room with no NPC when you are sneaking past things), and if you are back beside the hazard by then it simply stays on.
- **What goes back.** Whatever the gear set in force has for that slot: a set that changed while the counter was on (rest, bossing, Default) is the one honoured, and its swap never took the counter off in the meantime. When no set dresses the slot, the piece the game took off for the counter goes back on (MudPlay reads it from the game's own `You have removed …` line); when a free place was filled, the counter simply comes off. On a finger or a wrist the counter comes off first and the other piece then takes the place it left, and a gear set still dresses the other finger or wrist while the counter is on. A Location rule that wears the same item shares the slot with it: the item is put on once, and the slot goes back only when the rule's area and the rooms that need the counter are both behind you. A Location rule holding the slot with a *different* piece gives way to the counter (damage on every cast outranks the rule) and has its piece back when the counter comes off.
- **If you take it off yourself** where it is needed (or another swap borrows its slot), MudPlay leaves it off while you stay in that room and puts it on again in the next room that needs it. To keep it off, untick the setting.
- **In a fight** the wear stops your attack like any gear command, and the combat engine attacks again at once.
- **A wear that comes to nothing.** You go on with the room's spell uncountered, the Program Log says why, and routes stop counting that item until it is tried again:
  - *the game won't let you wear it* (`You may not wear that item!`, `… may not be worn!`): tried again after a level-up, an alignment change or a stat-screen read;
  - *no more room to wear it*: tried again when a worn piece comes off;
  - *not in the pack after all*, *a cursed piece in its place*, or *no answer at all*: tried again after the next full inventory read (the cursed piece also when a piece comes off). An answer that arrives late still counts, and the piece it pushed out goes back afterwards.
  - *commands held back* (a password prompt, the trainer form): nothing is sent and nothing waits; it goes on when commands go out again.
- **Caught in the middle uncountered.** A route is never planned *into* such rooms on an item that is set aside. If you are already standing in one (the game refused the wear part-way, a leader dragged you in), a new route may still cross rooms of that same hazard, so the walk can get you through or out instead of ending where the damage is.
- **Which rooms.** The game data's damaging room spells that an item negates. The crystal ward is not put on for you: the rooms it is for do no damage.

It is one tick box, **Settings → Periodic Damage Room Spells → Wear the item that negates a room's spell before stepping in**, on by default. With it off nothing is put on, and a route is no longer planned on a negating item you carry but aren't wearing: the route cards then treat such rooms as uncountered. One MudPlay had already put on is still put back as usual. With the **master switch** off nothing is put on *or* put back; when it comes back on, the room you stand in is settled at once. The Program Log (Equipment) says each time what was worn, for which spell and room, and what went back; the bug report lists what MudPlay has on this way, what is set aside and why, whether a wear or a restore is waiting for an empty room, and when one is on only for the next step of the route.

**Per-set behavior options.** Below the set list is a small options area that changes with the set you've selected. Select a **Pre-rest** set to see the **"Don't swap to default upon entering combat"** checkbox (per-character); select the **While Moving** set to see its **"Swap to default before entering lairs"** checkbox and its **"Also when moving by hand"** option with the seconds-without-a-move delay (both described above). Other sets show nothing there.

**Fighting a rest-interrupting mob in your Default gear** (the combat checkbox above) controls what you fight in when a hostile interrupts a rest:

- **Checked** (the default) — keeps the fight in your pre-rest loadout, reverting to Default only once recovered.
- **Unchecked** — fights in your combat gear: the moment a hostile enters while you're resting, MudPlay swaps to your **Default** set for the fight; once the room is clear, if you still haven't reached your rest-max HP and mana/kai, it swaps back to the pre-rest set and resumes the rest. (It only kicks in while you're actually mid-rest in a pre-rest set; a plain fight out on the loop is unaffected, since you're already in Default.)

The **Currently Equipped:** readout beside the set buttons names the last set the client put on this session — whether from Equip Now or an auto-fire trigger (loop start, pre-rest, recovery) — so you can see which loadout you're in at a glance.

The **Equipment Bonuses** panel shows the set's projected AC and stat totals. The projected AC assumes your **configured self-buffs are up** — it folds in the AC (and the Prot-Evil / Shadow / vile-ward effects) your buffs grant on top of the gear, and its tooltip breaks the total down by source (items, race/class/quests, buffs). "Configured buffs" here means everything that lands on you: self-only spells, whole-party buffs you keep on, and single-target buffs you cast on yourself.

**Unwearable items are flagged and skipped.** If a slot holds an item your character can't currently wear — its **alignment**, level, or class requirement isn't met — the slot's label turns **red** with a **⚠** marker, and the engine **skips that piece** on every swap instead of bonking the game with a wear it will refuse. Your alignment comes from your own row in the realm's player list — every `who` that shows you updates it — or, on Paradigm, from `pro`'s evil-point count (alignment isn't part of `stat`); until one of them has shown it on that realm, nothing is flagged for alignment. Since alignment moves while you play (Paradigm drifts toward good every hour unless you've set a floor with `set mineps`, and attacking good monsters moves you toward evil), MudPlay asks the game for your alignment — `pro` on Paradigm, which shows your exact evil points, `who` on Stock — when there's a reason to: a gear set disagrees with your recorded alignment, or the game shows it moved (gear taken off you, a wear refused, someone you attacked forgiving you, or the dark-cloud line while you were Good). There are no timed checks. On **Stock**, a **Seedy** character wears gear as Neutral (evil gear starts at Outlaw) and the "not Neutral" item restriction isn't used, matching the Stock game.

**Evil-only gear can also need evil points.** A plain evil-only item needs you to be Outlaw through Fiend. One with a number (the crimson blood robes' 200, say) needs at least that many evil points. On Paradigm, `pro` gives your exact count. On Stock only your title's range is known (Villain is 120–209, for example), so an item whose number falls inside that range isn't flagged: the client lets the game decide. If the game refuses it, the client learns you're below that number and flags that item, and anything needing more, until a dark cloud says you've gained evil points. The Item Finder and **Find Best** apply the same check while the Item Finder's alignment filter is on your own alignment.

This matters most for **alignment**: MajorMUD force-removes an alignment-restricted item when your alignment drifts past its threshold (the "cleanup EP-zap"), and re-equipping it then fails (*"You may not wear that item!"* for armor, *"You may not use that weapon."* for a weapon). When that happens the client catches the refusal, blocks the slot, and prints a yellow terminal notice — `[<item> skipped, unable to wear — adjust set to correct]` — so you stop repeatedly failing on it. The game's two other refusals block the slot the same way, without being read as a sign your alignment moved: *"<item> may not be worn!"* (the item has no wear slot) and *"You have no more room to wear that item!"*. The second is about the moment, not the item, so that block lifts by itself the next time a worn piece comes off; the first stays until you change the set.

**Change that slot's item** (pick something wearable, or clear it) to lift the block; if your alignment returns and the item becomes wearable again, an alignment-only flag clears on its own.

## Item Finder

The **Item Finder** sub-tab (My Equipment → Item Finder, beside Equipment Manager) is a searchable catalog of every equippable item, with columns for damage, AC, resists, stat bonuses, and more. Filter it by class, slot, level, or any stat, and sort by any column. A **Negates** column lists the spells an item cancels while worn, and the **Negates** dropdown in the stats filters lets you narrow to items that negate a particular spell (it's populated with every spell any item in the set negates; the default `(none)` doesn't filter).

**Attack type and damage columns.** The **Attack type** dropdown (Attack, Backstab, Bash, Smash, Punch, Kick, Jumpkick) sets which attack the weapon columns model, using your current stats and the rest of the gear you're wearing:
- **Swings (W. Spd)**: swings per round with that weapon. Under **Backstab** it reads 1 on backstab-capable weapons, since a backstab is one strike, and blank on the rest.
- **Dmg/Rnd**: average damage per round with that weapon for the selected attack, crits included. The crit chance is your crit rating from level and stats (see *The exact formulas*) plus +Crits gear and Quick & Deadly; the Calculators tab and Monster Intel count crit the same way. It assumes every swing lands: there's no monster to roll against, so treat it as a comparison figure. Monster Intel does the per-monster version.
- **Est. BS Dmg**: your backstab damage range and average with that weapon (e.g. `62-118 (90)`), shown on every backstab-capable weapon whatever the attack type. It uses the same backstab formula as Monster Intel and the Calculators tab. It's different from **BS Min-Max**, which is only the item's own +BS bonus.

A weapon's own +Strength / +Agility / +Stealth replaces your current weapon's in these numbers rather than adding to it.

**Level** is filtered one of two ways, picked by the two radio buttons: **Usable at level** shows what a character of that level can wear (0 = any), and **Level req (min / max)** shows the items whose required level falls between a lowest and a highest (0 switches either end off). Only the picked one applies; the other keeps its numbers, greyed out, and filters nothing. Find Best follows the same choice.

It's a **reference tool**: double-click a row to see the item's full data record, and use the **Gear Finder** panel (the **Gear Finder ▸** button, with **Find Best**) to plan a loadout and read its projected stats. Showing the panel widens the Workshop window by the panel's width so the table keeps its room; on a maximized window it takes the room from the table instead. To actually equip something you found, note its name and type it into that slot's **Item** box on the **Equipment Manager** sub-tab next door.

**Trial damage.** Under the encumbrance lines, the Gear Finder panel works out what the trial set would do with the selected **Attack type**:
- **Accuracy** and **Hit chance** (the target's dodge counted).
- **Damage / hit**: the low and high end of one hit, after the target's damage resist. Hover it for the range before the resist.
- **Swings / round**. A backstab is one strike, so it shows **Backstab damage** and **Expected / stab** instead.
- **Crit chance** and **Quick & Deadly** (the crit a fast swing adds, already inside the crit chance), for a plain Attack only: bash, smash and backstab never crit.
- **Damage / round**: what the round is worth on average, with misses, dodges and crits counted.

It needs a weapon in the trial set for Attack, Bash and Smash, and a backstab-capable one for Backstab; Punch, Kick and Jumpkick are bare-handed. These are the same formulas Monster Intel and the Calculators tab use, with your completed quests' bonuses counted.

**Configure Estimates** (top right of the Gear Finder panel) opens a small window that sets who is swinging and at what. It applies as you type, and stays open beside the Workshop if you want it to:
- **Your character**: your level and the stats the selected attack reads, **with nothing worn**. They start from your stat screen less the bonuses of the gear you had on when you first opened the Item Finder tab, and the trial set's own bonuses are added on top, so a set you aren't wearing is priced correctly. Edit them to plan for a later level or more training. **Reset to my character** puts them back. Class and race are your character's.
- **Target**: armour class, damage resist, dodge and BS defence. Type them, or pick a monster in the lookup box to fill all four from its record. **Clear target** goes back to no target (no armour, dodge or resist to get past). A backstab rolls against a quarter of the armour class plus the BS defence; a monster that sees hidden is flagged, since no backstab can open on it.

Stealth is taken as your stat screen shows it. In the game it also moves with how much you carry, which a lighter or heavier trial set doesn't change here. The settings last until the Workshop closes.

**Find Best searches whatever the results grid currently shows** — not the whole catalog. Leave every filter at its default and it searches everything; narrow the grid first and it searches only that. This is deliberate: a plate-capable class's "best AC" is plate almost by construction (nothing else comes close on raw AC), so without a way to narrow the search there'd be no way to ask for anything more specific.

Want the best AC available in **Leather** even though your class could wear Plate? Set **Armour Type** to Leather, pick **Armour Class** in the Find Best dropdown, and click it — only leather pieces are considered. The same applies to Slot, Weapon Type, Backstab-only, the name filter, and every stat-threshold filter.

- **Missing?** (top right of the slot list) marks each trial slot by what you're wearing right now: **green** if you're wearing that item, **red** if you aren't, **yellow** if nothing is picked for the slot. It stays on until you press it again, and follows the trial set and your worn gear while it's on, so a red slot turns green when you put the item on. Two of the same ring in the set with one on your hand shows one green and one red. It needs your inventory read once (`i`) to have anything to compare against.
- **Hold** a slot first to protect its current pick from the next Find Best pass, so you can layer several searches into one loadout (e.g. Find Best AC for armour slots, then switch the filter and Find Best again for the weapon).
- **Hovering an item inside the open dropdown** (not just the current pick) shows its full stat line, so you can see why Find Best chose something — or compare an alternative — without selecting it first.
- **The Find Best dropdown** covers every worn-stat column in the grid — AC/DR (flat, blur, and combined), Dodge, Magic Resist, ShockShield, VileWard, damage/accuracy (including backstab and the three martial-arts strikes), every attribute and regen, and every skill/resist/protection stat. (VileWard's magnitude is shown as the item's raw value — its actual AC effect scales with your own evil in a way the client doesn't model, so treat "higher" as "more VileWard on the item," not a guaranteed AC number.)
- **Computed damage criteria.** These rank on the damage you'd actually deal, not on one raw stat:
  - **Backstabbing**: the whole backstab loadout in one go. It aims for the **highest backstab minimum** you can reach, and among sets that tie on that, the highest average.
    - **The weapon first.** Each backstab weapon worth considering is tried with the best gear for it, and the one whose complete set comes out highest is picked. That isn't always the weapon with the best minimum on its own: a weapon with a low high end caps how far +min gear can take you.
    - **Then the rest of the gear, chosen together.** On **Paradigm** the lower end of the range is your minimum whichever side it comes from, so +min gear (+BS min, +min damage) raises the minimum only until that side passes the other; from there the search adds just enough to the max side (+BS max, +max damage) to keep the minimum climbing. On **Stock** the min side is always the minimum, so every slot takes what raises it most, and a slot with nothing for it takes what raises the maximum.
    - Stealth and Strength count through the real formula, as for the criteria below, and a **Target weight** is respected.
    - Use it alone or at the head of a search order; slots it leaves empty (nothing there helps a backstab) go to the next criterion.
  - **Backstab Dmg (min)**, **(max)** and **(avg)**: your computed backstab damage. The weapon slot gets the best backstab-capable weapon. Other slots get whatever adds the most backstab damage over your current gear: +BS min/max, +max damage (and +min damage on Paradigm), Stealth, and Strength all count, through the real formula. A backstab whose low end outgrows its high end swaps the two on Paradigm (on Stock the high end rises to match), so stacking +BS-min / +min gear can flip the range. For **(min)** and **(max)**, Find Best tries pushing each end in turn and keeps whichever complete set gives the better result, so it catches a flip that no single piece shows on its own.
  - **Damage / Round (attack type)**: the same idea for damage per round with whichever **Attack type** is selected.
  - The plain **BS Min Damage** / **BS Max Damage** criteria still rank on the item's own +BS bonus alone.

**Effective AC vs Evil** is a separate criterion from plain **Armour Class**: Prot-Evil is a confirmed 1 AC per point against evil monsters (most of what you'll fight), so an item with modest raw AC but a big Prot-Evil bonus can be the better pick even though plain AC sorting would rank it low — this criterion scores `AC + Prot-Evil` so that item shows up where it belongs.

Need more than one stat at once — "best VileWard, then AC, then Spellcasting"? Pick a criterion and click **+ Add to search order** to build a priority list (shown as "Search order: A → B → C" below the buttons); **Find Best** then resolves it highest-priority-first, filling each slot with whichever criterion earliest finds something for it — lower-priority criteria only get a turn at whatever's left over. **The weapon always goes first**: before any other slot, the first criterion in the order that finds a weapon settles it.

This is the same as manually **Hold**-ing a slot and re-running Find Best with a different criterion, automated into one click. **Clear order** empties the list, dropping back to searching by the single dropdown criterion.

The **Target weight** dropdown next to it caps what Find Best is willing to add: pick **None / Light / Medium / Heavy** and it keeps the projected Gear Finder loadout's encumbrance inside that band, using your character's live carry capacity — so "best AC" can mean "best AC that keeps me Light" instead of the raw-highest scorer regardless of what it weighs. **(Any)**, the default, is uncapped. Note that **None** is the encumbrance band called None, the lightest one (about 16% of what you can carry), not "no limit".

Under a target weight the slots compete for the weight:

- **The weapon is picked first**: the best one for the criterion that fits, and its weight comes off before anything else is weighed. It isn't traded off against armour.
- **The other slots are then chosen together**: the combination with the highest total for the criterion that fits in what is left. It doesn't take the best item for the first slot and work down, so one heavy piece can't use up the weight that several better pieces elsewhere would have had. Of two combinations that total the same, the lighter wins.
- A slot the best combination leaves empty stays empty for that criterion; with a search order, the next criterion gets it.

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

**Buy spells** runs the trainer's shop trip on its own: it walks to the shops selling scrolls for spells you can learn at your current level, buys them and reads them. Use it when you'd rather pick the moment yourself — it works whether **Auto-obtain spells from shops** (Settings → Auto-Trainer → Spells from shops) is on or off. The spells you unticked in that list are still skipped, and the money is fetched the same way a train trip fetches it when your purse is short. A running loop or Auto-Lair is stopped for the trip and picked up again afterwards. The notice beside the buttons shows *Buying spells…* while it runs and *Spell buying is complete, bought N spells.* when it ends (the program log names them); if there is nothing to buy, it says so and nothing moves.

**Buffs, curses and the plan.** The plan is measured from your *trained* stats, but the `stat` screen can show them with spells or gear added in. The game marks a stat it is showing that way (bright red; on Stock also a `*` in front), and MudPlay works the trained value back out: an unmarked stat is taken as it stands, and for a marked one it takes off whatever the effects listed on that same `stat` screen add or remove, whoever cast them — a bard's song counts the same as your own spell — and, on Paradigm, your worn stat gear. (Stock gear never changes the number `stat` shows.) A stat that is red only because of your gear is routine and nothing is said about it.

When it can't put a single number on a marked stat — an effect it doesn't recognise, one whose size is rolled or depends on the caster's level, or two spells that share the same line — it says so in amber under the checkboxes, and until a clean `stat` is read the grid **stops checking rows against your stats**: nothing you typed is clamped or rewritten, no row is tidied away as already trained, and the first row of a new plan can't be added (it would start from the buffed numbers). The same happens on Stock whenever any stat is marked, and for a character whose saved `stat` predates this (read `stat` once).

- **Paradigm** lets you train with a buff up, and its `train stats` screen shows the real trained values. **Train now**, **Apply this level** and **Auto-train stats** read the stats and **CP Left** off that screen and type from those, whatever `stat` said.
- **Stock** refuses `train stats` while anything is altering a stat (*Your stats are unnaturally altered!*). MudPlay doesn't send it while your last `stat` shows one altered. **Apply this level** and **Train now** read `stat` again first, so a buff that has since worn off doesn't hold them up. A training trip that reaches the trainer and still finds a stat altered waits there — two minutes unless you change **Wait for altered stats at the trainer** (Settings → Auto-Trainer) — reading `stat` again every 20 seconds, and applies the plan as soon as the stats are clean; if they never clear, the plan row is left for later. If the game refuses anyway, the pass ends there and the row is kept.

**Before saving, the trainer screen must show exactly what MudPlay typed.** If it doesn't — a value the screen refused, or one that landed in the wrong box — MudPlay stops without pressing SAVE and leaves the screen open for you to finish or leave, because spent CP can't be taken back. The program log says what it saw. Automation stays paused for as long as that screen is open, and **Train now** and **Apply this level** do nothing until you close it: `train stats` is never typed into a screen that is already up.

**A row is only cleared once the game shows it was applied** — the trainer screen closed having shown every stat of the row trained, or (when the screen can't be read) exactly the row's CP gone on a `stat` read afterwards. If the points weren't spent, or only some were, the row stays and the program log says why.

**Your own `train stats`.** With Auto-train stats on, opening the screen yourself applies the plan — but only when the screen shows something the plan can raise. Otherwise MudPlay types nothing and the screen stays yours.

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

**Hover any column header** to see how its figure is worked out and what goes into it. Where Paradigm's formula differs from Stock's (Total XP, HP/tick, Accuracy, Stealth, BS Accy), the tooltip shows the one for the realm you have loaded. For a column that's only confirmed on Stock, the Paradigm tooltip says so.

The **HP/tick** column shows both rates as `idle / resting` (resting regen is 3× idle).

The **Stealth (sneak %)** column shows your Stealth and, in brackets, the chance a sneak (`sn`) takes at it, e.g. `84 (84%)`. That's the figure for an empty room with a light load, and it tops out at 95%.

- **Each monster** in the room takes **1%** off, and so does each other player.
- **Carrying over a third** of your weight limit takes **5%** off, over two thirds **10%**.
- **Once your sneak is broken, you can't re-sneak** while monsters are in the room, whatever the chance.
- **If you've marked the Perfect Stealth quest complete** on the Quest Status tab, the column reads **100%** from the level you can do that quest at.

These figures reflect **your current character**: the base attributes carry your equipment's and completed quests' stat bonuses (the `stat` screen is already gear-inclusive), and the table folds your gear's and completed quests' **direct** bonuses on top too — extra max HP / max mana, HP- and MP-regen %, and flat +dodge / +crit / +stealth / +magic-resist / +damage / +carry / **+skill** from items. (Accuracy stays the stat-and-level contribution — a weapon's own accuracy is situational and can't be projected to future levels.)

Mark a quest **Complete** on the Quest Status tab and its bonuses flow in here automatically. **Reset to current** re-seeds it from your live character.

### Choosing which columns to show

**Columns ▾** opens a checklist of every column the table can show, and **your choice is saved per character** — each build keeps the columns it actually plans around. **Lvl** is always on (it's what labels the row); **Reset to defaults** forgets your choice and goes back to the built-in set, including any columns added in a later release.

Seven columns are **off by default**, because they only matter to some builds:

- **BS Accy** — backstab accuracy (see below).
- **Spellcast** — your spellcasting skill (`—` for non-casters). A Mystic's is a flat 500 plus level and magery tier, which no stat changes.
- **Percep** — Perception. Every class has it, and it's INT's biggest non-caster payoff.
- **Thievery**, **Traps**, **Picklocks**, **Tracking** — the four thief skills.

**BS Accy** projects your **backstab accuracy** per level. It reads `—` for a class and race with no stealth source, since that character can't backstab at all. Unlike the plain **Accy** column, this one folds in *everything* the game feeds it — level, stats, your gear and your completed quests — so it's a real number for your current loadout rather than a stat-only partial.

The trade-off: future levels assume **today's weapon**, so re-check it after a weapon swap. The two realms use genuinely different formulas (see *The exact formulas* below), and the client picks the right one from your active game-data set automatically.

The thief four are computed for whatever Race / Class the dropdowns are set to, so they're useful for previewing a rogue build — but a class that was never granted a skill has no score for it in-game. If they don't apply to you, leave them unchecked. (The CP Allocation tooltips are stricter: they only list a thief skill when **your** class or race actually grants it.)

A caution worth knowing: all four thief skills grow on a level term whose **slope halves at level 16**, so they climb quickly early and half as fast afterwards. Past that knee, stat points are what move them.

## What your stats do

Each of the six base stats feeds several derived numbers. The ratios below are the marginal rate (how many points buy one more of the derived stat); the exact breakpoints for *your* character are on the CP Allocation column tooltips.

- **Strength (STR)** — melee **damage** (adds to your weapon's own range: +1 max per 10 STR above 50, and +1 min per 10 above 100 on Paradigm or +2 on Stock; on Stock, STR below 50 also takes max damage away) and **carry weight** (+48 per point, steeper past 100). STR also feeds **accuracy** (~3/pt): on **Stock** for **all** attacks, on **Paradigm** for **bash / smash only** (normal Paradigm attacks get no STR accuracy).
- **Intellect (INT)** — **crit** rating (~10/pt), **stealth** (~8/pt), **magic resistance** (+1 per 4 INT), **perception** (+5 per 8 INT — the heaviest term in it), **all four thief skills**, and, for **Mages and Druids**, **mana regen + spellcasting**. On **Paradigm**, INT also feeds normal-attack **accuracy** (~6/pt); on **Stock** it does not. INT is the widest-reaching stat in the game — it's the only one that touches every utility skill as well as magic resistance, crit and mana.
- **Willpower (WIL)** — **magic resistance** (the heaviest term — resistance is `(INT + 3×WIL) / 4`, so +3 per 4 WIL), **perception** (+2 per 8 WIL), **tracking** (~8/pt), and, for **Priests and Druids**, **mana regen + spellcasting**. WIL does **not** raise your *maximum* mana (that's level × magery level); it scales how fast mana comes back. It feeds **no combat term at all** — not accuracy, damage, dodge or HP — so for a non-caster it buys only resistance, perception and tracking.
- **Agility (AGI)** — normal-attack **accuracy** (~6/pt on Stock, ~3/pt on Paradigm), **dodge** (~3/pt), **crit** (~20/pt), **stealth** (~4/pt), and **thievery / traps / picklocks**. Generally the most broadly useful combat stat.
- **Health (HEA)** — **max HP** (rises nearly every point, more per point the higher your level) and **HP regeneration** (idle, tripled while resting). Both scale with level. It feeds nothing else — no skill and no combat term.
- **Charm (CHM)** — **dodge** (~5/pt), **crit** (~30/pt), **stealth** (~6/pt), **perception** (+1 per 8), **traps** (~4/pt — CHM is weighted double there, the skill it moves fastest), **picklocks** on Paradigm only (~4/pt, also weighted double), **thievery** (~6/pt), **tracking** (~8/pt), and, for **Bards**, **mana regen**. On **Paradigm** it also feeds normal-attack **accuracy** (~10/pt).

**Mana regen scales off one stat per class.** Mage = INT, Priest = WIL, Druid = the average of INT and WIL, Bard = CHM (Mystics use a fixed Kai rate). Maximum mana is level × magery level regardless of stats.

**Realm accuracy differs, and by attack type.** On **Stock**, accuracy is driven by **STR + AGI** for every attack (INT and CHM don't affect accuracy at all). On **Paradigm** it splits by attack: a **normal** attack uses **AGI + INT + CHM**, while a **bash / smash** uses **STR + AGI** (INT and CHM don't help bash/smash). The tooltips label each accuracy line with the attacks it applies to, and the client uses the correct set for your realm automatically.

**Paradigm caveat.** Accuracy, dodge, stealth, damage, crit, carry weight, magic resistance and picklocks are verified for both realms. Perception, Thievery, Traps and Tracking use the **Stock** formula on Paradigm too and aren't confirmed there.

### The exact formulas

For the curious, here are the actual equations behind the numbers above, with everything that feeds them. These are the *base* (stat-and-level) values; your gear and completed-quest bonuses add on top in-game. Division drops the fraction (truncates) unless a formula says "round". `MinHits` is your class's per-level hit dice; `MageryLevel` is your class's magery level; `Level` is character level.

**Max HP** = `HEA/2 + Level×MinHits + (HEA−50)×Level/16 + per-level rolls + RaceHPPerLevel×Level` (+ gear `+MaxHP`). The per-level rolls are random, which is why the projection shows HP as a range.

**HP regen** (per tick) = `(Level+20)×HEA / divisor`, floored at 1, then **×3 while resting**, then **×(gearHPregen% + 100)/100**. `divisor` = **750 on Stock, 500 on Paradigm**. On Stock the natural tick comes every 30 s, and resting adds a separate ×3 tick every 21 s on top of it. On Paradigm the natural amount arrives in thirds, one every 10 s (fractions dropped); the thirds are of the amount before any HP-regen bonus, and what the bonus adds comes on the third one, with the mana tick (`+2, +2, +3` for an amount of 6 with +25%). Resting pays every 5 s instead: those three gains, then three of the whole amount with its bonus (`+7`), counted from lying down — so standing up and resting again starts back at the small gains. Mana comes every 30 s on both. Meditating adds a mana gain every 15 s on both, on top of the 30-second tick; that gain is the base amount, without your mana-regen bonus. On Paradigm every other meditate gain arrives together with the 30-second tick.

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

**Crit rating** = `Level/10 + (INT−50)/10 + (AGI−50)/20 + (CHM−50)/30`, at least 1. **Stock** also caps it at 75; **Paradigm** has no cap there, and on Paradigm a class with a Combat rating of 1–4 gets `5 − Combat` more (a Mage or Priest +4, a Warrior +1, a Witchunter nothing). In a fight, Stock counts crit above 40 one point in three, and Paradigm caps crit at 65.

**Dodge** (raw value, before the vs-accuracy % conversion) = `Level/5 + (CHM−50)/5 + (AGI−50)/3` (+ gear `+Dodge`, + a light-load bonus below 33% encumbrance; on Paradigm, exactly 33% still counts). Accuracy has the same light-load bonus with the same cutoff.

**Stealth** = `StealthLevel + 20 + stat terms`, where `StealthLevel = Level×2` at level ≤ 15, else `Level+15`. The stat terms differ by realm:
- **Stock**: `trunc(AGI/4) + trunc(INT/8) + trunc(CHM/6)` (each term truncated)
- **Paradigm**: `round(AGI/4 + INT/8 + CHM/6)` (summed, then rounded once)

**Max encumbrance** (carry weight) = `STR×48`, plus `STR×36 − 3600` once STR is above 100.

**Magic resistance** = `(INT + 3×WIL) / 4`.

**Melee damage bonus** (STR added onto the weapon's own min/max):
- **Stock**: min `2 × ((STR−100)/10)`, never below 0; max `(STR−50)/10`, which goes negative below 50 STR
- **Paradigm**: min `(STR−100)/10`, max `(STR−50)/10`, neither below 0

**Perception** = `(INT×5 + WIL×2 + CHM) / 8` (+ gear `+Perception`). The only utility skill with **no level term** — it's pure stats, and every class has it. *(Unverified on Paradigm.)*

**The four thief skills** all share one level term, `LevelTerm = Level` below 16, else `15 + (Level−15)/2` — so **the level slope halves at 16**, and past that point stats are what move them. Each is a grant: a class or race that was never given the skill has no score for it. *(Thievery, Traps and Tracking are unverified on Paradigm.)*
- **Thievery** = `(AGI + INT + CHM + LevelTerm×24) / 6`
- **Traps** = `(INT + AGI + CHM×2 + LevelTerm×28) / 7` — CHM counts double here
- **Picklocks**, **Stock** = `((AGI + INT + LevelTerm×10) × 2) / 7` — the doubling happens *before* the divide, so the effective divisor is 3.5
- **Picklocks**, **Paradigm** = `(INT + AGI + CHM×2 + LevelTerm×28) / 7` — the Traps formula, so CHM counts double
- **Tracking** = `(INT×2 + WIL + CHM + LevelTerm×40) / 8` — the heaviest level term of the four, so it grows mostly by levelling

## Quests, Bosses, and Deaths

### Quest Status

A journal of the realm's quests. Expand a card for its requirements, reward, and step checklist; tick every step (or the **Complete** box) to fold its permanent bonus into your character.

**What you've completed is the character's; the guides are the game data's.** Your ticks and completed quests are saved on the character. The names, notes and step write-ups you make in **Edit Quests…** belong to the game-data set, so every realm and character on that set reads the same guides, and a second client open on the set picks up your edits within a second or two. (The guides were kept per realm for a while; each realm's were taken into its set the first time it loaded under this version, the first realm's whole and any later realm's for the quests the set still lacked.)

Inside a step, two kinds of token are **clickable**: a `(map/room)` coordinate (cyan) walks you there — through the same route picker as the map, so a room past a hazard, gate, or teleport offers its route choices instead of just failing — and a single-quoted `'command'` (green) is typed at the game for you, exactly as if you'd entered it in the terminal — so annotate a step with `'ask jorah transport'` and clicking it sends that line.

A quest with no write-up of its own shows a checklist drafted from the game data. A drafted step reads **kill <monster>** (with the room the monster stands in and what it drops for the quest) when the step happens on that monster's death, a backticked command when there is something to type or ask, and **obtain <item>** when the data only shows an item changing hands.

**Edit Quests…** lets you name, hide, or annotate them (type in the **Filter by quest name** box above its list to narrow it to the quests whose name or flag label holds what you typed) — and for the handful of quests that are class-locked in a way the crawler can't see (Magebane, Tarl), its **Restrict to classes** dropdown (a checklist of every class) pins the quest to the ticked class(es), so any other class is marked *Cannot complete*.

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

A respawn-timer tracker. Timers also start on their own: when you kill a boss, or when a boss you saw in its room is gone from a re-display of that same room (someone else killed it). Walking out of the boss's room, even by a room command like `go manhole`, never starts one. A boss in a room too dark to list who is there is still caught: once its name shows in the fight's damage lines, an exp gain that the boss could have paid (anything from a sixth of its exp, for a full party, up to all of it) and nothing else in that room could have is its kill. Where exp can't tell them apart, it is caught when it was the only monster the lines named. **Mark** or **Now** stamps a boss's kill time and the **100%** column counts down to its respawn; on Paradigm the **-5% / -10% / -20%** columns count down to each early-spawn window, and on Stock a single **87.5%** column does the same. A **Last Killed** column shows when each boss's timer was last set; a **Clear** button in the Timer column wipes a running timer, and a **Notes** column holds your own per-boss annotations.

The tab **opens sorted by the 100% timer with running timers on top**, so a fresh open surfaces what's active — and it **re-sorts live whenever a timer starts or clears** (a marked or auto-captured kill), so a boss that just went active floats up into the running group without reopening the tab. Sorting by any timer column — or by **Boss**, **Respawn**, or **Last Killed** — groups cleanup spawns first, then bosses with a running timer, then idle ones. The toolbar runs, left to right: the **filter** box, the **Hit magic** and **Spell immune** dropdowns, the count of running timers, **Stop Before Toggle**, **Grab All Toggle**, **Sync Timers…**, **Import… / Export…** (a shared table), and **Manage Bosses…**, which edits the list. **Stop before** halts automation one room short of a boss (an Event's Walk to can be told to go in anyway, in the Event window); it is **on by default**, except for the bosses that won't attack on sight — the Neutral ones (the cocoons, *kai master*, *storm giant king*, *mayor of arlysia (arachnigoth)* and the like), *sheriff lionheart*, *justicar halford* and *mayor godfrey* — plus the *lord of the hunt*, and the *gigantic black ooze*, which is hostile but can't be avoided once you meet it in the labyrinth. Untick any others you want to walk straight into. **Reset to default**, alone at the right end of the toolbar, puts **Stop before and Grab All** back to each boss's own defaults, for every boss in the table. Those defaults are per boss: **Manage Bosses…** has a **Default Stop Before** and a **Default Grab All** column, so a boss you add or edit carries the defaults you give it (and a boss you add starts on them). The two toggle buttons work on the bosses the filter is showing: they tick the column for all of them, or untick it when every one is already ticked (Grab All skips bosses that have no checkbox).

**Filtering to what you can hurt.** Dropdowns beside the filter box narrow the table by what it takes to damage a boss:
- **Hit magic**, a lowest and a highest: the bosses whose weapon requirement falls between the two. Set both to the same level to see that level alone (1 to 1), or apart for several together (1 to 2). With only the highest set, it is every boss a weapon of that level can hit; **0** is a weapon with no magic.
- **Spell immune … or lower**: the bosses whose spell immunity is that level or lower. The immunity is the level a spell has to be learned at to land (the spell's own level, not yours), so pick the level your attack spell reaches to see what it works on. **0** shows the bosses with no spell immunity.

Each dropdown lists the levels the bosses in your table actually have. **(Any)** switches a dropdown off, and they all combine with each other and with the filter box. Hover a boss's name to see what it needs. For a boss that turns into something else as it dies (Lord Chisholm into the malformation), it is the highest any of them asks for, since the boss isn't dead until the last one is. Boxes and chests need nothing. The bulk toggles act on whatever the filters leave showing.

**Passing through a Stop-before boss room.** A walk that *ends* in such a room stops one room short of it, as it always has. A walk **you start** (a map click, GOTO, a favourite) that only *passes through* one on its way somewhere else now respects the mark too:

- **Another way exists** — the route card offers three choices: **Walk around** the boss room, **Walk up to it and wait** (pre-selected), or **Walk through without stopping**. The stop-before mark itself stays set whichever you pick.
- **It's the only way** — no card: the walk goes up to the room before the boss room and pauses there, with a line on the terminal naming the boss.
- **Going on from there** — press **Play / Resume** and the walk carries on through to its destination; or step in yourself; or start a new walk from that room. A walk begun in the room next to a boss room (or inside it) goes in without stopping, since asking to walk on from there is the go-ahead. It still pauses before the next marked boss room further along.

Walks MudPlay starts on its own (training, selling and banking trips, corpse recovery, the walk to a loop, a party leader's `@goto`) aren't affected and go through as before, as do loops and Auto-Lair.

**Bosses that share a name are listed one each.** The game has bosses with the same name in different rooms on different timers (two *Nahr*, two *master assassin*). Each gets its own row, labelled by what sets it apart (*nahr (spheres)*, *nahr (spaceghost)*, *master assassin (cob key)*, *master assassin (dying assassin)*), with its own room and its own timer; a kill starts the timer of the one whose room you're in. Every boss's respawn time is read from that boss's own monster record, so a name shared with another monster elsewhere doesn't change it. A boss that only appears when another dies shares that one's row and timer: *lord chisholm (malformation)* is Lord Chisholm, whose death summons the malformation, so the hour starts when he dies; *mayor of arlysia (arachnigoth)* is the mayor, whose death summons arachnigoth. A **?** in the Respawn column means the boss has no timer of its own: the *giant toad-beast* is summoned by placing the orbs in its shrine, and its Notes say so.

**The boss list belongs to the realm; Stop before and Grab All belong to the character.** Bosses you add, remove or edit (rooms, notes, the per-boss defaults and the rest) are the realm's: every character on it sees the same list, and two realms keep their own even on the same game data. The two ticks are **each character's own**: ticking Stop before or Grab All on one character changes nothing for another, even on the same realm. A boss you haven't ticked either way follows its own default from Manage Bosses…, and **Reset to default** puts this character's ticks back to those. The first time a character loads under this version it starts from the ticks the realm's list held. (The list itself used to be kept with the game-data set; each realm took a copy of its set's list the first time it loaded under that version.)

**Column widths you drag are remembered** for the character, so the table opens the way you left it.

**Boss timers belong to the realm, and every character on it shares them.** Run two characters on the same realm, a client each, and a kill on one shows on the other within a second or two; both clients keep each other's timers. (Two realms never share timers, even on the same game data.)

**Double-click a boss** to walk to it. A boss with a **single** room walks there straight away; one with **several** rooms opens a picker listing them **nearest first**, where **Run** starts the walk and **Load** only arms the destination (so you can start it later, the same as a GOTO's Load). (Double-tapping the Stop-before / Grab-All checkboxes just toggles them — it never fires the walk.)

Tick **Grab All** (default off) to blindly grab a boss's loot the instant it's available — a "throw a get at everything" spray straight from game data, never a corpse scan. What it does depends on what the boss's name resolves to:

- a **monster** — the instant it dies, `get` every item in its drop table (one `get <item>` per item it could drop, percentages ignored); works for cleanup bosses too (no timer needed). Some bosses cast a silent spell as they die that leaves the room unable to act until it runs out (about three seconds), and the game throws away what is sent before then. For those the grab waits out that spell and is sent once, right after; you stay in the room meanwhile (the nav line reads *waiting to loot*). Coins the kill dropped are picked up then too.
- a **boss that dies into another monster** — many bosses are two monsters: the one on the list dies and its death summons the next (Lord Chisholm becomes the malformation, the mayor of Arlysia becomes arachnigoth, the neutral Lallim Whitemane becomes the hostile one), and the loot is on the last. The grab goes out at each of those deaths, for the drops of the monster that just died, so the malformation's items are grabbed when the malformation dies. Only the first death starts the timer. Where the two share a name, the exp the kill paid tells which one died; when it can't, the grab goes for both monsters' items.
- an **item** that just sits in the room (a box, e.g. a bogwood box or Pastor Lander's box) — `get` it every time you **walk into** the room.
- **neither** (an unresolvable name — a touch-to-awaken mechanic like Iceforge) — Grab All doesn't apply, so its cell shows a muted dash reading *"Cannot resolve to a specific monster or item"*.

**Sync Timers…** shares respawn timers with other MudPlay users. Pick a channel (Gang, Telepath to a named player, or Local say) and click **Request Timers**; the client sends `@timer sync` on that channel, and any other MudPlay user who receives it (and grants the `@timer` remote command) replies with their active timers. Most arrivals need no action from you:

- a timer for a boss you track but have **no** timer for is **adopted automatically**.
- one that matches what you already hold is left alone (co-kills marked seconds apart count as the same timer).
- a genuine **conflict** — a boss where someone's timer disagrees with one you hold — shows your timer beside a **Keep ours** default plus a pick button per differing responder; choose whose to keep, then **Apply Selected**.
- a boss a responder tracks that **you don't** also asks — adopting it **adds the boss back to your list** (recovering a catalog boss you'd removed).

Bosses are matched by the monster itself (not by room), so it works even if you and they pinned different rooms. You don't have to open the tab first: sending `@timer sync` by hand — a telepath (`/name @timer sync`), a gang broadcast (`bg @timer sync`), or a say (`.@timer sync`) — **auto-opens this merge window** and starts collecting.

### Chest Offload

A helper for cashing in boss chests: the **Record Keeping → Chest Offload** sub-tab. The treasure-chest icon beside a carried container on Character Info jumps to it. It lists the containers you're holding; click one and it reads your inventory (`i`), sends `open`, then reads it again, and lists only what **that open** added, grouped into the fewest shops. Items you were already carrying, picked up between chests, or picked up while a chest was being read never show as chest loot. Typing `open <chest>` in the terminal (or `op`, as the game takes it) counts too (with the inventory as last read standing in for the first `i`; `open n` and the other directions are doors, never a container), and so does a chest opened while the Workshop is closed. A container opened for you by its **Auto-open** setting (Game Data → Items; see *The item / monster override editor*) is listed the same way, with the one inventory read after it. The read that counts is the one asked for after the open: an `i` that was already on its way is passed over, and if none comes back within a few seconds the open is logged as not read and nothing is listed for it. Chests opened one after another add up on the one list without counting anything twice — each open's "before" already holds the last chest's loot. Clicking Open on a second chest while the first is still being read waits its turn, and a chest typed open in the middle of another's reads is caught by whichever read sees its loot (both are then announced together, e.g. `oak chest + iron chest dropped: …`). **Coin gained** is measured the same way, for each open on its own, so money from selling loot never counts as chest coin, and shown by denomination, most-valuable first.

- **Sell Tour** — one button that sells the whole list. It first reads your inventory, then shows every shop in route order with exactly what it will sell there, what that's worth, and how far each leg is (steps and walking time from the shop before; fights on the way aren't counted), plus the whole tour's walk — so a long trip for one cheap item is plain before you go; **Start tour** walks to each shop in turn (a running loop or Auto-Lair stops) by the default route without asking about shortcuts — but whenever a room you marked **Avoid** is on the shortest route there, it stops and shows the route cards so you choose (go around it, or through it); it also asks when there's no route without obtaining something — sells that shop's items, waits for the game's `You sold …` (or a few quiet seconds) and goes on to the next. **Cancel tour** stops it. If a walk is called off before it starts — you cancel the route cards, or don't resume a trip the client was holding — the tour ends there and the line above the shops says so. **It never sells what you already had**: every quantity is capped, right before the `sell` goes out, at how many of that item the chests gave and you still carry — with 3 moonstones of your own and 2 from a chest, at most 2 are sold. A shop whose items were all taken off the list (✕), sold, dropped or hidden after you started is skipped, not walked to. The same cap applies to **Sell All** and each item's **Sell**, which are just a one-shop tour.
- **Say loot to the room** (checkbox, **off** by default, saved with the character) — ticked, after each open MudPlay says the chest's items and coin in the room (`.oak chest dropped: 2 moonstone, ruby, 5 gold`, carried over onto more lines when the list is long), so everyone there knows what came out. It covers a chest opened by typing or by Auto-open as well, whether or not the Workshop is open. Unticked, nothing is said and the list is kept all the same. With the **master switch (Auto-All) off**, nothing is said and MudPlay does not send the `i` that reads what a chest gave: your `open` goes out as typed and that open ends as *not read*. What it gave is in your pack and shows at your next `i`, but is not put on this list (open the next chest with the switch on to have it listed). The **Open** button is refused outright while the switch is off, with the terminal notice `[Chest Offload cannot open and read a chest: the master switch (Auto-All) is off]`: nothing is sent.
- **The list keeps** — it's saved with your character, so closing the Workshop, or the client, doesn't lose it. An item leaves the list when the game confirms it sold, dropped, hidden or given away — typed in the terminal, pressed here, or sent by Auto-sell, Auto-discard or a stash, and whether or not the Workshop is open — when an inventory read shows you no longer carry it, or when you take it off with its **✕** button (it stays in your pack). Part of a pile leaving lowers the count; the row goes at zero. The list goes by item name, so with 3 potions of your own and 1 from a chest, selling any one of them takes the chest's row off. The coin tally is one figure for the whole list: it clears when the last listed item of the last chest has left (the program log notes the amount), not when one chest's items go while another's are still listed, and a chest that gave only coin keeps its coin in the tally until then. **Clear list** empties the list and the coin tally.

- **Pricing and selling** — each item has an editable sell quantity (keep some, sell the rest) priced at a **charm** picker, its own **Sell** button, and a **Drop** button that drops that one item's **whole held stack**. **Sell** sells right away when you're standing in the item's shop, and otherwise walks you there first and sells on arrival; **Drop** acts where you stand. The list reconciles against the game's own confirmations: the row shrinks (and clears at zero) only when the `You sold …` / `You dropped …` / `You hid …` (or the line for a give) actually lands. A **Total to sell** figure sums everything selected across all shops.
- **Picking a shop** — when an item is sold by more than one shop it gets a **⇄** button. The plan already assigns it to whichever shop keeps the trip to the fewest counters, but click **⇄** to see the other shops that buy it (each with name, map/room, and current walking distance), pick one, and hit **Change** to move it there.
- **The selling trip** — each shop header shows a running total, its map/room, and the steps and walking time from where you stand, and **walks you there** when clicked. Its **Sell All** button sells every item in the group (batched on Paradigm, paced on Stock): right away when you're standing in that shop, otherwise it walks you there first by the default route (a running loop or Auto-Lair stops; the route cards come up whenever an Avoid room is on the shortest route), and sells when the walk arrives. While it walks, a line above the shops says where it's going; if the walk is stopped or fails, or you start a different walk, nothing is sold and the line says why. It also has a **Drop All** button (its drops go out a few at a time, like the Action menu's Drop All, so a big group can't overflow the game's command limit). The shops are ordered into a short nearest-first trip using the same routing the walker uses (respecting avoid rooms, usable teleport gates, and item/hazard/boat gates).
- **Drop hides when you've asked for that** — with **Settings → Other → Hide items when discarding** ticked, **Drop** and **Drop All** send `hide <item>` instead of `drop <item>` (one `hide 3 moonstone` on Paradigm, one `hide` per copy on Stock), so the loot you're getting rid of lands concealed in the room. Those hides stay out of the Transaction history, as auto-discard's do. A hide is only sent for copies that are in your pack and not already on their way, so pressing **Drop** again before the game has answered sends nothing (the line above the shops says so). If the game refuses a hide because the room has no room left for hidden items (`There is no room to hide <item> here.` — this only happens on Stock, whose rooms hold only so many; Paradigm rooms have no limit), the item stays in your pack and on the list, the line above the shops says so, and MudPlay **sends the hide again in the next room you enter**, and in each room after that, until it lands. It is never dropped in its place. Selling the item, dropping it, hiding it yourself, taking it off the list (**✕** or **Clear list**) or starting a **Sell** for it calls the waiting hide off, so the copies you keep stay yours.

### Death Recovery

Your death history. **How did I Die?** replays the backscroll from the moment of death, in the colours you saw it in and in your terminal font (select and copy with the mouse or Ctrl+C). Deaths recorded before colours were kept show in plain text. The saved log file keeps the colours as ANSI codes, so it also reads in colour in `less -R`. **Recover Now** walks to the death room and grabs the pile (or toggle **Auto-Recover Deathpiles** to do it automatically).

Recovery matches your realm: on **Paradigm** it recovers your `corpse` in one command; on **Stock**, where death scatters your items loose on the floor (and overflows into other rooms when that floor is full), it `get`s each item back.

**An arena death leaves no pile.** Dying in an arena takes no item, coin or key, so there is nothing to go back for. The death is still in the history (time, room, lives), marked **Recovered** with the note *Arena death: nothing was lost.* and with nothing listed under *Equipped at death* or *Inventory lost*. Nothing walks back to the room, no `recover corpse` or `get` is sent, and nothing is re-worn for it; gear that was waiting for Auto-All to come back on is still put on then. On **Paradigm** an arena death is one in an arena room (the Training Grounds, the Arena Practice Rooms, the Dwarven Arena, the arena passages of map 11). On **Stock** it is a death the game answers with *But, because you were in a colliseum, you have been saved.*: with the board's arenas switched off, a death in one of those rooms prints the ordinary lines and drops a pile like any other. MudPlay still reads your inventory once at the graveyard, as after any death.

On **Paradigm** the corpse is recovered the moment you arrive in the death room, whether the walk ends there or only passes through: the room's own display is read as it prints. If the room is shown without your corpse, the pile is marked **Missing** (looted or decayed), with a note saying which display that came from, and nothing is sent. Only the death room's own display decides that: the floor of the next room you walk into, or of a room you `look` into, never does.

- In a **dark** death room, one you walked into **blind**, or one MudPlay placed you in without the game showing it (a manual locate, the room you were in when you last logged off), nothing has been read, so nothing is concluded. The recovery waits for the room's next display (a light or your sight back, then a look). When that display comes, a corpse on its floor is recovered at once; a floor without it only counts once the display's exits line has shown it really was the death room, so being moved somewhere else in the meantime (a teleport, fear, a summon) never marks the pile Missing.
- **Recover Now** from inside the room looks again: it recovers the corpse if the look shows it, and marks the pile Missing if the look shows the room without it, however long the game takes to answer. A `look` through an exit that is still unanswered is not mistaken for it.
- **Recover Now on a pile already marked Missing** looks at the room again: from inside it as above, and from anywhere else by walking there and reading the arrival. If the corpse is there after all it is recovered; if not, the pile stays Missing with a fresh note.
- If your corpse shows up on the floor after all while you are still in the room, the Missing is taken back and the corpse recovered.
- **Gear handed back for a Missing pile still counts**, from anyone, in your party or not, and at any time: a corpse gone from the room is what it looks like when someone has picked it up for you. The item is struck off the most recent pile that still lists it, so a later death that dropped nothing doesn't get in the way, and the record reads as handed back (Recovered, or Partial while pieces are still out) with a note naming who gave it.

**What the record holds.** *Equipped at death* is what you were wearing. *Inventory lost* is everything else that dropped: your pack, the light you had lit, your keys (each with its count) and your coins. Recovery waits for all of it. A recovered light goes back into your pack and is **not** lit again by the recovery; Auto-Light lights one the next time a room needs it, as it would any light you carry. Keys go back on your key ring as they are picked up. A light that burned out before you died is not on the record: MudPlay takes it off what you hold when the game says it went out. A death MudPlay didn't see (a hang-up on a board that kills for it) records its pile by the same rules.

**A spare of something you were wearing is part of the pile.** Wearing a longsword with another in your pack, you lost two: both are listed and waited for, one goes back on when it is recovered and the other stays in your pack. On Stock, the first copy to come back is the one that goes on.

With **Auto-Equip on recovery** on, MudPlay re-wears everything you had on when you died — and if a hostile is in the room when the pile comes back, it does this **combat-aware**: grabbing the pile doesn't interrupt the fight, but wearing gear does, so it puts a few pieces on between combat rounds (weapon first, then armour heaviest-first) and keeps swinging in between, then equips whatever's left the moment the room clears.

**It follows the Auto-All master switch.** If you recover the corpse yourself while Auto-All is off, nothing is worn for you — the gear stays in your pack so a burst of wear commands can't pin you in a room you're trying to leave. Switch Auto-All back on and the held pieces go on then (paced the same way if something hostile is there), leaving out anything you've already put on by hand.

**Gear handed back by another player.** In a party it's often the leader who recovers your corpse and gives the items back, but anyone's hand-back counts. Each item handed to you (*Nineteen just gave you shimmering white robes.*, or Paradigm's other wording, *Nineteen gives you shimmering white robes.*) is struck off your open deathpile; once the hand-off goes quiet the pile is marked **Recovered** (or **Partial** if pieces are still missing) with a note naming who returned it, and — with Auto-Equip on recovery on — the gear you were wearing goes back on, paced round by round if you're in a fight. Items that weren't part of the pile are ignored.

On **Stock**, items that spilled into other rooms are chased down too. A room's floor holds 17 objects; when the death room is full the game puts each further item in the next room with space, trying the exits in a fixed order (north, south, east, west, northeast, northwest, southeast, southwest, up, down), going on from each room it tries before coming back for the next exit, up to five rooms out. It doesn't care about doors, locks or hidden exits, so gear can land behind one. It passes over an exit that changes map.

**The spill sweep** runs for a recovery you asked for: **Recover Now** (from inside the death room, or at the end of the walk it starts), or a walk-to of your own that **ends** in the death room with Auto-Recover on and nothing else driving the character. A Recover Now whose walk you stop, or whose route cards you close, is called off: coming into the death room later is not it arriving. A detour on the way (buying a light, a sell trip, a fight with a player or a flee from one) doesn't call it off; the walk is picked back up and still recovers when it arrives. Pressing Recover Now again while that walk is under way doesn't either, whatever you do with the second set of route cards.

1. It grabs what's on the death room's floor. In a **dark** death room, or one you walked into **blind**, nothing can be read: the grab waits until the room has been seen (a light, your sight back, then a look), and no sweep starts before that.
2. It looks through each exit of the death room (no movement). A neighbour seen holding your items is visited first.
3. It walks the rooms in the game's own spill order, getting whatever of yours is on each floor, and stops as soon as nothing is missing. The normal walker makes each trip, on foot, so it opens doors, searches out hidden exits and disarms traps on the way. A room it has no route to on foot, or whose route is more than 12 rooms, is skipped, and so is one the game won't let it into. A room that is **dark** when it gets there (after a death you may have no light) is still walked into, but nothing can be read on its floor; the pile's note says how many of the rooms it stopped in were dark, so carry a light and run it again.
4. If every one of those rooms is tried and items are still missing, it searches the rooms you walked through before you died, starting with the death room. When no floor nearby had space the game hides the item in one of them, where only a search shows it. Each room is searched up to twice (a search can miss a hidden item), once any fight in it is over; with Auto-Search on, its own search on entering the room counts as one of the two. A room you've marked as a **stash room** is never searched.
5. It walks back to the death room (if the way back is 12 rooms or fewer; otherwise it stays where it is) and wears what it got back, with Auto-Equip on recovery on.

- **What a search can do besides.** A search ends a sneak; with Auto-Sneak on you sneak again on the next step. Coins a search uncovers in a room that isn't a stash room are picked up by Auto-Get Cash like any coins in view. The sweep only gets items named on your missing list, but it can't tell your hidden torch from someone else's: a like-named item hidden there by another player would be taken.
- **Limits.** One sweep walks to at most 12 rooms. It has 10 minutes up to its walk back and 2 more for the walk back. Most death rooms have more than 12 rooms within reach of a spill, so the search of the rooms you walked through is only reached where the death room sits in a small pocket; that is also the only place the game hides items there. Whatever is still missing afterwards was most likely picked up by someone else. The pile stays **Partial** with a note saying why the sweep ended. Recover Now runs it again from the start, over the same rooms in the same order: a second run doesn't reach further out than the first.
- **It waits.** It doesn't start while something hostile is in the death room, during a rest, while you have movement paused, or while **Auto-All** is off; it starts by itself when those clear, unless you press Stop meanwhile or ten minutes go by first. It also waits for a move that is still on its way: it only starts once you stand confirmed in the death room with no walk running. Once running, it sends nothing at all (no look, get, search or step) while a rest is on, while you have it paused, or while Auto-All is off, and goes on afterwards. While **you** have it paused its time limits don't run either, and it doesn't end on its own account; Stop, another engine taking over, starting to follow a leader, or a walk it didn't start still end it. A fight on the way holds the walking but not the pickup in the room you're standing in.
- **It gives way.** It doesn't start, and isn't left waiting, while a loop, Auto-Lair, a bank, sell or training trip or any other errand is driving the character, during a fight with a player or a flee from one, or while you're following a party leader. If one of those begins during a sweep, or any walk starts that the sweep didn't start (your own walk-to included), the sweep ends where you stand and sends nothing more. The same goes for **Stop** (the toolbar's or the Navigation window's; the toolbar shows the sweep as running even while it stands in a room looking, getting or searching, so Stop and Pause are there for it), for a step that leaves a room with a hostile still engaged (it never walks back through it), for a walk back that fails or is held too long, and for a sweep that runs out of time while movement is held. A death, a disconnect, switching character and Reset States drop it outright.
- **Typing a move during it** pauses it, as it pauses any navigation; Resume carries on. If the character is no longer in the room the sweep had stopped in when it carries on (you walked off while it was paused, or something moved you), it ends there instead of getting or searching in the wrong room. A move sent while it is still looking through the death room's exits ends it too.
- **While Moving gear** (if you use that set) applies during the sweep, since it counts as navigation running: the set goes on when the sweep starts and Default comes back when it ends.
- **Ended where you stand** means nothing more is sent, the re-wear included: gear it had already picked up stays in your pack and the pile's note says so. Recover Now puts it on and picks the sweep up again. (If the pile happens to be complete at that moment, it is finished and worn as usual.)
- **A loop's, Auto-Lair's or an errand's own walk that ends in a death room** gets the short version: the looks, and a walk to a neighbour only if a look showed your items there, then back. It doesn't walk the spill order or search.
- An Auto-Recover walk that simply **passes through** a death room grabs your overflow from the rooms right before and after it in passing.
- Just *manually* stepping into one of your death rooms grabs whatever's on that floor but never fires the sweep.

Two kinds of item are never waited for on Stock: one that **stays with you** through a death (a loyal or major-cursed item; it is in your pack afterwards and is worn again with the rest), and one the game says *has returned to its rightful place* as you die, which is gone for good. Where two different items share a name and only one of them stays with you, the name is still waited for.

A bug report lists what the latest pile is still missing, the sweep's rooms in order, where the sweep stands or how the last one ended, and what is holding one back right now.

Finally, if the only thing left un-recovered is **currency**, the death counts as fully recovered: coins are picked up as cash automatically (never `get`-ed), so they never leave a pile stuck at "partly recovered".

**A death you never saw.** On a realm whose settings say a hang-up is penalised, hanging up (or losing the link) while dropped, or low enough on HP for the penalty to finish you, kills the character after the connection is gone. MudPlay works that out when you come back in and adds the death here like any other, with the room you were in, the time the link dropped, and the pile taken from what you held then and don't now. Its death line begins *Killed by the hang-up penalty (not seen: worked out on entering the game).* and adds that the room and time are where this client last had the character: if you played it from another client in between, the pile is where it died then. It has no **How did I Die?** replay. **Recover Now** and **Auto-Recover Deathpiles** treat it as any other record. The rule it goes by, and what happens when it can't tell, are under *Hang-up penalties on this realm → A hang-up that killed you*.

## Character Info and Calculators

**Character Info** is your read-only character sheet — stats, skills, **HP Regen** (a standing gain / a full resting gain — on Paradigm that is a third of the 30-second amount and the whole of it) and **Mana Regen** (every 30 s / meditating — the meditate figure appears once the *Meditate* quest is ticked complete on the Quests tab), each with a tooltip saying when its ticks land; hover a skill (its name or its value) for the chance it comes to — with any cap and any penalty (Stealth: starting a sneak with `sn`, and keeping it on each move — both 100% once the *Perfect Stealth* quest is ticked complete — −1 per player or monster in the room, and an encumbrance penalty when you carry over a third; Thievery: rob success, quiet fail and caught; Traps: find, and disarm / safe fail / trap fires; Tracking: per trail step; Magic Res: how much it changes the damage you take from a monster's spell, and your chance to resist one outright — both only for spells that magic resistance works on); the attack table (per attack type: accuracy, damage range, and swings per round, computed from your stats, equipped weapon and gear, plus the buffs being cast on you — a buff in your list counts only while it's enabled to land on you, e.g. smite's +max damage or shadowform's backstab bonuses; a buff's bonus is rolled when it's cast, so a row it affects shows the range it can land in — e.g. *10-(20-21)* or Backstab accuracy *129-130*; hover a row to see what went into it), and folded-in quest bonuses. It also lists your worn, carried, and key-ring inventory, each a clickable link to its Game Data record (an item whose dumped name didn't resolve stays plain text). A carried chest or other container has a **treasure-chest icon** beside it that jumps to the **Chest Offload** sub-tab (under Record Keeping). A **limited-use item shows its remaining charges** next to it (e.g. *token of Silvermere - 5 Charges*). How the count is known depends on the realm:

- **Paradigm** prints "Uses remaining: N" when you `look` an item, so the count is read straight from that — the authoritative source. The client fills the readout in for you: any charged item you hold — **carried, worn, or on the key-ring** — whose count it doesn't know yet gets **looked automatically**. From then on each use (yours or one a party member remotes to you) **counts it down by one** when the item's use message appears, with no further look. A use the game turns away because you had already cast that round spends nothing and changes nothing. If a use gets neither its message nor that refusal (an NPC in the room, too little gold, too low a level), the client looks once to read the true count rather than guess; it also looks on the last charge. A `look` you type yourself always re-reads it. Transport tokens are looked on login and after each use. For a **stack** of the same item, the readout follows the **top-of-stack** copy (the one a `look` reports) — re-read whenever the top is used up or dropped and the next copy surfaces. Once the last copy is gone (dropped, sold, given away or used up), its count is forgotten rather than looked at again, and the next one you pick up is looked afresh.
- **Stock** prints no charge line, so the client **counts your successful uses** and shows remaining = the item's max charges minus what you've spent. Only uses that actually fire count: a use is confirmed by the item's cast message, so a *bonked* one (sent between rounds) burns nothing. **Rechargeable** items (the align-quest cloaks, and tokens where they exist) restock to full at your BBS's configured **cleanup time** (Settings → BBS); **finite** items (the gnarled / teak / mahogany wands) stay spent. Infinite-use items (e.g. the nexus spear on stock) show no charge line. For a **stack**, once the top copy empties the next is **assumed full** (a fresh drop is max charges) — a guess, since a partly-used copy picked off the ground would start lower; stock has no charge line to confirm it.

On both realms these counts are **saved per character**, so they survive a restart — and a rechargeable item is assumed back to full once your BBS's cleanup time has passed, without needing to look at it again. The same figures back the `@uses` remote query.

**Click a base stat's name** (Strength, Intellect, Willpower, Agility, Health, Charm) to open **Stat Breakpoints**. It shows every number that stat feeds, as one column each: dodge, accuracy, crit, stealth, damage, carry weight, swing energy, magic resistance, the skills, prices, max HP, HP regen, mana regen and so on. Each column lists the stat values where that stat's share goes up or down a point, from 30 to 200, and **your row is highlighted**, with "You: +N" at the top.
- The formulas are your realm's. Columns only confirmed on Stock are tagged **STOCK FORMULA** on Paradigm.
- A **≈** column is one the game divides together with other stats, so the real step can land a point either side.
- Thief skills show only if your class or race has them; spellcasting shows only under your casting stats.
- **Mana / tick** shows under the stat your class draws mana from: Intellect for a Mage, Willpower for a Priest, Charm for a Bard, and both Intellect and Willpower for a Druid, whose mana stat is their average (each of the two columns holds the other stat at your own value). It is the base amount: the 30-second tick pays it scaled by your mana-regen bonus, and a meditate tick pays it as it is. A Mystic's Kai and a class with no mana have no column.
- **Max HP** (Health) is Health's own share of your hit points at your level; your class and race hit points per level come on top. **Carry weight** (Strength) is the most you can carry.
- **Energy / swing** (Agility) is what one swing costs with the weapon you're wielding — or a punch, for a class that has one and holds no weapon — at your current load and strength. A round has 1000 energy, so 1000 ÷ energy is your swings a round before the cap (5 on Stock, 6 on Paradigm). It follows a weapon swap or a change in load.
- The window follows your live stats and the loaded realm. The stat buttons along its top switch stats; clicking another stat name on Character Info switches it too, and clicking the same one again brings it forward, or closes it when it's already in front.

Below the wealth block it shows an **AC / DR breakdown** in two lines: one for what your worn gear grants, and one for what your **configured self-buffs** add on top (assuming they're up) — the same buff figure the Equipment Manager and Monster Intel use.

**Calculators** holds what-if tools: the Hit Calculator, Swing and Backstab calculators, Movement Speed, Mana Regen, and Monster Aggro. The **Hit Calculator** projects your hit% and damage against a monster with your current weapon; for the reverse — how often a monster hits *you*, and whether it's safe to fight — see **Monster Intel**.

### Monster Aggro

**Monster Aggro** predicts which member of your party a monster will attack — the same target-selection the game engine runs. It shows the model for the **loaded game-data set's realm** automatically (the Paradigm version on a Paradigm / GreaterMUD set, the Stock version on a Stock set — the two engines are completely different). Configure up to **six** party members with **＋ Add member** / **✕ remove**.

**On Paradigm** each member is scored from a **150 base**, adjusted by:

- **Charm** — higher charm lowers your score, so mobs notice you less;
- **party position** — set for you where it isn't a choice: the first member is your point man (**Solo** when they're the only one — a lone player is as exposed as a frontliner — **Frontrank** once there's a party), and every added member defaults to **Midrank**, which you can change;
- **recent aggro** — tick *Last hit* on whoever swung at the mob most recently: a big bonus that scales with party size, while everyone else takes a small penalty.

The monster rolls a **weighted lottery** over the scores, so each member's **Odds** is their share of being picked — bigger score, bigger slice, but never a guarantee. No monster is needed; the odds are the same for any mob.

**On Stock** it's a different engine, so you pick the **monster**: type its record **number or name** (best match) and it fills in the matched **#/name**, its **Align** (shown as a label — it comes from the record), **Follow%**, and whether it's a **guard** (Follow% and guard stay editable). Each member sets their **alignment title**, whether they've **provoked** the mob (hit it first — forces it to aggro them), whether they **hit it last**, and how many **hits** they're already taking this beat. Per member the result shows:

- **Opens?** — whether the monster is hostile to them unprovoked, from its alignment vs theirs: evil / chaotic-evil / neutral-evil mobs open on everyone, lawful-evil spares Outlaw-or-worse (Seedy still gets attacked), good / neutral / lawful-good open on no one, and guards attack Outlaw-or-worse titles. Hover for the reason.
- **Target%** — for members it's aggroed on, their chance of being *this beat's* target. Stock mobs spread away from whoever's already being piled on (each incoming hit lowers the odds), so a tank soaking hits pulls fire off the rest. Mark a member **Last hit** and the mob re-locks onto them **Follow%** of the time (the "attack last" behaviour), the rest re-spreading across the party.
- **Follow% stickiness** — how tightly the mob holds one target before re-spreading (a high-Follow% mob is hard to peel; a passive-aligned mob you provoked never lets go).

Reach it from the Calculators tab, or wire it to the terminal right-click menu / a toolbar deep-link like any calculator.

## Realm Rankings

**Record Keeping → Realm Rankings** shows the realm's top list as last captured, with an **XP/HR** column worked out between captures, each player's rank movement, and any reroll suspects since the capture before. **Parse Toplist** sends `top N` at the depth of the deepest list you've captured (100 before the first) and reads the reply in on its own; running `top <N>` yourself does the same. **Class Filter** narrows the table to one class, and **Clear history** wipes this BBS's stored captures. Click a column header to sort, largest first.

## Roomba

**Record Keeping → Roomba** automates sorting gang-house loot into labelled rooms and backs a shared item-location log you can query in-game with `@roomba`. It's involved enough to have its own writeup — see the **Roomba (Player Workshop)** section further down for the full walkthrough.

---

# Automation

MudPlay's automation is a set of independent engines you switch on and off — combat, healing, spells, pickup, movement, and more.

## The auto-engines

Each engine — Auto-Combat, Auto-Nuke, Auto-Heal, Auto-Rest, Auto-Bless, Auto-Light, Auto-Get Items, Auto-Get Cash, Auto-Sneak, Auto-Hide, Auto-Search — is an independent on/off switch. Your primary surface for them during play is the **Action menu** in the menu bar (the toolbar can also carry each as a button — add them in Settings → Toolbar + Shortcuts).

An engine only acts while it's on, and each has a matching Settings tab for its behavior. Some gate others: Auto-Combat, for example, gates the combat/spell tuning. But **Auto-Bless stands alone** — self and party buffing is controlled by the Auto-Bless toggle and nothing else, so turning off Auto-Combat or Auto-Rest/Heal never stops your blessing.

**Auto-Combat and a `break` you type.** Typing `break` mid-fight doesn't switch Auto-Combat off: it holds the attack on that monster until you attack something yourself, or the monster dies or leaves. See **Combat → The round loop**, *Stopping the fight yourself*.

**Sneak cooldown.** Right after a fight the game won't let you sneak for a few seconds (`You may not sneak right now!`). With **Auto-Sneak on**, your loop or walk waits instead of stepping on unsneaked: it retries the sneak every two seconds and moves once it takes, or after 15 seconds goes on unsneaked. **Being followed is different:** when a monster comes into the room right behind you, a sneak can't take while it's with you, so MudPlay stops sending `sn` and walks on unsneaked — no waiting, no stopping to cast — until you leave a room that nothing followed you into, or you kill what followed you. Then it sneaks again. The status bar reads *Waiting — sneak on cooldown* meanwhile. The route also waits for the game's answer before taking a step while you're not sneaking — each time you arrive in a room, and before the first step of a loop or walk — retrying a refused sneak until it takes (up to 15 seconds), so you don't walk into the next room seen (*Waiting — sneaking*). Entering a room without the game's `Sneaking...` line means the sneak silently broke: MudPlay treats that like `You make a sound as you enter the room!` and won't open with a backstab.

**Something in your pack that kills your Stealth.** A few items cut your Stealth just by being carried: a **log raft** (-125), a wooden skiff, a silverbark canoe, a river punt, a wooden ladder, the large black gem. With one of them in your pack a sneak can be refused every time. When your Stealth less that penalty (and less the penalty for a heavy load) leaves under a **15%** chance to sneak, **Auto-Sneak stands down**: it says so once on the terminal, naming the item, sends no `sn`, and your walk or loop carries on unsneaked. It checks again whenever your inventory changes and starts sneaking again, with another line on the terminal, once the item is gone. A smaller penalty that still leaves a real chance changes nothing, and a character with Perfect Stealth is never stood down. The 15% is yours to set: **Settings → Other → Stop auto-sneaking while a carried item leaves under … % chance to sneak**.

**Keeping the sneak.** A lot of what MudPlay does on its own ends a sneak in the game:
- casting any spell;
- swapping gear;
- searching;
- opening, picking or bashing a door;
- resting or meditating;
- inviting;
- saying anything aloud;
- using an item that casts a spell: a waterskin, a potion, a charged wand or amulet, the deck of cards, a token. Eating and drinking count, since food and drink cast a spell too. The game says nothing when it ends your sneak this way.

**Using an item that casts nothing leaves the sneak alone:** lighting a torch or a lantern, using a key on a door (the `open` after it does end the sneak), and reading a scroll to learn its spell. So does a `use` the game turns away because the spell needs a target you didn't name.

**The same things end a hide.** When you are hidden rather than sneaking, any of them (and any direction you send, even one that walks into a wall) drops the hide, and MudPlay stops counting on it for a backstab. A cast drops it whether Auto-Sneak is on or off. It doesn't hide again on its own; with Auto-Sneak on it sneaks instead. **Stepping out of a hide:** hiding doesn't end a sneak, so if you sneaked into the room and then hid (the usual case with Auto-Sneak and Auto-Hide both on) you are still sneaking under the hide, and the step out goes as it is, with no `sn`: sending one would drop the sneak you have and roll a new one. Only when there is no sneak under the hide (you hid from standing, or something ended the sneak since) does a step, typed or taken by a walk or loop, send `sn` first so you leave sneaking rather than seen. A hide that fails over a sneak leaves you sneaking. If a hide was still unanswered when one of those commands went out (Auto-Hide's `hid`, or one you typed), its `Attempting to hide...` isn't taken for a hide. The list comes from the Stock game and is used on Paradigm as well.

When **Auto-Sneak is on** it times those around your stealth. Commands you type count too: type `sea`, a door command, a gear change, a cast or a `use`, `eat` or `drink` of something that casts, and MudPlay knows your sneak has ended, so your next move re-sneaks (and a hand cast re-sneaks straight after, like its own casts). The same goes for a `use` sent by a macro, a trigger or an event. When MudPlay can't tell what a `use` will do (an item it doesn't find in your pack, words that could name two things you carry, or a name after the item), it takes the sneak as ended and sneaks again. That isn't free: an `sn` sent while you are still sneaking drops that sneak and rolls a new one, which can fail. But walking on believing in a sneak that is gone sends no `sn` at all and gets you seen, which is worse. Each decision is written to the program log at Debug, under *Stealth*.

**Walking by hand.** A walk-to, loop or Auto-Lair re-sneaks just before each of its own steps. When you are moving yourself, MudPlay does the same for a move you type: if you aren't sneaking it sends `sn` just ahead of your step. It also re-sneaks **where you stand**: a moment after a command of yours ends the sneak (`sea`, a door, a gear change), and as soon as a fight ends and the room is clear. Either way your next step is a sneaked one and the room after it can be backstabbed. It never breaks a rest to do it: while you are resting or meditating below your rest-max, whether MudPlay started the rest or you typed it, the re-sneak waits and goes out once the rest has topped off. A ShadowRest character (Paradigm, with *Utilize shadowrest* ticked) is the exception: it sneaks right where it rests, since there the `sn` leaves the rest going and the rest keeps the sneak. It still can't sneak with a monster in the room, and the after-fight cooldown still applies (it retries until the sneak takes). Switching **Auto-Sneak** on sneaks straight away too.

- **About to backstab.** With Backstab on, in a room you're going to fight, everything that can wait holds until your `bs` has gone out: in-between spells (heals included), gear swaps, the room search and light changes. Anything first would break the sneak and spoil the surprise. Once the backstab round is over, a buff or heal that waited for it goes out right away, and you re-attack straight after it.
- **Sneaking past.** With **Auto-Combat off** (or combat suppressed in a room), sneaking through a room with NPCs you won't fight, the same things hold until you reach a room with no NPCs (a sneak won't take with one there). There they go out and you re-sneak straight after. So in an empty room, a buff or heal your settings call for is cast and you re-sneak before moving on. This covers:
  - buffs, cures and heals;
  - a hazard counter's drink inside its refresh window (the desert waterskin);
  - automatic gear swaps (re-applied then);
  - the room search;
  - putting away or swapping a light;
  - optional rests;
  - party invites;
  - chat such as level-up announcements or ailment calls, which are queued and sent then.
- **Mid-step.** While a sneaked move is on its way, casts and the rest wait until the next room appears. The game carries out commands in order, so anything sent then would land in the room you're entering, unseen.
- **Stopping to cast.** A sneaked walk is always mid-step, so on its own a buff would never find a gap. When a buff, cure or heal is due (and you have the mana), your walk or loop pauses in the next room with no NPCs — including a room where your sneak already broke — casts it, re-sneaks and carries on. The status bar reads *Waiting — casting before re-sneaking*; if the cast doesn't go out within 7 seconds, the route moves on.
- **Walk steps still happen.** A door, a trap, a lever or winch, a hidden exit, or a room command the route needs is done anyway, along with its party relay. That includes a **spoken password** that opens an exit or teleports you (`say gazmuldduhaz`): it is part of the route, not chatter, so it is never held. MudPlay then re-sneaks before the next move. A **hazard counter** you have to use (the desert waterskin) waits for a room with no NPCs like a buff while its buff still has time on it, and goes out regardless at its last call (see *Keeping a hazard buff up*).
- **Emergency heal while fleeing.** When your *run if below* HP / mana settings have you fleeing (not a hit-and-run or a failed backstab's run), the *emergency heal* slot fires as soon as it's needed, and the re-sneak waits until it has gone out.
- **Rests you need still happen.** A rest your *rest if below* settings call for goes out even if it ends the sneak. On Paradigm, with **Utilize shadowrest** ticked and a race or class that has ShadowRest, it sneaks first and then rests, so the rest keeps you hidden — retrying a sneak that fails before the rest goes out. Without that, a rest ends the sneak, so a buff cast during the rest doesn't re-sneak; the sneak is taken again before your next step.
- **Replies stay quiet.** While you're sneaking or hidden, a reply to an @-command someone said aloud goes back by telepath instead of a say.
- **Gear before the sneak.** A boss / lair gear set or backstab gear for the next room goes on before the sneak, never after it.
- **See-hidden and failed-sneak fights.** If a see-hidden monster forces a fight (with *Clear hostiles when sneak broken by see-hidden monster* on), or a failed sneak stops you to clear a room (with *Clear hostiles when sneak fails* on), the now-cleared room becomes the place the held actions fire, you re-sneak, and the walk continues.

A flee or an emergency hangup is never held. With **Use @panic while leading** on, a leader's hangup says `@panic` first (telling the party to hang up too), even though it ends the sneak; a follower just hangs up. Turn Auto-Sneak **off** and none of this applies: everything goes out on schedule, wherever you are.

## Manual one-shots and Reset States

The **Action menu** also carries commands you fire once, on demand, rather than leaving running:

- **Get All / Deposit All** — pick up everything on the floor (except cursed items, which it leaves there and names in the log), or bank your wealth down to the keep-on-hand floor, right now.
- **Drop ▸** / **Hide ▸** — submenus with **All** (every carried, unworn item), **Everything** (worn gear, light, keys and coins too — no confirmation), **Coins** and **Keys**. Hide does the same sweeps with `hide`, stashing everything in the room where only a search turns it up. A stack goes in one counted command on Paradigm and one per item on Stock. Items the game won't let go of are left out and named in the log: no-drop items (Paradigm's tokens, the Gypsy's deck of cards), loyal items, and cursed gear you're wearing. The commands go out a few at a time, each batch waiting for the game to answer, so a long sweep never overflows the game's command limit.
- **Equip ▸** — wear any of your gear sets: **Default**, **Backstab**, **Pre-rest HP**, **Pre-rest Mana**, **While Moving** or **Bossing**. Any set but Default is a **hold**: it goes on, the terminal says *[<set> will stay equipped until you deselect it.]*, the entry shows ticked, and **every automatic gear swap is off** (resting, moving, boss rooms, loop start) until you pick the same entry again or pick **Default**. Deselecting swaps back to **Default**, and then to whatever set automation wants at that moment (a pre-rest set if you're resting, While Moving on a run, Bossing in a boss room). If you deselect mid-fight, the swap waits until combat is over. If the set you just deselected is itself the one automation wants (you untick Pre-rest HP while resting), it simply stays on and automation takes it off when it's done. The toolbar's Equip ▾ picks and an Equip hotkey work the same way. A hold also ends, with the newly asked-for set going on, when you use the Workshop's **Equip Now** or a party member sends `@equip`; and it ends when you load another character or close the client.
- (These are the local twins of the `@get-all` / `@drop-all` / `@hide-all` / `@equip` / `@deposit-all` remote commands.)

**Toolbar split buttons.** The **Drop All**, **Hide All** and **Equip** toolbar buttons each have a small **▾** beside them. The ▾ picks **what the button does** — Drop All's unworn / everything / coins / keys, or which gear set Equip wears. Picking one only changes the button; nothing is sent until you click it. From then on a click on the button does your pick (its tooltip names it), and the pick is saved to your character. A keybind on one of these buttons follows the same pick.
- **Reset States** — the recovery escape hatch. It puts **every engine back to idle, as if you were standing in a room with nothing running**: it stops any walk, loop, Auto-Lair or Roomba sweep, and drops everything they were holding on to — detours (fetching a route item, buying a light, a token route, an auto-deposit or training trip), a party member recovery or backtrack, corpse recovery, the check for items a hang-up dropped, door / trap / hidden-exit attempts, the maze and pyramid solvers, and any destination an engine was going to walk you back to afterwards. Your auto-engine toggles (Auto-Combat, Auto-Sneak, Auto-All and so on), lair markers, buff timers and settings are left exactly as they are. It also clears your own stuck ailments, waits, and movement holds **and every party member's ailment chips** (blind / poison / disease / confuse / held) — reach for it when an engine looks wedged (e.g. the walker parked "held" or "waiting" with nothing actually happening) or a party row is stuck showing a condition that's already gone. It also **re-equips your Default gear set** (undoing a stuck Pre-rest swap) and **re-polls `health`** — the game's compact one-line HP/pool readout, far less scroll than the full stat screen — so a drifted max HP/mana snaps back to the real value. (Typing `health` yourself re-anchors the same way.) It's also on the terminal's right-click menu.

## Base modes

The Settings → General **"Auto-Engines base modes"** checkboxes are your character's default engine states. The live toolbar settles to them **every time you load the character** — so a character always comes up in its configured defaults, not in whatever transient state the last session happened to end in — and the toggles also **snap back to them at the start of a loop or Auto-Lair**, when a **walk-to arrives** at its destination, and when **you stop movement yourself**: the Stop button or its hotkey, the Navigation window's Stop, or a party member's `@stop`. So you can flip combat off to travel somewhere and it returns to your defaults when you get there, when you stop, or when the circuit begins (or next time you load the character), even if you forget to turn it back on.

What doesn't reset them:

- A walk that **fails**, or one an **errand takes over** mid-route (a bank trip or sell detour replanning it): your toggles stay as they are.
- **Pause** on its own (the Pause button): only Stop and `@stop` reset.
- A **Run or Sprint start** you stop before it has begun: its own box in Settings → Other decides whether Auto-Combat comes back or Sprint Mode ends.
- While **Sprint Mode** is on: the autos it switched off come back when Sprint ends, not on a stop.

(A character created before these checkboxes existed adopts its current live modes as its base the first time it loads, so nothing changes until you edit the boxes.)

## The master switch (Auto-All)

The **All auto-responses** toggle at the top of the Action menu, its toolbar button, and the `@auto-all` remote command are one switch: the **master switch**. Off, **nothing automatic acts**. On, everything is as you set it.

**It is the only "all autos off".** Unticking the eleven auto toggles one by one is *not* the same thing: many automatic systems have no toggle of their own (remote commands, triggers, events, the polls, the party signals), so only the master switch silences all of them. The button shows the switch, not the toggles: with every toggle unticked by hand it still reads on.

**Switching it off** remembers which toggles were on, unticks them, and from then on:

- **No engine runs**: combat, nuke, heal, rest, bless, light, get items, get cash, sneak, hide, search. A toggle you tick by hand while the switch is off stays ticked for later but runs nothing.
- **No remote command is followed** except `@auto-all` itself. That includes the party signals (`@waiting`, `@comeback`; `@wait` and `@ok` are taken down but not acted on, see holds below), the single `@auto-…` toggles, `@settings`, `@hangup` and `@relog`. A command that is ignored for this reason gets **no reply at all**, whatever *Warn on invalid remote command* says, so nobody can probe whether you are at the keys. An `@auto-all` from someone without the grant gets no reply either while the switch is off.
- **Triggers and events are skipped**, not put off: a trigger that matches fires nothing, and an event whose time comes does not run later. An event already running is held with its walk, clock and all (a 30-second wait has the rest of its 30 seconds to run afterwards), and carries on when the switch is back on. An event already **waiting in the queue** does not start while the switch is off: it keeps its place, and the waiting ones start in their order once it is back on. Neither the queue's wait limit nor the limit on how long a paused event may stand still counts time the switch was off. If the switch held waiting events for **more than 5 minutes**, switching it on yourself asks you which of them should still run before any starts (the **Waiting Events** window, see [When events overlap](#when-events-overlap)); closing that window drops the events it lists. Switched on by a remote `@auto-all on` instead, they are dropped without asking. A Logoff event is skipped like any other.
- **A walk, loop or Auto-Lair you start by hand is refused**, with a notice in the terminal naming what could not start, for example `[Loop cannot start: the master switch (Auto-All) is off]`. Nothing already running is stopped for it. Resuming a run you had paused is refused the same way (`[Loop cannot resume: …]`) and your pause stays. The same goes for the other trips you start by hand: Train Now, a stash transfer, a Sell Tour, a Roomba sweep. One **already running** when the switch goes off is frozen where it is and resumes when the switch comes back on. Your own Pause / Resume is untouched.
- **Nothing is polled or fixed up**: no party health or level asks, no token or item-charge looks, no quest sync, no automatic `stat` / inventory re-read, no "where am I" fix (`rm`, `sys st`) and no room redisplay. The map goes on reading the rooms the game shows you; it just asks for nothing.
- **No hold is raised and no hold is signalled**: held, confused, afraid, a hurt party member, a party wait, too heavy. No `@wait` goes out and no ailment is announced. A `@wait` you had already sent is **released as the switch goes off** (one `@ok`, so your leader isn't left standing); if what held you still holds when the switch comes back on, one `@wait` with its reason is sent then. When you lead, a follower's `@wait` or `@ok` that arrives while the switch is off is still taken down, with no reply and no hold: when it comes back on you hold for whoever last asked you to, and your wait limit counts from then.
- **Nothing else answers for you**: no auto-invite or auto-join, no greeting or look-back, no level-up announce, no telepath divert, no auto-train, no bank or stash trip, no gear swap for a room (a Location rule's item, or the item that negates a room's spell: neither is put on or put back, and the room you stand in is settled when the switch comes back on), no use of your hazard counter item (the desert waterskin), no PvP response, no boss Grab All, no auto-open (a container still waiting to be opened when the switch goes off is forgotten, not opened when it comes back on).
- **Hang-ups** follow **Allow hangup in all-off mode** (Settings → General): unticked, nothing hangs up on its own; ticked, every automatic hang-up still works. See that setting.
- **The nightly-cleanup log-off does not start.** It is not a hang-up, but it is automatic. If the board drops you anyway, reconnecting follows your BBS reconnect settings as usual.

**What always works**, switch on or off: everything you type, your macros and aliases, logging in (the whole login automation, through entering the realm), setting up and repairing the statline, reconnecting as your BBS settings say, and `@auto-all`.

**Switching it back on** gives back exactly the toggles that were ticked when it went off, plus any you ticked meanwhile. If you had unticked every toggle and then switched it off, switching it on brings back only the switch: nothing is ticked for you. Your **base modes** are switched on only when nothing is remembered at all, which is an `@auto-all on` that arrives with the switch already on and no toggle ticked. The button and the menu item can't do that: pressed with the switch on, they switch it off. Holds that came due while it was off are raised before anything moves, a frozen walk or loop carries on, a monster already in the room is fought and a rest that is due starts. A downed party member whose rescue the switch ended is aided again if still down, and if a pickup had been put off until the end of a fight in the room you are still standing in, the room is shown once more so it can be made. A `@comeback` a stranded member sent while it was off is answered then, if it is still worth answering: it is kept only from someone you would go back for, and dropped when they leave the party, when you disconnect or load another profile, and once it is older than the Party tab's *If leading, accept @comeback for* time. Anyone else's gets no reply, then or later.

**Switched on while disconnected**, or at the board's menus, the switch reads on at once but nothing is sent or started and movement stays frozen until you are back in the game; all of the above happens at the first game prompt.

**One thing to know.** If you switch it off in the middle of a fight with **Break before fleeing** on (Settings → Combat), one `break` goes out as it does when you untick Auto Combat.

**Nothing ticks a toggle for you while it is off.** The moments that normally settle the toggles into your base modes (a Stop, a walk-to arriving, a loop or Auto-Lair starting) do not do so with the switch off, and are not made up for afterwards: what comes back is what was ticked when you switched it off.

**A reconnect respects it.** If you switch the autos off and then reconnect, **Re-enable on reconnect** ticks nothing back on and the switch stays off. A loop that was running when the link dropped is not restarted on the way back in either: it restarts when you switch back on, unless you press Stop first.

**It lasts for the session only.** Restarting the client, or loading a profile, puts the switch back on; "off" is not saved.

`@auto-all off` switches it off even when every toggle was already unticked by hand; `@auto-all on` switches it on, exactly as the button does (with the base modes only in the nothing-remembered case above); a bare `@auto-all` flips it. The reply names the switch: `@auto-all: on` or `@auto-all: off`.

The program log has one line each time the switch changes, listing what it stopped or held and, as it comes back on, how often each kind of thing was skipped while it was off. A bug report carries the same counts.

## Macros, aliases, and triggers

Beyond the engines, you can script your own automation. All three editors live in the **Game Data Browser** — press **F3** (or use View → **Macros** / **Triggers** / **Aliases** to jump straight to one) and pick **Macros**, **Triggers**, or **Aliases** from the *Tables + editors* list on the left.

Each shows the same surface: a **Filter…** box, an **Add** button, a **Remove** button, and a grid of what you've already made. **Double-click a row to edit it.** There's no separate save step — each editor's **Save** button writes to disk immediately, and the list's **Enabled** column shows a ✓ for the ones that are live.

- **Macros** bind a **key chord to a command.** Click **Add**, press **Capture** and hit the key combo (release the main key to lock it in; click **Capture** again to abort), then type the **Command** to send. Split it into several lines with `^M` or `;` — each fragment fires as its own command. (A `;` at the start of a word, as in `;o`, is part of the command and is sent as typed.) Macros work while you're typing in the terminal; new profiles start with the numpad pre-wired to compass movement. **Esc is a bindable key** — you can put it on a macro or a shortcut; an unbound Esc still passes through to the game as usual.
- **Aliases** expand a **typed word into a longer command** — a shorthand you invent, so `cast heal bob` can send `c 'heal' bob`. See **Writing an alias** just below for a full walkthrough.
- **Triggers** are **auto-responses to game text** — when a line matches, MudPlay fires a reply. Give the trigger a **Name**, then set:
  - Every trigger belongs to the **character** it was made on, like macros and aliases. (Triggers could once be saved "to the game data" and shared by every character on it. Each character from then took its own copy of those the first time it loaded under this version; a new character starts with the default triggers.)
  - **Scope** — which incoming lines it watches: *Game messages* (the default), a single chat channel (*Say / Yell / Gossip / Telepath / Gangpath / Broadcast*), *Chat (any)*, or the *System log*.
  - **Match type** — *Literal* (type the text as it appears; `*` wildcards a span and `{name}` — or a numbered `{1}`, `{2}` — captures a piece) or *Regex* (full .NET regex, with `(?<name>…)` for captures).
  - **Pattern** — the text or expression to match against each line. Any pieces you capture appear in the **Captures** row.
  - **Response** — what MudPlay sends back on a match. Drop a captured value in with `{name}` (or `{1}`, `{2}`).

    To send **several commands**, put each on its own line in the box (the Response box accepts Enter) — every line is sent as a separate command, each with its own Enter. `^M` and `;` do the same thing on a single line, so `north;get all;south` is three commands too.

    Leave the box blank to send a bare Enter.
  - **Sound** (optional) — a sound file to play when the trigger matches. WAV plays on every system; MP3, OGG and FLAC depend on your system's player. **Settings → Sounds → Trigger sounds** turns trigger sounds on (it starts off) and sets how loud they play.

**Triggers follow the master switch (Auto-All).** While it is off a trigger that matches is skipped whole: nothing is captured, nothing is sent and no sound plays. Macros and aliases are things you fire by hand, so they always work.

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

MudPlay also ships **built-in defaults** for the automation-facing bits (a monster's default relationship/priority, item auto-flags, the message catalogue, boss and quest lists). These are baked into the program, so **updating the app refreshes them automatically** on the next launch — a shipped fix reaches you just by running the new version. Your own edits are never lost: overrides you make in the Browser (and your per-set message edits, which are kept on top of the shipped messages so shipped fixes still reach every message you haven't changed) resolve *above* the defaults, so they keep winning; and your custom **triggers** are left completely alone.

The same applies to the **starter navigation loops** that come bundled with each set: they're baked into the program too, so **new ones added in a later release are added to your existing sets on the next launch** — added only, never overwriting a loop you already have, and **never re-adding one you deleted** (MudPlay remembers what it has already offered each set). Your own loops are always left untouched.

**GOTO favourites belong to the character.** A new character starts with none and keeps its own list from there; nothing another character adds or removes reaches it. (Favourites used to be one list per game-data set, shared by every character on it. Each character from then took its own copy of that list the first time it loaded under this version.)

**Favourite loops and auto-lair setups belong to the character too.** The loops and setups themselves stay with the game data, shared by every character on it, so a loop one character builds is there for the others. Which of them are *favourites* is each character's own choice: favouriting one adds it to that character's right-click Favorites menus and nobody else's, and it doesn't touch the loop file. (The favourite used to be stored in the loop file, so it showed for everyone. Each character from then took the favourites it could already see, the first time it loaded; a character made since starts with none.)

## Importing and switching sets

The top **Game Data** menu (in the menu bar) manages your data sets:

- **Import .mdb…** — pick a MajorMUD `.MDB` file; MudPlay imports it as a new named set and switches to it. This populates the tables the engines read from — the terminal itself works without it. If you import after launch, the startup splash is dismissed so the import's progress and any errors show on the terminal. The **MDB IMPORT COMPLETE** line names the set, its table and entry counts, and what that export carries: **lairs table: yes / no** (newer exports add a Lairs table) and **room commands: yes (N items) / no** (item sources by room command, written by Nightmare Redux for Linux). An export can have either, both or neither.
  - **"No game tables found"** means the MDB's internal catalog is damaged — usually from being opened and edited in Microsoft Access without a *Compact and Repair* afterward, which detaches the game tables from the database's object list. MudPlay won't switch to an empty set; to fix it, run Access's **Database Tools → Compact and Repair Database**, or re-export a fresh MDB from Nightmare Redux, then import again. (The Program Log records the catalog scan so you can confirm what the database reported.)
  - **Each table is verified on write** — re-read after writing and retried once if it didn't come back as valid JSON, so a truncated or interrupted write is caught during the import. A table that still can't be read (or one already corrupt from an older import) is reported as **unavailable** on the terminal in red rather than crashing, and the engines that rely on it stay missing data until you re-import.
  - **A set imported by a much older MudPlay can have damaged long text**: monster spawn lists, item sources and room command scripts with two characters missing here and there (`oup: 1/547` for `Group: 1/547`). MudPlay checks each set as it becomes active and, if it finds the damage, says so on the terminal and in the program log. Nothing in the set can be mended in place: **import the same .mdb again** (a file with the same name as the set, since the set is named after it) and its tables are rewritten whole. Your own overrides, loops and favourites for the set are kept.
- **The set list** — every imported set appears at the top of the menu with a checkmark on the active one; click another to switch. The Browser's status bar shows *Set: <name>*.
- **Import loops (MegaMUD .mp)…** — bring a MegaMUD `.mp` loop into the active set, so a circuit you already built in MegaMUD comes across without re-walking it. Pick a file and the **import review** opens with two panes side by side (see *Importing a MegaMUD loop* under Navigation & Looping).
- **Manage Game Data…** — copy or move what you made for one set into another, or delete a set. Pick the **From** and **To** sets, tick what should go, then **Copy** or **Move** (a move takes it out of the source). Each tick box shows how much the source set holds, and is greyed when it holds none:
  - **Loops and lair setups** — loops, Auto-Lair setups and their nav folders. These are added to the destination's; a loop of the same name is replaced.
  - **Message edits** (spell, condition and monster messages, flavor prefixes) — replaces the destination's.
  - **Game Data Browser edits** — your changes to items, monsters, spells, rooms and the other tables, at every level (all characters, each realm, each character). Replaces the destination's.

  This is the way to carry your work over after importing a newer MDB under a new name. If either set is the active one, it reloads when the copy finishes. Favourites and triggers belong to each character, and the boss list and unrecognized lines to the realm, so they come along on their own and aren't in this list.
- **Modify Blacklist…** — hide specific rooms (by map/room number) from the map and room search, and mark ones the walker should treat as unreachable. You can also blacklist a room straight off the map — **right-click it → Add this room to Blacklist**. A room blacklisted from the map stays drawn (and selected) until you click a **different** room, so you can confirm you hid the right one before it disappears — handy for pruning rooms that aren't really reachable or that you'd rather not see on the map or in the search box.
- **Modify avoid/stash rooms…** — a staged editor over your character's **avoid rooms** and **stash rooms** together. Each row is tagged by type (*Avoid Room* / *Stash Room*) with its map/room number and name. Avoid rooms are your personal no-go list — the walker, loops, and auto-lair route around them; stash rooms are the drop-off points the cash/item engines use. Quick-add a room by picking a type, typing its map and room number (the name fills in from the active set), and clicking **Add room**; select one or more rows and **Remove selected** to clear them. **Save** commits every change and redraws the map; **Cancel** or the title-bar X discards. (You can still mark either kind straight off the map with a right-click — this editor is for reviewing and bulk-editing the whole list.) The two sets are independent, so a room flagged as both appears once per type.

## Getting around the Browser

The window is a sidebar plus a content pane:

- The sidebar's **Search…** box filters the **section list**, not the rows — type "weapon" and unrelated sections drop away.
- **Tables + editors** (top group) holds what you build: **Players, Macros, Triggers, Aliases, Incomplete Messages, Unrecognized Lines, Flavor Prefixes**. (The macro/alias/trigger editors are covered in the **Macros, aliases, and triggers** section; Flavor Prefixes has its own note below.)
- **Imported tables** (bottom group) holds the game data: **Monsters, Items, Spells, Rooms, Lairs, Shops, Races, Classes, TextBlocks, Info, Unobtainable, Quest Flags.**

Click a section to open it. Each table has its own **Filter…** box (this one filters *rows*), sortable and resizable columns, and a row-count line at the bottom. The box matches the **visible cell text** across every column (including the friendly labels), and several tabs accept **special filter words** on top of that — the full list is under **Filtering a table**, below.

The rightmost **Use** column shows which tier owns each row — **Def** for the untouched import, or **Glob / BBS / Char** once you've overridden it.

The **Items** and **Players** tables carry a **Toggles** column that lists, per row, the flags *you've* turned on for it — an item's **Collect / Discard / Open / Buy / Sell / Sell-detour / Stash** (plus **No-take / Keep-min / Loyal / Path-get**), or a player's **Invite-if-seen / Join-if-invited / Don't-delete** followed by each **remote-control permission** you've granted them (a full grant collapses to *All @-permissions*). It reads blank when you've set none; a crowded cell trims with an ellipsis — hover it for the full list, or drag the column wider. Click its header to sort by it, which groups the rows you've configured together. (The Players tab's separate **@'s** column keeps the quick None / Some / All summary of those permissions.) The **Monsters** table surfaces the same kind of per-record settings, but as their own columns — see its column list below.

The **Players** table also has a **Relationship** column, and a player's edit dialog (double-click the row) has the two settings behind it:

- **Relationship** — how you stand with that player on this realm: **Neutral** (the default for everyone: left alone until they attack you, which makes them an Enemy), **Friend** (never attacked) or **Enemy** (the PvP response applies on sight). It is saved with the realm's player list, so every character you play on that realm shares it, and a Friend or Enemy is never removed by the stale-player cleanup.
- **PvP response** — what to do about that player when they are an Enemy. *Use the PvP settings* follows the general response; any other choice (hang up, flee then hang up, flee, attack, chase and attack) replaces it for that one player.

The column is blank for a Neutral player, so the ones you've marked stand out. So far they decide whose presence holds your room attacks on a PvP realm, and a Neutral who attacks you is moved to Enemy for you (see *Room attacks and other players (PvP realms)* under Combat). What happens next is set on Settings → PvP.

A player's **class** is filled in from wherever the game states it: the party list, the **top** list (run `top` and every listed player you already have a record for gets their class), and `look`. Failing those it is worked out from their title on `who` when only one class uses that title.

The **Monsters** table lists only the monsters that can actually be met in the game. The ones the game data marks *out of play* — sysop-only NPCs, unused or test monsters (about 70 in the Paradigm set, such as the extra copies of *dark cleric* or *guardsman* that no room ever spawns) — are not here; they are in the **Unobtainable** table instead, so a name that appears twice in Monsters is two real spawns.

The **Unobtainable** table collects everything the game data marks out of play, **Items** and **Monsters** alike, read-only. It also holds any **monster that can never spawn** even though the data marks it in play: one that isn't placed, isn't in a lair, isn't summoned by anything, and is only listed under rooms that have a different NPC. The game data can't show which rooms really skip their listed spawns, so this is a careful guess; in the known data sets it catches only *Cygani*, listed under Aiken's Magic Shoppe, where the Stock game files confirm the shop only ever spawns Aiken. Map room tooltips and room panels leave out anything on this list, so they only show monsters you can actually meet. Its **Kind** column says which table a row came from (the two number ranges overlap, so read the ID together with the Kind), and **Reason** says why it's here; the item columns (type, slot, damage, price…) fill in for items and **HP / Exp / Avg Damage / Alignment** for monsters. The Item Finder skips the same items.

The **Quest Flags** table lists every script line in the game data that touches a quest flag (an ability number the game uses to keep a character's place in a quest), one row per line. **Flag** and **Name** say which flag; **Relationship** says what the line does with it (*Grants*, *Advances*, *Requires*, *Tests*, *Gate (must not have)*, *Clears*) and **Step** the value involved. Then come the columns that say how to get there:

- **Command** — how the line is set off, when the data shows it: the room command to type (`throw egg`), the keyword to ask an NPC (`ask old man phoenix`, with the NPC's other keywords for the same reply in brackets), or `kill <monster>` when the line runs off that monster's death. When several NPCs share the same dialogue, each NPC's row shows its own `ask`. Blank when the data doesn't show it.
- **Level** — the level the line asks for: `15+`, `up to 19`, or `20 to 29`.
- **Class**, **Race** — the classes or races the line is written for.
- **Items** — the items the line looks for, with *(taken)* after the ones it takes from you.
- **Kind**, **Source**, **Location** — the NPC, room or spell the line hangs off, as before.

Type a flag's number into **Filter…** and press Enter to see that flag's rows only (`133` shows flag 133, not 1330 or a room numbered 133); any other text matches any column, so a flag's name, an NPC or an item works too.

**Double-click a row** to open **Quest Flag Steps** for that row's flag: everything the game data's scripts do with the flag, in the order you would walk it (by the value the flag has to hold going in, then by the value the step leaves it at). The top shows the flag's name and number, your name for the quest if you have given it one, the value at which the quest reads as complete, and a class or race limit when the quest list records one (the steps' own **Needs** are what to go by). Each step then has:

- a **heading** — what the line does to the flag: *Start — without the flag → sets 1*, *At exactly 5 → sets 6*, *At 4 or more → no change*, *At 3 or more → clears the flag*. A line that sets the flag and then checks it for a value it cannot have just been given says so: *sets 1, then stops at `checkability 131 3`*. A check on the flag that comes after a spell or another textblock has run, or after the line has started giving, is marked: *Without the flag, checked after `cast 5145`*;
- **Do** — the command and where: the room, or the NPC and the room it stands in (every NPC, when several share the dialogue), or the monster to kill and where it is found. A line that is one outcome of a random draw says which line draws it and how often that outcome comes up (each draw, when one drawn table draws from another). Anything else the data shows the line hanging off is named as *Also reached from …*. When the data doesn't show how the line is reached, it says which textblock, spell or room it is reached from instead; a line that comes off a keyword the NPC shows by itself (`message`, `greeting`, `text`) says that, since it isn't something you ask;
- **Needs** and **Gives** — what the line asks for and what it hands over, in the order the line goes through them, because a line stops at the first condition that fails. **Needs** is level, class, race, items you must have, items taken from you, items you must *not* have, other flags and the value they must hold, what it costs, whether monsters may or must be in the room, and any alignment check (quoted as the script writes it). **Gives** is items, experience, stat rewards, spells taught, where it teleports you, and other flags it sets or clears. For a line drawn at random, Needs includes what the drawing line asks;
- **First, whatever the checks say** — things the line gives before it checks anything, so you get them even when a check then fails (a teleport ahead of the level check, say);
- **Then checks** / **Then gives** — further conditions the line only reaches after it has already given something, and what it gives once those pass. When the flag's own change falls in one of these runs it is listed there (*This flag: sets 1*) so you can see what comes before it and what after. A label ending *(after `cast …`)* marks conditions checked after the line has cast a spell or run another textblock, which may itself have changed things;
- **Also in the script** — every remaining step, exactly as the game data writes it. These are steps whose meaning isn't established, so the client shows them rather than guess;
- **Not reached** / **After …** — the rest of a line that checks an ability it has itself just changed. When the line's own steps settle the value and the check cannot pass, what follows is listed as not reached, and its rewards are not shown under Gives. When they don't settle it, what follows is quoted without saying whether it runs;
- **Script** — the whole line and its textblock number, which you can select and copy.

Lines that are the same apart from the class they are written for are shown as one step listing the classes, and a room command with several wordings is shown once with all of them. Lines that only run for a character *without* the flag and don't change it are kept at the bottom under **Only without this flag**, closed until you open it, so they don't crowd the steps that move the flag (for the alignment flags they are the other two paths' whole quests); the line under the heading says how many there are. When a flag has no other lines, they are the list. **Other flags these steps touch** are links: click one to see that flag's steps in the same window, and **Back** to return. Double-clicking another row of the table swaps the window to that flag rather than opening a second one.

The **Monsters** table carries a full column set for browsing and filtering monster stats:

- **Landmass**, **Region**, **Area** (right after the name) — where the monster lives, as the hierarchy Landmass → Region → Area (for example *Mainland → Volcano → Infernal Cavern*). The Paradigm set ships them filled in for every monster that has a spawn room or that something summons from a known room; the monster's most common place wins when it appears in several. In the Paradigm set every monster in this table has one. They are labels only — nothing in combat or navigation reads them — and they are searchable in the Filter… box. A monster with nothing set reads blank; fill it in from its record (below). If you've already customised this table's columns, the three start **unticked** — switch them on from **Columns ▾** (or **Reset to defaults**).
- **Relationship** — how *your* overlay tells the engine to treat this monster: **Enemy** / **Neutral** / **Friend** / **Flee** / **Hangup**, resolved across all four tiers just like the combat engine reads it, so an un-tagged monster shows **Enemy** and any relationship you or a shipped default set shows through here without opening the record.
- **Priority** — your attack-priority for this species (**First / High / Normal / Low / Last**), resolved across all four tiers like Relationship; **Kill-on-sight** and **No Backstab** — each reads **✓** when you've set that per-monster flag, blank otherwise.
- **Respawn** (respawn timer), **Exp** (experience per kill — base × multiplier), **HP**, **AC/DR**, **Dodge**, **Magic Res**, **BS Def** (backstab defence: what a backstab's to-hit meets on top of a quarter of the monster's AC; blank when it has none).
- **Acc (typ/max)** (typical/highest attack accuracy), **Damage**, **Exp Eff** (an exp-per-effort efficiency score).
- **Lair Exp**, **# Lairs**, **Avg Lair Size**, **Biggest Lair**.
- **Mag-wpn req** (the HitMagic level a weapon must meet to land a hit), and **Undead**.

Every game-data record table — Monsters, Items, Spells, Rooms, Classes, Races, Lairs, Shops, and the like — has a **Columns ▾** button at its **top-right**: a picker to check/uncheck which columns show, so you can tailor each table to just the stats you care about. (The engine-backed utility tabs — Macros, Triggers, Aliases, Players, Incomplete Messages, Unrecognized Lines, Flavor Prefixes — keep their fixed columns, so they have no picker.)

It also surfaces columns otherwise only used by the filter sidebar: on the Monsters tab, for instance, you can turn on the per-element resist columns (Cold / Fire / Stone / Lightning / Water), spell-immunity, and more, to *see* them in the grid instead of only filtering by them. Your choices are saved **per character**, per table; **Reset to defaults** in the picker restores that table's standard columns.

On the **Monsters**, **Items**, and **Spells** tables you can also **rearrange the columns**: drag a column header left or right and drop it where you want it. The order is saved per character alongside the visible set, so it's how the table opens next time; a column you switch on afterwards joins at the right-hand end, and **Reset to defaults** puts the standard order back. The **Use** tier column always stays last.

It also carries a **filter sidebar** on the right — drag its left edge to resize it — that **curates** which monsters are in the list. Edit the boxes, then press **Apply** to run them (a deliberate step, so a half-typed range never re-filters mid-edit); **Reset** clears every filter and the search box at once. It's split into labelled sections, all AND'd together:

- **Location** — **Landmass**, **Region** and **Area** dropdowns that narrow each other: pick a landmass and the Region list shows only the regions on it, pick a region and the Area list shows only its areas (for example *Mainland → Volcano → Infernal Cavern*). Each list also has **(not set)** whenever some monsters in scope have no label yet, so you can find the ones still to be filed from their records. The lists are built from the monsters' own location labels (the shipped ones plus anything you have typed into a record), and a change you make in a record shows up here after its reload.
- **Combat** — Exp, HP, Avg damage, Accuracy, Armour Class, Damage Resist, Dodge, Magic Resist.
- **Elemental defenses** — Cold / Fire / Stone / Lightning / Water resist %. These are **signed**: a *negative* resist means the monster is **vulnerable** (takes extra of that element), so bracket the max at −1 to find things a given element shreds.
- **Casting & immunity** — Magic-weapon requirement, Spell immunity level, and a **Casts spells** toggle.
- **Type & alignment** — Type (Solo / Leader / Follower / Stationary) and Alignment dropdowns, plus **Undead**, **Animal**, and **Non-living** checkboxes.
- **Loot & lairs** — a **Drops an item** toggle, and Lair Exp / # Lairs / Respawn ranges.

Every numeric filter is a **min / max range** — either box can be blank for no limit on that side, so `HP 500–2000` brackets a band, `AC ≤ 20` finds easy kills, and a lone minimum works like the old "at least N". Hover any label for what the stat means. **Reset** (top-right of the panel) clears every filter and the search box at once. The **Filter…** text box at the top is separate: it **finds** a specific monster within the curated list, while the sidebar decides which monsters are in it.

## Filtering a table

Every table's **Filter…** box (top-left) *finds* rows in the current list when you press **Enter** — typing alone doesn't re-filter, so even a big table stays responsive while you type — and clearing it shows everything again straight away. By default it matches the **visible cell text** across **every** column, including the friendly label a formatter renders — so on the Items tab `Weapon`, `Plate`, or `Feet` match the type / armour / slot columns, and `Lawful Good` matches an alignment, not just the raw code behind it. On every imported table it also matches the **Use-tier badge**, so typing `Char`, `BBS`, `Glob`, or `Def` lists just the rows owned by that tier — a fast way to see only the records you've overridden.

Some tabs understand **special filter words** beyond that plain-text match:

| Tab | Type… | …to show |
|---|---|---|
| **Items** | `get` or `collect` · `drop` or `discard` · `open` · `buy` · `sell` · `detour` · `stash` · `keep` · `loyal` · `notake` · `path` | only the items you've set that **auto-toggle** on (the flags in the Toggles column). Exact-word match, so `get` filters by the flag, not by names containing "get". |
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

**For an item** the left side groups its settings by what they do: **Getting it** (Auto-collect, Auto-buy, **Max to get**, Cannot be taken, Auto-open for a container, Auto-obtain for path), **Keeping it** (Must have minimum, **Min. to keep**, Loyal item) and **Getting rid of it** (Auto-sell with its sell-detour options, Auto-stash, Auto-discard), plus the item's on-use message. Hover any box for what it does. The window remembers its size and position.

**Opening.** With **Auto-open** ticked on a container (and Auto-Get Items running), a copy that comes into your pack — picked up, bought or handed to you — is opened for you. MudPlay sends `open <container>`, reads your inventory a moment later, and puts what the container gave on the **Chest Offload** list (Workshop → Record Keeping), where you can sell or drop it; it is said to the room only if that tab's **Say loot to the room** box is ticked.

- **What counts as arriving:** one you pick up, buy, or are handed by another player, and one you dropped yourself and pick up again.
- **One at a time:** several containers arriving together are opened in turn, each read before the next goes out.
- **It waits** for a fight to be over, for a rest or a meditate to finish, and for a sneak that is being kept (an `open` ends each of them), while the client can't send (the board's menus, a password prompt, a mortally wounded character), and after you enter the game until your inventory has been read. The container is opened once that has passed. If you sell, drop or give it away first, or untick its box, it is not.
- **It does not open** a container that is already in your pack when you connect or when you tick its box (ticking the box on one you carry opens nothing, however many you hold); one that arrives while Auto-Get Items or the Auto-All switch is off (switching back on doesn't open it later, and switching off drops any that were still waiting); or one picked up during a Roomba sweep. Open those from Chest Offload or by typing `open`.
- **Coming back after a death or a hang-up penalty:** a container you were carrying unopened is not opened when you recover your pile, or when the hang-up item check picks it back up. One that was still waiting to be opened when you died or the link dropped (picked up in the middle of a fight, say) is opened when it comes back, if its box is still ticked. After a death this lasts while that death is still open in your Death Recovery list, and it is kept only while the client stays running.
- **One try each:** a container that gives nothing and stays in your pack (out of uses, or not yours to use) is left there and not opened again. If no inventory read comes back after the open, the program log says its contents were not read; what it gave is in your pack but not on the Chest Offload list.
- Auto-sell and Auto-discard flags on the items a container gives work as they always do, on the inventory read after the open; the Chest Offload list follows as those items leave your pack.

**Selling.** With **Auto-sell** on (and Auto-Get Items running), walking into a shop room whose shop has the item in its inventory listing sells it straight away — no `list` needed. Selling keeps your **Min. to keep** count when it's above 0, and sells every copy when it's 0 or blank. Your walk or loop waits while it sells (*Waiting — selling*).

**Discarding.** With **Auto-discard** on (and Auto-Get Items running), the item is dropped as soon as it is in your pack. With **Must have minimum** ticked, **Min. to keep** copies stay with you and every copy above that goes; without it, every copy goes. A pile is counted by its copies: carrying 10 with 2 to keep discards 8, as one `drop 8 <item>` on Paradigm and as eight `drop <item>` a few at a time on Stock. They are hidden instead of dropped when **Settings → Other → Hide items when discarding** is ticked.

- **Worn copies count toward Min. to keep.** A piece you are wearing or wielding is one of the copies you're keeping, and it is never the one discarded: only copies in your pack are. Wearing one ring with one more in the pack and 1 to keep, the spare goes; take the ring off afterwards and it stays, since it is now the one you keep. With one worn and two in the pack, 1 to keep discards both spares and 2 to keep discards one.
- **Lights are discarded like anything else** if you flag them, **and the lit one is not protected.** A light you have lit is not worn gear: it counts as one more copy in your pack, and the game chooses which copy a drop or hide takes, so it can be the lit one (which puts it out). Don't flag a light you rely on. Which light is lit is known from your last inventory read, kept up as you take it off or it leaves you; one lit or burnt out since then is counted as it stood at that `i`.
- **The count is MudPlay's running tally**, kept from every get, buy, drop, sale, hide and hand-over it reads, and reset by each `i`. A copy that goes without a line the client reads (used up, burnt out, stolen) is still counted until the next `i`, so with a keep amount set an extra can be discarded in that gap.
- **A pile still waiting to be sent is taken back** when a Roomba sweep starts, Auto Get Items is switched off, the item's Auto-discard is unticked or its Min. to keep is raised. Each waiting command is also checked again as its turn comes, so nothing goes out after the engine is held, after you can no longer see to hide, or once the pack no longer holds a copy above your keep amount. What has already been sent can't be taken back.

**Sell detours.** Tick **Make detours to sell it** (under Auto-sell, once Auto-sell is on) and a walk-to, loop or Auto-Lair will turn aside to sell it once you carry more than the **when carrying more than** count, and more than **Min. to keep**. **0** goes as soon as you carry more than Min. to keep (your first copy when that's blank or 0). **Blank means no detour**, so a red warning appears under the box when detours are ticked with it blank:

- **Which shop:** tick **Sell here** on the shops in the **Bought / sold** list to choose. With none ticked, any shop that trades the item can be used. Among the allowed shops, it picks the one that adds the fewest steps.
- **The trip:** the route stops at the next room, walks to the shop, sells, then carries on. A walk-to heads on to its destination; a loop walks back to whichever of its rooms is nearest the shop and picks up from there, and Auto-Lair walks back to where it stopped. If the sale pushes you over your auto-deposit threshold, it goes **straight to the bank from the shop** and then back to the loop, instead of walking back first and setting off again.
- **On a bank run:** when an auto-deposit comes due while you carry anything Auto-sell would sell, and a shop for it is close to the bank (25 steps by default — **Settings → Cash + Items → Detours**), the trip sells there first, then checks again whether a deposit is still due and goes on to the bank, so the sale's coin is deposited too. This goes for every Auto-sell item, under its detour count or with *Make detours to sell it* unticked: you're going to town anyway. A shop further from the bank isn't worth the walk, and the bank run goes alone. Stash rooms don't do this, only a real bank.
- **Unticking Auto-sell** clears *Make detours to sell it* and its count too, since a detour only walks to the shop and Auto-sell does the selling (batch edit's Auto-sell **Off** does the same).
- **When it doesn't detour:** if your walk ends at one of those shops, or your loop or Auto-Lair passes through one, it just sells on the way. It also waits while you're fighting, resting, paused, following a party leader, or another errand (a bank trip, a train trip, a token route) has the route.
- **A shop that didn't buy it:** a shop that refuses the item ("You cannot sell … here.") or can't be reached isn't tried for it again this session. A shop that just didn't sell it — Auto-sell had nothing to sell there, or no sale reply came — waits 10 minutes before it's tried again. The bug report's *Sell detour* line lists both.

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
- **Room command** lists the commands that can **hand the item over when typed in a room** — `pry coffin — 18.6%`, `mine ore / mine vein / mine copper vein — 25%` — each with the rooms it works in as the same room links. Commands joined with `/` are alternatives. A percentage is the chance per use, an estimate from the game data; `?%` means it's random with no figure; no percentage means the command gives the item outright. What else a command needs (an item to hold or hand in, a level, a price) isn't shown here; it's in the room's command record. A long room list shows the first six with **Show all N rooms**. This group only appears for game data exported by **Nightmare Redux for Linux**, which is the one that lists these; every other export works exactly as before and simply has no such group. When an item has this group, **Given by** lists only the NPCs that hand it over; the rooms are all here.
- A room you **blacklisted** (Game Data → Modify Blacklist…, or right-click it on the map) is left out of **Placed in**, **Room command** and **Given by**; a command with no room left isn't listed.

**For a monster** you can set its **Landmass**, **Region** and **Area** (three type-ahead boxes over the labels already in use — pick one or type a new name; blank means *not set*), its **Relationship** and **Priority** (under *Fighting it*, with **Don't backstab**), and pin its whole **single-target combat chain** for that species in the *Attacks on this monster* table, rung-for-rung with the Settings → Combat spell grid. The window has no splitter to drag and remembers its size and position. Like every other field, the location is saved to the tier you pick in **Use**; a box that still shows the shipped label saves nothing, so an updated shipped label still reaches every monster you haven't re-filed yourself (and clearing a box goes back to the shipped label rather than blanking it):

- **Debuff**, **Spell** and **Alt spell** — each a spell **picker** (type-ahead over your castable spells, commits the cast-code; unlearned spells struck through, as in Settings → Combat) with a per-room **Max casts** cap and a **Min mana** floor beside it. Each lists only what fits it: **Debuff** offers debuffs (0-energy, between-round spells on one enemy or the whole room), **Spell** and **Alt spell** offer attack spells (ones that cost energy — the round's action — on one enemy or the whole room; a room spell is cast bare, with no target). A typed spell of the wrong kind turns the box red with a note saying why, and **Save** stays off until you fix or clear it. The **Debuff** override follows the same rule as the Combat-tab debuff slots: it takes a 0-energy between-round spell only. An attack spell there is refused at cast time with a program-log note. To open on a monster with an attack spell (say `mmis` once, then your weapon), put it in **Spell** with a Max casts of 1.
- **Physical** — a command box (the spell boxes are spell-only; a raw attack verb goes here).

Each configured spell rung **substitutes** its spell for this monster and runs the *same* gated cascade the global slot does: its Max cap, its Mana floor (read as % or absolute per the Combat tab's mana mode — below it the rung holds and the flow moves on), **and** the effectiveness gates — a target immune to that spell, or whose level or element fully resists it, skips it down the cascade exactly as a configured spell would. So an override is no longer a blanket bypass; pick a spell that can actually land.

The **Physical** box replaces the weapon command **only on a round the engine already chose physical** — it does not force physical or suppress the spell rungs, and carries no mana/cap gating.

A monster's **Greet** row shows every keyword you can ask it as a collapsible tree, the same layout as a room spell's **Conditional effects**: expand a keyword to see what happens when you ask it (**expand all** / **collapse all** sit beside the row). A keyword that **teleports** you is labelled `(teleport)` and tinted, and the destination room is a link that opens the map on that room. The read-only pane's **Spawns In** list shows each room's lair size (e.g. `1/2122 (lair: 2)`). Every spell a monster references — its **spell-attacks, per-hit, create, death, and between-rounds** spells — links to that spell's record and shows the spell's number (`[#N]`), and each entry in the **Summons** list links to the summoned monster's record.

(Combat message wording and per-monster flavor prefixes are no longer edited here — hits, misses, dodges, blocks, and deaths are recognized generically from line colour and the experience line, and flavor adjectives come from one shared vocabulary you edit under **Flavor Prefixes** (below), so you never hand-enter a monster's messages or prefixes.)

**Neutral monsters and Kill on sight.** When you set the Relationship to **Neutral**, a **Kill on sight** checkbox appears — a neutral is normally left alone (it never attacks first), but checking this makes auto-combat engage it while leaving other passive neutrals safe to rest among, so the engine can rest/meditate between kills instead of being forced to clear the whole room.

Even *without* Kill on sight, if you hand-attack a passive neutral yourself (a manual swing or combat cast), the engine takes over and finishes it — hitting a neutral turns it hostile, so it's treated like an enemy until it dies and the walker holds in the room — so you don't have to keep swinging manually; the other un-engaged neutrals stay passive and rest-safe.

**What each Relationship does.**

- **Enemy** — fought on sight. A monster with nothing set is an Enemy.
- **Neutral** — left alone unless it attacks you, you attack it, or **Kill on sight** is ticked.
- **Friend** — never attacked on sight. If it attacks you, self-defense fights back.
- **Flee** — never attacked on sight. MudPlay runs from it as soon as that monster is seen in your room by name, when a walk or loop is running. When no run can start, it is left alone unless it attacks, and then it is fought back. Two monsters ship set to Flee: the **gigantic black ooze** and the **huge gruesome creation**. See *How Flee works* below, which also says how to turn it off.
- **Hangup** — MudPlay hangs up as soon as that monster is seen in your room by name. No monster ships set to Hangup; it only applies to the ones you set.

**How Hangup works.**

- **What counts as seeing it.** The monster is on the room's **Also here:** line, or a line says it came into the room. It does not wait for the monster to attack. The hang-up goes out before the combat engine can start a fight in that room.
- **It is the Health tab's hang-up.** MudPlay sends your **Game exit command** and closes the connection. The realm's hang-up penalty line is logged as for any other hang-up. MudPlay does not dial back in, and the next connect stops at the menu so you can read the screen and enter yourself.
- **The wimpy jump takes its place.** With **Sys goto wimpy instead of hanging** set up, MudPlay jumps there instead of hanging up, and you stay connected. As after a low-HP jump, a running loop or walk is not stopped: if it brings you back to the monster, MudPlay jumps again.
- **Disable hangups stops it.** With the toolbar's **Disable hangups** on, nothing is sent (no hang-up and no wimpy jump), and the program log says once that the monster was seen. The monster is still not attacked on sight, but if it attacks you it is fought back (with Auto-Combat on), the way a Neutral monster is. Turn Disable hangups off while the monster is still there and the next change in the room's list hangs up.
- **A fight with a player comes first.** While MudPlay is fighting another player, the PvP actions win: no hang-up goes out for a Hangup monster. The same holds while a player you marked **Enemy** is in the room on a PvP realm, whichever PvP action is set: the PvP response answers that room. The program log says once why, and when the fight ends or the player has left, with the monster still in the room, it is hung up on then. The Health tab's own hang-up works in PvP as it always has.
- **All-off mode stops it.** It follows General → **Allow hangup in all-off mode** exactly as the low-HP hang-up does. With the master switch (Auto-All) off and that option not ticked, a Hangup monster is not hung up on, and the program log says once why. Tick the option and it is. With the master switch on it needs no toggle: Auto-Rest gates the low-HP hang-up only.
- **Once per sighting.** One room display is one sighting: there is one hang-up for it, however many other monsters come and go. A new **Also here:** line (the next room, or the same room displayed again) is a new sighting, and so is a Hangup monster that walks in after the first was answered. This only matters when the first answer did not end the session: a wimpy jump, a missing exit command, Disable hangups or all-off mode.
- **After you reconnect: one minute off.** After the connection has dropped with a Hangup monster in sight (MudPlay hung up for it, or a low-HP or PvP hang-up went out as it was seen), the watch is off for your first minute back in the game, so the monster still standing there does not hang you up at once. This is for a reconnect you make yourself: when the PvP response dials back in on its own, there is no minute off. The minute starts at your first game prompt after reconnecting, not while you log in. While it runs, the status bar shows **Hangup watch off 0:59** in amber beside the connection light, counting down each second, and the terminal has one notice when it starts and one when it ends. (A status bar layout with no connection item shows only the terminal notices.) During the minute, as with **Disable hangups** on, the Hangup monster is not attacked on sight, but if it attacks you it is fought back. When the minute ends, a Hangup monster still in the room is hung up on then. To stay longer, turn **Disable hangups** on or change the monster's relationship. Loading another character clears it.
- **At the board's menu.** If you have left the game for the board's menu, nothing is sent: the exit command would be a menu choice there. A Hangup monster in the room when you come back in is hung up on at your first game prompt.
- **One hang-up at a time.** When low HP, a PvP enemy and a Hangup monster call for a hang-up in the same moment, one goes out. Any other asked for within two seconds of it sends nothing more (no second exit command, penalty line or `@panic`).
- **When no names are shown.** A room too dark to see in, or a move made while you are blind, lists nobody, so nothing happens. In a dark room the monster is first named by an attack line (its own on you, or a party member moving to attack it), and that is when it is seen. A `look` into the next room does not count.
- **Monsters that share a name.** The setting belongs to one monster record. A name on the screen is first looked for among the monsters the game data places in the room you are in, then matched to the first record with that name. As you walk into a room its **Also here:** line is read before the move is confirmed, so the room looked in is the one you are leaving and the first record with that name is usually the one read: set Hangup on that one. Once you are standing in the room (it is displayed again, or the monster walks in) it is that room's own monsters that are looked at. Combat reads the same record. A name that matches no record does nothing.
- **In the log and the bug report.** The program log has one `[MonsterHangup]` line naming the monster and the room for each sighting, saying what was done or why nothing was. A bug report's Session section has a **Hangup-relationship monster** line with the last one and a **Hangup watch hold** line with the seconds left.

**How Flee works.**

- **What counts as seeing it.** The same as for Hangup: the monster is on the room's **Also here:** line, or a line says it came into the room. It does not wait for the monster to attack. What *When no names are shown* says above holds here too: a room that lists nobody (too dark, or you are blind) and a `look` into the next room are not sightings, and in a dark room the monster is seen when an attack line first names it.
- **It is the Health tab's flee.** MudPlay makes the same retreat it makes when you fall below **Run if below**, at any HP: it pauses the walk or loop, sends `break` first when you are in a fight and **Break combat before running** is ticked, and runs **Run distance** rooms the way **Go backwards if running** sets (Settings → Combat). The **Fleeing** sound plays. Nothing in the room is attacked on the way out. No hang-up is sent, whatever happens: the Health tab has no flee that ends in a hang-up.
- **It needs a walk or loop to be running.** A flee runs along what you were doing: a walk-to, a loop or an Auto-Lair run. Moving by hand, standing still, or with no way out it can work out, MudPlay sends nothing on sight and the program log says why. It does not hang up instead.
- **A paused walk or loop is idle.** While you have it paused (the Pause button, a move you typed, a trip held by Stop, Auto-Lair's own pause), nothing is running: no run starts, and the monster is fought back if it attacks. A walk or loop that is only waiting out a fight or a rest is still running.
- **When it cannot run, it fights back.** Whenever no run is coming (nothing is running or it is paused, there is no way out, you are following a party leader, or Auto-Heal and Auto-Rest are both off), a Flee monster that attacks you is fought back, the way a Neutral monster is. So is one still beside you after a run is over: the game refused the run's first move, or a second Flee monster stood where the run landed. It is still never attacked first. This is ordinary self-defense, so it needs **Auto-Combat** on, does nothing in a room marked "do not attack", and a walk-to that is on the move walks on instead of turning to fight. While a run is under way the monster is not fought.
- **Afterwards the walk or loop carries on.** When the last room of the run is behind you, the walk or loop is picked up again at once (after a rest, if your HP or mana call for one).
- **Backwards or forwards.** With **Go backwards if running** unticked the run goes on along your route, out past the monster's room, and the walk or loop carries on from where it landed, so it does not get stuck. Ticked (the default), the run goes back the way you came and the walk or loop comes back through the monster's room: **it meets the monster again, runs again, and keeps bouncing for as long as the monster stands on your route, or on the room a walk-to is headed for**. Nothing stops that: there is no count of runs. It is yours to fix by unticking the box, changing the route, or changing the monster's relationship (below).
- **Turning it off.** Open the monster in Game Data and set its **Relationship** to **Neutral** (left alone unless it attacks) or **Enemy** (fought on sight), then save for the character, the realm, or all characters. The two that ship set to Flee are run from until you change them.
- **Auto-All off stops it.** While the **Auto-All** master switch has every auto switched off, no run starts, even if you have turned Auto-Heal or Auto-Rest back on by hand since. The program log says once that the monster was seen. Press Auto-All again while the monster is still there and the next change in the room's list starts the run. There is no option to allow it in all-off mode. Auto-Combat is off with everything else, so the monster is not fought back either (self-defense follows Auto-Combat, as it does for every monster).
- **It needs Auto-Heal or Auto-Rest on.** Like every flee, it belongs to the health engine, which runs while either switch is on. With both off no run starts, and the program log says once why; the monster is fought back if it attacks. Turn either on while the monster is still there and the next change in the room's list starts the run.
- **In a party.** A follower does not run: it stays with the party, as it does at low HP, and sends nothing on sight (no `@heal`, which asks for HP). If the monster attacks, the follower fights back. A leader or a solo character runs.
- **Hangup comes first.** With a Hangup monster in the same room the hang-up is the answer and no run starts. If no hang-up went out for it and none is coming (**Disable hangups** is on, the minute after a reconnect, all-off mode held it, or no exit command is set), the Flee monster is treated as if it were alone: run from when a walk or loop is running, and otherwise left alone on sight and fought back if it attacks. With **Disable hangups** on and nothing running, both monsters are fought back if they attack.
- **A fight with a player comes first.** While MudPlay is fighting another player, or a player you marked **Enemy** is in the room on a PvP realm, no run starts for a Flee monster. When the fight ends or the player has left, with the monster still in the room, it is run from then.
- **Once per sighting.** One room display is one sighting, and it gets one answer, however many other monsters come and go. A new **Also here:** line (the next room, or the same room displayed again) is a new sighting, and so is a Flee monster that walks in after the first was answered. A run that could not start is not tried again until the next sighting, so a walk you start while standing beside the monster is not turned round.
- **Other cases where nothing is sent.** At the board's menu (the monster is answered when you are back in the game); while you are down at 0 HP or below (it is answered at the next change in the room's list once you are up); when a hang-up or wimpy jump went out in the last two seconds; and when a flee is already under way, which carries on as it was.
- **The other monsters in the room.** While a run is under way nothing in the room is attacked. When no run starts, the other monsters are fought as usual.
- **Monsters that share a name.** As for Hangup: the setting belongs to one monster record, and a name is matched the way *How Hangup works* describes.
- **In the log and the bug report.** The program log has one `[MonsterFlee]` line naming the monster and the room for each sighting, saying that it ran or why it did not, and whether it is fought back if it attacks. A bug report's Session section has a **Flee-relationship monster** line with the last one.

**Where the override saves.** The **Use** dropdown chooses the tier — **Only for this character**, **Only for this realm** (the realm of the BBS your character plays), or **For all characters (global)** — then **OK** writes it and the row's Use column updates to match. Priority when the same record is set at more than one tier is character → realm → global → installed defaults, so a character edit always wins over a global one for that character.

The dropdown also offers **Installed defaults**: picking it and saving **resets the record** — after a confirm it wipes your character, BBS, *and* global edits for that one record and restores the seeded value (the row returns to **Def**). This is the only way to clear a lower-priority edit that a higher tier is shadowing.

You don't have to use it for the common case, though: editing a record's values back to exactly what the installed default was and saving **removes that tier's now-redundant override on its own** (the row shifts back toward **Def**), so an accidental or reverted change doesn't leave a stale override behind.

### Editing a spell's message

Double-click a **Spells** row to edit the spell's player-cast **message** wording (its success and wear-off lines); the spell's own stats are read-only.

For a **damage spell** the Game Data tab leads with an interactive **damage calculator**: a **Level** picker (starting at the spell's learned level, ticking up to its cap) recomputes the min/max damage live so you can watch it grow, and — where they bear on the spell — a **Magic resist** picker (for `Damage(-MR)` spells) and an **elemental resist** picker (Cold / Fire / … matching the spell's type) show how a resistant target cuts it down; a **Spell damage bonus %** picker shows what a caster's +Spell Damage gear adds. The figures follow your realm's own rules (Stock and Paradigm apply these in a different order and round differently).

The read-only record also lists **Negated by** — any items that cancel the spell while carried (the inverse of the Item Finder's *Negates* column). Record references on that tab — Summons, Casts, **End cast**, Cast By, Removes, Negated by, Learned From — are clickable links that open the monster / spell / item record, each showing that record's number (`[#N]`). (**End cast** is the spell this one chain-casts when its effect ends — e.g. poison bolt → the poison-bite DoT — so you can follow the chain to the follow-up spell's record.) **Cast in rooms** lists *every* room that casts this spell on entry (a river's damage-on-entry, a sea's crossing script), each a clickable link that jumps the map to that room — the first 20 show inline and the rest hide behind a **show N more** expander. (These rooms used to appear, capped and unlinked, in the *Cast By* row; that row now keeps only the non-room casters — a monster or textblock that casts the spell.) The two carry-gate rows name the command behind them — **Requires carrying (checkitem)** and **Avoided by carrying (failitem)** — so you can tell "you must hold this" from "hold this to skip the effect" at a glance.

For a **room spell with a scripted effect** (the sea-crossing and river spells, whose behaviour branches on your level and what boat you carry), a **Conditional effects** tree lays out the full percentage-gated logic the flat rows above can't: each **condition branch** — a level gate, a carry check, or a **no NPCs in the room** gate (a spell that only fires when the room is empty), its edge tinted red when it fires because you're *missing* an item and cyan when carrying one steers you onto a safe path — opens to its **weighted outcomes** (each with its `%` chance), and each outcome shows what it does — *summons* a monster, *casts* a spell, or *teleports* you to a room — as a link to that record or map room. Effects are read in the game's **top-down order**, so a step that only happens under a further gate (e.g. *summon crimson mist*, then sea hags **only if no NPCs remain**) nests that gated step beneath the unconditional one rather than lumping them together. A huge random-destination sweep (a spell that scatters you across a hundred sea rooms) collapses to a single summary line like "→ a random room (100 destinations)" instead of flooding the tree — expand that line to see the individual rooms, each a map link. **Expand all** / **Collapse all** beside the *Conditional effects* header open or close the whole tree at once.

A spell's message is stored in the Messages table, but because you edit it here, a **complete** message claimed by a spell in this set is **hidden from the Incomplete Messages tab**. (A message still **missing** a required line surfaces on the Incomplete Messages tab, and a message whose spell link is orphaned stays there too.)

**Putting a spell's message back to the seed.** The Spells tab's **Seed** column shows when a spell's message isn't the plain shipped record: *text edited* (your copy with changed wording — the shipped original is set aside under it), *fields edited* (the shipped message with its flags, links, confuse-fumble line, or cast response changed), *yours only* (a message you added for the spell that the seed doesn't have), or *removed* (you deleted the shipped message). A spell can show more than one. (If you've customised the tab's columns, tick **Seed** in **Columns ▾** to see it.) To work with them:

- **Differs from seed** — a checkbox in the tab's **Filters** sidebar on the right (tick it, then press **Apply**) that lists only the spells whose message differs from the seed. **Reset** shows every spell again.
- **Compare with seed…** — the button beside the Filter box. It compares the **selected** spells' messages with the seed (a selected spell whose message is the plain seed record is ignored); with **nothing selected**, it takes **every** spell message that differs, including shipped ones you deleted. It's greyed out while no spell message differs. Pressing it again while the compare window is open brings that window to the front.

The compare window lists each message on the left with its kind and a choice, and on the right shows the **Seed** and **Yours** versions side by side — Name, the caster / target / witness / applied / wears-off lines, flags, links, fumble line, and cast response — with the fields that differ highlighted (tick **Show unchanged fields** to see the rest, dimmed). Each message starts on the choice that changes nothing:

- *Text edited* / *fields edited* — **Keep mine**, or **Use seed** to drop your version and have the message follow the seed again (it receives future seed fixes).
- *Yours only* — **Keep it**, or **Remove it**.
- *Seed only* (a shipped message you deleted) — **Keep removed**, or **Restore it**.

**Use seed for all** / **Keep mine for all** set every row at once. **Apply** commits the choices and saves; **Cancel** or closing the window changes nothing. The Program Log notes how many messages went back to the seed and how many you kept. (If you edit one of the listed messages while the compare window is open, Apply leaves that one as it is.)

**Confuse fumble.** A message flagged **Confused** also gets a **Confuse fumble** box (below *Wears off*): the line(s) that confuse source emits when a confused character fumbles a just-sent command — one wording per line. On a match, a fumbled *move* reverts instead of stranding the walker, **and** the client re-sends whatever command the fumble ate — a weapon swing, an attack spell, an item use — instead of sitting there getting beat on. An attack is re-sent only until the game answers it with `*Combat Engaged*`: once the fight is under way the game repeats the attack itself each round, so a fumble line then starts no fresh attack (which would break the fight off and begin it again). Heals, buffs, item uses and other commands are still re-sent mid-fight.

A command you typed yourself is never auto-repeated, and a fumbled move self-recovers through the walker rather than a blind re-send. No separate **Last action failed** checkbox is needed for a confusion fumble, since a fumble line already means "the command was eaten." It's seeded with the generic *"You fumble in confusion!"*; enter a spell's own wording (e.g. convulsions' *"You convulse violently"*) here rather than the client hardcoding it.

**Cast response.** Every message also has a **Cast response** box — an engine-driven response sent to the server when that record's spell is detected cast (`^M` = a carriage return, like a Trigger). Its one use is the silent *…temp* death-spells, which emit no line but stall the game engine: a monster whose **DeathSpell** is a temp spell fires this on death, seeded `^M^M` to nudge the engine past the stall. When a room spell of yours kills several monsters in one round it is sent once for the room, not once per kill: a room holds only one such monster at a time.

**Effects checkboxes** tag what condition a matched line *means*:

- **Blinded / Confused / Poisoned / Diseased / Movement prevented** — drive the automatic cures, the navigation pauses, and the party ailment announcements.
- **Fear** — marks a forced-movement debuff (the "You are afraid!" shriek — the game runs you at random through the room's obvious exits until it wears off) so it's tracked as its own condition. While you're afraid your walk, loop or Auto-Lair waits (the status reads *Waiting — afraid*) instead of fighting it, the map follows you through the fear's moves, and navigation resumes from wherever you ended up once it wears off.
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

**On seeds and precedence.** The shipped message seed **refreshes automatically on launch**, so an app update's recurated wording reaches you on its own. Your message edits are **kept on top of the shipped messages**: a set's `messages.json` holds only what you changed — messages you added, edited, or deleted, plus flag / link / cast-response changes to a shipped message — and everything else comes straight from the current seed. So a shipped fix still reaches every message you **haven't** changed, while your own edits keep winning. A message whose **text** you edited is your copy from then on and no longer follows the seed; a message you deleted stays deleted. (Changing only a shipped message's flags, links, confuse-fumble line, or cast response keeps its text following the seed.) Monster names you add from the unknown-monster prompt are kept the same way, on top of the shipped monster list.

A `messages.json` saved by an older version (a full copy of the catalogue) is converted once, automatically, the first time the set loads: messages you added or edited are kept, every other message goes back to following the seed, and the old file is saved beside it as `messages.json.pre-delta`. The Program Log notes what was kept. (A shipped message you had deleted before this conversion may reappear once — delete it again and it stays gone.) To put a spell's message back to the shipped wording, use **Compare with seed…** on the **Spells** tab (see *Putting a spell's message back to the seed*). To drop all your message edits for a set and return to the shipped data, delete that set's `messages.json`.

**Flavor Prefixes** is a small editor of its own in the *Tables + editors* list (not a double-click table). It's the vocabulary of adjectives the game prepends to a monster's name — *large*, *nasty*, *huge*, and so on. The room classifier strips a leading word in this list so "large giant rat" resolves to "giant rat" with no per-monster data.

It starts from the built-in stock list and applies to the **active game-data set**, so a custom realm that uses different adjectives just adds them here (type a word → **Add**; **✕** removes one; **Reset to defaults** restores the built-ins). Edits save to that set immediately. If the classifier ever meets a prefixed name whose leading adjective isn't in the list, it flags a Program-Log row you can double-click to add the word in one click.

**Unrecognized Lines** lists wire lines the Messages catalogue doesn't recognize, staged automatically by the **Capture unrecognized messages** diagnostic (Program Log window, on by default — see *Diagnostics / Log Pane*). It filters out lines the client already handles some other way — party/stat/spell-list screens, the inventory dump, item look text, and benign chatter like player departures, disconnects, follows, socials/emotes, gear swaps by a player in the room or in your party, toll payments, status labels, broadcast-channel listings, counterstrike damage, blows that glance off, other people's dodges and door work, a look at a player (description, gear and wound line), and the tail of a wrapped exits row — so the queue stays focused on genuinely unknown spell/monster/proc messages. A line also counts as recognized when a catalogue entry's applied or wear-off text appears anywhere in it, so an entry that holds only the start of a longer line (a deck of cards reading, say) covers the whole line. Standard command output is skipped too: a shop's `list`, a top list, the `profile` and `abil` readouts, a gang roster and an item's or a room's description are dropped from their first line to the next prompt, along with the wrapped rows of a long *Also here:* / *You notice* list, bank and level-up lines, death and corpse lines, channel join/leave notices, and the reply to a room command you just sent (*You pull the large iron lever.* after `pull lever`), including the passage line a named exit prints (*You pull open the manhole cover, and slip inside the hole.* after `go manhole`). The engine's fixed command replies are skipped as well — usage lines (*Syntax: PICKLOCK {direction}*), refusals (*Why would you want to rob yourself?*), and door, bank, shop, gang and channel notices — but only the ones that can't be taken for a spell message. Lines already in the queue that the client has since learned to recognize are removed when the game data loads. A few kinds of free text can still get through — a door or statue reacting to a lever, a monster's shout. Each row shows two columns: The list is the **realm's**: every character on the realm adds to it, and a line two characters both saw keeps the later sighting. (It used to be kept with the game-data set; each realm took a copy of its set's list.)

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

The left list holds every monster placed in the realm — the same set as the Game Data Browser's **Monsters** tab (records the game marks out of play are on its **Unobtainable** tab, not here). It's filterable by name and shows six columns:

- **Name**, **HP**, **EXP** — the basics.
- **Accuracies** — every one of the monster's physical attacks' accuracies, most-used first, so you see the full spread that feeds Hits You %, not just its best (blank for a spell-only monster).
- **Hits You %** — that monster's chance to land a hit on you, **weighted across all its physical attacks** by how often it throws each, given your live AC/Dodge, your Shadow bonus if you have one, and whichever ward (Prot Evil/Prot Good) applies to its alignment. The detail pane breaks this down per attack.
- **Est. Rounds to Kill** — rounds for the attack you pick in **Edit Attacks** to drop it. The default is **Fastest of all my attacks**: for each monster, whichever of your usable melee attacks (Backstab excluded — it's a one-time opener) or obtained attack spells kills it soonest, so a caster's spells count instead of a weak melee swing. Pick a single attack there to pin the column to just that one. **Backstab** as the pick counts the fight as your opening stab and then normal attacks (`a`): a **sure one-stab kill** — one that always lands and always kills — reads **1**; anything else is 1 round for the stab (at its average damage after the monster's DR) plus the normal-attack rounds to finish what's left. A monster that sees hidden gets no stab, so it's the normal attack alone (see *Backstab* below). With **Max rounds to kill** above 1, that's how backstabbable monsters that take a follow-up round or two make the list. A melee attack projects your live accuracy/damage/swings/crit; an attack spell divides the monster's HP by that spell's expected damage a round — its average (not its best case), lifted by your +Spell Damage % (on Paradigm that includes +1% per 50 Spellcasting above 100) and cut by the monster's elemental resist, its magic resist (for Damage(-MR) spells) and its chance to resist the spell outright, each worked out the way your realm does it. Shown as "—" when no attack you're using can out-damage it (unarmed, fully resisted, spell-immune, and so on).

A monster that would take longer than the **rounds-to-kill cap** (a spinner beside the Hits-You-% filter dropdown, default 999, editable right in this window) is **filtered out of the list entirely**, so you see only fights you can finish quickly — a superboss projecting into the millions of rounds simply drops out rather than showing a noise number. Because that filter is otherwise invisible, an amber note beside the spinner says how many monsters the cap is currently hiding; raise the cap to bring them back.

Raise the cap to include tougher monsters. At the default 999, a monster the selected attack *can't* kill at all still shows as "—" (a different axis — can't-kill, not slow-kill — whose Hits You % is still worth seeing). Lower the cap and those drop out too, since you're asking what you can finish in that many rounds — so Backstab with a cap of 1 lists only the monsters one stab surely kills. Editing the cap re-applies immediately and saves per character.

Every column is independently sortable (click a header; click again to reverse), and **double-clicking a monster opens its full record in the Game Data Browser**. Once a character is loaded, a monster with no computable Hits You % (an NPC/caster-only record with no catalogued physical attack — a trainer, quest-giver, etc.) is dropped from the list entirely — it isn't a meaningful "can this thing hurt me" entry.

A **Hide regen timers** checkbox (beside the rounds cap) drops monsters that respawn on their own timer — bosses, lair leaders, and other timed spawns (any with a non-zero respawn/regen time) — leaving only freely-farmable monsters in the list. Next to it, **Hide 0 exp** drops every monster that gives no experience (shopkeepers, trainers, quest NPCs and the like). Both start unticked each time the window opens.

A **Hits You %** filter dropdown narrows the list by how dangerous a monster's own attack is. It offers a set of contiguous %-bands — each its own discrete range, together covering the realm's whole range with no gap — and you tick **any combination** (multi-select): a monster shows if its Hits You % falls in **any** ticked band (tick the safe end and the risky end to see both while hiding the middle); tick none and every monster shows.

The bands are **realm- and class-dependent**, because the lowest a monster's attack can ever land differs — **8% on Stock, 2% on ParaMUD**, dropping to **1% on ParaMUD for a light-armour class** (the engine lets Silk/Ninja/Leather armour-type classes floor one point lower):

- **Stock** skips the impossible sub-9% bands and starts at `≤9%` (an attack there lands at least 9 times in 99, and at most 98 in 99).
- **ParaMUD** offers `≤2%, 3–5%, 6–10%, 11–15%, …`.
- **A light-armour ParaMUD character** additionally gets a leading `≤1%` band.

The dropdown button shows how many bands you've picked, so the filter fits the realm — and class — you're actually playing.

Select a monster to fill the right-hand detail panel, which has two parts — **Your Matchup** and **Abilities & resistances**.

**Your Matchup** is only shown once a character is loaded. It gathers everything about fighting *this* monster with *your* character:

- **Weapon check** — whether your **currently-worn weapon** is magical enough to hit the monster physically (its HitMagic vs the monster's requirement).
- **Incoming threat** — a **Melee** line (the monster's chance to hit you, its damage per hit, its **attacks per round**, and its damage **per round**) plus one line per element it casts alongside how much your own worn gear resists it.
- **Your physical attacks** — each usable type (Normal, Bash, Smash, Martial Arts) with its **rounds-to-kill**, per-round damage, hit %, and damage-per-hit against this monster. **Backstab** gets a one-stab verdict instead (below).
- **Your attack spells** — every spell you've obtained, ranked by **mana efficiency** (damage per mana, rounds to kill, total mana to kill, per-round damage), **split into single-target and AOE groups**. A spell blocked by the monster's spell immunity, fully resisted by its element, or restricted to undead-only/living-only targets it doesn't qualify for shows the reason instead of a damage number.

The **Edit Attacks** button (top-right of the window) drives this list: check which attacks appear here, and pick (the radio) what fills the master list's **Est. Rounds to Kill** column — **Fastest of all my attacks** (the default) or one specific attack — so you can weigh "how many rounds if I nuke it with my best spell?" against "if I just swing my weapon?" The attack you picked there is **highlighted** in Your Matchup (a cyan wash with a bar down its left edge), melee line or spell row, so the attack you're judging monsters by stands out. For the full attack-type-by-attack-type melee breakdown with editable what-if inputs, use the Player Workshop's **Calculators** tab.

The **Apply Debuffs** button (just under Edit Attacks) folds your known enemy debuffs onto the *selected* monster as a what-if: check any of your stat-affecting debuffs — the ones that lower a monster's **AC**, **DR**, **Dodge**, or **accuracy**, or **slow** it — and every number in Your Matchup (your hit %, rounds-to-kill, and the monster's Melee threat line) recomputes against the softened target, with a banner naming what's applied.

**Backstab** is judged on the safe side, as a single opener:

- **Damage:** your **minimum** stab after the monster's **DR** must reach its HP. A 37 minimum against a 35 HP monster with 5 DR leaves 32, so it isn't a one-stab kill.
- **To-hit:** the stab is rolled against the monster's **backstab defence** (a quarter of its AC plus its BS Defense), not its full AC.
- **Verdict:** **sure one-stab kill** when the min kills *and* the stab lands as often as the game ever allows: **100% on Paradigm**, **99% on Stock** (Stock never lets any attack be certain, a stab included: its best is 98 in 99). Otherwise it says *one-stab kill if it lands*, *kills only on a high roll*, *can't kill it in one stab*, or *it sees hidden* (a see-hidden monster spots your sneak, so no surprise lands).
- **The working** — your range before and after DR, and the to-hit — sits on a second line.
- **Weapon:** if your Equipment Manager's **Backstab** set names a weapon, the stab is worked out with that weapon (the one you'll actually swing), and the line says so.

The **Apply Buffs** button (under Apply Debuffs, shown when you know one) is the same kind of what-if for **your own** buffs: check a buff that raises your **Stealth**, **accuracy**, **backstab accuracy / min / max**, **max damage** or **crits** — a Gypsy's **shadowform**, say — and every attack in Your Matchup and the Est. Rounds to Kill column is worked out as if it's up, with a banner naming what's counted. The game **rolls a buff's bonus when it's cast** (shadowform's backstab bonus, smite's max damage), so each buff is counted at the **bottom of its roll**: a kill called *sure* is sure whatever you rolled. The picker shows the roll as a range (*+5–10/+5–10 BS min/max*), and an attack's detail line adds what the top of the roll would reach when that differs. A buff that's **already on you** is in your `stat` Stealth already, so only its other bonuses are added. Your picks save per character.

Debuffs stack and can push a stat **negative**: drive a monster's accuracy below zero and it can't hit you at all; drive its AC below zero and your accuracy benefits a lot. Slowness raises the monster's attack energy, thinning its attacks per round. This is a preview only — the client never applies these effects in live combat — and your picks save per character.

**Abilities & resistances** shows the monster's *own* properties (character-independent, always shown when the record carries any):

- its **elemental profile** as weakness vs strength (e.g. `Fire +50% (resists)`, `Cold -25% (weak)`; `100%` = immune, over `100%` = healed by that element instead);
- whether it needs a **magic weapon** to hit (and the HitMagic level), its **spell-immunity** level, **damage resist** and **magic resist**;
- **undead** / **non-living** (immune to life-drain), and any other notable abilities (see-hidden, fear, confusion, poison, and so on).

This is the quick "what is it" summary; the full record (loot, every placement, the overlay editor) still lives in the Game Data Browser.
- **Your Observations** — only shown once this character has actually fought the monster at least once: landed-hit damage extent and average, weapon hit rate, and how many times a physical attack or a spell had **no effect** — a real, confirmed discovery that this monster's Magical or SpellImmunity requirement is higher than what you're using against it. The physical count is retired the moment one of your swings lands damage on that monster: the "no effect" was true of the weapon that drew it, not of the monster, so a weapon that does get through settles the question. The hit rate is meant to count weapon swings only (swings landed out of swings made). A spell that lands is not counted as a missed swing; a cast that fails to land can still read as one, because the game's line for it looks the same as a miss. Older versions took every spell cast for a missed swing, so the saved swing counts (hits and misses) were cleared once, the first time each character was loaded after the update that fixed it, and count afresh from there; the no-effect discoveries were kept. This is deliberately kept separate from Your Matchup — that comes from the game-data record (the MDB); this is only what *this character* has personally seen happen in combat. A **Clear** button wipes every monster's recorded observations for this character (not just the one you're viewing).
- **Attacks** — every physical, spell, and rob attack slot with its chance, its damage, accuracy, and energy cost, plus its between-round spells — how dangerous is its swing, beyond the bare Hits You % number. A **spell attack** now shows the **computed damage** for its cast, not just the spell number: the linked spell's formula scaled to the monster's assigned cast level, for a **single cast** (a monster casts its spell once when the attack lands — how often it fires rides the monster's own attack energy, so the spell's own player-side energy cost is not folded in). A poison/blind/hold spell or other pure-effect cast shows no damage figure (it deals none), and the same computed range appears on the between-round spells. Each spell also gets a line on what **your resists** do to it: the damage you'd take after your Magic Res and your resist to the spell's damage type (cold, fire, stone, lightning, water — and poison on Stock), with what caused the change (e.g. *you take 20-30 dmg (MR 120 −35%, fire resist 25%)*), plus your chance to resist the spell outright when it can be. A spell your Magic Res does nothing to is marked **ignores MR**. Resists are counted from worn gear, race, class, completed quests and the buffs you have configured. The **Resists (what-if)** pickers in the defence row (Cold, Fire, Stone, Lightning, Water, and Poison on Stock) start at those figures; change one to see what a spell would do to you in a resist set. With no character loaded it only says *cut by MR* / *can be resisted* / *ignores MR*.

---

# Conversation

Press **Alt+C** to open the **Conversation** window — a dedicated view of all the chat MudPlay pulls out of the terminal, with its own input box so you can talk without hunting for the game prompt. Alt+C again closes it (or brings it forward if it's buried).

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

Type into the input box at the bottom and press **Enter** (or click **Send**) to send the line to the game — you still type the game's own chat commands (`gos hi`, `/bob hey`, and so on). This is the input box where your **aliases** expand and where `;` or `^M` splits one line into several commands. A `;` that starts a word is not a separator: it is sent to the game as typed, so the game's own commands that begin with one (`;o`, or `/bob @do ;o`) go out whole, and so does a `;)` in chat. The same holds in the terminal, and in macros, aliases, triggers, events, loop commands and the pre-/post-rest commands. To send such a command after another on one line, put a space before it: `n; ;o` (or `n^M ;o`) sends `n` and then `;o`. In a line you type you can also double it: `;;time` sends `;time`, and `n;;time` sends `n` and then `;time`. (Macros, aliases, triggers, events and loop commands don't read `;;` that way: there a doubled separator is an empty step.)

**`;o` and `=x`.** Sending `;o` (typed, from a macro or alias, or relayed by a remote `@do`) drops the connection and MudPlay dials straight back in and logs in again, whatever the BBS tab's reconnect triggers say. Sending `=x` drops the connection and MudPlay stays off until you connect again. Either counts only if the board hangs up within a few seconds of it.

**↑ / ↓** recall what you sent before, and the chevron at the right edge of the box opens a list of recent commands to pick from. **Tab** completes the word at your cursor against your carried, worn, and key-ring item names, the same as the terminal (**Settings → General**) — press it again, or **Shift+Tab**, to step through other matches.

## Logging and history

The window keeps its history even after you close it, and replays your last session's chat when you reconnect. To save chat to a file, turn on **Settings → Talk → Log conversations** — it writes to the `Logs` folder, which you can open from **Tools → Open logs folder**. **Clear All**, beside Auto-scroll, wipes the whole history and the saved copy after one confirming click; **Tools → Clear chatlog** on the main window does the same without asking. The chat font and channel colors are set on the Talk tab and apply live the moment you hit Apply — an already-open window re-fonts and recolors on the spot.

---

# Tools & Diagnostics

A few smaller windows for reviewing your session and troubleshooting. Each is modeless: pressing its key again brings it forward if it's buried, or closes it if it's already in front.

## Program Log (F4)

Press **F4** (or **Tools → Program Log…**) to open the **Program Log** — a running, timestamped record of what the engines are actually doing, and the first place to look when something automated didn't behave. Each row is tagged with a severity and the source engine.

- **INF / WRN / ERR** — severity filters; tick the ones you want to see.
- **Search** filters the rows by source or message text; **Clear** empties the view; **Auto-scroll** keeps it pinned to the newest row.
- **Debug** and **Combat** are *generation* toggles (not just filters): they turn the verbose cross-engine trace and the combat-decision channel on or off across the whole app, and show those rows here. Both are **on by default** and persist per character — leave them on for the richest diagnostics; turn one off to quiet the noise. (These are the same two channels you'll see in a bug report.)
- **Tick timing.** With Debug on, every HP or mana gain is logged with its size, the gap since the last one, your posture, and how long after a combat round it came. A bug report also carries a **Tick timing** section: the last 400 combat rounds, HP / mana gains, posture changes and a room's own damage (its heat, say), to the millisecond. To capture a realm's tick cycle, stand still for a couple of minutes, then rest, then meditate, with a fight or two in between, and take a bug report.
- **Auto-collect logs** writes the program, memory, combat-trace and performance files to the Logs folder for the session (off by default, so a normal run leaves nothing behind). The Exp/Hr Estimator's **Check against my play** reads its loop history from these program logs, so leave it on if you want that check. **Hop timing** logs one line per confirmed room hop with its measured wall-clock time — used to tune the Auto-Lair travel-cost table.
- **Log session statistics every N min** writes everything the **Session Stats** window shows to its own file in the Logs folder, on that interval, without the window having to be open (off by default). See *Log session statistics* under Diagnostics / Log Pane.
- **Simulate buttons** is a dropdown of test-only toggles, each revealing a hidden **Simulate …** button on its feature window (all off by default and reset off every launch, so a normal session never shows them): **Simulate Death button** (Player Workshop → Death Recovery tab), **Simulate Chest button** (Record Keeping → Chest Offload — seeds a few random containers so you can exercise the window without real chests), and **Simulate entry button** (Game Data → Unrecognized Lines — feeds a synthetic unknown line through the capture flow so a candidate appears, letting you see the feature work without waiting for the game to emit one).

## Backscroll (Alt+L)

Press **Alt+L** to open **Backscroll** — the full terminal history, including lines that have scrolled off the top, on a timestamped transcript that opens at the newest line.

- **Search** — type a term and press **Enter** (or **Find next**) to step through matches, newest to oldest, wrapping back to the top. The footer shows the line count and how many matches were found.
- **Jump to end** — return to the newest line.
- **Export…** — save the whole transcript to a text file, each line prefixed with its timestamp.
- Drag to select a region, then **Ctrl+C** (**⌘C** on macOS) or **right-click → Copy** to put it on the clipboard as plain text. Right-click → **Select all** grabs the whole transcript to copy at once.

Backscroll is a **snapshot taken when you open it**, not a live tail — to pick up newer output, close and reopen it (nothing is lost in the meantime). The transcript renders in your **terminal font** (family and size), so history looks exactly like the live screen; that font is captured when the window opens, so changing it takes effect the next time you open Backscroll.

## Session Stats

Open **Session Stats** from the **View** menu or its toolbar button (it has no default hotkey — you can assign one on Settings → Shortcuts). It tracks this session's performance in a stack of panels: **Kills/hour** and **Exp/hour** graphs, an **HP/MA per loop step** chart, and **Player Statistics**, **Time Analysis**, and **Session Statistics** tables (kills, experience, currency, and time spent moving, resting, and fighting). While a loop is running, the **Time Analysis** panel also shows a **Loop laps** readout — one lap is a full completion of the circuit — with the laps completed, last and average lap time, the live current-lap timer, and the room each lap starts at.

To keep a record of these figures over a long run, tick **Log session statistics** in the **Program Log** window (F4): it writes all three stat panels to a file every few minutes, whether or not this window is open.

The **Player Statistics** panel is your own combat, read off the same round ledger that prints *Show combat round totals*, so the two always agree on whose damage a line was. **Offense** shows your regular attacks (attack, martial arts, bash, smash) as **Hit / Miss / Crit** with their rates and damage. **Backstab** and **BS miss** keep their own rate over the stabs you attempted: a stab fails when it misses, or when the sneak broke and the round swung as a normal attack (that whiff is the stab's, so it isn't a regular miss). Then come your per-round damage, your **procs** (every proc the ledger credits to you — a weapon's "Your weapon sears…" or a proc that names only its victim right after your hit), and **one row per spell** you've landed, showing its damage range, cast count and accuracy. Any spell your class can learn gets its row, not just the ones in your Combat-tab attack slots, so hand-cast spells no longer count as swings. Spells and procs never count as swings: a cast's flavor line ("You scatter some ashes…!") isn't a swing miss, so a caster's miss rate reflects real resists rather than one phantom miss per cast. A spell that chains to a second one counts both lines as one cast: necromantic bolt's drain adds to the bolt it followed. **Per-round damage** is only what *you* dealt. **Defense** shows **Hit by** — every blow that landed on you, whatever its wording, with its damage range, average, and the share of incoming attacks that hit — and **Dodge/Miss**, the share you avoided. Damage nobody dealt (poison ticks, falls) isn't a blow.

The **Time Analysis** panel splits the session's time into moving, attacking, resting and waiting, with the time spent under each ailment. Below that:

- **Sneak** — the share of rooms you entered while sneaking where the sneak held (the room showed `Sneaking...`). A loud entry, or a room that showed without `Sneaking...`, counts as a lost sneak. Hover it for the counts.
- **Disarm Trap** — how many traps the client disarmed this session, then the share of its `disarm trap` attempts that worked. A trap going off counts as a failed attempt. On Stock, `You failed to disarm any trap…` also answers an exit with no trap, so it only counts as a failure once a later attempt on that exit disarms the trap or sets it off. Paradigm's `The trap is already disarmed.` isn't an attempt: the exit is taken as clear. Disarms you type yourself aren't counted. Hover it for the counts.
- **Walk Latency** — the average time per walk or loop step, from the move going out to the new room showing (how long the server takes to answer a move). Time stopped between steps (a fight, a rest, a door, a gate) doesn't count, and a step that didn't land isn't timed.
- **Loop laps** — while a loop runs: laps completed, the last and average lap time, the live current lap and the room each lap starts at.

Each panel's **Reset** clears everything under it and nothing else: Time Analysis's clears the time breakdown, Sneak, Disarm Trap, Walk and the loop laps (a running loop's current lap keeps ticking); Session Statistics' clears its totals and restarts its per-hour rates.

The **Session Statistics** panel, modelled on MegaMUD's statistics screen, is in three groups:

- **Kills & experience:**
  - **Kills**, **Kills / hour**, **Experience** and **Exp / hour**: this session's totals and their per-hour rates. A room spell's kills count one each, as they happen.
  - **Exp needed**: the experience still to earn for the level the countdown is heading for, with that level in brackets. It counts banked levels, so it's the first level your exp hasn't reached, not merely the next one to train.
  - **Will level in**: the time to get there at this session's exp rate, the same countdown as the status bar's TNL and your Party-window row.
- **Coin**, as denominations with the number of coins in brackets. Hover a value for the exact amount.
  - **Collected**: coin you picked up.
  - **Deposited / sold**: coin you banked, by hand or by auto-deposit, plus coin from items sold. Deposits are counted from the game's own `You deposit …` replies.
  - **Stashed**: coin you hid, by hand or by the stash automation, counted from the game's `You hid …` replies. Both are the same replies the **Transaction history** records.
  - **Income / hour**: coin picked up per hour.
- **Items:**
  - **Collected**: any `get`, yours or the automation's.
  - **Sold**: items you sold.
  - **Stashed**: items you hid, by hand or by the stash automation.

All of these reset with the rest of the session (connect, character switch, **Reset session**, the panel's own **Reset**, an `@reset` from the party, and a loop start when *Reset statistics on loop start* is on).

- **I / II** (top right) sets one column or two side by side: with every panel open, two columns keep the window on your screen. Going to two widens the window; drag a panel by its title across to the other column to move it there. Saved per character.
- **Every rate** (hit / miss / crit / backstab, spell accuracy, hit by, dodge, sneak, the HP / MA graph's low) shows to a tenth of a percent, and never reads 0% or 100% unless it exactly is — a single miss in a thousand swings shows as 99.9%, not 100%.
- **Every panel starts collapsed** — click its title to open it (the graphs' titles still show the current kills/hour and exp/hour). **Right-click** the panel area to show or hide individual panels, and **drag a panel by its title** to reorder them (a line shows where it will land; drop below the last panel to put it last). Which panels are open, their order and which are hidden are all saved per character.
- The window **sizes itself to show every open panel** whenever it opens and whenever you open or close one, up to your screen's height (and moves up if it would run off the bottom). Only more than a screenful scrolls.
- **Reset session** zeroes every counter and restarts the clocks; individual panels have their own **Reset** too. (These don't ask for confirmation.)
- **Transaction history** and **Players Seen** open the detailed ledgers — coin banked and stashed this session, and every player you've encountered. In the Transaction history, coin you stash in a room is **one row per stash room** rather than a row per stash. It carries the time of the latest stash and three lines: **Last** (what you hid that time, each coin type) with **Total Stashes**, **Avg** (the average stash, in the highest coins), and **Total** (everything hidden there, per coin type, followed by what it all comes to in the highest coin, e.g. *(≈ 8.7 platinum)*). A **stash transfer** draws on that same row: each load sets **Last** to what was taken (*Last: took 2 platinum, 2,998 silver*) and **Total** to what the search showed still in the stash afterwards (*nothing* once it's empty), so Total is the stash as it was last seen, whichever way the last change went. **Avg** stays the average of what you hid. A stash with no row yet doesn't get one from a transfer. Rows from an older log are folded in the first time it loads, and **Clear** starts the count again. Items you hide still get a row each. **Selling and buying** are recorded too, one row per shop visit listing the items and what they came to (*Sold orc-head ×7, club for 16 gold, 5 silver*). Players in your party at the time don't count as seen; once someone leaves the party, seeing them counts again. In the transaction ledger, **stash** entries are tinted faint gold (the map's stash-marker colour) so they stand out from bank deposits, and **double-clicking any entry** opens the Navigation map centred on the room where that deposit or stash happened. Each row has a **Keep** checkbox, saved with the row so it's still ticked after a restart: check the entries you want to hold onto, and **Clear history** wipes everything *except* those — a way to prune a full ledger without losing the rows that matter (with nothing checked it clears the whole thing, as before). The clear updates the on-disk log too, so kept rows survive a reconnect and cleared ones don't come back.
- In **Players Seen**, double-click a row to show where that player was last seen: the Navigation map opens (or comes to the front), centres on the room and flashes it green for a few seconds, the same way an answered `@where` does. A sighting with no room recorded (shown as `—`) has nothing to show.

## Round Totals

Open **Round Totals** from the **View** menu or its toolbar button (it has no default hotkey — you can assign one on Settings → Toolbar + Shortcuts). It is a small window showing the last combat round's damage table: who **dealt** and **took** what, one row per combatant, with your own row picked out.

- **Always on.** Every round lands in the window while it is open, whether or not **Settings → Combat → Show combat round totals** is ticked. That checkbox only decides whether the table is *also* printed in the terminal.
- **Its own rows.** The **Rows** button in the window ticks which kinds of row it shows — **Me**, **Party**, **Other players**, **Monsters** — and **One row per monster** (off: same-named monsters share a row, `muckworm x3`; on: `muckworm #1`, `#2`, …). The same menu has **Cap at monster HP**: on, a monster's damage taken (and its attacker's damage dealt) counts only up to the HP it had left. These are saved for the character and are separate from the terminal table's boxes, so the terminal can print only your own row, uncapped, while the window shows the whole room capped, or the other way round. (Session Stats follows the Settings → Combat cap.)
- **Steady size.** The window fits the table, but it doesn't jump around as the room changes: it grows at once for a bigger round and only shrinks after ten rounds in a row have been smaller, so stepping between a packed room and a near-empty one leaves it where it was.
- **Out of your way.** It opens without taking the keyboard from the terminal, and it remembers where you put it. If it was open when you closed MudPlay, it comes back open.

Before the first round of a session it reads *Waiting for a round of combat.* See *Show combat round totals* for how the numbers are counted.

---

## Buff Watchdog

Open **Buff Watchdog** from the **View** menu (right after Party) or its toolbar button — it has no default hotkey, but you can assign one on Settings → Shortcuts. This is the **one place you configure every automated buff** — self bless, party bless, room light, mana-regen, and the "when HP/MA full" utility casts all live here now, in a single unified list — **and** it shows a live timer bar for each one as it runs. Re-selecting the menu item (or toolbar button) brings it forward if it's buried, or closes it if it's already in front.

### Before you can add a buff: your spell list

The Buff Watchdog only offers buffs you have **actually learned**, and MudPlay learns that from the game's own spell list (`sp`, or `pow` for a kai class). On a **new profile** nothing is known yet, so there is nothing to add:

- **MudPlay reads the list for you** the first time it sees your stats in the game and knows of no learned spell. You'll see `sp` go out in the terminal.
- **If the window still has nothing to add**, it says why in plain words, and when the list simply hasn't been read it shows a **Read my spell list** button that sends the command. Typing `sp` yourself does the same.
- **After that it stays current on its own**: a spell you learn from a trainer or a scroll is picked up as it happens.

The other reasons it gives: your character hasn't been read yet (enter the game first), your class has no buff spells and you carry no item that casts one, none of the spells you've learned is a buff, or every buff you've learned is already on the list. The **Spell Book** (F2) shows the same learned / not-learned picture for every spell of your class.

### Building the buff list

Click **＋ Add buff** to open the Add-buff dialog:

- **Pick a buff** — a **dropdown**, not a text box: it lists every buff spell you've actually **learned** (attacks and heals filtered out), each shown as its **name and the level you learned it at** (e.g. *bless (Lvl 2)*), plus any **cast-on-use buff item** you can actually use — one you own (carried or worn) and meet the level for (an unlimited-use item like a *shimmering greatsword* that casts a buff when used; these show as a `#item` entry). A buff that's **already slotted** stays in the list but is **greyed out / unselectable**, so you can see it's taken rather than wonder where it went. The list is ordered by learn-level, low to high. What targeting a slot offers depends on the spell: a self-only spell can only be cast on you, a single-target spell can be aimed at you and/or party members, and a whole-party spell (chant and the like) blankets everyone with one cast.
- **Set a recast timer** — "recast (s)" recasts the buff that many seconds before it expires (0 = wait for it to actually wear off). It can also be **negative**, which recasts that many seconds *after* the buff wears off — e.g. `-30` on a 60s buff recasts it every 90s, letting it lapse on purpose to **spread out mana use**. The Watchdog bar shows the post-expiry wait as a **red** extension (see *The buff bars* below).
- **Set conditions** — per-slot gates. The first four are on every buff; the rest only appear for the spell that uses them:
  - **Priority buff** — off (the default), the buff casts with your other buffs, at the **Buffing** row of the spell type priority (Settings → Spells). Ticked, it casts at the **Priority buffs** row instead, which by default sits between the major and the minor self heal. Tick it on the one or two buffs that matter more than topping off a little HP. A priority buff has a **★** beside its timer bar.
  - **Cast if mana ≥** — this buff is only cast once your mana is at or above this value (default **50%** for a new buff), so mana recovers past a floor before it goes on upkeep. Each buff has its own: a cheap bless can go out at 30% while an expensive one waits for 80%. `0` never holds it back. It is a percent of max mana, or a raw mana / kai amount when Settings → Health reads its mana thresholds as amounts (the label drops the `%`). Beside the box is what it comes to against your max mana right now, e.g. *125/250*. A free item-cast buff ignores it.
  - **Cast while resting** — off (the default), this buff waits out a **triggered recovery rest** (HP or MA fell below your rest-if-below setting and you're resting back up); ticked, it is cast during one too. An idle rest never holds a buff. The cast stands you up for a moment; MudPlay lies back down and rests on to your rest max.
  - **Cast during combat** — off (the default), this buff waits until the fight is over; ticked, it is also cast mid-fight (the cast spends that round's between-round slot). Tick it on the buffs worth a round and leave the rest for after the fight.

    All three apply to **every cast of that buff**, solo or in a party: the Self cast, a cast on a party member, a whole-party spell, and a mana-regen reroll. They replaced the shared *Bless if above* (Settings → Health), *Bless self while resting / during combat* (Settings → Spells) and *Bless party while resting / during combat* (Settings → Party); each buff you already had took the values you had there.
  - **Only when HP is full** / **Only when MA is full** — hold the cast until you've rested up to your **rest-max** target (not literal 100%); a "topped-off, ready for the next fight" buff. A triggered recovery rest suspends it until you're back at max.
  - **Only when the room is dark** — shown for a **light** spell. Ticked, it keeps the reactive cast-on-entering-a-dark-room behaviour (via the auto-light system); unticked, the light is maintained like any ordinary buff.
  - **Cast before resting for mana** — shown for a **mana-regen roll** spell (nature tap / mana flux / prfl). Ticked, the buff is only kept up **while you're resting for mana**: it's (re)cast when your mana drops below its rest threshold and recast on expiry through the whole rest — including if a fight interrupts the rest — and stops once your mana tops back up. Unticked, it's kept up all the time like a normal buff. (It also carries the reroll knobs, below.)
  - **Keep these when drawn** — shown for a **draw item**, one whose use deals one of several buffs at random. On Paradigm that is the Gypsy's **deck of cards** (Stock's deck can't be redrawn, so it has no tick boxes; see *How a deck slot runs*). There is one tick box per buff it can deal, each with its chance; hover a box to see what that buff applies and how long it lasts. A ticked buff is kept when it is drawn. An unticked one makes MudPlay use the item again on the next between-round cast, and the next, until a ticked one lands. At least one box has to stay ticked.
- **OK** adds it as a slot.

**How a deck slot runs.** Using the deck takes the between-round cast slot, like any buff spell, so a re-draw comes one combat round after the last. Each new draw replaces the card you had. Once a ticked card lands, the slot holds for that card's own duration (less the recast timer) and then draws again; if the card wears off early it draws again at once. The deck is used straight from your pack — nothing is equipped or swapped — and each use takes one of its 9,999 charges, re-draws included. **Stock's deck works differently.** It has no shuffle: used while a card is still on you it answers *Nothing happens.* and deals nothing, so a card can't be drawn over. There are no tick boxes for it, and **Recast** only takes 0 or a negative number. MudPlay uses it once, keeps whatever card it deals, and uses it again when that card has worn off. If a use shows no card (one was still up), it waits three minutes before trying again: on Stock that refused use still costs one of the deck's 100 charges.

While a card is up, the slot's timer row is named for it (*Knight*, *Priest*) instead of the deck, so with several cards ticked you can see which one landed. It is not added by **Add all blesses**; add it yourself so you choose the cards.

A mana-regen roll spell (nature tap, mana flux, profane link, and kin) rolls a random regen contribution each cast, so the "Cast before resting" condition also carries **reroll knobs** to chase a good roll:

- **Reroll below abil 145** — a threshold: reroll while the spell's rolled contribution (read off `abil 145`) lands under it. That value can be negative, so "reroll below 0" chases a non-negative roll. The box only accepts values the spell can actually roll **at your level** — the range is shown under it (e.g. *rolls -64 … 216 at your level* for mana flux), and it's the same for nature tap and every other roll spell. On **Stock** the box is labelled **Reroll below roll** and takes the same rolled value. Under it is the list of what each mana tick needs, e.g. *6 MP/tick at worst · 7 from 12 · 8 from 37*. The tick is whole MP, so only those step values change what you're paid, and they're the useful thresholds. A Stock threshold saved before this (it used to be a mana tick) converts once to the roll that pays that tick, and the program log notes it.
- **Max rerolls** — how many times to chase a better roll before accepting what landed.
- **Reroll infinite** — a checkbox just below Max rerolls: keep re-casting until the roll clears the threshold, no cap (ticking it greys out Max rerolls).

Each reroll re-casts the spell, so it costs mana; if you run out mid-cycle the reroller **pauses rather than giving up** — it waits while you meditate back up, then resumes, so it spends its full budget instead of settling for a bad roll. It also **holds its rerolls during a fight**: every reroll is a cast between rounds, which turns combat off (and breaks a running room spell), so a roll that lands mid-fight is rerolled once the fight ends instead.

Rerolling works on **Paradigm** (reading the roll back from `abil 145`) and on **Stock**, which has no `abil 145`. Stock reads the roll back off your next natural mana tick (every 30 s) from your level, stats, class and worn +mana regen:
- **Meditating is fine.** A meditate tick pays the same base every time, so the client subtracts it from the combined jump when the two land together.
- **A roll so bad it pays nothing** shows no tick at all. No tick within 40 s, with mana below max, counts as one.
- **The tick is whole MP**, so it only narrows the roll to a band (e.g. 50–58). The client rerolls only when the whole band is below your threshold.
- **Ticks it skips:** one that fills your mana to max (cut short), and one that lands in the middle of a gear-set swap.
- **Gear sets are accounted for.** A mana-regen set's +mana regen, and its INT / WIL / CHA, are taken from what's worn when the tick lands. **Raising the cap / threshold (or ticking infinite) also re-checks the roll spell that's already up** — if its last roll now falls short, it rerolls right away. (A mana-regen roll spell is a self-only cast, so the caster always gets the roll whenever it fires.)

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
- **Alignment-aware** where it can be: a class's spell list often carries both sides of a holy/unholy pair, and once MudPlay knows your alignment (from a `who` that showed you on this realm), only the side you can cast is offered — a "non-evil only" spell is hidden from an evil character, a "non-good only" one offered to them same as a neutral.

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
- A few single-target buffs print a cast line that doesn't say who it went on (*angelic halo* answers just *"You cast angelic halo!"*). For those the member's bar starts **when the cast is sent**, not when a line confirms it, and is taken back if the game refuses the cast or can't see the member.
- A small **✕** on a live bar **clears that timer** — marks the buff off (e.g. when a dispel you didn't see stripped it). A configured buff that's still due recasts on the next pass; a leftover timer (say an ex-member's, still running) just disappears. A timer on someone who has left the party goes on its own once it has run out, and any negative recast wait after it has passed; while it is still running it stays until you clear it. The ✕ only shows while a timer is actually up.
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

The window is a live view — it refreshes about once a second while open. *When* a buff may cast — its mana floor, and whether it casts in a recovery rest or a fight — is set on the buff itself, in its edit dialog, and holds for casts on you and on the party alike.

## Wire Inspector (F5)

Press **F5** to open the **Wire Inspector** — a troubleshooting view of the data the server sends, in up to three panes you toggle with the **Raw / Stripped / Classified** checkboxes:

- **Raw** — control codes made visible (e.g. `^[` for escape).
- **Stripped** — the same stream with the ANSI escape sequences removed.
- **Classified** — each combat-window line tagged with how the combat engine read it (e.g. `[Combat: Monster Miss (you)]`, `[Combat: You Hit]`, `[Combat: Armor Block (you)]`, `[Combat: Damage (you)]` for damage nobody dealt such as poison or a fall, `[Combat: Smashed (other)]` when a smash's penalty lands). Every **damage** line also shows how the round ledger credited it: `[Ledger: Bob → large orc 9]` (who dealt it → who took it, and how much), `unknown` for a side the line doesn't name, `no attacker` for damage nobody dealt (a poison tick, tagged `[Combat: Damage (you)]`), and `not counted (no round)` for a line that fell outside a combat round. A party member's fight shows here too, even when you aren't in combat yourself. See *Show combat round totals* under Settings → Combat. It also marks each **recognized monster death** with `[Monster Death: <name>]`, and an exp-inferred death whose message *wasn't* recognized as `[Monster Death: inferred from exp — message not recognized]`, so an unrecognized death line stands out.

**Raw and Classified are on by default** (Stripped off); unchecking a pane collapses its column so the others fill, and your choice sticks. It shows inbound server output only, and keeps the most recent 64 KB.

- **Pause / Resume** freezes the view so you can read it; **Clear** empties the buffer.
- **Auto-scroll** keeps the panes pinned to the newest bytes, and **Sync scroll** ties the Raw and Stripped panes' scrolling together.
- **Find next** locates a term in the Stripped pane, and **Export raw… / Export stripped… / Export classified…** save any pane to a file.

Reach for this when reporting a display or parsing glitch — it shows exactly what arrived on the wire. Because **Raw and Classified are on by default**, a **Bug Report** attaches the last 750 lines of each unless you turn them off — so a combat-recognition problem lands with the exact wire and the engine's read of every combat line and death.

**A bug report leaves your login out.** The report's **Scrollback** and raw-wire sections copy only what was written while you were inside the game: from your first game prompt, until you disconnect or exit to the board's menus, and again from the next game prompt. The menu itself isn't copied. So the board's login screen, with your account name on it, isn't in a report even when it's still on your terminal or in the Backscroll window, which go on showing everything. A report made before you've entered the game says the section was left out. The program log section was already safe: what you type isn't logged, and the automatic login logs only the prompts it waits for, not what it sends.

**At the board's menu the client stands down.** If you `exit` the game to the board's menu without disconnecting, MudPlay stops sending anything on its own until you're back in: no party poll, buffs, sneaking, statline repair or loop steps, since at the menu each would be read as a menu selection. What you type still goes through, and `e` to enter the realm isn't mistaken for a step east. Everything resumes at your first game prompt.

---

# Settings Menu

MudPlay is a Telnet terminal client for MajorMUD / MegaMUD-style BBS door games. On top of a faithful terminal, it layers a large automation suite — auto-combat, auto-healing, auto-spellcasting, navigation/looping, party coordination, cash and item collection, and more — and almost every piece of that automation is tunable. This guide documents every one of those tunable settings: what it does, what happens when you change it, and where to find it.

**What these settings control.** Broadly: how your character fights, heals, casts spells, and buffs; how the client walks you around the map and loops between monster spawns; how it handles party coordination, chat, and remote `@`-commands from other players; how it manages coin and item pickup; how the terminal looks and behaves; and various connection/reconnection behaviors for the BBS itself.

**Where to find them.** Almost everything lives in one place: the **Settings window** (opened from the toolbar, the View menu, or its keybind — default varies by build). It's organized into tabs down the left side: General, Toolbar + Shortcuts, BBS + Display, Health, Spells, Combat, Party, Cash, Statline, Talk, Auto-Light, Auto-Lair, Auto-Trainer, Other, Events, and Sounds. A search box at the top of the window filters the tab list.

Two related editors live outside this window: the **keybind rebind dialog** (opened from a row on the Toolbar + Shortcuts tab) and the **macro editor** (a separate Game Data dialog).

**Where settings are stored.** MudPlay never stores a setting in one flat file. It uses a four-tier hierarchy — **Defaults → Global → BBS → Character** — and each tab's fields belong to one specific tier.

Every tab makes this visible: its controls sit under **banner-headed sections** naming the tier they save to (Global client settings / BBS settings / Character profile settings), so you can see at a glance where a change lands. A tab whose settings are all one tier shows a single banner; the mixed tabs (BBS + Display, General, Toolbar + Shortcuts, Other) split into a section per tier. The tiers:

- **Character-tier** (the vast majority of settings — Combat, Spells, Health, Party, Cash, Talk, Auto-Light, Auto-Lair, Auto-Trainer, most of General, keybinds, macros) live inside that character's own profile file and only apply to that one character.
- **BBS-tier** (connection info, reconnect behavior, terminal size, and the board's realms with their own settings) live in that BBS's own file and are shared by every character who plays there; each **realm** also keeps its own collected data, shared by the characters playing it. Characters that are online together, a client each, see each other's changes to that shared data within a second or two, and whatever a character saw last is what stands: the **boss list** and **boss timers**, **stash balances**, the **player database**, the **room blacklist**, **Roomba** room labels and item sightings, **leaderboard** captures and **unrecognized lines**. The same goes for a Game Data Browser edit saved **for all characters** or **only for this realm**, and for the quest guides. A BBS's settings are saved a change at a time, so two clients changing different things both keep theirs. (GOTO favourites and triggers are each character's own and aren't shared.)
- **Global-tier** (a handful of install-wide toggles — navigation-line colors, the Pyramid/Asylum puzzle solvers, confirmation prompts, the Help-menu website list, player-database cleanup) apply to every character on every BBS on this install. Every open client shares them: change one in a client and the others take it up within a second or two, and two clients changing different settings both keep theirs.

All of this is stored under a single MudPlay data folder (`~/.local/share/MudPlay/` on Linux, `%AppData%\MudPlay\` on Windows, `~/Library/Application Support/MudPlay/` on macOS) as JSON files that only record *deltas* from the tier below them — so an unmodified setting isn't written to disk at all.

**Does MudPlay save automatically?** No — the Settings window uses an explicit **OK / Apply / Cancel** model. Edits are staged in memory; **OK** applies every changed tab and closes the window, **Apply** applies without closing, and **Cancel** (or the window's X button) discards everything you changed since opening it. **Closing MudPlay or running an update with Settings still open saves your changes**, as OK would, without asking: you never pressed Cancel. The one exception is a change Settings would have warned you about first (a custom statline missing the HP, mana or resting field): nobody can answer "Save anyway?" on the way out, so that tab is left as it was saved and the program log names it. A few things outside the main Settings tabs are the exception and save the instant you change them: keybind rebinds, macro edits, and everything on the Events tab: its list, the "Disable all events" toggle, and the three event limits ("Events waiting at most", "Drop a waiting event after" and "Give up a paused event after").

**Before you start changing things — a few things worth knowing:**
- Nearly every setting documented here takes effect **live**, with no restart or reconnect required — this guide calls out the exceptions explicitly (e.g. terminal scrollback size, a handful of BBS-connection fields that only apply on the *next* connect).
- A handful of controls exist in the UI but currently **do nothing** — fields that were built but never wired into the automation engines (Combat's *Polite mode*). This guide flags every one of them explicitly rather than describing invented behavior.
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
| `POST /command` | Runs any `@`-command but `@dupe` (which is taken by telepath or gangpath only) — `{"command":"@goto","args":["Newhaven"]}`. Replies the command would have telepathed back come to you in the response instead of going out on chat. What each command does and answers is under *Remote @-commands*. |
| `POST /send` | Types one line at the game exactly as if you'd typed it in the terminal. |
| `GET /commands` | Lists every dispatchable command with its permission category and whether it counts as destructive. |

These reuse the same handlers as the `@`-commands a party member can send you, so behaviour is identical — no second implementation to drift. The **per-player permission check is skipped**, because that gate answers "may this *other player*, over chat, do this to me?" and the answer is meaningless for whoever is holding the keyboard. Your own master switch (Settings → Talk → disallow remote commands) and the unconditional hard-blocks (reroll) still apply. So does the **master switch (Auto-All)**: while it is off, `POST /command` runs nothing but `@auto-all` and the reply says why. `POST /send` is the same as typing, so it always works. Every local invocation is logged at Info, so the program log shows what drove the client even with Debug diagnostics off.

**Allow destructive commands** (the second checkbox, off by default) governs anything that ends the session or can't be undone — `@suicide`, `@hangup`, `@relog` — and the raw `POST /send`, which is destructive by nature since a raw line can carry any command the game accepts. While it's off those return *403* and nothing is sent.

Which commands count is derived from each command's permission category rather than a hand-kept list, so a command added later is classified automatically instead of defaulting to allowed; an unrecognised command name is refused too. `POST /send` also rejects multi-line input — one command per request, so nothing can smuggle a burst past the game's own rate limiting.

**Notes.** Requests are logged at Debug, so individual requests only appear in the program log while Debug diagnostics are on (the *actions* are logged at Info regardless). `/state/full` and `/scrollback` answer *503* until a terminal session exists.

The status line under the checkbox says whether the socket actually came up — if the port is already taken, that's where it tells you. A bug report records whether the API was listening **and whether destructive commands were allowed**, since that changes how the rest of the report should be read.
**Comparing loops.** `/loops` exists so "where should I be hunting?" is answerable without opening each one. Note that exp is reported with the **monster's exp multiplier already applied**, which is the number that actually matters and can differ from the raw table value by orders of magnitude — a loop showing a million-plus median is boss content, not a grind circuit. Lair monsters (campable, respawn on a timer) are reported separately from placed fixtures and assigned roamers (which wander in on their own schedule), because a room full of roamers is not a loop you can pace.

**Notes.** Requests are logged at Debug, so they only appear in the program log while Debug diagnostics are on. `/state/full` and `/scrollback` answer *503* until a terminal session exists.

Endpoints that read live state do so on the UI thread and give up after five seconds, answering *504 ui thread unresponsive* — which is itself worth knowing: if a client looks frozen and the API says 504, the freeze is the UI thread, not the connection. The status line under the checkbox says whether the socket actually came up — if the port is already taken, that's where it tells you.


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

### Navigation map: other floors

**Default:** **10** floors up and down; leave out a floor that overlaps more than **50%**; route lines drawn on other floors
**Available options:** 1–99 floors; 0–100%; route lines on other floors on / off.
**What it does:** Sets how far the Navigation map's shadowed floors reach: the floors reached by up and down exits, drawn dimmed around the floor shown (see **The map and obstacles**). Which floors are drawn (all, up only, down only, or none) is the map's **Overlays → Other floors** chip. **Floors drawn up and down** is how many up/down steps away a floor may be; a mountain path climbs one step per floor (the Barren Hills climb is over 40), so raise it to see a long climb end to end. **Leave out a floor that overlaps more than** drops a floor when that share of its rooms would land on rooms already drawn, since a floor stacked right on this one is a different place; a floor with three or fewer rooms covered is always kept, and **100%** keeps every floor. **Draw route lines on other floors** lets a walk's route line run on through the shadowed floors' rooms; the game doesn't always lay two floors out so they line up, so a route crossing between them can draw as a long diagonal. Turn it off to keep route lines to the floor shown, broken off where the route leaves it.
**When you might change it:** Raise the floor count for long climbs and deep dungeons; lower it, or the overlap share, if the map feels busy.
**Important notes:** Install-wide (Global tier). Applies on Apply; an open map redraws at once.

### Buff Watchdog layout

**Default:** Config above bars
**Available options:** Config above / below / left of / right of the timer bars.
**What it does:** Chooses where the Buff Watchdog's config panel sits relative to its timer-bar side. Changing it reflows an open Buff Watchdog immediately.
**Important notes:** Per-character. The splitter position, collapse state, and window size are remembered separately (see the **Buff Watchdog** section).

### Navigation line appearance (color + thickness)

**Default:** Go-to `#1E64DC` (blue), Loop `#7AB870` (green), Preview `#E0A000` (amber), Loop-builder preview `#E66C5A` (orange-red), Auto-Lair `#DC821E` (orange), Following `#5FB3D9` (cyan) — all 3.0 px thick.
**Available options:** Any RGB color via the color-picker; thickness 1.0–8.0 px in 0.5 steps.
**What it does:** Sets the color and line thickness for each of the six distinct route lines the Navigation map draws — the active walk-to path, an active loop's route, a queued go-to preview, the in-progress loop-builder preview line, an Auto-Lair run's route, and a party leader's route you're following (from their `@path` reply, or their reply to your `@goto`).
**When you might change it:** Make the lines thicker or higher-contrast if you find the default map lines hard to see; give each route type a color you can tell apart at a glance.
**Important notes:** This is a **Global-tier** setting — changing it changes the map for every character on the install, not just the current one. "Restore Defaults" resets every line at once, and each row has its own **Reset** button. Applies live — the Navigation map repaints immediately with no restart.

### Visual impairment: terminal colours

**Default:** Default (the standard colours).
**Available options:** **Default**, **Deuteranopia**, **Protanopia**, **Tritanopia**, **Custom**.
**What it does:** Chooses how the 16 colours the game uses are drawn in the terminal and in the Backscroll window. The list under the choices shows all 16 as the choice draws them, each with a sample of text on the terminal's black.

- **Default** draws them as the terminal always has.
- **Deuteranopia** and **Protanopia** are for the two kinds of red-green colour blindness (green-weak and red-weak). Under the standard colours green and yellow are close to the same for both, and for protanopia red is very dim.
- **Tritanopia** is for blue-yellow colour blindness, where the standard green and cyan are hard to tell apart.
- **Custom** lets you set each colour yourself: click a colour's swatch to pick another. **Reset** puts that one back to the standard colour and **Restore standard colours** puts them all back. Custom starts from whichever choice you were looking at, so you can pick one of the three above and then adjust it.

Each of the three made choices keeps every colour near its usual hue, so red is still a red to anyone looking over your shoulder, and moves its shade and brightness until the 16 are as far apart as they will go for that kind of colour blindness. Every colour is also kept bright enough to read on black, which the standard dark blue is not.

**When you might change it:** You can't tell two of the game's colours apart, or one of them is too dim for you to read.
**Important notes:** This is a **Global-tier** setting: it changes the colours for every character on the install. It takes effect when you press **Apply** or **OK**, and the terminal repaints at once. Your own colours are kept while another choice is picked, so going back to Custom finds them as you left them. Only how a colour **looks** changes. Everything the client reads from the game's colours (room names, a modified stat on `stat`, emotes) goes by which colour the game sent, not by how it is drawn, so nothing else behaves differently. The colours above the base 16 and the terminal's black background are not changed.

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

**Default:** On — Auto-Combat, Auto-Nuke, Auto-Heal, Auto-Rest, Auto-Bless, Auto-Get Items, Auto-Get Cash, Auto-Sneak. Off — Auto-Light, Auto-Hide, Auto-Search, Auto-Train.
**What it does:** Each checkbox is the base on/off state for one automation engine: Auto-Combat (fighting), Auto-Nuke (offensive AoE/debuff spells), Auto-Heal (heal and cure casts, and aiding a downed party member), Auto-Rest (stopping to rest or meditate), Auto-Bless (buffing), Auto-Light (keeping a light lit), Auto-Get Items (picking up ground loot), Auto-Get Cash (picking up coin), Auto-Sneak and Auto-Hide (the two stealth engines), Auto-Search (searching for hidden things), and Auto-Train (the Auto-Trainer tab's leveling automation).
**When you might change it:** Set the automation posture a character should return to — e.g. a scout that should never auto-fight, or a healer that should always rest.
**Important notes:** These are your character's **base** engine states, not the live toolbar toggles. They're applied when the character loads, and the live toggles snap back to them at the start of a loop or Auto-Lair run, when a walk-to arrives, and when you stop movement yourself (Stop, or a party member's `@stop`) — so you can flip an engine off to travel somewhere and have it return to your baseline when you get there, stop, or the circuit begins. See **Automation → Base modes** for the full picture.

### Allow hangup in all-off mode

**Default:** Off
**What it does:** "All-off mode" is the **master switch (Auto-All) being off**. Unticking the auto toggles one by one is not it. With the master switch off MudPlay does nothing on its own, hang-ups included. Turning this on carves out the exception: with the master switch off, **every automatic hang-up still works** — the low-HP hang-up (Health tab's "Hang up if below"), a received `@panic`, the PvP hang-up actions, and the hang-up for a monster whose relationship is **Hangup**. With it off (the default) and the master switch off, none of them fires, and the wimpy jump that can stand in for a hang-up is not made either.
**Important notes:** With the master switch off, this ticked and Disable hangups off, the hang-ups fire whatever the toggles read: "if master switch is off, but allow hangups in all off mode is on, and disable hangups is off, it should still trigger hangups". So the low-HP hang-up does not need Auto-Rest to have been on then. This setting does nothing while the master switch is **on**. Then the low-HP hang-up goes by **Auto-Rest** (see *Hang up if below*) and the others need no toggle. The toolbar's **Disable hangups** always wins: with it on, nothing hangs up automatically whatever this is set to. The remote `@hangup` and `@relog` are remote commands, so with the master switch off they are not followed at all, this option or not.

### Re-enable on reconnect (11 checkboxes)

**Default:** Off (all)
**What it does:** One checkbox per automation engine (the same 11 engines as above). When you reconnect after having been disconnected mid-session (not the very first connect of an app session), each checked engine gets automatically turned back on — useful if you manually paused something, got dropped, and want your automation state to reset to "on" on redial rather than staying off.
**When you might change it:** Check the engines you always want running even through a flaky connection (e.g. Auto-Heal and Auto-Rest, which share one re-enable box); leave off the ones you deliberately paused for a reason (e.g. Auto-Nuke while grinding a safe area).
**Important notes:** It never overrides the **master switch (Auto-All)**. If you switched the autos off with it and then reconnect, nothing is turned back on (Auto-Train included) and the switch stays off: the reconnect respects what you set.

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
**What it does:** This is a toolbar button, not a checkbox on a settings tab — but it's documented here because that's where you'll actually find it (look for the "no hangup" icon). When on, **no** automatic mechanism can drop your connection — not the emergency low-HP hangup, not a PvP response, not a partymate's `@panic`, not a monster whose relationship is **Hangup**. What still ends the session is you disconnecting, or someone you trust asking for it.
**Important notes:** This is a hard override — it wins over the General tab's "Allow hangup in all-off mode" carve-out. A monster whose relationship is **Hangup** is then treated like a Neutral one: left alone on sight, and fought back if it attacks you. Three things still go through with this on: a remote `@hangup` or `@relog` from a player you gave the **Hangup/disconnect** grant (they are requests, not the client's own decision); a log-off command you send yourself (`;o`, `=x`); and a graceful log-off ahead of the BBS's nightly server cleanup, if you've opted into "reconnect after cleanup" on the BBS tab.

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

Settings → "BBS + Display" — despite the plain "BBS" name in some places, this tab also carries terminal-size/scrollback settings, the per-character credentials + logon steps, and the four global confirmation-prompt checkboxes. **Adding, removing, and renaming BBSes now lives in Profile Management** (View → Profile Management, or the button on this tab's left rail); this tab's list is for **selecting** a saved BBS to edit its details, and selecting one here only edits it — it never moves your loaded character (that's Profile Management's *Move to BBS*).

To make each setting's persistence level obvious, the tab is split into three banner-headed sections:

- **BBS settings** — stored with the board, shared by every character on it: connection, retry/reconnect, display size + scrollback, board disconnect line, and the board's **realms** (each with its own game data, game-menu commands, realm mechanics and runic-currency name — see *Realms* below).
- **Character profile settings** — only for the loaded character: the realm it plays, username/password, the read-only captured suicide password, SYSOP powers, the Sys Goto table, and the automated logon-menu steps.
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

"After Cleanup" is a two-part behavior: it also makes MudPlay proactively exit the realm and drop the connection *before* the BBS forcibly disconnects it, once a "shutting down soon" warning is seen. It waits for a safe room (no hostiles, not mid-fight), sends the exit command, and drops the carrier the moment the game confirms your character has been saved — so it disconnects cleanly regardless of which menu your board drops you to after leaving the realm. If something attacks you during the exit wait the game calls the exit off (`Your meditation has been interrupted - you may not exit now!`); MudPlay then waits until the room has stayed safe for a full combat round and sends the exit again. On Stock it also takes the game's own `[MAJORMUD]:` prompt as proof you are out, since a board can reword the saved message. If the exit is called off three times it stops waiting (whatever stops it isn't something the safe-room check can see): it re-sends the exit once and drops the connection on the timeout.
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

### Realms

**Default:** one realm, named after the BBS.
**What it does:** A BBS can host several versions of the game, picked from its menu (a PVE and a PVP realm, say), and each usually differs — often with its own MDB export. Each realm has its own **game data** (the imported MDB it uses), **game-menu commands**, **realm mechanics** and **runic-currency name**, and keeps its own copy of everything MudPlay collects while you play it: the **players you've seen** (the `who` list), the **room blacklist**, the **leaderboard** history, **Roomba** room labels and item sightings, your **quest** edits, **boss timers**, and game-data edits saved **Only for this realm**. Characters assigned to the same realm share all of it, the way characters on a BBS used to.
**How to use it:** pick a realm in the list to edit its settings below; **Add realm** makes a new one (default settings, no collected data), the **Realm name** box renames the selected one (its data and characters come along), and **Remove realm** deletes it when you press **OK** — **along with every character playing it and its collected data**; you confirm first, with the characters named, and the realm your loaded character plays can't be removed here. The selected realm's settings sit in a frame in **that realm's own colour**, labelled with its name, the way combat-profile groups are framed in their profile's colour — switch realms and the frame's colour switches with it. The frame also lists **which characters play the realm**. Put a character on a realm with **Plays on realm** in the character section of this tab (for the loaded character), or **Move to realm** in Profile Management (for any character); Profile Management can also add, rename and remove realms.
**Important notes:** A BBS saved before realms existed became one realm named after it, holding its settings and everything collected on it, so nothing was lost. If two of your characters on one BBS actually play different realms, add the second realm and assign that character to it — it starts fresh.

### Plays on realm

**Default:** the BBS's first realm.
**What it does:** Which realm of this BBS the loaded character plays (shown when the selected BBS is the character's own). It decides the game data and realm settings in use and where what you collect is kept. Saved when you press **OK**; the game data and the realm's stores switch straight away.

### Game entry command / Game exit command

**Default:** `E` / `=x`
**What it does:** The literal keys sent at the main menu to enter the game, and to log off cleanly. Set per realm.
**When you might change it:** Only if a particular board remaps its main-menu options away from the MajorMUD-standard letters.

### Player dies at (HP)

**Default:** `-25`
**What it does:** The negative HP value at which this realm actually kills a character (0 HP alone just "drops" you — bleeding out but revivable). Used by the emergency-hangup safety logic to know how far into negative HP it's safe to let things go.
**Important notes:** With "Auto-refine the floor from slow deaths" (below) on, MudPlay learns the real number over time from observed deaths and updates this automatically.

### PvP is enabled on this realm

**Default:** Off
**What it does:** Tells MudPlay that players can attack one another on this realm. Nothing the game prints says so, so it is yours to set, once per realm (a board with a PVE and a PVP realm has it off on one and on on the other).
**Important notes:** It is the switch the player-versus-player features look at: the care taken not to catch other players in a room attack, and the PvP settings, only apply on a realm where it is ticked. It turns on the room-attack care (see *Room attacks and other players (PvP realms)* under Combat) and everything on Settings → PvP.

### Hang-up penalties on this realm

**Default:** Off (no penalty recorded).
**What it does:** Records what this board does to a character who hangs up in a fight. Some boards take a share of your maximum HP for it, and a few also drop some of your items on the floor of the room. The board decides this and nothing the game prints says what its rule is, so it is yours to set, once per realm (a board's PVE and PVP realms each have their own).
**How the options work:**
- **Does the BBS have hang-up penalties?** — the master switch. Ticked, a hang-up in PvP combat is penalised; everything below is greyed out until it is ticked.
- **A hang-up in PvP combat: HP lost (%)** — two figures, *from* and *to*. The board takes a share of your maximum HP somewhere between them (default `25` to `50`). Set both the same for a fixed share. The second is never less than the first: move one past the other and the other follows.
- **A hang-up in PvP combat: Items dropped (up to)** — the most items the board drops (default `0`, 0–100). Leave it at 0 when the board takes HP only, as most do. Above 0, MudPlay also looks for dropped items when you come back into the game (*Picking up what a hang-up dropped*, below); the same goes for the PvE figure when its box is ticked.
- **Also penalised in combat with monsters (PvE)** — tick it when the board penalises a hang-up in a fight with a monster too. It has its own **HP lost (%)** pair (default `25` to `50`) and **Items dropped (up to)** (default `0`), greyed out until it is ticked.
- **Also penalised outside a fight (every hang-up)** — tick it when the board penalises every hang-up made in the game, in a fight or not, at the PvE figures (default off; greyed out until the PvE box is ticked). Unticked, a hang-up with no fight on is taken as free. It decides one thing only: whether MudPlay checks for a death by the penalty after such a hang-up (*A hang-up that killed you*, below).

**Important notes:** These settings **record the board's rule**. They do **not** change when MudPlay hangs up, and no hang-up is held back, delayed or swapped for something else because of them. Whether and when MudPlay hangs up stays with the toolbar's **Disable hangups** toggle, the **Health** tab's hang-up trigger, the **PvP** tab's actions, and a partymate's `@panic` (unless you ignore it). What they set in motion comes afterwards, on the way back into the game: see *Picking up what a hang-up dropped* and *A hang-up that killed you* below.

Where the record shows up:
- **The program log.** When MudPlay sends a hang-up (the Health tab's, the PvP response's, one for a monster whose relationship is **Hangup**, or one from `@panic`, `@hangup` or `@relog`) on a realm with a penalty recorded, one `[Hangup]` line says what the board takes: for example *This realm penalises a hang-up in PvP: 25–50% of max HP and up to 3 items.* It names the PvP side when a fight with a player is under way or the hang-up is the PvP response's, and the monster side when you are in combat. When MudPlay can't tell either way it gives the whole rule without picking a side. A hang-up from a fight with a monster on a realm that only penalises PvP logs nothing. A hang-up you make yourself with the Connect / Disconnect button is not logged this way.
- **Bug reports.** The Session section's **Realm hang-up penalty** line carries the recorded penalties, or `none`.

**Picking up what a hang-up dropped.** On a realm where an **Items dropped (up to)** figure that applies is above 0 (the PvP one, or the PvE one with its box ticked), MudPlay checks for dropped items each time you come back into the game. It runs only on such a realm, and it never changes when or whether MudPlay hangs up.

- **What it compares.** While you play, MudPlay keeps a list in your profile of what you hold: worn, carried, your lit light and your key ring, by name and count. The pickup doesn't look for coins; the list notes them, with your HP and lives, only for *A hang-up that killed you* below. The list is saved whenever you leave the game, so it survives a dropped link, closing MudPlay and an update restart. It is kept on every disconnect, a lost carrier as much as a hang-up MudPlay sent, since the board decides which of them it penalises. It is kept on every realm, too: it is small, and it is only acted on where the realm's settings say items drop. It belongs to the character and the realm it was taken on: a list from another realm is ignored, and the default profile (no character loaded) keeps none. **When you die the list is thrown away** until your inventory is next read, because what you were carrying is in your deathpile, not dropped by a hang-up. If you play the character from another client in between, whatever you sold, used or stored there reads as missing the next time MudPlay checks.
- **When it runs.** Once per connection, after the login's own inventory read (`i`). If you entered the game by hand and no inventory has been read after a few seconds, MudPlay sends one `i` itself.
- **What it does.** Anything you held before and hold fewer of now is missing. If nothing is missing, that is the end of it. Otherwise MudPlay reads the room's floor list (*You notice … here.*) from the login's room display, asking for one redisplay if none was read, and sends a `get` for each missing item that is lying there: as many copies as are both missing and on the floor, and nothing else. On Paradigm that is one counted `get 3 torch`; on Stock one `get` per copy.
- **Never more than the board drops.** In all it picks up no more copies than the realm's **Items dropped (up to)** figure (the larger of the PvP and PvE figures that apply, since MudPlay can't know afterwards which one did), for each drop of the link the list covers. If more than that is missing, the rest is left and the log says so. This is what keeps an out-of-date list (torches burnt since the last `i`, say) from turning into a sweep of the floor.
- **Worn gear.** There is no separate switch for putting it back on, and the Death tab's **Auto-Equip After Recovery** has no say here: a hang-up is not death recovery. Once the pickup is done MudPlay asks the Equipment Manager to apply again **the gear set that was last equipped** (Default, a rest set, While Moving, Bossing or Backstab), and the Equipment Manager works out what to wear, by its own rules and under Auto-All. It only does so when at least one picked-up item is part of that set; picking up a torch sends no equip. It is the whole set that is applied, so any other piece of it sitting in your pack goes on too, and it is applied even if the set's Enabled switch is off, since it was the set on you when the line dropped. The last-equipped set is remembered while MudPlay runs, so it is known after a reconnect; after MudPlay itself restarts it isn't, and the set automation would choose for you right now is applied instead (an enabled set only). That is Default when nothing else is called for, but after a penalised hang-up you are often low enough that a rest is due, and then it is your rest set. A piece that belongs to no gear set stays in your pack, and a piece of some other set goes on when that set next does.
- **Movement waits for it.** From the connect until the check is over, loops, walks and Auto-Lair are held, so a loop that restarts on reconnect doesn't walk out of the room first. Normally that is the second or so the login's `stat` and `i` take. The hold doesn't outlast its reason: it is given up if the game isn't entered within 3 minutes of connecting, or if no inventory has been read about 8 seconds after entering. The list is kept all the same, and compared when your inventory is next read, without the hold; items are then picked up only if the map is sure you are standing in the room you left the game in.
- **When it holds back.** If the map is sure of both rooms and you came in somewhere other than where you left, nothing is picked up. When the map can't say (the room wasn't known when you left, or isn't now), the floor of the room you are in is checked, unless a move has gone out since you entered. If the room is too dark to show, or you are blinded, MudPlay says it couldn't see the floor rather than that the items aren't there. If a fight is on in the room, the pickup waits until it is over, and is dropped if you leave the room first. If a hostile monster is there that MudPlay isn't set to fight, the items are left on the floor. With Auto-All off it sends nothing at all.
- **What the board destroyed can't be found.** On Stock the penalty puts items on the floor of that one room only: an item that doesn't fit there is gone, and so is one flagged *Destroy On Death*. The check reports those as missing and not on the floor.
- **One pass.** There is no retry. What isn't on the floor at that moment is logged as still missing and not looked for again. The exception is a link that drops again before the pass has finished: what it hadn't found is carried to the next connection, and since each drop may have cost the realm's item count, the limit for that check is the count for each drop the list covers (twice the figure after one interrupted check, three times after two).
- **It can't tell your item from one like it.** The floor list gives names only, so an item of the same name that someone else left in the room is taken as yours, up to the number you are missing and the board's limit.
- **It is your own gear coming back, so the Game Data loot flags don't apply.** An item marked *Cannot be taken* is picked up all the same, and so is one over its *Max to get*; an item flagged for Auto-Discard is picked up and then discarded again by that engine.
- **Where to see it.** Two terminal notices: one naming what was picked up, and one naming what is still missing and why it wasn't picked up, whichever way the check ended (not on the floor, the floor couldn't be seen, another room, a hostile in the room, Auto-All off). The program log's `[HangupItems]` lines say what was missing, what was on the floor, what was picked up and what stays missing, and its `[Equipment]` line says which gear set was applied again. A bug report's Session section carries **Hang-up item check**, **Missing after hang-up** and **Held list on file**. A walk or loop it is holding says *checking what a hang-up cost* on the Navigation window's status line (the same hold serves the death check below, on realms that drop nothing too). **Reset States** frees the hold: before the comparison the list is kept and still compared at the next inventory read, and after it the check stops.

**A hang-up that killed you.** A board that penalises a hang-up kills a character that hangs up while dropped (at or below 0 HP), and one that is standing when the HP it takes puts the character under the death threshold. It does this after the link is gone, so you never see a death line: you come back standing in the temple at full HP with a life less and your gear in a pile where you fell. MudPlay works this out on the way back in and records the death, so Death Recovery can go for the pile.

- **When it looks.** Only when the list it keeps (the one described above, which also holds your HP and max HP, lives, coins, what you wore and whether you were in a fight) says the hang-up **could have killed**, by this realm's settings. That takes two things. First, the settings penalise that hang-up: the master switch is ticked and you were in a fight with a player (one under way, or a player attacked you in the last 30 seconds); or **Also penalised in combat with monsters (PvE)** is ticked and you were in a fight with a monster (in combat, or a hostile one in the room); or **Also penalised outside a fight (every hang-up)** is ticked too. Second, you were **dropped**, or your HP was no more than the top **HP lost (%)** figure of that side, taken of your max HP (at 50% and 500 max HP: 250 HP or less); with that figure at 0, only a dropped character counts. It needs no **Items dropped** figure, so it runs on a realm that takes HP only. **Only a dropped link counts as a hang-up.** Leaving by the game's own exit command (`x`, the meditation, *Your character has been saved.*) is not one: the board takes nothing for it, and MudPlay looks into nothing afterwards, whatever your HP was.
- **What counts as a death: a life lost, and nothing else.** Exactly one life fewer than you left the game with is a death. The same number, or more, is not. Your HP, the room you are in and what is in your pack never count as one: all three change just by playing on. Both counts have to come from the game. The one "before" is on the list only if a `stat` was read on the connection you left on (the login sends one); the one "now" comes from a `stat` read on this connection. When something changes your lives with no screen saying so (a life asked back by **Sysop god lives**, a level trained), MudPlay sends a `stat` a few seconds later to read them again, under Auto-All; until one is read the count is treated as not known.
- **When the lives haven't been read.** The login's own `stat` normally gives them. If none has been read when your inventory comes in, MudPlay sends one `stat` itself, but only with Auto-All on and while it is still holding movement for the check. Once it has let go of the hold (Auto-All off, the 3-minute wait at the board's menu, Reset States) it sends nothing more: the question stays open, nothing is held, one terminal notice says so, and **typing `stat` and `i` answers it** at any time on that connection. It is judged on what you came in with (your HP at the first prompt, your pack at the first inventory read), not on what you have become since. If the link drops before a `stat` is read, nothing is recorded.
- **A life lost somewhere else is not this hang-up's.** If you played the character from another client in between and died there, the life is gone but not to this hang-up, and MudPlay can't judge it. It records nothing, and says so on the terminal, when a life is gone but what you came in with says it wasn't lost here. On both realms: your HP at the first prompt was 0 or less, under what you left with, or exactly what you left with short of full (a death sets HP to full); anything you held when you left that a death takes is still held at the first inventory read, coins included (a death takes every item but those that stay with you, such as loyal ones, so holding even one of the rest means you got your pile back somewhere; a torch burnt meanwhile doesn't change that); or any piece is still worn at that read (a death takes everything off, on Paradigm as on Stock; whether a cursed piece that stays with you comes off on Paradigm isn't known, and it is treated as it is on Stock). On Stock also: the board didn't print *Last time you were on, you disconnected while playing.* since the hang-up (it prints that on the first entry after a hang-up it didn't let go free, and on no other). Paradigm isn't known to print it, so it isn't asked for there. If the `stat` screen names a different character from the one the list was written for (a profile copied or shared between characters), the list is left alone without a word.
- **What it records.** The same record a death you saw makes, in **Death Recovery**: the room you were in when the link dropped, that time, your lives now, and as the pile what you held then and didn't hold at the first inventory read (worn pieces with their slots, so Auto-Equip on recovery can put them back on; everything else by name and count, your keys and lit light included; and your coins). What stayed with you isn't on it. In place of a death line it reads *Killed by the hang-up penalty (not seen: worked out on entering the game). The room and time are where this client last had the character in the game.* There is no **How did I Die?** replay for it, since nothing was on screen, and no death sound, since it happened minutes ago. If the link dropped before any inventory was read on that connection, the record's room and time are still that hang-up's. The usual case is the quick one: a hang-up left you dropped, you came back dropped with the monster still there, and MudPlay hung up again at the first prompt. That second hang-up is the one that kills, and the death is recorded where and when it happened, with your lives and what you held taken from the list of the first hang-up (which is known not to have killed, since you came back dropped). When what you held can't be known that way (you had moved on since entering, or the realm's penalty drops items), the record lists no pile and says so. And if an earlier hang-up was still waiting to be looked into when that happened, it is settled then: recorded if a `stat` had already shown the life gone (on Stock only; off Stock, with no inventory read to separate it from a life lost on another client, it is told, not recorded), and otherwise said on the terminal to be past telling. The first hang-up's lives are not carried when you came back with more HP than you left with, since only a death does that.
- **What happens then.** One terminal notice says you died to the hang-up penalty, where MudPlay last had you, and that **Recover Now** goes for the pile. When this is worked out at the login, while movement is still held: loops, walks and Auto-Lair are stopped, as after any death, the loop set aside when the link dropped is not restarted, and a default task still waiting to start (it waits for your party after a reconnect) is called off for that connection, so nothing walks a stripped character out of the temple; your own buff timers are cleared (a death wipes them; they were only paused when the link dropped). **When it is worked out later** (MudPlay had let go of the hold, or the `stat` that answered came long after), you have been playing since: the death is recorded and you are told, and your loop and your buff timers are left alone. Either way, with **Sysop god lives** on the life is asked back, unless Auto-All is off. The item pickup above does **not** run: what is missing is your deathpile, and it is neither picked up from this room nor reported as dropped. With **Auto-Recover Deathpiles** on, the pile is grabbed when you next walk into that room, as for any death.
- **When it can't tell.** MudPlay records **nothing**, and says so once on the terminal with what it saw, when: no `stat` was read on the connection you left on, so there is no count to compare with; two or more lives are gone, more than one hang-up costs; or a life is gone but not to this hang-up (above). A death is never guessed. If you are still in the room you left, the item pickup then runs as usual; anywhere else it stands down, since what is missing may be a deathpile. The same holds while the question is still open. **One thing it can't tell apart at all:** if you played the character from another client in between and that session itself ended in a death by a hang-up, you come back stripped with a life less exactly as if this client's hang-up had done it. The record is then made where MudPlay last had you, which is why it says so in its own words and the notice adds that the pile is where you died if you played elsewhere since.
- **What it can't know.** Whether you were in a fight is MudPlay's own reading when the link dropped; if the board saw you attacked and MudPlay didn't, a realm without the **every hang-up** box ticked is not checked. A room the board treats as free costs nothing: in a protected room (when you aren't being attacked) you come back with the HP you left with; in an arena a dropped character comes back standing where the dead wake, with its HP put back and its lives unchanged. Neither is a death, and nothing is recorded. The board's death threshold is its own setting and MudPlay relies on no figure for it, which is why a low HP only makes it look, and your lives decide. On Stock a room can name another room that a character who hangs up there is put in, and the death then happens in that other room; the record still names the room you were in, since nothing tells MudPlay of the move.
- **Where to see it.** The terminal notice; the program log's `[HangupItems]` lines (HP and lives then, HP at the first prompt, lives now, what was worn, the board's lines, what is gone, and the verdict); and a bug report's Session section, where **Held list on file** shows the list's HP, lives and fight, **Hang-up death check** the last verdict and what it went by, and **Hang-up item check** says while a death is still to be judged.

### Boss cleanup time / Boss cleanup zone

**Default:** `21:00`, your computer's local time zone.
**What it does:** The realm's daily maintenance time. Some boss monsters only respawn at this specific wall-clock time rather than on a countdown timer — MudPlay's boss tracker uses this to know when a "cleanup-only" boss should flip back to alive.

### Board disconnect line

**Default:** blank (built-in lines only)
**What it does:** An optional extra logoff line for MudPlay to watch, on top of the built-in "just disconnected" / "just hung up" forms. Some boards emit a custom logoff line keyed on a player's **account** name rather than their character name, which the standard detection misses — so a party member's drop slips past and the party runs off without them. Teaching MudPlay that line means the drop is caught and the party waits for them.
**How the options work:** Uses the same literal syntax as triggers — `{name}` captures the disconnecting player (matched against a member's account-name override in Game Data → Players, else their character name), and `*` matches a varying run (e.g. a trailing "Lines in Use: N" count). Example: `►►► [{name}] logs OFF*`.
**Important notes:** BBS-tier — the line is shared by every character who plays this board. Leave it blank on boards that use the standard disconnect wording.

### Name of runic currency

**Default:** `runic`
**What it does:** Some realms rename MajorMUD's top currency denomination to their own word. This field tells MudPlay what that word is on the selected realm, so cash automation keeps parsing coin messages correctly.

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

## Status Bar

Found at the bottom of the "BBS + Display" tab, under the confirmations. **Global-tier** — one layout for every BBS and every character on this install.

**What it does:** Lets you decide what the bar under the terminal shows. The bar is one to four **rows**; each row has a **Left**, **Centre** and **Right** side, and each side holds the items you put on it, in order. The default is the bar MudPlay has always had: one row with the engine chip, location, exp rate and time to level on the left, the looked-at target in the centre, and the statline warning, tick countdowns and connection light on the right.

### Building a row

Each row is drawn as three boxes — **Left**, **Centre**, **Right** — laid out the way they sit on the bar.

- **+ Add** (under each side) — opens a menu of everything the bar can show, grouped by kind (Standard bar, Character, Vitals, …). Point at a group, click an item, and it goes on the end of that side. Hover an item for what it shows.
- **Click a placed item** — opens its menu: **Move earlier** / **Move later** within its side, **Move to** another side (or a side of another row), and **Remove**.
- **Scroll as a marquee** — shows the row as one line of text crawling sideways (left items, then centre, then right), like the "update available" crawl in the title bar. Use it when you want more on a row than fits the window. Only text crawls: the engine chip, the statline warning and the connection light stay where they are, at the left end (if they sit on the left or centre) or the right end (if they sit on the right).
- **Remove row** — on every row when there is more than one; the bar always keeps at least one.
- **Add a status bar row** — up to four. A new row goes under the others, and the window grows by the height of the row so the terminal keeps its size (and shrinks back when a row is removed).
- **Reset to default** — back to the single original row.

An item with nothing to show takes no space, and the items after it close up. The statline warning, the looked-at target, the loop name, the next event and the combat target are empty most of the time.

### Preview

Above the rows, **Preview** draws the bar as it would look with your edits so far. It uses live values where there are any and sample values otherwise, so every item you placed is visible even when you aren't connected. Nothing under the terminal changes until you press **OK** or **Apply**; **Cancel** throws the edits away.

### What you can show

| Group | Items |
|---|---|
| Standard bar | Engine state chip · Location (map/room, the walk readout, or the lap and its step: `lap 12 · step 36 of 60`) · Exp rate · Time to next level · Looked-at target HP · Statline warning · Combat tick · HP tick · Mana tick · Connection light |
| Character | Profile name · Character name · Level · Race and class · Lives · BBS · Game data set · Combat profile (number and name, number only, or name only) · Gear set |
| Vitals | HP · HP percent · Mana · Mana percent · Posture (resting / meditating) · Stealth (sneaking / hidden) · Encumbrance (the word, weight carried out of your limit, and percent) |
| Location and movement | Map / room number · Room name · Loop name · Lap · Loop step (`Step 36 of 60`) · Walk destination |
| Combat | Combat target · Exp to next level · Party (size and leader) · Hit rate · Crit rate · Backstab rate · Average hit · Average round · Dodge rate · Hit-taken rate |
| Session stats | Time online · Exp earned · Kills · Kills per hour · Cash collected · Cash per hour · Items collected · Items sold · Steps walked · Average step time · Sneak success |
| Other | Auto engines (which are on) · Next event (the Event due soonest, and how long until it fires) · Cash carried · Clock · Custom text |

The Combat and Session stats items are the same tallies the **Session Stats** window shows.

### Custom text

**Custom text…** (in the Other group) adds a box where you type your own label. Put another item's name in braces to show its live value: `Lap {lap} of {loop}`, or `{profile} · {hp} · {mana}`. Hover the box for the full list of names (`{profile}`, `{hp}`, `{roomkey}`, …). A name that isn't an item is left exactly as typed, so a typo is easy to spot in the preview. The button beside the box opens the same move / remove menu.

**Important notes:** The standard-bar items update the instant they change. The others are read twice a second, and only the ones actually on a bar are read, so a bigger bar costs next to nothing; a marquee row steps about four and a half times a second. The layout applies when you click OK/Apply — no restart needed.

---

## Combat

Settings → Combat. Two switches live *outside* this tab and gate everything here: **Auto-Combat** (Settings → General, or its toolbar toggle) must be on for any of this to matter at all; **Auto-Nuke** separately gates **both multi-attack** slots and the **AoE-debuff** slot (single-target attack spells aren't considered "nukes" and stay available regardless). The **single-target debuff** is part of the attack rotation, so it follows **Auto-Combat**, not Auto-Nuke.

While a **backstab** is still owed (you're sneaking or hidden with Do BS attacks on), every pre-attack debuff waits until after the backstab round: any cast ends your sneak, so a debuff first would spend the surprise.

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
**When you might change it:** Only if your class or realm uses a non-standard attack word. A Mystic sets it to a martial-arts strike: `punch`, `kick` or `jumpkick`, usually typed short (`pu`, `ju`; kick is `kic` at the shortest, since the game doesn't take `ki`). A saved `ki` is still sent as you set it, and the program log warns that it does nothing.

**Monsters that need magic to hit.** Some monsters can only be hurt by an attack with enough *hit magic*. Before it fights one, MudPlay checks whether your attack can hurt it, and leaves alone (walks past) a monster nothing you have can damage. What it weighs:

- **A weapon attack** (`a`, `bash`, `smash`): the weapon's hit magic together with your class's own. A **Witchunter** carries enough of its own to hurt a magical monster with any weapon it can use.
- **A martial-arts strike** (`punch`, `kick`, `jumpkick`): your class's own hit magic only. A strike doesn't use the weapon, so whatever you are holding makes no difference. A **Mystic's** strikes hurt magical monsters this way, whatever weapon is in hand.
- If neither attack can hurt it, your attack spells are tried instead; with none that can land, the monster is skipped. The Program Log says why on a `skip un-actionable` line, with the level the monster needs and what each attack lands with.
- The game has the last word: a `Your weapon has no effect…`, `Your fists have no effect…` or `Your feet have no effect…` line writes that monster off for that attack for the rest of the room.

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
- **How the re-announce is timed** (these modes): party announces trickle in a line at a time, and someone reacting to another member's announce lands a moment later. So MudPlay waits until the announces stop for half a second and re-announces once, after the last. Whenever a qualifying announce still lands after yours, it re-announces again — every time — so you always end up last.
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

The backstab options that depend on it (*Don't BS if multi-attack room spell is firing*, *Run if BS fails*, *Hit and Run tactics*) sit indented beneath it and are greyed out while it's off. *Clear hostiles when sneak broken by see-hidden monster* and *Clear hostiles when sneak fails* are separate combat-off stealth-running options, so they're listed on their own below them.

### Don't BS if multi-attack room spell is firing

**Default:** On
**What it does:** Skips the backstab attempt whenever the room holds enough enemies to trigger your configured room-wide attack spell, so you don't waste a sneak opener on an AoE round. Keyed on the enemy count meeting the Multi-attack 1 slot's minimum (not on whether you currently have the mana), so the opener is skipped consistently in a room you mean to room-spell.

### Run if BS fails

**Default:** Off
**What it does:** Runs instead of fighting when your backstab opener can't work. A failed backstab leaves the target alert and swinging at you, so the fight is riskier than the one you planned. It covers two cases:
- **The backstab swings without "surprise".** It missed its surprise and you're now in an ordinary fight. MudPlay waits for that round to finish, and doesn't run if the same round killed the target and nothing else is in the room.
- **Your sneak broke on the way in.** Either `You make a sound as you enter the room!`, or the room showed up without the game's `Sneaking...` line (a silent break). You entered seen, so a backstab is bound to fail, and MudPlay runs rather than opening with a plain attack.

It runs the way *Run distance* and *Go backwards if running* set for any flee, only sends `break` when you're actually engaged, and needs a running loop or walk. With it off (and *Hit and Run tactics* off), a room where your backstab couldn't or didn't work is simply fought.

**Greyed out while Hit and Run tactics is on.** *Hit and Run tactics* does everything this option does and more, so this box has no effect then and can't be changed.

**After any backstab** (landed or not), if the target is still standing and nothing's making you run, MudPlay re-announces the round's attack once that round is over: the spell your action order picks, or `a <target>`. The party then sees what you're fighting, since a backstab itself is silent. (With an *attack last* timing, other players' announces drive that re-attack instead.)

**On its own**, *Run if BS fails* is a safety net for backstab openers: when one fails you back off, and otherwise you fight as normal. For backstab-only play, use *Hit and Run tactics*, which includes it.

### Hit and Run tactics / Give up and fight after N runs

**Default:** Off / 3 runs
**What it does:** Backstab everything, as many times as it takes. The **only** time you stay is when a backstab kills its target and nothing else is in the room: then your route just carries on. Otherwise you run, your loop or walk re-sneaks, and you come back in with another backstab. It runs when:
- **the backstab missed:** it swung without "surprise";
- **the target survived** the backstab;
- **other monsters are in the room**, even if the backstab killed its target;
- **your sneak broke on the way in:** `You make a sound as you enter the room!`, or no `Sneaking...` on arrival, so the backstab would fail;
- **a fight would start without a backstab:** a monster walks in after the room is clear, or one chases you.

**After a run, a loop walks straight back to the room it fled, sneaking, and carries on from there.** It doesn't restart the lap from the nearest waypoint. This applies to a hit-and-run, a failed backstab's run and a PvP flee whichever way they ran, and to every run made backwards. A low-HP or mana run, or a run from a monster set to Flee, made with *Go backwards if running* unticked does not come back: the loop carries on from where it landed.

It includes everything *Run if BS fails* does, which is why that box greys out while this is on. It runs the way *Run distance* and *Go backwards if running* set for any flee, and needs **Do BS attacks** and a running loop or walk.

It doesn't run where a backstab couldn't work anyway: a room with a see-hidden monster, or a monster marked *don't backstab*.

**Give up and fight after N runs** caps the runs between backstabs, the first included. Once they're spent you stand and fight rather than keep hunting for a chance to re-sneak. A landed backstab starts the count over.

### Clear hostiles when sneak broken by see-hidden monster

**Default:** Off
**What it does:** A safety valve for stealth routes. While Auto-Sneak is on (you're trying to sneak through a route untouched), a see-hidden monster in a room breaks your stealth. With this on, MudPlay stops, fights and clears the room instead of walking on exposed and dragging monsters behind you, then re-sneaks and carries on. Because the room is now clear, any buff/cure the sneak-aware timing was holding fires there before you re-sneak.

**Your room thresholds still apply to where it stops.** A room with fewer monsters than **Min monsters** or more than **Max monsters** isn't stopped in, the same as any other fight: you move on, still exposed. The break stays with you until you're sneaking again, not with the room it happened in, so the **first room that does meet your thresholds** is where you stop, whether or not anything there sees hidden. The same goes if you're carried out of the room by a move that was already sent. Once stopped, the **whole room is cleared**, even if more monsters walk in past your Max while you fight. Then you re-sneak, the break is over, and the route goes back to behaving as your settings say: sneaking past everything.

This works whether **Auto-Combat is on or off**: with Auto-Combat off it engages just for that room and fights it to the end, moving to the next monster as each one dies.

**While solo / While in a party.** Two ticks under the option say when it applies. **While solo** covers you on your own; **While in a party** covers you leading or following. Both are on by default, which is how the option behaved before. Untick **While in a party** to clear see-hidden rooms when you're alone but keep running with the party when you're grouped (or the other way round). With both unticked the option does nothing. Like the option itself, the two ticks are saved with a combat profile's Backstab group.

### Clear hostiles when sneak fails

**Default:** Off
**What it does:** For running through an area with **Auto-Combat off** and **Auto-Sneak on**. When a sneaked move fails, MudPlay can stop and clear the room instead of walking on exposed. A failed move is `You make a sound as you enter the room!`, or a room that shows without `Sneaking...`. MudPlay stops only if the room's monster count is inside your **Min / Max monsters in room** thresholds. It then holds the walk, fights every monster in the room you'd normally engage, re-sneaks and carries on skipping. A room outside the thresholds is walked through unsneaked, as it would be without the option.

It only acts while Auto-Combat is off. With it on, the room is fought or skipped by your thresholds as usual. The failure counts only for the room you failed into: once the next move goes out, it's forgotten.

### Run distance

**Default:** `2` rooms
**What it does:** How many rooms MudPlay flees before re-checking whether it's safe to stop.

### Go backwards if running

**Default:** On (backward)
**What it does:** When fleeing, `Backward` retraces the rooms you just came through (safer — you already know what's there); unchecked (`Forward`) instead keeps pushing along your planned route into unexplored territory (faster, riskier).

**Where you go afterwards.** After a run backwards, a loop walks back to the room it ran from and a walk-to heads for its destination again, so both pass through that room once more. After a run forwards made for low HP or mana, or from a monster set to **Flee**, the loop or walk carries on from where the run landed and does not go back. (A run that walks over the end of a lap finishes that lap, which is counted as usual. If the run was turned off your route, the loop is re-planned from where it stopped.) A hit-and-run, a failed backstab's run and a PvP flee always come back.

Backward heads for where your loop or walk started. If you're already standing there, it runs the **opposite way to your walk** out of that room, steering clear of boss rooms and then of bigger lairs when another exit allows. (With no map data for the room, it falls back into the room you just came from.) While you're still under *run if below*, a monster that's there when a flee lands, or follows you in after, **doesn't get attacked** — MudPlay runs again instead. Each leg heads on away and never doubles back into the room the last one fled. Only when the only way out is back (a dead end) does it stand and fight. A flee that's decided the instant you walk into a room waits for that move to land first, so it always retreats from the room you're really in.

**A way out the game refuses is never tried twice.** If a flee move comes back *"There is no exit in that direction!"* (or any other refusal), MudPlay remembers that exit as shut for the rest of that run, and for as long as you then stay in that room, and takes another way out of the room — away from your route first, then along it — rather than sending the same move again. Only when every way out has been refused does it stand and fight.

A flee sends one move per room. A **text exit** on the way (a trail you leave with `go path`, say) is crossed with its own command; anything else that isn't a plain compass move makes the flee **stop short**: a lever or door step, or a teleport hop (the way in and out of somewhere like the Negative Power Plane). It retreats as far as the ordinary moves go and re-checks there rather than trying to cross it mid-fight — and if the very first step out is a teleport, it doesn't run at all and your other low-HP reactions take over. The program log names the step that cut the retreat short.

**A flee never steps into a room routes keep out of.** Crystal Lake's teleporting rooms are shut to every walk and loop, and a flee keeps out of them too: it stops short of one, or takes another way out when its first step would lead into one. A flee that starts inside such a room still leaves it.

### Break combat before running

**Default:** On
**What it does:** Sends a `break` command before the first flee move so you disengage cleanly first. Turning it off starts fleeing immediately, which is faster but the game may reject the first move since you're technically still fighting, wasting a round.

### Minimum mana per cast — Percentage / Value

**Default:** `Percentage`
**What it does:** Decides how every "Min mana per cast" field on the five spell slots below is read — as a 0–100% share of your maximum mana, or as a flat number.

### Combat profiles (quick-swap loadouts)

**What it does:** Saves a whole combat posture under a name so you can keep **several** and switch between them in one click. This helps when different fights want different setups — a fire loadout for most monsters, a cold one for the fire-immune, a cautious "bossing" loadout with a two-hander and a lower flee threshold. Rather than re-tuning your Combat and Health tabs each time, you save each as a profile and flip between them.

**What a profile remembers (a full loadout):**
- on the **Combat tab**: the **action order** (spells-first / physical-first / custom cycle), the **weapons & attack commands** (primary / alternate + off-hands and the normal / alternate verbs), the **backstab options** (including stealth running), the **room thresholds** (min / max monsters, run distance, **kill all engaged**, and **when running away** — go backwards, break before running), and the **spell combat** slots with their per-slot gates, the mana-threshold mode and the drain settings;
- the **entire Health tab** — rest / heal / flee / hangup thresholds, meditate / shadowrest, the emergency escape, and the pre-/post-rest commands;
- on the **Spells tab**: the **between-round spell-type priority order** and the **healing / regeneration picks** (Minor heal, Major heal, Emergency heal, HP Regen);
- on the **Party tab**: **party healing** (the Minor / Major single-target and party (AOE) heals, their thresholds, and how many members switch to the AOE heal).

The rest stays per-character and never swaps: targeting and display on the Combat tab, the cures and ailment gates on the Spells tab, the Buff Watchdog self-bless slots, and the Party tab's **Rank** and options.

**Include in combat profile.** Each of those groups has an **Include in combat profile** checkbox at the right of its header, checked by default. Uncheck it and that group stops swapping: **one set of values is shared by every combat profile**, and switching profiles leaves it as it is. The values you're looking at when you uncheck it become the shared ones. Check it again and every profile starts from that shared value, which you can then change per profile. The checkboxes belong to the character, not to a profile, and like everything else on these tabs they're saved with **Apply** / **OK**.

Every group a profile can carry has the checkbox on its header. While it's included, the group is wrapped in a **coloured border** with **"Combat profile: `<name>`"** beside the checkbox, so you can see at a glance what swaps with the profile; uncheck it and the border goes away. On the Combat tab the per-profile settings sit at the **top**; the **shared** settings (targeting, display) sit at the **bottom** under a **"Shared combat settings"** divider. Shared settings apply to every combat profile and don't swap with the chip.

**Each profile has its own colour.** Add a second profile and its chip picks up a distinct colour; the third another, and so on. That colour tints the profile's chip, all of its bordered groups (across the Combat, Health, Spells and Party tabs), and the Workshop's Default-set weapon rows — so it's always obvious which profile you're looking at.

**Weapons — how they stay in sync:** a profile's weapons *are* the Workshop → Equipment Manager **Default** gear set's weapon slots (the surface the combat engine actually reads). So editing a profile's weapon pickers here and editing the Default set's Weapon / Off-Hand / Alt rows in the Workshop are the **same loadout, kept in sync** — and the Workshop shows a matching amber "Combat profile: `<name>`" marker over those rows. Switching a profile writes its stored weapons into the Default set, so your equipped weapon changes with the profile. (Backstab gear stays global on the Backstab set.)

**Setting them up (in Settings → Combat):** your current setup is already **Profile 1** — you always have at least one. The **Combat profile** selector sits near the top of the tab:
- Numbered **chips** (`1 2 3 …`) are your profiles; the **active one has a ring**. Click a chip to load that profile into every per-profile group on the Combat, Health, Spells and Party tabs.
- **The same chips, with ＋ and ✕, sit at the top of the Health, Spells and Party tabs,** so you can flip between, add or remove profiles from whichever tab you're on, without going back to Combat. The name box stays on the Combat tab.
- **＋** adds a new, empty profile and switches to it, ready to fill in; **✕** removes the one you're on (the last one can't be removed).
- The **name box** just below the chips names the profile you're viewing.

This editor is **staged** — nothing is saved or used until you press **Apply** or **OK**. Switch chips, edit boxes (on any of these tabs), add and remove freely; it's all held in memory and committed together on save. **Cancel** (or the title-bar ✕) throws every change away. Once applied, the active profile's combat settings take effect on the next round, its Health thresholds on the next rest cycle, and its weapons on the next combat weapon read.

**Switching during play** (these act on your *saved* profiles right away, without opening Settings):
- **Action menu → Combat Profiles** — a fly-out listing every profile; click one to switch.
- **Toolbar buttons** (add them under Settings → Toolbar + Shortcuts) — a **Combat Profile (cycle)** button that shows the active number (`P1`, `P2`, …) and steps through them (left-click = next, right-click = previous), or a **Combat Profile (menu)** button that shows the active number the same way and pops the same fly-out when clicked.
- **`@profile`** — lets a trusted party member switch your profile remotely from chat (needs the **Alter settings** permission): `@profile 2` by number, `@profile fire` by name, or a bare `@profile` to report the roster without switching. Its forms, how a name is matched and every reply are under **@profile** in *Remote @-commands*.

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

### Room attacks and other players (PvP realms)

**Applies when:** *PvP is enabled on this realm* is ticked (Settings → BBS, on the realm). On any other realm none of this happens.
**What it does:** A room attack or room debuff hits every player in the room who isn't in your party. So on a PvP realm MudPlay looks at who is standing there before it uses one:

- **A player outside your party is in the room** — the two Multi-attack slots and the AOE debuff are held, and the round goes to your single-target spells or your weapon. A room spell you put in the Normal or Alternate attack slot (or in a monster's override) is held the same way. The hold lasts for as long as that player is in the room and not in your party; the moment they join, or leave, your room spells are back. Inviting them is left to the invite settings you already have. (With a full party of six they can't join, so the hold stays.)
- **A player walks in while your room attack is already running** — a room attack repeats by itself every round, so MudPlay sends `break` to stop it and carries on single-target. When they walk out again (their departure line), the hold is lifted.
- **You walk into a room where another player is room-attacking** — a walk or a loop carries on to its next room instead of fighting there. On Paradigm the game says so (`<player> is poised to assault the room!`). Stock says nothing, so there it is a guess: the player's class has room attacks (mages and druids mostly; gypsies, warlocks, bards and rangers too) and the room holds three or more monsters. A player whose class isn't known yet is treated as one who can. The same goes for a player who was in the room before you and starts a room attack while you are there. Moving by hand, MudPlay leaves the choice to you.

**Who counts:** only party members are spared. Everyone else holds your room spells: Friend, Neutral or Enemy. An Enemy who walks in on your room attack gets the same `break`, and then their PvP response (Settings → PvP) is carried out: attacked on their own if you are set to attack, or fled from, or hung up on. An Enemy is never left to be hit by a room spell.

**When another player attacks you:** MudPlay reads `<player> moves to attack you!` (and, as a backstop, a damage line that starts with a player's name) as an attack. A **Neutral** who attacks you is marked **Enemy** on the spot, saved with the realm's player list, and the terminal says so: `[PvP: <player> attacked you and is now marked Enemy]`. A **Friend** stays a Friend. A room attack only counts when you watched that player walk in on you and then start it: not when the room was theirs before you arrived, and not from someone who was in your party in the last two minutes (a teleport that splits the party can put a room attack out before everyone has rejoined). What MudPlay then does about an Enemy is chosen on Settings → PvP. Change anyone back in Game Data → Players.

**Knowing a player's class:** it comes from your party list, the realm's player list (`who`, a `look`), or the top list. Type the top-list command once on a realm and every listed player is remembered with their class, including ones you had no record of.

**A player MudPlay has no record of:** `who` lists everyone online, so when a name in the room can't be placed, or someone it doesn't know enters the realm, MudPlay sends one `who` to fill the records in. It is kept from spamming: at most one `who` every 30 seconds however many names turn up, and the same name isn't asked about again for five minutes. Until that `who` has been read the name counts as a player for the room-attack hold; if the list didn't carry it, it wasn't one.

**After a party split:** when a member drops out of your party, room attacks are also held until they are back in it or the hold on Settings → PvP runs out (two minutes by default), whether or not they are in the room yet.
**Important notes:** A debuff is a single cast: once it has gone out the damage is done, so it can only be held beforehand. The program log says when room attacks are held, released or broken off, and why a room was walked through (category `PvP`).

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

### Show combat round totals

**Default:** Off
**Terminal only:** this checkbox and the boxes under it control what prints **in the terminal**. The same table is always available in the **Round Totals** window (View → Round Totals), which has its own row choices and doesn't need this box ticked — see *Round Totals*.
**What it does:** After each combat round, prints a small yellow table to the terminal: how much damage each combatant **dealt** and **took** that round, one row for **everyone in the room** — you, party members, other players and monsters:

```
[Round 3 ------------------------]
[ Combatant          Dealt  Taken ]
[ You                   45     12 ]
[ Bob                   30      0 ]
[ large orc             12     75 ]
[ goblin                 0      0 ]
[ unknown                8     20 ]
```

The round number starts again at 1 once the room is clear of hostiles, so each fight counts its own rounds. **Everyone in the room is listed every round** — you, your party, other players and monsters — even at 0. **You** always come first, then your party, then other players and monsters; within each group the biggest dealer is on top. A room spell (yours or a party member's) is credited to its caster and counts against every monster in the room. **unknown** only appears when a line couldn't be pinned to anyone. The program log and bug report keep the same numbers as two compact lines per round. A round prints as soon as its lines stop (a quarter of a second), or the moment the room is clear, so the totals sit right under that round's combat, ahead of your next action.


Four boxes under it (enabled while round totals are on, all **Off** by default) pick which rows print: **Me**, **Party**, **Other players** (players in the room who aren't in your party) and **Monsters**. Tick all four for the full table, or just the groups you care about. The **unknown** row prints whenever the table does. With **none ticked, no table prints**, so tick at least one after turning round totals on. (Updating from a version before these boxes: if round totals were on, all four start ticked; if they were off, all four start off.)

A choice below them sets how same-named monsters show — one or the other:
- **Stack same-named monsters (muckworm x3)** (the default) puts them on one row labelled with how many there were, so a room spell's 2436 taken reads as three muckworms' worth.
- **One row per monster** gives each monster its own row (`muckworm #1`, `#2`, …). A hit on a shared name goes to the first one listed in *Also here:* (the same rule the monster HP estimates use), and a room spell hits each. It needs the monster's HP from game data; one without it stays on a stacked row.

**Cap at monster HP** (Off by default) counts a monster's damage only up to the HP it had left, the way the game applies it: a monster **can't take more damage than it has left**, so an 812 room spell on a 540-HP muckworm counts as 540 taken and 540 dealt by the caster, and a killing blow counts just what killed it (an 80 slash on a monster with 19 HP left reads 19). The HP used is the monster's running estimate, which includes regen and whatever your `look`s showed. With it **off**, every hit counts the number the game printed, so a killing blow reads in full and a monster's Taken can run past its max HP. The Player Statistics panel's per-round damage follows the same choice.

**Important notes:**
- **How damage is credited.** Each "… for N damage!" line is read against the room's occupants (from *Also here:*), your party and "you". "Bob slashes large orc for 30" credits Bob, and "The large orc claws you with its pincers for 12" is damage you took from the orc.
- **unknown** collects damage a line doesn't name a side for. Examples: a spell whose line names no caster ("Acid sears you"), an area effect ("An earthquake rocks the room"), or someone the room display hasn't shown yet.
- **Damage nobody dealt** — a poison tick ("You are poisoned for 2 damage!"), "You combust", "Your blood is drained" — counts only under your **Taken**; no one is credited with dealing it.
- **Your own spells.** Many spell lines read the same to the caster as to everyone watching ("Dark flame sears the orc for 12 damage!"). Such a line counts as **yours** when it's one of your class's spells and you cast within the last few seconds; otherwise its dealer is unknown. If a party member casts the same spell in the same round, theirs counts as yours too. A weapon proc names only its victim too ("Flames burn the orc"), and it goes to whoever hit that monster just before it.
- **Your room spells** ("A hellish storm of fire and brimstone scorches your foes for 603 damage!") hit every monster in the room, so each is credited with taking the full amount and you with dealing it to each. Each monster's magic resistance trims its share a little, which no line shows.
- **Two monsters with the same name** share one entry, because the game prints them identically.
- **Where else it shows up.** The same ledger feeds Session Stats (your swings, procs, spells, per-round damage and the blows that hit you), the program log (`[Round]` rows) and the bug report (last 10 rounds). The Wire Inspector's **Classified** pane shows how each damage line was credited.

---

## Spells (+ Ailments)

Settings → Spells. This tab picks *which spell* fills each automated role and sets the *priority order* the caster walks through each tick. The actual HP/mana percentage *thresholds* that trigger a cast live on the **Health** tab, not here.

### Spell type priority

**Default order (highest priority first):** Emergency heal → Major party heal → Minor party heal → Major self heal → Priority buffs → Minor self heal → Downed-ally heal (rescue) → Curing → Buffing → Debuffing.
**What it does:** Every tick, MudPlay checks all ten categories and casts the highest-priority one that has something ready to fire. Use the **▲ / ▼** arrows on each row to reorder them — higher in the list casts earlier.
**When you might change it:** Move Curing above self-heals if you'd rather cure a debilitating ailment before topping off HP; move Debuffing higher if landing your debuff matters more to you than proactive buffing. Emergency heal defaults to the top so a life-threat save leads, but you can move it like any other row.
**Priority buffs** are the buffs you ticked **Priority buff** in the Buff Watchdog (edit a buff). They cast at this row's rank instead of with the rest under **Buffing**, so by default a priority buff goes up ahead of a minor self heal. A buff that isn't ticked stays under Buffing. With nothing ticked the row does nothing.

**If you had this list before Priority buffs existed:** a list you never reordered moved to the default order above. A list you had reordered kept your order, with Priority buffs added at the **bottom** — move it up to where you want it.

**Important notes:** Emergency heal and the downed-ally rescue used to be hidden always-first casts; they're now ordinary rows in this list (defaulting to slots 1 and 7), so you can rank them wherever you like. Emergency heal keeps its special *gates* — it ignores the mana floor and fires in any state (see below) — but its *position* in the queue is now yours to set.

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
- It's the only cast that fires **while a round's hits are still landing**. Your HP drops hit by hit through a round, so MudPlay waits for it to settle (a fraction of a second) before picking Minor vs Major heal, a cure, or a buff — otherwise it could spend the round's one cast on a Major heal and leave you with no cast when the round ends much lower. Emergency heal goes out the moment HP crosses its trigger, unless you ranked a cast that's also due above it; then that cast fires once HP settles.

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
**What it does:** The specific spell used to cure each named ailment. These feed the Curing priority category (self first, then party members). A party member is cured when their MudPlay client announces the ailment — `@held` (paralysed / held), `.@poisoned`, `.@diseased`, `.@blind` — in that order: a hold first, as for you. A member's hold clears when they send `@ok`.

During a fight a cure is cast between rounds like any other spell in the priority list: when the round's hits have landed and nothing ranked above Curing (a heal, by default) is due, the cure goes out.

### Cure after combat (one box beside each cure)

**Default:** off
**What it does:** Ticked, that cure waits until the fight is over (the room has no monster left to fight) before it is cast, on you or on a party member. A cure takes the round's one between-round cast, so no heal can go out that round, and the next hit often poisons you or knocks you down again; tick the box for a cure you would rather not spend a round on. Each cure has its own box, so you can hold the poison cure and still cure a hold mid-fight. A held cure never blocks the spells ranked below it. Saved with the character, like the cure spells.

### Room light — moved to the Buff Watchdog

The room-light spell is configured in the **Buff Watchdog** now. Add it there and tick **Only when the room is dark** on the slot to keep the reactive cast-on-entering-a-dark-room behaviour (via the auto-light system); leave it unticked to maintain the light like an ordinary buff. See the **Buff Watchdog** section.

### Self-bless — moved to the Buff Watchdog

The self-buff slots (which spells, `#item`-cast buffs, per-slot recast timers) live in the **Buff Watchdog** now, folded into the one unified buff list alongside your party buffs — tick the **Self** box on a slot to cast it on yourself. See the **Buff Watchdog** section for how to add and target buffs. *When* each buff may cast — its mana floor, and whether it casts in a recovery rest or a fight — is set on the buff too, in its edit dialog (**Cast if mana ≥**, **Cast while resting**, **Cast during combat**). Those replaced this tab's *Bless self while resting / during combat* boxes; each buff you already had took the values you had here.

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

**Where that max comes from:** MudPlay records your max HP and mana from a `stat` screen, but only one taken while your **Default set** is worn:
- **Other screens don't count:** a `stat` in other gear, or an `exp` screen, never changes it.
- **When it's re-read:** only when you level up or change your Default set in a way that alters your max HP or mana. Then MudPlay sends a `stat` itself the next time your Default set is on and nothing else is going on, and keeps using the old figures until it lands.
- **Before the first one:** until a `stat` has been seen in Default gear, the max is worked out from your current max and your gear's bonuses.
- **When it's dropped:** if the recorded figures were read at a different level and you now own none of the Default set's items (a profile whose gear sets belong to another character, a reroll), they can't be re-read, so they're discarded and the live max is used.
- **Copied profiles:** a copy starts with nothing recorded. See *Profile Management* → **Copy**.

Only Default-set items you actually **have** (worn or carried) count — an item lost to a deathpile, sold, or never obtained is left out, and when you have none of them (or before your first inventory check) the **live** max is used instead. The figure beside each threshold says which basis it's using: **(def)** for the Default-set max, **(live)** for your current max.

**Following a party:** when your `@wait` was for mana and the game says `Meditation will not help at this time.`, your mana is full. MudPlay swaps your Default set back on, re-reads your HP and mana, then sends `@ok`.

### Rest if below (HP / MA)

**Default:** HP 60%, Mana 30%
**What it does:** The trigger for auto-resting. Once a pool drops to or below this, MudPlay pauses movement and starts resting the moment combat ends (never mid-fight). Not in a room whose own damage would break the rest or the meditation: there it carries on and rests in the next room that isn't barred (see *No resting in a room that hurts* under Health). Which room spells bar resting is set in **Settings → Periodic Damage Room Spells**.

### Heal (rest)

**Default:** 80%
**What it does:** While actually resting, cast the Minor heal spell if HP is still below this — a way to speed along recovery rather than waiting on the passive rest tick alone. In a room whose spell bars resting (**Settings → Periodic Damage Room Spells**; see *No resting in a room that hurts* under Health), it is cast standing instead while a rest is owed.

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
**Important notes:** Set either to `0` to disable that pool's flee trigger — useful for a class with no mana pool. Which way it runs is **Go backwards if running** (Settings → Combat): ticked, a loop comes back to the room it ran from once you have recovered; unticked, the run goes on along your route and the loop carries on from where it landed, and does not return to the fight room. A flee ends at once if you die, the connection drops or another character is loaded: nothing more is sent for it. After a disconnect, a walk-to the flee had paused goes on at your first game prompt back in the game.

**A party follower never flees.** While you are in a party and not its leader, nothing makes your character run: not low HP or mana, not Hit and Run or a failed backstab, not a monster set to Flee, and not the PvP tab's Flee (by rooms or to a Flee room). A run of your own would walk you out of the party. At low HP your client asks the party's healer for a heal with `@heal` instead; a Hit and Run character stands and fights; a PvP **Flee then hang up** hangs up at once. The wimpy jump is not a flee: with **Sys goto wimpy instead of hanging** set up, a follower whose health rule would hang up still jumps there, as it would still hang up. Leading or solo, everything flees as set.

### Hang up if below

**Default:** 5%
**What it does:** The absolute last resort: disconnects the game outright once HP falls to or below this value. Since 0 HP only "drops" you in MajorMUD rather than killing you outright, this threshold can go negative, all the way down to (but never past) the point your BBS's realm actually treats as death.
**Important notes:** There's no "0 disables it" here — to fully disable the emergency hangup, use the toolbar's "Disable hangups" toggle instead. **It sits behind Auto-Rest**: with Auto-Rest off it does not fire, even with Auto-Heal on (it used to run under either). With the **master switch (Auto-All) off** it fires only if General → **Allow hangup in all-off mode** is ticked, and then whatever the Auto-Rest toggle reads.

### Sys goto wimpy instead of hanging

**Default:** Off
**What it does:** Changes what the emergency escape *does* when your HP crosses the "Hang up if below" threshold with a hostile present. Instead of dropping the connection, MudPlay breaks combat (if you're actively fighting) and fires **`sys goto <location>`** to jump you to a safe town — a "wimpy" escape that keeps you online. Pick which location from the **Wimpy goto location** dropdown right below the checkbox.
**Important notes:** This needs the **Sysop goto** power enabled on the BBS tab (the checkbox is greyed out until it is), and the location must be one of that BBS's Sys Goto entries. If the power is off, or the chosen location has been removed from the table, MudPlay falls back to the normal hangup — you're never left sitting in a fight. It rides the same trigger as the hangup, so the toolbar's "Disable hangups" toggle suppresses this too.

### Heal if above (rest/idle) / Heal if above (combat)

**Default:** Resting 50%, Combat 0% (disabled — always heal)
**What it does:** A mana floor that gates self-heal casts — below this, MudPlay skips the heal so mana can regenerate. `0` disables the gate entirely (always heal regardless of mana).
**Important notes:** This floor gates Minor and Major heal only. **Emergency heal ignores it** — a last-resort save spends whatever mana is left rather than conserving it (it still won't attempt a spell it can't afford the mana for). So even with a high floor set here, your Emergency heal still fires in its band.

### Bless if above (moved)

The mana floor for re-casting buffs is set on each buff now: **Buff Watchdog → edit a buff → Cast if mana ≥**. Each buff you already had took the value that was set here.

### Use 'meditate' ability

**Default:** Off
**What it does:** Uses the class-specific `meditate` command instead of `rest`, on classes that have it.

### Meditate before resting

**Default:** Off
**What it does:** Only relevant with "Use 'meditate' ability" on. If both HP and mana are low at the same time, this decides whether MudPlay meditates first to top off mana before resting for HP. If only mana is low, MudPlay always meditates regardless of this setting.

### Utilize shadowrest

**Default:** Off
**What it does:** ShadowRest is a class ability on certain realms (not stock MajorMUD) that lets a stealthed character rest safely even with a monster in the room. With this on — and your class has the ability, and you're solo and currently hidden/sneaking — MudPlay uses that instead of retreating to rest. It also sneaks before each rest (and again after a buff cast mid-rest), so the rest stays stealthed. When a monster is in the room, nothing that would end the sneak goes out — no buffs, heals, gear swaps, searches or chatter — and combat isn't re-checked every few seconds, until you're rested to your rest-max; then the held actions go out. If something in the room attacks you, the ShadowRest is over: you fight back, or run if you're under *run if below*. With it off, MudPlay doesn't sneak for a rest at all — even on a race or class that has the ability.
**Important notes:** This checkbox only appears at all on realms that actually have a class with the ShadowRest ability; it's invisible on stock realms.

### Pre-rest / meditate command, Post-rest / meditate command

**Default:** empty (both)
**What it does:** Custom commands sent right before entering rest/meditate, and right after standing back up — e.g. checking your surroundings first, or re-arming something the moment you stand. The post-rest commands follow a rest that ran its course; a rest given up because the room began to bar it (see *No resting in a room that hurts* under Health) sends none.

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

**Where it's configured:** the party-buff **slots** (which spells, which members, recast timers) live in the **Buff Watchdog** — see the **Buff Watchdog** section under *Tools & Diagnostics*. *When* each buff may cast is set there too, on the buff itself (**Cast if mana ≥**, **Cast while resting**, **Cast during combat**).

**Targeting, and when they fire:**

- A **whole-party** spell (chant and the like) is sent once with no target and blankets the party, including you. Its **Party** checkbox is the master enable: unticking it stops the spell everywhere, clears Solo, and leaves both boxes visibly off. While that master is enabled, the **Solo** option allows it while you're alone (a whole-party cast still lands on a lone character); untick only Solo to make it party-only. Solo or in a party, it follows the buff's own **Cast while resting / Cast during combat** boxes (Buff Watchdog → edit the buff).
- A **single-target** spell is cast on each selected member individually with its own recast timer, is **not** cast on your own character (self comes from the self-bless slots), and only fires for a member who's both in your party and in the room — so it genuinely needs a party.

Your self-bless slots always fire, party or not.

**Supersession:** if a whole-party buff *removes* a spell you have in a self-bless slot (the Spell Book shows it as "Removes …" — e.g. **chant removes bless** on Paradigm), then in a party the client **layers them when it can and covers only when it can't**:
- **Stock, one-way remover** (your self-buff doesn't remove the party buff back) — both are kept: the party buff is cast first and your self-buff re-applied after it, since Stock only strips at the moment of cast.
- **Paradigm, or a mutual pair on either realm** — the two can't coexist, so the client stops self-casting the removed spell and lets the party buff cover you. The Buff Watchdog shows that self-buff row as **"covered by"** the party buff instead of a timer.

Any OTHER pair of configured buffs that remove each other this way — two self-cast buffs, two whole-party buffs, a whole-party buff removing a member's buff, and so on — aren't auto-resolved like this one case is; they instead get the **⚠** warning described in the **Buff Watchdog** section, so you know about the conflict without the client silently changing what it casts.

### Bless party while resting / during combat (moved)

These two boxes are gone from this tab. Each buff has its own **Cast while resting** and **Cast during combat** now (Buff Watchdog → edit a buff), and they cover casts on the party as well as on you. Each buff you already had that was cast on the party took the values you had here.

### Help leader open doors

**Default:** Off
**What it does:** When you see your party leader failing to bash a locked door, you automatically pitch in (bashing or picking, depending on your own door-preference setting).
**Important notes:** It only works while the leader **bashes**. The game shows the room each bash attempt, and that line is what this reacts to. A leader who picks the lock instead shows the room nothing while they try, so there is nothing to pitch in on.

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
**Important notes:** Only a run you start counts as a start. A loop that stops to go back for a party member who fell behind (held, knocked down, dropped) and then carries on is the same session — the counters keep running, as they do across a bank, sell or training trip.

### Re-invite lost party members

**Default:** On
**What it does:** Leader-only. If a party member disconnects and reconnects within the grace window (see "If leading, wait only" below), you automatically re-invite them instead of having to notice and do it manually. It also covers a follower your move left behind because they couldn't move (held, knocked down): you go back for them and re-invite them (see "If leading, wait only").

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
**What it does:** As leader, the one window every party wait uses (0 = wait until they send it / come back):
- how long you keep watching for a disconnected member to come back before giving up on them;
- how long a member's `@wait` (or `@held`) holds your automation before you move on without their `@ok`;
- how long you wait for a member you went back for to follow you again (at 0 this one wait is 90 seconds, so a member who never answers the re-invite can't hold your walk or loop for good);
- how long you hold for a member you left behind (below) after they rejoin.

**Left behind at an exit.** An exit you got through can turn followers away (an item they don't carry, a toll, a level or class gate). A toll is checked before you step through it, every member's purse asked and the short ones paid for (*A party at a toll*, under The Navigation window), so a member is left at one only when that check was wrong; when you come back to that toll after fetching them the purses are asked again, and a member already paid for there is not paid for a second time. On Paradigm the game tells you for each (`<name> is no longer following you.`, and `Your party has been disbanded.` when it was all of them); on Stock it tells you nothing, and their `@comeback` is how you hear. Either way a MudPlay follower asks with `@comeback`. **On a walk-to you go back for it, once**: you walk back as below, re-invite it and wait for it to follow, and your walk carries on. If the same member is then turned away at the same exit again, going back would never end, so **the walk ends there** with a line in the terminal naming the member and the exit, and no second pickup is started; what happens next is yours to decide, and their `@comeback` gets the answer an idle leader gives. Another gated exit further along the walk gets its own one pickup. **On a loop you don't go back**: the loop would leave it at the same exit every lap, so the request is refused with `I can't, my loop goes through an exit you can't pass` (said only while Settings → Talk lets denials be answered) and the loop carries on. That goes only for an exit that is part of the loop's own circuit and was crossed by a step of the loop: a gate on the way to the loop, on a train trip, or one you stepped through by hand doesn't count, and the member is gone back for. **Auto-Lair doesn't break off either**: it carries on, answers the member once (`I can't yet, an exit on my way turned you away. I'll invite you when my next pass finds you`), and invites them when a later pass finds them in a room it enters. For a loop and for Auto-Lair this applies only when the exit is what stopped that member: one with a `@wait` or `@held` out with you (or whose `@ok` came a moment before they failed to move, which shows they weren't free after all) was stopped by that, and one your client knows the exit lets through (a level inside its window from the `@level` probe, the right class or race, a purse that covers the toll, the item handed to them or counted) was stopped by something else; both are gone back for. Where your client can't tell, the gated step counts as the reason. What the game does outweighs what your client knows of the member: fetched from an exit once and dropped at that same exit again, they are treated as unable to pass it until the loop or Auto-Lair is stopped, or they are seen through it. When one step leaves several behind they are fetched one after the other, each at the room it names, and your walk, loop or Auto-Lair is put back after the last; a second request while you are fetching someone is answered `comeback already in progress — fetching <name> first, then you`.

**Left behind by a hold.** If a follower can't move when your walk, loop or Auto-Lair steps on — held or knocked down — the game drops them from the party (`<name> is no longer following you.`; with one follower the whole party disbands). With **Re-invite lost party members** on, you stop, backtrack to find them, and re-invite them. Once they follow, their party row shows **Held** and you hold the full window again, or until their `@ok`. Their client is sent `@waiting` so it knows you're holding: a MudPlay follower answers `@ok` at once if nothing still holds it, or as soon as the hold clears. If they were left behind within a few seconds of sending `@ok`, that `@ok` evidently didn't mean they could move, so this time you wait the **full** window and ignore their `@ok`. A teleport that splits the party — a token, or an exit like Darkwood's `go vortex` that moves only whoever uses it — also prints `<name> is no longer following you.` for everyone; that's expected, so you don't go back for them. The members come through on the relay and are re-invited where you land. An exit that teleports everyone on as they step through it (the golden idol's passage in the Earthen Catacombs) is the same, except that the followers are pulled through behind you instead of relayed. The game prints the same line when a follower walks off by themselves (one you sent on with `@do`, say), so you only go back for a member who dropped within a few seconds of a move of your own. A move you made yourself — typed, or sent by a macro, a trigger or a relayed `@do` — is yours to follow up: nobody is gone back for after one. (A member your walk's own step left behind is still gone back for, even if a pause landed at the same moment.) And once a member you were going back for is following you again, the trip back ends on the spot.

**Too heavy to move.** A debuff such as *weakness* or *frail* lowers how much you can carry, so a character near the limit can suddenly see `You are too heavy to move`. That isn't a hold: freedom and cure paralysis don't help. When one of these debuffs lands, your client reads `i` straight away to see whether you are over your (lowered) max. If you are, or if the game refuses a move for weight, your own walk, loop or Auto-Lair waits (the hold chip reads **Too heavy**), and as a follower your client telepaths the leader `@wait (too heavy to move)`. It reads `i` again when the debuff wears off and every 15 seconds in between, and carries on (sending `@ok` to a leader) once you're back under your max, either because the debuff wore off or because you dropped something. It never drops anything for you.

**As a follower:** your client sends the leader `@wait` when you drop below a rest floor, and asks again whenever you drop below one afresh (HP or mana) or get walked on while still recovering — so if the leader's wait window runs out while you're resting, the next drop or the next room you're pulled into re-asks instead of leaving you dragged along. A re-ask waits at least 5 seconds after the last `@wait`, so HP bouncing across the rest floor doesn't send a burst of them. `@ok` goes once you're back to full rest-max and stay there for a second, so a one-prompt blip up to rest-max doesn't release the leader.

### Return distance (rooms)

**Default:** `30`
**What it does:** How far the leader will go to retrieve a party member. When the member names their room (`@comeback 9/1012`, or an `@where` answer), it's the farthest in map rooms the leader will walk there; beyond it the leader tells them to catch up on their own. When they send a bare `@comeback`, it's how many rooms the leader walks back along its own recent path looking for them (the client remembers the last 50) before going idle. It doesn't limit the fetch at the end of a train trip: members the leader's own trip left behind are gone back for however far the trip went.

### If leading, accept @comeback for (min)

**Default:** `2`
**What it does:** How many minutes after a member drops (or is left behind) you'll still honour their `@comeback` and go back for them. If a search for them gives up, the walk, loop or Auto-Lair it stopped is kept this long, so a later `@comeback` still recovers them and then resumes it. As a follower, the same value limits your own rejoin: after a longer drop, you don't send `@comeback` when you re-enter. It is also how long a follower left behind waits, with no answer and no pickup, before it stops counting itself as following (see **Auto-request @comeback when left behind**, *When nobody is coming*). `0` turns `@comeback` rejoin off.

### Send `par` (party status)

`par` is the game's party screen: each member's HP and mana percentage, whether they're resting, and who is still in the party. This list picks what makes MudPlay send it. Tick any mix of the three, or none; they all apply at once.

- **every N seconds** — **Default:** on, `5` seconds (1–60). `par` on a timer, as MegaMUD does.
- **after each combat round** — **Default:** off. `par` as soon as a combat round you fought in ends, when its round totals are worked out. A round you only stood by for (a member's fight in the room) doesn't count.
  - **including combat I only witness** — **Default:** off. Widens the box above to every combat round seen in the room, whether or not you took part: a member's fight while you stand by, or any fight while **Auto Combat** is off. It does nothing unless *after each combat round* is ticked.
- **when a combat round has unknown damage** — **Default:** off. `par` when a round's totals have an **unknown** row: damage no line named a dealer or a victim for, so it may have landed on a member without MudPlay seeing whom. This one doesn't need you in the fight.

A round sends one `par` at most, whichever of the two round boxes asked for it, and a round's `par` starts the timer's count over, so the timer and the rounds together don't send two in a row. In a fight whose rounds come as fast as the timer, the timer stays quiet and takes over again between fights.

**With none ticked** MudPlay never sends `par` to read health. (One `par` is still sent after you lead the party through an item gate with a member who never answers counts; see *Leading a party past an item gate*.) Members' HP then moves only from the round totals (see *HP between `par` polls* under The Party window), which works only for members whose maximum HP is known, and is only as right as the totals are. Their mana, their resting state and whether they're still in the party aren't re-read until you type `par` yourself.

Whatever is ticked, `par` is only sent while you're in a party and **Auto Heal** is on, and never into the trainer screen or the board's menus. The program log notes each change to this list under `PartyPoll`.

---

## PvP

Settings → PvP. What MudPlay does about a player you marked **Enemy** (Game Data → Players) who is in the room with you, or about any player who attacks you. Nothing on this tab is acted on unless the realm you are playing has **PvP is enabled on this realm** ticked on the BBS tab; the tab says so when it isn't. The care taken with room attacks around other players is described under *Room attacks and other players (PvP realms)* in Combat, and needs nothing set here.

**Who is answered.** Party members never. A **Friend** never, even one who attacks you, unless *Flip a Friend to Enemy if they attack you* is ticked. A **Neutral** is left alone until they attack you; that marks them Enemy on the spot, saved, and the response follows. An **Enemy** is answered on sight. A player's own **PvP response** in Game Data → Players replaces the action chosen here for that one player.

**A Hangup monster waits for PvP.** While a fight with a player is under way, or an Enemy is in the room, a monster whose relationship is **Hangup** is not hung up on; it is when the fight ends or the Enemy has left, if it is still there. The Health tab's hang-up is not held back.

**One response per encounter.** A player just answered is not answered again for 30 seconds on sight, or 10 seconds when they attack. The terminal shows each response as `[PvP: …]`, and the program log carries it under `PvP`.

### Action

**Default:** Hang up immediately
**What it does:** The general response. There is no "do nothing" choice: on a realm without PvP the realm simply isn't ticked as a PvP realm, and nothing here applies.

- **Hang up immediately** — sends your exit command and drops the connection. *Disable hangups* (General) is honoured, and when the Health tab's sysop wimpy jump is set up it is taken in place of the hang-up.
- **Flee, then hang up** — starts a flee, then hangs up once the *Flee hangup delay* has passed. With nowhere to flee it hangs up at once. Nothing else is answered while the hang-up is pending.
- **Flee (come back later)** — flees, stays away for *Come back after*, then picks the interrupted walk or loop up again. If the Enemy is still there when you return, the response fires again.
- **Attack (dangerous)** — attacks an Enemy on sight and stops when they leave the room. The running walk or loop is stopped for the fight and picked up again after it, and monsters in the room are left alone meanwhile. The attack is your Combat tab's normal attack spell when the game lets that spell be aimed at a player, otherwise your normal attack command.
- **Chase and attack (dangerous)** — the same, and follows them when they leave. How far and how is set under *Chasing* below.

**Attacking first is an evil deed.** Attacking a player who has not attacked you adds evil points and can bring a dark cloud (and with it the loss of Good-only gear). The tab says so beside the two attack choices. A fight also ends, with a `[PvP: …]` line, when the game refuses it: your evil warnings are on (see the checkbox below), you are already too evil, you are lawful, or the two of you are too far apart in level.

**A leader's `@kill <player>`** starts the same fight with a player who is in your room, for anyone you have granted the command. Naming a monster still retargets your combat as before.

### Flee to

**Default:** none
**What it does:** A room from your GOTO favourites to run to. Whatever walk, loop or Auto-Lair run was under way is stopped and the walker heads there in **Sprint Mode**, so nothing on the way is fought, looted, searched or rested for; Sprint ends by itself on arriving. You then stay in that room. For *Flee (come back later)* the *Come back after* time starts on arriving, and when it is up you go back to what you were doing.
**Important notes:** With none chosen, or no route to the room, the flee runs back along your walk or loop instead (*No. of rooms to flee*). Add the room to your favourites first (map right-click) for it to be listed.

### No. of rooms to flee

**Default:** `10`
**What it does:** With no *Flee to* room, a flee runs backwards along whatever you were doing, the loop or the walk, this many rooms. It is the same retreat a low-HP flee makes, with this length in place of the Combat tab's run distance. *Flee, then hang up* hangs up as soon as those rooms are behind you.
**Important notes:** It needs a walk or loop to be running: moving by hand with no *Flee to* room there is nowhere to flee, so *Flee, then hang up* hangs up at once and *Flee (come back later)* tells you so and does nothing.

### Flee hangup delay

**Default:** `30` seconds
**What it does:** For *Flee, then hang up*. Fleeing to a *Flee to* room, the hang-up comes this long after the flee starts. Running back by rooms, the hang-up comes when the rooms are done, and this is only the latest it can be (a run that gets stuck still ends in a hang-up).

### Come back after

**Default:** `60` seconds
**What it does:** For *Flee (come back later)*: how long to stay away once the flee has landed before the interrupted walk or loop is picked up again. Resting and healing go on as usual meanwhile.

### Notify gang members

**Default:** Off
**What it does:** Says on the gang channel, with the room, about the events you tick under it:

- **an Enemy is seen** — `PvP: Raijin is here at Town Square`
- **a player attacks me** — `PvP: Raijin attacked me at Town Square`
- **I attack a player** — `PvP: attacking Raijin at Town Square`, including an attack a leader's `@kill` ordered

All three are ticked by default. **The same line about the same player at most every N seconds** (default `60`) sets how often a line may repeat while the situation lasts: an Enemy who stays in sight, or keeps attacking, is announced again only after that long. `0` sends the line every time the event happens.

### Re-connect after PvP in N minutes

**Default:** Off, `30` minutes
**What it does:** After a PvP hang-up, dials back in that many minutes later. The terminal shows the dial time; pressing Connect cancels it.
**and enter the realm** (default on): ticked, the dial-back goes on into the game. Unticked, it logs in and waits at the menu for you.
**Important notes:** Any other hang-up still leaves you at the menu on the next login, as before. This applies only to a hang-up the PvP response made.

### Flip a Friend to Enemy if they attack you

**Default:** Off
**What it does:** Off, a Friend stays a Friend whatever they do and is never answered. On, a Friend who attacks you is marked Enemy the way a Neutral is, and answered.

### Turn off my evil warnings if they are on, to attack

**Default:** Off
**What it does:** With evil warnings on, the game refuses an attack on a player who hasn't attacked you (`To do this action, you must turn off your evil warnings.`). Ticked, MudPlay sends `set warning off` and attacks again. Unticked, the refusal ends the fight.
**When they go back on:** not when the fight ends. MudPlay sends `set warning on` once you are back at what the fight interrupted (your loop, walk or Auto-Lair run going again) and a minute has passed with no new fight; a fight that interrupted nothing only needs the quiet minute. A second fight before then finds them still off and sends nothing more.
**Important notes:** Hitting back at someone who attacked you never needs it. If the connection drops while they are off, they can't be put back; the terminal tells you, and `set warning on` restores them.

### When I lead a party and start a fight with a player, send @kill to the party

**Default:** On
**What it does:** When you are the party leader and a fight with a player starts (your PvP action attacking an Enemy), MudPlay also says `.@kill <player>`, so members who take your remote commands attack the same player.
**Important notes:** It is not sent when you are following or alone, or for a fight that someone else's `@kill` started. A member needs the *Execute commands* permission granted to you for it to act, and their own evil-warning setting below decides whether the attack gets through.

### Turn off my evil warnings when a leader's @kill names a player

**Default:** Off
**What it does:** A leader's `@kill <player>` is, for you, an attack on someone who hasn't attacked you, even when that player attacked your leader: the game only lets the one who was attacked hit back for free. Ticked, MudPlay sends `set warning off` before attacking the named player. Unticked, the attack is still tried, and a refusal ends it unless the box above is ticked.
**Important notes:** It costs evil points like any other first attack. A player whose own evil points are 30 or more can be attacked without the warnings mattering.

### Switch my evil warnings back on after N seconds back on task

**Default:** `60` seconds
**What it does:** How long you must be back at what the fight interrupted, with no new fight, before MudPlay sends `set warning on` for warnings it switched off.

### After a party member drops out, hold room attacks for N seconds

**Default:** `120` seconds
**What it does:** A teleport that splits the party puts everyone back in the same room a moment later, outside the party, where a room attack would hit them. So when a member drops out of your party, your room attacks are held until they are back in it, or this long has passed. A room attack of theirs in that time is not taken as an attack on you either. `0` turns it off.

### Chasing (Chase and attack)

When the player you are fighting leaves, MudPlay steps the way they went at once. Arriving in a room without them, it tracks them if *Track enemies* is on, and otherwise guesses. Seeing them again means attacking again. The chase's own steps go through the walker, so a door on the way is handled as usual.

**Give up after N rooms without seeing them** — default `8`. How many rooms in a row to follow without sight of them before the chase ends and you go back to what you were doing. Seeing them starts the count again.

**Guess the way they went when it wasn't seen** — default on. At a crossroads with a door that just opens, it looks behind the door first (see the next setting). Otherwise it carries on the way they were heading; if that way is shut and the room has just one other way out, it takes that. It never guesses back the way you came, and never through a door that needs a key, picking or strength, or an exit that takes a command or a search. Off, or with no guess to make, the chase waits where it stands.

**Behind a door, look N rooms in, then come back** — default `3`. How far to go in behind a door at a crossroads. With nothing found it walks back to the crossroads and tries the other way; the walk back is not counted against the rooms-without-sight limit.

**With no way to follow, wait N seconds** — default `20`. How long to stand in case they come back into sight before the chase ends.

**Track enemies every N seconds** — default off, `60` seconds. Out of sight in a chase, sends `track <player>` on arriving in each room and follows the answer, which outranks a guess; with no answer within three seconds, or a failed track, it falls back to the guess. While waiting with no way to follow, the track is sent again this often. It needs the Tracking skill.

### PvP Spells 1 and 2

**Default:** blank
**What it does:** Spells to use on a player in a fight. What a spell does here depends on what kind it is:

- **A between-round spell** (one cast alongside your attack, like a debuff) is cast at the start of the fight and again each time its duration runs out: a spell that lasts five rounds is cast every five rounds. One with no duration is cast once. Your attack is sent again after each cast.
- **A combat spell** (one that takes the round) becomes your attack, every round, for as long as its conditions hold.
- **A spell that only takes monsters** (the charm family) is not used, and the program log says so.

Each row has **Max casts** (casts of a between-round spell, or rounds on a combat spell, in one fight; blank is no limit) and **Min mana per cast** (not used below it; a percentage or a mana figure, the same way the Combat tab's spell slots are set).

**With no combat spell here**, or when its conditions aren't met, you attack with your combat profile: its normal attack spell, then its alternate, each while your mana meets its own floor, then your normal attack command. Anything you can use on a monster can be used on a player, except a spell that only takes monsters; a room spell can't be aimed at one.

---

## Cash + Items

Settings → Cash + Items.

### Per-currency policy (Copper / Silver / Gold / Platinum / Runic)

**Default:** Copper = `Ignore`; everything else = `Collect`
**Available options:** `Collect`, `Ignore`, `Discard`
**What it does:** What MudPlay does automatically whenever it sees each coin type on the ground or as loot. `Collect` picks it up. `Ignore` leaves it alone. `Discard` means if you're already holding any of that coin, MudPlay drops it (it won't pick new piles up, but it'll shed what you're carrying). The amount comes from your inventory (what `i` last showed, kept current as you go), one drop at a time; if the game answers "You don't have … to drop!", MudPlay re-reads your inventory with `i` and drops the right amount.
**When you might change it:** Set Copper to `Discard` if you never want to bother carrying near-worthless coin.

### Auto-deposit if wealth exceeds / Auto-deposit if coins exceed

**Default:** both `0` (disabled)
**What it does:** When your total held wealth (converted to a single value) — or, separately, your total raw coin count — passes this number, MudPlay automatically detours to your chosen Bank/Stash and deposits the excess.
**Important notes:** Either threshold tripping is enough to trigger a deposit; both must fall back below their thresholds before it can trigger again. Requires a Bank/Stash to actually be selected below — without one, nothing happens even if the threshold is crossed. If you're carrying items flagged **Auto-sell** and a shop that buys them is close enough to the bank (*On an auto-deposit trip, sell Auto-sell items first…* below), the trip stops there to sell first and banks the proceeds with the rest (see *Sell detours* under the item override editor).

### Bank

**Default:** empty (auto-deposit disabled)
**What it does:** Picks the destination for the auto-deposit trips above — either an in-game bank, or one of your own map-marked stash rooms. Banks receive a deposit command; stash rooms get individual "hide" commands for each coin type.

### Minimum cash to keep on hand (deposit)

**Default:** `0`
**What it does:** The minimum cash to leave in your pocket after an **auto-deposit** — an amount plus a denomination, so you can type `1` and pick **Runic** to always keep 1 runic (1,000,000 copper) on hand. The deposit sends everything above this floor. `0` deposits everything.
**Auto-train keeps it too.** It won't spend below this floor to pay for training: it counts only what's above it, and withdraws the rest from your bank.
Stashing isn't affected; it's governed by the coin-type filter below.

### Only stash coin up to (stash)

**Default:** Everything (stash every denomination)
**What it does:** A dropdown that caps which coins a **stash** offloads. **Nothing** stashes no coin at all — a stash then only puts away your flagged items. **Everything** stashes all of your coin. In between, pick a denomination and a stash hides coins *up to* it and keeps the higher coins in your pocket — e.g. "Gold" hides copper / silver / gold and keeps platinum / runic. It's the stash-side counterpart to the deposit keep-on-hand floor: use it to shed bulky low-value coin as you pass a stash room while keeping your compact high-value coin. Applies to **stashing only**.

### Enable stashing as a follower

**Default:** off
**What it does:** When you're a **party follower** (in a party, not leading), lets you stash currency as the leader drags you through your marked stash rooms. Normally a follower's own movement is held by the leader's drag, so the usual "stash while looping through" trigger never fires for them; this opts their pass-through back in. Marking stash rooms and the coin-type filter above work the same as when you're solo.

### Stash transfers: party members carry a share too

**Default:** off
**What it does:** When you run a stash → bank transfer (the map's **Transfer Stash to Bank**, or the Stash transfer event action) as the **party leader**, has the other members carry coin too. After you have taken your own load at the stash, each member is telepathed `@get-stash` (their client searches and takes coin up to their own coin weight limits); at the bank each is telepathed `@deposit-all`. MudPlay moves on as soon as every member has replied, or after 12 seconds.
**Important notes:** Members must be running a MudPlay version that knows `@get-stash` and allow you to run commands on them (their Players entry for you). MudPlay searches the stash again afterwards and counts what is left, so coin nobody took is carried on a later trip. `@deposit-all` banks each member down to *their own* keep-on-hand floor, into their own account. It does nothing when you are solo or a follower.

### No combat during an auto-sell detour / an auto-deposit trip

**Default:** both Off
**What it does:** Turns **Auto-Combat** off while a loop or Auto-Lair is out on a detour, and back on when it's done. A detour is turning aside to sell an item (an item's *Make detours to sell it*), or auto-depositing at the bank or an off-route stash room.

- **From a loop:** combat stays on while you're still among the loop's rooms, since a trip can start mid-loop. It goes off once the detour leaves them, and back on when you're back among them or the detour ends.
- **From Auto-Lair:** there's no fixed area, so it's off for the whole trip.
- **It flips the real toolbar toggle,** so everything Auto-Combat off already does applies. For example, a rest that triggers on the way still clears the room first.
- **Your own changes win.** It only turns back on a toggle it turned off, so if you change Auto-Combat yourself during the trip, that stays.

The program log notes each flip, and the bug report shows whether a detour is holding combat off.

### On an auto-deposit trip, sell Auto-sell items first at a shop within this many steps of the bank

**Default:** 25
**What it does:** When an auto-deposit trip to a **bank** comes due while you carry anything flagged **Auto-sell**, and a shop that buys it is this many steps or fewer from the bank, the trip goes to the shop first, sells, and then goes on to the bank if a deposit is still due. It counts every Auto-sell item, including ones under their detour count or with *Make detours to sell it* unticked.
**Important notes:** **0** never adds the stop. Raise it if your shop is further from your bank; lower it to keep bank trips short. A stash-room destination never adds a shop stop.

The pickup rules come in three groups on the tab. **Collection Rules: Coins and Items** holds the one switch both share. **Coin Collection Rules** limit how much weight picking up *coin* may bring you to. **Item Collection Rules** set the same kind of limit for ground *items*, separately, so you can let coin fill you further than items or the other way round.

### Collect after combat finished (Cash and Items)

**Default:** Off
**What it does:** Waits until a room's fight is fully over before picking up ground coin and items (this one switch governs both engines), instead of grabbing them mid-fight.

### Don't collect coin if it makes you Light / Medium / Heavy

**Default:** all Off
**What it does:** Skips picking up a coin if doing so would push your encumbrance into the named bracket. The three are nested by strictness — checking "Light" implies "Medium" and "Heavy" are also refused, since those are looser thresholds.
**When you might change it:** Turn on "Don't make you Medium" if you want to stay light on your feet while exploring or fighting.

### Don't collect coin past 90% encumbrance

**Default:** Off
**What it does:** Lets you pick up coin all the way into Heavy, but stops at 90% of your max carry weight. The spare 10% is there because a debuff such as *frail* can lower your max mid-fight. If you're filled to the brim, that leaves you **too heavy to move** until you drop something or it wears off (see *If leading, wait only*).
**Important notes:** Any bracket gate above already stops lower, so turning one on ticks and locks this box.

### Drop smaller currency to make room for larger Collect-flagged coin

**Default:** Off
**What it does:** When picking up a higher-value coin would push you over an encumbrance limit, this drops just enough lower-value **Collect-flagged** coin you're already carrying to make room, instead of skipping the pickup. It never sacrifices Ignore-flagged coin.

### Don't get item if it makes you Light / Medium / Heavy

**Default:** all Off
**What it does:** Same nested-strictness idea as the coin version above, but applied to picking up ground *items* instead of coin. **Don't get item past 90% encumbrance** is the item twin of *Don't collect coin past 90% encumbrance*.

### Drop coin to make room for Auto-sell items, up to

**Default:** Off, up to Silver
**What it does:** When an item flagged **Auto-collect** and **Auto-sell** is too heavy to pick up under the item limits above (or your carry maximum), this drops just enough coin to fit it and then takes it, instead of leaving it on the ground. Coin goes cheapest first, and never anything dearer than the coin you pick: **Silver** drops copper, then silver, and leaves gold, platinum and runic alone.
**Important notes:** Three coins weigh one unit, so a 40-weight item costs 120 coins. If the coin it's allowed to drop can't free enough room, nothing is dropped and the item stays where it is. Items that aren't flagged Auto-sell never cost you coin. It doesn't check what the item sells for, so keep the limit at a coin you'd happily trade for the things you sell. While *Collect after combat finished* is holding pickups for a fight, the drop waits for the fight to end too. The dropped coin stays on the floor: coin pickup won't take it back unless your coin limits allow more weight than your item limits.

---

## Talk

Settings → Talk.

### Disallow all remote control commands

**Default:** Off
**What it does:** A total kill-switch — with this on, MudPlay silently ignores every `@`-command from anyone on any channel, including from your own party.

### Disallow @party commands (from any party member)

**Default:** Off
**What it does:** Blocks the normal rule that any active party member can send you steering directives (`@party attack`, `@party rest`, etc.) that get relayed to your character. With it on, nothing said as `@party <command>` is relayed, and a party member needs the **Query health/status** permission to get an answer to a bare `@party`. The other party signals (`@wait`, `@ok`, `@comeback`, `@share`) are not affected. See **@party** under *Remote @-commands*.
**When you might change it:** If you're technically partied but want this character to act independently without being steered.

### Disallow @commands from telepaths / pages, gangpaths, or say (local)

**Default:** all Off
**What it does:** Three separate switches to drop `@`-commands arriving via each specific channel (telepaths and pages, guild/gang chat, local room speech). These are the only three channels MudPlay listens for `@`-commands on at all.

### Warn sender on invalid / denied remote command

**Default:** On
**What it does:** The master gate for replies to a refused `@`-command. When on, most refusals send a reply back (a specific reason when there is one, otherwise the generic message below); when off, refusals are silent. Some things are never answered, whatever this is set to: an `@` word that is no command at all, the hard-blocked ones (anything with `reroll` in it, and `@party set suicide`), and every command while the master switch (Auto-All) is off. Which replies of each command count as refusals is in its topic under *Remote @-commands*.

### Failure message

**Default:** `"command invalid or not allowed"`
**What it does:** The generic text sent back for a refused command that has no more specific reason (most often: the sender lacks the permission), when a reply is sent at all (see above). Left empty, no generic refusal is sent.

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

Under the toggle, the **Custom emotes** area lists **every** emote — the built-in ones and your own — with a filter box. This library belongs to the **BBS**: every character on that BBS shares it, and another BBS has its own. (It used to be one library for every BBS; each BBS took a copy of it the first time it was loaded under this version.) It is not part of any one character's profile, and — like the rest of this tab — edits only take effect when you press **Apply / OK**; **Cancel** discards them.

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
**Available options:** Font — JetBrains Mono, IBM Plex Sans, MX437 IBM VGA, then **every font installed on your system** (proportional, symbol and non-Latin fonts included — chat rows are plain wrapped text, not the terminal's fixed grid, and a letter the font lacks is drawn from another font); Size — 8–32pt, the same list as the terminal font. Sizes are in points on the same scale as the terminal, so 16 here matches 16 there.
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
**It checks it can pay first.** Before walking anywhere, MudPlay prices the whole run and compares it to the coin you're carrying:
- **The whole run is priced**, including the second trainer when your banked levels span two level bands, since each charges its own markup.
- **The purse is confirmed before it travels.** MudPlay tracks your coin from each inventory read plus what you pick up. It only sends `i` when that figure says the run can go, so it checks against what you really hold before walking. While you're clearly short, it doesn't keep re-reading.
- **Your bank is checked once.** If your purse can't cover it and your bank balance hasn't been read this session, MudPlay sends `bank` before deciding. `bank` works from any room, but typed inside a bank it shows that bank alone, so a reading taken there doesn't count as the full list and MudPlay asks again once you're outside one. After that, your deposits and withdrawals keep the balance current, so it doesn't ask again. A character that has never used a bank gets no reply, which simply means there's nothing to withdraw.
- **Keep-on-hand is left alone.** Coin up to your **Minimum cash to keep on hand** (Settings → Cash) doesn't count toward the bill.
- **If the trainer still refuses for money**, MudPlay re-reads the purse and fetches the difference from your bank. It does that once per run. If it's refused again, the run stops and stays armed.

If you're short it collects the difference first: your stash rooms, then your bank, or a combination, picking the bank branch nearest the trainer rather than nearest you. **Settings → Auto-Trainer → When short on cash** narrows where it may fetch from (a bank only, stashes only, one named bank or stash room), or tells it not to fetch and keep looping instead. At a stash it searches first and reads the pile before taking anything, because anyone may have drawn on it since you hid it. If the pile covers what the train is short, it takes just that, in the largest coins there (so the fewest and lightest), and leaves the rest hidden. If the pile is short, it sends `bank` to check your balances: when the stash and the bank together cover the train it takes the pile and goes on to the bank for the rest; when they don't, it takes **nothing**, leaves the stash hidden, and goes back to your loop until the difference is earned. Drawing on your own stash ignores the per-coin pickup rules on Settings → Cash (your encumbrance limits still apply). While the errand runs, coin on the ground along the way is picked up only until the sum is covered. Pick up enough coin along the way and it abandons the errand and heads straight for the trainer. If everything you can reach still falls short, nothing is walked: it logs how far short you are and roughly how many laps of your loop will close the gap, and stays armed.
**About stashed coin.** MudPlay tracks what it has hidden in each stash room, but any player who searches that room can take it — so a stash is only ever a good guess. The amounts are kept **per realm, shared by all your characters on it**: coin one of them hides is there for the others, and each client picks up the others' hides and pick-ups as they happen. Which rooms count as stash rooms is still set per character. The run confirms by searching when it arrives, and if the room has been emptied it simply re-prices from where it's standing and carries on to the bank.
**Auto-Get Cash is borrowed, not changed.** A collection trip needs cash pickup on to work, so MudPlay switches it on for the duration and puts it back exactly as it found it. Your saved setting is never modified. Auto-stashing is suppressed for the same window, so the trip can't hide the coin it just came to collect.
**Taking over cancels it.** If you stop the walk to the trainer, or start your own walk-to while it's heading there, the training run is cancelled and your loop stays stopped. MudPlay won't restart it and pull you away from wherever you went.
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
**Important notes:** You need a saved CP plan (from the Player Workshop's CP Allocation tab) before this checkbox will actually stay checked — MudPlay reverts it and warns you if you try to enable it with no plan saved. Applying stats isn't level-gated like a level-up is, so "Train Now" applies your CP at whatever class-valid trainer you're standing in — it no longer walks you off to a level-band-matching one (or gives up) just to spend points. The plan is typed from the trained stats and **CP Left** on the `train stats` screen itself, so a buff showing on `stat` doesn't throw it off; the screen must show what was typed before MudPlay saves, and the plan row is cleared only once the game shows the points were spent. Opening the screen yourself is only typed into when there is something the plan can raise. On Stock the game won't open `train stats` while a stat is altered; a trip at the trainer waits for that (**Wait for altered stats at the trainer**, below) instead of sending it (see **CP Allocation → Buffs, curses and the plan**).

### Levels to keep banked

**Default:** `0`
**What it does:** A reserve buffer — Auto-train (and manual training) stops once only this many further trainable levels remain, rather than always training everything available.
**When you might change it:** Set to 2–3 if you like to keep some levels "in the bank" as a cushion.

### Do not train above level

**Default:** `0` (no ceiling)
**What it does:** A hard level cap — Auto-train stops permanently once you reach this level, even if more experience would normally allow further training.
**When you might change it:** Deliberately capping a character for a challenge run or a competitive-play limit.

### Wait for altered stats at the trainer (Stock)

**Default:** `120` seconds (0–600)
**What it does:** Stock won't open `train stats` while a spell or item is altering one of your stats. When a training trip gets to the trainer and its `stat` shows one altered, it waits there this long, reading `stat` again every 20 seconds, and applies your CP plan the moment the stats are back to normal. If they haven't cleared when the time is up, the plan row is left for a later trip and the run carries on. `0` doesn't wait at all.
**When you might change it:** Shorten it (or set 0) if a buff you keep up all the time would only make every trip stand around; lengthen it if your stat buffs run longer than two minutes and you'd rather wait them out.
**Important notes:** Saved per character. Stock realms only — Paradigm lets you train with a stat altered, so nothing waits there and the control is greyed out. The Buff Watchdog isn't paused during the wait, so a stat buff it keeps recasting will outlast any wait. The wait only applies to a trip already at the trainer; **Apply this level** and **Train now** just read `stat` again first.

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

**Seeing where everyone stands.** While you lead with the box on, the Party window shows a line under each member's bars from their last report — their total exp (between their reports, what they reported plus the exp **you've** gained since — party members gain the same from a kill, so it's an estimate), their time to next level at **your** exp/hour, and their state: `4,120,331 xp · TNL 1h 5m · not ready`, `… · ready +2 (12,345c)` (levels this trip trains and the fee), or `… · no party train` — and your own on your row. Members on another client (MegaMUD, or an older MudPlay) can't report, so the leader asks them `@level` instead and shows `1,000 to L23 · TNL 12m · other client`: the line names the level their figure counts toward — their next level, or a later one when their client counts past it and says so — and their own "will level in" estimate stands in until you have an exp/hour rate. A member with no line hasn't answered either. When a member broadcasts **"I can now train to level: N"** (the Auto-Trainer's *Announce level-ups*, on any channel), their line says so — `can train L12 · party train off`, `can train L12 · other client` — until they're seen at that level. It's display only: it shows who could train even though they won't be auto-trained (box off, still short of their own settings, another client). Whether a member comes on a party trip is still their own report. **Following** with the box on, your Party window shows the same lines: your own row is the status you report to the leader, and the leader's and other members' rows fill in from their `@level` / `@exp` replies (ask them yourself), timed at your own exp/hour, since the party shares the kills. Members' reports only ever go to the leader, so a follower sees readings, not their ready state. Each member's TNL counts down between readings the same way the status bar's does. Each member's level also shows with their class (`Level 20 - Druid`) — from their report, or from the last `@level` reading for members who don't report. It is the level they state: an `@exp` reply never changes it, and neither does a "can train" announcement.
**The leader doesn't have to be training.** A high-level leader power-leveling the party still escorts everyone to the trainer and back — the trip goes whenever enough members are ready.
**Money.** Each member reports its purse and its biggest bank deposit. If someone is short, members with coin to spare give it to them before anyone walks (only what they can spare above their own fee and keep-on-hand amount). If the party's spare coin can't cover everyone, the trip first stops at a bank and short members withdraw their own fee; anyone who still can't pay sits the trip out.
**Who isn't waited for:** members with the box off, members on another client, and anyone who doesn't answer. They just follow the leader there and back.
**A member shut out of the trainer's room.** Some trainers' rooms can't be entered during a fight, so a member still fighting outside is left behind and their follow breaks. The train order carries the trainer's room, so that member walks in by itself once the fight is over, trains, and reports. Everyone at a stop trains at once. As soon as the members' train orders are delivered, the leader trains too, then re-invites everyone who set out on the trip (including a member left in an `[Invited]` slot) and waits for each member's `done` before moving on. Every train drops that character from the party, so there's just the one re-form. **A member left on the way** (one who couldn't enter a trainer's room gated by level or class, say, and has no train order of its own) doesn't stop the trip. Nothing does: neither the game's `<name> is no longer following you.` nor a `@comeback` that arrives during it starts a pickup, and a member who asks is told once `I can't yet, I'm on a train trip. I'll come for you when the training is done`. The leader keeps the room each member was left in (the room its own step had just left, or the room they name) and, **when the trip is over, goes back for each of them in turn** like any member left behind, whatever client they are on and however long the trip ran, with its loop or walk put back first and also when it had none running. **Return distance** doesn't limit this fetch: the trip was the leader's, so it goes back however far the trip took it. If a member isn't in the room the leader's own step left them in, the leader doesn't invite into an empty room; it answers `didn't find you where the trip left you — @comeback with your room and I'll come` and goes where their request says. If Auto-All is off when the trip ends, the fetch waits for it to come back on, and is dropped if that takes longer than **If leading, accept @comeback for**. The leader's client also tells every MudPlay member when the trip sets out and when it is over, so theirs hold their own `@comeback` until then. The leader's log names who the trip came back without. On Stock, where the game doesn't tell a leader a follower dropped, the leader knows where such a member is only from their `@comeback`; one sent after the trip is fetched like the rest (even by a leader with nothing running, and from beyond the return distance) for as long as **If leading, accept @comeback for** allows. **The leader's own train run with the party in tow** (the armed auto-train, Train Now, Buy spells) is treated the same way: nobody is fetched in the middle of it, and everyone it left is fetched when it is over. **If you end the trip yourself** (a second Stop, starting another walk or loop over it and answering **No** to *Resume it first?*, Reset States) or its walk is stopped under it, nobody is fetched automatically: the terminal names who was left and where (`[Train trip ended early - left behind: … Not going back for them on its own]`), the log has it too, and their `@comeback` is still accepted and answered by whatever you are doing then.
**Kept quiet on the wire.** A telepath at the wrong moment costs the leader a little exp/hour, so the handshake is the bare minimum: the leader asks each member **once** when they join (after a short pause so the join-time `@version` check can say whether they're on MudPlay at all — members on another client are never asked), and from then on a member only speaks up when something that matters changes (ready, level, levels to train, or switching the box off). A member only ever reports to a leader that asked. If a member that's still waiting hasn't spoken up by **10 minutes past** its projected ready time, the leader sends it one "ready yet?" ask — its own report normally beats that, so it's rarely needed. Members on another client get one `@level` if the join-time check didn't already supply it, and one more 10 minutes past their projected level-up. Asking a member `@level` or `@exp` yourself (by telepath, or `.@level` on say) also refreshes their line. Outside a trip, nothing is sent mid-combat.
**Important notes:** Orders are only taken from your current party leader, and only while your own box is on — nobody can make your character train, give or withdraw otherwise. After a trip, the leader waits a few minutes before deciding again.

### Party options

These shape how the **leader** runs an Auto-train party trip (the level-11 rule applies to everyone).

- **Go once at least N party members are ready to train** — default `2`, range 1–6. The leader counts as one. Members who don't report, or are skipped by the level gap, don't count — and if everyone who does count is ready, the trip goes even when that's fewer than N.
- **Don't wait for anyone more than N levels above the party** — default `5`, `0` = off. A member who isn't ready and is this far above the rest of the party (a power-leveler — the leader included) is never waited for.
- **Leave the level 11 train to a solo trip** — default on. Party trips train no higher than level 10; the step to 11 is a solo effort, so take it on your own.

### When short on cash

**Default:** Stash rooms first, then a bank · any bank · any stash room
**What it does:** Decides where auto-train may fetch the difference when your purse, above keep-on-hand, can't pay for the training.
The bill counts the **tolls and transport fares on the trip** as well as the training fees — the walk on to each trainer and back to where the run left off — priced from wherever it stands, so a withdrawal at a bank covers the tolls from that bank onward too. The trip takes the **shortest route your money allows**: when your purse covers the training but not the tolls, it skips the bank and walks round them; when even the bank can't cover both, it fetches the training fee and walks round the tolls. (Routing round only happens when there's a toll-free way; otherwise the tolls have to be paid.) While it's heading to train, the fees are set aside, so a toll is taken only when you can pay it on top of the training.
- **Fetch the difference from:**
  - **Stash rooms first, then a bank:** the original behavior.
  - **A bank only.**
  - **Stash rooms only.**
  - **Don't fetch - keep looping until I have it:** auto-train stays armed and your loop carries on until your purse covers the training.
- **Bank:** limits withdrawals to one bank room, listed the same way as Settings → Cash's bank picker (`(map/room) Room name - Bank name`).
  - A bank with two branches is listed once per branch. For example, Bank of Godfrey appears for both Silvermere and Khazarad, so you pick which one to walk to. The branches share one balance.
  - **(Any bank)** uses whichever bank holds enough.
  - Only a bank you've deposited at has a balance to draw on; see *Auto-train*.
- **Stash:** limits collection to one of your flagged stash rooms, e.g. the one where your money sits. It's listed as `(map/room) Room name - Stash`. **(Any stash room)** uses any of them.
**Important notes:** A bank or stash the setting excludes is never planned, so the run reads as short and keeps looping. With banks excluded, MudPlay doesn't send `bank` to check balances.

### Auto-obtain spells from shops

**Default:** off · every listed spell wanted
**Where:** Settings → Auto-Trainer → **Spells from shops** tab (beside **Trainers**).
**What it does:** When a solo Auto-train or **Train Now** trip has trained its levels, it walks on to the shops that sell the scrolls for spells you can now learn, buys them, reads them, and only then heads back to your loop. With this off you can still make the same shop trip whenever you like with **Buy spells** on the Player Workshop's CP Allocation tab.
- **Which spells:** every spell your class can learn from a scroll that some shop restocks, that your new level allows, and that isn't in your spellbook yet. That includes spells from earlier levels you never picked up, because many scrolls restock on a small chance and a shop is often out of them. A scroll a shop only has when another player sold it there is never planned for.
- **The list:** one row per spell, with the level it unlocks at, the scroll, the shops that sell it and the cheapest price at your Charm. Untick **Get?** on any spell the trip should never go after, for instance one sold only somewhere you don't want to walk alone. **Hide learned** (a view filter) leaves out what you already know.
- **Money:** the trip's funding fetches the scroll money along with the training fee, and counts the tolls out to the shops. If it can't cover both, it funds the training alone and trains anyway; the shop trip then buys what your purse above keep-on-hand stretches to, lowest level first, and logs what it left out. On the way to a shop a toll is taken only when the scrolls can still be paid for after it.
- **At the shop:** it reads the shop's `list` first. A scroll that is out of stock isn't tried, and neither is one the shop marks **(You can't use)** (a spell your character can never learn) or **(Too powerful)** (one above your level); the program log says which were left and why.
- **Which shop:** one already on the trip, otherwise the one that adds the fewest steps between the trainer and where your loop resumes. A shop it can't route to and back from is skipped: an area behind a level gate you can't pass yet (Port Blackwater below level 25), a room you've marked Avoid.
- **At the shop:** it sends `list` and buys only what's in stock, then `read <scroll>` for each. A scroll already in your pack is read without buying another. A read that isn't answered leaves the scroll in your pack, and the next trip tries it again.
- **Before it leaves the trainer** it sends `sp`, so the plan is checked against your actual spellbook.
**Important notes:** Solo trips only: a party training trip and a remote `@train` never go shopping. Stopping the walk yourself ends the trip and leaves the loop stopped, the same as stopping a walk to the trainer. Every step is in the Program Log under `AutoTrain`.

### Discovered trainers table

**Default:** every discovered trainer allowed
**What it does:** A list of the trainers in your loaded game data that apply to you — the universal Training Room plus your own class's trainer — each with a checkbox controlling whether MudPlay is allowed to route to it. Uncheck a specific trainer to exclude it — useful if a trainer sits somewhere dangerous or inconvenient. A **Usable at my level** filter above the table narrows it to trainers whose level range covers your current level.
**Which one gets walked to:** the nearest allowed trainer that serves your level, by steps from where you stand. When two are the same distance, the cheaper one (lower markup) wins. Each run logs its choice with every candidate's step count, or why it was skipped (`disabled`, `no path`), so the Program Log shows why a trainer was passed over. An unchecked row here shows up as `disabled`. Copying a profile copies this list too.

---

## Statline

Settings → Statline, modeled on MegaMUD's Statline dialog. Statline is **server-owned** — this tab builds a text string that gets sent to the game with a `set statline` command, and MudPlay's own screen parser is generated from that same string, so the two stay in sync. The tab has three parts: a read-only **Current Statline** preview (how the prompt will look, using your live numbers when connected or sample numbers otherwise), the editable **Statline Command** field, and a **Customize** row for building the string from wildcards.

### Statline Command

**Default:** `full` (a sensible class-appropriate default format)
**Available options:** `full` (class default), a hand-built wildcard string, or `full custom <wildcards>`.
**What it does:** Controls the exact text/format your character's status-line prompt uses in the game, which MudPlay then reads back to track your live HP/mana/etc.
**How the options work:** Pick tokens from the **Customize** dropdown (current/max HP, current/max mana, resting flag, wealth, experience, color codes, and more) and click **Add** to build a custom string. **Default** resets back to `full`.
**Important notes:** When you change this and click OK/Apply while connected, MudPlay sends the updated `set statline` command to the game immediately. On each connect it also checks that the game's live prompt matches your saved statline and re-sends the command if it doesn't (self-correcting, up to 3 retries) — so a server reset that lost your custom statline fixes itself without you having to do anything.

**What a custom statline needs.** MudPlay's automation reads three things off the prompt: your **current HP** (`%h`), your **current mana** (`%m` — any label or none, since your `stat` screen tells MudPlay whether it's mana or kai; skip it only if your class has no mana), and the **resting flag** (`%r`). Labels are up to you (`HP=`, `HITS:`, `MANA=` or nothing), and spacing is forgiving, but numbers can't touch: `%h%H%m%M` prints `91913242`, which can't be split back into four numbers, so put a space, `/` or letter between them. Leave something out, or run numbers together, and the Statline tab lists each problem in red, and pressing OK or Apply asks "Save anyway?" first; **Go back** returns to the tab with nothing saved. Max HP and max mana (`%H`, `%M`) are optional: MudPlay takes those from your `stat` screen.

### When the game's prompt doesn't match

MudPlay reads your HP and mana from the prompt. If the game's prompt doesn't match Settings → Statline, MudPlay can't read them, and every HP-based automation (resting, healing, door bashing, running) acts as if you were at 0 HP. This applies to **Default** as well as a custom statline — some servers, or a statline set by hand in the game, print a shape Default doesn't cover (for example `[HP=145/145][MA=46/46]:`).

- **How it's detected:** once you're in the game (after the first room display), MudPlay checks the prompt each time a command goes out — whatever text the game left on the cursor's line, even plain words with no numbers — and also any statline-shaped text (bracketed, with numbers, ending in `:`) that arrives at the start of a line. If it doesn't match Settings → Statline for **3 prompts in a row**, it's a mismatch. A single matching prompt resets the count, and BBS menus before you enter the game never count. It keeps watching all session, so a statline changed in the game mid-session is caught too.
- **Automatic reset:** MudPlay then sends `set statline <your Settings → Statline command>` — `set statline full` when you're on Default — up to 3 times, a couple of seconds apart. The first prompt that matches ends it.
- **The warning:** if the resets don't take, MudPlay prints a red terminal notice showing the game's prompt, and a red **STATLINE MISMATCH** warning appears in the status bar. Hover it for the details; click it to open Settings → Statline. It clears as soon as a prompt matches.
- **What to do:** set Settings → Statline to match what the game prints (use **Customize** to build it — e.g. `[HP=%h/%H][MA=%m/%M]:` for the prompt above), or type `set statline full` in the game to go back to the class default. If you use a custom statline the game won't take, switch Settings → Statline back to Default.
- **In a bug report:** the **Statline** section shows your Settings → Statline command, whether the latest prompt matched, the last prompt that didn't, and where the automatic reset got to.

---

## Teleports

Settings → Teleports. One list, saved for the loaded character: which of the game's teleports a walk the client starts by itself may use.

### Allow automatic walks to use the following teleports

**Default:** none ticked
**What it does:** Decides which teleports a walk the client starts by itself may use. A teleport here is anything the game moves you with by command: a vortex or portal, but also a hatch onto a roof, a book you read, a panel you push. To the route planner each is a single step, so it is nearly always on the shortest route, and nothing checks where it lands you. An automatic walk uses only the ones you tick. If the trip needs one that isn't ticked, the walk stops and says it was refused as an automatic walk, naming the line here that would open a route (or, when no single line would, every line the shortest way needs), so you can tick it or make the trip yourself.

The walks it covers are the ones nobody is there to approve: bank and sell trips, training and spell-buying trips, Auto-Lair's walks between lairs, events, and a walk-to or loop another player starts for you with a remote command.

**The list comes from your game data.** Each line is one teleport spot: the room it's in and the room it lands in, each with its **map/room number** so you can find a spot you don't know by name on the map. **→** is a one-way teleport; **⇄** is one that also runs straight back, and both directions are ticked together. Under it is what is typed there and what the spot leads to: the area at its far end and **how many rooms there can be reached no other way**. The spots with the most rooms behind them come first, so the ways into the big regions and the hubs (the Black Wasteland, the Negative Power Plane) are at the top. A line marked *a shortcut* joins two places you could also walk between.

The **filter** box narrows the list to lines holding what you type: a room name, a map/room number, or a command such as `go hatch`. **Allow all** and **Allow none** tick or clear the lines the filter is showing.

**Important notes:** Walks **you** start aren't affected: they ask you on the route cards (**Walk it** or **Teleport**) whenever the shortest way there teleports and it could also be walked, and any other card whose route teleports names the teleport. That holds for the whole walk: a side trip it makes to fetch an item its route needs (to an NPC who hands it over, or a shop) isn't held to this list either. It goes on foot when it can and takes a teleport only when it can't, and the leg from there on to where you were going is the route you picked again. The same goes for the walk picking up after a sell trip or a flee, and for a Shortcut card's trip to the shortcut item's source. That covers a walk-to, a **loop you start** from off the loop (getting to it is a walk-to like any other, with every route card a walk-to shows; the loop begins when you arrive), and **Recover Now** on a death. Only that first walk to the loop is yours: once the loop is reached, a walk back to it after a bank or sell trip or a flee is automatic and uses this list. So is a loop started by an event or by another player's remote command, and the walk back to your loop after an event. A sailing isn't a teleport and is always allowed. Saved for this character; a change applies from the next automatic walk on, never to one already under way.

## Periodic Damage Room Spells

Settings → Periodic Damage Room Spells. One list, saved for the loaded character: the room spells of your game data that do damage, and for each whether a room with it bars resting.

A *room spell* is a spell a room casts by itself on whoever stands in it, about every six seconds: the volcano's heat, a swamp's poison, a river with no boat. Damage from it breaks a rest or a meditation. What MudPlay does in a barred room is under **Health: rest, heal, flee** → *No resting in a room that hurts*: no rest or meditate is started, healing goes on, a walk or loop carries on, and the rest is taken in the next room that isn't barred.

**With no profile loaded, or no game data, the tab is empty** and says which of the two it is waiting for. The list is read again whenever the game data set changes; ticks you had changed and not yet saved are kept for the spells the new set still has, and the program log names any that had to be dropped. Loading another character reloads the tab for that character, as on every Settings tab, and the log names the unsaved ticks that went with the old one.

### Wear the item that negates a room's spell before stepping in

**Default:** ticked
**What it does:** An item that negates a room's spell (the *Countered by* column marks these **(worn)**: the phoenix feather and magma amulet, the swamp boots, the fish-helm, the cold-weather pieces) works only while it is worn. Ticked, MudPlay puts a carried one on when you come next to such a room, keeps it on inside and while you are still next to one, and puts the usual piece back one room clear. The full rules (which item, sneaking, what goes back, a wear the game refuses) are under **Equipment Manager — gear sets** → *A counter that has to be worn is put on for you*. Crystal ward is not covered.

**Why you might change it:** untick it if you manage that slot yourself, with a gear set or a Location rule that already wears the item where it is needed.

**Important notes:** Shown whenever a character is loaded, with or without game data. Unticked, nothing is put on, and routes stop counting a negating item you carry but aren't wearing: the route cards then offer those rooms as uncountered. An item put on before you unticked it is still put back as usual. With the master switch off nothing is put on or put back; when it comes back on, the room you stand in is settled at once. Saved for this character; in effect at once, the room you stand in included.

### Bars resting

**Default:** ticked for a spell that damages on **every tick** or **on a timer**; clear for one that damages only **on a roll** or **on a condition**
**What it does:** Ticked, no rest or meditate is started in a room with that spell, unless its counter is in effect (see *Countered by*). Clear, the room is rested in like any other, whatever its spell does.

**Why you might change it:** With a spell ticked, a character that cannot heal itself will not recover in those rooms at all: it neither rests nor heals there, and only gets its rest in the next room that isn't barred. Unticking the spell lets it try to rest there. The frozen north's *freezing cold* (1 to 4 a tick, on the most rooms of any spell) is the usual one to untick; a loop that lies wholly inside such rooms needs either heals, the counter, or the spell unticked. Tick a roll spell if its hits keep breaking your rests.

**Important notes:** Only the spells you set away from their default are saved, so a later game-data update that reads a spell differently reaches every spell you never touched. A choice saved for a spell the loaded game data doesn't have (another realm's) is kept. A change is in effect from the next rest decision; the program log names each spell whose rule changed. **Reset to defaults** puts every box back.

### The columns

- **Record** is the spell's number in the Spells table (Game Data Browser → Spells), and **Spell** its name there.
- **Damage** is what one cast does, from the spell's record: a range (`30–60`), a single figure, or *not in the data* when the game data doesn't hold it. Paradigm's desert is a special case: its data doesn't say what burns you, and the two desert rows show *desert damage* (5–20), which is cast on a character without the waterskin buff. *More with level* marks damage the room casts from a text block (the ice fall of the ice caverns): the game rolls that at your own level. A room's own spell does its set damage whatever your level, so chaos storm reads 30–35 and the ocean and murky drowning 5–15. Damage that only comes when a timer runs out is not in this figure: it is said under *Comes*.
- **Comes** is how the damage arrives:
  - **every tick**: on every cast of the room's spell. **every tick; then …** adds what a timer the spell starts ends in: *freezing water* does its 1 to 4 a tick, and its held breath ends in *drowning* after 25 rounds and *drowned to death* 5 rounds after that.
  - **on a timer: …**: no cast does damage, but the first one starts a timer that ends in it, with the spell at each stage, what it does and how many rounds after that first cast (*holding breath*: drowning after 25 rounds, drowned to death after 30). The room's later casts don't start the timer over: it keeps running for as long as you stay. It starts ticked because the timer ends in death.
  - **on a roll: N% of ticks**: only when a roll comes up, with the share of casts that do damage.
  - **on a failed skill test**: only when you fail a skill check (the Great Pyramid's traps); there is no fixed share.
  - **only if …**: only for a character or room that meets a condition, named in a few words (*level 19 or under*, *no monster in the room*, *holding* an item). A condition MudPlay has no wording for is shown as the game data writes it, in `code`. When there are many alternatives the first three are named and the rest counted.
- **Countered by** is what makes the spell harmless, and how it has to be had: **(worn)** for an item that negates the spell, which works only on the body; **(held)** for an item the room checks you have, in the pack or worn; **the buff from … (used)** for a buff you raise by using an item. *Nothing in the game data* means the data has no counter for it.
- **Rooms** is how many rooms carry the spell.

### The rooms of a spell

Click a spell to list its rooms under the table. A spell can sit on a couple of thousand rooms, so they are grouped: the left list has one line per **map and room name** with its count, the largest first; pick a line and the right list shows that group's rooms as **map/room** links. **Click a room to show it on the map**: the Navigation window opens (or comes to the front), centres on the room, selects it and shows its details, the same as a room link in the Game Data Browser.

## Other

Settings → Other. A catch-all tab for safety thresholds and walker (auto-pathing) behavior. Most fields here are character-tier; the two solver toggles and the player-database cleanup setting are Global-tier (install-wide).

### Block @suicide commands when lives ≤

**Default:** `5`
**Available options:** 0–9
**What it does:** Refuses to let a remote `@suicide` command through if your remaining lives are at or below this number — protects a near-dead character from a careless or malicious remote kill command. `0` disables the protection entirely. (If MudPlay can't read your current life count, it blocks the command regardless of this threshold.)

### Utilize self or party members to disarm traps

**Default:** On
**What it does:** When a walk-to, a loop or an Auto-Lair run crosses a trapped exit, MudPlay tries to disarm it before stepping through, using your own skill or, if you don't have it, a party member who does. Turning this off walks straight through and takes any trap damage. See *How Traps and disarming work* for the odds.

**Handing a trap to a party member.** When you can't disarm and a party member's class or race can, MudPlay says `@trap <direction>` to the room and waits:

1. A member's MudPlay that takes the job answers at once with `{Attempting to disarm trap <dir>.}`. That is how yours knows someone has it.
2. That member disarms. If the trap goes off and drops them under their own *rest if below*, they rest first and then try again.
3. They report the result: `{Trap to the <dir> disarmed.}` (or *already disarmed*, or *No trap to the <dir> to disarm.*) and you step through; `{Couldn't disarm the trap to the <dir> (N attempts).}` and your walk stops rather than walking into it.

With more than one able member, the first to report the way clear moves you on, and one giving up doesn't stop the walk while another is still working. Answers are read from say and from telepath (a sneaking member answers by telepath so it keeps its sneak).

**If nobody accepts within 10 seconds**, nobody is going to: the able member isn't running MudPlay, hasn't given you the **Execute commands** grant on their Players tab, or isn't in the room. MudPlay then walks through the trap, the same as when nobody in the party can disarm, and says so in the program log.

### @trap max disarms

**Default:** 5
**What it does:** Caps how many times MudPlay tries to disarm a trap before giving up, whether for your own walk or a remote `@trap` command.
- **A failed disarm can set the trap off,** so each retry risks its damage again.
- **When the trap goes off (both realms),** each trap prints its own line, e.g. `You try to disarm the trap, but instead trigger it!` or `You trigger the trap, and a large spear shoots out!`. MudPlay knows them all and retries; after the cap, a walk stops at that exit rather than walking into the trap.
- **Paradigm:** `Your command had no effect.` means there's no trap that way, and the walk carries on.
- **Paradigm:** `The trap is already disarmed.` means the trap is down; the walk crosses straight away.
- **Stock:** `You failed to disarm any trap to the <dir>.` means either a failed disarm or no trap there; the game doesn't say which. MudPlay retries up to the cap, and if it's still getting that answer it takes the exit as clear and walks on.
- **Recently disarmed:** a trap you disarmed stays down until the game re-arms it — 5 minutes on Stock, 2 on Paradigm. Coming back to that exit sooner, MudPlay crosses without disarming again, which saves the command and keeps your sneak. Once the time is up it disarms again. Only your own disarms count: a trap that was already down when you got there is disarmed again next time.
- **No searching:** MudPlay never searches for a trap first. `disarm trap <dir>` works on the trap directly, and your game data already says which exits are trapped.

### Stop auto-sneaking while a carried item leaves under … % chance to sneak

**Default:** `15`
**Available options:** 0–95
**What it does:** A few items cut your Stealth just by being in your pack (a log raft is -125; see *Something in your pack that kills your Stealth* under Auto-Sneak). MudPlay estimates your chance to sneak from your Stealth with that penalty applied, less the penalty for a heavy load. While the estimate is **under this figure**, Auto-Sneak sends no `sn`, says so once on the terminal, and your walk goes on unsneaked until the item is gone.
- **Why not just try anyway:** every sneaked move re-rolls against the same chance, so at a few percent a sneak that does take is lost a room later, after a long run of resent `sn`.
- **`0`** never stands down (the old behaviour: keep resending `sn`). **`1`** stands down only when a sneak can't take at all.
- It only applies while one of those items is carried. A character whose Stealth is simply low is never stood down.

### Door max pick

**Default:** 10
**What it does:** Caps how many times the walker retries **picking** a locked door before giving up (picking is probabilistic — it can fail even when your skill meets the requirement). **Bashing has no cap**: bashing a door drains HP, so instead of a fixed retry count the walker bashes a genuinely bashable door until it opens, pausing to **rest to your rest-max** whenever HP dips to your Health-tab rest trigger, then resuming. A door that isn't actually bashable (strength/requirement too high) still falls through to picking or a key rather than bashing forever. When the picks run out and nothing else opens the door, the walk goes round it if it can (see **Doors** under *The map and obstacles*).

### Pick locks instead of bashing

**Default:** Off (bash first)
**What it does:** When a door supports both, this decides which the walker tries first. Picking is quieter and keeps you stealthed; bashing is louder but faster/more reliable for a strong character.

### Search rooms if item needed

**Default:** Off
**What it does:** If your route crosses an exit that needs an item you don't have (a boat, a rope, a ticket), turning this on makes MudPlay search every room along the way hunting for it — even if the separate Auto-Search master toggle elsewhere is off.

### Hide items when discarding

**Default:** Off (plain drop)
**What it does:** When an item is discarded from your pack, this makes the discard use `hide` instead of `drop` so the item lands concealed rather than in plain view on the ground. It covers auto-discard's offloads and the **Drop** / **Drop All** buttons on the Workshop's **Chest Offload** tab. These hides are kept out of the Transaction history (a discard isn't a stash). It does not change the Action menu's **Drop** entries or `@drop-all`, which always drop — use the **Hide** entries / `@hide-all` beside them to hide — nor Roomba, which sorts items where they can be seen.
**When a room is full:** a Stock room holds only so many hidden items, and refuses one more with `There is no room to hide <item> here.` (Paradigm rooms have no limit). The item then stays in your pack and MudPlay sends the hide again when you arrive in a different room, once per room, until it lands — it is never dropped instead. The waiting hide is **called off** if you sell the item, drop it, hide it yourself, take it off the Chest Offload list or start selling it from there. It **never touches the copies you weren't discarding**: if you carried two of your own beside the one being hidden, and that one is given away or lost while it waits, nothing is hidden in its place; auto-discard's own retries also leave the item's keep amount. It is **given up** if you untick this setting, die or switch character, and an auto-discard item's waiting hide is let go if you untick its auto-discard flag. A disconnect keeps it, and it waits for the first inventory read after you're back. It **waits** without being sent while MudPlay isn't sure which room you're in, during a Roomba sweep, while the game is at a menu or prompt, and — on Stock — while you're blind or the room is dark (the game hides nothing then); auto-discard's own items also wait while **Auto Get Items** is off, unless you press Drop for that item on the Chest Offload tab, which takes them over. A Chest Offload **Drop** pressed where a hide can't be answered is held the same way instead of sent. The program log says when a hide is held, each time it is tried again, what is keeping it back, when it lands and when it is called off, and the bug report lists the hides still waiting, who sent each and why it waits. A plain drop the room has no room for (`There is no room to drop <item> here.`) isn't repeated in that room either, and auto-discard tries it again in the next. After a death, a reconnect or a change of character nothing is discarded until your inventory has been read again.

### Auto-request @comeback when left behind

**Default:** On
**What it does:** As a follower, if your leader moves on without you, MudPlay telepaths `@comeback <map/room>` to the leader on your behalf, once, so their client comes back for you. When it isn't sure which room you're in it sends a bare `@comeback`, and the leader backtracks the way they came.

**When it asks:**

- You were held, stunned, knocked down or too heavy to move when the leader stepped on. Your client's `@wait` for that goes first, then the `@comeback`.
- An exit the leader got through turned you away: an item or key you don't carry, a toll you can't pay, a level, class, race or alignment gate, a room you aren't permitted in. It is known by its shape, not the exit's wording: the game says you are following your leader that way, no room arrives, and your follow ends.
- The leader was seen leaving the room and you weren't taken along.
- Your own `par` shows the leader as `[Invited]`: you are still on the party's list but following nobody.

**When it doesn't:**

- The leader uninvited you or disbanded the party.
- You left: you typed `leave` or a bare `follow` (or a short form the game takes: `le`, `lea`, `leav`, `fo`, `fol`), or you moved by yourself (a keystroke, a macro, a relayed command). A move the game refused doesn't count as leaving, and neither does `join <name>` or `follow <name>`, which is how you rejoin.
- You died or dropped, or the leader did.
- A teleport split the party: the leader's own teleport, or an exit that casts a spell on whoever walks it. These end your follow with the same line an uninvite does, and nothing before it shows you were left behind.
- You aren't following anyone (solo, or leading).
- This setting is off.

**When it waits:** with Auto-All off, at the board's menu or on a trainer screen the request can't be sent, and it goes out when that clears, as long as you still aren't following and no more than the Party tab's *If leading, accept @comeback for* time has passed. During your leader's **party train trip** it waits for the trip to end: the leader's client tells yours when the trip sets out and when it is over. The leader goes back for everyone the trip left once the training is done, whether they asked or not (unless its player ended the trip by hand), so your request then is a second word, not the only one. If your client is never told the trip is over, it sends the request anyway 15 minutes after it was told the trip had set out. After a disconnect the reconnect's own `@comeback` (see **Reconnecting**) is the only one sent.

**Rejoining.** A follower an exit turned away is out of the party and has to be invited again. When the leader you asked comes back and invites you, your client joins, whether or not **Join party if invited** is ticked for that player: having asked is the consent. That holds until you are following again, the leader declines, or ten minutes pass. With this setting off no request was sent, and the invite is taken only if **Join party if invited** is ticked for that leader. With Auto-All off no invite is taken at all.

**A leader on a loop may refuse.** If the leader's loop itself goes through the exit that turned you away, coming back would only leave you there again next lap, so its client answers `I can't, my loop goes through an exit you can't pass` and carries on. A leader on a walk-to comes back for you once (and ends its walk if the same exit turns you away again), and so does a looping leader when the exit wasn't what stopped you (you were held or too heavy on that step) or wasn't part of its loop.

**A leader who comes later.** A leader on a train trip answers `I can't yet, I'm on a train trip. I'll come for you when the training is done`, and comes when the trip ends by itself; if the leader's player ends the trip by hand, nobody is fetched automatically and regrouping is up to the two of you. A leader on Auto-Lair that an exit separated you from answers `I can't yet, an exit on my way turned you away. I'll invite you when my next pass finds you`. Neither is a refusal: your client keeps waiting, for up to 15 minutes.

**One request per split.** After asking, your client asks nothing more until you are following again. The leader's client answers by telepath, and the answer is written to the program log. A leader with no walk, loop or Auto-Lair running answers `I can't I'm idle` and stays put. A leader who is too far, has a full party or can't find a path says so and sends `@forget`. Nothing here ever walks your own character to the leader.

**When nobody is coming.** On Paradigm the game ends your follow with its own line. On Stock an exit that turns you away ends it without a word, so your client would go on believing it follows, holding its own walk, loop and Auto-Lair for a leader who isn't coming. So once the leader refuses your request (idle, its loop, too far, party full, no path), or sets out and gives up, or nobody answers and nobody comes within the Party tab's *If leading, accept @comeback for* time, **your client stops counting itself as a follower**: the party is cleared as if the game had said you no longer follow, your own engines are free again, and flee and hang-up rules are a solo character's. The program log says why. `I can't yet` is not a refusal, and neither is any answer that says the leader is on its way: then your client waits 15 minutes before giving up. If the leader does come and invites you later, the invite is handled as usual: taken on **Join party if invited** after a refusal, and taken as one you asked for otherwise (see **Rejoining**).

The program log (source `Comeback`) says what showed you were left behind each time a request goes out, and why each time one is held back. The bug report's Party section carries the last such incident and the leader's answer.

### Only auto-invite while navigation is running

**Default:** Off
**What it does:** Players you've flagged **Invite to party if seen** (Game Data → Players) are auto-invited only while navigation is running: a walk, loop or Auto-Lair (running or paused), or an auto-deposit or train trip. Standing idle, seeing them does nothing until you start moving: anyone still in the room with you is invited as the walk, loop or trip starts, before its first step. Off, they're invited whenever you see them.
**Important notes:** Re-inviting your own party (after a disconnect, a split or a trainer trip) isn't affected. Saved for this character.

### Stopping a Run turns Auto-Combat back on

**Default:** Off
**What it does:** Covers a walk-to, loop or Auto-Lair you started with **Run** (Go with Auto-Combat off) and then stopped yourself before it began, with a Stop chip in the Navigation window or the toolbar's Stop. On, Auto-Combat comes back on at the stop. Off, it stays off.
**Important notes:** A Run you don't stop turns combat back on by itself once it arrives, reaches its loop, or reaches its first lair. Saved for this character.

### Stopping a Sprint ends Sprint Mode

**Default:** Off
**What it does:** The same for a **Sprint** start (Go in Sprint Mode). On, stopping it before it began ends Sprint Mode at once, turning back on the autos Sprint turned off. Off, Sprint Mode stays on until your next walk arrives.
**Important notes:** Saved for this character.

### Print monster HP in the terminal when I look

**Default:** On
**What it does:** When you `look <monster>`, MudPlay shows its estimated remaining hit points in two places. This checkbox switches the first, the terminal line. (It used to be "Show monster HP lookup" and switched both; a character that had that off keeps the line off.)
- **The terminal** gets a yellow line with the monster's max HP, its wound band and that band's HP range, and a **best guess**: `[large orc: 100 HP, Sev: 30-49, ~41]`. The bands are **Full** (unwounded), **Slight**, **Mod** (moderately), **Hvy** (heavily), **Sev** (severely), **Crit** (critically) and **V.Crit** (very critically).
- **The status bar's TGT HP:** slot shows the range with the best guess in brackets, `TGT HP: 35-48 [~41]`. The bracket follows the damage the monster takes after the look. This is the status bar's **Looked-at target HP** item, which the checkbox doesn't affect: remove it from the bar (or put it on another row) under **Settings → BBS + Display → Status bar**.

**How the best guess works:** it starts from the monster's max HP and subtracts the damage the round totals credited to it (see *Show combat round totals*). It adds the monster's regen every 30 seconds while it's hurt, on both realms. Every `look` keeps it inside the wound band. When a look shows a regen tick fired (the band rose since the last look, or it held up despite the damage), it adds that tick to the best guess and re-times the regen from then. With two monsters of the same name in the room, attacks and looks go to the first one listed in *Also here:*, and so does the estimate.

### Enable the Great Pyramid climb solver / Enable the asylum (random-teleport maze) solver

**Default:** both On
**What it does:** Two Global-tier toggles for automated navigation through two of MajorMUD's notoriously tricky areas — the Great Pyramid's climbing puzzle and the Warped Asylum's random-teleport maze. On means walking to a destination inside either area drives the puzzle-solving automatically; off means a walk there just fails like any other unreachable spot, and you navigate manually. The pyramid climb sends one move at a time and waits to see where it led before sending the next, checking every step against the room MudPlay has you in. A move that didn't go through is taken again; a gate that stays shut sends the climb back to push its block again; a climb started partway along a floor picks up from the room you're in. If a move draws no answer at all, a Paradigm realm is asked where you are (`rm`); elsewhere the move is taken as made.

While it climbs, the toolbar shows navigation as running: **Pause** holds the climb where it stands and **Stop** ends it, on any floor. The asylum maze solver is the same: while it works out where a teleport dropped you and walks on, the toolbar shows navigation running, **Pause** holds its next move, and **Stop** ends the solve. The timed first floor and the second floor are run through, because stopping there is what kills the run. As the climb comes onto floor 1 it switches **Auto Combat, Auto Nuke, Auto Rest, Auto Get Items, Auto Get Cash, Auto Search, Auto Hide and Auto Light** off — whichever of them were on — and the toolbar shows them off; Auto Heal, Auto Bless and Auto Sneak stay as you have them. When the climb reaches floor 3, or ends for any reason, it switches back on the ones it switched off. Because it unticks Auto Rest, the **low-HP hang-up is off for those floors** too (it sits behind Auto Rest). If the master switch (Auto-All) goes off during the climb, the climb freezes; switching it back on gives back what was ticked at that moment, and the toggles the climb switched off still come back when it reaches floor 3 or ends (with the switch, if it ended while the switch was off). To override it, switch one back on yourself during those floors: it is yours from then on (the climb won't touch it again), it runs, and where it holds movement — a fight, a rest, a pickup, a search — the climb waits for it. The door-maze, footpath and top floors wait for fights, rests, pickups and party holds the way an ordinary walk does. The climb waits at the firepit for a fight or rest to finish before it starts the timer.

**What the Navigation window shows of a climb.** The climb is drawn like any walk: the map's route line runs through the rooms its script has left, shrinking as you go, with your destination marked. The status reads *Climbing the Great Pyramid to …*, with the floor and the step you're on, and **Current Nav** lists that floor's steps (*west*, *push block*, *ask sphinx fire, then up*). **On floor 1** the status also counts down the time left before the floor throws the party out, five minutes from the firepit `up`; the countdown is gone once you reach floor 2. A climb picked up part-way along floor 1 shows no countdown, since its clock started before MudPlay was watching.

On the door-maze floor, a door shown open is simply walked through. A plain door that is shut is opened the way any door on a walk is (bash or pick, per your door settings, resting when HP runs low). The four doors nobody can force are waited for until their timer swings them open. For the key door the climb will not leave the floating key's room until the golden lion key is in your pack: it picks the key up itself, asks a party member for it if their client got there first, and steps out and back in — up to three times — when the kill dropped nothing.
**Important notes:** These apply to every character on the install, not just the current one.

### Paradigm transport tokens (route offering + rooms-saved threshold)

**Default:** On, threshold 50 (Paradigm realms only — the rows are hidden otherwise)
**What it does:** When on, a walk-to whose destination a held transport token reaches faster surfaces a blue **"use token"** card in the route picker (see *Use a transport token* under navigation). The threshold sets how many rooms a token must save over walking before the card appears — a one- or two-room saving isn't worth a token's gold, daily charge, and buff-wipe. Turn the offering off entirely if you never want token routes suggested.
**Important notes:** Global-tier — applies to every character on the install. A token is only ever used when you pick its card; it's never taken automatically.

### Navigation map: hold a browsed view for N seconds

**Default:** `15`
**What it does:** After you pan or zoom the Navigation map, step between floors, or use **Center on…** / **Center on Destination**, the map stays where you're looking for this many seconds before it re-centres on you again. `0` makes it follow you again straight away.
**Important notes:** Global-tier (one setting for the whole install). Takes effect as soon as you Apply — no need to reopen the map.

### Cleanup Player Database after N days

**Default:** `90`
**What it does:** MudPlay keeps a database of every player it's seen. Records not seen within this many days get deleted automatically at the next startup. `0` disables cleanup entirely.
**Important notes:** Global-tier (one setting for the whole install). Cleanup runs at startup, so changing this doesn't retroactively purge anything until you next launch MudPlay.

### "Teleport to avoid combat instead of hanging" — not functional

**Important notes:** This checkbox is permanently disabled in the UI — a placeholder for a planned feature that isn't built yet. It does nothing currently.

---

## Events

Settings → Events. Lets you define per-character events. Each has three parts, set in the editor top to bottom: **When** it fires (a clock, a connection event, your stats, or a boss timer), what it **Does**, and **Then** what happens once that's done — go back to the loop you were running, start another, walk somewhere, or fire another event.

### Disable all events

**Default:** Off
**What it does:** A single master pause switch for every scheduled event on this character, without deleting or individually disabling each one.
**Important notes:** Saves immediately on toggle — no separate Apply step. The **master switch (Auto-All)** being off stops events too, whatever this reads: an event whose time comes while it is off is skipped, not run later, and one already running is held until the switch is back on. An event that walks somewhere plans its route like any walk the client starts by itself: by the **Settings → Teleports** list for automatic walks, with no route cards.

### Events waiting at most

**Default:** `10` (1–100)
**What it does:** Events run one at a time (see [When events overlap](#when-events-overlap)). This is how many may wait behind the one running. An event that fires with that many already waiting is dropped: it doesn't run, the Program Log says so and the terminal prints `[Event '<name>' dropped: …]`.
**Important notes:** An event never waits twice, so the limit only matters when more events than this fire behind one long run. Logoff events are never turned away. Saved for this character, immediately.

### Drop a waiting event after … minutes

**Default:** `30` (1–1440)
**What it does:** How long an event may wait for the running one. One that has waited longer is dropped, with the same Program Log line and terminal notice, so a walk you left paused for an hour doesn't set off an hour's worth of events when you resume it.
**Important notes:** A loop or Auto-Lair event with a **Stop after** rule makes the events behind it wait until the rule is met. If yours run longer than this (a 60-minute loop, say), raise it or the events that fire meanwhile are dropped. Saved for this character, immediately.

### Give up a paused event after … minutes standing still

**Default:** `5` (1–120)
**What it does:** A bank or sell trip, a trainer detour, a party comeback or a reconnect can pause the running event while it does its own job, and the event goes on when its loop or walk is back. If the trip never brings it back (it was cancelled, or ended by a plain stop), the event is given up after this many minutes with **nothing moving**, together with the events waiting behind it. Its Then step is skipped, the Program Log says so, and the terminal prints `[Event '<name>' given up: …]`.
**Important notes:** Only standing still counts. Time spent walking, however long, never does, and a walk held by a fight or a rest is not standing still either. Saved for this character, immediately.

### Event list (New… / Modify… / Remove)

**What it does:** Shows every scheduled event you've defined, with its **Name**, its trigger (**When**), a live countdown to its next fire (**Next**), and its action, its stop rule and its Then step (**What**, e.g. `Loop "Sewer" (until 3 laps) → go back`). **New…** and **Modify…** open the event editor; **Remove** deletes the selected event. Changes save to the profile immediately.
**Important notes:** Each event has a **Name** and a **Disabled** checkbox in its editor — untick Disabled to make it live. A row can show a "target missing" warning if it points at a saved Loop or Auto-Lair setup that's since been deleted or renamed — the event auto-disables itself in that case, and you'll need to clear its **Disabled** box again once you've fixed the reference. The **Next** column only counts down for **At time**, **Every** and timed **Boss** events while you're connected and in-game (those timers don't run otherwise); lifecycle events (Logon/Logoff/Re-log) fire on connection, not a clock, and **When** events fire on a state change, so both show a dash.

### Event editor — Sound

**What it does:** Next to the event's name, **Sound** picks what plays when the event fires: **(no sound)** (the default), one of the built-in tones, or **Custom file…** with a path box and **Browse…**. **▶** plays it at the Event sounds volume.
**Important notes:** Whether event sounds play at all, and how loud, is the **Event sounds** row on **Settings → Sounds** — it starts off, so tick it there. The sound plays when the event fires, before its action runs.

### Event editor — trigger types

- **Logon** — fires on every successful game entry, including the first connect of a session and every reconnect.
- **Logoff** — fires once, right before a clean, user-initiated disconnect (or a BBS cleanup-shutdown warning). A dropped/lost connection does **not** fire this.
- **Re-log** — fires like Logon, but only on reconnects — never the very first connect.
- **At time** — fires once at a specific daily clock time. If MudPlay wasn't connected when the time passed, that occurrence is simply skipped, not caught up later.
- **Every** — a recurring interval (seconds/minutes/hours). The timer restarts fresh at every connect and stops on disconnect.
- **When** — fires when your character reaches a state. Use **+ Add condition** to build a list; the event fires when **all** of them are true.
  - **Money:** coin you're carrying, entered as an amount of a coin type, e.g. `≥ 5 Platinum`.
  - **Encumbrance %:** carried weight as a percent of your max.
  - **Experience** and **Level:** your totals.
  - **Comparisons:** each condition compares with `≥`, `≤`, `>`, `<`, `=` or `≠`. Example: money `≥ 5 Platinum` **and** encumbrance `≥ 60`% to trigger a bank or stash run.
  - **Fires once, not repeatedly.** It fires when the conditions become true, then waits until they stop being true before it can fire again, so picking up more coin while you're already over the line doesn't fire it on every coin.
  - **Login counts.** If the conditions are already true when you log in, it fires once.
  - **Only in the game.** Like the timed triggers, it only fires while you're in the game.
  - **Needs readings first.** Money and encumbrance aren't known until MudPlay has read your inventory, and experience / level until it has read your stats. A condition on something not read yet doesn't count as true.
- **Boss** — fires off a boss on the **Bosses** tab's timer table. Pick the boss and the moment:
  - **A timer column hits 0** — pick which of the Bosses tab's columns to watch: an early spawn window (Paradigm **−20%**, **−10%**, **−5%**; Stock **87.5%**), or **Guaranteed (full)** — its full respawn time.
  - **Is killed** — the moment the timer table records its kill.
  - **Cleanup reset** — a cleanup boss comes back at nightly cleanup.
  - **min early** fires that many minutes before the moment (time to walk there); it doesn't apply to *Is killed*. Each fires once per kill, only while you're in the game, and not at all if the moment passed more than 10 minutes before MudPlay saw it (you weren't connected).

### Event editor — action types (Do)

- **Walk to** — navigate to a room. Done when you arrive.
  - **Start typing and a list of matching places opens under the box**, the same search the Navigation window's room box runs: room names, a coordinate (`1/297`), a boss's name (which lists that boss's room) and your saved GOTO favourites. Pick a row (click it, or arrow down and press Enter) and the box fills in as `map/room - room name`, so you can see which room the event will walk to.
  - Typing a coordinate by hand still works. The list shows the room that number is, so you can check it before saving.
  - A saved event reopens showing the room's name beside its number.
  - **A boss room marked Stop before entering.** When the room in the box belongs to a boss with **Stop before** ticked on the Bosses tab, a note says so under the box and a tick box appears: **Ignore the stop and walk into the room**.
    - **Unticked** (the default), the event respects the stop: the walk ends in the room next to the boss room and **does not go in**. The event counts that as arriving, so its **Then** step runs from there, and Then is all that happens after it.
      - The note under the box says this, and the **Then** block adds a line spelling out what the event does from outside the room as Then is currently set: goes back to what was running, stays there, fires a follow-on event, or walks on.
      - An event left like this never enters the boss room. That can be exactly what you want: walk up to the room, then fire a follow-on event that looks in and decides what to do. If you want this event to go in, either tick the box, or set **Then** to **Walk to** the same room and tick Then's box.
      - Then set to Walk to the same room with its box unticked stops short a second time and does nothing more; the Then block points that out.
    - **Ticked**, this event's walk goes into the boss room. It doesn't change the Bosses tab setting, so your own walks to that room still stop one room short.
    - The note shows for a coordinate or a picked row. A name typed without picking from the list isn't checked until you pick it.
- **Start loop** — starts a saved Loop by name. With a **Stop after** rule (below) the event is done when the rule ends the loop; with none it is done as soon as the loop has started.
- **Auto-lair** — starts a saved Auto-Lair setup by name. Done the same way as a loop.
- **Command** — sends free-form text to the game; an empty command is valid (useful for paging through a prompt). Done as soon as it's sent. A command with **Nothing** after it doesn't interrupt anything — handy for a periodic `stat`.
- **Roomba** — starts a Roomba sweep of your actively-managed rooms: **Sort** (a full sweep) or **Inventory only** (walks the circuit and refreshes the item log without moving anything). Done when the sweep finishes. If the sweep can't start (fewer than 2 rooms set to Actively Manage, or a sweep already running), the reason is written to the Program Log.
- **Wait** — stand still for that many seconds.
- **Rest up** — stand still and rest / meditate to your rest max (Settings → Health), as a loop room flagged *rest up here* does. Done once resting stops.
- **Bank trip** — walk to your Settings → Cash bank or stash room and deposit / stash there, then stop (the Then step decides where to go next).
- **Stash transfer** — carry one of your stash rooms' coin to a bank you pick, trip by trip, until the stash is empty: the same run as the map's **Transfer Stash to Bank** (see [Banking](#banking)). Pick the stash room and the bank from the two dropdowns. Done when the transfer ends, in the bank; if it gives up (no route, nothing could be picked up) the Then step still runs, and if you stop it yourself the Then step is dropped.

Every action except a plain command stops whatever walk, loop or Auto-Lair was running first. If another event is still running, the new one waits for it: see [When events overlap](#when-events-overlap).

### Event editor — Stop after (loop / Auto-Lair)

A loop or Auto-Lair never ends by itself, so its **Then** only runs once one of these ends it — whichever comes first:

- **after N laps** (loops only), **after N minutes**,
- **when a boss's timer moment comes** — the same choices as the Boss trigger: one of its timer columns hitting 0, the kill, or a cleanup reset, optionally minutes early (e.g. camp a boss's lair until it dies, or loop elsewhere until its −10% column hits 0). It defaults to the event's own boss. A timer moment that's already behind you stops the loop straight away,
- **when all of these hold** — money / encumbrance / experience / level conditions, e.g. encumbrance ≥ 80% to go sell.

With a rule set, the event isn't finished until the rule ends the loop and the Then after it is done, and events that fire meanwhile wait for it. Stopping the loop yourself skips Then.

With none set, nothing ever ends the loop, so the event is **finished the moment the loop has started** and its Then never runs (the editor warns about that). From then on the loop is simply what you are doing: a later event takes over from it and its **Go back** returns to it, exactly as with a loop you started by hand.

### Event editor — Then

What happens once the action is done:

- **Go back** — to the loop, Auto-Lair or walk that was running when the event fired. A new event defaults to this (a command defaults to Nothing).
- **Start loop** / **Auto-lair** — start a saved one.
- **Walk to** — a room, picked from the same type-to-search list as the action's Walk to box, with the same Stop before entering note and tick box when the room is such a boss room.
- **Fire event** — run another event by name; its own Then carries on from there, and its **Go back** still returns to what the first event interrupted. A chain of more than 10 events in a row is stopped as a loop.
- **Nothing** — stop there.

A walk or trip that can't be finished (no path, a leg fails) still runs its Then, so you aren't left standing. If **you** take over — stop the event's walk / loop / Auto-Lair, or start one of your own while it waits or rests — the event ends without its Then. Events you made before Then existed are converted the first time the character loads: a walk-to gets **Go back**, anything else **Nothing** — what they did before — so edit them to choose something else.

**Going back is an automatic walk.** The walk back to a loop or Auto-Lair (**Go back**, or a **Start loop** / **Auto-lair** Then) is one the client starts by itself, so it uses only the teleports ticked on **Settings → Teleports**. An event can walk you out of a place whose only way back in is a teleport: a room you enter by a command, say. If that teleport isn't ticked, the loop can't walk back and ends where the event did. The terminal then says the walk was refused because it is an automatic walk with no route open to it, and names the line to tick, as that tab lists it: `[Event 'Boss walk' finished, but loop 'Farm' didn't get going: approach failed: no route: this is an automatic walk, and the way there uses a teleport it isn't allowed. Tick "Library (17/1927) → Dusty Stair, Landing (17/9777)" on Settings → Teleports (Allow automatic walks to use the following teleports) to open it.]` When the shortest way back uses more than one unticked teleport and none of them alone would open a route, it says so and lists them all. The same kind of line is written for a Then loop or walk that can't start (a loop that no longer exists, a walk with no route), and for a Then Auto-Lair that refuses to start. An Auto-Lair that does start isn't watched after that: it reports no arrival to watch for.

### When events overlap

Events run one at a time, each from start to finish, **in the order they fired**. An event that fires while another is still running **waits its turn** and starts when that one is done. "Done" means the whole event: its action, a **Stop after** rule being met, its Then walk-to, and any events its Then fires.

- **A loop event with a Stop after rule is one event.** "Loop 3 laps, then walk to the bank" finishes its laps and its walk before anything that fired meanwhile starts; those then run in the order they fired.
- **A loop or Auto-Lair event with no Stop after rule is done once the loop has started.** It can't make others wait, since it would never finish. An event that fires later takes over from the loop and goes back to it. With another event already waiting behind it, the loop isn't started just to be stopped: the waiting event starts instead and takes the loop as what it goes back to, so the loop starts once, after it.
- **Go back happens once.** When the event that just finished would only go back or start a loop or Auto-Lair, and another event is waiting, the waiting event starts straight away and takes that loop as what **it** goes back to. Two boss events that both end in the same loop walk to the first boss, then the second, then start the loop.
- **A plain command** (Nothing after it) is sent the moment it fires and waits for nothing.
- **Logoff events jump the queue.** Only an event whose **When** is **Logoff** (fired by a cleanup warning or your own disconnect) starts at once: the event it interrupts is abandoned without its Then, and the events already waiting keep their places behind it. A second Logoff event waits for the first, ahead of the others. Every other event waits its turn, boss events and events whose command logs off (`;o`, `=x`, the realm's exit command) included.
- **The same event isn't queued twice.** An **Every 5 minutes** event whose run takes seven fires again while it is still going: that firing is skipped. One that fires again while it waits stays queued once.
- **Limits.** How many events may wait, and for how long, are two of the settings above ([Events waiting at most](#events-waiting-at-most), 10 and 30 minutes by default). An event turned away at either is dropped, and the terminal says so. An event that keeps firing into a full queue is named in the terminal once, not on every firing, and again only after the queue has had room. An event you remove, edit or disable while it waits doesn't run.
- **Stopping the running event empties the queue.** Press **Stop** (the toolbar's or the Navigation window's) while an event is walking, looping, waiting or resting, or take over as above, and the event ends without its Then and the waiting events are dropped with it. So does dying and **Reset States**.
- **A trip of the client's own doesn't end the event.** A bank or sell trip, a flee, a trainer or comeback detour stops the event's loop or walk only to start it again afterwards. The event waits for it and goes on when its loop or walk is back, with the laps it had counted and its Stop after rule intact, and the events behind it keep their places. A Stop after rule met in the meantime is applied when the loop is back. If the trip never brings the loop back (it was cancelled, or ended by a plain stop) and nothing has moved for five minutes, something called the detour off, so the event is given up the way a Stop ends it: no Then, the waiting events dropped, and one notice in the terminal. The five minutes is the **Give up a paused event after** setting, and only standing still counts: a walk, however long, never does, and a walk held by a fight or a rest is not standing still either. A walk or loop you start yourself while an event is paused takes over at once: the event and the ones waiting end then, not minutes later. Only loop, Auto-Lair and walk events survive a detour: a flee or a party comeback that stops the walker under a bank-trip or stash-transfer event still ends that event and its waiting events.
- **A hazard item's "halt walk"** ends the running event and its queue, as a Stop does.
- **A lost connection keeps the waiting events.** They are still waiting when you are back in the game, in the same order, and nothing is finished or started while the link is down. Their waiting time keeps counting through the outage, so one past the limit is dropped (with its terminal notice) as you come back in. The Logon and Re-log events fired by the reconnect take their places behind them. The event that was running goes by what its engine does: a loop restarts itself after a reconnect and the event goes on with it; a walk the drop didn't stop carries on; a wait's clock kept running; and one whose walk or trip was stopped by the drop ends without its Then, the next waiting event starting once you are back in. Closing MudPlay or switching character clears everything.
- **A chained event runs once.** If an event's Then fires another event that was already waiting, that event runs then, as the chain, and leaves the queue.
- **Your own walk or loop** is not an event: an event that fires over it takes over at once, as always, and goes back to it if its Then says so.
- **Pause** holds an event's walk like any other, and the loop it goes back to stays paused until you resume.
- **Auto-All off** freezes an event's walk where it stands, like any other walk. The event stays the running one and carries on when Auto-All is back on. A loop an event goes back to while Auto-All is off starts only then.
- **Events waiting when Auto-All goes off are kept.** They keep their places and their clocks stop: nothing is dropped for the time Auto-All was off. Switch back on within **5 minutes** and they simply carry on. After longer, the **Waiting Events** window opens instead and nothing waiting starts by itself: it lists each waiting event with what it does, how long it has waited and how much of that was with Auto-All off, each with a tick box. **Tick the ones that should still run**; the button runs those in their order and drops the rest (*Select all* / *Select none* set every tick). The window does not block the terminal. The event that was running carries on meanwhile, and an event that fires while the window is open queues behind the listed ones and is not part of the answer. **Closing the window drops every event it lists**, the same as answering with nothing ticked; a terminal notice gives the count, and events that fire afterwards run as usual. Switching Auto-All off with the window open closes it without dropping anything, and the question comes back at the next switch-on. If you were disconnected when you switched on, you can answer straight away, but nothing starts until you are back in the game.
- **A remote `@auto-all on` never asks.** When Auto-All is switched back on by a party member's `@auto-all on` (or through the local API) rather than by your own press of the button, menu item or hotkey, nobody is taken to be at the keyboard: after more than **5 minutes** off, the waiting events are **dropped without asking**, with a terminal notice giving the count. After 5 minutes or less they carry on, as with your own press.

The Program Log names each event as it is queued, started, finished, skipped, dropped or abandoned, and a bug report lists the running event (and whether it is suspended behind a trip or the connection is down), the ones waiting, the three limits and what the last Then came to.

---

## Sounds

**What it does:** Plays a sound when something you care about happens — a level-up, a boss kill, a walk finishing — so you can look away from the client and still know. Each moment (a *cue*) has its own on/off tick, its own sound and its own volume, so one can be quiet and another loud. Everything on this tab saves to the loaded character.

**It never slows the client.** A cue hands its sound to your operating system's own player and returns straight away; the playing happens in the background. No more than four sounds play at once (a fifth is dropped rather than queued), and the same cue never plays more than once a second, so a room of monsters dying together is one sound, not ten.

### Master

- **Sounds enabled** — off silences every cue below, and the sounds on Triggers and Events. Default on.
- **Master volume** (0–100, default 80) — every sound's own volume is scaled by this. A cue at 50 with the master at 80 plays at 40.

### Each cue's row

- **The tick** — whether this cue plays. **Every cue starts unticked**, so nothing makes a sound until you choose it. Hover the name for exactly when it fires.
- **Sound** — one of the built-in tones (Ding, Chime, Fanfare, Coin, Alert, Alarm, Low tone, Click) or **Custom file…**, which shows a path box and a **Browse…** button for your own file. WAV plays on every system; MP3, OGG and FLAC depend on your system's player.
- **Volume** (0–100, default 100) — this cue's own level, before the master volume. 0 is silent.
- **▶** — plays the row as it is set right now, unsaved edits included, even if the cue or the master switch is off.
- **every** (the two milestone cues only) — how many laps or kills between sounds.

### The cues

**Progress**

- **Level up** — you train a level. Its default sound is **Ding**: a single bell strike that rings for a second or so.
- **Loop milestone** *(every 100)* — every so many laps of the running loop. The count is the loop's own lap count: it carries on across a sell, train or bank detour and starts again when you start a loop.
- **Kill milestone** *(every 300)* — every so many kills since this character was loaded.
- **Walk finished** — a walk-to reaches its destination. A loop lap, and the legs of a sell / train / bank detour, don't count.

**Automation**

- **Auto-training** — an auto-train trip sets off.
- **Auto-selling** — an auto-sell trip sets off.
- **Event sounds** — whether Events that have a sound play it, and how loud. Each event names its own sound in its editor (Settings → Events), so this row has no sound to pick.

**Bosses**

- **Boss killed** — a boss on the Bosses table dies.
- **Boss spawn window opens** — a boss timer reaches its first early spawn window (Paradigm 80% of the timer, Stock 87.5%).
- **Boss timer done** — a boss timer reaches its guaranteed respawn; for a cleanup boss, the nightly cleanup that brings it back.

The two timer cues are checked every 30 seconds while you're in the game, and each plays once per kill. A timer that ran out more than ten minutes ago — while the client was closed or disconnected — stays quiet, so logging in doesn't ring for everything that respawned overnight.

**Chat and party**

- **Telepath received** — someone telepaths you. An `@` remote command doesn't count.
- **Party invite** — someone invites you to follow them.
- **Party member down** — a party member drops to the ground.

**Danger**

- **You died**.
- **Mortally wounded** — you drop to the ground.
- **Fleeing** — a flee starts: low HP or mana, a hit-and-run or failed-backstab retreat, a PvP flee back along your route, or a monster set to **Flee** seen in the room.
- **Navigation stopped** — a walk or loop fails, or the client loses track of the room.

**Connection and triggers**

- **Disconnected** — the connection drops on its own. Hanging up yourself is silent.
- **Reconnected** — the connection comes back after such a drop.
- **Trigger sounds** — whether Triggers with a sound file play it, and how loud. Each trigger names its own file, so this row has no sound to pick.

### What plays the sounds

MudPlay uses the player your system already has, so there's nothing to install on Windows or macOS. On Linux it uses `pw-play` (PipeWire), else `paplay` (PulseAudio), else `aplay` (ALSA); `aplay` plays WAV only and ignores the volume settings. If a sound can't be played — no player found, or a custom file that's missing or in a format the player can't read — the program log gets a `Sounds` warning saying why. The built-in tones are written to the `Sounds` folder inside the app folder the first time each is used.

---

## Diagnostics / Log Pane

Not a Settings tab — these six toggles live in the **Program Log** window (default shortcut F4), and are documented here for completeness since they're genuine saved preferences. They're saved **for all characters** (not per character), and take effect from the moment MudPlay starts, so **Auto-collect logs** captures the whole session, including before you load a character. They control how much detail MudPlay records about its own decisions, mainly useful for troubleshooting or preparing a bug report.

### Debug channel

**Default:** On
**What it does:** Turns on the generation of Debug-level log lines across the app's engines. With it off, that channel's lines simply aren't produced (not just hidden) — turning it on gives you a much more detailed decision trail, at the cost of a noisier log.

### Combat channel

**Default:** On
**What it does:** The same idea, but specifically for verbose combat-decision tracing (why an attack/spell choice was made each round).
**Important notes:** Both Debug and Combat default **on** so that a fresh Program Log already has enough detail to diagnose a problem the first time something goes wrong — a bug report captured with both off has nothing useful in it.

### Auto-collect logs

**Default:** Off
**What it does:** When on, MudPlay writes out full on-disk diagnostic files (program log, memory log, combat-trace log, performance log) for the session, under the data folder's `Logs/` directory, instead of only keeping recent lines in memory.
**The performance log** (`…-performance.log`) is for lag and stutters. MudPlay keeps checking whether its window is keeping up. Every moment it fell behind by 50 ms or more gets a `stall` line saying how long it lasted and what it was busy with: incoming game text, opening or closing a window (named), saving your profile, reading game data, or drawing the terminal or the map. Once a minute a `summary` line adds up that minute: the stalls, how long each kind of work took, the client's CPU, memory and garbage-collection activity, and which kinds of objects it allocated most (sampled) — the churn that causes garbage-collection pauses. Nothing is measured while the setting is off.
**When you might change it:** Turn on before a play session where you're trying to reproduce and capture an intermittent bug, or one where the client felt laggy.

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
- **monster death messages** — realms write those per species and don't publish them, so they're identified by position instead: the line before an experience gain is treated as death flavour and dropped (any row an earlier session captured for that same text is cleared out too). A line or two the client already knows may sit between them, such as the wear-off of what the monster had cast.

One shape can't be settled by wording at all: `Suijin shoots an arrow at bandit!` and `Suijin hurls a fireball at bandit!` are the same sentence. That one is decided by **who acted** — if the named player's class has no magery (Warrior, Witchunter, Ninja, Thief), they cannot be casting, so the line is dropped; from a class that *can* cast, or from a name the client doesn't know, it's kept. Class comes from the party screen, your own `stat` line, or the Players database (an observed class, else an unambiguous title).

A few line shapes are deliberately still captured:

- **a monster *casting* at a partymate** — uncatalogued monster spell messages are the main thing this is for;
- **any line where a non-caster is the *subject*** rather than the actor (`Suijin convulses violently!`), since that's a monster's spell landing on them;
- **ambient room flavour**, which in many areas is a room-spell trigger rather than scenery.

Double-click the row to open the same editor the Messages tab uses, pre-filled with the raw text, so you can turn it into a real catalogue entry on the spot. Repeated candidates are also listed in Game Data → **Unrecognized Lines** (with a **Seen In** map:room column) for batch review later; dismissing one there is sticky, so it won't quietly resurface as "new" if it recurs.
**Important notes:** On by default — the point of this toggle is catching the game's devs changing or adding message wording before it silently breaks something else (navigation, combat, condition tracking) that depends on recognizing that line.

### Log session statistics

**Default:** Off, every `5` minutes (1–120)
**What it does:** Writes everything the **Session Stats** window shows to a text file, so a long unattended run leaves a record of how its rates and totals moved instead of only what the window shows at the end. Each entry is one block, headed by the date and time, the character and board, and why it was written, followed by the window's three sections under the same row names:

- **Player Statistics**: every Offense and Defense row with its count, min–max, average and rate, including a row per spell.
- **Time Analysis**: the time breakdown, Sneak, Disarm Trap, Walk Latency and the loop laps.
- **Session Statistics**: kills, experience, their per-hour rates, Exp needed, Will level in, coin and items.

**When it writes:** once every N minutes **while your character is in the game**, and once more when you leave it (a disconnect, or an exit to the board's menus), so the stretch since the last entry isn't lost. Nothing is written at a login screen or a board menu. Ticking the box mid-session writes a first entry straight away.

**Where:** the Logs folder (**Tools → Open Logs folder…**), in a file ending `-session-stats.log`. A new file starts each time MudPlay is started or the box is ticked. The Program Log notes the file's name when it starts. Like the other files there, it is cleared out after 30 days.

**Important notes:**
- The Session Stats window doesn't need to be open, and the entries are the same figures it would show.
- The graphs (kills/hour, exp/hour, HP/MA per loop step) aren't written; the per-hour rates are.
- Resetting a panel in the Session Stats window resets what the next entry shows, since it is the same data. The one exception is **Laps completed**, which is the running loop's own count and isn't set back by Time Analysis's Reset.
- A file that can't be written (disk full, folder removed) stops the logging until the box is ticked again, with a warning in the Program Log.

---

## Roomba (Player Workshop)

Not a Settings-window tab (the **Roomba** tab in the Player Workshop). An automated gang-house (GH) item sorter, built on the same loop engine every saved Loop runs on rather than a separate navigation system.

**Shared per realm, not per character.** Room labels, the hidden-search settings, and the item-location log below are all saved against the realm you play (see *Realms* under BBS + Display), not your character — every character on a realm shares the same gang house, so labeling rooms (or running a sweep) on any one character makes them available to every other character on that realm.

**Setup:** mark your gang-house rooms one of two ways. On the Navigation map, right-click a room and choose **Toggle: Roomba Room** — the room gets a small **robot marker**; right-clicking it again removes it. Or, on the Roomba tab, type a room's **map/room number** into the box and click **Add Room**. Either way opens the rule picker (titled *Set 1/384 <room name> as Roomba Room*). A room's rules are OR'd together, so a single room can sort for several categories at once (e.g. a "Chain Scale" room admitting both Chainmail and Scalemail). Each rule is either:
- an **item category** — Weapon, Armour, Food, etc. (the same categories the imported item data already carries), optionally narrowed to a specific weapon or armour subtype; or
- an **equip slot** — Neck, Wrist, Finger, Off-Hand, etc. — for jewelry-style rooms that aren't classified by material or weapon type at all (a necklace has no "armour type"). A slot rule matches any item worn there regardless of its category.

Use **+ Add rule** to add another rule to the room, and the ✕ on a rule row to remove it. Any number of rooms may be flagged **"Make this the gang house's catch-all room"** — anything matching no explicit rule anywhere gets swept there instead of being left in place. Several catch-alls form an **overflow chain**, tried in order, so when the first fills up the next one takes over; flag as many as you like. Right-clicking **Toggle: Roomba Room** on an already-marked room removes it. At least two labeled rooms are required to start a sweep.

**Running a sweep:** open the **Player Workshop → Roomba** tab to review your labeled destinations and click **Start Sweep**. **The table lists one row per sort rule**, so a room admitting both Chainmail and Scalemail shows as two rows sharing a name, map/room and tick — "[catch-all]" repeats on each of that room's rows. Each row has a **Goto** button that opens the map and walks you straight to that room — handy for checking a house by hand without starting a full sweep.

**Editing and removing rules** — above the table sit **Edit** and **Remove**, and both act on whatever rows you've *highlighted* (ctrl/shift-click to pick several):

- **Edit** takes exactly one row and reopens that room in the rule picker, prefilled — so a rule set to the wrong category is corrected in place instead of being removed and re-added. It's also where you add a further rule to an existing room or flag it catch-all.
- **Remove** drops the highlighted **rules**, not their rooms: a room with three rules loses only the one you picked and keeps the other two. A room leaves the list entirely only once it has nothing left to sort by — its last rule removed and no catch-all flag (removing a catch-all room's "(no rules)" row takes the room off too).

Both buttons grey out while a sweep is running, since the circuit it's walking was planned from these labels.

**Only rooms with the "Actively Manage" checkbox ticked are visited.** This tick is **per character** — the room labels themselves are shared by every character on the realm, but *which* of them a character sweeps is its own choice, so alts who belong to **different gang houses on the same realm** each manage their own house without stepping on each other.

- A room you add yourself (the Add Room box, or the map's right-click *Toggle: Roomba Room*) is checked for you by default.
- A room adopted from someone else's **`@roomba sync`** arrives **unchecked** — because a shared label set can span *several* gang houses, and Roomba must never route from house to house or into one you lack the emblem for. Tick a synced room only once you're sure it belongs to the house *this* character sweeps.
- If you press Start Sweep (or Start Inventory) with **no** room checked, the phase label turns red with **"Select rooms to actively manage"** instead of starting; you still need **2+** checked rooms to run.

The **Search rooms for hidden items** checkbox is **off by default** — Roomba sorts only what's plainly visible on the floor; tick it to also send `sea` in each room while scanning and sort what's hidden (its **Searches per room** count, default 3, applies only then, and greys out while the box is unticked).

A sweep runs in three phases:
- **Scan (one lap):** the circuit is walked once — including unlabeled rooms between destinations — purely observing; nothing is picked up or moved yet. (There's no lap-count setting: one scan of the same rooms tells Roomba everything it needs.) With hidden-item search on, each room is also `sea`'d. **How hidden items are counted:** a search lists only the hidden copies it finds, never what's in plain sight, and each search finds its own share: one may show 8 of a hidden pile, the next 10, the next leave it out. So Roomba keeps the **highest** count any one search showed for each hidden item (never the searches added together), and adds that to what the room display shows: `34 rope and grapple` on the floor and `2 rope and grapple` from a search is 36 in the room. What is in plain sight is taken from the **latest** display of the room, since a display always shows all of it; a display that lists no items is an empty floor. The room's entry in the item-location log is written once, after its last search. (Roomba knows a list is a search's reply by the echoed `sea` right before it. If something lands between the two, or your statline puts text of its own after the prompt, it can't tell the reply from a redisplay of the room; an item that is also in plain sight then keeps the higher of the two counts instead of the sum, and the program log says so once.)
- **Sorting:** once the scan has mapped which items are misplaced, Roomba moves them all in the **fewest trips between rooms** — visible items picked up immediately, an item found only by a `sea` re-searched before its `get`, an item matching no rule sent to the catch-all (or left in place if there's none). An item with copies in plain sight and hidden copies is two pickups: the game hands over the ones in plain sight first, so the hidden ones are only asked for once those are in the pack, after a search. **When a pickup comes up short:** on Paradigm a counted `get` for more than is there is refused whole, so Roomba looks at the room once more (or searches again, for hidden copies) when its other pickups there are done, and picks up what is there, one look serving every stack that was refused in that room; on Stock, where a stack is taken a copy at a time, the copies it got are kept and delivered and only the rest is left. A hidden stack the search didn't find is listed as *hidden, and this sweep's search didn't find it*, not as gone. It **fills the pack toward your carry limit before delivering**, collecting several items bound for nearby rooms on one trip rather than shuttling each on its own (the carry-budget mechanics are detailed just below the phases).
- **Final lap:** when sorting is done the circuit is walked once more, looking only (no searches, nothing moved), and each room's entry in the item-location log is rewritten from what is in plain sight now, plus the hidden items the scan found and the sort didn't take. If a room isn't as the sort left it (someone took or dropped something meanwhile) the program log says so once for that room, naming what is extra and what is missing. Nothing is re-sorted on this lap; run another sweep for that. A room the lap couldn't show (too dark, or not on its way) gets the entry the sort itself accounts for: what the scan saw, less what was taken, plus what was delivered.
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

The **Roomba Log** button opens a window with the full per-move record, everything left in place (tagged with why — *no matching room*, *every room that takes it is full*, *gone by sort time*, *the game won't let it be picked up*, *too heavy to carry*, *couldn't be sorted this sweep — no room or headroom*, or *the pickup never landed*), and an end-of-run summary: rooms sorted, items sorted, and the explicit list of unmovable items. **Stop** ends a sweep early.

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

That per-player permission is the only gate — there's no separate on/off checkbox; a member you haven't granted it to gets nothing. The log itself is shared realm-wide (every character on the realm sees the same sightings). The Roomba tab shows a **Roomba Data Timestamp** next to *Searches per room* — the time of the newest sighting anywhere in the log — so you can tell at a glance whether the gang-house data is current or stale (it reads "no data yet" before the first scan).

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
| Player dies at (HP) | `-25` | -999–0 | `PlayerDiesAtHp` | Models/Settings/RealmProfile.cs |
| Auto-refine death floor | `true` | bool | `AutoRefineDeathFloor` | Models/Settings/BbsProfile.cs |
| PvP is enabled on this realm | `false` | bool | `PvpEnabled` | Models/Settings/RealmProfile.cs |
| Does the BBS have hang-up penalties? | `false` | bool | `HangupPenaltyEnabled` | Models/Settings/RealmProfile.cs |
| Hang-up in PvP combat: HP lost (%) from / to | `25` / `50` | 0–100, to ≥ from | `HangupPvpHpFromPercent` / `HangupPvpHpToPercent` | Models/Settings/RealmProfile.cs |
| Hang-up in PvP combat: Items dropped (up to) | `0` | 0–100 | `HangupPvpItemsDropped` | Models/Settings/RealmProfile.cs |
| Also penalised in combat with monsters (PvE) | `false` | bool | `HangupPvePenaltyEnabled` | Models/Settings/RealmProfile.cs |
| Hang-up in combat with monsters: HP lost (%) from / to | `25` / `50` | 0–100, to ≥ from | `HangupPveHpFromPercent` / `HangupPveHpToPercent` | Models/Settings/RealmProfile.cs |
| Hang-up in combat with monsters: Items dropped (up to) | `0` | 0–100 | `HangupPveItemsDropped` | Models/Settings/RealmProfile.cs |
| Also penalised outside a fight (every hang-up) | `false` | bool | `HangupOutsideFightPenaltyEnabled` | Models/Settings/RealmProfile.cs |
| Boss cleanup time / zone | `"21:00"` / local zone | `HH:mm` / IANA/Windows tz id | `CleanupTimeOfDay` / `CleanupTimeZoneId` | Models/Settings/RealmProfile.cs |
| Board disconnect line | `null` | pattern string | `DisconnectPattern` | Models/Settings/BbsProfile.cs |
| Name of runic currency | `"runic"` | string | `RunicCurrencyName` | Models/Settings/BbsProfile.cs |
| Columns / Rows (NAWS) | `80` / `25` | 40–200 / 20–100 | `TerminalCols` / `TerminalRows` | Models/Settings/BbsProfile.cs |
| Scrollback (lines) | `4000` | 100–100,000 | `ScrollbackLines` | Models/Settings/BbsProfile.cs |
| Wheel scroll (lines) | `5` | 1–50 | `BackscrollWheelLines` | Models/Settings/BbsProfile.cs |
| Username / Password (per-char) | `null` | encrypted string | `EncryptedUsername` / `EncryptedPassword` | Models/Profile/BbsCredentials.cs |
| Sysop powers — status / god lives (per-char) | `false` | bool ×2 | `SysopStatus` / `SysopGodLives` | Models/Profile/BbsCredentials.cs |
| Menu nav steps (per-char) | `[]` | list of `MenuStep{WaitForPattern, Send}` | `MenuNavSteps` | Models/Profile/BbsCredentials.cs |
| Confirm exit / hangup / save settings / deletes | `false` (all) | bool | `ConfirmExit`, `ConfirmHangup`, `ConfirmSaveSettings`, `ConfirmDeletes` | Models/Settings/ConfirmSettings.cs |
| Status bar layout (rows, zones, items, marquee) | one row: the original bar | 1–4 rows; items from the Status Bar list; custom text | `GlobalSettings.Settings["StatusBar"]` → `Rows[].Left` / `Center` / `Right` / `Marquee` | Models/Settings/StatusBarSettings.cs |

### Combat

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Normal / Alternate weapon attack command | `a` | free text | `NormalAttackCommand` / `AlternateAttackCommand` | Models/Profile/CombatSettings.cs |
| Action order | `SpellsFirst` | SpellsFirst / PhysicalFirst / AlternateSpellPhysical / AlternatePhysicalSpell / CustomRoundCycle | `ActionOrder` | Models/Profile/CombatSettings.cs |
| Round cycle (physical / spell rounds, start on spell) | 1 / 1 / false | 0–999 / 0–999 / bool | `CycleRoundsPhysical`, `CycleRoundsSpell`, `CycleStartOnSpell` | Models/Profile/CombatSettings.cs |
| Do BS attacks | `false` | bool | `DoBackstab` | Models/Profile/CombatSettings.cs |
| Don't BS if multi-attack | `true` | bool | `SkipBackstabIfMultiAttack` | Models/Profile/CombatSettings.cs |
| Run if BS fails | `false` | bool | `RunIfBackstabFails` | Models/Profile/CombatSettings.cs |
| Hit and Run tactics / Give up and fight after N runs | `false` / 3 | bool / 1–20 | `HitAndRunTactics` / `HitAndRunMaxRuns` | Models/Profile/CombatSettings.cs |
| Clear hostiles when seen hidden | `false` | bool | `ClearHostilesWhenSeenHidden` | Models/Profile/CombatSettings.cs |
| Clear hostiles when seen hidden: While solo / While in a party | `true` / `true` | bool / bool | `SeenHiddenClearWhileSolo`, `SeenHiddenClearWhileInParty` | Models/Profile/CombatSettings.cs |
| Clear hostiles when sneak fails | `false` | bool | `ClearHostilesWhenSneakFails` | Models/Profile/CombatSettings.cs |
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
| Show combat round totals | `false` | bool | `ShowCombatRoundTotals` | Models/Profile/CombatSettings.cs |
| Round Totals window options (set in the window) | all rows on / stacked monsters / cap off | bool × 6 | `RoundTotalsWindow.ShowSelf` / `ShowParty` / `ShowPlayers` / `ShowMonsters` / `EachMonster` / `CapAtMonsterHp` | Models/Profile/RoundTotalsWindowSettings.cs |
| Round totals rows: Me / Party / Other players / Monsters | `false` each | bool | `ShowCombatRoundTotalsSelf` / `…Party` / `…Players` / `…Monsters` | Models/Profile/CombatSettings.cs |
| Round totals: Cap at monster HP | `false` | bool | `CapRoundTotalsAtMonsterHp` | Models/Profile/CombatSettings.cs |

### Spells / Health

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Spell type priority (10 categories) | Emergency heal(1)…Debuffing(10), Priority buffs(5) | 1–10 permutation | `PriorityEmergencyHeal` … `PriorityDebuffing`, `PriorityPriorityBuffs` | Models/Profile/SpellsSettings.cs |
| Priority buff (per buff, Buff Watchdog) | off | on / off | `BuffSlot.PriorityBuff` | Models/Profile/BuffSettings.cs |
| Minor / Major / Emergency heal, HP Regen | unset | spell code | `MinorHealSpell`, `MajorHealSpell`, `EmergencyHealSpell`, `HpRegenSpell` | Models/Profile/SpellsSettings.cs |
| Cure Holds/Poison/Disease/Blindness | unset | spell code | `CureHoldsSpell` etc. | Models/Profile/SpellsSettings.cs |
| Cure after combat (per cure) | off | checkbox | `CureHoldsAfterCombat` etc. | Models/Profile/SpellsSettings.cs |
| Unified buff list (self + party bless, room light, mana-regen + reroll, when-HP/MA-full) | empty | spell / `#item` + targets + recast + conditions | `PartyBuffs` (`BuffSettings`) | Models/Profile/BuffSettings.cs (Buff Watchdog) |
| Per buff: Cast if mana ≥ / Cast while resting / Cast during combat | `50` (%) / false / false | 0–100 (%) or a mana amount / bool / bool | `BlessIfAboveMa` / `BlessWhileResting` / `BlessDuringCombat` on each `BuffSlot` | Models/Profile/BuffSettings.cs (Buff Watchdog → edit a buff) |
| Ignore poison, blindness, confusion, diseased (each suppresses both @wait + say) | false (all) | bool | `IgnorePoison` etc. | Models/Profile/SpellsSettings.cs |
| HP/MA threshold mode | `Percentage` (both) | Percentage / Absolute | `HpThresholdMode` / `MaThresholdMode` | Models/Profile/HealthSettings.cs |
| Rest max / Rest if below (HP, MA) | 95/60/95/30 (%) | 0–100,000 | `RestMaxHp`, `RestIfBelowHp`, `RestMaxMa`, `RestIfBelowMa` | Models/Profile/HealthSettings.cs |
| Run if below (HP, MA) | 20 / 10 (%) | 0–100,000 (0=off) | `RunIfBelowHp` / `RunIfBelowMa` | Models/Profile/HealthSettings.cs |
| Hang up if below | `5` (%) | death-floor minimum–100,000 | `HangIfBelowHp` | Models/Profile/HealthSettings.cs |
| Sys goto wimpy instead of hanging (+ location) | false / unset | bool / Sys Goto keyword | `SysGotoWimpyInsteadOfHanging` / `SysGotoWimpyLocation` | Models/Profile/HealthSettings.cs |
| Heal (rest) / Minor / Major / Emergency heal (combat) | 80/70/40/20 (%) | 0–100,000 | `HealRestTrigger`, `MinorHealCombatTrigger`, `MajorHealCombatTrigger`, `EmergencyHealTrigger` | Models/Profile/HealthSettings.cs |
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
| Help leader open doors / Ignore @wait when leading / Reset stats on loop start | false/false/true | bool | `HelpLeaderOpenDoors`, `IgnoreWaitWhenLeading`, `ResetStatisticsOnLoopStart` | Models/Profile/PartySettings.cs |
| Use @panic while leading / Ignore @panics | false / false | bool | `UsePanicWhileLeading`, `IgnorePanics` | Models/Profile/PartySettings.cs |
| Re-invite lost members / send @join nags / send @health nags / probe on join | true (all) | bool | `AutoInviteReconnecting`, `SendJoinToInvited`, `SendHealthToMembers`, `ProbeStatsOnPartyJoin` | Models/Profile/PartySettings.cs |
| Nag initial delay / frequency / max window (s) | 5/10/55 | 1–60 / 1–60 / 5–600 | `JoinNagInitialDelaySec`, `JoinNagFrequencySec`, `JoinNagMaxTotalSec` | Models/Profile/PartySettings.cs |
| Max monsters when partying | `20` | 1–20 | `MaxMonstersWhenPartying` | Models/Profile/PartySettings.cs |
| Wait if members below % | `0` | 0–100 | `WaitIfMemberBelowPercent` | Models/Profile/PartySettings.cs |
| If leading, wait only (s) / Return distance (rooms) | 90 / 30 | 0–3600 / 1–500 | `IfLeadingWaitTotalSec` / `ReturnDistanceRooms` | Models/Profile/PartySettings.cs |
| If leading, accept @comeback for (min) | 2 | 0–60 | `AcceptComebackMinutes` | Models/Profile/PartySettings.cs |
| Send par: every N seconds | `true`, `5` | bool, 1–60 | `ParPollOnTimer`, `ParPollFrequencySec` | Models/Profile/PartySettings.cs |
| Send par: after each combat round / including combat I only witness / when a round has unknown damage | `false` (all) | bool | `ParPollAfterCombatRound` / `ParPollIncludeWitnessedRounds` / `ParPollOnUnknownDamage` | Models/Profile/PartySettings.cs |
| PvP action | `HangUp` | HangUp / FleeThenHangUp / Flee / Attack / ChaseAttack | `Action` | Models/Profile/PvpSettings.cs |
| PvP flee to / rooms to flee | none / `10` | a favourite room / 1–99 | `FleeTo` / `RoomsToFlee` | Models/Profile/PvpSettings.cs |
| PvP flee hangup delay / come back after (s) | `30` / `60` | 0–600 / 0–3600 | `FleeHangupDelaySeconds` / `ComeBackAfterSeconds` | Models/Profile/PvpSettings.cs |
| PvP notify gang / re-connect after PvP (min) | `false` / `false`, `30` | bool / bool, 1–1440 | `NotifyGang` / `ReconnectAfterPvp`, `ReconnectAfterPvpMinutes` | Models/Profile/PvpSettings.cs |
| Flip a Friend to Enemy if they attack you | `false` | bool | `FlipFriendToEnemyIfAttacked` | Models/Profile/PvpSettings.cs |
| Turn off my evil warnings to attack | `false` | bool | `TurnOffEvilWarningsToAttack` | Models/Profile/PvpSettings.cs |
| Track enemies every (s) | `false`, `60` | bool, 5–3600 | `TrackEnemies`, `TrackEnemiesEverySeconds` | Models/Profile/PvpSettings.cs |
| Chase: give up after rooms unseen / guess the way / wait with no way to follow (s) | `8` / `true` / `20` | 1–50 / bool / 0–600 | `ChaseRoomsUnseen` / `ChaseGuessDirection` / `ChaseWaitSeconds` | Models/Profile/PvpSettings.cs |
| PvP spells 1 / 2: cast code, max casts, min mana per cast | blank, no limit, `0` | cast code / blank or 0–100 / 0–9999 | `Spell1` / `Spell2` (`SpellName`, `MaxCasts`, `MinManaPerCast`) | Models/Profile/PvpSpellSlot.cs |
| @kill on a player turns off evil warnings / warnings back on after (s) | `false` / `60` | bool / 0–3600 | `KillOrderTurnsOffEvilWarnings` / `WarningsBackAfterSeconds` | Models/Profile/PvpSettings.cs |
| As leader, send @kill to the party when starting a fight with a player | `true` | bool | `LeaderSendsKillOrder` | Models/Profile/PvpSettings.cs |
| Gang notice events (seen / attacked / I attack) and repeat (s) | `true` (all) / `60` | bool / 0–3600 | `GangTellSeen`, `GangTellAttacked`, `GangTellWeAttack` / `GangRepeatSeconds` | Models/Profile/PvpSettings.cs |
| Party-split hold (s) / chase: rooms in behind a door / reconnect enters the realm | `120` / `3` / `true` | 0–3600 / 1–20 / bool | `PartySplitHoldSeconds` / `ChaseDoorRooms` / `ReconnectEntersRealm` | Models/Profile/PvpSettings.cs |
| Copper / Silver / Gold / Platinum / Runic policy | Ignore/Collect×4 | Collect / Ignore / Discard | `CopperPolicy` etc. | Models/Profile/CashSettings.cs |
| Auto-deposit if wealth / coins exceed | 0 / 0 | 0–100,000,000 | `AutoDepositIfWealthExceeds` / `AutoDepositIfCoinsExceed` | Models/Profile/CashSettings.cs |
| Bank | none | dropdown of banks/stashes | `BankRoomKey` | Models/Profile/CashSettings.cs |
| Keep wealth (copper) | `0` | 0–100,000,000 | `KeepOnHandWealth` | Models/Profile/CashSettings.cs |
| Don't collect/get item → Light/Medium/Heavy (6 flags) | false (all) | bool | `SkipCollectIfMakesLight` etc. / `SkipGetItemIfMakesLight` etc. | Models/Profile/CashSettings.cs |
| Collect after combat finished / Drop smaller for larger | false / false | bool | `CollectAfterCombatFinished` / `DropSmallerForLarger` | Models/Profile/CashSettings.cs |
| On an auto-deposit trip, sell first at a shop within N steps of the bank | 25 | 0–500 (0 = never) | `SellOnBankRunWithinSteps` | Models/Profile/CashSettings.cs |
| Drop coin to make room for Auto-sell items / up to | false / Silver | bool / Copper–Runic | `DropCoinForSellItems` / `DropCoinForSellItemsUpTo` | Models/Profile/CashSettings.cs |
| Stash transfers: party members carry a share too | false | bool | `StashTransferPartyShare` | Models/Profile/CashSettings.cs |
| Disallow all remote / @party / telepaths / gangpaths / local | false (all) | bool | `DisallowAllRemoteCommands` etc. | Models/Profile/TalkSettings.cs |
| Warn on invalid remote command / Failure message | true / default text | bool / free text | `WarnOnInvalidRemoteCommand` / `RemoteCommandFailureMessage` | Models/Profile/TalkSettings.cs |
| Greet / Look back / Look on arrival | false (all) | bool | `GreetPlayersWhenFirstMet`, `LookBackWhenLookedAt`, `LookAtPlayersOnArrival` | Models/Profile/TalkSettings.cs |
| Log conversations / transactions / line limit | true/true/2000 | bool / bool / 100–100,000 | `LogConversations`, `LogTransactions`, `LogMaxLines` | Models/Profile/TalkSettings.cs |
| Conversation font / size / channel colors | defaults | bundled + every installed font / 8-32pt / hex per channel | `ConvoFont`, `ConvoFontSize`, `ChannelColors` | Models/Profile/TalkSettings.cs |

### Auto-Light / Auto-Lair / Auto-Trainer / Other / Events / Sounds

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
| Wait for altered stats at the trainer (Stock) | 120 | 0–600 seconds (0 = don't wait) | `AlteredStatsWaitSeconds` | Models/Profile/AutoTrainerSettings.cs |
| Announce level-ups / channel | false / Gangpath | bool / Gangpath,Gossip,Yell,Say | `AnnounceLevelUps` / `AnnounceChannel` | Models/Profile/AutoTrainerSettings.cs |
| Discovered trainers "Use?" | all allowed | bool per trainer (disabled-list) | `DisabledTrainers` | Models/Profile/AutoTrainerSettings.cs |
| Auto-obtain spells from shops / spell "Get?" | false / all wanted | bool / bool per spell (skipped-list, by spell name) | `AutoObtainShopSpells` / `SkippedShopSpells` | Models/Profile/AutoTrainerSettings.cs |
| Block @suicide when lives ≤ | `5` | 0–9 | `OtherSettings.MaxSuicideLivesThreshold` | Models/Profile/OtherSettings.cs |
| Utilize disarm traps | `true` | bool | `OtherSettings.UtilizeDisarmTrapsIfAble` | Models/Profile/OtherSettings.cs |
| @trap max disarms | 5 | 1–50 | `MaxTrapDisarmAttempts` | Models/Profile/OtherSettings.cs |
| Door max bash / pick / Pick over bash | 10/10/false | 1–100 / 1–100 / bool | `MaxBashAttempts`, `MaxPickAttempts`, `PicklocksOverBash` | Models/Profile/OtherSettings.cs |
| Stop auto-sneaking while a carried item leaves under … % | `15` | 0–95 | `OtherSettings.SneakStandDownChance` | Models/Profile/OtherSettings.cs |
| Hide items when discarding | false | bool | `HideWhenDiscarding` | Models/Profile/OtherSettings.cs |
| Teleports: Allow automatic walks to use the following teleports | none | list of teleports | `TeleportSettings.AutomaticWalkTeleports` | Models/Profile/TeleportSettings.cs |
| Periodic Damage Room Spells: Wear the item that negates a room's spell before stepping in | true | bool | `PeriodicDamageRoomSpellSettings.WearCounterBeforeEntering` | Models/Profile/PeriodicDamageRoomSpellSettings.cs |
| Periodic Damage Room Spells: Bars resting | ticked for every-tick and timer spells, clear for roll / condition spells | bool per room spell (only changes are stored) | `PeriodicDamageRoomSpellSettings.BarsResting` | Models/Profile/PeriodicDamageRoomSpellSettings.cs |
| Auto-request @comeback when left behind | true | bool | `AutoRequestComebackWhenLeftBehind` | Models/Profile/OtherSettings.cs |
| Pyramid / Asylum solver enabled | true / true | bool (Global) | `GlobalSettings.PyramidSolverEnabled` / `AsylumSolverEnabled` | Models/Settings/GlobalSettings.cs |
| Token routes: offer / min rooms saved | true / 50 | bool + 1–300 (Global, Paradigm) | `GlobalSettings.EnableTokenRoutes` / `TokenRouteMinRoomsShorter` | Models/Settings/GlobalSettings.cs |
| Navigation map: hold a browsed view | `15` s | 0–300 (Global) | `GlobalSettings.MapRecenterHoldSeconds` | Models/Settings/GlobalSettings.cs |
| Cleanup Player DB after N days | `90` | 0–3650 (Global) | `GlobalSettings.PlayerCleanupDays` | Models/Settings/GlobalSettings.cs |
| Disable all events | `false` | bool | `CharacterProfile.EventsGloballyDisabled` | Models/Profile/CharacterProfile.cs |
| Events waiting at most | `10` | 1–100 | `CharacterProfile.EventQueueLimit` | Models/Profile/CharacterProfile.cs |
| Drop a waiting event after (minutes) | `30` | 1–1440 | `CharacterProfile.EventQueueWaitMinutes` | Models/Profile/CharacterProfile.cs |
| Give up a paused event after (minutes standing still) | `5` | 1–120 | `CharacterProfile.EventSuspendedIdleMinutes` | Models/Profile/CharacterProfile.cs |
| Event (Name/Disabled/Sound/Trigger/Action fields) | see above | see above | `ScheduledEvent.*` | Models/GameData/ScheduledEvent.cs |
| Sounds enabled / Master volume | true / 80 | bool / 0–100 | `SoundSettings.Enabled` / `MasterVolume` | Models/Profile/SoundSettings.cs |
| Sound cue (on / sound / volume / every) | off / per cue, see **Sounds** / 100 / per cue | bool / built-in tone or file path / 0–100 / ≥1 | `SoundSettings.Cues[<cue>].Enabled` / `Sound` / `Volume` / `Every` | Models/Profile/SoundSettings.cs |

### Diagnostics / Log Pane / Equipment

| Setting | Default | Allowed Values | Config Key | Location |
|---|---|---|---|---|
| Debug channel | `true` | bool (Global) | `GlobalSettings.LogDiagnostics.Debug` | Models/Settings/LogDiagnosticsSettings.cs |
| Combat channel | `true` | bool (Global) | `GlobalSettings.LogDiagnostics.Combat` | Models/Settings/LogDiagnosticsSettings.cs |
| Auto-collect logs | `false` | bool (Global) | `GlobalSettings.LogDiagnostics.AutoCollect` | Models/Settings/LogDiagnosticsSettings.cs |
| Hop timing | `false` | bool (Global) | `GlobalSettings.LogDiagnostics.HopTiming` | Models/Settings/LogDiagnosticsSettings.cs |
| Log session statistics / every N min | `false`, `5` | bool, 1–120 (Global) | `GlobalSettings.LogDiagnostics.SessionStatistics` / `SessionStatisticsMinutes` | Models/Settings/LogDiagnosticsSettings.cs |
| Equipment sets (gear loadouts, edited in Character Workshop) | empty list, seeded per trigger type | list of `EquipmentSet` | `EquipmentSettings.Sets` | Models/Profile/EquipmentSettings.cs |

### Not user-configurable (confirmed, for completeness)

The following were traced and confirmed to have **no** exposed setting — listed so it's clear they were checked, not missed: Telnet terminal-type string (fixed `"ansi-bbs"`), the Telnet option negotiation whitelist, TCP keepalive probe interval/retry count, outgoing text encoding (fixed Latin-1), and IAC byte-escaping. (The one command-line flag that *does* exist, `--profile`, is documented under **Profiles** and **Command-Line / Environment**.)

---

*This guide reflects the MudPlay source as of the `main` branch. One setting in the Combat tab (Polite mode) is present in the UI but not currently wired to any runtime behavior — see its entry above for details. If a setting here stops matching what you see in the app, the code is the source of truth; please report the discrepancy.*

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
