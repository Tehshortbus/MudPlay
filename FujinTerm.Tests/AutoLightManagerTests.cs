using FujinTerm.Game.Light;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.K — <see cref="AutoLightManager"/> posts a
/// <see cref="NeedKind.LightSource"/> need on a live "can't see"
/// room-light line, gated by the AutoLight master toggle, deduped by
/// the registry across a chain of dark rooms.
/// </summary>
public sealed class AutoLightManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public NeedsRegistry Needs { get; } = new();
        public AutoLightManager Light { get; }
        public bool Enabled { get; set; } = true;

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Light = new AutoLightManager(Router, Needs, Log);
            Light.SetEnabledToggle(() => Enabled);
        }

        public void Feed(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public void Dispose() => Light.Dispose();
    }

    [Fact]
    public void PitchBlackLine_PostsLightSourceNeed()
    {
        using Harness h = new();

        h.Feed("The room is pitch black.");

        Need need = Assert.Single(h.Needs.Outstanding(NeedKind.LightSource));
        Assert.Equal(AutoLightManager.AnyLightDescriptor, need.Descriptor);
        Assert.Equal(nameof(AutoLightManager), need.Requester);
    }

    [Fact]
    public void VeryDarkLine_PostsLightSourceNeed()
    {
        using Harness h = new();

        h.Feed("The room is very dark - you can't see anything.");

        Assert.Single(h.Needs.Outstanding(NeedKind.LightSource));
    }

    [Fact]
    public void DisabledToggle_DoesNotPost()
    {
        using Harness h = new() { Enabled = false };

        h.Feed("The room is pitch black.");

        Assert.Empty(h.Needs.Outstanding(NeedKind.LightSource));
    }

    [Fact]
    public void ConsecutiveDarkRooms_DedupeToSingleNeed()
    {
        using Harness h = new();

        h.Feed("The room is pitch black.");
        h.Feed("The room is very dark - you can't see anything.");
        h.Feed("The room is pitch black.");

        Assert.Single(h.Needs.Outstanding(NeedKind.LightSource));
    }

    [Fact]
    public void LitRoomLines_DoNotPost()
    {
        using Harness h = new();

        // Penalized lines render contents — no auto-light action.
        h.Feed("The room is barely visible.");
        h.Feed("The room is dimly lit.");
        // An ordinary room display line.
        h.Feed("A Quiet Clearing");

        Assert.Empty(h.Needs.Outstanding(NeedKind.LightSource));
    }

    [Fact]
    public void Disposed_StopsPosting()
    {
        using Harness h = new();
        h.Light.Dispose();

        h.Feed("The room is pitch black.");

        Assert.Empty(h.Needs.Outstanding(NeedKind.LightSource));
    }
}
