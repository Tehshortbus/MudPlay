using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;

namespace FujinTerm.ViewModels.GameData.Edit;

// Per-record edit dialog for Game Data Browser → Triggers. Editable fields cover the full Trigger
// record: Name / Enabled / Scope / MatchType / Pattern / Response, plus an optional Sound sidecar.
//
// Validation is live: name + pattern must be non-empty, and regex patterns must compile. The
// capture-hints panel parses the current pattern for {name} placeholders (Literal mode) or
// (?<name>…) named groups (Regex mode) and lists them so the user knows what's interpolable inside
// Response / Notify. Save returns the updated Trigger; Cancel returns null. Wiring to
// TriggerEngine Add / Replace happens in the caller — this dialog only shapes the record.
public sealed partial class TriggerEditDialogViewModel : ObservableObject, Services.IDialogViewModel<Trigger>
{
    // Regex matching {name} placeholders in a Literal pattern. Same syntax used for substitution.
    private static readonly Regex _literalPlaceholder = new(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    // Regex matching (?<name>…) named groups inside a Regex pattern.
    private static readonly Regex _regexNamedGroup = new(@"\(\?<(?<name>[A-Za-z_][A-Za-z0-9_]*)>", RegexOptions.Compiled);

    public event Action<Trigger?>? CloseRequested;

    private readonly Trigger _original;
    // True for "Add new trigger" flows; false when editing an existing record. Drives the title.
    private readonly bool _isNew;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = string.Empty;

    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchHint))]
    [NotifyPropertyChangedFor(nameof(CaptureHints))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private TriggerMatchType _matchType = TriggerMatchType.Literal;

    [ObservableProperty] private TriggerScope _scope = TriggerScope.GameMessages;

    // Where the trigger persists on disk. Picked by the user in the dialog; the engine routes the
    // record to the right file on Save.
    [ObservableProperty] private TriggerLocation _location = TriggerLocation.GameData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureHints))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _pattern = string.Empty;

    [ObservableProperty] private string _response = string.Empty;
    [ObservableProperty] private string _soundFile = string.Empty;

    // Available enum values for the Scope dropdown.
    public IReadOnlyList<ScopeOption> ScopeOptions { get; } = new[]
    {
        new ScopeOption(TriggerScope.GameMessages,  "Game messages"),
        new ScopeOption(TriggerScope.ChatAny,       "Chat (any)"),
        new ScopeOption(TriggerScope.ChatSay,       "Say"),
        new ScopeOption(TriggerScope.ChatYell,      "Yell"),
        new ScopeOption(TriggerScope.ChatGossip,    "Gossip"),
        new ScopeOption(TriggerScope.ChatTelepath,  "Telepath"),
        new ScopeOption(TriggerScope.ChatGangpath,  "Gangpath"),
        new ScopeOption(TriggerScope.ChatBroadcast, "Broadcast"),
        new ScopeOption(TriggerScope.SystemLog,     "System log"),
    };

    // Available enum values for the Match Type dropdown.
    public IReadOnlyList<MatchOption> MatchOptions { get; } = new[]
    {
        new MatchOption(TriggerMatchType.Literal, "Literal"),
        new MatchOption(TriggerMatchType.Regex,   "Regex"),
    };

    // Available enum values for the Location dropdown.
    public IReadOnlyList<LocationOption> LocationOptions { get; } = new[]
    {
        new LocationOption(TriggerLocation.GameData, "Game data — saves with the active set"),
        new LocationOption(TriggerLocation.Profile,  "Profile — saves with the active character"),
    };

    public string Title => _isNew ? "Trigger — (new)" : $"Trigger — {_original.Name}";

    // Context-sensitive hint under the Pattern field — changes with MatchType.
    public string MatchHint => MatchType switch
    {
        TriggerMatchType.Literal =>
            "Literal — type the text as it appears. " +
            "Use * to wildcard a span. Use {name} to capture into the shared variable cache.",
        TriggerMatchType.Regex =>
            "Regex — full .NET regex. " +
            "Use (?<name>…) named groups to capture into the shared variable cache.",
        _ => string.Empty,
    };

    // Comma-list of capture names parsed from the current pattern. Empty when none.
    public string CaptureHints
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Pattern)) return string.Empty;
            IEnumerable<string> names = MatchType switch
            {
                TriggerMatchType.Literal => _literalPlaceholder.Matches(Pattern)
                    .Select(m => m.Groups["name"].Value),
                TriggerMatchType.Regex   => _regexNamedGroup.Matches(Pattern)
                    .Select(m => m.Groups["name"].Value),
                _ => Enumerable.Empty<string>(),
            };
            string[] unique = names.Distinct(StringComparer.Ordinal).ToArray();
            return unique.Length == 0
                ? string.Empty
                : string.Join("  ", unique.Select(n => $"{{{n}}}"));
        }
    }

    public bool HasError => GetValidationError() is not null;
    public bool CanSave  => !HasError;

    public string StatusMessage
    {
        get
        {
            string? err = GetValidationError();
            if (err is not null) return err;
            return MatchHint;
        }
    }

    public TriggerEditDialogViewModel(Trigger original, bool isNew)
    {
        _original = original;
        _isNew    = isNew;

        Name      = original.Name;
        Enabled   = original.Enabled;
        Scope     = original.Scope;
        MatchType = original.MatchType;
        Pattern   = original.Pattern;
        Response  = original.Response;
        SoundFile = original.SoundFile ?? string.Empty;
        Location  = original.Location;
    }

    // Returns the first validation problem, or null when the record is savable.
    private string? GetValidationError()
    {
        if (string.IsNullOrWhiteSpace(Name))    return "Name is required.";
        if (string.IsNullOrWhiteSpace(Pattern)) return "Pattern is required.";
        if (MatchType == TriggerMatchType.Regex)
        {
            try { _ = new Regex(Pattern); }
            catch (ArgumentException ex) { return $"Regex error: {ex.Message}"; }
        }
        return null;
    }

    [RelayCommand]
    private async Task BrowseSound(Window? owner)
    {
        if (owner?.StorageProvider is not { } storage) return;
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Pick a sound file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio")
                {
                    Patterns = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac" },
                },
                FilePickerFileTypes.All,
            },
        });
        if (picked.Count == 0) return;
        string? path = picked[0].TryGetLocalPath();
        if (path is not null) SoundFile = path;
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;
        Trigger updated = new(
            Name:      Name.Trim(),
            Enabled:   Enabled,
            Scope:     Scope,
            MatchType: MatchType,
            Pattern:   Pattern,
            Response:  Response ?? string.Empty,
            SoundFile: string.IsNullOrWhiteSpace(SoundFile) ? null : SoundFile.Trim(),
            Location:  Location);
        CloseRequested?.Invoke(updated);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    // One scope dropdown row — pairs the enum value with its friendly label.
    public sealed record ScopeOption(TriggerScope Value, string Label);

    // One location dropdown row — pairs the enum value with its friendly label.
    public sealed record LocationOption(TriggerLocation Value, string Label);

    // One match-type dropdown row — pairs the enum value with its friendly label.
    public sealed record MatchOption(TriggerMatchType Value, string Label);
}
