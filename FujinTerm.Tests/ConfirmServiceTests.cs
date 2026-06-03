using FujinTerm.Models.Settings;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the short-circuit contract: every confirm method must return
/// <c>true</c> immediately (without spawning a dialog) when its setting
/// flag is off. The dialog path itself is interactive — Avalonia
/// window-spawn — and exercised live, not here.
/// </summary>
public sealed class ConfirmServiceTests
{
    private static ConfirmService NewService() => new(new DialogService());

    [Fact]
    public async Task ConfirmExit_FlagOff_ReturnsTrueWithoutPrompt()
    {
        ConfirmService svc = NewService();
        svc.ApplyFrom(new ConfirmSettings { ConfirmExit = false });
        Assert.True(await svc.ConfirmExitAsync());
    }

    [Fact]
    public async Task ConfirmHangup_FlagOff_ReturnsTrueWithoutPrompt()
    {
        ConfirmService svc = NewService();
        svc.ApplyFrom(new ConfirmSettings { ConfirmHangup = false });
        Assert.True(await svc.ConfirmHangupAsync());
    }

    [Fact]
    public async Task ConfirmSave_FlagOff_ReturnsTrueWithoutPrompt()
    {
        ConfirmService svc = NewService();
        svc.ApplyFrom(new ConfirmSettings { ConfirmSaveSettings = false });
        Assert.True(await svc.ConfirmSaveAsync());
    }

    [Fact]
    public async Task ConfirmDelete_FlagOff_ReturnsTrueWithoutPrompt()
    {
        ConfirmService svc = NewService();
        svc.ApplyFrom(new ConfirmSettings { ConfirmDeletes = false });
        Assert.True(await svc.ConfirmDeleteAsync("a thing"));
    }

    [Fact]
    public void ApplyFrom_ReplacesLiveSettings()
    {
        ConfirmService svc = NewService();
        Assert.False(svc.Settings.ConfirmExit);

        svc.ApplyFrom(new ConfirmSettings { ConfirmExit = true, ConfirmDeletes = true });

        Assert.True(svc.Settings.ConfirmExit);
        Assert.True(svc.Settings.ConfirmDeletes);
        Assert.False(svc.Settings.ConfirmHangup);
        Assert.False(svc.Settings.ConfirmSaveSettings);
    }

    [Fact]
    public void Defaults_AllFlagsOff()
    {
        ConfirmSettings dto = new();
        Assert.False(dto.ConfirmExit);
        Assert.False(dto.ConfirmHangup);
        Assert.False(dto.ConfirmSaveSettings);
        Assert.False(dto.ConfirmDeletes);
    }
}
