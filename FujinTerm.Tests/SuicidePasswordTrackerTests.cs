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
    public void FirstTimeSet_PrimedByOutboundSetSuicide_CapturesOnPasswordChanged()
    {
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            // User typed `set suicide` — this arms the outbound sniffer.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));

            // Server prompts (first-time set: no old-password phase).
            Dispatch(router, "Enter New Password:");
            Assert.True(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.AwaitingNewPassword, tracker.State);

            // User types "hunter2" then Enter.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("hunter2\r"));

            // Server confirms — Playpen renders this as lowercase
            // "Password changed". Regex tolerates either casing.
            Dispatch(router, "Password changed");

            Assert.False(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.Idle, tracker.State);
            Assert.NotNull(profile.Current!.EncryptedSuicidePassword);
            Assert.Equal("hunter2", protector.Unprotect(profile.Current.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ChangePassword_OldThenNew_RollingBufferKeepsNewOnly()
    {
        // Existing password change: user types old THEN new. The
        // outbound sniffer's 1-deep rolling buffer overwrites the
        // old-password line with the new-password line, so the
        // commit picks up the right value.
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
            Dispatch(router, "Enter the current password:");
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("oldpw\r"));
            Dispatch(router, "Enter New Password:");
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("newpw\r"));
            Dispatch(router, "Password Changed");

            Assert.False(gate.IsLocked);
            Assert.Equal("newpw", protector.Unprotect(profile.Current!.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void BurstedServerResponse_StillCommitsCorrectly()
    {
        // Real failure mode the LogPane revealed: the server batches
        // its prompt acks + "Password changed" into one parser tick
        // AFTER the user has already finished typing both passwords.
        // Pre-fix the state-machine-gated capture missed every byte.
        // Sniffer model captures regardless of state-machine timing.
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
            // User types both passwords BEFORE any inbound prompts fire.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("oldpw\r"));
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("newpw\r"));

            // NOW the server's burst arrives, firing all three patterns
            // in one parser tick.
            Dispatch(router, "Enter the current password:");
            Dispatch(router, "Enter New Password:");
            Dispatch(router, "Password changed");

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

            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
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
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
            Dispatch(router, "Enter New Password:");
            // User just hits Enter — empty line, sniffer ignores it
            // (zero-length lines aren't candidates).
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
        // `suicide` (use form) does NOT arm the sniffer — only
        // `set suicide` does. So even if the user types something at
        // the password challenge, the stored value stays untouched.
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            profile.Current!.EncryptedSuicidePassword = protector.Protect("stored");

            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("suicide\r"));
            Dispatch(router, "Enter your suicide password:");
            Assert.True(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.AwaitingUsePassword, tracker.State);

            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("anything\r"));
            Dispatch(router, "Invalid password specified.");

            Assert.False(gate.IsLocked);
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
    public void RerollLine_WipesStoredPassword()
    {
        var (tracker, router, gate, profile, protector, tmp) = Setup();
        try
        {
            profile.Current!.EncryptedSuicidePassword = protector.Protect("doomed");

            // Successful suicide → the character is rerolled; the realm's
            // stored password dies with it, so our cached copy is wiped.
            Dispatch(router, "After a LONG thought, you take your own life.");

            Assert.Null(profile.Current.EncryptedSuicidePassword);
            Assert.False(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.Idle, tracker.State);
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

        gate.Hold("SuicidePassword");
        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Single(sent); // still 1 — second drop on the floor

        gate.Release("SuicidePassword");
        wrapped(Encoding.Latin1.GetBytes("par\r"));
        Assert.Equal(2, sent.Count);
    }

    // ===== Char-mode capture (real MajorMUD path) ========================
    // Password prompts use Telnet ECHO suppression — each keystroke
    // ships as its own ObserveOutbound call, server echoes `*` per char.
    // The outbound-line sniffer accumulates bytes into a per-line
    // builder and only commits the completed line, so capture works
    // identically in char-mode and line-mode.

    [Fact]
    public void CharMode_CapturesAcrossMultipleSingleByteCalls()
    {
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            // Arm via outbound `set suicide` — same per-byte path as
            // password entry. Could equally be one Latin1.GetBytes("set suicide\r")
            // call; the sniffer accumulates either way.
            foreach (byte b in Encoding.Latin1.GetBytes("set suicide\r"))
                tracker.ObserveOutbound(new[] { b });

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
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
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
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
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

            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
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
        // Full set-existing flow: current-password phase types arrive
        // first, then new-password phase. The sniffer's rolling 1-deep
        // buffer overwrites the old-password line with the new-password
        // line, so the commit picks up the right value.
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
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
        // Arming line uses LF too for symmetry.
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\n"));
            Dispatch(router, "Enter New Password:");
            tracker.ObserveOutbound(new byte[] { (byte)'a', (byte)'b', (byte)'c', 0x0A });
            Dispatch(router, "Password changed");

            Assert.Equal("abc", protector.Unprotect(profile.Current!.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // ===== Sniffer-specific edge cases ===================================

    [Fact]
    public void Sniffer_NotArmedWithoutSetSuicide_PasswordChangedNoOps()
    {
        // Stranger flow: somehow "Password changed" fires without us
        // ever seeing outbound `set suicide`. Should NOT commit
        // whatever happens to be the latest outbound line — could be
        // a chat message, par poll, anything.
        var (tracker, router, _, profile, _, tmp) = Setup();
        try
        {
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("par\r"));
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("who\r"));
            Dispatch(router, "Password changed");

            Assert.Null(profile.Current!.EncryptedSuicidePassword);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Sniffer_SetSuicideArmsCaseInsensitive()
    {
        // MUDs accept any casing on commands. Arming should follow.
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("SET SUICIDE\r"));
            Dispatch(router, "Enter New Password:");
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("hunter2\r"));
            Dispatch(router, "Password changed");

            Assert.Equal("hunter2", protector.Unprotect(profile.Current!.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Sniffer_BareSuicideUseFormDoesNotArm()
    {
        // `suicide` (use form) does NOT change the password. Sniffer
        // must not arm on it, even if "Password changed" somehow lands
        // later.
        var (tracker, router, _, profile, _, tmp) = Setup();
        try
        {
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("suicide\r"));
            // Hypothetical: stale "Password changed" from a prior
            // session's tail end. Sniffer is NOT armed, no commit.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("hunter2\r"));
            Dispatch(router, "Password changed");

            Assert.Null(profile.Current!.EncryptedSuicidePassword);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Sniffer_InvalidPasswordDisarms_SecondPasswordChangedDoesNotCommit()
    {
        // After Invalid, a fresh `set suicide` is needed to re-arm.
        var (tracker, router, _, profile, _, tmp) = Setup();
        try
        {
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("wrongpw\r"));
            Dispatch(router, "Invalid password specified.");  // disarms

            // Some other outbound line + a stray "Password changed"
            // should NOT commit because we're no longer armed.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("chat hello\r"));
            Dispatch(router, "Password changed");

            Assert.Null(profile.Current!.EncryptedSuicidePassword);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // ===== Invalid-password rejection variants ============================
    // Real-Playpen observed two distinct rejection lines depending on
    // which prompt the wrong password was typed at:
    //   "Invalid password specified."  — `suicide` use-form
    //   "Invalid password!"            — `set suicide` with wrong CURRENT
    // Both must disarm the sniffer + unlock the gate. The original
    // regex only matched the first variant, leaving the sniffer armed
    // after a typo in the change-password flow.

    [Theory]
    [InlineData("Invalid password specified.")]
    [InlineData("Invalid password!")]
    [InlineData("Invalid password — try again")]
    [InlineData("invalid password specified.")]  // case tolerance
    public void Sniffer_InvalidPasswordVariantsAllDisarm(string serverLine)
    {
        var (tracker, router, gate, profile, _, tmp) = Setup();
        try
        {
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("wrongpw\r"));
            Dispatch(router, serverLine);
            Assert.False(gate.IsLocked);
            Assert.Equal(SuicidePasswordTracker.FlowState.Idle, tracker.State);

            // Stray "Password changed" after a disarmed flow must NOT commit.
            Dispatch(router, "Password changed");
            Assert.Null(profile.Current!.EncryptedSuicidePassword);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ChangePassword_WrongOldThenRetry_SecondAttemptCommitsCleanly()
    {
        // Real-world recovery flow from Tehshortbus's screenshot:
        // attempt 1 sets the password successfully, attempt 2 types
        // the wrong CURRENT password and server says "Invalid
        // password!", attempt 3 types it correctly and commits.
        var (tracker, router, _, profile, protector, tmp) = Setup();
        try
        {
            // Attempt 1: first-time set (no old phase).
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
            Dispatch(router, "Enter New Password:");
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("asdf\r"));
            Dispatch(router, "Password changed");
            Assert.Equal("asdf", protector.Unprotect(profile.Current!.EncryptedSuicidePassword!));

            // Attempt 2: change with WRONG current password.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
            Dispatch(router, "Enter the current password:");
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("wrongguess\r"));
            Dispatch(router, "Invalid password!");
            // Attempt 1's stored value must survive untouched.
            Assert.Equal("asdf", protector.Unprotect(profile.Current.EncryptedSuicidePassword!));

            // Attempt 3: correct flow this time, new password commits.
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("set suicide\r"));
            Dispatch(router, "Enter the current password:");
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("asdf\r"));
            Dispatch(router, "Enter New Password:");
            tracker.ObserveOutbound(Encoding.Latin1.GetBytes("zxcv\r"));
            Dispatch(router, "Password changed");
            Assert.Equal("zxcv", protector.Unprotect(profile.Current.EncryptedSuicidePassword!));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
