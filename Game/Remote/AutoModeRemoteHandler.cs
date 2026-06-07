using System.Text.Json;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

/// <summary>
/// Phase 9 Cluster 5d — consumer of <see cref="RemoteCommandManager"/>
/// for the <c>@auto-*</c> family. A party member's <c>@auto-combat off</c>
/// flips our <see cref="AutoActionDefaults.AutoCombat"/> flag, persists
/// the profile, and replies with the new state.
/// </summary>
/// <remarks>
/// <para>
/// Argument grammar: each <c>@auto-X</c> takes an optional first arg
/// of <c>on</c> / <c>off</c>. Empty arg → report the current state.
/// Any other value is rejected with an "?" reply (per the engine's
/// existing failure-reply policy).
/// </para>
/// <para>
/// <c>@auto-all on/off</c> applies the change to every individual flag
/// at once (matches MudProxy semantics). Useful for bulk-on / bulk-off
/// before a fight or when going AFK.
/// </para>
/// <para>
/// All commands require <see cref="PlayerRemoteControls.AlterSettings"/>
/// per the catalog — this is a "do something on my behalf" tier.
/// </para>
/// </remarks>
public sealed class AutoModeRemoteHandler : IDisposable
{
    private const string TabKey = "General";
    private const string LogCategory = "RemoteCmd";

    /// <summary>Mapping from @-command name → flag accessor.</summary>
    private static readonly (string Cmd, Func<AutoActionDefaults, bool> Get,
                             Action<AutoActionDefaults, bool> Set)[] Mapping =
    {
        ("@auto-combat", d => d.AutoCombat,   (d, v) => d.AutoCombat   = v),
        ("@auto-nuke",   d => d.AutoNuke,     (d, v) => d.AutoNuke     = v),
        ("@auto-heal",   d => d.AutoHealRest, (d, v) => d.AutoHealRest = v),
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
    private readonly LogService? _log;
    private bool _disposed;

    public AutoModeRemoteHandler(
        RemoteCommandManager engine,
        ProfileService profile,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(profile);
        _engine = engine;
        _profile = profile;
        _log = log;

        foreach ((string cmd, _, _) in Mapping)
        {
            if (!RemoteCommandCatalog.TryGetCategory(cmd, out PlayerRemoteControls category))
                continue;
            _engine.RegisterHandler(cmd, category, ctx => HandleOne(cmd, ctx));
        }

        // @auto-all toggles every flag at once.
        if (RemoteCommandCatalog.TryGetCategory("@auto-all", out PlayerRemoteControls allCat))
            _engine.RegisterHandler("@auto-all", allCat, HandleAll);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach ((string cmd, _, _) in Mapping) _engine.UnregisterHandler(cmd);
        _engine.UnregisterHandler("@auto-all");
    }

    private void HandleOne(string cmd, RemoteCommandContext ctx)
    {
        (_, Func<AutoActionDefaults, bool> get, Action<AutoActionDefaults, bool> set) =
            Mapping.First(m => m.Cmd == cmd);

        if (_profile.Current is not { } profile)
        {
            ctx.Reply("?");
            return;
        }

        GeneralSettings general = ReadGeneral(profile);

        if (ctx.Args.Count == 0)
        {
            ctx.Reply($"{cmd}: {(get(general.AutoMode) ? "on" : "off")}");
            return;
        }

        if (!TryParseOnOff(ctx.Args[0], out bool wanted))
        {
            ctx.Reply("?");
            return;
        }

        bool current = get(general.AutoMode);
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
        if (_profile.Current is not { } profile)
        {
            ctx.Reply("?");
            return;
        }
        if (ctx.Args.Count == 0)
        {
            ctx.Reply("@auto-all: on or off");
            return;
        }
        if (!TryParseOnOff(ctx.Args[0], out bool wanted))
        {
            ctx.Reply("?");
            return;
        }

        GeneralSettings general = ReadGeneral(profile);
        foreach ((_, _, Action<AutoActionDefaults, bool> set) in Mapping)
            set(general.AutoMode, wanted);
        WriteGeneral(profile, general);

        _log?.Log(LogSeverity.Info, LogCategory,
            $"@auto-all from {ctx.Sender}: {(wanted ? "on" : "off")}");
        ctx.Reply($"@auto-all: {(wanted ? "on" : "off")}");
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
