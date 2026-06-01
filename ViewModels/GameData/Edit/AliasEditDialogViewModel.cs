using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

/// <summary>
/// Per-record edit dialog for Game Data Browser → Aliases. Editable
/// fields: Name (required, no whitespace, unique, must not collide
/// with a chat-channel command), Enabled, Expansion (multi-line,
/// <c>^M</c> / <c>;</c> split, <c>{0}</c>/<c>{N}</c> positional
/// substitution).
/// </summary>
/// <remarks>
/// Validation runs live; <see cref="StatusMessage"/> + <see cref="HasError"/>
/// flag the dialog when the name is blank / has whitespace / matches
/// another alias / collides with a chat-channel command (see
/// <see cref="AliasEngine.NameConflictReason"/>). Save returns the
/// updated <see cref="Alias"/>; Cancel returns <c>null</c>.
/// </remarks>
public sealed partial class AliasEditDialogViewModel : ObservableObject, IDialogViewModel<Alias>
{
    /// <summary>Matches a <c>{N}</c> placeholder where N is a non-negative integer. Used by the live preview panel.</summary>
    private static readonly Regex _placeholderPattern = new(@"\{(?<n>\d+)\}", RegexOptions.Compiled);

    public event Action<Alias?>? CloseRequested;

    private readonly Alias _original;
    private readonly AliasEngine _engine;
    /// <summary>True for "Add new alias" flows; false when editing. Drives the title + the duplicate-name self-skip.</summary>
    private readonly bool _isNew;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = string.Empty;

    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaceholderHints))]
    private string _expansion = string.Empty;

    public string Title => _isNew ? "Alias — (new)" : $"Alias — {_original.Name}";

    /// <summary>
    /// Returns the first validation problem, or <c>null</c> when the
    /// record is savable.
    /// </summary>
    private string? GetValidationError()
    {
        if (string.IsNullOrWhiteSpace(Name))      return "Name is required.";
        if (Name.Any(char.IsWhiteSpace))          return "Name can't contain whitespace.";
        if (AliasEngine.NameConflictReason(Name) is { } reason) return reason;
        Alias? excluding = _isNew ? null : _original;
        if (_engine.IsDuplicate(Name, excluding)) return $"Another alias is already named '{Name}'.";
        return null;
    }

    public bool HasError => GetValidationError() is not null;
    public bool CanSave  => !HasError;

    /// <summary>
    /// Status line text — error reason (red) when invalid, friendly
    /// preview hint (muted) when valid.
    /// </summary>
    public string StatusMessage
    {
        get
        {
            string? err = GetValidationError();
            if (err is not null) return err;
            return "Typing this name + Enter in the Conversation window expands the alias and sends each step.";
        }
    }

    /// <summary>
    /// Read-only list of <c>{N}</c> placeholders referenced by the
    /// current expansion, plus <c>{0}</c> if used. Helps the user see
    /// what the typed args have to fill in. Empty when the expansion
    /// uses no placeholders (a static alias like <c>gh → get all here</c>).
    /// </summary>
    public string PlaceholderHints
    {
        get
        {
            if (string.IsNullOrEmpty(Expansion)) return string.Empty;
            int[] indices = _placeholderPattern.Matches(Expansion)
                .Select(m => int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture))
                .Distinct()
                .OrderBy(i => i)
                .ToArray();
            return indices.Length == 0
                ? string.Empty
                : string.Join("  ", indices.Select(i => $"{{{i}}}"));
        }
    }

    public AliasEditDialogViewModel(Alias original, AliasEngine engine, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(engine);
        _original = original;
        _engine   = engine;
        _isNew    = isNew;

        Name      = original.Name;
        Enabled   = original.Enabled;
        Expansion = original.Expansion;
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;
        Alias updated = new(
            Name:      Name.Trim(),
            Enabled:   Enabled,
            Expansion: Expansion ?? string.Empty);
        CloseRequested?.Invoke(updated);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
