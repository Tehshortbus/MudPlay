using System.ComponentModel;
using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

// "Statline" tab — modelled on MegaMUD's Statline dialog: a read-only Current
// Statline preview, the editable Statline Command (default full), and a
// Customize dropdown that appends wildcard tokens to the command box. The saved
// command is the single source of truth — it's both what we send to the BBS
// (set statline …) and what the prompt parser is generated from, so the editor
// and the on-wire prompt can't drift. The grammar that interprets the command
// string lives in StatlineSyntax.
public sealed partial class StatlineSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Statline";

    private readonly ProfileService _profile;
    private readonly PlayerState _playerState;
    private readonly Func<string, Task<bool>>? _sendText;
    private string _appliedCommand = StatlineSyntax.Default;
    private bool _suppressDirty = true;
    private bool _dirty;
    private Control? _view;

    public override string Id => "statline";
    public override string Title => "Statline";
    public override bool IsDirty => _dirty;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Statline", "Prompt", "HP", "MA", "Set statline", "Wildcards",
    };

    public override Control View => _view ??= new StatlineSectionView { DataContext = this };

    // True when any character profile is loaded.
    public bool HasProfile => _profile.Current is not null;

    // What goes on the wire after "set statline". Default is full; user can type
    // a raw wildcard string or "full custom <wildcards>".
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStatlinePreview))]
    private string _command = StatlineSyntax.Default;

    // Read-only preview that mirrors how the BBS will render the prompt once
    // Command is in effect. Synthesised from live PlayerState values where
    // available, falling back to sample numbers so the editor stays useful
    // pre-connect.
    public string CurrentStatlinePreview => RenderPreview(Command);

    // Picker selection driving the Add button.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddWildcardCommand))]
    private WildcardItem? _selectedWildcard;

    // Catalogue of wildcards offered by the Customize dropdown.
    public IReadOnlyList<WildcardItem> AvailableWildcards { get; } = BuildCatalogue();

    public StatlineSectionViewModel(
        ProfileService profile,
        PlayerState playerState,
        Func<string, Task<bool>>? sendText)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(playerState);
        _profile = profile;
        _playerState = playerState;
        _sendText = sendText;

        _profile.ProfileLoaded += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosedExternally;
        _playerState.PropertyChanged += OnPlayerStateChanged;
        OnDispose(() =>
        {
            _profile.ProfileLoaded -= OnProfileChanged;
            _profile.ProfileClosed -= OnProfileClosedExternally;
            _playerState.PropertyChanged -= OnPlayerStateChanged;
        });

        LoadFromProfile();
        _suppressDirty = false;
    }

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        // Treat "full" / blank as null on disk so the JSON stays minimal
        // and round-trips back to the default in the editor.
        string? toStore = StatlineSyntax.IsDefault(Command) ? null : Command;
        StatlineSettings dto = new() { Command = toStore };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();
        _profile.NotifyMutated();

        // If the command changed since this section loaded (i.e. the user
        // actually edited something), push the new statline to the BBS so
        // the in-game prompt matches what we just persisted. Fire-and-
        // forget — the send is async but the Apply contract is sync.
        if (!string.Equals(_appliedCommand, Command, StringComparison.Ordinal))
        {
            string wire = StatlineSyntax.NormalizeForWire(Command);
            _ = _sendText?.Invoke($"set statline {wire}\r");
            _appliedCommand = Command;
        }

        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    // Append the selected wildcard token to the command box.
    [RelayCommand(CanExecute = nameof(CanAddWildcard))]
    private void AddWildcard()
    {
        if (SelectedWildcard is null) return;

        // If user hasn't customised yet (command is "full" or blank),
        // promote them to a `full custom ` prefix before appending so the
        // first token forms a valid custom statline.
        if (StatlineSyntax.IsDefault(Command))
        {
            Command = "full custom " + SelectedWildcard.Code;
        }
        else
        {
            Command += SelectedWildcard.Code;
        }
    }

    private bool CanAddWildcard() => SelectedWildcard is not null;

    // Reset the command box to the class default (full).
    [RelayCommand]
    private void ResetToDefault() => Command = StatlineSyntax.Default;

    private void OnProfileChanged(CharacterProfile _) => ReloadAfterProfileSwap();
    private void OnProfileClosedExternally() => ReloadAfterProfileSwap();

    private void ReloadAfterProfileSwap()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
        OnPropertyChanged(nameof(HasProfile));
    }

    private void OnPlayerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Any live HP / MA / position / class change can shift the preview.
        OnPropertyChanged(nameof(CurrentStatlinePreview));
    }

    private void LoadFromProfile()
    {
        StatlineSettings dto = ReadOrDefault();
        Command = string.IsNullOrWhiteSpace(dto.Command) ? StatlineSyntax.Default : dto.Command!;
        _appliedCommand = Command;
    }

    private StatlineSettings ReadOrDefault()
    {
        CharacterProfile? profile = _profile.Current;
        if (profile?.Settings is null) return new();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json)) return new();
        return JsonSerializer.Deserialize<StatlineSettings>(json.GetRawText()) ?? new();
    }

    private void Dirty()
    {
        if (_suppressDirty || _dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void ClearDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnCommandChanged(string value) => Dirty();

    // ----- Wildcard rendering -------------------------------------------

    private string RenderPreview(string? command)
    {
        // Default / blank has no fixed template (it's class-dependent), so
        // synthesise the class default; otherwise pull the bare wildcard
        // template out of the command via the shared grammar.
        string template = StatlineSyntax.IsDefault(command)
            ? DefaultTemplateForClass()
            : StatlineSyntax.ExtractTemplate(command!);

        return ApplyWildcards(template);
    }

    private string DefaultTemplateForClass() => _playerState.ManaType switch
    {
        ManaType.Kai  => "[HP=%h/KAI=%m]: %r",
        ManaType.None => "[HP=%h]: %r",
        _             => "[HP=%h/MA=%m]: %r",
    };

    private string ApplyWildcards(string template)
    {
        // Drop colour / formatting codes from the plain-text preview —
        // they render as ANSI escapes on the wire, never plain text.
        string s = StatlineSyntax.StripFormatting(template);

        // Sample values stand in for unobserved live state so the preview
        // is informative pre-connect. Once PromptParser has seen a status
        // line we use the real numbers.
        int hp     = _playerState.HasPromptData ? _playerState.Hp     : 874;
        int maxHp  = _playerState.HasPromptData ? _playerState.MaxHp  : 985;
        int ma     = _playerState.HasPromptData ? _playerState.Ma     : 441;
        int maxMa  = _playerState.HasPromptData ? _playerState.MaxMa  : 600;
        string r   = _playerState.Position switch
        {
            PlayerPosition.Resting    => "(Resting)",
            PlayerPosition.Meditating => "(Meditating)",
            _ => string.Empty,
        };

        // Order: uppercase tokens are replaced first to avoid the
        // lowercase replacement consuming the H/M character. Replace is
        // case-sensitive so this is belt-and-braces, but cheap.
        s = s.Replace("%H", maxHp.ToString())
             .Replace("%h", hp.ToString())
             .Replace("%M", maxMa.ToString())
             .Replace("%m", ma.ToString())
             .Replace("%r", r)
             .Replace("%c", "0")
             .Replace("%X", "0")
             .Replace("%x", "0")
             .Replace("%w", string.Empty)
             .Replace("%n", "\n");

        return s;
    }

    // ----- Customize catalogue ------------------------------------------

    // One entry in the Customize dropdown — label + wildcard code.
    public sealed record WildcardItem(string Label, string Code)
    {
        // Pad to the widest label (18 chars: "Current Mana / Kai") + 4 for
        // breathing room so the code column lines up under FontMono.
        public string Display => $"{Label,-22}{Code}";
    }

    private static List<WildcardItem> BuildCatalogue() => new()
    {
        new("Current HP",         "%h"),
        new("Maximum HP",         "%H"),
        new("Current Mana / Kai", "%m"),
        new("Maximum Mana / Kai", "%M"),
        new("Resting flag",       "%r"),
        new("Current wealth",     "%c"),
        new("Current experience", "%x"),
        new("Exp to level",       "%X"),
        new("Warning state",      "%w"),
        new("Newline",            "%n"),
        new("Foreground 0",       "%f0"),
        new("Foreground 1",       "%f1"),
        new("Foreground 2",       "%f2"),
        new("Foreground 3",       "%f3"),
        new("Foreground 4",       "%f4"),
        new("Foreground 5",       "%f5"),
        new("Foreground 6",       "%f6"),
        new("Foreground 7",       "%f7"),
        new("Background 0",       "%b0"),
        new("Background 1",       "%b1"),
        new("Background 2",       "%b2"),
        new("Background 3",       "%b3"),
        new("Background 4",       "%b4"),
        new("Background 5",       "%b5"),
        new("Background 6",       "%b6"),
        new("Background 7",       "%b7"),
        new("Default colors",     "%d"),
        new("Bold",               "%B"),
        new("Normal",             "%N"),
        new("Underline",          "%U"),
        new("Blink",              "%L"),
        new("Reverse",            "%R"),
    };
}
