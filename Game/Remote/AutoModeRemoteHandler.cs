using System.Text.Json;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// The @auto-* family. A party member's @auto-combat off flips our
// AutoActionDefaults.AutoCombat flag, persists the profile, and replies with the
// new state.
//
// Per-engine grammar: each @auto-X takes an optional first arg of on / off which
// sets that state explicitly. With no arg the flag is toggled. Either way the
// reply echoes the resulting state. Any other arg is rejected with a "?" reply
// (gated on WarnOnDenial).
//
// @auto-rest is an alias for @auto-heal — both drive AutoActionDefaults.AutoHealRest.
//
// @auto-all drives the shared AutoModeController master kill-switch (same session
// snapshot as the toolbar / Action-menu "Auto-All" button). No arg toggles
// (kill → restore); explicit off ensures every engine is off, on restores the
// snapshot. Reply reports whether everything is now off.
//
// All commands require PlayerRemoteControls.AlterSettings per the catalog — a
// "do something on my behalf" tier.
public sealed class AutoModeRemoteHandler : IDisposable
{
    private const string TabKey = "General";
    private const string LogCategory = "RemoteCmd";

    // Mapping from @-command name to flag accessor.
    private static readonly (string Cmd, Func<AutoActionDefaults, bool> Get,
                             Action<AutoActionDefaults, bool> Set)[] Mapping =
    {
        ("@auto-combat", d => d.AutoCombat,   (d, v) => d.AutoCombat   = v),
        ("@auto-nuke",   d => d.AutoNuke,     (d, v) => d.AutoNuke     = v),
        ("@auto-heal",   d => d.AutoHealRest, (d, v) => d.AutoHealRest = v),
        ("@auto-rest",   d => d.AutoHealRest, (d, v) => d.AutoHealRest = v),
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

        if (_profile.Current is not { } profile)
        {
            if (_engine.WarnOnDenial) ctx.Reply("?");
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
            if (_engine.WarnOnDenial) ctx.Reply("?");
            return;
        }

        if (current != wanted)
        {
            set(general.AutoMode, wanted);
            WriteGeneral(profile, general);
            _log?.Log(LogSeverity.Info, LogCategory,
                $"{cmd} from {ctx.Sender}: {(current ? "on" : "off")} -> {(wanted ? "on" : "off")}");
        }
        ctx.Reply($"{cmd}: {(wanted ? "on" : "off")}");
    }

    private void HandleAll(RemoteCommandContext ctx)
    {
        if (_profile.Current is null)
        {
            if (_engine.WarnOnDenial) ctx.Reply("?");
            return;
        }

        if (ctx.Args.Count == 0)
        {
            // No arg → snapshot toggle (same shape as the Auto-All button).
            _controller.ToggleAll();
        }
        else if (TryParseOnOff(ctx.Args[0], out bool wanted))
        {
            // Explicit: on = ensure restored, off = ensure killed.
            if (wanted && _controller.AllWiredOff) _controller.ToggleAll();
            else if (!wanted && !_controller.AllWiredOff) _controller.ToggleAll();
        }
        else
        {
            if (_engine.WarnOnDenial) ctx.Reply("?");
            return;
        }

        string state = _controller.AllWiredOff ? "off" : "on";
        _log?.Log(LogSeverity.Info, LogCategory, $"@auto-all from {ctx.Sender}: {state}");
        ctx.Reply($"@auto-all: {state}");
    }

    private void HandleSettings(RemoteCommandContext ctx)
    {
        // A query always answers, even with no profile loaded — defaults
        // (everything off) are the truthful report in that case.
        GeneralSettings general = _profile.Current is { } p ? ReadGeneral(p) : new GeneralSettings();

        // One entry per engine, in Mapping order. Skip @auto-rest — it's
        // an alias for @auto-heal (same AutoHealRest flag), so listing it
        // would double-report the same engine.
        IEnumerable<string> parts = Mapping
            .Where(m => m.Cmd != "@auto-rest")
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

    private static GeneralSettings ReadGeneral(CharacterProfile profile)
    {
        if (profile.Settings is null) return new GeneralSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new GeneralSettings();
        try { return JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText()) ?? new GeneralSettings(); }
        catch { return new GeneralSettings(); }
    }

    private void WriteGeneral(CharacterProfile profile, GeneralSettings general)
    {
        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(general);
        _profile.Save();
    }
}
