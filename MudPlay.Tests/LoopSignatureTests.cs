using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// A loop's signature says whether two loops are the same loop: the same rooms in the
// same order, the same settings on each, run with the same two loop-wide settings.
// `@loop send` decides on it whether a loop that arrives is one already held.
public sealed class LoopSignatureTests
{
    private static Loop Known() => new("King's Road", new[]
    {
        new LoopWaypoint(new RoomKey(1, 297), command: "ask barmaid ale", delayMs: 1500),
        new LoopWaypoint(new RoomKey(1, 298), doNotRest: true),
        new LoopWaypoint(new RoomKey(2, 14), command: "say café", delayMs: 250, doNotAttack: true)
        {
            RestHereHp = true, RestHereMana = true,
        },
    })
    {
        OwnSettings = new LoopOwnSettings(true, LairEntryDebuffMode.WaitForPriorityBuffs),
    };

    // The two hashes were worked out apart from the client, from the byte form
    // LoopSignature.cs sets down. If either changes, the form changed: two versions
    // of the client would then disagree on which loops are the same, so a change has
    // to be meant (and takes a new tag in the form).
    [Fact]
    public void KnownLoops_HaveTheSignaturesTheWrittenFormGives()
    {
        Assert.Equal("9da659db9b0d4f2c55a1fee98bf6f1ad54b5ed2f538de2b77b92abcc0af3a859", LoopSignature.Of(Known()));

        Loop bare = new("anything", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });
        Assert.Equal("c40c5515bd5b1716b6113fd87190266786f9cdca2ad48af747afce130533d5a3", LoopSignature.Of(bare));
    }

    [Fact]
    public void ShortCode_IsTheFirstEightCharacters()
    {
        Assert.Equal("9da659db", LoopSignature.ShortOf(Known()));
        Assert.Equal("9da659db", LoopSignature.Short(LoopSignature.Of(Known())));
    }

    // Labels on a loop are not the loop, and neither is what the loop file holds of
    // the two settings: the signature is of the loop as the character runs it.
    [Fact]
    public void NameFolderNotesFavouriteAndTheFilesOwnSettings_DoNotCount()
    {
        Loop a = Known();
        Loop b = Known();
        b.Name = "Something else";
        b.Folder = "Roads/North";
        b.Notes = "watch the bridge";
        b.Favorite = true;
        b.SharedFavorite = true;
        b.SharedSettings = new LoopOwnSettings(false, LairEntryDebuffMode.BlockSpells);

        Assert.Equal(LoopSignature.Of(a), LoopSignature.Of(b));
    }

    [Fact]
    public void ACommandLeftEmpty_IsTheSameAsNone()
    {
        Loop a = Known(), b = Known();
        a.Waypoints[1].Command = null;
        b.Waypoints[1].Command = string.Empty;

        Assert.Equal(LoopSignature.Of(a), LoopSignature.Of(b));
    }

    private static readonly (string What, System.Action<Loop> Change)[] Changes =
    {
        ("a room", l => l.Waypoints[0].Room = "1/299"),
        ("a room's map", l => l.Waypoints[0].Room = "3/297"),
        ("the order of the rooms", l => l.Waypoints.Reverse()),
        ("a room added", l => l.Waypoints.Add(new LoopWaypoint(new RoomKey(2, 15)))),
        ("a room taken out", l => l.Waypoints.RemoveAt(1)),
        ("a command", l => l.Waypoints[0].Command = "ask barmaid wine"),
        ("a command's case", l => l.Waypoints[0].Command = "Ask barmaid ale"),
        ("a command added", l => l.Waypoints[1].Command = "search"),
        ("a delay", l => l.Waypoints[0].DelayMs = 1501),
        ("no rest", l => l.Waypoints[0].DoNotRest = true),
        ("no attack", l => l.Waypoints[0].DoNotAttack = true),
        ("rest here for HP", l => l.Waypoints[0].RestHereHp = true),
        ("rest here for mana", l => l.Waypoints[0].RestHereMana = true),
        ("a lap wait", l => l.Waypoints[0].LapWaitMs = 60_000),
        ("a lap wait's length", l => { l.Waypoints[2].LapWaitMs = 90_000; }),
        ("a start room", l => l.Waypoints[1].StartHere = true),
        ("only attack in lair rooms", l => l.OnlyAttackInLairRooms = false),
        ("the lair wait", l => l.LairEntryDebuff = LairEntryDebuffMode.BlockSpells),
        ("the lair wait off", l => l.LairEntryDebuff = LairEntryDebuffMode.Off),
    };

    [Fact]
    public void AnythingOfTheLoopItself_ChangesTheSignature()
    {
        string known = LoopSignature.Of(Known());
        HashSet<string> seen = new() { known };
        foreach ((string what, System.Action<Loop> change) in Changes)
        {
            Loop changed = Known();
            change(changed);

            // Different from the loop as it was, and from every other change.
            Assert.True(seen.Add(LoopSignature.Of(changed)), $"{what} did not change the signature");
        }
    }

    // One mark is never read as another, and text running into the next field is
    // never read as a different split of the same characters.
    [Fact]
    public void NoTwoDifferentLoops_ComeOutTheSame()
    {
        static Loop With(System.Action<LoopWaypoint, LoopWaypoint> set)
        {
            Loop l = new("x", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });
            set(l.Waypoints[0], l.Waypoints[1]);
            return l;
        }
        string[] signatures =
        {
            LoopSignature.Of(With((a, b) => a.DoNotRest = true)),
            LoopSignature.Of(With((a, b) => a.DoNotAttack = true)),
            LoopSignature.Of(With((a, b) => a.RestHereHp = true)),
            LoopSignature.Of(With((a, b) => a.RestHereMana = true)),
            LoopSignature.Of(With((a, b) => a.LapWaitMs = 1)),
            LoopSignature.Of(With((a, b) => b.LapWaitMs = 1)),
            LoopSignature.Of(With((a, b) => a.StartHere = true)),
            LoopSignature.Of(With((a, b) => b.StartHere = true)),
            LoopSignature.Of(With((a, b) => b.DoNotRest = true)),
            LoopSignature.Of(With((a, b) => { a.Command = "ab"; b.Command = "c"; })),
            LoopSignature.Of(With((a, b) => { a.Command = "a"; b.Command = "bc"; })),
            LoopSignature.Of(With((a, b) => a.Command = "abc")),
            LoopSignature.Of(With((a, b) => b.Command = "abc")),
            LoopSignature.Of(With((a, b) => { })),
        };

        Assert.Equal(signatures.Length, signatures.Distinct().Count());
        Assert.All(signatures, s => Assert.Equal(64, s.Length));
    }
}
