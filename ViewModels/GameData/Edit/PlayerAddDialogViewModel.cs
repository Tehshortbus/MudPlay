using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// Lightweight "Add Player" dialog for the Game Data Browser → Players tab. Collects only
// the two identifying fields — Given (required) and Family (optional) — and lets the
// caller create a minimal observation record. The user lands back on the Players tab with
// the new row and can double-click it to set the rich customization fields (RemoteControls
// flags, auto-party toggles, Notes, etc.) via the existing PlayerEditDialogViewModel.
//
// Two-click split (Add → Edit) is deliberate. The full Player edit dialog is rich
// (12 RemoteControls checkboxes + behavior toggles + observed-fields read-only pane), and
// most of that would render empty in an "add" context. Keeping the create path narrow
// makes the action obvious: name a player so they're tracked, then refine.
public sealed partial class PlayerAddDialogViewModel : ObservableObject, IDialogViewModel<PlayerAddResult>
{
    public event Action<PlayerAddResult?>? CloseRequested;

    private readonly PlayerDatabase _db;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _givenName = string.Empty;

    [ObservableProperty] private string _familyName = string.Empty;

    public string Title => "Add Player — (new)";

    public bool HasError => GetValidationError() is not null;
    public bool CanSave  => !HasError;

    public string StatusMessage
    {
        get
        {
            string? err = GetValidationError();
            if (err is not null) return err;
            return "Given name is required. Family is optional — leave blank for players without one.";
        }
    }

    public PlayerAddDialogViewModel(PlayerDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    private string? GetValidationError()
    {
        string given = (GivenName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(given)) return "Given name is required.";
        if (given.Contains(' ')) return "Given name can't contain whitespace — use the Family field for the second word.";
        // Same-given duplicate check: lookups go through PlayerDatabase's
        // case-insensitive given-name dict via the merged Players view.
        foreach (Models.GameData.PlayerRecord p in _db.Players)
        {
            if (string.Equals(p.GivenName, given, StringComparison.OrdinalIgnoreCase))
                return $"A player named '{given}' is already tracked. Double-click that row to edit it instead.";
        }
        return null;
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;
        string given  = GivenName.Trim();
        string family = (FamilyName ?? string.Empty).Trim();
        CloseRequested?.Invoke(new PlayerAddResult(given, family));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// Result returned from PlayerAddDialogViewModel on Save. Carries the two name fields; the
// section VM then mints an observation with the current UTC time.
public sealed record PlayerAddResult(string GivenName, string FamilyName);
