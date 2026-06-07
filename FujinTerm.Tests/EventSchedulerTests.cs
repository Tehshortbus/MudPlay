using System.Collections.Generic;
using System.Text;
using FujinTerm.Game.Events;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Unit coverage for <see cref="EventScheduler"/>'s connect / disconnect
/// / prompt-observed lifecycle. Focused on the state-latch paths PR
/// 8.2 ships — Logon-fires-on-first-prompt, Re-log latching across
/// in-session disconnects, idempotence within a single in-game window,
/// and Logoff dispatch via <see cref="EventManager.FireLogoffEvents"/>.
/// AtTime + Every timer paths smoke-tested manually (DispatcherTimer
/// needs an Avalonia dispatcher running, out of scope for these
/// in-process tests).
/// </summary>
public sealed class EventSchedulerTests
{
    /// <summary>Latin-1 bytes of a minimal MajorMUD status line — drives PromptObserved.</summary>
    private static readonly byte[] PromptBytes = Encoding.Latin1.GetBytes("[HP=100]: ");

    private static (EventManager events, EventScheduler scheduler, WirePromptScanner prompt,
                    List<byte[]> sent) Build()
    {
        EventManager events = new();
        WirePromptScanner prompt = new();
        EventScheduler scheduler = new(events, prompt);
        List<byte[]> sent = new();
        events.SetWireSender(sent.Add);
        return (events, scheduler, prompt, sent);
    }

    private static (EventManager events, EventScheduler scheduler, WirePromptScanner prompt,
                    CleanupWarningWatcher cleanup, List<byte[]> sent) BuildWithCleanup()
    {
        EventManager events = new();
        WirePromptScanner prompt = new();
        CleanupWarningWatcher cleanup = new();
        EventScheduler scheduler = new(events, prompt, cleanup);
        List<byte[]> sent = new();
        events.SetWireSender(sent.Add);
        return (events, scheduler, prompt, cleanup, sent);
    }

    private static ScheduledEvent CommandEvent(EventTriggerType trigger, string cmd, string? name = null) =>
        new()
        {
            Name = name ?? $"{trigger}-{cmd}",
            TriggerType = trigger,
            ActionType = EventActionType.Command,
            CommandText = cmd,
        };

    // ----- Logon ------------------------------------------------------

    [Fact]
    public void FirstPromptAfterConnect_FiresLogonEvents()
    {
        var (events, scheduler, prompt, sent) = Build();
        events.Add(CommandEvent(EventTriggerType.Logon, "stat"));

        scheduler.NotifyConnected();
        prompt.Append(PromptBytes);

        Assert.Single(sent);
        Assert.Equal("stat\r", Encoding.Latin1.GetString(sent[0]));
    }

    [Fact]
    public void SecondPromptInSameSession_DoesNotRefireLogon()
    {
        var (events, scheduler, prompt, sent) = Build();
        events.Add(CommandEvent(EventTriggerType.Logon, "stat"));

        scheduler.NotifyConnected();
        prompt.Append(PromptBytes);
        prompt.Append(PromptBytes);
        prompt.Append(PromptBytes);

        Assert.Single(sent);
    }

    [Fact]
    public void DisabledLogonEvent_DoesNotFire()
    {
        var (events, scheduler, prompt, sent) = Build();
        ScheduledEvent disabled = CommandEvent(EventTriggerType.Logon, "stat");
        disabled.Disabled = true;
        events.Add(disabled);

        scheduler.NotifyConnected();
        prompt.Append(PromptBytes);

        Assert.Empty(sent);
    }

    // ----- Re-log -----------------------------------------------------

    [Fact]
    public void FirstConnect_DoesNotFireRelog()
    {
        var (events, scheduler, prompt, sent) = Build();
        events.Add(CommandEvent(EventTriggerType.Relog, "look"));

        scheduler.NotifyConnected();
        prompt.Append(PromptBytes);

        Assert.Empty(sent);
    }

    [Fact]
    public void Reconnect_FiresRelogAlongsideLogon()
    {
        var (events, scheduler, prompt, sent) = Build();
        events.Add(CommandEvent(EventTriggerType.Logon, "stat"));
        events.Add(CommandEvent(EventTriggerType.Relog, "look"));

        // First connect — in-game, then disconnect.
        scheduler.NotifyConnected();
        prompt.Append(PromptBytes);
        Assert.Single(sent);  // Logon only.
        scheduler.NotifyDisconnected();

        // Second connect — both Logon AND Re-log fire.
        scheduler.NotifyConnected();
        prompt.Append(PromptBytes);

        Assert.Equal(3, sent.Count);
        Assert.Equal("stat\r", Encoding.Latin1.GetString(sent[0]));
        Assert.Equal("stat\r", Encoding.Latin1.GetString(sent[1]));
        Assert.Equal("look\r", Encoding.Latin1.GetString(sent[2]));
    }

