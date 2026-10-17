using System.Text.Json;
using MudPlay.Services;
using MudPlay.Services.Api;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// GET /log, /scrollback and /events are the bug report's content leaving the
// client by another door: the saved login name and passwords are masked in all
// three, and the scrollback is held to the time spent in the game.
public sealed class LocalApiPrivacyTests
{
    private const string Login = "loginzed";
    private const string Password = "Pa55w0rd!x";
    private const string Suicide = "endgame77";
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    // Built as the API builds one: for the response, uncompiled.
    private static BugReportScrub.Scrubber Scrubber() =>
        BugReportScrub.Build(
        [
            new(BugReportScrub.LoginNameLabel, Login),
            new(BugReportScrub.LoginPasswordLabel, Password),
            new(BugReportScrub.SuicidePasswordLabel, Suicide),
        ], ["Tester"], compiled: false);

    private static string Json(object payload) => JsonSerializer.Serialize(payload, LocalApiJson.Options);

    private static void AssertNoAccountDetails(string text)
    {
        Assert.DoesNotContain(Login, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Suicide, text, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteRow(TerminalScreen screen, int y, string text, DateTimeOffset stamp)
    {
        screen.FeedTimestamp = stamp;
        for (int x = 0; x < text.Length && x < screen.Cols; x++)
            screen.Put(x, y, new Cell(text[x], default));
    }

    [Fact]
    public void Log_MasksTheMessageAndTheContext_AndKeepsTheRest()
    {
        LogService log = new(16);
        log.Info("AutoLogin", $"sent the name {Login} and the password {Password}");
        log.Log(LogSeverity.Warn, "Trigger", "a trigger fired on a line it shouldn't have", $"You say \"{Suicide.ToUpperInvariant()}\"");
        log.Info("Walker", "step 3 of 40 east");

        string json = Json(LocalApiState.Log(log, Scrubber(), since: 0, limit: 500, new HashSet<LogSeverity>(), source: null));

        AssertNoAccountDetails(json);
        Assert.Contains("sent the name [redacted] and the password [redacted]", json);
        Assert.Contains("You say \\u0022[redacted]\\u0022", json);
        Assert.Contains("step 3 of 40 east", json);
        Assert.Contains("\"count\": 3", json);
    }

    // A password with characters JSON writes as escapes is masked in the text,
    // before it is serialised: looking for it in the JSON would miss it.
    [Fact]
    public void Log_MasksAValueThatJsonWouldEscape()
    {
        const string awkward = "a<b&\"c'+d";
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build(
            [new(BugReportScrub.LoginPasswordLabel, awkward)], [], compiled: false);
        LogService log = new(4);
        log.Info("T", $"typed {awkward} at the prompt");

        string json = Json(LocalApiState.Log(log, scrubber, 0, 10, new HashSet<LogSeverity>(), null));

        Assert.Contains("typed [redacted] at the prompt", json);
        Assert.DoesNotContain("u003Cb", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Log_FilterAndCursorAreUnchangedByTheMasking()
    {
        LogService log = new(16);
        for (int i = 0; i < 5; i++) log.Info(i % 2 == 0 ? "A" : "B", $"m{i} {Suicide}");

        string json = Json(LocalApiState.Log(log, Scrubber(), since: 1, limit: 2, new HashSet<LogSeverity>(), source: "a"));

        // Entries 3 and 5 are the "A" ones after the cursor.
        Assert.Contains("m2 [redacted]", json);
        Assert.Contains("m4 [redacted]", json);
        Assert.DoesNotContain("m1", json);
        Assert.Contains("\"nextSince\": 5", json);
        AssertNoAccountDetails(json);
    }

    [Fact]
    public void Scrollback_LeavesOutTheLoginScreen_AndMasksWhatIsLeft()
    {
        TerminalEmulator emulator = new(80, 6);
        TerminalScreen screen = emulator.Screen;
        CaptureWindow inGame = new(on: false);

        // A login typed by hand isn't saved, so no mask knows it: only the window
        // keeps it out.
        WriteRow(screen, 0, "Enter your user-ID: handtyped", T0);
        WriteRow(screen, 1, "Password: ******", T0.AddSeconds(1));
        inGame.Set(true, T0.AddSeconds(5));
        WriteRow(screen, 2, "Town Square", T0.AddSeconds(5));
        WriteRow(screen, 3, $"You say \"{Suicide}\"", T0.AddSeconds(6));
        WriteRow(screen, 4, "[HP=100/MA=50]:", T0.AddSeconds(7));

        string json = Json(LocalApiState.Scrollback(emulator, inGame, Scrubber(), lines: 750));

        Assert.DoesNotContain("handtyped", json);
        Assert.DoesNotContain("user-ID", json);
        AssertNoAccountDetails(json);
        Assert.Contains("Town Square", json);
        Assert.Contains("You say \\u0022[redacted]\\u0022", json);
        Assert.Contains("\"count\": 3", json);
        Assert.Contains("\"inGameNow\": true", json);
    }

    [Fact]
    public void Scrollback_OutsideTheGame_SendsNoRows()
    {
        TerminalEmulator emulator = new(80, 4);
        WriteRow(emulator.Screen, 0, $"Enter your user-ID: {Login}", T0);

        string json = Json(LocalApiState.Scrollback(emulator, new CaptureWindow(on: false), Scrubber(), lines: 750));

        Assert.Contains("\"count\": 0", json);
        Assert.Contains("\"inGameNow\": false", json);
        AssertNoAccountDetails(json);
    }

    // The terminal wrapped a value onto the next row: it is masked, and every row
    // keeps its own time.
    [Fact]
    public void Scrollback_AValueWrappedOntoTheNextRow_IsMaskedAndTheRowsStayAligned()
    {
        TerminalEmulator emulator = new(80, 5);
        TerminalScreen screen = emulator.Screen;
        CaptureWindow inGame = new(on: true);
        WriteRow(screen, 0, "Bob gossips: " + new string('x', 63) + Suicide[..4], T0);
        WriteRow(screen, 1, Suicide[4..] + " tail", T0.AddSeconds(1));
        WriteRow(screen, 2, "after", T0.AddSeconds(2));

        using JsonDocument doc = JsonDocument.Parse(
            Json(LocalApiState.Scrollback(emulator, inGame, Scrubber(), lines: 750)));

        JsonElement[] rows = doc.RootElement.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.EndsWith("[redacted]", rows[0].GetProperty("text").GetString());
        Assert.Equal(" tail", rows[1].GetProperty("text").GetString());
        Assert.Equal("after", rows[2].GetProperty("text").GetString());
        Assert.Equal(T0.AddSeconds(2), rows[2].GetProperty("at").GetDateTimeOffset());
        AssertNoAccountDetails(doc.RootElement.GetRawText());
    }

    private sealed class Sink
    {
        private readonly object _sentLock = new();
        private readonly List<(string Name, string Json)> _sent = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public void Send(string name, object payload)
        {
            lock (_sentLock) { _sent.Add((name, JsonSerializer.Serialize(payload, LocalApiJson.Compact))); }
            _arrived.Release();
        }

        public async Task<(string Name, string Json)[]> WaitForAsync(int count)
        {
            for (int i = 0; i < count; i++)
                Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(10)), $"only {i} of {count} events arrived");
            lock (_sentLock) { return _sent.ToArray(); }
        }
    }

    [Fact]
    public async Task Events_AreMaskedOnThePump_WithAScrubberBuiltForTheRunAndInOrder()
    {
        Sink sink = new();
        int built = 0;
        using LocalApiMaskedEvents events = new(
            () => { Interlocked.Increment(ref built); return Task.FromResult(Scrubber()); },
            anyoneListening: () => true, sink.Send, gather: TimeSpan.Zero);

        events.Post("log", s => new { message = s.Apply($"sent {Password}") });
        events.Post("gate", s => new { reason = s.Apply($"waiting on {Login}") });

        (string Name, string Json)[] sent = await sink.WaitForAsync(2);

        Assert.Equal(new[] { "log", "gate" }, sent.Select(e => e.Name));
        Assert.Equal("{\"message\":\"sent [redacted]\"}", sent[0].Json);
        Assert.Equal("{\"reason\":\"waiting on [redacted]\"}", sent[1].Json);
        Assert.InRange(built, 1, 2);
    }

    // A steady log must not cost a build of the masks (a read on the UI thread)
    // for every entry: the events of one gathering share a build, in order.
    [Fact]
    public async Task Events_ThatArriveTogether_ShareOneBuildOfTheMasks_AndKeepTheirOrder()
    {
        Sink sink = new();
        int built = 0;
        using LocalApiMaskedEvents events = new(
            () => { Interlocked.Increment(ref built); return Task.FromResult(Scrubber()); },
            anyoneListening: () => true, sink.Send, gather: TimeSpan.FromMilliseconds(200));

        // Forty entries posted in one go, well inside one gathering. (They used to
        // be spaced by Task.Delay(2), which under a loaded thread pool resumes tens
        // of milliseconds late and ran the forty over several gatherings: that
        // timed the scheduler, not the batching.)
        for (int i = 0; i < 40; i++)
        {
            int n = i;
            events.Post("log", s => new { message = s.Apply($"entry {n} {Password}") });
        }

        (string Name, string Json)[] sent = await sink.WaitForAsync(40);

        Assert.Equal(Enumerable.Range(0, 40).Select(i => $"{{\"message\":\"entry {i} [redacted]\"}}"), sent.Select(e => e.Json));
        Assert.InRange(built, 1, 3);
    }

    [Fact]
    public void Gates_MaskTheReasonOfATransition_AsEventsDo()
    {
        MudPlay.Game.Map.MovementCoordinator coordinator = new();
        coordinator.AssertGate(MudPlay.Game.Map.MovementCoordinator.SearchGate, "AutoSearch", $"waiting on {Login}");
        coordinator.ClearGate(MudPlay.Game.Map.MovementCoordinator.SearchGate, "AutoSearch", "done");

        string json = Json(LocalApiState.Gates(coordinator, Scrubber()));

        AssertNoAccountDetails(json);
        Assert.Contains("\"reason\": \"waiting on [redacted]\"", json);
        Assert.Contains("\"reason\": \"done\"", json);
        Assert.Contains("\"asserter\": \"AutoSearch\"", json);
    }

    [Fact]
    public async Task Events_WithNobodyListening_QueueNothingAndReadNoAccountDetails()
    {
        Sink sink = new();
        int built = 0;
        bool listening = false;
        using LocalApiMaskedEvents events = new(
            () => { Interlocked.Increment(ref built); return Task.FromResult(Scrubber()); },
            () => listening, sink.Send, gather: TimeSpan.Zero);

        events.Post("log", s => new { message = s.Apply("unheard") });
        listening = true;
        events.Post("log", s => new { message = s.Apply("heard") });

        (string Name, string Json)[] sent = await sink.WaitForAsync(1);

        Assert.Equal("{\"message\":\"heard\"}", Assert.Single(sent).Json);
        Assert.Equal(1, built);
    }

    // What can't be masked is not sent: the stream says how many it lost and why,
    // and carries on once the details can be read again.
    [Fact]
    public async Task Events_WhenTheAccountDetailsCannotBeRead_AreNotSentAndTheStreamSaysSo()
    {
        Sink sink = new();
        bool readable = false;
        using LocalApiMaskedEvents events = new(
            () => readable ? Task.FromResult(Scrubber()) : throw new InvalidOperationException("key file gone"),
            () => true, sink.Send, gather: TimeSpan.Zero);

        events.Post("log", _ => new { message = $"sent {Password}" });
        (string Name, string Json)[] sent = await sink.WaitForAsync(1);

        (string name, string json) = Assert.Single(sent);
        Assert.Equal(LocalApiMaskedEvents.LostEvent, name);
        Assert.Contains("\"count\":1", json);
        Assert.Contains("could not be masked", json);
        AssertNoAccountDetails(json);

        readable = true;
        events.Post("log", s => new { message = s.Apply($"sent {Password}") });
        sent = await sink.WaitForAsync(1);
        Assert.Equal("{\"message\":\"sent [redacted]\"}", sent[^1].Json);
    }
}
