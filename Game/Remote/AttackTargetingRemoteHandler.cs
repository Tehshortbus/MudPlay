using System.Globalization;
using System.Text.Json;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// The two attack-targeting settings commands: @atkprio (Target Priority — the
// "who") and @atkorder (Attack Order — the "when"). Both mirror the Combat tab's
// dropdowns — a bare command reports the current selection plus the numbered
// option list; a numbered arg selects that option; the name-requiring option
// (atkprio 3 / atkorder 4) takes a second arg naming the player to follow.
// Anything else replies "command failed" with the usage list.
//
// Both require PlayerRemoteControls.AlterSettings per the catalog — a "change a
// setting on my behalf" tier. The numbered options are tied 1:1 to the Combat
// tab's ComboBox rows (CombatSectionViewModel's TargetPriorityOptions /
// AttackTimingOptions) so a remote caller and the local UI share one vocabulary.
//
// Writes persist via ProfileService.Save exactly like the Settings tab's Apply —
// the running CombatManager re-reads CombatSettings on the profile-mutated tick.
public sealed class AttackTargetingRemoteHandler : IDisposable
{
    private const string TabKey = "Combat";
    private const string LogCategory = "RemoteCmd";

    // One selectable option — its wire number, the Combat-tab label it maps to,
    // and whether picking it requires a trailing player name.
    private readonly record struct Option(int Num, string Label, bool NeedsName);

    // @atkprio options — tied to CombatSectionViewModel.TargetPriorityOptions.
    private static readonly Option[] PrioOptions =
    {
        new(1, "Default", false),
        new(2, "Attack what party leader attacks", false),
        new(3, "Attack what player attacks", true),
    };

    // @atkorder options — tied to CombatSectionViewModel.AttackTimingOptions.
    private static readonly Option[] OrderOptions =
    {
        new(1, "Default", false),
        new(2, "Attack Last Party", false),
        new(3, "Attack Last Room", false),
        new(4, "Attack After", true),
    };

    private readonly RemoteCommandManager _engine;
    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private bool _disposed;

    public AttackTargetingRemoteHandler(RemoteCommandManager engine, ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(profile);
        _engine = engine;
        _profile = profile;
        _log = log;

        if (RemoteCommandCatalog.TryGetCategory("@atkprio", out PlayerRemoteControls prioCat))
            _engine.RegisterHandler("@atkprio", prioCat, HandlePrio);
        if (RemoteCommandCatalog.TryGetCategory("@atkorder", out PlayerRemoteControls orderCat))
            _engine.RegisterHandler("@atkorder", orderCat, HandleOrder);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@atkprio");
        _engine.UnregisterHandler("@atkorder");
    }

    // ----- @atkprio (Target Priority — WHO) -------------------------------

    private void HandlePrio(RemoteCommandContext ctx)
    {
        CombatSettings dto = ReadCombat();

        if (ctx.Args.Count == 0)
        {
            ctx.Reply(Query("@atkprio", PrioNumber(dto.TargetPriority),
                            dto.TargetPriorityMemberName, PrioOptions));
            return;
        }

        if (!TryParseOption(ctx.Args, PrioOptions, out Option opt, out string? name)
            || _profile.Current is not { } profile)
        {
            Fail("@atkprio", PrioOptions, ctx);
            return;
        }

        dto.TargetPriority = opt.Num switch
        {
            2 => TargetPriority.FollowLeader,
            3 => TargetPriority.FollowMember,
            _ => TargetPriority.Default,
        };
        dto.TargetPriorityMemberName = opt.NeedsName ? name : null;
        WriteCombat(profile, dto);
        _log?.Log(LogSeverity.Info, LogCategory,
            $"@atkprio from {ctx.Sender}: {opt.Num} {opt.Label}{NameSuffix(name)}");
        ctx.Reply(SetReply("@atkprio", opt, name));
    }

    // ----- @atkorder (Attack Order — WHEN) --------------------------------

