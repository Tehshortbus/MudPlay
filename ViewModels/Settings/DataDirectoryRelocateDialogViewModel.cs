using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Settings;

// Confirm-and-execute dialog for the Settings → General "Change data directory"
// flow. Summarises what's about to happen (file count, total size, source →
// destination paths), validates the destination live, and — on user confirm —
// kicks off DataRootRelocator.Relocate followed by an app restart.
public sealed partial class DataDirectoryRelocateDialogViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly DataRootRelocator.MovePlan _plan;

    public string CurrentPath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(CanMove))]
    private string _destinationPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanMove))]
    private bool _busy;

    public bool IsBusy => Busy;
    public bool CanMove => !Busy && Validate() == DataRootRelocator.FailureReason.None;
    public bool HasError => Validate() is not (DataRootRelocator.FailureReason.None);

    public string FileCountText => _plan.FileCount == 1
        ? "1 file"
        : $"{_plan.FileCount:N0} files";

    public string TotalSizeText => HumanBytes(_plan.TotalBytes);

    public string StatusMessage => Validate() switch
    {
        DataRootRelocator.FailureReason.None when string.IsNullOrWhiteSpace(DestinationPath)
                                                       => "Pick a destination folder.",
        DataRootRelocator.FailureReason.None            => "Ready to move.",
        DataRootRelocator.FailureReason.DestinationIsSource
                                                       => "Destination is the same as the current data folder.",
        DataRootRelocator.FailureReason.DestinationInsideSource
                                                       => "Destination is inside the current data folder.",
        DataRootRelocator.FailureReason.DestinationNotEmpty
                                                       => "Destination folder isn't empty — pick an empty or new folder.",
        DataRootRelocator.FailureReason.DestinationNotWritable
                                                       => "Destination folder isn't writable.",
        _                                              => string.Empty,
    };

    public DataDirectoryRelocateDialogViewModel(string currentPath, DataRootRelocator.MovePlan plan)
    {
        CurrentPath = currentPath;
        _plan       = plan;
    }

    // Called from the View after the user picks a destination via the platform
    // folder picker. The View owns the picker because that API lives off the
    // Window class — the VM stays platform-clean.
    public void SetDestination(string path)
    {
        DestinationPath = path;
    }

    [RelayCommand]
    private void Move()
    {
        if (!CanMove) return;

        Busy = true;
        DataRootRelocator.FailureReason outcome =
            DataRootRelocator.Relocate(DestinationPath, AppServices.Current.Log);
        Busy = false;

        if (outcome is DataRootRelocator.FailureReason.None
                    or DataRootRelocator.FailureReason.SourceDeletePartial)
        {
            CloseRequested?.Invoke(true);
            DataRootRelocator.RestartApp();
        }
        else
        {
            // Validation should've caught all UI-time errors; this is the
            // mid-flight failure path. Log already wrote the detail.
            AppServices.Current.Log.Error("DataRootRelocator",
                $"Move aborted: {outcome}. Source folder is still authoritative.");
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private DataRootRelocator.FailureReason Validate()
    {
        if (string.IsNullOrWhiteSpace(DestinationPath))
            return DataRootRelocator.FailureReason.DestinationIsSource; // any non-None blocks Move
        return DataRootRelocator.Validate(DestinationPath);
    }

    private static string HumanBytes(long bytes)
    {
        if (bytes < 1024)             return $"{bytes} B";
        if (bytes < 1024L * 1024)     return $"{bytes / 1024d:N1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):N1} MB";
        return $"{bytes / (1024d * 1024 * 1024):N2} GB";
    }
}
