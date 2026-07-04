using FujinTerm.Game.Remote;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// IL-scans the compiled FujinTerm assembly to prove <see cref="RemoteCommandCatalog"/>
/// and the registered handlers stay in lockstep: every catalog command has
/// a handler wired somewhere in <c>FujinTerm.Game.Remote</c>, and no handler
/// registers a command the catalog doesn't know. A catalog entry with no
/// handler would answer an inbound <c>@foo</c> with an unknown-command
/// denial instead of doing anything, and the drift is invisible at compile
/// time — the catalog and the handlers live in separate files.
/// </summary>
/// <remarks>
/// <para>
/// Why an IL scan rather than constructing every handler: the handlers'
/// constructors pull most of the app object graph (RoomGraphManager,
/// EquipmentManager, ProfileService, TrainerWalkManager, the hangup/relog
/// signals, …), so a construct-all harness would re-assemble AppServices and
/// rot on every new dependency. Instead we read what the code literally
/// registers — matching the <see cref="SingleWriterInvariantTests"/> Cecil
/// style.
/// </para>
/// <para>
/// Every command name reaches the engine as an <c>ldstr</c> in its handler's
/// constructor — the direct <c>RegisterHandler("@x", …)</c> and the private
/// <c>Register("@x", …)</c> wrapper alike put the literal in <c>.ctor</c>,
/// while the looped registrations pull theirs from a <c>.cctor</c>-built
/// static table (the <c>RegisteredCommands</c> arrays and
/// AutoModeRemoteHandler's <c>Mapping</c>). Collecting the command-shaped
/// string literals from those two constructor kinds — minus the catalog's
/// own <c>.cctor</c>, which lists every key — reproduces the registered set
/// without instantiating anything. Constructors never build reply / help
/// prose, and the <see cref="LooksLikeCommand"/> filter drops any multi-word
/// string regardless, so stray <c>@</c>-bearing text can't contaminate the
/// scan. Assumption: handlers register in their constructors (they all do);
/// a future handler that registers from some Init() method would surface
/// here as a false "missing handler" and should either move registration
/// into its constructor or extend this scan.
/// </para>
/// </remarks>
public sealed class RemoteCommandHandlerCoverageTests
{
    [Fact]
    public void EveryCatalogCommandHasARegisteredHandler()
    {
        (HashSet<string> exact, HashSet<string> prefixes) = ScanRegisteredCommands();

        List<string> missing = new();
        foreach (string command in RemoteCommandCatalog.Map.Keys)
            if (!IsCovered(command, exact, prefixes))
                missing.Add(command);

        Assert.True(missing.Count == 0,
            "Catalog commands with no registered handler:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void NoHandlerRegistersACommandOutsideTheCatalog()
    {
        (HashSet<string> exact, HashSet<string> prefixes) = ScanRegisteredCommands();

        List<string> orphans = new();
        foreach (string command in exact)
            if (!RemoteCommandCatalog.Map.ContainsKey(command)) orphans.Add(command);
        foreach (string prefix in prefixes)
            if (!RemoteCommandCatalog.Map.ContainsKey(prefix[..^1])) orphans.Add(prefix);

        Assert.True(orphans.Count == 0,
            "Handlers registered for commands absent from the catalog:\n  "
            + string.Join("\n  ", orphans));
    }

    private static bool IsCovered(string command, HashSet<string> exact, HashSet<string> prefixes)
    {
        if (exact.Contains(command)) return true;
        // A prefix registration (e.g. "@equip-") covers the catalog's bare
        // stem ("@equip") and every suffixed form.
        foreach (string prefix in prefixes)
            if (command == prefix[..^1] || command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static (HashSet<string> Exact, HashSet<string> Prefixes) ScanRegisteredCommands()
    {
        string assemblyPath = typeof(RemoteCommandCatalog).Assembly.Location;
        using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(assemblyPath);

        HashSet<string> literals = new(StringComparer.OrdinalIgnoreCase);
        foreach (TypeDefinition type in asm.MainModule.GetTypes())
        {
            if (type.Namespace != "FujinTerm.Game.Remote") continue;
            // The catalog's own .cctor lists every command as an ldstr —
            // scanning it would make the coverage check vacuous.
            if (type.Name == nameof(RemoteCommandCatalog)) continue;

            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.IsConstructor || !method.HasBody) continue;   // .ctor + .cctor only
                foreach (Instruction instr in method.Body.Instructions)
                {
                    if (instr.OpCode != OpCodes.Ldstr) continue;
                    if (instr.Operand is string s && LooksLikeCommand(s))
                        literals.Add(s);
                }
            }
        }

        Assert.NotEmpty(literals);   // sanity — the scan actually found the handlers.

        HashSet<string> prefixes = new(literals.Where(s => s.EndsWith('-')), StringComparer.OrdinalIgnoreCase);
        HashSet<string> exact = new(literals.Where(s => !s.EndsWith('-')), StringComparer.OrdinalIgnoreCase);
        return (exact, prefixes);
    }

    /// <summary>
    /// A command literal is an <c>@</c> followed by non-whitespace — the
    /// shape every catalog key and prefix uses. Reply / help prose is
    /// multi-word, so it never matches.
    /// </summary>
    private static bool LooksLikeCommand(string s) =>
        s.Length >= 2 && s[0] == '@' && !s.Any(char.IsWhiteSpace);
}
