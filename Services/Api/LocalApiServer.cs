using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using MudPlay.Game.Map;
using MudPlay.Terminal;

namespace MudPlay.Services.Api;

// Loopback-only HTTP control API for the running client. Off unless
// Settings.Global.LocalApiEnabled is set — it opens a listening socket, so it is
// opt-in rather than something a user discovers they've been running.
//
// Why this exists: diagnosing a wedged client meant waiting for it to break,
// capturing a bug report, and reconstructing the cause from a 750-entry log tail
// that had usually already scrolled past the failure. The state that would have
// answered the question was in memory the whole time and simply unreachable.
//
// THREADING. HttpListener hands each request to a thread-pool thread. Nearly all
// game state is UI-thread-confined (RoomTracker, the engines, the view-models),
// so every handler that touches it goes through Dispatcher.UIThread.InvokeAsync
// and awaits. The accept loop itself never touches game state, and handlers must
// stay short — this borrows the render thread.
//
// HttpListener on Windows is backed by http.sys, where a non-admin process needs
// a `netsh http add urlacl` reservation to bind. On macOS/Linux (this project's
// primary targets) it's a managed socket listener with no such requirement. The
// bind failure is caught and surfaced rather than crashing; see Start.
public sealed class LocalApiServer : IAsyncDisposable
{
    public const string LogCategory = "LocalApi";

    // MMUD on a phone keypad.
    public const int DefaultPort = 6683;

    private readonly AppServices _services;
    private readonly LogService _log;
    private readonly Func<TerminalEmulator?> _emulator;

