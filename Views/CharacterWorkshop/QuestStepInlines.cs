using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

/// <summary>
/// Attached behaviour that renders a quest step's
/// <see cref="QuestStepSegmentViewModel"/> list into a <see cref="TextBlock"/>'s
/// inline runs: plain segments become text <see cref="Run"/>s (so the line still
/// wraps at word boundaries) and link segments become an inline, underlined
/// hot-text button wired to the segment's walk-to command. Bind
/// <see cref="SegmentsProperty"/> in XAML; the handler rebuilds the inlines
/// whenever the bound list changes.
/// </summary>
public static class QuestStepInlines
{
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
                inlines.Add(seg.IsLink
                    ? new InlineUIContainer(new Button
                    {
                        Classes = { "MapLink" },
                        Command = seg.WalkCommand,
                        Content = new TextBlock { Text = seg.Text, Classes = { "MapLinkText" } },
                    })
                    : new Run(seg.Text));
        target.Inlines = inlines;
    }
}
