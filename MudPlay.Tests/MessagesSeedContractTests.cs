using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The realm-flavored Messages seeds are produced offline by tools/decode_messages_md.py
// and deserialized by MessageStore via JsonStore.Load<List<MessageRecord>>. This pins the
// decoder→model JSON contract that the whole catalogue depends on: Flags serialize as
// MessageFlags enum NAMES (a multi-bit value as a comma-joined list) and Links as
// {Table, Number}, plus the optional ConfuseFumbleLine on Confused records. The legacy
// Action/Response keys are gone from the model but a seed still carrying them (the fixture
// below does) must keep loading. Drift here would silently blank the
// catalogue on load (a corrupt-parse falls through to an empty seed).
public sealed class MessagesSeedContractTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "msgseed-" + Path.GetRandomFileName() + ".json");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort */ }
    }

    [Fact]
    public void DecoderShapedSeed_Deserializes_WithFlagsActionAndLinks()
    {
        // Exactly the shape tools/decode_messages_md.py emits: a standalone condition
        // detector (multi-bit flags, no link) and an item buff (a single Items link).
        const string json = """
        [
          {
            "Id": "aaaa000000000001",
            "Name": "net hold",
            "Action": "WaitForEnd",
            "Flags": "Confused, MovementPrevented",
            "RawFlagsHex": 18,
            "Response": "",
            "CasterMessage": "",
            "TargetMessage": "",
            "WitnessMessage": "",
            "AppliedMessage": "You are entangled in a net!",
            "AppliedEndsWith": "You work yourself free.",
            "Links": null,
            "ConfuseFumbleLine": "You fumble in confusion!",
            "CastResponse": "^M^M"
          },
          {
            "Id": "aaaa000000000002",
            "Name": "belt of might",
            "Action": "Ignore",
            "Flags": "None",
            "RawFlagsHex": 0,
            "Response": "",
            "CasterMessage": "",
            "TargetMessage": "",
            "WitnessMessage": "",
            "AppliedMessage": "You feel strong",
            "AppliedEndsWith": "Your enhanced strength wears off",
            "Links": [ { "Table": "Items", "Number": 438 } ]
          }
        ]
        """;
        File.WriteAllText(_path, json);

        List<MessageRecord>? recs = JsonStore.Load<List<MessageRecord>>(_path);
        Assert.NotNull(recs);
        Assert.Equal(2, recs!.Count);

        MessageRecord net = recs[0];
        Assert.True(net.Flags.HasFlag(MessageFlags.Confused));
        Assert.True(net.Flags.HasFlag(MessageFlags.MovementPrevented));
        Assert.Equal("You are entangled in a net!", net.AppliedMessage);
        Assert.Null(net.Links);
        Assert.Equal("You fumble in confusion!", net.ConfuseFumbleLine);
        Assert.Equal("^M^M", net.CastResponse);

        MessageRecord belt = recs[1];
        Assert.Equal(MessageFlags.None, belt.Flags);
        Assert.NotNull(belt.Links);
        Assert.Single(belt.Links!);
        Assert.Equal("Items", belt.Links![0].Table);
        Assert.Equal(438, belt.Links[0].Number);
        Assert.Equal(string.Empty, belt.ConfuseFumbleLine);   // key absent → default
        Assert.Equal(string.Empty, belt.CastResponse);        // key absent → default ""
    }
}
