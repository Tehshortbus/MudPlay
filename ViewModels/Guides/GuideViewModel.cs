using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Settings;

namespace MudPlay.ViewModels.Guides;

// A checklist line as the card renders it: its text, whether it's ticked, whether
// it's the current (next-to-do) line to highlight, and whether it's an
// explanation line that never ticks.
public sealed record GuideLineView(string Text, bool IsDone, bool IsCurrent, bool IsInfo)
{
    public string Marker => IsInfo ? "•" : IsDone ? "☑" : "☐";
}

// Runs one Visual Guide at a time in the floating step card: the first-run setup
// tour and every guide in GuideCatalogue. Each step is a checklist; a line ticks
// on its action notification (GuideActions, raised where the user really does the
// thing) or its probe, and a step whose tracked lines are all ticked — or whose
// Outcome is reached — is done, which moves the guide on by itself. Steps of
// explanation lines only move on with Next. Pure presentation state: the guides,
// the progress store and its persistence are injected, so this stays free of
// AppServices.
public sealed partial class GuideViewModel : ObservableObject
{
    private readonly IReadOnlyList<GuideDefinition> _guides;
    private readonly Func<GuideProgress> _progressStore;
    private readonly Action _saveProgress;
    private readonly Action _persistSetupDismiss;
    private readonly Action<string> _log;

    private GuideDefinition? _guide;
    private readonly List<GuideStep> _steps = new();

    // Actions the user has taken during this run (cumulative) — the completion
    // signal for action lines.
    private readonly HashSet<string> _doneActions = new(StringComparer.Ordinal);

    // Demo walkthrough of the setup tour (the Program Log test button): every step
    // as if nothing is set up, no outcome counts, and closing persists nothing — so
    // the tour can be reviewed on a configured install. Action lines still tick on
    // real clicks, so the checklist stays interactive.
    private bool _demoMode;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private int _currentIndex;

    // A guide was finished, restarted or moved on — the picker re-reads its rows.
    public event Action? ProgressChanged;

    // persistSetupDismiss: the setup tour was finished or skipped, so it stops
    // auto-showing on launch. log: an Info line for a guide started / finished.
    internal GuideViewModel(
        IReadOnlyList<GuideDefinition> guides, Func<GuideProgress> progress, Action saveProgress,
        Action persistSetupDismiss, Action<string>? log = null)
    {
        _guides = guides ?? throw new ArgumentNullException(nameof(guides));
        _progressStore = progress ?? throw new ArgumentNullException(nameof(progress));
        _saveProgress = saveProgress ?? throw new ArgumentNullException(nameof(saveProgress));
        _persistSetupDismiss = persistSetupDismiss ?? throw new ArgumentNullException(nameof(persistSetupDismiss));
        _log = log ?? (_ => { });
    }

    internal IReadOnlyList<GuideDefinition> Guides => _guides;
    internal GuideProgress Progress => _progressStore();

    // The setup tour at its first outstanding step, built from what's missing now.
    public void StartSetup() => Begin(GuideCatalogue.SetupId, demo: false, restart: false);

    // Force-show the whole setup tour as if nothing is configured — the Program Log
    // test button. It still opens at the first section the user actually lacks
    // (step 1 when they lack nothing), like the real auto-show.
    public void StartDemo() => Begin(GuideCatalogue.SetupId, demo: true, restart: false);

    // Open a guide where the user left it (step 1 when new, or just finished).
    public void Start(string guideId) => Begin(guideId, demo: false, restart: false);

    // Forget a guide's progress and walk it again from step 1.
    public void Restart(string guideId) => Begin(guideId, demo: false, restart: true);

    private void Begin(string guideId, bool demo, bool restart)
    {
        GuideDefinition? guide = _guides.FirstOrDefault(g => g.Id == guideId);
        if (guide is null) return;

        if (restart && (Progress.StepReached.Remove(guideId) | Progress.Finished.Remove(guideId)))
            _saveProgress();

        _guide = guide;
        _demoMode = demo;
        _doneActions.Clear();
        _steps.Clear();
        foreach (GuideStep step in guide.Steps)
            if (demo || step.Include is null || step.Include())
                _steps.Add(step);

        int start = FirstUndoneIndex();
        // A finished guide's place is dropped at its Finish, so opening it again
        // starts at step 1 and from then on keeps its place like any other.
        if (!demo && guide.Resumable && Progress.StepReached.TryGetValue(guideId, out int reached))
            start = Math.Clamp(reached, 0, _steps.Count - 1);

        CurrentIndex = start;
        IsActive = true;
        RaiseStepProperties();
        SaveStepReached();
        _log($"{(demo ? "demo of " : "")}\"{guide.Title}\" started at step {start + 1} of {_steps.Count}");
    }

    // The first step whose Outcome isn't reached — where a run opens. The final
    // step is excluded so a set-up-but-disconnected install still opens the setup
    // tour at step 1 rather than jumping to Connect.
    private int FirstUndoneIndex()
    {
        for (int i = 0; i < _steps.Count - 1; i++)
            if (_steps[i].Outcome is not { } outcome || !outcome()) return i;
        return 0;
    }

    private bool LineDone(GuideLine line) =>
        line.Probe is { } probe ? probe() : line.ActionKey is { } key && _doneActions.Contains(key);

    // Done when it has tracked lines and every one is ticked, or — outside a demo —
    // its Outcome is reached (a fallback for something done off-guide).
    private bool StepComplete(GuideStep step)
    {
        bool anyTracked = false;
        foreach (GuideLine line in step.Lines)
        {
            if (!line.IsTracked) continue;
            if (!LineDone(line)) return !_demoMode && step.Outcome?.Invoke() == true;
            anyTracked = true;
        }
        return anyTracked || (!_demoMode && step.Outcome?.Invoke() == true);
    }

