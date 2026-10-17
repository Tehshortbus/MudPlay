using System;
using System.Collections.Generic;
using System.Text;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins PathItemSummonRouter's detour FSM: on a path-item need a room command can
// conjure a guaranteed dropper for, walk to the summon room, type the command,
// wait out the fight, re-survey the floor on the kill, and resume once the drop
// lands — with graceful fail-outs (feature off, engine walk, a cheaper source, an
// unreachable room, a kill that never comes, user takeover).
public sealed class PathItemSummonRouterTests
{
    private static readonly RoomKey Cur = new(8, 400);
    private static readonly RoomKey Dest = new(8, 1698);
    private static readonly RoomKey GateRoom = new(8, 461);    // Black Steel Gate
    private static readonly RoomKey FarRoom = new(8, 900);

    private const int GateKey = 806;

    private static string Decode(byte[] b) => Encoding.Latin1.GetString(b).TrimEnd('\r');

    private static Need PathNeed(int id, int qty = 1)
        => new(NeedKind.PathItem, id.ToString(), "test", DateTimeOffset.Now, qty);

    // The ONLY shape MonsterDeathWatcher ever emits: a kill inferred from the
    // generic "exp gain + *Combat Off*" pair, carrying no identity at all (the
    // per-monster death lines it used to match were retired as unmatchable). Tests
    // must not construct an attributed death — asserting on one would pin a
    // guarantee production cannot deliver.
    private static MonsterDeathEvent Died()
        => new(1000, DateTimeOffset.Now);

    // The other shape: a kill of our own room spell, raised on its exp line. It names
    // nobody either, only the kinds of monster the room listed.
    private static MonsterDeathEvent DiedUnderRoomSpell(DateTimeOffset at)
        => new(1000, at,
            RoomSpellRoster: new[] { new MonsterDeathIdentity(7, "obsidian statue") });

    private sealed class Harness
    {
        // The router's timeout timers.
        public readonly ManualTimers Timers = new();
        public readonly Dictionary<int, List<SummonSource>> Sources = new();
        public readonly Dictionary<(RoomKey From, RoomKey To), int> Dist = new();
        public readonly Dictionary<int, int> Carried = new();
        public readonly Dictionary<int, string> Names = new() { [GateKey] = "gate key" };
        public RoomKey? Current = Cur;
        public RoomKey? WalkDest = Dest;
        public bool Enabled = true;
        public bool EngineWalk;
        public bool CheaperSource;
        public bool SiblingDetour;
        public readonly List<RoomKey> Walks = new();

        public void Carry(int id, int n = 1) => Carried[id] = n;

        private readonly List<byte[]> _sent = new();
        public IReadOnlyList<byte[]> Sent => _sent;

        public PathItemSummonRouter Build()
        {
            var r = new PathItemSummonRouter(
                summonSourcesForItem: id => Sources.TryGetValue(id, out List<SummonSource>? s)
                    ? s
                    : (IReadOnlyList<SummonSource>)Array.Empty<SummonSource>(),
                cheaperSourceExists: _ => CheaperSource,
                siblingDetourActive: () => SiblingDetour,
                currentRoom: () => Current,
                walkDestination: () => WalkDest,
                distanceBetween: (a, b) => Dist.TryGetValue((a, b), out int d) ? d : null,
                carriedCount: id => Carried.TryGetValue(id, out int c) ? c : 0,
                itemName: id => Names.TryGetValue(id, out string? n) ? n : null,
                isEnabled: _ => Enabled,
                engineWalkActive: () => EngineWalk,
                walkTo: Walks.Add,
                post: a => a(),                            // synchronous in tests
                log: null,
                schedule: Timers.Arm);
            r.SetWireSender(_sent.Add);
            return r;
        }

        // The reported case: `touch statue` in 8/461 summons the obsidian statue,
        // which drops the gate key outright. Three steps out, four on to dest.
        public Harness WithGateStatue()
        {
            Sources[GateKey] = new List<SummonSource>
            {
                new(GateRoom, "touch statue", "obsidian statue"),
            };
            Dist[(Cur, GateRoom)] = 3;
            Dist[(GateRoom, Dest)] = 4;
            return this;
        }
    }

    private static WalkEvent Finished(RoomKey dest) => new(WalkEventKind.Finished, "reached", dest);
    private static WalkEvent Failed(RoomKey dest) => new(WalkEventKind.Failed, "no path", dest);
    private static WalkEvent Stopped() => new(WalkEventKind.Stopped, "user walk", null);
    private static WalkEvent Started() => new(WalkEventKind.Started, "user walk", null);

    [Fact]
    public void OnNeedPosted_SummonSourceExists_DetoursToSummonRoom()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();

        r.OnNeedPosted(PathNeed(GateKey));

