using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A quest flag read off any `abil` the game answers, not only the client's own sync:
// what the user typed is picked up for a short window, a Stock dump only when it names
// this character, and the reads are kept on the character for the quest cards.
public sealed class QuestFlagReadoutTests : IDisposable
{
    private readonly ServerFeed _feed = new();
    private readonly List<IReadOnlyDictionary<int, int>> _reads = new();
    private DateTimeOffset _now = new(2026, 11, 2, 13, 0, 0, TimeSpan.Zero);
    private bool _clientReading;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-flagreadout-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort cleanup of a temp folder */ }
    }

    private QuestFlagReadout Readout()
    {
        QuestFlagReadout r = new(() => "Masta Blaster", () => _clientReading, _reads.Add, () => _now);
        r.AttachLineExtractor(_feed.Lines);
        return r;
    }

    private static void Send(QuestFlagReadout r, string command) => r.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));

    [Fact]
    public void TypedAbil_ReplyIsRead()
    {
        QuestFlagReadout r = Readout();
        Send(r, "abil 126");
        _feed.Line("GoodQuest(126)             8");
        Assert.Equal(8, Assert.Single(_reads)[126]);
    }

    [Fact]
    public void ReplyShape_WithNoAbilSent_OrPastTheWindow_IsNotRead()
    {
        QuestFlagReadout r = Readout();
        _feed.Line("GoodQuest(126)             8");
        Send(r, "abil 126");
        _now += QuestFlagReadout.ReplyWindow + TimeSpan.FromSeconds(1);
        _feed.Line("GoodQuest(126)             8");
        Assert.Empty(_reads);
    }

    [Fact]
    public void StockDump_IsReadOnlyWhenItNamesThisCharacter()
    {
        QuestFlagReadout r = Readout();
        Send(r, "sys god bob abil");
        _feed.Line("User abilities: 126(17) 2(1)");
        Assert.Empty(_reads);

        Send(r, "sys god masta abil");
        _feed.Line("User abilities: 126(17) 2(1)");
        IReadOnlyDictionary<int, int> read = Assert.Single(_reads);
        Assert.Equal(17, read[126]);
        Assert.Equal(1, read[2]);
    }

    [Fact]
    public void ClientsOwnRead_KeepsItsLines()
    {
        QuestFlagReadout r = Readout();
        _clientReading = true;
        Send(r, "abil 126");
        _feed.Line("GoodQuest(126)             8");
        Assert.Empty(_reads);
    }

    // Every read of a quest's flag is kept with its time, a read of 0 included, and an
    // ability no quest uses is not.
    [Fact]
    public void LiveRead_KeepsEachQuestFlagsValue()
    {
        string dir = Path.Combine(_root, "test-set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Action"] = "giveability 500 3" },
            new Dictionary<string, object> { ["Action"] = "giveability 501 2" },
        }));
        GameDataCache cache = new(_root);
        cache.SwitchSet("test-set");
        ProfileService profile = TestProfiles.Loaded();
        QuestFlagSyncManager sync = new(
            cache, profile, new QuestStore(), new QuestFlagProbe(_ => { }),
            realm: () => RealmType.ParaMud, hasSysopPowers: () => false, characterName: () => "Masta",
            classId: () => null, enabled: () => false, isInRealm: () => true, reannounce: () => { },
            availableQuests: () => Array.Empty<QuestAvailabilityInfo>(), now: () => _now);
        int synced = 0;
        sync.Synced += () => synced++;

        sync.MarkObserved(new Dictionary<int, int> { [500] = 0, [501] = 1, [2] = 780 });

        Dictionary<int, QuestFlagRead> reads = profile.Current!.QuestFlagReads!;
        Assert.Equal(new[] { 500, 501 }, reads.Keys.Order());
        Assert.Equal(0, reads[500].Value);
        Assert.Equal(1, reads[501].Value);
        Assert.Equal(_now, reads[501].At);
        Assert.Equal(1, synced);
    }
}
