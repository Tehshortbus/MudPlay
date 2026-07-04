using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Info tab. Single-row realm metadata exposed by the MDB — version
// stamps, build date / time, custom-realm flag, the Legit / DisableKai gameplay-mode bits,
// and the update URL the MDB curator publishes. Read-only; users curious which dat-file
// version they're browsing can confirm it here.
public sealed class InfoSectionViewModel : JsonTableSectionViewModel
{
    public override string Id => "info";
    public override string Title => "Info";

    protected override string TableName => "Info";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "NMR Version",
        "Dat File Version",
        "Date",
        "Time",
        "Custom",
        "Legit",
        "DisableKai",
        "UpdateURL",
    };

    public override string SearchKeyColumn => "Dat File Version";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "info", "version", "metadata", "realm",
    };

    public InfoSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null) : base(cache, resolver) { }
}
