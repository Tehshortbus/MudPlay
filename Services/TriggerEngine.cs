using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Terminal;

namespace MudPlay.Services;

// In-memory cache of the active character's triggers + the app-session named
// variable store, plus the runtime dispatch path.
//
// Three subscription surfaces: ChatRouter.EntryClassified for chat-scoped
// triggers, LineExtractor.LineEmitted for GameMessages (non-chat) lines, and
// LogService.EntryAdded for the SystemLog scope. Chat lines are tracked in a
// small recency queue so the GameMessages path can dedupe them — every line
// goes through LineExtractor, including those ChatRouter already classified.
//
// Wildcards (captured values) are app-session-scoped (in-memory, wiped on app
// close — same lifetime as ChatHistoryStore) so a trigger that captures a value
// on one character can be read by another trigger on a different character within
// the same session. This store belongs to the TRIGGER system alone — it is NOT
// shared with aliases (which compute their own positional {0}/{1} tokens per
// expansion), macros, or events. Keeping the namespace isolated is deliberate:
// {1} in a trigger pattern captures the first match span into THIS store; {1} in
// an alias means the first typed token, a different thing entirely. The
// substitution syntax is {name} in both patterns (capture) and Response / Notify
// text (read); a name is any run of letters, digits, and underscores, so {1},
// {who}, and {tgt2} are all valid wildcards.
public sealed class TriggerEngine
{
    // Recent-classification queue depth — enough to cover bursts without
    // retaining stale entries.
    private const int RecentChatCapacity = 32;

    // Source tag the engine uses on its own log writes — skipped in the
    // SystemLog scope to avoid feedback loops.
    public const string LogSource = "Trigger";

    private readonly ProfileService _profile;
    private readonly ChatRouter? _chat;
    private readonly Action<Action> _onUiThread = OnDispatcher;
    private readonly LogService? _log;

    // Plays a trigger's sound file. Bound by AppServices to the sound engine's
    // trigger cue; unbound, a trigger's sound is only logged.
    public Action<string>? PlaySound { get; set; }
    private LineExtractor? _lines;
    private Action<byte[]>? _sender;
    // Names the game-data set the loaded profile runs on. Only for a profile from
    // before triggers were per character, which takes a copy of that set's list.
    private readonly Func<string?>? _profileSet;

    // Compiled-regex cache, keyed by (match type, raw pattern). Built lazily as
    // triggers fire.
    private readonly Dictionary<(TriggerMatchType Kind, string Pattern), Regex?> _regexCache = new();

    // Recent ChatRouter.EntryClassified snapshots used to dedupe the parallel
    // LineExtractor.LineEmitted path.
    private readonly Queue<(DateTimeOffset At, string Text)> _recentChat = new();

    // The loaded character's triggers — empty when no profile is active.
    public ObservableCollection<Trigger> Triggers { get; } = new();

    // App-session-scoped trigger-wildcard store. Populated by pattern captures;
    // read during Response / Notify interpolation. Trigger-only — see the class
    // comment on why this namespace stays isolated from aliases / macros / events.
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Raised whenever a capture writes the wildcard store (or ClearWildcards
    // empties it) so a live viewer can refresh. Fires on the UI thread — every
    // dispatch path is already marshalled there upstream.
    public event Action? WildcardsChanged;

    public TriggerEngine(ProfileService profile, Func<string?>? profileSet = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _profileSet = profileSet;
        // Before the first load below, which logs what a profile took.
        _log = log;
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        profile.ProfileSaving += SnapshotForSave;
        // Bust the regex cache whenever the trigger list mutates — Save
        // path uses Replace, which swaps in a new Trigger record, so the
        // old cached compile may no longer match the new pattern.
        Triggers.CollectionChanged += (_, _) => _regexCache.Clear();
        if (profile.Current is { } current) LoadFrom(current);
    }

    // Production ctor — wires the chat + log subscriptions at construction time.
    // onUiThread runs a log entry's dispatch on the UI thread; by default the
    // dispatcher's (OnDispatcher).
    public TriggerEngine(ProfileService profile, ChatRouter chat, LogService log, Func<string?>? profileSet = null,
        Action<Action>? onUiThread = null)
        : this(profile, profileSet, log)
    {
        if (onUiThread is not null) _onUiThread = onUiThread;
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(log);
        _chat = chat;
        chat.EntryClassified += OnChatClassified;
        log.EntryAdded       += OnLogEntry;
    }

    // Wire up the LineExtractor source. Called from MainWindowViewModel after
    // the extractor is constructed — AppServices can't reach LineExtractor
    // because it's owned by the main window, not the service container.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
        _lines = lines;
        lines.LineEmitted += OnLineEmitted;
    }

