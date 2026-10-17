using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The shared completed-quest bonus resolver Monster Intel reads: a completed quest
// in the character's log yields its stat reward, and one not yet completed yields
// none.
public sealed class CompletedQuestBonusesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-questbonus-tests-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // One single-part quest, flag 125: +1 of ability 2 at level 15.
    private GameDataCache Cache()
    {
        string dir = Path.Combine(_root, "test-set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Action"] = "giveability 125 2:minlevel 15:addability 2 1" },
        }));
        GameDataCache cache = new(_root);
        cache.SwitchSet("test-set");
        return cache;
    }

    [Fact]
    public void ACompletedQuest_YieldsItsBonus()
    {
        QuestProgress[] log = { new(125, 0) { Complete = true } };

        Assert.Equal(new[] { new QuestBonus(2, 1) }, CompletedQuestBonuses.Resolve(Cache(), classId: 1, log));
    }

    [Fact]
    public void AQuestNotCompleted_YieldsNothing()
    {
        QuestProgress[] log = { new(125, 0) { Complete = false } };

        Assert.Empty(CompletedQuestBonuses.Resolve(Cache(), classId: 1, log));
    }

    [Fact]
    public void ResolveClassId_NullForBlankName()
        => Assert.Null(CompletedQuestBonuses.ResolveClassId(Cache(), ""));
}
