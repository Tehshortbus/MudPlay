using System.Globalization;
using MudPlay.Game.Inventory;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// The @auto-* family. A party member's @auto-combat off flips our
// AutoActionDefaults.AutoCombat flag, persists the profile, and replies with the
// new state.
//
// Per-engine grammar: each @auto-X takes an optional first arg of on / off which
// sets that state explicitly. With no arg the flag is toggled. Either way the
// reply echoes the resulting state. Any other arg is rejected with a "?" reply
// (gated on WarnOnDenial).
//
// @auto-heal and @auto-rest are separate switches (AutoActionDefaults.AutoHeal /
// AutoRest): healing casts and the rest engine turn on and off independently.
//
// @auto-all drives the shared AutoModeController master switch (the same one as
// the toolbar / Action-menu "Auto-All" button). No arg flips it; `off` switches
// it off even when every toggle was already off by hand; `on` switches it on,
// falling back to the character's base modes when no toggle is remembered. The
// reply reports the switch, not the toggles. It is the one remote command still
// obeyed while the switch is off (RemoteCommandManager.MasterSwitchCommand).
//
// @auto-get also takes an item (user, 2026-10-17): `@auto-get <item>` reports
// that item's own Auto-collect and Max to get, and `on` / `off` and `max <n>` /
// `max none` after the name set them for this character (AutoGetItemRules). The
// switch words are read off the end, so an item's name may hold any words. A
// first word of `on` / `off` is always the engine toggle, and what follows it is
// ignored, as for every @auto-* toggle (user, 2026-10-17).
//
// All commands require PlayerRemoteControls.AlterSettings per the catalog — a
// "do something on my behalf" tier.
public sealed class AutoModeRemoteHandler : IDisposable
{
    private const string TabKey = "General";
    private const string LogCategory = "RemoteCmd";
    private const string AutoGet = "@auto-get";

    // Mapping from @-command name to flag accessor.
    private static readonly (string Cmd, Func<AutoActionDefaults, bool> Get,
                             Action<AutoActionDefaults, bool> Set)[] Mapping =
    {
        ("@auto-combat", d => d.AutoCombat,   (d, v) => d.AutoCombat   = v),
        ("@auto-nuke",   d => d.AutoNuke,     (d, v) => d.AutoNuke     = v),
        ("@auto-heal",   d => d.AutoHeal,     (d, v) => d.AutoHeal     = v),
        ("@auto-rest",   d => d.AutoRest,     (d, v) => d.AutoRest     = v),
        ("@auto-bless",  d => d.AutoBless,    (d, v) => d.AutoBless    = v),
        ("@auto-light",  d => d.AutoLight,    (d, v) => d.AutoLight    = v),
        ("@auto-cash",   d => d.AutoGetCash,  (d, v) => d.AutoGetCash  = v),
        ("@auto-get",    d => d.AutoGetItems, (d, v) => d.AutoGetItems = v),
        ("@auto-sneak",  d => d.AutoSneak,    (d, v) => d.AutoSneak    = v),
        ("@auto-hide",   d => d.AutoHide,     (d, v) => d.AutoHide     = v),
        ("@auto-search", d => d.AutoSearch,   (d, v) => d.AutoSearch   = v),
    };

    private readonly RemoteCommandManager _engine;
    private readonly ProfileService _profile;
    private readonly AutoModeController _controller;
    private readonly LogService? _log;
    private AutoGetItemRules? _itemRules;
    private bool _disposed;

    public AutoModeRemoteHandler(
        RemoteCommandManager engine,
        ProfileService profile,
        AutoModeController controller,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(controller);
        _engine = engine;
        _profile = profile;
        _controller = controller;
        _log = log;

        foreach ((string cmd, _, _) in Mapping)
        {
            if (!RemoteCommandCatalog.TryGetCategory(cmd, out PlayerRemoteControls category))
                continue;
            _engine.RegisterHandler(cmd, category, ctx => HandleOne(cmd, ctx));
        }

        // @auto-all drives the shared master snapshot controller.
        if (RemoteCommandCatalog.TryGetCategory("@auto-all", out PlayerRemoteControls allCat))
            _engine.RegisterHandler("@auto-all", allCat, HandleAll);

        // @settings is the read-only counterpart: a single query that
        // reports every auto-engine's current state in one reply.
        if (RemoteCommandCatalog.TryGetCategory("@settings", out PlayerRemoteControls settingsCat))
            _engine.RegisterHandler("@settings", settingsCat, HandleSettings);
    }

