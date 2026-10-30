using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.GameData;
using MudPlay.Game.Quests;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// The @quest <name|flag> remote command (QueryQuests category). Reports the sender's
// character's marked-complete quest state, and — on Paradigm (built-in `abil`) or a
// stock board with sys-god access (`sys god <name> abil`) — the live quest-flag step
// read off the game. On stock without sys-god access it reports only the marked state.
//
//   @quest                 → every quest with a marked-complete band, grouped by flag.
//   @quest good align      → resolves the alias to flag 126, reports its marked bands
//                            (by ordinal) plus the live step, e.g.
//                            "Good align 1, 2, 3 marked complete. Abil: 126 step 16".
//   @quest 126             → same, by flag number.
//   @quest update          → read every flag this character's quests use and mark the
//                            completed ones, then report what's marked.
//
// A live read also MARKS what it proves (QuestFlagSyncManager.MarkObserved): a flag at a
// band's complete value means that band is done, so the reply reflects the fresh marks —
// "Good align: none marked complete. Abil: 126 step 6" becomes "Good align 1 marked
// complete …" once step 6 is read.
//
// The live read reuses QuestFlagProbe (its own instance so an in-flight daily
// QuestFlagSync never clobbers this read), and the realm/gate selection mirrors
// QuestFlagSyncManager: ParaMud → abil, else sys-god → sys god, else marked-only.
public sealed class QuestQueryHandler : IDisposable
{
    private const int MaxFlagsShown = 12;

    private readonly RemoteCommandManager _engine;
    private readonly Func<CharacterProfile?> _profile;
    private readonly QuestStore _quests;
    private readonly QuestFlagProbe _probe;
    private readonly QuestFlagSyncManager _sync;
    private readonly Func<bool> _isParadigm;
    private readonly Func<bool> _canStockRead;
    private readonly Func<string?> _characterName;
    private readonly LogService? _log;
    private bool _disposed;

    public QuestQueryHandler(
        RemoteCommandManager engine,
        Func<CharacterProfile?> profile,
        QuestStore quests,
        QuestFlagProbe probe,
        QuestFlagSyncManager sync,
        Func<bool> isParadigm,
        Func<bool> canStockRead,
        Func<string?> characterName,
        LogService? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _quests = quests ?? throw new ArgumentNullException(nameof(quests));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _isParadigm = isParadigm ?? throw new ArgumentNullException(nameof(isParadigm));
        _canStockRead = canStockRead ?? throw new ArgumentNullException(nameof(canStockRead));
        _characterName = characterName ?? throw new ArgumentNullException(nameof(characterName));
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory("@quest", out PlayerRemoteControls category))
            throw new InvalidOperationException(
                "RemoteCommandCatalog missing entry for '@quest'. Add it to the Map before registering.");
        _engine.RegisterHandler("@quest", category, OnQuest);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@quest");
    }

    private void OnQuest(RemoteCommandContext ctx)
    {
        // The engine only routes here when the sender holds QueryQuests, so the grant
        // is the gate — no separate opt-in check.
        List<QuestProgress> log = _profile()?.QuestLog ?? new List<QuestProgress>();

        string query = string.Join(' ', ctx.Args).Trim();
        if (query.Length == 0)
        {
            ctx.Reply(QuestQueryReport.FormatAll(log, _quests));
            return;
        }

        if (string.Equals(query, "update", StringComparison.OrdinalIgnoreCase))
        {
            _ = UpdateAndReplyAsync(ctx);
            return;
        }

        int? resolved = QuestNameResolver.Resolve(query, BuildCandidates(log));
        if (resolved is not int flag)
        {
            ctx.Refuse($"no quest matches \"{query}\"");
            return;
        }

        string label = QuestNameResolver.Label(flag, _quests.FlagName(flag, log));
        string marked = QuestQueryReport.FormatFlag(flag, label, log, _quests.StepsForFlag(flag));

        // Pick the live-read path exactly as QuestFlagSyncManager does. Stock without
        // sys-god access can't read flags, so reply with the marked state alone.
        if (_isParadigm())
            _ = ProbeAndReplyAsync(ctx, flag, marked, paradigm: true);
        else if (_canStockRead())
            _ = ProbeAndReplyAsync(ctx, flag, marked, paradigm: false);
        else
            ctx.Reply(marked);
    }

    private async Task ProbeAndReplyAsync(RemoteCommandContext ctx, int flag, string marked, bool paradigm)
    {
        IReadOnlyDictionary<int, int> observed;
        try
        {
            observed = paradigm
                ? await _probe.ReadParadigmAsync(new[] { flag }).ConfigureAwait(true)
                : await _probe.ReadStockAsync(_characterName() ?? string.Empty).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log?.Warn("QuestFlags", $"@quest live read for flag {flag} failed: {ex.Message}");
            ctx.Reply(marked);
            return;
        }

        if (!observed.TryGetValue(flag, out int step))
        {
            ctx.Reply($"{marked}. Abil: {flag} unavailable");
            return;
        }

        // The read proves what's done — mark it, then report the updated state.
        int newly = _sync.MarkObserved(observed).Count;
        List<QuestProgress> log = _profile()?.QuestLog ?? new List<QuestProgress>();
        string label = QuestNameResolver.Label(flag, _quests.FlagName(flag, log));
        string now = QuestQueryReport.FormatFlag(flag, label, log, _quests.StepsForFlag(flag));
        ctx.Reply(newly > 0
            ? $"{now}. Abil: {flag} step {step} ({newly} newly marked)"
            : $"{now}. Abil: {flag} step {step}");
    }

    private async Task UpdateAndReplyAsync(RemoteCommandContext ctx)
    {
        QuestFlagSyncManager.UpdateResult result;
        try
        {
            result = await _sync.UpdateNowAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log?.Warn("QuestFlags", $"@quest update failed: {ex.Message}");
            ctx.Refuse("quest flag read failed");
            return;
        }

        List<QuestProgress> log = _profile()?.QuestLog ?? new List<QuestProgress>();
        switch (result.Outcome)
        {
            case QuestFlagSyncManager.UpdateOutcome.Done:
                ctx.Reply($"{result.FlagsRead} flag(s) read, {result.Marked.Count} newly marked"
                    + (result.Progressed > 0 ? $", {result.Progressed} in progress advanced" : "")
                    + " - " + QuestQueryReport.FormatAll(log, _quests));
                break;
            case QuestFlagSyncManager.UpdateOutcome.NothingToCheck:
                ctx.Reply("nothing to check - " + QuestQueryReport.FormatAll(log, _quests));
                break;
            case QuestFlagSyncManager.UpdateOutcome.NoAccess:
                ctx.Refuse("can't read quest flags here (stock needs sys-god access)");
                break;
            case QuestFlagSyncManager.UpdateOutcome.Busy:
                ctx.Refuse("a quest flag read is already running");
                break;
        }
    }

    // Candidate (flag, name) pairs the name resolver matches against: every named quest
    // in the store, plus each flag the character has progress on named via AbilityNames
    // — so "goodquest"/a user-named quest resolves even when it isn't in the alias table.
    private IEnumerable<(int Flag, string Name)> BuildCandidates(List<QuestProgress> log)
    {
        foreach ((int Flag, string Name) named in _quests.NamedQuests())
            yield return named;
        foreach (int flag in log.Select(p => p.Flag).Distinct())
            if (AbilityNames.GetName(flag) is { Length: > 0 } n)
                yield return (flag, n);
    }
}
