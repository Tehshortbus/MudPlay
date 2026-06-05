using FujinTerm.Game.Map;
using FujinTerm.Game.Map.MpFile;
using Xunit;

namespace FujinTerm.Tests;

public sealed class MpFileParserTests
{
    // ----- happy path: single-header in-the-wild loop ---------------

    private const string AcryLoopText =
        "[Ancient Crypt-1 1943][]\n" +
        "[ACRY:Island:Ancient Crypt-1 1943]\n" +
        "3C900060:3C900060:32:-1:0:::\n" +
        // 32 fake step lines — directions cycle around but the
        // parser doesn't care about closure, only structure.
        "3C900060:0000:w\n3C900015:0000:s\n" +
        "3C900015:0000:n\n3C900015:0000:e\n" +
        "3C900015:0000:w\n3C900015:0000:s\n" +
        "3C900015:0000:n\n3C900015:0000:e\n" +
        "3C900015:0000:w\n3C900015:0000:s\n" +
        "3C900015:0000:n\n3C900015:0000:e\n" +
        "3C900015:0000:w\n3C900015:0000:s\n" +
        "3C900015:0000:n\n3C900015:0000:e\n" +
        "3C900015:0000:w\n3C900015:0000:s\n" +
        "3C900015:0000:n\n3C900015:0000:e\n" +
        "3C900015:0000:w\n3C900015:0000:s\n" +
        "3C900015:0000:n\n3C900015:0000:e\n" +
        "3C900015:0000:w\n3C900015:0000:s\n" +
        "3C900015:0000:n\n3C900015:0000:e\n" +
        "3C900015:0000:w\n3C900015:0000:s\n" +
        "3C900015:0000:n\n3C900015:0000:e\n";

    [Fact]
    public void Parse_SingleHeaderLoop_ExtractsAllFields()
    {
        MpLoopFile file = MpFileParser.Parse(AcryLoopText);
        Assert.Equal("Ancient Crypt-1 1943", file.Label);
        Assert.Equal("",                     file.Author);
        Assert.Equal("ACRY",                 file.Code4);
        Assert.Equal("Island",               file.GroupName);
        Assert.Equal("Ancient Crypt-1 1943", file.RoomName);
        Assert.Equal("3C900060",             file.StartHashExits);
        Assert.Equal(32,                     file.Steps.Count);
        Assert.Equal(Direction.W,            file.Steps[0].Compass);
        Assert.True(file.Steps[0].IsCompass);
        Assert.Equal("w",                    file.Steps[0].ActionText);
        Assert.Equal("3C900060",             file.Steps[0].HashExits);
    }

    // ----- dual-header loop (V4 generator shape) --------------------

    [Fact]
    public void Parse_DualHeaderLoop_AcceptsWhenStartCodeEqualsEndCode()
    {
        string text =
            "[Loop label][Fujin]\n" +
            "[ABCD:Group:Room name]\n" +
            "[ABCD:Group:Room name]\n" +     // V4 emits the end-room duplicate
            "DEADBEEF:DEADBEEF:2:-1:0:::\n" +
            "DEADBEEF:0000:n\n" +
            "DEADBEEF:0000:s\n";
        MpLoopFile file = MpFileParser.Parse(text);
        Assert.Equal("Loop label", file.Label);
        Assert.Equal("Fujin",      file.Author);
        Assert.Equal(2,            file.Steps.Count);
    }

    // ----- rejection paths ------------------------------------------

    [Fact]
    public void Parse_StartHashNotEqualEndHash_RejectsAsPath()
    {
        string text =
            "[][]\n" +
            "[CODE:Group:Name]\n" +
            "[OTHR:Group:Other room]\n" +
            "AAAAAAAA:BBBBBBBB:2:-1:0:::\n" +
            "AAAAAAAA:0000:n\n" +
            "AAAAAAAA:0000:s\n";
        MpFileFormatException ex =
            Assert.Throws<MpFileFormatException>(() => MpFileParser.Parse(text));
        Assert.Contains("path-style", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_DualHeaderDifferentCodes_RejectsAsPath()
    {
        string text =
            "[][]\n" +
            "[ABCD:Group:Name]\n" +
            "[WXYZ:Group:Other]\n" +
            "AAAAAAAA:AAAAAAAA:2:-1:0:::\n" +
            "AAAAAAAA:0000:n\n" +
            "AAAAAAAA:0000:s\n";
        MpFileFormatException ex =
            Assert.Throws<MpFileFormatException>(() => MpFileParser.Parse(text));
        Assert.Contains("path-style", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_StepCountMismatch_Throws()
    {
        string text =
            "[][]\n" +
            "[CODE:Group:Name]\n" +
            "AAAAAAAA:AAAAAAAA:5:-1:0:::\n" +    // promises 5
            "AAAAAAAA:0000:n\n" +                // delivers 2
            "AAAAAAAA:0000:s\n";
        MpFileFormatException ex =
            Assert.Throws<MpFileFormatException>(() => MpFileParser.Parse(text));
        Assert.Contains("declared 5", ex.Message);
    }

    [Fact]
    public void Parse_NonCompassAction_IsKeptAsActionTextNotThrown()
    {
        // MegaMUD's path engine records the literal verb when its
        // engine couldn't infer a compass move ("go path", "climb
        // wall", "open door"). The parser shouldn't reject these —
        // the resolver picks the right exit via next-step hashExits.
        string text =
            "[][]\n" +
            "[CODE:Group:Name]\n" +
            "AAAAAAAA:AAAAAAAA:2:-1:0:::\n" +
            "AAAAAAAA:0000:go path\n" +
            "BBBBBBBB:0000:s\n";
        MpLoopFile file = MpFileParser.Parse(text);
        Assert.False(file.Steps[0].IsCompass);
        Assert.Null(file.Steps[0].Compass);
        Assert.Equal("go path", file.Steps[0].ActionText);
        Assert.True(file.Steps[1].IsCompass);
    }

    [Fact]
    public void Parse_TooShort_Throws()
    {
        string text = "[][]\n[CODE:Group:Name]\n";
        Assert.Throws<MpFileFormatException>(() => MpFileParser.Parse(text));
    }

    [Fact]
    public void Parse_CRLF_LineEndings_AreToleratedLikeLF()
    {
        string text = AcryLoopText.Replace("\n", "\r\n");
        MpLoopFile file = MpFileParser.Parse(text);
        Assert.Equal(32, file.Steps.Count);
    }
}
