using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using FujinTerm.Game;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Terminal;

namespace FujinTerm.Services;

/// <summary>
/// In-memory cache of the active character's triggers + the app-session
/// named variable store, plus the runtime dispatch path.
/// </summary>
/// <remarks>
/// <para>
/// Three subscription surfaces: <see cref="ChatRouter.EntryClassified"/>
/// for chat-scoped triggers, <see cref="LineExtractor.LineEmitted"/> for
/// <see cref="TriggerScope.GameMessages"/> (non-chat) lines, and
/// <see cref="LogService.EntryAdded"/> for <see cref="TriggerScope.SystemLog"/>.
/// Chat lines are tracked in a small recency queue so the GameMessages
/// path can dedupe them — every line goes through <see cref="LineExtractor"/>,
/// including those <see cref="ChatRouter"/> already classified.
/// </para>
/// <para>
/// Variables are app-session-scoped (in-memory, wiped on app close —
/// same lifetime as <see cref="ChatHistoryStore"/>) so a trigger that
/// captures a value on one character can be read by a trigger or alias
/// on another within the same session. The substitution syntax is
/// <c>{name}</c> in both patterns (capture into the cache) and Response
/// / Notify text (read from the cache).
/// </para>
/// </remarks>
public sealed class TriggerEngine
{
    /// <summary>Recent-classification queue depth — enough to cover bursts without retaining stale entries.</summary>
    private const int RecentChatCapacity = 32;

    /// <summary>Source tag the engine uses on its own log writes — skipped in the SystemLog scope to avoid feedback loops.</summary>
    public const string LogSource = "Trigger";

    private readonly ProfileService? _profile;
    private readonly ChatRouter? _chat;
    private readonly LogService? _log;
    private LineExtractor? _lines;
    private Action<byte[]>? _sender;

    /// <summary>Compiled-regex cache, keyed by (match type, raw pattern). Built lazily as triggers fire.</summary>
    private readonly Dictionary<(TriggerMatchType Kind, string Pattern), Regex?> _regexCache = new();

    /// <summary>Recent <see cref="ChatRouter.EntryClassified"/> snapshots used to dedupe the parallel <see cref="LineExtractor.LineEmitted"/> path.</summary>
    private readonly Queue<(DateTimeOffset At, string Text)> _recentChat = new();

    /// <summary>The loaded character's triggers — empty when no profile is active.</summary>
    public ObservableCollection<Trigger> Triggers { get; } = new();