    // Bind the item rules the @auto-get <item> forms read and change. Unbound,
    // those forms are refused.
    public void SetItemRules(AutoGetItemRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _itemRules = rules;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach ((string cmd, _, _) in Mapping) _engine.UnregisterHandler(cmd);
        _engine.UnregisterHandler("@auto-all");
        _engine.UnregisterHandler("@settings");
    }

    private void HandleOne(string cmd, RemoteCommandContext ctx)
    {
        (_, Func<AutoActionDefaults, bool> get, Action<AutoActionDefaults, bool> set) =
            Mapping.First(m => m.Cmd == cmd);

        if (cmd == AutoGet && IsItemForm(ctx.Args))
        {
            HandleAutoGetItem(ctx);
            return;
        }

        if (_profile.Current is not { } profile)
        {
            ctx.Refuse("?");
            return;
        }

        GeneralSettings general = ReadGeneral(profile);
        bool current = get(general.AutoMode);

        bool wanted;
        if (ctx.Args.Count == 0)
        {
            // No arg → toggle the current state.
            wanted = !current;
        }
        else if (!TryParseOnOff(ctx.Args[0], out wanted))
        {
            ctx.Refuse("?");
            return;
        }

        if (current != wanted)
        {
            set(general.AutoMode, wanted);
            if (!WriteGeneral(profile, general))
            {
                ctx.Refuse(SettingsUnreadable);
                return;
            }
            _log?.Log(LogSeverity.Info, LogCategory,
                $"{cmd} from {ctx.Sender}: {(current ? "on" : "off")} -> {(wanted ? "on" : "off")}");
        }
        ctx.Reply($"{cmd}: {(wanted ? "on" : "off")}");
    }

    // A first word other than on / off names an item.
    private static bool IsItemForm(IReadOnlyList<string> args) =>
        args.Count > 0 && !TryParseOnOff(args[0], out _);

    private void HandleAutoGetItem(RemoteCommandContext ctx)
    {
        if (!TryParseItemForm(ctx.Args, out ItemForm form, out string? problem))
        {
            ctx.Refuse(problem);
            return;
        }
        if (_itemRules is not { } rules)
        {
            ctx.Refuse();
            return;
        }
        bool changing = form.AutoCollect is not null || form.SetMax;
        if (changing && !rules.CanChange)
        {
            ctx.Refuse(NoCharacter);
            return;
        }

        IReadOnlyList<int> found = rules.Find(form.Item);
        if (found.Count != 1)
        {
            ctx.Refuse(found.Count == 0
                ? $"{NoItemOpening}{WireText.Shortened(form.Item)}'"
                : AmbiguousItem(form.Item, found, rules));
            return;
        }
        int number = found[0];
        string name = rules.NameOf(number);
        AutoGetItemRules.State before = rules.Read(number);
        if (!changing)
        {
            _log?.Debug(LogCategory, $"{AutoGet} from {ctx.Sender}: asked about '{name}' (#{number})");
            ctx.Reply(Describe(name, before));
            return;
        }

        AutoGetItemRules.State after = rules.Change(number, form.AutoCollect, form.SetMax, form.Max);
        if (after != before)
            _log?.Log(LogSeverity.Info, LogCategory,
                $"{AutoGet} from {ctx.Sender}: '{name}' (#{number}) for this character: "
                + $"auto-get {OnOff(before.AutoCollect)} -> {OnOff(after.AutoCollect)}, "
                + $"max {MaxText(before.Max)} -> {MaxText(after.Max)}");
        ctx.Reply(Describe(name, after));
    }

    // What @auto-get <item> asked for: the typed name, and the switch and max
    // read off its end.
    private readonly record struct ItemForm(string Item, bool? AutoCollect, bool SetMax, int? Max);

    // `on` / `off` and `max <n>` / `max none` are read off the end, in either
    // order, and the rest is the item's name. A trailing `max` with no value, or
    // one that is neither a whole number nor `none`, is refused rather than taken
    // as part of the name.
    private static bool TryParseItemForm(IReadOnlyList<string> args, out ItemForm form, out string? problem)
    {
        form = default;
        problem = null;
        int end = args.Count;
        bool? autoCollect = null;
        bool setMax = false;
        int? max = null;
        for (int pass = 0; pass < 2 && end > 0; pass++)
        {
            if (autoCollect is null && TryParseOnOff(args[end - 1], out bool on))
            {
                autoCollect = on;
                end--;
                continue;
            }
            if (setMax) break;
            if (IsMaxWord(args[end - 1]))
            {
                problem = MaxMissing;
                return false;
            }
            if (end < 2 || !IsMaxWord(args[end - 2])) break;
            if (!TryParseMax(args[end - 1], out max))
            {
                problem = $"{BadMaxOpening}{WireText.Shortened(args[end - 1])}': give a number or none";
                return false;
            }
            setMax = true;
            end -= 2;
        }

        string item = string.Join(' ', args.Take(end)).Trim();
        if (item.Length == 0)
        {
            problem = ItemUsage;
            return false;
        }
        form = new ItemForm(item, autoCollect, setMax, max);
        return true;
    }

