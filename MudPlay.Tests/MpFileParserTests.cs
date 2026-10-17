using MudPlay.Game.Map;
using MudPlay.Game.Map.MpFile;
using Xunit;

namespace MudPlay.Tests;

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
        Assert.Equal("ACRY",                 file.Start.Code);
        Assert.Equal("Island",               file.Start.Group);
        Assert.Equal("Ancient Crypt-1 1943", file.Start.Name);
        Assert.Equal("3C900060",             file.StartHashExits);
        Assert.Equal(32,                     file.Steps.Count);
        Assert.Equal(Direction.W,            file.Steps[0].Compass);
        Assert.NotNull(file.Steps[0].Compass);
        Assert.Equal("w",                    file.Steps[0].RawAction);
        Assert.Equal("3C900060",             file.Steps[0].HashExits);
    }

    // ----- dual-header loop (V4 generator shape) --------------------

    [Fact]
    public void Parse_DualHeaderLoop_AcceptsWhenStartCodeEqualsEndCode()
    {
        string text =
            "[Loop label][MudPlay]\n" +
            "[ABCD:Group:Room name]\n" +
            "[ABCD:Group:Room name]\n" +     // V4 emits the end-room duplicate
            "DEADBEEF:DEADBEEF:2:-1:0:::\n" +
            "DEADBEEF:0000:n\n" +
            "DEADBEEF:0000:s\n";
        MpLoopFile file = MpFileParser.Parse(text);
        Assert.Equal("Loop label", file.Label);
        Assert.Equal("MudPlay",      file.Author);
        Assert.Equal(2,            file.Steps.Count);
    }

    // ----- problems are reported, not thrown ------------------------

    [Fact]
    public void Parse_GotoPath_IsReadButFlaggedNotALoop()
    {
        string text =
            "[][]\n" +
            "[CODE:Group:Name]\n" +
            "[OTHR:Group:Other room]\n" +
            "AAAAAAAA:BBBBBBBB:2:-1:0:::\n" +
            "AAAAAAAA:0000:n\n" +
            "AAAAAAAA:0000:s\n";
        MpLoopFile file = MpFileParser.Parse(text);
        Assert.False(file.IsLoop);
        Assert.Equal("OTHR", file.End.Code);
        Assert.Contains(file.Problems, p => p.Contains("goto path"));
    }

    [Fact]
    public void Parse_StepCountMismatch_IsAProblem()
    {
        string text =
            "[][]\n" +
            "[CODE:Group:Name]\n" +
            "AAAAAAAA:AAAAAAAA:5:-1:0:::\n" +    // promises 5
            "AAAAAAAA:0000:n\n" +                // delivers 2
            "AAAAAAAA:0000:s\n";
        MpLoopFile file = MpFileParser.Parse(text);
        Assert.Equal(2, file.Steps.Count);
        Assert.Contains(file.Problems, p => p.Contains("says 5"));
    }

    // Three of MegaMUD's stock loops end in a DOS Ctrl-Z byte, which used to fail the
    // whole file as a malformed last step (issue #243: Cplnloop / Cplsloop / Fungloop).
    [Fact]
    public void Parse_TrailingCtrlZ_IsIgnored()
    {
        string text = "[Loop][]\r\n[CODE:G:N]\r\nAAAAAAAA:AAAAAAAA:2:-1:0:::\r\nAAAAAAAA:0000:n\r\nBBBBBBBB:0000:s\r\n\u001A";
        MpLoopFile file = MpFileParser.Parse(text);
        Assert.Equal(2, file.Steps.Count);
        Assert.Empty(file.Problems);
    }

    // Dhelloop.mp repeats the [label][author] line, the second carrying the author.
    [Fact]
    public void Parse_SecondLabelLine_FillsTheAuthor()
    {
        string text =
            "[Dhelvanen, Trade Loop-7 199][]\n" +
            "[Dhelvanen, Trade Loop][Kitty & Wulfman]\n" +
            "[FFSB:Black House:Fungus Forest (Stone Bridge)-7 199]\n" +
            "86605041:86605041:2:-1:0:::FNG1LOOP.MP\n" +
            "86605041:0000:sw\n" +
            "1BC01040:0000:w\n";
        MpLoopFile file = MpFileParser.Parse(text);
        Assert.Equal("Dhelvanen, Trade Loop-7 199", file.Label);
        Assert.Equal("Kitty & Wulfman", file.Author);
        Assert.Equal("FFSB", file.Start.Code);
        Assert.Equal("FNG1LOOP.MP", file.SuccessPath);
    }

    [Fact]
    public void Parse_PathDetailsAndStepOptions()
    {
        string text =
            "[Loop][Me]\n" +
            "[CODE:G:N]\n" +
            "AAAAAAAA:AAAAAAAA:3:-1:25:rope and grapple:FAILPATH.MP:NEXT.MP\n" +
            "AAAAAAAA:0014:s[search s]\n" +          // don't rest + stash point
            "BBBBBBBB:0242:e[use black star key e]\n" +  // rest here + no attack + disarm
            "CCCCCCCC:0000:E -- (Hidden/Needs 1 Actions\n";
        MpLoopFile file = MpFileParser.Parse(text);
        Assert.Equal(25, file.Gold);
        Assert.Equal("rope and grapple", file.RequiredItem);
        Assert.Equal("FAILPATH.MP", file.FailPath);
        Assert.Equal("NEXT.MP", file.SuccessPath);
        Assert.Equal("-1", file.Use);

        Assert.Equal(MpStepFlags.DontRest | MpStepFlags.Stash, file.Steps[0].Flags);
        Assert.Equal(Direction.S, file.Steps[0].Compass);
        Assert.Equal(new[] { "search s" }, file.Steps[0].PreActions);

        Assert.Equal(MpStepFlags.RestHere | MpStepFlags.NoAttack | MpStepFlags.Disarm, file.Steps[1].Flags);
        Assert.Equal(new[] { "use black star key e" }, file.Steps[1].PreActions);

        Assert.Equal(Direction.E, file.Steps[2].Compass);
        Assert.Equal("(Hidden/Needs 1 Actions", file.Steps[2].Note);
    }

    [Fact]
    public void Parse_MalformedRow_IsSkippedWithAProblem()
    {
        string text =
            "[][]\n[CODE:G:N]\nAAAAAAAA:AAAAAAAA:2:-1:0:::\n" +
            "AAAAAAAA:0000:n\ngarbage\nBBBBBBBB:0000:s\n";
        MpLoopFile file = MpFileParser.Parse(text);
        Assert.Equal(2, file.Steps.Count);
        Assert.Contains(file.Problems, p => p.Contains("malformed"));
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
        Assert.Null(file.Steps[0].Compass);
        Assert.Null(file.Steps[0].Compass);
        Assert.Equal("go path", file.Steps[0].Command);
        Assert.NotNull(file.Steps[1].Compass);
    }

    [Fact]
    public void Parse_TooShort_Throws()
    {
        string text = "[][]\n";
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
