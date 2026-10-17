using System.Text;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Remote;

// Write-side handler for the inventory / cash action commands. Unlike the
// read-only InventoryQueryHandler, these emit wire commands, so a wire-sender is
// bound (SetWireSender):
//   - @get-all — get <item> for every item on the room floor the
//     GroundItemTracker last surveyed (cash is left for the cash policy engine).
//   - @drop-all — drop <item> for every carried-but-unworn item (equipped gear
//     is left worn). @drop-all full drops everything held: worn gear too, plus
//     the readied light, the key ring, and every coin; @drop-all coins / keys
//     drop just the coins / just the key ring.
//   - @hide-all — the same four sweeps with `hide` instead of `drop`, stashing
//     it all in the room (like drop, hide takes worn gear directly).
//   - @deposit-all — bank the wealth above the per-denomination keep-on-hand
//     floors, or withdraw up to them when the character is below. Amount is the
//     copper-farthing total the game consolidates to the highest denomination on
//     dep / with.
//   - @share — split held coin evenly, per denomination, across the whole party
//     (self keeps a share plus the remainder); a party-whitelist command, so any
//     active party member can call it.
//
// @drop-all / @deposit-all / @share read the immutable InventoryManager.Snapshot
// and gate on IsLoaded — a full i dump has to have landed before we know what to
// drop / bank / share. @get-all reads the room-scoped GroundItemTracker instead
// (the last "You notice" survey). The engine gates authorisation via
// RemoteCommandCatalog before the handler runs. Wire replies ride the
// Latin1/CP437 BBS wire, so every reply is ASCII-only (no em-dash / approx
// glyphs).
public sealed class InventoryActionHandler : IDisposable
{
    private static readonly string[] RegisteredCommands =
        { "@drop-all", "@hide-all", "@deposit-all", "@share", "@get-all" };

    private readonly RemoteCommandManager _engine;
    private readonly InventoryManager _inventory;
    private readonly GroundItemTracker _ground;
    private readonly PartyState _party;
    private readonly Func<CashSettings> _readCash;
    private readonly CurrencyNaming _naming;
    // Paradigm batches a counted item command ("drop 3 black star key"); Stock needs
    // one command per copy (CountedCommand). Unwired reads as Stock — the safe form.
    private readonly Func<bool> _isParadigm;
    // True when the game would refuse to drop or hide the named item, worn or not.
    // Unwired: nothing is skipped.
    private readonly Func<string, bool, bool> _cannotDrop;
    // True for a cursed item: Get All leaves it on the floor (user, 2026-10-02).
    // Unwired: nothing is left.
    private readonly Func<string, bool> _isCursed;
    // Paces the sweeps (BulkCommandPacer). Null when no scheduler is wired:
    // every command then goes straight out.
    private readonly BulkCommandPacer? _pacer;
    private Action<byte[]>? _wireSender;
    // A get-all that found an empty ground cache sent a re-survey CR and is waiting
    // for the next "You notice" survey to grab on. One-shot; reset on that survey.
    private bool _getAllAwaitingResurvey;
    private bool _disposed;

    public InventoryActionHandler(
        RemoteCommandManager engine,
        InventoryManager inventory,
        GroundItemTracker ground,
        PartyState party,
        Func<CashSettings> readCash,
        CurrencyNaming naming,
        Func<bool>? isParadigm = null,
        Func<string, bool, bool>? cannotDrop = null,
        Func<string, bool>? isCursed = null,
        Action<TimeSpan, Action>? scheduleAfter = null,
        Services.LogService? log = null)
    {
        _isParadigm = isParadigm ?? (() => false);
        _cannotDrop = cannotDrop ?? ((_, _) => false);
        _isCursed = isCursed ?? (_ => false);
        if (scheduleAfter is not null) _pacer = new BulkCommandPacer(Send, scheduleAfter, log: log);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(readCash);
        ArgumentNullException.ThrowIfNull(naming);
        _engine = engine;
        _inventory = inventory;
        _ground = ground;
        _party = party;
        _readCash = readCash;
        _naming = naming;

        Register("@drop-all", OnDropAll);
        Register("@hide-all", OnHideAll);
        Register("@deposit-all", OnDepositAll);
        Register("@share", OnShare);
        Register("@get-all", OnGetAll);

        _ground.SurveyUpdated += OnGroundResurveyed;
    }

