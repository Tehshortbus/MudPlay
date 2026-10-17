using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Models.GameData;
using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

public sealed class TriggerEngineTests
{
    // ----- LiteralToRegex -------------------------------------------------

    [Fact]
    public void LiteralToRegex_EscapesRegexMetacharacters()
    {
        string regex = TriggerEngine.LiteralToRegex("HP=10/20 (Resting).");
        Assert.Matches(regex, "HP=10/20 (Resting).");
        // The literal '.' must NOT match an arbitrary character.
        Assert.DoesNotMatch(regex, "HP=10/20 (Resting)X");
    }

    [Fact]
    public void LiteralToRegex_TranslatesStarToGreedyNonCapture()
    {
        string regex = TriggerEngine.LiteralToRegex("* enters the room.");
        Match m = Regex.Match("Joe enters the room.", regex);
        Assert.True(m.Success);
        // Star is non-capturing — only Group[0] (the full-match) should exist.
        Assert.Single(m.Groups);
    }

    [Fact]
    public void LiteralToRegex_TranslatesBracedNameToNamedCapture()
    {
        string regex = TriggerEngine.LiteralToRegex("{usr} enters the room.");
        Match m = Regex.Match("Joe enters the room.", regex);
        Assert.True(m.Success);
        Assert.Equal("Joe", m.Groups["usr"].Value);
    }

    [Fact]
    public void LiteralToRegex_TranslatesNumberedNameToNamedCapture()
    {
        // {1} / {2} are valid wildcards — the bug was that they were treated as
        // literal braces, so the pattern never matched.
        string regex = TriggerEngine.LiteralToRegex("{1} telepaths: &@{2}");
        Match m = Regex.Match("Raijin telepaths: &@exp", regex);
        Assert.True(m.Success);
        Assert.Equal("Raijin", m.Groups["1"].Value);
        Assert.Equal("exp",    m.Groups["2"].Value);
    }

    [Fact]
    public void LiteralToRegex_EndOfPatternCaptureConsumesRestOfLine()
    {
        // Regression for the "Also here: {test}" case where non-greedy `.+?`
        // matched only the first character ("h") instead of "healer.".
        string regex = TriggerEngine.LiteralToRegex("Also here: {test}");
        Match m = Regex.Match("Also here: healer.", regex);
        Assert.True(m.Success);
        Assert.Equal("healer.", m.Groups["test"].Value);
    }

    [Fact]
    public void LiteralToRegex_LeavesNonNameBracePairsAsLiterals()
    {
        // {HP=10/20} holds characters that aren't name chars — not a wildcard, so
        // it stays literal text. (A pure {123} now IS a numbered wildcard.)
        string regex = TriggerEngine.LiteralToRegex("hp {HP=10/20}");
        Match m = Regex.Match("hp {HP=10/20}", regex);
        Assert.True(m.Success);
        Assert.Single(m.Groups);
    }

    [Fact]
    public void LiteralToRegex_HandlesMultipleCaptures()
    {
        string regex = TriggerEngine.LiteralToRegex("{who} hit {tgt} for {dmg} damage.");
        Match m = Regex.Match("Joe hit an orc for 12 damage.", regex);
        Assert.True(m.Success);
        Assert.Equal("Joe",    m.Groups["who"].Value);
        Assert.Equal("an orc", m.Groups["tgt"].Value);
        Assert.Equal("12",     m.Groups["dmg"].Value);
    }

    // ----- TryInterpolate -------------------------------------------------

    [Fact]
    public void TryInterpolate_SubstitutesKnownVariables()
    {
        TriggerEngine engine = new(TestProfiles.Loaded());
        engine.Variables["usr"] = "Joe";
        bool ok = engine.TryInterpolate("/{usr} hi there!", "test", out string result);
        Assert.True(ok);
        Assert.Equal("/Joe hi there!", result);
    }