    [Fact]
    public void DisconnectBeforeInGame_DoesNotLatchRelog()
    {
        var (events, scheduler, prompt, sent) = Build();
        events.Add(CommandEvent(EventTriggerType.Relog, "look"));

        // Failed connect — connected but never reached the prompt.
        scheduler.NotifyConnected();
        scheduler.NotifyDisconnected();

        // Retry connect — should fire Logon only (no Re-log latch).
        scheduler.NotifyConnected();
        prompt.Append(PromptBytes);

        Assert.Empty(sent);  // no Logon configured; no Re-log fires either.
    }

    // ----- Logoff -----------------------------------------------------

    [Fact]
    public void FireLogoffEvents_FiresOnlyLogoffTriggerEvents()
    {
        var (events, _, _, sent) = Build();
        events.Add(CommandEvent(EventTriggerType.Logoff, "save"));
        events.Add(CommandEvent(EventTriggerType.Logon,  "stat"));
        events.Add(CommandEvent(EventTriggerType.AtTime, "tick", "ticker"));

        events.FireLogoffEvents();

        Assert.Single(sent);
        Assert.Equal("save\r", Encoding.Latin1.GetString(sent[0]));
    }

    [Fact]
    public void FireLogoffEvents_SkipsDisabledLogoff()
    {
        var (events, _, _, sent) = Build();
        ScheduledEvent disabled = CommandEvent(EventTriggerType.Logoff, "save");
        disabled.Disabled = true;
        events.Add(disabled);
        events.Add(CommandEvent(EventTriggerType.Logoff, "drop"));

        events.FireLogoffEvents();

        Assert.Single(sent);
        Assert.Equal("drop\r", Encoding.Latin1.GetString(sent[0]));
    }

    // ----- Cleanup-warning → Logoff wiring --------------------------

    [Fact]
    public void CleanupWarning_WhileInGame_FiresLogoffEvents()
    {
        var (events, scheduler, prompt, cleanup, sent) = BuildWithCleanup();
        events.Add(CommandEvent(EventTriggerType.Logoff, "save"));

        scheduler.NotifyConnected();
        prompt.Append(PromptBytes);
        // Feed the cleanup-warning line — watcher parses it and fires
        // WarningObserved, which the scheduler routes to Logoff.
        cleanup.Append(Encoding.Latin1.GetBytes("System shutting down in 5 minutes"));

        Assert.Single(sent);
        Assert.Equal("save\r", Encoding.Latin1.GetString(sent[0]));
    }

    [Fact]
    public void CleanupWarning_RepeatedInSameSession_FiresLogoffOnlyOnce()
    {
        // BBS warns multiple times during a cleanup cycle
        // ("5 min", "4 min", …) — the latch should keep Logoff actions
        // from firing every warning. A reconnect (NotifyConnected)
        // clears the latch so the next cycle can fire.
        var (events, scheduler, prompt, cleanup, sent) = BuildWithCleanup();
        events.Add(CommandEvent(EventTriggerType.Logoff, "save"));

        scheduler.NotifyConnected();
        prompt.Append(PromptBytes);

        cleanup.Append(Encoding.Latin1.GetBytes("System shutting down in 5 minutes"));
        cleanup.Append(Encoding.Latin1.GetBytes("System shutting down in 4 minutes"));
        cleanup.Append(Encoding.Latin1.GetBytes("System shutting down in 3 minutes"));
        Assert.Single(sent);
        Assert.Equal("save\r", Encoding.Latin1.GetString(sent[0]));

        // Reconnect → latch clears → next cycle fires again.
        scheduler.NotifyDisconnected();
        scheduler.NotifyConnected();
        prompt.Append(PromptBytes);
        cleanup.Append(Encoding.Latin1.GetBytes("System shutting down in 5 minutes"));
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void CleanupWarning_BeforeInGame_DoesNotFireLogoff()
    {
        var (events, _, _, cleanup, sent) = BuildWithCleanup();
        events.Add(CommandEvent(EventTriggerType.Logoff, "save"));
        // Connected at TCP level but we never got the in-game prompt —
        // Logoff events would have nowhere to land.
        cleanup.Append(Encoding.Latin1.GetBytes("System shutting down in 5 minutes"));
        Assert.Empty(sent);
    }

    [Fact]
    public void MultipleLogoffEvents_FireInListOrder()
    {
        var (events, _, _, sent) = Build();
        events.Add(CommandEvent(EventTriggerType.Logoff, "drop", "first"));
        events.Add(CommandEvent(EventTriggerType.Logoff, "save", "second"));

        events.FireLogoffEvents();

        Assert.Equal(2, sent.Count);
        Assert.Equal("drop\r", Encoding.Latin1.GetString(sent[0]));
        Assert.Equal("save\r", Encoding.Latin1.GetString(sent[1]));
    }

    // ----- Dispose ----------------------------------------------------

    [Fact]
    public void Dispose_UnsubscribesPromptObserved()
    {
        var (events, scheduler, prompt, sent) = Build();
        events.Add(CommandEvent(EventTriggerType.Logon, "stat"));
        scheduler.NotifyConnected();

        scheduler.Dispose();
        prompt.Append(PromptBytes);

        Assert.Empty(sent);
    }
}
