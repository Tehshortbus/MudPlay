using FujinTerm.Game;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the pure <see cref="BugReportBuilder"/> surface — file naming and
/// Markdown composition. The live <c>Capture</c> path reads the whole
/// AppServices graph and is smoke-tested via <c>dotnet run</c>; these tests
/// exercise the deterministic render half by constructing a capture directly.
/// </summary>
public sealed class BugReportBuilderTests
{
    private static BugReportBuilder.BugReportCapture MakeCapture(RealmType realm) => new(
        new DateTimeOffset(2026, 7, 3, 14, 25, 30, TimeSpan.Zero),
        realm,
        [
            new BugReportBuilder.Section("Session", "- **Realm**: stock"),
            new BugReportBuilder.Section("Scrollback", "```\nhello world\n```"),
        ]);

    [Fact]
    public void FileName_UsesRealmLabelAndClickTimestamp()
    {
        Assert.Equal("paradigm-20260703-142530.md",
            BugReportBuilder.FileName(MakeCapture(RealmType.ParaMud)));
        Assert.Equal("stock-20260703-142530.md",
            BugReportBuilder.FileName(MakeCapture(RealmType.Stock)));
    }

    [Fact]
    public void Render_PutsIssueFirstThenEverySection()
    {
        string md = BugReportBuilder.Render(MakeCapture(RealmType.Stock), "  it froze on move  ");

        // Description is trimmed and appears under the Issue heading.
        Assert.Contains("## Issue\n\nit froze on move", md);

        // Every captured section heading + body is emitted.
        Assert.Contains("## Session", md);
        Assert.Contains("- **Realm**: stock", md);
        Assert.Contains("## Scrollback", md);
        Assert.Contains("hello world", md);

        // Issue precedes the state dump.
        Assert.True(md.IndexOf("## Issue", StringComparison.Ordinal)
                  < md.IndexOf("## Session", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_EmptyDescription_ShowsPlaceholder()
    {
        string md = BugReportBuilder.Render(MakeCapture(RealmType.Stock), "   ");
        Assert.Contains("_(none provided)_", md);
    }

    [Fact]
    public void RenderStateOnly_EmitsSectionsWithoutTitleOrIssue()
    {
        string md = BugReportBuilder.RenderStateOnly(MakeCapture(RealmType.Stock));

        // No bug-report title and no user-description block — the crash reporter
        // supplies its own document wrapper.
        Assert.DoesNotContain("# FujinTerm bug report", md);
        Assert.DoesNotContain("## Issue", md);

        // Every captured section still lands.
        Assert.Contains("## Session", md);
        Assert.Contains("- **Realm**: stock", md);
        Assert.Contains("## Scrollback", md);
        Assert.Contains("hello world", md);
    }
}