    // The game's lines, numbered for /scrollback?since=. Fed by the main view
    // model's LineExtractor, which is built after us.
    public LocalApiLines Lines { get; } = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public LocalApiServer(AppServices services, LogService log, Func<TerminalEmulator?> emulator)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(emulator);
        _services = services;
        _log = log;
        _emulator = emulator;
        Auth = new LocalApiAuth(Path.Combine(AppPaths.DataRoot, ".apitoken"), log);
        Events = new LocalApiEvents(log);
    }

    public LocalApiAuth Auth { get; }
    public LocalApiEvents Events { get; }

    public bool IsListening => _listener?.IsListening == true;
    public int Port { get; private set; }

    // Why the last Start attempt failed, for the bug report and the settings UI.
    // Null when listening or never started.
    public string? LastStartError { get; private set; }

    public void Start(int port)
    {
        if (IsListening) return;
        Port = port <= 0 || port > 65535 ? DefaultPort : port;
        LastStartError = null;

        try
        {
            // Materialise the token before accepting anything. It's lazy, and
            // Reject() short-circuits on a missing Authorization header without
            // ever reading it — so without this the file only appeared after a
            // successful request, which can't happen until the user can read the
            // file. Chicken and egg.
            _ = Auth.Token;

            HttpListener listener = new();
            // 127.0.0.1 explicitly, never '+' or '*': the prefix IS the primary
            // access control, and a wildcard would expose the character to the
            // whole network.
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            AttachEventSources();
            _log.Info(LogCategory, $"listening on http://127.0.0.1:{Port}/ (loopback only, token required).");
        }
        catch (Exception ex)
        {
            LastStartError = ex.Message;
            _listener = null;
            // A port already in use or a missing Windows urlacl must not take the
            // client down — the API is a diagnostic, not a dependency.
            _log.Error(LogCategory, $"couldn't listen on 127.0.0.1:{Port} — {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_listener is null) return;
        _cts?.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { /* shutting down */ }
        _listener = null;
        // ApplySettings cycles Stop/Start on every toggle or port edit, so the
        // outgoing source has to go with it rather than being left to the finaliser.
        _cts?.Dispose();
        _cts = null;
        _log.Info(LogCategory, "stopped listening.");
    }

    // Apply the current settings — called on startup and whenever the user
    // changes the toggle or the port, so the socket follows the setting without
    // a restart.
    public void ApplySettings(bool enabled, int port)
    {
        if (!enabled) { Stop(); return; }
        if (IsListening && Port == port) return;
        Stop();
        Start(port);
    }

    // Subscribe the event stream to the signals worth watching live. Attached on
    // the first successful Start and left attached: LogService and the
    // coordinator outlive every listen/stop cycle, and LocalApiEvents no-ops when
    // nobody is subscribed, so there's nothing to gain by unsubscribing.
    private void AttachEventSources()
    {
        if (_eventsAttached) return;
        _eventsAttached = true;

        // Both kinds go through the masking pump, in the order they were made:
        // a log message can quote anything, and a gate's reason is free text.
        LocalApiMaskedEvents masked = new(BuildScrubberAsync, () => Events.HasSubscribers, Events.Send);
        _maskedEvents = masked;

        _log.EntryAdded += e => masked.Post("log", scrubber => new
        {
            at = e.Timestamp,
            severity = e.Severity.ToString(),
            source = e.Source,
            message = scrubber.Apply(e.Message),
        });

        // The signal that mattered most in every wedge so far: which gate just
        // went on or off, who did it, and why. GatesChanged carries no payload,
        // but RecordTransition appends to History before raising it, so the newest
        // history entry IS the transition being announced.
        _services.MovementCoordinator.GatesChanged += () =>
        {
            if (_services.MovementCoordinator.History is not { Count: > 0 } h) return;
            GateTransitionEntry t = h[^1];
            // Read here, on the thread that changed the gates; only the masking
            // waits for the pump.
            string[] assertedNow = _services.MovementCoordinator.AssertedGates.ToArray();
            masked.Post("gate", scrubber => new
            {
                at = t.Timestamp,
                gate = t.Gate,
                action = t.Asserted ? "assert" : "clear",
                asserter = t.Asserter,
                reason = t.Reason is { } reason ? scrubber.Apply(reason) : null,
                assertedNow,
            });
        };
    }

    private bool _eventsAttached;
    private LocalApiMaskedEvents? _maskedEvents;

    // How long the masks wait for the UI thread before they are built without it.
    private static readonly TimeSpan ScrubberUiWait = TimeSpan.FromMilliseconds(500);

    // The masks for one response (or one run of events), built from the saved
    // account details as they stand now and dropped by the caller after it: the
    // scrubber holds the values in clear, so none is kept between requests.
    //
    // The saved values belong to the UI thread, so that is where they are read.
    // But the log is the one thing still readable while that thread is wedged,
    // which is what this API is for, and it must be masked then too: after a
    // short wait the values are read from here instead. A thread that isn't
    // running isn't changing them.
    private async Task<BugReportScrub.Scrubber> BuildScrubberAsync()
    {
        BugReportScrub.Scrubber Build() => BugReportBuilder.BuildScrubber(_services, compiled: false);
        if (Dispatcher.UIThread.CheckAccess()) return Build();
        DispatcherOperation<BugReportScrub.Scrubber> queued = Dispatcher.UIThread.InvokeAsync(Build);
        Task<BugReportScrub.Scrubber> onUi = queued.GetTask();
        if (await Task.WhenAny(onUi, Task.Delay(ScrubberUiWait)).ConfigureAwait(false) == onUi)
            return await onUi.ConfigureAwait(false);
        // No longer waited for, so taken off the dispatcher's queue: left there,
        // every request made while the UI thread was wedged would run, for
        // nobody, the moment it came back. One that has already started can't be
        // taken off; what it then throws has no reader, so it is read here.
        queued.Abort();
        _ = onUi.ContinueWith(static late => _ = late.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return Build();
    }

    // The masks for a response, or null after answering 503: what can't be masked
    // is not sent (the rule a bug report follows).
    private async Task<BugReportScrub.Scrubber?> ScrubberOrRefuseAsync(HttpListenerResponse res, string what)
    {
        try { return await BuildScrubberAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log.Warn(LogCategory, $"{what} was not sent: the saved account details could not be read, so it could not be masked ({ex.GetType().Name}).");
            WriteJson(res, 503, new { error = $"the saved account details could not be read, so {what} could not be masked" });
            return null;
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }        // Stop() closed it
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            // Each request on its own task so one slow SSE subscriber can't stall
            // the accept loop.
            _ = Task.Run(() => HandleSafelyAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext ctx)
    {
        try { await HandleAsync(ctx).ConfigureAwait(false); }
        catch (UiUnresponsiveException ex)
        {
            // Not an internal error — a finding. Say it plainly in the body: a
            // caller chasing a wedge gets the answer from the status alone.
            _log.Warn(LogCategory, $"state read abandoned — {ex.Message}");
            try { WriteJson(ctx.Response, 504, new { error = "ui thread unresponsive", waitedSeconds = ex.Waited.TotalSeconds }); }
            catch { /* peer gone */ }
        }
        catch (Exception ex)
        {
            _log.Warn(LogCategory, $"request failed: {ex.Message}");
            try { WriteJson(ctx.Response, 500, new { error = "internal error" }); } catch { /* peer gone */ }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;
        string path = req.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        if (path.Length == 0) path = "/";

        // /health is the one open route. It answers "is MudPlay up?" and nothing
        // else — no character, no state — so it's safe to leave unauthenticated
        // and lets a caller distinguish "not running" from "wrong token".
        if (path == "/health")
        {
            WriteJson(res, 200, new
            {
                ok = true,
                version = AppInfo.Version,
                listening = true,
                authRequired = true,
            });
            return;
        }

        if (Auth.Reject(req.RemoteEndPoint, req.Headers["Authorization"]) is { } why)
        {
            // Warn, not Debug: a rejected request on a loopback-only port is
            // either a misconfigured tool or something probing, and both are
            // worth seeing in a capture.
            _log.Warn(LogCategory, $"rejected {req.HttpMethod} {path} — {why}");
            res.Headers["WWW-Authenticate"] = "Bearer";
            WriteJson(res, 401, new { error = "unauthorized" });
            return;
        }

        _log.Debug(LogCategory, $"{req.HttpMethod} {path}{req.Url?.Query}");

        bool isPost = string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);
        if (isPost)
        {
            await HandlePostAsync(req, res, path).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(res, 405, new { error = "only GET and POST are supported", method = req.HttpMethod });
            return;
        }

        switch (path)
        {
            case "/state":
                WriteJson(res, 200, await OnUiAsync(() => LocalApiState.Snapshot(_services)).ConfigureAwait(false));
                return;

            case "/state/full":
            {
                if (_emulator() is not { } emu)
                {
                    WriteJson(res, 503, new { error = "no terminal session yet" });
                    return;
                }
                bool markdown = string.Equals(req.QueryString["format"], "markdown", StringComparison.OrdinalIgnoreCase);
                if (markdown)
                {
                    string md = await OnUiAsync(() => LocalApiState.FullMarkdown(_services, emu)).ConfigureAwait(false);
                    WriteText(res, 200, md, "text/markdown; charset=utf-8");
                }
                else
                {
                    WriteJson(res, 200, await OnUiAsync(() => LocalApiState.Full(_services, emu)).ConfigureAwait(false));
                }
                return;
            }

            case "/stats":
                WriteJson(res, 200, await OnUiAsync(() => LocalApiState.Stats(_services)).ConfigureAwait(false));
                return;

            case "/gates":
            {
                if (await ScrubberOrRefuseAsync(res, "the gate history").ConfigureAwait(false) is not { } scrubber) return;
                WriteJson(res, 200, await OnUiAsync(() =>
                    LocalApiState.Gates(_services.MovementCoordinator, scrubber)).ConfigureAwait(false));
                return;
            }

            case "/commands":
                // Self-describing surface: what can be dispatched, and which
                // entries need the destructive opt-in, so a caller can tell the
                // difference without trying one and reading a 403.
                WriteJson(res, 200, new
                {
                    allowDestructive = _services.Settings.Current.LocalApiAllowDestructive,
                    commands = Game.Remote.RemoteCommandCatalog.Map
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new
                        {
                            command = kv.Key,
                            category = kv.Value.ToString(),
                            destructive = LocalApiActions.IsDestructive(kv.Key),
                            pathChannelOnly = Game.Remote.RemoteCommandCatalog.IsPathChannelOnly(kv.Key),
                        })
                        .ToArray(),
                });
                return;

            case "/log":
            {
                long since = ParseLong(req.QueryString["since"], 0);
                int limit = Math.Clamp(ParseInt(req.QueryString["limit"], 500), 1, 5000);
                IReadOnlySet<LogSeverity> severities = ParseSeverities(req.QueryString["severity"]);
                string? source = req.QueryString["source"];
                if (await ScrubberOrRefuseAsync(res, "the log").ConfigureAwait(false) is not { } scrubber) return;
                // The ring is lock-guarded and LogEntry is immutable, so the read
                // itself needs no dispatcher hop.
                WriteJson(res, 200, LocalApiState.Log(_log, scrubber, since, limit, severities, source));
                return;
            }

            case "/scrollback":
            {
                if (_emulator() is not { } emu)
                {
                    WriteJson(res, 503, new { error = "no terminal session yet" });
                    return;
                }
                int lines = Math.Clamp(ParseInt(req.QueryString["lines"], 750), 1, 5000);
                if (await ScrubberOrRefuseAsync(res, "the scrollback").ConfigureAwait(false) is not { } scrubber) return;
                if (req.QueryString["since"] is { } sinceText)
                {
                    long since = Math.Max(0, ParseLong(sinceText, 0));
                    WriteJson(res, 200, await OnUiAsync(() =>
                        LocalApiState.ScrollbackSince(Lines, _services.InGameCapture.Window, scrubber, since, lines)).ConfigureAwait(false));
                    return;
                }
                WriteJson(res, 200, await OnUiAsync(() =>
                    LocalApiState.Scrollback(emu, _services.InGameCapture.Window, scrubber, lines, Lines.NewestSeq)).ConfigureAwait(false));
                return;
            }

            case "/loops":
                WriteJson(res, 200, await OnUiAsync(() => LocalApiCatalog.Loops(_services)).ConfigureAwait(false));
                return;

            case "/events":
                await StreamEventsAsync(res).ConfigureAwait(false);
                return;

            default:
                await HandleParameterisedGetAsync(res, path).ConfigureAwait(false);
                return;
        }
    }

    // Action routes. Each marshals to the UI thread before touching an engine.
    private async Task HandlePostAsync(HttpListenerRequest req, HttpListenerResponse res, string path)
    {
        // Resolved per request, not cached: the user can flip the destructive
        // opt-in while a caller is connected, and the next request must see it.
        bool allowDestructive = _services.Settings.Current.LocalApiAllowDestructive;

        switch (path)
        {
            case "/command":
            {
                if (await ReadJsonAsync<CommandRequest>(req, res).ConfigureAwait(false) is not { } body) return;
                if (string.IsNullOrWhiteSpace(body.Command))
                {
                    WriteJson(res, 400, new { error = "command is required" });
                    return;
                }
                LocalApiActions.CommandOutcome outcome = await OnUiAsync(() =>
                    LocalApiActions.Command(_services, body.Command, body.Args, allowDestructive))
                    .ConfigureAwait(false);
                // 403 for the destructive refusal specifically — it's a policy
                // decision the caller can act on (flip the setting), unlike a
                // malformed request.
                int status = outcome.Ok ? 200
                    : outcome.Status.StartsWith("destructive", StringComparison.Ordinal) ? 403
                    : 400;
                WriteJson(res, status, outcome);
                return;
            }

            case "/send":
            {
                if (await ReadJsonAsync<SendRequest>(req, res).ConfigureAwait(false) is not { } body) return;
                LocalApiActions.CommandOutcome outcome = await OnUiAsync(() =>
                    LocalApiActions.Send(_services, body.Line ?? string.Empty, allowDestructive))
                    .ConfigureAwait(false);
                int status = outcome.Ok ? 200 : allowDestructive ? 400 : 403;
                WriteJson(res, status, outcome);
                return;
            }

            default:
                WriteJson(res, 404, new { error = "no such endpoint", path });
                return;
        }
    }

    private sealed record CommandRequest(string? Command, string[]? Args);
    private sealed record SendRequest(string? Line);

    // Read and deserialise a JSON body, answering 400 and returning null if it
    // can't be parsed — so every caller doesn't repeat the same guard.
    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest req, HttpListenerResponse res)
        where T : class
    {
        try
        {
            using StreamReader reader = new(req.InputStream, Encoding.UTF8);
            string raw = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                WriteJson(res, 400, new { error = "a JSON body is required" });
                return null;
            }
            T? parsed = JsonSerializer.Deserialize<T>(raw, LocalApiJson.Options);
            if (parsed is null) WriteJson(res, 400, new { error = "body did not parse" });
            return parsed;
        }
        catch (JsonException ex)
        {
            WriteJson(res, 400, new { error = "malformed JSON", detail = ex.Message });
            return null;
        }
    }

    // Routes carrying a value in the path. Kept out of the switch above because a
    // switch can only match constants, and these need prefix matching.
    //
    // Segments are unescaped explicitly: loop names contain spaces and brackets
    // ("Storm Mountain Path (Top Third)"), so a caller must percent-encode them
    // and we must decode before looking anything up.
    private async Task HandleParameterisedGetAsync(HttpListenerResponse res, string path)
    {
        if (TrySegment(path, "/loops/", out string loopName))
        {
            object? loop = await OnUiAsync(() => LocalApiCatalog.Loop(_services, loopName)).ConfigureAwait(false);
            if (loop is null) WriteJson(res, 404, new { error = "no such loop", name = loopName });
            else WriteJson(res, 200, loop);
            return;
        }

        if (TrySegment(path, "/monsters/", out string monsterRaw))
        {
            if (!int.TryParse(monsterRaw, out int id))
            {
                WriteJson(res, 400, new { error = "monster id must be a number", got = monsterRaw });
                return;
            }
            object? mon = await OnUiAsync(() => LocalApiCatalog.Monster(_services, id)).ConfigureAwait(false);
            if (mon is null) WriteJson(res, 404, new { error = "no such monster", id });
            else WriteJson(res, 200, mon);
            return;
        }

        if (TrySegment(path, "/rooms/", out string roomRaw))
        {
            string[] parts = roomRaw.Split('/', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int map) || !int.TryParse(parts[1], out int room))
            {
                WriteJson(res, 400, new { error = "expected /rooms/{map}/{room}", got = roomRaw });
                return;
            }
            WriteJson(res, 200, await OnUiAsync(() =>
                LocalApiCatalog.Room(_services, new RoomKey(map, room))).ConfigureAwait(false));
            return;
        }

        WriteJson(res, 404, new { error = "no such endpoint", path });
    }

    private static bool TrySegment(string path, string prefix, out string rest)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = string.Empty;
            return false;
        }
        rest = Uri.UnescapeDataString(path[prefix.Length..]);
        return rest.Length > 0;
    }

    // Holds the response open until the reader disconnects. The task parked here
    // is why each request runs on its own task.
    private async Task StreamEventsAsync(HttpListenerResponse res)
    {
        Guid id = Events.Add(res);
        if (id == Guid.Empty)
        {
            // At the concurrent-stream limit. Say so rather than handing back a
            // connection that would never carry an event.
            WriteJson(res, 503, new { error = "too many event subscribers", active = Events.SubscriberCount });
            return;
        }
        _log.Info(LogCategory, $"event subscriber attached ({Events.SubscriberCount} active).");
        try
        {
            // Nothing to do but wait: LocalApiEvents writes to the stream from
            // wherever events originate. The heartbeat is what detects a dead
            // reader — a failed write retires the subscription, which drops us out
            // of this loop.
            while (Events.Contains(id) && IsListening)
                await Task.Delay(1000).ConfigureAwait(false);
        }
        finally
        {
            Events.Remove(id);
            _log.Info(LogCategory, $"event subscriber detached ({Events.SubscriberCount} active).");
        }
    }

    // How long a state read waits for the UI thread before giving up. Generous
    // for a read that normally lands in microseconds; short enough that a caller
    // gets an answer rather than a hang.
    private static readonly TimeSpan UiReadTimeout = TimeSpan.FromSeconds(5);

    // Marshal onto the UI thread and bring the value back. Everything reading
    // game state goes through here — see the threading note on the class.
    //
    // Bounded on purpose. A wedged UI thread is the headline case this API exists
    // to investigate, and an unbounded InvokeAsync would hang precisely then:
    // every request parked forever holding its connection and its thread-pool
    // task, so the more the user polled to find out what was stuck, the more they
    // piled up. Timing out turns that into the most useful answer the API can
    // give — 504 plus "the UI thread isn't responding", which IS the diagnosis.
    // The abandoned read still runs whenever the dispatcher recovers; it only
    // reads state, so nothing minds it arriving late.
    //
    // A request may read game data, so the startup draft's put-off set is built
    // first (AppServices.BuildDeferredGameData), outside the bound: that build takes
    // seconds, and the first request must not time out on it.
    private static async Task<T> OnUiAsync<T>(Func<T> read)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            BuildDeferredGameData();
            return read();
        }
        if (AppServices.CurrentOrNull?.GameData.BuildDeferred == true)
            await Dispatcher.UIThread.InvokeAsync(BuildDeferredGameData).GetTask().ConfigureAwait(false);
        Task<T> pending = Dispatcher.UIThread.InvokeAsync(read).GetTask();
        if (await Task.WhenAny(pending, Task.Delay(UiReadTimeout)).ConfigureAwait(false) != pending)
            throw new UiUnresponsiveException(UiReadTimeout);
        return await pending.ConfigureAwait(false);
    }

    private static void BuildDeferredGameData() =>
        AppServices.CurrentOrNull?.BuildDeferredGameData("a local API request");

    // Signals "the dispatcher never got to us", mapped to 504 by HandleSafelyAsync.
    private sealed class UiUnresponsiveException(TimeSpan waited)
        : Exception($"UI thread did not respond within {waited.TotalSeconds:0.#}s")
    {
        public TimeSpan Waited { get; } = waited;
    }

    private static void WriteJson(HttpListenerResponse res, int status, object payload)
        => WriteText(res, status, JsonSerializer.Serialize(payload, LocalApiJson.Options),
            "application/json; charset=utf-8");

    private static void WriteText(HttpListenerResponse res, int status, string body, string contentType)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        // Belt and braces against a browser treating a response as a page.
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }

    private static long ParseLong(string? raw, long fallback)
        => long.TryParse(raw, out long v) && v >= 0 ? v : fallback;

    private static int ParseInt(string? raw, int fallback)
        => int.TryParse(raw, out int v) ? v : fallback;

    // Comma-separated severity names, matched as a SET — see LocalApiState.Log
    // for why this can't be a minimum. `?severity=warn,error` gets the two that
    // matter without dragging in combat traces.
    //
    // Unrecognised names are skipped rather than rejected: a typo shouldn't turn
    // a diagnostic request into an error. An entirely unrecognised value yields
    // the empty set, i.e. no filtering — the caller sees everything rather than
    // an empty result they might read as "nothing happened".
    public static IReadOnlySet<LogSeverity> ParseSeverities(string? raw)
    {
        HashSet<LogSeverity> set = [];
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // IsDefined as well as TryParse: TryParse happily accepts the string
            // form of any integer, so `?severity=99` would parse to (LogSeverity)99
            // and make the set non-empty — filtering everything out and returning
            // zero entries, which reads as "nothing happened". That's the exact
            // outcome the empty-set fallback above exists to avoid.
            if (Enum.TryParse(part, ignoreCase: true, out LogSeverity s) && Enum.IsDefined(s))
                set.Add(s);
        }
        return set;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        _maskedEvents?.Dispose();
        Events.Dispose();
        if (_acceptLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch { /* cancelled */ }
        }
        _cts?.Dispose();
    }
}