    private void HandleOrder(RemoteCommandContext ctx)
    {
        CombatSettings dto = ReadCombat();

        if (ctx.Args.Count == 0)
        {
            ctx.Reply(Query("@atkorder", OrderNumber(dto.AttackTiming),
                            dto.AttackAfterPlayerName, OrderOptions));
            return;
        }

        if (!TryParseOption(ctx.Args, OrderOptions, out Option opt, out string? name)
            || _profile.Current is not { } profile)
        {
            Fail("@atkorder", OrderOptions, ctx);
            return;
        }

        dto.AttackTiming = opt.Num switch
        {
            2 => AttackTiming.AttackLastParty,
            3 => AttackTiming.AttackLastRoom,
            4 => AttackTiming.AttackAfter,
            _ => AttackTiming.Default,
        };
        dto.AttackAfterPlayerName = opt.NeedsName ? name : null;
        WriteCombat(profile, dto);
        _log?.Log(LogSeverity.Info, LogCategory,
            $"@atkorder from {ctx.Sender}: {opt.Num} {opt.Label}{NameSuffix(name)}");
        ctx.Reply(SetReply("@atkorder", opt, name));
    }

    // ----- Shared parsing / formatting ------------------------------------

    // Parse the args of a set request. args[0] must be a listed option number.
    // No-name options accept exactly that one token; name options require exactly
    // a second token (the player name). Any other shape — non-numeric, unknown
    // number, missing/extra tokens — is a syntax error (returns false).
    private static bool TryParseOption(IReadOnlyList<string> args, Option[] options,
                                       out Option opt, out string? name)
    {
        opt = default;
        name = null;
        if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
            return false;
        foreach (Option o in options)
        {
            if (o.Num != num) continue;
            opt = o;
            if (o.NeedsName)
            {
                if (args.Count != 2) return false;
                name = args[1].Trim();
                return name.Length > 0;
            }
            return args.Count == 1;
        }
        return false;
    }

    // Bare-command reply: current selection + the numbered options.
    private static string Query(string cmd, int currentNum, string? currentName, Option[] options)
    {
        Option cur = options.First(o => o.Num == currentNum);
        string current = cur.NeedsName && !string.IsNullOrWhiteSpace(currentName)
            ? $"{cur.Num} {cur.Label} ({currentName})"
            : $"{cur.Num} {cur.Label}";
        return $"{cmd}: {current}. Options: {OptionsList(options)}";
    }

    // Successful-set reply: the option just applied.
    private static string SetReply(string cmd, Option opt, string? name) =>
        opt.NeedsName && !string.IsNullOrWhiteSpace(name)
            ? $"{cmd}: {opt.Num} {opt.Label} ({name})"
            : $"{cmd}: {opt.Num} {opt.Label}";

    // Syntax-error reply — gated by the engine's WarnOnDenial master switch so a
    // muted client stays silent.
    private void Fail(string cmd, Option[] options, RemoteCommandContext ctx)
    {
        if (_engine.WarnOnDenial)
            ctx.Reply($"{cmd}: command failed. Options: {OptionsList(options)}");
    }

    private static string OptionsList(Option[] options) =>
        string.Join(", ", options.Select(o =>
            o.NeedsName ? $"{o.Num}={o.Label} <name>" : $"{o.Num}={o.Label}"));

    private static string NameSuffix(string? name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : $" ({name})";

    private static int PrioNumber(TargetPriority p) => p switch
    {
        TargetPriority.FollowLeader => 2,
        TargetPriority.FollowMember => 3,
        _ => 1,
    };

    private static int OrderNumber(AttackTiming t) => t switch
    {
        AttackTiming.AttackLastParty => 2,
        AttackTiming.AttackLastRoom => 3,
        AttackTiming.AttackAfter => 4,
        _ => 1,
    };

    // ----- Profile I/O ----------------------------------------------------

    private CombatSettings ReadCombat()
    {
        if (_profile.Current is not { Settings: { } settings }) return new CombatSettings();
        if (!settings.TryGetValue(TabKey, out JsonElement json)) return new CombatSettings();
        try { return JsonSerializer.Deserialize<CombatSettings>(json.GetRawText()) ?? new CombatSettings(); }
        catch { return new CombatSettings(); }
    }

    private void WriteCombat(CharacterProfile profile, CombatSettings dto)
    {
        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();
    }
}