    [Fact]
    public void TryInterpolate_AbortsOnUndefinedVariable()
    {
        TriggerEngine engine = new(TestProfiles.Loaded());
        bool ok = engine.TryInterpolate("hello {foo}", "test", out string result);
        Assert.False(ok);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void TryInterpolate_LeavesNonNameBracesAsLiteralText()
    {
        // {a/b} holds a non-name character — left as literal text, not treated as a
        // wildcard reference. (A pure {123} now IS a wildcard name.)
        TriggerEngine engine = new(TestProfiles.Loaded());
        bool ok = engine.TryInterpolate("loose {a/b} braces", "test", out string result);
        Assert.True(ok);
        Assert.Equal("loose {a/b} braces", result);
    }

    [Fact]
    public void TryInterpolate_SubstitutesNumberedWildcards()
    {
        TriggerEngine engine = new(TestProfiles.Loaded());
        engine.Variables["1"] = "Raijin";
        engine.Variables["2"] = "exp";
        bool ok = engine.TryInterpolate("/{1} @{2}", "test", out string result);
        Assert.True(ok);
        Assert.Equal("/Raijin @exp", result);
    }

    [Fact]
    public void TryInterpolate_EmptyTemplateReturnsEmpty()
    {
        TriggerEngine engine = new(TestProfiles.Loaded());
        bool ok = engine.TryInterpolate(string.Empty, "test", out string result);
        Assert.True(ok);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void TryInterpolate_ChainsMultipleVariables()
    {
        TriggerEngine engine = new(TestProfiles.Loaded());
        engine.Variables["who"] = "Joe";
        engine.Variables["dmg"] = "12";
        bool ok = engine.TryInterpolate("{who} hit for {dmg}", "test", out string result);
        Assert.True(ok);
        Assert.Equal("Joe hit for 12", result);
    }

    // ----- End-to-end capture + response flow ----------------------------

    // The engine as the client builds it, fed the way the client feeds it: server
    // rows through the extractor it is attached to, chat through the router its
    // ChatRouter reads, and the program log it watches.
    private sealed class Rig
    {
        public MessageRouter Router { get; } = new();
        public ServerFeed Feed { get; }
        public LogService Log { get; } = new();
        public TriggerEngine Engine { get; }
        // What the engine sent (decoded, trailing CR trimmed).
        public List<string> Sent { get; } = new();

        public Rig(Trigger t)
        {
            DefaultPatterns.Seed(Router);
            Feed = new ServerFeed(Router);
            Engine = new TriggerEngine(TestProfiles.Loaded(), new ChatRouter(Router), Log, onUiThread: act => act());
            Engine.AttachLineExtractor(Feed.Lines);
            Engine.SetSender(bytes => Sent.Add(Encoding.Latin1.GetString(bytes).TrimEnd('\r')));
            Engine.Add(t);
        }

        // A line where the scope's triggers read it: the program log for SystemLog,
        // the game's output (a chat line among it) for the rest.
        public void Arrive(TriggerScope scope, string line)
        {
            if (scope == TriggerScope.SystemLog) Log.Info("Test", line);
            else Feed.Line(line);
        }
    }

    // Drives a trigger through the client's own input path and returns what was sent
    // to the wire, plus the live engine for wildcard assertions.
    private static (TriggerEngine Engine, List<string> Sent) FireOne(Trigger t, TriggerScope scope, string line)
    {
        Rig rig = new(t);
        rig.Arrive(scope, line);
        return (rig.Engine, rig.Sent);
    }

    [Fact]
    public void NamedCaptures_PopulateWildcardStore_AndInterpolateResponse()
    {
        Trigger t = new(
            Name: "enter", Enabled: true, Scope: TriggerScope.GameMessages,
            MatchType: TriggerMatchType.Literal,
            Pattern: "{usr} enters the room.", Response: "wave {usr}");
        (TriggerEngine engine, List<string> sent) = FireOne(t, TriggerScope.GameMessages, "Joe enters the room.");

        Assert.Equal("Joe", engine.Variables["usr"]);
        Assert.Equal(new[] { "wave Joe" }, sent);
    }

    [Fact]
    public void NumberedWildcards_CaptureAndReplyOnTheirChannel()
    {
        // The reported bug end-to-end: a {1}/{2} telepath trigger captured nothing
        // and never replied because the numbered braces were treated as literals.
        Trigger t = new(
            Name: "echo", Enabled: true, Scope: TriggerScope.ChatAny,
            MatchType: TriggerMatchType.Literal,
            Pattern: "{1} telepaths: &@{2}", Response: "/{1} @{2}");
        (TriggerEngine engine, List<string> sent) = FireOne(t, TriggerScope.ChatAny, "Raijin telepaths: &@exp");

        Assert.Equal("Raijin", engine.Variables["1"]);
        Assert.Equal("exp",    engine.Variables["2"]);
        Assert.Equal(new[] { "/Raijin @exp" }, sent);
    }

    // Triggers are the user's own automation: with the master switch off a
    // matched trigger's activation is skipped whole (user, 2026-10-09). On, it
    // fires as before (NamedCaptures_PopulateWildcardStore_AndInterpolateResponse).
    [Theory]
    [InlineData(TriggerScope.GameMessages, "Joe enters the room.")]
    [InlineData(TriggerScope.ChatAny, "Joe gossips: Joe enters the room.")]
    [InlineData(TriggerScope.SystemLog, "Joe enters the room.")]
    public void MasterSwitchOff_MatchedTrigger_DoesNotCaptureSendOrPlay(TriggerScope scope, string line)
    {
        Trigger t = new(
            Name: "enter", Enabled: true, Scope: scope,
            MatchType: TriggerMatchType.Literal,
            Pattern: "{usr} enters the room.", Response: "wave {usr}", SoundFile: "ding.wav");
        Rig rig = new(t);
        int played = 0, asked = 0;
        rig.Engine.PlaySound = _ => played++;
        rig.Engine.BlockedByMasterSwitch = () => { asked++; return true; };

        rig.Arrive(scope, line);
        rig.Arrive(scope, "nothing that matches");

        Assert.Empty(rig.Sent);
        Assert.Empty(rig.Engine.Variables);
        Assert.Equal(0, played);
        // Asked only for the line that matched, so the skip count means triggers
        // that would have fired.
        Assert.Equal(1, asked);
    }

    [Fact]
    public void ClearWildcards_EmptiesStore_AndRaisesChanged()
    {
        TriggerEngine engine = new(TestProfiles.Loaded());
        engine.Variables["1"] = "Raijin";
        int raised = 0;
        engine.WildcardsChanged += () => raised++;

        engine.ClearWildcards();
        Assert.Empty(engine.Variables);
        Assert.Equal(1, raised);

        // Already empty → no-op, no further event.
        engine.ClearWildcards();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Capture_RaisesWildcardsChanged()
    {
        Trigger t = new(
            Name: "cap", Enabled: true, Scope: TriggerScope.GameMessages,
            MatchType: TriggerMatchType.Literal,
            Pattern: "hi {who}", Response: "");
        Rig rig = new(t);
        int raised = 0;
        rig.Engine.WildcardsChanged += () => raised++;
        rig.Arrive(TriggerScope.GameMessages, "hi Joe");
        Assert.Equal("Joe", rig.Engine.Variables["who"]);
        Assert.Equal(1, raised);
    }
}
