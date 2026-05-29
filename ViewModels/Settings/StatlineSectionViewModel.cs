using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Statline" tab — Phase 4 PR 4.7 surface, modelled on MegaMUD's
/// Statline dialog: a read-only <b>Current Statline</b> preview, the
/// editable <b>Statline Command</b> (default <c>full</c>), and a
/// <b>Customize</b> dropdown that appends wildcard tokens to the
/// command box. Phase 12 PR 12.1 replaces this with a richer
/// drag-token builder; the persisted DTO stays a single string so the
/// upgrade is transparent.
/// </summary>
public sealed partial class StatlineSectionViewModel : SettingsSectionViewModel
{
    private const string TabKey = "Statline";
    private const string DefaultCommand = "full";

    private static readonly Regex FormattingCodes =
        new(@"%(?:[fb][0-7]|[dBNULR])", RegexOptions.Compiled);

    private readonly ProfileService _profile;
    private readonly PlayerState _playerState;
    private readonly Func<string, Task<bool>>? _sendText;
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

    /// <summary>True when any character profile is loaded.</summary>
    public bool HasProfile => _profile.Current is not null;

    /// <summary>
    /// What goes on the wire after <c>set statline</c>. Default is
    /// <c>full</c>; user can type a raw wildcard string or
    /// <c>full custom &lt;wildcards&gt;</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStatlinePreview))]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private string _command = DefaultCommand;

    /// <summary>
    /// Read-only preview that mirrors how the BBS will render the prompt
    /// once <see cref="Command"/> is in effect. Synthesised from live
    /// <see cref="PlayerState"/> values where available, falling back to
    /// sample numbers so the editor stays useful pre-connect.
    /// </summary>
    public string CurrentStatlinePreview => RenderPreview(Command);

    /// <summary>Picker selection driving the <c>Add</c> button.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddWildcardCommand))]
    private WildcardItem? _selectedWildcard;

    /// <summary>
    /// Short status line shown next to the Send button — last action's
    /// result. Reset when the user edits the command.
    /// </summary>
    [ObservableProperty] private string _sendStatus = string.Empty;

    /// <summary>Catalogue of wildcards offered by the Customize dropdown.</summary>
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

        LoadFromProfile();
        _suppressDirty = false;
    }

    /// <summary>True when the Send button should be enabled — needs both a sender and non-empty text.</summary>
    public bool CanSend => _sendText is not null && !string.IsNullOrWhiteSpace(Command);

    public override void Apply()
    {
        if (_profile.Current is not { } profile) return;

        // Treat "full" / blank as null on disk so the JSON stays minimal
        // and round-trips back to the default in the editor.
        string? toStore = IsDefault(Command) ? null : Command;
        StatlineSettings dto = new() { Command = toStore };

        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(dto);
        _profile.Save();
        _profile.NotifyMutated();
        ClearDirty();
    }

    public override void Discard()
    {
        _suppressDirty = true;
        LoadFromProfile();
        _suppressDirty = false;
        ClearDirty();
    }

    /// <summary>
    /// Push the current command to the connected BBS as
    /// <c>set statline &lt;command&gt;\r</c>. No-op (with a status
    /// banner) when nothing is connected or the command is blank.
    /// </summary>
    [RelayCommand]
    private async Task SendToBbsAsync()
    {
        if (_sendText is null)
        {
            SendStatus = "Connect to a BBS first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Command))
        {
            SendStatus = "Statline command is empty.";
            return;
        }

        bool ok = await _sendText($"set statline {Command}\r").ConfigureAwait(true);
        SendStatus = ok ? "Sent." : "Send failed — check connection.";
    }

    /// <summary>Append the selected wildcard token to the command box.</summary>
    [RelayCommand(CanExecute = nameof(CanAddWildcard))]
    private void AddWildcard()
    {
        if (SelectedWildcard is null) return;

        // If user hasn't customised yet (command is "full" or blank),
        // promote them to a `full custom ` prefix before appending so the
        // first token forms a valid custom statline.
        if (IsDefault(Command))
        {
            Command = "full custom " + SelectedWildcard.Code;
        }
        else
        {
            Command += SelectedWildcard.Code;
        }
    }

    private bool CanAddWildcard() => SelectedWildcard is not null;

    /// <summary>Reset the command box to the class default (<c>full</c>).</summary>
    [RelayCommand]
    private void ResetToDefault() => Command = DefaultCommand;

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
        Command = string.IsNullOrWhiteSpace(dto.Command) ? DefaultCommand : dto.Command!;
        SendStatus = string.Empty;
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

    partial void OnCommandChanged(string value)
    {
        if (!_suppressDirty) SendStatus = string.Empty;
        Dirty();
    }

    // ----- Wildcard rendering -------------------------------------------

    private static bool IsDefault(string? cmd)
        => string.IsNullOrWhiteSpace(cmd) || cmd!.Trim().Equals(DefaultCommand, StringComparison.OrdinalIgnoreCase);

    private string RenderPreview(string? command)
    {
        string template;

        if (IsDefault(command))
        {
            template = DefaultTemplateForClass();
        }
        else
        {
            string trimmed = command!.TrimStart();
            const string prefix = "full custom ";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                template = trimmed.Substring(prefix.Length);
            }
            else
            {
                template = command;
            }
        }

        return ApplyWildcards(template);
    }

    private string DefaultTemplateForClass() => _playerState.ManaType switch
    {
        ManaType.Kai  => "[HP=%h,KAI=%m]: %r",
        ManaType.None => "[HP=%h]: %r",
        _             => "[HP=%h,MA=%m]: %r",
    };

    private string ApplyWildcards(string template)
    {
        // Drop colour / formatting codes from the plain-text preview
        // — Phase 12's preview will honour them visually.
        string s = FormattingCodes.Replace(template, string.Empty);

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

    /// <summary>One entry in the Customize dropdown — label + wildcard code.</summary>
    public sealed record WildcardItem(string Label, string Code)
    {
        public string Display => $"{Label}   {Code}";
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