    // Bind the wire-send callback used to deliver a fired trigger's Response to
    // the live telnet connection. Mirrors MacroDispatcher.SetSender; same sender
    // instance is fine.
    public void SetSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _sender = sender;
    }

    // Insert a new trigger and save the profile.
    public void Add(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        Triggers.Add(trigger);
        _profile.Save();
    }

    // Replace an existing trigger by reference. Returns false when the original
    // isn't found.
    public bool Replace(Trigger original, Trigger updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        int index = Triggers.IndexOf(original);
        if (index < 0) return false;
        Triggers[index] = updated;
        _profile.Save();
        return true;
    }

    // Remove a trigger by reference. Returns false if not found.
    public bool Remove(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        bool removed = Triggers.Remove(trigger);
        if (removed) _profile.Save();
        return removed;
    }

    // Empty the wildcard store — the viewer's "Clear" affordance. Fires
    // WildcardsChanged only when there was something to clear so an already-empty
    // store doesn't churn the viewer.
    public void ClearWildcards()
    {
        if (Variables.Count == 0) return;
        Variables.Clear();
        WildcardsChanged?.Invoke();
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
        _onUiThread(() => DispatchScope(TriggerScope.SystemLog, entry.Message));
    }

    private static void OnDispatcher(Action act)
    {
        if (Dispatcher.UIThread.CheckAccess()) act();
        else Dispatcher.UIThread.Post(act);
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

    // The master switch: true means it is off and a matched trigger must not
    // fire (AutoModeController.Blocks counts it). Triggers are the user's own
    // automation, so with the switch off their activation is skipped whole: no
    // capture, no response, no sound (user, 2026-10-09). The skip is logged here,
    // under this engine's own source, because a line logged by anyone else is fed
    // back in to the System-log triggers and one matching it would loop.
    public Func<bool>? BlockedByMasterSwitch { get; set; }

    private void TryFire(Trigger t, string text)
    {
        Regex? regex = GetOrCompile(t);
        if (regex is null) return;

        Match m = regex.Match(text);
        if (!m.Success) return;

        if (BlockedByMasterSwitch?.Invoke() == true)
        {
            _log?.Log(LogSeverity.Debug, LogSource, $"'{t.Name}' matched but not fired — master switch off");
            return;
        }

        // Push captures into the trigger-wildcard store. A Literal pattern emits
        // only the whole-match group plus named captures (no unnamed groups), so
        // every group past index 0 is a user wildcard — numeric names ({1}, {2})
        // included. A Regex pattern can carry unnamed (...) groups, whose Name is
        // their number, so there skip any digit-named group and bind only the
        // user's (?<name>…) captures.
        bool literal = t.MatchType == TriggerMatchType.Literal;
        bool changed = false;
        int index = -1;
        foreach (Group g in m.Groups)
        {
            index++;
            if (index == 0) continue;   // whole-match group, never a wildcard
            if (!g.Success) continue;
            string gname = g.Name;
            if (!literal && (gname.Length == 0 || char.IsDigit(gname[0]))) continue;
            Variables[gname] = g.Value;
            changed = true;
        }
        if (changed) WildcardsChanged?.Invoke();

        // A matched-but-not-sent trigger (undefined wildcard in the Response) is
        // the common "my trigger didn't work" report, so log the match at Debug
        // either way — the send path logs its own outcome via the response.
        _log?.Log(LogSeverity.Debug, LogSource, $"'{t.Name}' matched: {text}");

        // Substitute + send the Response. Empty Response → bare CR.
        if (!TryInterpolate(t.Response, t.Name, out string responseOut)) return;
        SendResponse(responseOut);

        // Optional sound sidecar, played through the Sounds tab's "Trigger sounds"
        // cue (its switch and volume). No OS / toast notifications anywhere in the
        // app, ever.
        if (!string.IsNullOrWhiteSpace(t.SoundFile))
        {
            _log?.Log(LogSeverity.Debug, LogSource, $"'{t.Name}' fired — sound: {t.SoundFile}");
            PlaySound?.Invoke(t.SoundFile);
        }
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

    // Substitute every {name} in template with the current value of that
    // variable. Returns false + logs a warning when any referenced variable is
    // undefined — silent substitution to empty would send broken commands.
    // Matches the rule established for macros.
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

    // Look ahead from start (pointing at {) for a well-formed {name}
    // placeholder. Returns false when the brace doesn't open a valid identifier
    // — caller treats the brace as a literal character.
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

    // A wildcard name is any non-empty run of letters, digits, and underscores.
    // Digit-first names are allowed on purpose so {1} / {2} work as numbered
    // wildcards. A brace pair holding anything else — {HP=10/20}, {a b} — isn't a
    // name and stays literal.
    private static bool IsValidName(string s)
    {
        if (s.Length == 0) return false;
        for (int i = 0; i < s.Length; i++)
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

    // Translate a Literal pattern (with * wildcards and {name} placeholders)
    // into the equivalent .NET regex. Greedy spans so an end-of-pattern {name}
    // captures the rest of the line (non-greedy would settle for one character);
    // multi-capture patterns still resolve correctly because the regex engine
    // backtracks against the intervening literals.
    internal static string LiteralToRegex(string literal)
    {
        StringBuilder sb = new(literal.Length + 16);
        int i = 0;
        while (i < literal.Length)
        {
            char c = literal[i];
            if (c == '{' && TryReadName(literal, i, out string name, out int next))
            {
                // Emit the user's name verbatim as the group name — {who}→(?<who>…),
                // {1}→(?<1>…). .NET accepts a numeric group name; the trigger push
                // loop reads it back by skipping only the whole-match group. Other
                // consumers of this helper (ChatRouter / PartyManager) read the group
                // by its exact name, so the name must stay unmangled.
                sb.Append("(?<").Append(name).Append(">.+)");
                i = next;
                continue;
            }
            if (c == '*')
            {
                sb.Append(".+");
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

    // Apply the loaded profile's triggers: every trigger is the character's own,
    // like its macros and aliases. Triggers were once split between the profile and
    // a list kept with the game-data set, shared by every character on it; a profile
    // that hasn't taken its own copy of that list does so first.
    private void LoadFrom(CharacterProfile profile)
    {
        Triggers.Clear();
        if (!profile.OwnsTriggers) TakeFirstList(profile);
        if (profile.Triggers is null) return;
        foreach (Trigger t in profile.Triggers) Triggers.Add(t);
    }

    // Give a profile the triggers it used to get from outside itself. A character
    // from before the move takes its game-data set's list (or the default ones,
    // where that set never had a list of its own, which is what it was running); a
    // character made since, or the default profile, takes the default ones. Anything
    // the profile already holds under the same name and pattern is kept as it is.
    //
    // Only set on the profile here, not saved: this runs inside the profile load,
    // when the other stores still hold the previous profile's state and a save would
    // write theirs into this one. The next save carries it.
    private void TakeFirstList(CharacterProfile profile)
    {
        bool shared = profile.PredatesOwnLists && _profile.CurrentProfileName is not null;
        string source = AppPaths.DefaultTriggersSeedFile;
        if (shared && _profileSet?.Invoke() is { Length: > 0 } set
            && System.IO.File.Exists(AppPaths.LegacySetTriggersFile(set)))
            source = AppPaths.LegacySetTriggersFile(set);

        List<Trigger> own = profile.Triggers ?? new List<Trigger>();
        HashSet<(string, string)> held = own.Select(t => (t.Name, t.Pattern)).ToHashSet();
        int taken = 0;
        foreach (Trigger t in ReadTriggers(source))
        {
            if (!held.Add((t.Name, t.Pattern))) continue;
            own.Add(t);
            taken++;
        }
        profile.Triggers = own;
        profile.OwnsTriggers = true;
        _log?.Log(LogSeverity.Info, LogSource, shared
            ? $"Took a copy of {taken} trigger(s) that were kept with the game data; they are this character's now."
            : $"Started with the {taken} default trigger(s).");
    }

    private void Clear() => Triggers.Clear();

    private void SnapshotForSave(CharacterProfile profile) => profile.Triggers = Triggers.ToList();

    // The triggers in a file, or none when it is missing or unreadable.
    private List<Trigger> ReadTriggers(string path)
    {
        if (!System.IO.File.Exists(path)) return new List<Trigger>();
        try
        {
            return JsonStore.Load<List<Trigger>>(path) ?? new List<Trigger>();
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, LogSource,
                $"Failed to load triggers from '{path}': {ex.Message}");
            return new List<Trigger>();
        }
    }
}
