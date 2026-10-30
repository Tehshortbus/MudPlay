using System.Text.Json;
using MudPlay.Game.Quests;
using MudPlay.Services;

namespace MudPlay.Game.Gmcp;

// Contract 1.2.0's quest names: the realm's own name for each quest flag, handed to
// the quest store as a layer of its own (QuestStore.ApplyServerNames), never into the
// user's guides. No text path reads quest names, so there is nothing for Shadow to
// compare or stand aside for: the names apply in Shadow as in Active, and only Off
// ignores them. They go with the session (a full reset, a new server, a disconnect),
// so one realm's names never show on another.
public sealed partial class GmcpIntegration
{
    private const string QuestNamesPackage = "Anomaly.Quests.Names";

    private QuestStore? _quests;

    public void BindQuests(QuestStore quests) => _quests = quests;

    private void ApplyQuestNames(string package)
    {
        if (_quests is null || package is not ("*" or QuestNamesPackage)) return;
        try
        {
            _quests.ApplyServerNames(
                State.Compatible && State.InSession && State.Mode(QuestNamesPackage) != GmcpMode.Off
                && State.Snapshot(QuestNamesPackage) is { } value
                    ? QuestNames(value)
                    : Array.Empty<ServerQuestName>());
        }
        catch (Exception ex) when (IsApplyFault(ex))
        {
            // Off re-applies the package, which drops the names held.
            _log.Warn("GMCP", $"Cannot apply {QuestNamesPackage}: {ex.Message}; dropping the realm's quest names.");
            State.SetMode(QuestNamesPackage, GmcpMode.Off);
        }
    }

    private static List<ServerQuestName> QuestNames(JsonElement value) =>
        value.GetProperty("quests").EnumerateArray()
            .Select(q => new ServerQuestName(
                q.GetProperty("flag").GetInt32(),
                q.GetProperty("from").GetInt32(),
                q.GetProperty("to").ValueKind == JsonValueKind.Null ? null : q.GetProperty("to").GetInt32(),
                q.GetProperty("name").GetString()!.Trim()))
            .Where(q => q.Name.Length > 0)
            .ToList();
}
