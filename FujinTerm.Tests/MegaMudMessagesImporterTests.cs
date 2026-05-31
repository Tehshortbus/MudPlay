using FujinTerm.Models.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Tests for <see cref="MegaMudMessagesImporter"/>'s legacy
/// <c>messages.md</c> parser. Format details + algorithm provenance
/// live in the importer's doc comments.
/// </summary>
public sealed class MegaMudMessagesImporterTests
{
    [Fact]
    public void Parses_FourLineRecord()
    {
        const string src =
            "Poisoned:0004:2:cure poison\n" +
            "You feel sick.\n" +
            "The poison wears off.\n" +
            "\n";

        MessageImportResult r = MegaMudMessagesImporter.ParseText(src, "test");

        Assert.Empty(r.Failures);
        Assert.Single(r.Messages);
        MessageRecord m = r.Messages[0];
        Assert.Equal("Poisoned", m.Name);
        Assert.Equal("You feel sick.", m.Message);
        Assert.Equal("The poison wears off.", m.EndsWith);
        Assert.Equal(MessageAction.WaitForEnd, m.Action);
        Assert.Equal(MessageFlags.Poisoned, m.Flags);
        Assert.Equal((ushort)0x0004, m.RawFlagsHex);
        // Response is stored verbatim — exactly the bytes after the
        // header's third colon. The runtime consumer (Phase 13)
        // interprets ^M / CR as multi-step boundaries at send time.
        Assert.Equal("cure poison", m.Response);
    }

    [Fact]
    public void Parses_ShortRecord_NoEndsWithNoBlank()
    {
        // Legacy authors often skip the ends-with line entirely. Two
        // back-to-back 2-line records with no separator.
        const string src =
            "Alert1:0000:0:\n" +
            "First message.\n" +
            "Alert2:0000:0:\n" +
            "Second message.\n";

        MessageImportResult r = MegaMudMessagesImporter.ParseText(src, "test");

        Assert.Empty(r.Failures);
        Assert.Equal(2, r.Messages.Count);
        Assert.Equal("Alert1", r.Messages[0].Name);
        Assert.Equal("",        r.Messages[0].EndsWith);
        Assert.Equal("Alert2", r.Messages[1].Name);
    }

    [Fact]
    public void Response_StoredVerbatim_Including_LiteralCaretM_And_RawCR()
    {
        // Response field is the raw 4th-colon-suffix of the header line.
        // No splitting at import time — the runtime consumer interprets
        // ^M / CR as multi-step boundaries when actually sending.
        const string src =
            "Warn:0000:0:c1^Mc2\rc3\n" +
            "msg\n";

        MessageImportResult r = MegaMudMessagesImporter.ParseText(src, "test");
        Assert.Single(r.Messages);
        Assert.Equal("c1^Mc2\rc3", r.Messages[0].Response);
    }

    [Fact]
    public void DisabledBit_FlowsIntoFlagsAndRaw()
    {
        const string src = "X:8000:0:\nmsg\n\n";
        MessageImportResult r = MegaMudMessagesImporter.ParseText(src, "test");
        Assert.Single(r.Messages);
        Assert.True(r.Messages[0].Flags.HasFlag(MessageFlags.Disabled));
        Assert.Equal((ushort)0x8000, r.Messages[0].RawFlagsHex);
    }

    [Fact]
    public void ReservedBit_PreservedOnRawFlagsHex_NotInTypedFlags()
    {
        // Bit 0x0800 is documented-as-reserved in the legacy format —
        // typed enum should mask it out but RawFlagsHex retains it.
        const string src = "X:0800:0:\nmsg\n\n";
        MessageImportResult r = MegaMudMessagesImporter.ParseText(src, "test");
        Assert.Single(r.Messages);
        Assert.Equal(MessageFlags.None, r.Messages[0].Flags);
        Assert.Equal((ushort)0x0800, r.Messages[0].RawFlagsHex);
    }

    [Fact]
    public void Resync_AfterMalformedHeader_ContinuesParsing()
    {
        const string src =
            "GOOD1:0000:0:\n" +
            "ok message 1\n" +
            "\n" +
            "this line is not a header at all\n" +
            "neither is this\n" +
            "\n" +
            "GOOD2:0000:0:\n" +
            "ok message 2\n";

        MessageImportResult r = MegaMudMessagesImporter.ParseText(src, "test");

        Assert.Equal(2, r.Messages.Count);
        Assert.Equal("GOOD1", r.Messages[0].Name);
        Assert.Equal("GOOD2", r.Messages[1].Name);
        // The malformed header triggers a failure entry.
        Assert.NotEmpty(r.Failures);
    }

    [Fact]
    public void ResponseField_MayContainColons_DueToMaxSplit4()
    {
        const string src = "Tell:0000:0:tell wizard hello:there\nmsg\n\n";
        MessageImportResult r = MegaMudMessagesImporter.ParseText(src, "test");
        Assert.Single(r.Messages);
        Assert.Equal("tell wizard hello:there", r.Messages[0].Response);
    }

    [Fact]
    public void Id_IsStable_AcrossSameInputs()
    {
        string a = MegaMudMessagesImporter.ComputeId("Name", "msg", "end");
        string b = MegaMudMessagesImporter.ComputeId("Name", "msg", "end");
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void Id_Differs_WhenAnyFieldChanges()
    {
        string a = MegaMudMessagesImporter.ComputeId("Name", "msg", "end");
        Assert.NotEqual(a, MegaMudMessagesImporter.ComputeId("Other", "msg", "end"));
        Assert.NotEqual(a, MegaMudMessagesImporter.ComputeId("Name", "other", "end"));
        Assert.NotEqual(a, MegaMudMessagesImporter.ComputeId("Name", "msg", "other"));
    }

    [Theory]
    [InlineData("0", MessageAction.Ignore)]
    [InlineData("1", MessageAction.RecheckRoom)]
    [InlineData("2", MessageAction.WaitForEnd)]
    [InlineData("3", MessageAction.RestHp)]
    [InlineData("4", MessageAction.RestMana)]
    [InlineData("5", MessageAction.Run)]
    [InlineData("6", MessageAction.Hangup)]
    public void Action_MapsAllSevenLegacyCodes(string code, MessageAction expected)
    {
        string src = $"X:0000:{code}:\nmsg\n\n";
        MessageImportResult r = MegaMudMessagesImporter.ParseText(src, "test");
        Assert.Single(r.Messages);
        Assert.Equal(expected, r.Messages[0].Action);
    }
}
