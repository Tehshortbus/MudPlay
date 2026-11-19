using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.ViewModels.Settings;

// One selectable source in the BBS section's "import logon steps from another BBS"
// picker: one board's saved logon steps. A step's {username} / {password} resolves
// from the connecting character's own credentials, so nothing secret travels.
public sealed record LogonStepsImportOption(string BbsName, IReadOnlyList<MenuStep> Steps)
{
    // Shown directly in the ComboBox (no ItemTemplate needed); the step count hints
    // how complete it is.
    public override string ToString() =>
        $"{BbsName} ({Steps.Count} step{(Steps.Count == 1 ? "" : "s")})";

    // Every other board with logon steps saved, by name.
    public static IReadOnlyList<LogonStepsImportOption> Build(IEnumerable<BbsProfile> boards, string editingBbs) =>
        boards
            .Where(b => b.LogonSteps is { Count: > 0 }
                        && !string.Equals(b.Name, editingBbs, StringComparison.OrdinalIgnoreCase))
            .Select(b => new LogonStepsImportOption(b.Name, b.LogonSteps!))
            .OrderBy(o => o.BbsName, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