        Assert.True(r.DetourActive);
        Assert.Equal(GateRoom, Assert.Single(h.Walks));
        Assert.Equal("obsidian statue", r.PendingMonsterName);
    }

    [Fact]
    public void ArrivingAtSummonRoom_TypesTheCommandVerbatim()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));

        r.OnWalkEvent(Finished(GateRoom));

        // The summon, then a bare CR (Enter) to redisplay the room, so the roster
        // sees what arrived and auto-combat can engage it.
        Assert.Equal(new[] { "touch statue", "" }, h.Sent.Select(Decode).ToArray());
    }

    // The drop is not announced on the death line, so the floor has to be
    // re-surveyed before PathItemFloorCollector can see and `get` it.
    [Fact]
    public void KillReSurveysTheFloor()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));
        r.OnWalkEvent(Finished(GateRoom));

        r.OnMonsterDied(Died());

        // summon, bare-CR roster redisplay, then the bare-CR floor survey.
        Assert.Equal(new[] { "touch statue", "", "" }, h.Sent.Select(Decode).ToArray());
    }

    // Deaths carry no identity, so a death must never be taken as OUR kill and end
    // the wait. An unrelated kill in the room (a wanderer, a party member's mob)
    // costs one re-survey and nothing else — abandoning here would walk away with
    // the 300 HP statue still alive and the key never dropped.
    [Fact]
    public void UnrelatedKill_ReSurveysButKeepsWaiting()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));
        r.OnWalkEvent(Finished(GateRoom));

        r.OnMonsterDied(Died());       // someone else's kill — nothing lands

        Assert.True(r.DetourActive);
        Assert.Equal("obsidian statue", r.PendingMonsterName);

        r.OnMonsterDied(Died());       // ours, this time
        h.Carry(GateKey);
        r.OnInventoryChanged();

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
    }

    // A room spell's kills are a death each, all in one burst. One look at the floor
    // covers the burst; a look per kill would spend the allowance in two rounds.
    [Fact]
    public void RoomSpellKillsOfOneBurst_ReSurveyOnce()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));
        r.OnWalkEvent(Finished(GateRoom));
        int before = h.Sent.Count;
        DateTimeOffset round = DateTimeOffset.Now;

        for (int i = 0; i < 4; i++) r.OnMonsterDied(DiedUnderRoomSpell(round));
        Assert.Equal(before + 1, h.Sent.Count);

        // The next round's kills are another burst, and another look.
        for (int i = 0; i < 3; i++) r.OnMonsterDied(DiedUnderRoomSpell(round.AddSeconds(5)));
        Assert.Equal(before + 2, h.Sent.Count);
    }

    // A room being ground by a party would otherwise turn every kill into a `look`.
    [Fact]
    public void ReSurveysAreCapped()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));
        r.OnWalkEvent(Finished(GateRoom));

        for (int i = 0; i < 50; i++) r.OnMonsterDied(Died());

        int looks = h.Sent.Count(b => Decode(b).Length == 0);
        Assert.InRange(looks, 1, 10);
        Assert.True(r.DetourActive);   // capping the surveys must not end the detour
    }

    [Fact]
    public void KeyLandsAfterKill_ResumesToDestination()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));
        r.OnWalkEvent(Finished(GateRoom));
        r.OnMonsterDied(Died());

        h.Carry(GateKey);
        r.OnInventoryChanged();

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
        Assert.Null(r.PendingMonsterName);
    }

    // Found en route (search, a party hand-off) aborts the detour before the fight.
    [Fact]
    public void KeyFoundWhileWalking_AbandonsDetourAndResumes()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));

        h.Carry(GateKey);
        r.OnInventoryChanged();

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
        Assert.Empty(h.Sent);        // never summoned anything
    }

    [Fact]
    public void KillThatNeverComes_ResumesOnTimeout()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));
        r.OnWalkEvent(Finished(GateRoom));

        h.Timers.RunAll();

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
    }

    [Fact]
    public void DropThatNeverLands_ResumesOnTimeout()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));
        r.OnWalkEvent(Finished(GateRoom));
        r.OnMonsterDied(Died());

        h.Timers.RunAll();

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
    }

    [Fact]
    public void UnreachableSummonRoom_ResumesToDestination()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));

        r.OnWalkEvent(Failed(GateRoom));

        Assert.False(r.DetourActive);
        Assert.Equal(Dest, h.Walks[^1]);
    }

    [Fact]
    public void UserTakeoverWhileWalking_AbandonsQuietly()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));

        r.OnWalkEvent(Stopped());

        Assert.False(r.DetourActive);
        Assert.Single(h.Walks);      // only the detour walk; no resume forced on the user
    }

    // Combat is not ours to interrupt — a redirect mid-fight drops the DETOUR only.
    [Fact]
    public void UserRedirectDuringFight_AbandonsDetourOnly()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));
        r.OnWalkEvent(Finished(GateRoom));

        r.OnWalkEvent(Started());

        Assert.False(r.DetourActive);
        Assert.Equal(new[] { "touch statue", "" }, h.Sent.Select(Decode).ToArray());
    }

    // Every router redirects with supersedeSilently, so a sibling detour stealing
    // our walk fires no Stopped — the only signal is a Finished for somewhere else.
    // Without treating that as an abandon the detour sits in WalkingToSummon until
    // the deadline, and DetourActive keeps the one door-key source shut the whole
    // time.
    [Fact]
    public void WalkSupersededBySibling_AbandonsInsteadOfStranding()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));

        r.OnWalkEvent(Finished(new RoomKey(1, 999)));   // a sibling's destination

        Assert.False(r.DetourActive);
        Assert.Empty(h.Sent);   // never typed the summon somewhere it doesn't work
    }

    // The walk phase is inside the same deadline, so a silently-superseded walk
    // that never reports at all still releases.
    [Fact]
    public void WalkThatNeverReports_ReleasesOnTimeout()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));

        h.Timers.RunAll();

        Assert.False(r.DetourActive);
    }

    // A route needing several items posts one need per item back-to-back; if a
    // sibling already owns the walk, claiming a second item would have both drive
    // walkTo and strand the loser.
    [Fact]
    public void SiblingDetourAlreadyRunning_StandsDown()
    {
        var h = new Harness().WithGateStatue();
        h.SiblingDetour = true;
        PathItemSummonRouter r = h.Build();

        r.OnNeedPosted(PathNeed(GateKey));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void NoSummonSource_DoesNothing()
    {
        var h = new Harness();       // black-star-key shape: nothing summons its dropper
        PathItemSummonRouter r = h.Build();

        r.OnNeedPosted(PathNeed(GateKey));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    // A free give or a shop buy costs no combat and wins outright.
    [Fact]
    public void CheaperSourceExists_StandsDown()
    {
        var h = new Harness().WithGateStatue();
        h.CheaperSource = true;
        PathItemSummonRouter r = h.Build();

        r.OnNeedPosted(PathNeed(GateKey));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void FeatureOff_DoesNothing()
    {
        var h = new Harness().WithGateStatue();
        h.Enabled = false;
        PathItemSummonRouter r = h.Build();

        r.OnNeedPosted(PathNeed(GateKey));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    // A loop / auto-lair run owns movement — the need is left to search.
    [Fact]
    public void EngineWalkActive_DoesNothing()
    {
        var h = new Harness().WithGateStatue();
        h.EngineWalk = true;
        PathItemSummonRouter r = h.Build();

        r.OnNeedPosted(PathNeed(GateKey));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    [Fact]
    public void AlreadyCarryingTheKey_DoesNothing()
    {
        var h = new Harness().WithGateStatue();
        h.Carry(GateKey);
        PathItemSummonRouter r = h.Build();

        r.OnNeedPosted(PathNeed(GateKey));

        Assert.False(r.DetourActive);
        Assert.Empty(h.Walks);
    }

    // Fewest added steps wins: the far room is nearer to us but much further from
    // the destination, so the gate room's 3+4 beats its 1+20.
    [Fact]
    public void PicksSourceMinimisingAddedSteps()
    {
        var h = new Harness().WithGateStatue();
        h.Sources[GateKey].Add(new SummonSource(FarRoom, "touch statue", "obsidian statue"));
        h.Dist[(Cur, FarRoom)] = 1;
        h.Dist[(FarRoom, Dest)] = 20;
        PathItemSummonRouter r = h.Build();

        r.OnNeedPosted(PathNeed(GateKey));

        Assert.Equal(GateRoom, Assert.Single(h.Walks));
    }

    [Fact]
    public void UnreachableCandidatesAreSkipped()
    {
        var h = new Harness();
        h.Sources[GateKey] = new List<SummonSource>
        {
            new(FarRoom, "touch statue", "obsidian statue"),   // no distances → unreachable
            new(GateRoom, "touch statue", "obsidian statue"),
        };
        h.Dist[(Cur, GateRoom)] = 3;
        h.Dist[(GateRoom, Dest)] = 4;
        PathItemSummonRouter r = h.Build();

        r.OnNeedPosted(PathNeed(GateKey));

        Assert.Equal(GateRoom, Assert.Single(h.Walks));
    }
    // Report paradigm-20260911-112005: the summon directive carries no message of
    // its own, so whether the room re-renders after it is up to the engine — and in
    // the observed run the statue swung BEFORE any redisplay named it. A bare CR
    // (Enter) is what makes the roster reliable rather than incidental.
    [Fact]
    public void SummonIsFollowedByARoomReRead()
    {
        var h = new Harness().WithGateStatue();
        PathItemSummonRouter r = h.Build();
        r.OnNeedPosted(PathNeed(GateKey));

        r.OnWalkEvent(Finished(GateRoom));

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal(string.Empty, Decode(h.Sent[1]));   // bare CR, after the summon
    }

}
