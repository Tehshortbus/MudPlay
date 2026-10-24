using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using MudPlay.Controls;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

// Attached behaviour that renders a quest step's QuestStepSegmentViewModel list
// into a TextBlock's inline runs: plain segments become text Runs (so the line
// still wraps at word boundaries) and link segments become underlined LinkRuns
// wired to the segment's command (a walk to a map/room, or a command typed at
// the game). Bind SegmentsProperty in XAML; the handler rebuilds the inlines whenever the bound list changes.
public static class QuestStepInlines
{
    // Narrow no-break space either side of a link: a few pixels of breathing room
    // around the hot-text without adding a wrap point.
    private const string LinkGap = "\u202F";

    public static readonly AttachedProperty<IReadOnlyList<QuestStepSegmentViewModel>?> SegmentsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<QuestStepSegmentViewModel>?>(
            "Segments", typeof(QuestStepInlines));

    public static void SetSegments(TextBlock target, IReadOnlyList<QuestStepSegmentViewModel>? value) =>
        target.SetValue(SegmentsProperty, value);

    public static IReadOnlyList<QuestStepSegmentViewModel>? GetSegments(TextBlock target) =>
        target.GetValue(SegmentsProperty);

    static QuestStepInlines() =>
        SegmentsProperty.Changed.AddClassHandler<TextBlock>(OnSegmentsChanged);

    private static void OnSegmentsChanged(TextBlock target, AvaloniaPropertyChangedEventArgs e)
    {
        var inlines = new InlineCollection();
        if (e.NewValue is IReadOnlyList<QuestStepSegmentViewModel> segments)
            foreach (QuestStepSegmentViewModel seg in segments)
            {
                if (seg.Command is not { } command) { inlines.Add(new Run(seg.Text)); continue; }

                // Walk links read cyan (MapLinkText); send-command links read green
                // (CmdLinkText) so a "walks me there" token is visually distinct from
                // a "types this at the game" one.
                var link = new LinkRun(seg.Text, () =>
                {
                    if (command.CanExecute(null)) command.Execute(null);
                });
                link.Classes.Add(seg.Kind == QuestStepLinkKind.Send ? "CmdLinkText" : "MapLinkText");
                inlines.Add(new Run(LinkGap));
                inlines.Add(link);
                inlines.Add(new Run(LinkGap));
            }
        target.Inlines = inlines;
    }
}