    // Bind the wire-sender — the gate-wrapped SendUserInput pipeline from
    // MainWindowViewModel, same shape the cash / divert handlers use. Without it
    // the commands still authorise and reply, but no drop / dep / give reaches the
    // game.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ground.SurveyUpdated -= OnGroundResurveyed;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void Register(string command, Action<RemoteCommandContext> handler)
    {
        if (!RemoteCommandCatalog.TryGetCategory(command, out PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(command, category, handler);
    }

    // @get-all — get <item> for every item on the room floor from the latest
    // "You notice" survey. Cash is excluded by the GroundItemTracker (the
    // cash-policy engine owns coin), and the leading article is stripped so the
    // wire verb matches the item's noun phrase. There is no bulk "get all" verb in
    // MajorMUD, so this paces one get per item. Encumbrance is left to the game to
    // enforce — the server refuses a pickup that would overload us, same as the
    // auto-get engine.
    private void OnGetAll(RemoteCommandContext ctx) => ctx.Reply(GetAll());

    // Run the get-all sweep and return the status line. Shared by the @get-all
    // remote handler (which replies it on the party channel) and the local
    // Action-menu / toolbar "Get All" (which logs it). Emits the paced get
    // commands as a side effect.
    public string GetAll()
    {
        if (_ground.Items.Count > 0) return GrabGround();

        // Empty cache — the last "You notice" survey may be stale: items can drop
        // mid-combat (from kills) with no auto re-survey, so a manual Get All saw
        // nothing even with loot on the floor (report stock-20260730-215247). Force
        // a fresh look and grab on the next survey, so the click reflects the actual
        // floor. One re-survey at a time; a second while pending doesn't re-CR.
        if (_getAllAwaitingResurvey) return "nothing on the ground to get";
        _getAllAwaitingResurvey = true;
        Send("");   // bare CR — re-observe the room + its "You notice" survey
        return "re-surveying the floor for get-all";
    }

    // The re-survey (or any later "You notice") landed — grab the freshly-surveyed
    // floor for a get-all that was waiting. One-shot: the wire shows the gets.
    private void OnGroundResurveyed()
    {
        if (!_getAllAwaitingResurvey) return;
        _getAllAwaitingResurvey = false;
        if (_ground.Items.Count > 0) GrabGround();
    }

    private string GrabGround()
    {
        List<string> commands = new();
        List<string> cursed = new();
        foreach (string item in _ground.Items)
        {
            string name = StripArticle(item);
            if (name.Length == 0) continue;
            // A cursed item, once picked up and worn, can't be dropped again.
            if (_isCursed(name))
            {
                cursed.Add(name);
                continue;
            }
            commands.Add($"get {name}");
        }
        Dispatch(commands);
        string leaving = cursed.Count == 0 ? "" : $" (leaving {cursed.Count} cursed: {string.Join(", ", cursed)})";
        return $"getting {commands.Count} ground item{(commands.Count == 1 ? "" : "s")}{leaving}";
    }

    // The game answered a command with a prompt; the sweep pacer counts it.
    public void NotePrompt() => _pacer?.NotePrompt();

    // The game ignored a command for coming too fast.
    public void NoteRateLimited() => _pacer?.NoteRateLimited();

    // True while the pacer holds commands it hasn't sent yet.
    public bool HasPacedCommandsQueued => _pacer is { Pending: > 0 };

    // Drop whatever the pacer still holds: after a death or a disconnect the pack
    // those commands were worked out from is not the one they would act on.
    public void CancelPaced() => _pacer?.Cancel();

    // Item commands from elsewhere (the Chest Offload tab's discards, the
    // auto-discard engine's piles and its held hides on entering a room) that
    // should share the sweeps' pacing. A sender that may want its waiting commands
    // back names itself as their owner (RecallPaced), and may ask to be consulted
    // just before each one goes (`mayGo`; see BulkCommandPacer.Enqueue).
    public void SendPaced(IReadOnlyList<string> commands, object? owner = null, Func<string, bool>? mayGo = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        Dispatch(commands, owner, mayGo);
    }

    // Take back the owner's commands still waiting in the pacer that `take` picks.
    // Returns the ones taken; without a pacer everything has gone out already.
    public IReadOnlyList<string> RecallPaced(object owner, Func<string, bool> take)
        => _pacer?.CancelOwned(owner, take) ?? Array.Empty<string>();

    private void Dispatch(IReadOnlyList<string> commands, object? owner = null, Func<string, bool>? mayGo = null)
    {
        if (_pacer is { } pacer) pacer.Enqueue(commands, owner, mayGo);
        else foreach (string command in commands)
        {
            if (mayGo is null || mayGo(command)) Send(command);
        }
    }

    // What a drop-all / hide-all sweep takes. Unworn is the original @drop-all: the
    // carried, unworn pack. Full is everything held — worn gear (a worn item drops or
    // hides with a plain `drop` / `hide`, no `rem` first — see GAME_MECHANICS), the
    // readied light, the key ring and every coin. Coins / Keys take just that part.
    public enum DropScope { Unworn, Full, Coins, Keys }

    // @drop-all [full|coins|keys] — bare is the unworn pack. InventorySnapshot
    // .CarriedItems already excludes worn gear (slot-suffixed lines land in
    // EquippedItems) and currency tokens. The leading article is stripped so the
    // wire verb matches on the item's noun phrase ("a rusty dagger" → drop rusty
    // dagger).
    private void OnDropAll(RemoteCommandContext ctx) => OnSweep(ctx, "@drop-all", DropAll);

    // @hide-all [full|coins|keys] — the @drop-all sweeps with `hide`, stashing
    // everything in the room instead of leaving it on the floor.
    private void OnHideAll(RemoteCommandContext ctx) => OnSweep(ctx, "@hide-all", HideAll);

    // A sweep that can't be read or can't be run is refused; one that ran answers
    // with what it sent, "nothing to drop" included.
    private void OnSweep(RemoteCommandContext ctx, string command, Func<DropScope, string> sweep)
    {
        DropScope scope = DropScope.Unworn;
        if (ctx.Args.Count > 1 || (ctx.Args.Count == 1 && !TryParseScope(ctx.Args[0], out scope)))
        {
            ctx.Refuse($"usage: {command} [full|coins|keys] (bare = unworn items)");
            return;
        }
        if (!_inventory.IsLoaded) { ctx.Refuse(InventoryNotParsed); return; }
        ctx.Reply(sweep(scope));
    }

    private const string InventoryNotParsed = "inventory not parsed yet (type i)";

    private static bool TryParseScope(string word, out DropScope scope)
    {
        switch (word.ToLowerInvariant())
        {
            case "full": scope = DropScope.Full; return true;
            case "coins": scope = DropScope.Coins; return true;
            case "keys": scope = DropScope.Keys; return true;
            default: scope = DropScope.Unworn; return false;
        }
    }

    // Run a drop-all / hide-all sweep and return the status line. Shared by the
    // remote handlers and the local Drop / Hide actions. Emits the commands as a side
    // effect. Coins go out as `drop N <coin noun>` / `hide N <coin noun>`, the same
    // wording the cash Discard policy and the stash rooms use.
    public string DropAll(DropScope scope = DropScope.Unworn) => Sweep("drop", "dropping", scope);

    public string HideAll(DropScope scope = DropScope.Unworn) => Sweep("hide", "hiding", scope);

    private string Sweep(string verb, string doing, DropScope scope)
    {
        if (!_inventory.IsLoaded) return InventoryNotParsed;
        InventorySnapshot snap = _inventory.Snapshot;

        List<string> commands = new();
        List<string> kept = new();
        int items = 0;
        if (scope is DropScope.Unworn or DropScope.Full)
            foreach (string item in snap.CarriedItems) items += SweepNamed(commands, kept, verb, item, worn: false);
        if (scope == DropScope.Full)
        {
            foreach (EquippedItem worn in snap.EquippedItems) items += SweepNamed(commands, kept, verb, worn.Name, worn: true);
            if (snap.ReadiedLight is { } light) items += SweepNamed(commands, kept, verb, light.Name, worn: true);
        }
        if (scope is DropScope.Full or DropScope.Keys && snap.Keys is { } keys)
            foreach (string key in keys) items += SweepNamed(commands, kept, verb, key, worn: false);

        int coinKinds = 0;
        if (scope is DropScope.Full or DropScope.Coins)
        {
            CurrencyHoldings c = snap.Currency;
            coinKinds += SweepCoins(commands, verb, c.Copper, "copper");
            coinKinds += SweepCoins(commands, verb, c.Silver, "silver");
            coinKinds += SweepCoins(commands, verb, c.Gold, "gold");
            coinKinds += SweepCoins(commands, verb, c.Platinum, "platinum");
            coinKinds += SweepCoins(commands, verb, c.Runic, _naming.RunicName);
        }
        Dispatch(commands);

        // Wire replies are ASCII-only; the list of kept items is short.
        string done = verb == "hide" ? "hidden" : "dropped";
        string keeping = kept.Count == 0 ? "" : $" (keeping {kept.Count} that can't be {done}: {string.Join(", ", kept)})";
        if (items == 0 && coinKinds == 0) return $"nothing to {verb}{keeping}";
        string status = scope switch
        {
            DropScope.Unworn => $"{doing} {items} carried item{Plural(items)}",
            DropScope.Keys => $"{doing} {items} key{Plural(items)}",
            DropScope.Coins => $"{doing} all coins ({coinKinds} denomination{Plural(coinKinds)})",
            _ => $"{doing} everything: {items} item{Plural(items)}"
                 + (coinKinds > 0 ? $" and all coins ({coinKinds} denomination{Plural(coinKinds)})" : ""),
        };
        return status + keeping;
    }

    // Drop / hide one pack / ring entry, which may be a stack ("43 black diamond"):
    // one counted command on Paradigm, one per copy on Stock (no item batching
    // there). Returns how many copies it covered. An item the game won't let go of
    // (no-drop, loyal, or a cursed item worn — see ItemDropRule) is left out: sending it only earns "You may not
    // drop that item!" and spends a place in the game's command queue.
    private int SweepNamed(List<string> commands, List<string> kept, string verb, string item, bool worn)
    {
        (int count, string raw) = CountedCommand.SplitLeadingCount(item.Trim());
        string name = StripArticle(raw);
        if (name.Length == 0) return 0;
        if (_cannotDrop(name, worn))
        {
            kept.Add(name);
            return 0;
        }
        CountedCommand.Emit(commands.Add, verb, count, name, _isParadigm());
        return count;
    }

    private int SweepCoins(List<string> commands, string verb, long count, string currency)
    {
        if (count <= 0) return 0;
        commands.Add($"{verb} {count} {_naming.WireNoun(currency)}");
        return 1;
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    // @deposit-all — level the character's wealth to the raw keep-on-hand floor.
    // Over the floor → dep <excess>; under it → with <shortfall>; exactly on it →
    // no-op reply. The amount is the consolidated copper-farthing value (same
    // figure @wealth reports); the game re-consolidates held coin to the highest
    // denomination after the transaction, so we never have to name individual
    // coins.
    //
    // Protocol, not a courtesy: a leader's stash transfer (StashTransferRunner)
    // waits at the bank for one answer from each member and reads it by its
    // opening words, "wealth unknown" and "not in a bank" among them. So every
    // answer goes whatever the Warn setting says.
    //
    // Outside a bank nothing is sent and the answer says so: "depositing" is only
    // answered where a deposit can start. A room we can't place is not a room
    // known to be no bank, so there the command goes out as before and the game
    // decides.
    private void OnDepositAll(RemoteCommandContext ctx) =>
        ctx.Reply(InBank?.Invoke() == false ? NotInBank : DepositAll());

    // Whether the room we stand in is a bank: false only when the room is known
    // and is none, null when the room isn't known. Unbound, nothing is checked.
    public Func<bool?>? InBank { get; set; }

    // The opening words are what a leader's stash transfer reads.
    public const string NotInBank = "not in a bank - nothing deposited";

    // Level held coin to the keep-on-hand floor and return the status line. Shared
    // by the @deposit-all remote handler and the local "Deposit All" action. Emits
    // a dep / with as a side effect.
    public string DepositAll()
    {
        if (!_inventory.IsLoaded) return "wealth unknown - parse inventory first (type i)";
        Models.Profile.CashSettings cash = _readCash();
        // Keep-on-hand is an amount of a chosen denomination — convert to copper.
        long keep = cash.KeepOnHandWealth
            * Game.Inventory.CurrencyHoldings.CopperUnit(cash.KeepOnHandDenomination);
        long held = _inventory.Snapshot.Currency.TotalCopperValue;
        long delta = held - keep;
        if (delta > 0)
        {
            Send($"dep {delta}");
            return $"depositing {delta:N0} copper (keeping {keep:N0})";
        }
        if (delta < 0)
        {
            long shortfall = -delta;
            Send($"with {shortfall}");
            return $"withdrawing {shortfall:N0} copper (up to {keep:N0} on hand)";
        }
        return $"already at keep-on-hand ({keep:N0} copper)";
    }

    // @share — split held coin evenly across the whole party. For each
    // denomination, the per-head share is count / partySize (integer division,
    // party size counting self); every non-self member is
    // give <share> <denom> to <member>'d that amount, so self keeps one share plus
    // any indivisible remainder. Party-whitelist gated (catalog category None), so
    // the engine only reaches here for an active party member.
    private void OnShare(RemoteCommandContext ctx)
    {
        if (!_inventory.IsLoaded) { ctx.Refuse("wealth unknown - parse inventory first (type i)"); return; }

        List<string> recipients = new();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            recipients.Add(GivenName(m.Name));
        }
        if (recipients.Count == 0) { ctx.Refuse("no party members to share with"); return; }

        // +1 for self: self is one of the sharers and keeps their own cut, so
        // the divisor is the full party size regardless of whether the par
        // table currently lists a self row.
        int partySize = recipients.Count + 1;
        CurrencyHoldings c = _inventory.Snapshot.Currency;
        // Runic carries the board's wire word so `give N <word>` is a command the
        // server accepts; the other four denominations are stable.
        (string Denom, int Count)[] denominations =
        {
            ("copper", c.Copper),
            ("silver", c.Silver),
            ("gold", c.Gold),
            ("platinum", c.Platinum),
            (_naming.RunicName, c.Runic),
        };

        bool anyShared = false;
        foreach ((string denom, int count) in denominations)
        {
            int per = count / partySize;
            if (per <= 0) continue; // fewer coins than sharers — nothing to split.
            foreach (string recipient in recipients)
                Send($"give {per} {denom} to {recipient}");
            anyShared = true;
        }

        if (anyShared) ctx.Reply($"sharing coins among {partySize} party members");
        else ctx.Refuse("nothing to share (too few coins to split)");
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    // Drop a leading indefinite / definite article so the wire item name is the
    // bare noun phrase MajorMUD matches ("a rusty dagger" → "rusty dagger").
    // Leaves the name untouched when it carries no article.
    private static string StripArticle(string name)
    {
        if (name.StartsWith("a ", StringComparison.OrdinalIgnoreCase)) return name[2..];
        if (name.StartsWith("an ", StringComparison.OrdinalIgnoreCase)) return name[3..];
        if (name.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) return name[4..];
        return name;
    }

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