    // Re-check the probes (call when the user returns to the main window), so a
    // step finished in another window shows its tick and the guide moves on.
    public void Refresh()
    {
        if (!IsActive || _demoMode) return;
        AdvancePastCompleted();
        RaiseStepProperties();
    }

    // The user did a tracked action (GuideActions). Ticks its line and moves the
    // guide past any step that is now complete. Works in demo too.
    public void NotifyActionDone(string actionKey)
    {
        if (!IsActive || string.IsNullOrEmpty(actionKey)) return;
        if (!_doneActions.Add(actionKey)) return;
        AdvancePastCompleted();
        RaiseStepProperties();
    }

    private void AdvancePastCompleted()
    {
        while (CurrentIndex < _steps.Count - 1 && CurrentStep is { } s && StepComplete(s))
            CurrentIndex++;
    }

    private GuideStep? CurrentStep =>
        CurrentIndex >= 0 && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : null;

    private bool IsSetup => _guide?.Id == GuideCatalogue.SetupId;

    public string GuideTitle => _guide?.Title ?? "";
    public string CurrentTitle => CurrentStep?.Title ?? "";
    public string? CurrentTargetName => CurrentStep?.TargetName;
    public bool CurrentIsDone => CurrentStep is { } s && StepComplete(s);
    public string StepCounterText => _steps.Count == 0 ? "" : $"Step {CurrentIndex + 1} of {_steps.Count}";
    public bool CanPrev => CurrentIndex > 0;
    public bool IsLastStep => CurrentIndex >= _steps.Count - 1;
    public string NextButtonText => IsLastStep ? "Finish" : "Next";
    public string CloseButtonText => IsSetup ? "Skip — don't show this again" : "Close — your place is kept";

    // The action the current (next-to-do) line wants the user to take, or null when
    // that line is ticked or isn't an action — drives which control glows.
    public string? CurrentActionKey
    {
        get
        {
            if (CurrentStep is not { } step) return null;
            int i = FirstNotDoneLine(step);
            if (i < 0) return null;
            GuideLine line = step.Lines[i];
            return LineDone(line) ? null : line.ActionKey;
        }
    }

    public bool HighlightProfileManagement => IsActive && CurrentActionKey == GuideActions.ProfileManagement;
    public bool HighlightImportMdb => IsActive && CurrentActionKey == GuideActions.ImportMdb;

    // The current step's checklist, with the first not-done tracked line flagged.
    public IReadOnlyList<GuideLineView> CurrentLines
    {
        get
        {
            if (CurrentStep is not { } step) return Array.Empty<GuideLineView>();
            int current = FirstNotDoneLine(step);
            var views = new List<GuideLineView>(step.Lines.Count);
            for (int i = 0; i < step.Lines.Count; i++)
            {
                GuideLine line = step.Lines[i];
                views.Add(new GuideLineView(line.Text, line.IsTracked && LineDone(line), i == current, !line.IsTracked));
            }
            return views;
        }
    }

    // The first tracked line not yet done; the last tracked line when all are; -1
    // for a step of explanation lines only.
    private int FirstNotDoneLine(GuideStep step)
    {
        int lastTracked = -1;
        for (int i = 0; i < step.Lines.Count; i++)
        {
            if (!step.Lines[i].IsTracked) continue;
            if (!LineDone(step.Lines[i])) return i;
            lastTracked = i;
        }
        return lastTracked;
    }

    [RelayCommand]
    private void Prev()
    {
        if (CanPrev) CurrentIndex--;
    }

    [RelayCommand]
    private void Next()
    {
        if (IsLastStep) { Finish(); return; }
        CurrentIndex++;
    }

    // Close the card. The setup tour's "Skip" also stops it auto-showing; any other
    // guide keeps the step it was on for next time.
    [RelayCommand]
    private void Close()
    {
        IsActive = false;
        if (_demoMode || _guide is null) return;
        if (IsSetup) _persistSetupDismiss();
        _log($"\"{_guide.Title}\" closed at step {CurrentIndex + 1} of {_steps.Count}");
    }

    private void Finish()
    {
        IsActive = false;
        // A demo run (test button) never persists — it's a review, not the real thing.
        if (_demoMode || _guide is null) return;
        if (IsSetup) _persistSetupDismiss();
        Progress.StepReached.Remove(_guide.Id);
        Progress.Finished[_guide.Id] = true;
        _saveProgress();
        ProgressChanged?.Invoke();
        _log($"\"{_guide.Title}\" finished");
    }

    private void SaveStepReached()
    {
        if (!IsActive || _demoMode || _guide is not { Resumable: true } guide) return;
        if (Progress.StepReached.TryGetValue(guide.Id, out int saved) && saved == CurrentIndex) return;
        Progress.StepReached[guide.Id] = CurrentIndex;
        _saveProgress();
        ProgressChanged?.Invoke();
    }

    partial void OnCurrentIndexChanged(int value)
    {
        RaiseStepProperties();
        SaveStepReached();
    }

    partial void OnIsActiveChanged(bool value) => RaiseStepProperties();

    private void RaiseStepProperties()
    {
        OnPropertyChanged(nameof(GuideTitle));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentTargetName));
        OnPropertyChanged(nameof(CurrentLines));
        OnPropertyChanged(nameof(CurrentIsDone));
        OnPropertyChanged(nameof(CurrentActionKey));
        OnPropertyChanged(nameof(HighlightProfileManagement));
        OnPropertyChanged(nameof(HighlightImportMdb));
        OnPropertyChanged(nameof(StepCounterText));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(CloseButtonText));
    }
}