    private static bool IsMaxWord(string word) =>
        string.Equals(word, "max", StringComparison.OrdinalIgnoreCase);

    // A whole number of copies from 0 to MaxCap, the values the auto-get engine
    // counts Max to get in; `none` clears it. The cap keeps a number from reading
    // back as no limit.
    private const int MaxCap = 9999;

    private static bool TryParseMax(string word, out int? max)
    {
        max = null;
        if (string.Equals(word, "none", StringComparison.OrdinalIgnoreCase)) return true;
        if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n <= MaxCap)
        {
            max = n;
            return true;
        }
        return false;
    }

    private const string ItemUsage = "usage: @auto-get [on|off] or @auto-get <item> [on|off] [max <n>|none]";
    private const string NoCharacter = "no character loaded";
    private const string NoItemOpening = "no item named '";
    private const string AmbiguousOpening = "item name '";
    private const string BadMaxOpening = "bad max '";
    private const string MaxMissing = "max needs a number or none";

    private static string Describe(string name, AutoGetItemRules.State state) =>
        $"{WireText.Ascii(name)}: auto-get {OnOff(state.AutoCollect)}, max {MaxText(state.Max)}"
        + (state.CannotBeTaken ? " (cannot be taken)" : "")
        + (state.CarryOnlyOne ? " (carry only one)" : "");

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string MaxText(int? max) =>
        max is { } n ? n.ToString(CultureInfo.InvariantCulture) : "none";

    // The refusal for a name that fits several items, with as many of their names
    // as fit one telepath.
    private static string AmbiguousItem(string typed, IReadOnlyList<int> found, AutoGetItemRules rules) =>
        WireText.Listed($"{AmbiguousOpening}{WireText.Shortened(typed)}' matches {found.Count}",
            found.Select(number => $"'{WireText.Ascii(rules.NameOf(number))}'").ToList());

    private void HandleAll(RemoteCommandContext ctx)
    {
        if (_profile.Current is null)
        {
            ctx.Refuse("?");
            return;
        }

        string by = $"@auto-all from {ctx.Sender}";
        if (ctx.Args.Count == 0)
        {
            _controller.ToggleAll(by);
        }
        else if (TryParseOnOff(ctx.Args[0], out bool wanted))
        {
            if (wanted) _controller.TurnOn(by);
            else _controller.TurnOff(by);
        }
        else
        {
            ctx.Refuse("?");
            return;
        }

        string state = _controller.KillSwitchEngaged ? "off" : "on";
        _log?.Log(LogSeverity.Info, LogCategory, $"@auto-all from {ctx.Sender}: {state}");
        ctx.Reply($"@auto-all: {state}");
    }

    private void HandleSettings(RemoteCommandContext ctx)
    {
        // A query always answers, even with no profile loaded — defaults
        // (everything off) are the truthful report in that case.
        GeneralSettings general = _profile.Current is { } p ? ReadGeneral(p) : new GeneralSettings();

        // One entry per engine, in Mapping order.
        IEnumerable<string> parts = Mapping
            .Select(m => $"{Label(m.Cmd)}: {(m.Get(general.AutoMode) ? "On" : "Off")}");
        ctx.Reply(string.Join(", ", parts));
    }

    // "@auto-combat" → "Auto-Combat" for the @settings report.
    private static string Label(string cmd)
    {
        string[] words = cmd.TrimStart('@').Split('-');
        for (int i = 0; i < words.Length; i++)
            if (words[i].Length > 0)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join('-', words);
    }

    private static bool TryParseOnOff(string arg, out bool wanted)
    {
        if (string.Equals(arg, "on",  StringComparison.OrdinalIgnoreCase)) { wanted = true;  return true; }
        if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)) { wanted = false; return true; }
        wanted = false;
        return false;
    }

    private static GeneralSettings ReadGeneral(CharacterProfile profile) => SettingsSection.Read<GeneralSettings>(profile, TabKey);

    // False when the stored section can't be read and so was left as it is: nothing
    // changed, and the engines still run on its defaults.
    private bool WriteGeneral(CharacterProfile profile, GeneralSettings general)
    {
        if (!SettingsSection.Write(profile, TabKey, general, "a remote auto-mode command")) return false;
        _profile.Save();
        return true;
    }

    // The refusal when the change can't be stored (WriteGeneral false).
    private const string SettingsUnreadable = "my General settings can't be read; change it in Settings";
}
