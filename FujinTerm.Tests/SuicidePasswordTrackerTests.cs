using System.IO;
using System.Text;
using FujinTerm.Game;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the passive observer for the in-game `set suicide` /
/// `suicide` password flows. The state machine is the load-bearing
/// piece — both the engine-gate locking and the encrypted-blob
/// commit / wipe behaviours hang off it.
/// </summary>
public sealed class SuicidePasswordTrackerTests
{
    private static (
        SuicidePasswordTracker tracker,
        MessageRouter router,
        EngineSendGate gate,
        ProfileService profile,
        PasswordProtector protector,
        string tmpDir
    ) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        EngineSendGate gate = new();
        ProfileService profile = new();
        profile.LoadBlank();
        string tmpDir = Path.Combine(Path.GetTempPath(), "fterm-suicide-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        PasswordProtector protector = new(Path.Combine(tmpDir, ".credkey"));
        SuicidePasswordTracker tracker = new(router, gate, profile, protector);
        return (tracker, router, gate, profile, protector, tmpDir);
    }

    private static void Dispatch(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(
            Text: text,
            Attributes: Array.Empty<CellAttributes>(),
            Timestamp: DateTimeOffset.UnixEpoch,
            IsPromptLine: false));

    [Fact]
    public void NewPasswordPrompt_LocksGate_AndCapturesOnPasswordChanged()
    {
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            // First-time set: server prompts directly with "Enter New Password:"
            Dispatch(router, "Enter New Password:");
            Assert.True(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.AwaitingNewPassword, tracker.State);

            // User types "hunter2" then Enter — wire-send observes.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("hunter2\r"));

            // Server confirms — Playpen renders this as lowercase
            // "Password changed". Regex tolerates either casing now,
            // but this test pins the realm-observed literal so a
            // future regression to capital-only would fail here.
            Dispatch(router, "Password changed");

            Assert.False(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.Idle, tracker.State);
            Assert.NotNull(profile.Current!.EncryptedSuicidePassword);
            string? roundtrip = protector.Unprotect(profile.Current.EncryptedSuicidePassword!);
            Assert.Equal("hunter2", roundtrip);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ChangePassword_OldPromptThenNewPrompt_CapturesAndCommits()
    {
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            // Change-existing flow: old prompt first.
            Dispatch(router, "Enter the current password:");
            Assert.True(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.AwaitingOldPassword, tracker.State);
            // User types old.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("oldpw\r"));

            // Server accepts and moves to new-password prompt.
            Dispatch(router, "Enter New Password:");
            Assert.Equal(SuicidePasswordTracker.FlowState.AwaitingNewPassword, tracker.State);
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("newpw\r"));

            Dispatch(router, "Password Changed");

            Assert.False(gate.IsLocked);
            Assert.Equal("newpw", protector.Unprotect(profile.Current!.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void InvalidOldPassword_AbortsFlowWithoutTouchingStored()
    {
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            // Pre-seed an existing stored password.
            profile.Current!.EncryptedSuicidePassword = protector.Protect("original");

            Dispatch(router, "Enter the current password:");
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("wrongoldpw\r"));
            Dispatch(router, "Invalid password specified.");

            Assert.False(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.Idle, tracker.State);
            // Stored value unchanged.
            Assert.Equal("original", protector.Unprotect(profile.Current.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void EmptyNewPassword_PasswordNotChanged_DoesNotCommit()
    {
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            Dispatch(router, "Enter New Password:");
            // User just hits Enter — empty payload after stripping CR.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("\r"));

            Dispatch(router, "Password NOT changed");

            Assert.False(gate.IsLocked);
            Assert.Null(profile.Current!.EncryptedSuicidePassword);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void UseSuicidePrompt_LocksGateButDoesNotCapture()
    {
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            profile.Current!.EncryptedSuicidePassword = protector.Protect("stored");

            Dispatch(router, "Enter your suicide password:");
            Assert.True(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.AwaitingUsePassword, tracker.State);

            // User types whatever — must NOT overwrite stored.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("anything\r"));

            // Server eventually invalids or executes; either way the
            // pattern that fires terminates. Test the invalid path.
            Dispatch(router, "Invalid password specified.");

            Assert.False(gate.IsLocked);
            // Stored unchanged.
            Assert.Equal("stored", protector.Unprotect(profile.Current.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void NotSetLine_WipesStoredPassword()
    {
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            profile.Current!.EncryptedSuicidePassword = protector.Protect("stale");

            // User typed `pro` and server confirmed no password is set.
            Dispatch(router, "You do not have a suicide password set.");

            Assert.Null(profile.Current.EncryptedSuicidePassword);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void IdleState_ObserveOutbound_IsNoOp()
    {
        // Defensive: outbound bytes outside an active flow must never
        // touch the profile. The wire-send path forwards every send
        // through ObserveOutbound — most calls land in Idle.
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("par\r"));
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("look\r"));
            Assert.Null(profile.Current!.EncryptedSuicidePassword);
            Assert.False(gate.IsLocked);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void AnchoredPatterns_DoNotMatchChatLines()
    {
        // Belt-and-braces — chat lines embedding the prompt strings
        // shouldn't lock the gate. Pattern regexes are anchored to
        // line start.
        var (tracker, router, gate, _, _, tmp) = Setup();
        try
        {
            Dispatch(router, "Foo gossips: hey, Enter New Password: lol");
            Dispatch(router, "Bar telepaths: I got Invalid password specified. earlier");
            Assert.False(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.Idle, tracker.State);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void EngineGate_WrapperShortCircuitsWhileLocked()
    {
        // EngineSendGate's wrapper sanity test — the load-bearing
        // mechanism that protects user-typed input from engine
        // pollution during password entry.
        EngineSendGate gate = new();
        List<byte[]> sent = new();
        Action<byte[]> wrapped = gate.WrapEngineSender(sent.Add);

        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Single(sent);

        gate.IsLocked = true;
        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Single(sent); // still 1 — second drop on the floor

        gate.IsLocked = false;
        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Equal(2, sent.Count);
    }

    // ===== Char-mode capture (real MajorMUD path) ========================
    // Password prompts use Telnet ECHO suppression — each keystroke
    // ships as its own ObserveOutbound call, server echoes `*` per char.
    // Pre-fix the tracker overwrote _pendingNewPassword each call and
    // the trailing CR cleared it to null, leaving
    // CharacterProfile.EncryptedSuicidePassword stuck at null on disk.

    [Fact]
    public void CharMode_CapturesAcrossMultipleSingleByteCalls()
    {
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            Dispatch(router, "Enter New Password:");

            // One byte per call — mirrors the real wire trace.
            tracker.ObserveOutbound(new byte[] { (byte)'q' });
            tracker.ObserveOutbound(new byte[] { (byte)'w' });
            tracker.ObserveOutbound(new byte[] { (byte)'e' });
            tracker.ObserveOutbound(new byte[] { (byte)'r' });
            tracker.ObserveOutbound(new byte[] { 0x0D });   // Enter

            Dispatch(router, "Password changed");

            Assert.NotNull(profile.Current!.EncryptedSuicidePassword);
            Assert.Equal("qwer", protector.Unprotect(profile.Current.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void CharMode_BackspaceShrinksAccumulatedPassword()
    {
        // User mistypes 'x', backspaces, types the correct char.
        // Server-side echo treats backspace as deleting one '*' from
        // the displayed asterisks; our captured value must match.
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            Dispatch(router, "Enter New Password:");

            tracker.ObserveOutbound(new byte[] { (byte)'p' });
            tracker.ObserveOutbound(new byte[] { (byte)'a' });
            tracker.ObserveOutbound(new byte[] { (byte)'x' });
            tracker.ObserveOutbound(new byte[] { 0x08 });  // backspace
            tracker.ObserveOutbound(new byte[] { (byte)'s' });
            tracker.ObserveOutbound(new byte[] { (byte)'s' });
            tracker.ObserveOutbound(new byte[] { 0x0D });

            Dispatch(router, "Password changed");

            Assert.Equal("pass", protector.Unprotect(profile.Current!.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void CharMode_DelKey0x7F_AlsoShrinksBuffer()
    {
        // Some terminals send 0x7F (DEL) for the Backspace key instead
        // of 0x08. Tracker treats both identically.
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            Dispatch(router, "Enter New Password:");
            tracker.ObserveOutbound(new byte[] { (byte)'a', (byte)'b', 0x7F, (byte)'c', 0x0D });
            Dispatch(router, "Password changed");

            Assert.Equal("ac", protector.Unprotect(profile.Current!.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void CharMode_LoneEnterAfterPromptDoesNotCommit()
    {
        // User hits Enter immediately at the new-password prompt — the
        // server fires "Password NOT changed" and we must leave the
        // stored value untouched.
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            // Pre-seed something so we can verify it's not overwritten.
            profile.Current!.EncryptedSuicidePassword = protector.Protect("kept");

            Dispatch(router, "Enter New Password:");
            tracker.ObserveOutbound(new byte[] { 0x0D });
            Dispatch(router, "Password NOT changed");

            Assert.Equal("kept", protector.Unprotect(profile.Current.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void CharMode_OldPasswordPhase_DoesNotPollute_NewPasswordCapture()
    {
        // Full set-existing flow as the user actually sees it:
        // current-password phase types pass through unobserved (state
        // gate), then new-password phase captures only that phase's
        // bytes. Pre-fix any leftover from the old phase could have
        // ended up in the captured candidate.
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            Dispatch(router, "Enter the current password:");
            tracker.ObserveOutbound(new byte[] { (byte)'o', (byte)'l', (byte)'d', (byte)'p', (byte)'w', 0x0D });

            Dispatch(router, "Enter New Password:");
            tracker.ObserveOutbound(new byte[] { (byte)'n' });
            tracker.ObserveOutbound(new byte[] { (byte)'e' });
            tracker.ObserveOutbound(new byte[] { (byte)'w' });
            tracker.ObserveOutbound(new byte[] { 0x0D });

            Dispatch(router, "Password changed");

            Assert.Equal("new", protector.Unprotect(profile.Current!.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void CharMode_LFAlsoTerminatesLine()
    {
        // Some BBSes send LF instead of CR. Tracker treats both as
        // line terminators so the password commits on whichever fires.
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            Dispatch(router, "Enter New Password:");
            tracker.ObserveOutbound(new byte[] { (byte)'a', (byte)'b', (byte)'c', 0x0A });
            Dispatch(router, "Password changed");

            Assert.Equal("abc", protector.Unprotect(profile.Current!.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