    /// <summary>App-session-scoped named-variable store. Populated by pattern captures; read during Response / Notify interpolation.</summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TriggerEngine(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        profile.ProfileSaving += SnapshotForSave;
        // Bust the regex cache whenever the trigger list mutates — Save
        // path uses Replace, which swaps in a new Trigger record, so the
        // old cached compile may no longer match the new pattern.
        Triggers.CollectionChanged += (_, _) => _regexCache.Clear();
        if (profile.Current is { } current) LoadFrom(current);
    }

    /// <summary>Production ctor — wires the chat + log subscriptions at construction time.</summary>
    public TriggerEngine(ProfileService profile, ChatRouter chat, LogService log) : this(profile)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(log);
        _chat = chat;
        _log = log;
        chat.EntryClassified += OnChatClassified;
        log.EntryAdded       += OnLogEntry;
    }

    /// <summary>Parameterless ctor for tests / in-memory scenarios — no profile persistence, no live subscriptions.</summary>
    public TriggerEngine() { }

    /// <summary>
    /// Wire up the <see cref="LineExtractor"/> source. Called from
    /// <see cref="ViewModels.MainWindowViewModel"/> after the extractor
    /// is constructed — AppServices can't reach <see cref="LineExtractor"/>
    /// because it's owned by the main window, not the service container.
    /// </summary>
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
        _lines = lines;
        lines.LineEmitted += OnLineEmitted;
    }

    /// <summary>
    /// Bind the wire-send callback used to deliver a fired trigger's
    /// Response to the live telnet connection. Mirrors
    /// <see cref="MacroDispatcher.SetSender"/>; same sender instance is
    /// fine.
    /// </summary>
    public void SetSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _sender = sender;
    }

    /// <summary>Insert a new trigger and persist.</summary>
    public void Add(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        Triggers.Add(trigger);
        _profile?.Save();
    }

    /// <summary>Replace an existing trigger by reference. Persists. <c>false</c> when the original isn't in the list.</summary>
    public bool Replace(Trigger original, Trigger updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        int index = Triggers.IndexOf(original);
        if (index < 0) return false;
        Triggers[index] = updated;
        _profile?.Save();
        return true;
    }

    /// <summary>Remove a trigger by reference. Persists. <c>false</c> if not found.</summary>
    public bool Remove(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        bool removed = Triggers.Remove(trigger);
        if (removed) _profile?.Save();
        return removed;
    }

    // ----- Dispatch -----------------------------------------------------

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        // Skip the status-line prompt — fires on every server update and
        // would spam GameMessages-scoped triggers that match anything.
        if (line.IsPromptLine) return;

        // Lines classified as chat already fired via OnChatClassified —
        // suppress the parallel GameMessages path so a chat-scoped trigger
        // doesn't also fire as a game-message-scoped one.
        if (WasRecentlyClassified(line)) return;

        DispatchScope(TriggerScope.GameMessages, line.Text);
    }

    private void OnChatClassified(ChatLogEntry entry)
    {
        RememberClassified(entry);

        // ChatAny always fires for any classified entry; the specific
        // channel scope fires too when the channel maps to one of our
        // user-facing enum members.
        DispatchScope(TriggerScope.ChatAny, entry.RawText);
        if (MapChannelToScope(entry.Channel) is { } specific && specific != TriggerScope.ChatAny)
            DispatchScope(specific, entry.RawText);
    }

    private void OnLogEntry(LogEntry entry)
    {
        // Skip our own log writes so a trigger that emits a SystemLog
        // entry can't loop into firing itself.
        if (string.Equals(entry.Source, LogSource, StringComparison.Ordinal)) return;

        // LogService fires on the producer's thread; marshal onto the UI
        // thread so the dispatch path stays single-threaded (same place
        // OnLineEmitted / OnChatClassified run from).
        if (Dispatcher.UIThread.CheckAccess())
            DispatchScope(TriggerScope.SystemLog, entry.Message);
        else
            Dispatcher.UIThread.Post(() => DispatchScope(TriggerScope.SystemLog, entry.Message));
    }

    private void DispatchScope(TriggerScope scope, string text)
    {
        foreach (Trigger t in Triggers)
        {
            if (!t.Enabled) continue;
            if (t.Scope != scope) continue;
            TryFire(t, text);
        }
    }

    private void TryFire(Trigger t, string text)
    {
        Regex? regex = GetOrCompile(t);
        if (regex is null) return;

        Match m = regex.Match(text);
        if (!m.Success) return;

        // Push named captures into the shared variable cache. Skip the
        // implicit Group[0] (full match) and any unnamed numbered groups
        // — only {name} placeholders / (?<name>…) named groups bind.
        foreach (Group g in m.Groups)
        {
            if (!g.Success) continue;
            if (string.IsNullOrEmpty(g.Name) || char.IsDigit(g.Name[0])) continue;
            Variables[g.Name] = g.Value;
        }

        // Substitute + send the Response. Empty Response → bare CR.
        if (!TryInterpolate(t.Response, t.Name, out string responseOut)) return;
        SendResponse(responseOut);

        // Optional sidecars — sound + notify are stubs until Phase 13
        // wires actual playback / OS notifications.
        if (!string.IsNullOrWhiteSpace(t.SoundFile))
            _log?.Log(LogSeverity.Debug, LogSource, $"'{t.Name}' fired — would play sound: {t.SoundFile}");

        if (!string.IsNullOrWhiteSpace(t.NotificationText)
            && TryInterpolate(t.NotificationText!, t.Name, out string noteOut))
            _log?.Log(LogSeverity.Info, LogSource, $"'{t.Name}' notify: {noteOut}");
    }

    private void SendResponse(string substituted)
    {
        if (_sender is null) return;
        if (string.IsNullOrWhiteSpace(substituted))
        {
            _sender(new byte[] { (byte)'\r' });
            return;
        }
        foreach (string step in MacroStore.SplitCommandSteps(substituted))
        {
            // Latin-1: BBSes expect 8-bit clean bytes, not UTF-8 — same
            // encoding the terminal uses for ordinary keystrokes.
            byte[] bytes = Encoding.Latin1.GetBytes(step + "\r");
            _sender(bytes);
        }
    }

    /// <summary>
    /// Substitute every <c>{name}</c> in <paramref name="template"/> with
    /// the current value of that variable. Returns <c>false</c> + logs a
    /// warning when any referenced variable is undefined — silent
    /// substitution to empty would send broken commands. Matches the
    /// rule established for macros.
    /// </summary>
    internal bool TryInterpolate(string template, string triggerName, out string result)
    {
        if (string.IsNullOrEmpty(template))
        {
            result = string.Empty;
            return true;
        }

        StringBuilder sb = new(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '{' && TryReadName(template, i, out string name, out int next))
            {
                if (!Variables.TryGetValue(name, out string? value))
                {
                    _log?.Log(LogSeverity.Warn, LogSource,
                        $"'{triggerName}' skipped — variable {{{name}}} is undefined.");
                    result = string.Empty;
                    return false;
                }
                sb.Append(value);
                i = next;
                continue;
            }
            sb.Append(c);
            i++;
        }
        result = sb.ToString();
        return true;
    }

    /// <summary>
    /// Look ahead from <paramref name="start"/> (pointing at <c>{</c>) for
    /// a well-formed <c>{name}</c> placeholder. Returns <c>false</c> when
    /// the brace doesn't open a valid identifier — caller treats the
    /// brace as a literal character.
    /// </summary>
    private static bool TryReadName(string s, int start, out string name, out int nextIndex)
    {
        name = string.Empty;
        nextIndex = start + 1;
        int end = s.IndexOf('}', start + 1);
        if (end <= start + 1) return false;
        string candidate = s.Substring(start + 1, end - start - 1);
        if (!IsValidName(candidate)) return false;
        name = candidate;
        nextIndex = end + 1;
        return true;
    }

    private static bool IsValidName(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        for (int i = 1; i < s.Length; i++)
            if (!char.IsLetterOrDigit(s[i]) && s[i] != '_') return false;
        return true;
    }

    private Regex? GetOrCompile(Trigger t)
    {
        var key = (t.MatchType, t.Pattern);
        if (_regexCache.TryGetValue(key, out Regex? cached)) return cached;
        Regex? compiled = TryCompile(t);
        _regexCache[key] = compiled;
        return compiled;
    }

    private Regex? TryCompile(Trigger t)
    {
        if (string.IsNullOrEmpty(t.Pattern)) return null;
        try
        {
            string regex = t.MatchType == TriggerMatchType.Literal
                ? LiteralToRegex(t.Pattern)
                : t.Pattern;
            return new Regex(regex, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            _log?.Log(LogSeverity.Warn, LogSource,
                $"'{t.Name}' has an invalid pattern — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Translate a Literal pattern (with <c>*</c> wildcards and
    /// <c>{name}</c> placeholders) into the equivalent .NET regex.
    /// Non-greedy spans so multiple captures in one line work as
    /// expected. Internal for test visibility.
    /// </summary>
    internal static string LiteralToRegex(string literal)
    {
        StringBuilder sb = new(literal.Length + 16);
        int i = 0;
        while (i < literal.Length)
        {
            char c = literal[i];
            if (c == '{' && TryReadName(literal, i, out string name, out int next))
            {
                sb.Append("(?<").Append(name).Append(">.+?)");
                i = next;
                continue;
            }
            if (c == '*')
            {
                sb.Append(".+?");
                i++;
                continue;
            }
            sb.Append(Regex.Escape(c.ToString()));
            i++;
        }
        return sb.ToString();
    }

    private static TriggerScope? MapChannelToScope(ChatChannel channel) => channel switch
    {
        ChatChannel.Gossip            => TriggerScope.ChatGossip,
        ChatChannel.Local             => TriggerScope.ChatSay,
        ChatChannel.TelepathIncoming  => TriggerScope.ChatTelepath,
        ChatChannel.TelepathOutgoing  => TriggerScope.ChatTelepath,
        ChatChannel.Gangpath          => TriggerScope.ChatGangpath,
        ChatChannel.Broadcast         => TriggerScope.ChatBroadcast,
        ChatChannel.Yell              => TriggerScope.ChatYell,
        // RealmEvent + DaySeparator deliberately drop through — they
        // fire only the ChatAny scope, not any specific channel.
        _                             => null,
    };

    private void RememberClassified(ChatLogEntry entry)
    {
        _recentChat.Enqueue((entry.Timestamp, entry.RawText));
        while (_recentChat.Count > RecentChatCapacity) _recentChat.Dequeue();
    }

    private bool WasRecentlyClassified(LineExtractor.EmittedLine line)
    {
        foreach ((DateTimeOffset at, string text) in _recentChat)
        {
            if (at == line.Timestamp && string.Equals(text, line.Text, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ----- Profile sync ---------------------------------------------------

    private void LoadFrom(CharacterProfile profile)
    {
        Triggers.Clear();
        if (profile.Triggers is null) return;
        foreach (Trigger t in profile.Triggers) Triggers.Add(t);
    }

    private void Clear() => Triggers.Clear();

    /// <summary>Snapshot the live list onto the profile DTO right before save.</summary>
    private void SnapshotForSave(CharacterProfile profile)
    {
        profile.Triggers = Triggers.Count == 0 ? null : Triggers.ToList();
    }
}
