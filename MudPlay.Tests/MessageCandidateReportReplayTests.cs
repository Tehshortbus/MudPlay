using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Replays the lines from a real unrecognized-lines export through the whole capture
// path — real stock catalogue, real router patterns, real exclusions — rather than a
// synthetic catalogue. The reported queue was almost entirely noise: party-screen
// rows, room light, melee swings, known casts, and monster death flavour, which
// buried the handful of genuinely uncatalogued room-spell messages it existed to
// surface. These pin which of those classes stay out and, just as importantly, which
// still come through.
public sealed class MessageCandidateReportReplayTests : IDisposable
{
    private readonly string _dir;
    private readonly LogService _log = new();
    private readonly MessageRouter _router = new();
    private readonly MessageStore _messages = new();
    private readonly MessageCandidateStore _candidates = new();
    private readonly MessageCandidateWatcher _watcher;
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _server;

    // A catalogued line that comes well after the line before it: it releases a line
    // held back as possible death flavour, as the next line of the game does.
    private const string LaterKnownLine = "Raijin casts bless on Raijin!";

    public MessageCandidateReportReplayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-replay-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
        // ReplaceAll raises one Reset, so the watcher rebuilds its indexes once
        // instead of once per record.
        _messages.Messages.ReplaceAll(
            JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, "Messages.stock.seed.json"))
            ?? new List<MessageRecord>());
        DefaultPatterns.Seed(_router);
        _watcher = new MessageCandidateWatcher(
            _router, _messages, _candidates, log: _log,
            // Same composition AppServices wires: the par roster, the stat/exp/health
            // sheet, and the `spells` listing are all read as direct blocks.
            isRecognizedByDirectParser: text =>
                PartyManager.IsRosterRow(text)
                || StatParser.IsStatScreenLine(text)
                || Game.Spells.SpellListParser.IsSpellListLine(text)
                || Game.BenignChatterMatcher.IsBenign(text)
                || Game.BenignChatterMatcher.IsOtherPlayerGearSwap(
                    text, n => n is "Client" or "Raijin" or "Suijin" or "Fujin"),
            // The report's party: Suijin the Witchunter casts nothing, Raijin the
            // Priest does. AppServices resolves this from the live roster + the
            // Classes table; here it's stated directly.
            isNonCasterPhysicalAction: text => Game.Combat.NonCasterAttackLine.Matches(
                text, name => name switch
                {
                    "Suijin" => false,
                    "Raijin" => true,
                    "Fujin"  => true,   // Mystic — Kai magery
                    _        => null,
                }));
        _server = new ServerFeed(_router);
        _watcher.AttachLineExtractor(_server.Lines);
        _watcher.NotifyInGame();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private void Feed(string text, bool flush = true)
    {
        DateTimeOffset at = DateTimeOffset.UtcNow;
        _server.Line(text, at);
        if (flush) _server.Line(LaterKnownLine, at.AddMinutes(1));
    }

    [Theory]
    // Party screen — read by PartyManager's block parser, not a router pattern.
    [InlineData("  Raijin WuzHere                 (Priest)     [M:100%] [H: 85%]   - Backrank")]
    [InlineData("  Suijin WuzHere                 (Witchunter)          [H:100%]   - Midrank")]
    [InlineData("  Fujin WuzHere                  (Mystic)     [K:100%] [H: 94%]   - Frontrank")]
    [InlineData("  Raijin WuzHere                 (Priest)     [M: 94%] [H: 87%]   - Backrank")]
    // Room light — fully known from game data.
    [InlineData("The room is dimly lit")]
    // Known casts, stored as catalogue templates.
    [InlineData("Raijin casts minor healing on Raijin!")]
    [InlineData("Raijin casts bless on Raijin!")]
    [InlineData("Raijin casts bless on Suijin!")]
    [InlineData("Raijin casts a spell on you!")]
    [InlineData("You invoke the way of the swan.")]
    [InlineData("You invoke the way of the tiger.")]
    // Buff expiry — the catalogue wording omits the trailing "!".
    [InlineData("The effects of way of the tiger wear off!")]
    // Another player's failed cast.
    [InlineData("Raijin attempted to cast minor healing, but failed.")]
    // Third-party physical attacks, weapon / body part named.
    [InlineData("The wild dog snaps at Suijin with its teeth!")]
    [InlineData("The dark goblin archer shoots an arrow at Suijin with their shortbow!")]
    [InlineData("The nasty bandit swings at Suijin with their broadsword!")]
    [InlineData("The bandit swings at Raijin with their broadsword!")]
    [InlineData("The bandit swings at Suijin with their broadsword!")]
    // Bare swings with no weapon named.
    [InlineData("Raijin swipes at dark goblin archer!")]
    [InlineData("Raijin swipes at wild dog!")]
    [InlineData("Raijin swipes at nasty bandit!")]
    [InlineData("Raijin swipes at bandit!")]
    // Ranged attack naming ammunition but no weapon. Textually identical to a
    // projectile spell, so only the actor's class settles it: Suijin is a Witchunter
    // (no magery), which proves this can't be a spell message.
    [InlineData("Suijin shoots an arrow at bandit!")]
    // Stat-screen rows — read by StatParser's gated block, not a router pattern.
    // (Exact lines from the unrecognized-lines-20260912-235152 export.)
    [InlineData("Name: Fujin              Lives/CP: 9/2")]
    [InlineData("Race: Kang               Exp: 1234567         Perception: 40")]
    [InlineData("Class: Paladin           Level: 28            Stealth: 0")]
    [InlineData("Hits: 324/324            Armour Class: 78/16  Thievery: 0")]
    [InlineData("Mana: * 56/72            Spellcasting: 94     Traps: 0")]
    [InlineData("Picklocks: 0")]
    // Spell-list rows — read by SpellListParser's block, not a router pattern.
    // (Exact lines from the unrecognized-lines-20260912-235315 export.)
    [InlineData("You have the following spells:")]
    [InlineData("Level Mana Short Spell Name")]
    [InlineData("1   1    harm  harm")]
    [InlineData("1   2    mihe  minor healing")]
    [InlineData("2   4    bles  bless")]
    [InlineData("3   2    turn  turn undead")]
    // Benign non-spell chatter — player movement / status / social lines no spell record
    // describes. (Exact lines from the unrecognized-lines-20260922-014747 export.)
    [InlineData("Miserable just left to the east.")]
    [InlineData("Client just left to the south.")]
    [InlineData("Durnan just disconnected!!!")]
    [InlineData("You are following Client.")]
    [InlineData("You just paid 5 gold crowns in toll charges.")]
    [InlineData("You say \"\"")]
    [InlineData("Also here: albino salamander, river ray, albino salamander, river")]
    [InlineData("To prevent accidental suicide or reroll, these commands")]
    [InlineData("SET SUICIDE command.")]
    [InlineData("Client wears runed cowl!")]           // roster-gated gear swap (Client is a known player here)
    [InlineData("Client removes jeweled turban!")]
    public void ReportedNoise_IsNoLongerCaptured(string line)
    {
        Feed(line);
        Assert.Empty(_candidates.Candidates);
    }

    [Theory]
    [InlineData("The dog yelps loudly, and dies.")]
    [InlineData("The dark goblin archer collapses with a spiteful hiss.")]
    public void ReportedDeathFlavour_IsNoLongerCaptured(string line)
    {
        Feed(line, flush: false);
        Feed("You gain 350 experience.");
        Assert.Empty(_candidates.Candidates);
    }

    [Theory]
    [InlineData("swan")]
    [InlineData("tige")]
    public void ReportedEngineCommandEcho_IsNoLongerCaptured(string echo)
    {
        // These were engine-issued casts whose truncated echo bounced back. The echo
        // ring is now fed from the engine's raw-send path too, not just typed input.
        _watcher.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes(echo + "\r\n"));
        Feed(echo);
        Assert.Empty(_candidates.Candidates);
    }

    [Theory]
    // Darkwood room-spell triggers with no catalogue entry — the reason the feature
    // exists. Every exclusion above has to leave these alone.
    [InlineData("An ominous wind blows through the trees")]
    [InlineData("A flock of birds fly overhead.")]
    [InlineData("The forest becomes strangely silent.")]
    [InlineData("The leaves begin to rustle, as if some beast were about to spring forth!")]
    public void GenuineUnknowns_AreStillCaptured(string line)
    {
        Feed(line);
        Assert.Single(_candidates.Candidates);
    }

    [Fact]
    public void SameRangedShapeFromACaster_IsStillCaptured()
    {
        // The mirror of the Suijin case: Raijin is a Priest, so an identical line
        // could be an uncatalogued projectile spell and has to stay in the queue.
        // This is what keeps the actor check from becoming a blanket suppressor.
        Feed("Raijin shoots a searing bolt at bandit!");
        Assert.Single(_candidates.Candidates);
    }
}
