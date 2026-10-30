using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData;

// The Quest Flag Steps window: everything the active set's scripts do with one ability flag,
// laid out as steps (QuestFlagWalkthroughBuilder does the reading; this only formats). One
// window serves every flag — the Quest Flags table and the window's own "other flags" links
// call Show to swap what it displays, and Back returns to the flag shown before.
public sealed partial class QuestFlagStepsViewModel : ObservableObject, IDialogViewModel<bool>, IDisposable
{
    public event Action<bool>? CloseRequested;

    private readonly QuestFlagIndex _index;
    private readonly GameDataCache _cache;
    private readonly Func<IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>?> _monsterRooms;
    private readonly Func<CrawledQuest, string?> _questName;
    private readonly LogService? _log;
    private readonly Action<string?> _activeSetHandler;
    private readonly Stack<int> _history = new();

    // monsterRooms is where each monster stands; questName a crawled quest's name
    // (QuestStore.DisplayName: the user's, the realm's or the seed's; null or empty
    // when none names it).
    public QuestFlagStepsViewModel(
        QuestFlagIndex index, GameDataCache cache, int flag,
        Func<IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>?> monsterRooms,
        Func<CrawledQuest, string?> questName,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(monsterRooms);
        ArgumentNullException.ThrowIfNull(questName);
        _index = index;
        _cache = cache;
        _monsterRooms = monsterRooms;
        _questName = questName;
        _log = log;
        // The steps are read from the active set, so a set swap re-reads the flag on show.
        _activeSetHandler = _ => Load(Flag);
        _cache.ActiveSetChanged += _activeSetHandler;
        Load(flag);
    }

    public int Flag { get; private set; }

    [ObservableProperty] private string _heading = string.Empty;
    [ObservableProperty] private string _questNames = string.Empty;
    [ObservableProperty] private string _completeText = string.Empty;
    [ObservableProperty] private string _restrictionText = string.Empty;
    [ObservableProperty] private string _countText = string.Empty;
    [ObservableProperty] private string _withoutFlagHeader = string.Empty;
    [ObservableProperty] private bool _hasQuestNames;
    [ObservableProperty] private bool _hasRestriction;
    [ObservableProperty] private bool _hasSteps;
    [ObservableProperty] private bool _hasWithoutFlagSteps;
    [ObservableProperty] private bool _hasOtherFlags;
    [ObservableProperty] private bool _canGoBack;

    public ObservableCollection<QuestFlagStepRow> Steps { get; } = new();
    public ObservableCollection<QuestFlagStepRow> WithoutFlagSteps { get; } = new();
    public ObservableCollection<QuestFlagLinkRow> OtherFlags { get; } = new();

    // Swap the window to another flag, remembering the one it leaves for Back.
    public void Show(int flag)
    {
        if (flag == Flag) return;
        _history.Push(Flag);
        Load(flag);
    }

    [RelayCommand]
    private void Back()
    {
        if (_history.Count > 0) Load(_history.Pop());
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);

    public void Dispose() => _cache.ActiveSetChanged -= _activeSetHandler;

    private static string StepCount(int count)
        => string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? "step" : "steps")}");

    private void Load(int flag)
    {
        Flag = flag;
        QuestFlagWalkthrough walk = QuestFlagWalkthroughBuilder.Build(_index, _cache, flag, _monsterRooms());

        Heading = string.Create(CultureInfo.InvariantCulture, $"{walk.FlagName} ({walk.Flag})");
        List<string> named = walk.Quests
            .Select(q => _questName(q)?.Trim() ?? string.Empty)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        QuestNames = (named.Count == 1 ? "Quest: " : "Quests: ") + string.Join(", ", named);
        HasQuestNames = named.Count > 0;
        CompleteText = walk.CompleteText;
        RestrictionText = walk.RestrictionText;
        HasRestriction = walk.RestrictionText.Length > 0;

        Steps.Clear();
        int number = 0;
        foreach (QuestFlagStepEntry entry in walk.Steps) Steps.Add(new QuestFlagStepRow(++number, entry));
        WithoutFlagSteps.Clear();
        foreach (QuestFlagStepEntry entry in walk.WithoutFlagSteps) WithoutFlagSteps.Add(new QuestFlagStepRow(++number, entry));
        HasSteps = Steps.Count > 0;
        HasWithoutFlagSteps = WithoutFlagSteps.Count > 0;
        WithoutFlagHeader = string.Create(CultureInfo.InvariantCulture,
            $"Only without this flag, and leaving it alone ({WithoutFlagSteps.Count})");
        CountText = number == 0
            ? "No script in this game-data set touches this flag."
            : string.Create(CultureInfo.InvariantCulture,
                $"{StepCount(Steps.Count)} {(Steps.Count == 1 ? "reads or changes" : "read or change")} the flag")
              + (WithoutFlagSteps.Count == 0
                  ? "."
                  : string.Create(CultureInfo.InvariantCulture,
                      $"; {WithoutFlagSteps.Count} more only run without it and leave it alone (listed at the bottom)."));

        OtherFlags.Clear();
        foreach (int other in walk.OtherFlags)
            OtherFlags.Add(new QuestFlagLinkRow(
                other,
                string.Create(CultureInfo.InvariantCulture, $"{QuestScriptNames.Ability(other)} ({other})"),
                Show));
        HasOtherFlags = OtherFlags.Count > 0;
        CanGoBack = _history.Count > 0;

        _log?.Info("QuestFlags", string.Create(CultureInfo.InvariantCulture,
            $"Quest Flag Steps: showing {walk.FlagName} ({flag}) — {Steps.Count} steps, {WithoutFlagSteps.Count} without-flag lines"));
    }
}
