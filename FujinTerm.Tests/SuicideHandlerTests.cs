using System.IO;
using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the @suicide remote-command consumer. The interesting
/// behaviours: sends both commands when a password is stored, replies
/// to the original sender on invalid response, and ignores invalid
/// lines that don't belong to one of our pending invocations.
/// </summary>
public sealed class SuicideHandlerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; }
        public ProfileService Profile { get; }
        public PasswordProtector Protector { get; }
        public PartyState Party { get; } = new();
        public PlayerState PlayerState { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public LogService Log { get; } = new();
        public WirePromptScanner PromptScanner { get; } = new();
        public RemoteCommandManager Engine { get; }
        public SuicideHandler Handler { get; }
        public List<byte[]> Wire { get; } = new();
        public string TmpDir { get; }

        public Harness(int currentLives = 9)
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            // ChatRouter is required for RemoteCommandManager to receive
            // telepath input.
            ChatRouter chat = new(Router);
            Profile = new ProfileService();
            Profile.LoadBlank();
            TmpDir = Path.Combine(Path.GetTempPath(), "fterm-suicide-handler-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TmpDir);
            Protector = new PasswordProtector(Path.Combine(TmpDir, ".credkey"));
            Engine = new RemoteCommandManager(chat, Party, Players, Log)
            {
                LivesProvider = () => currentLives,
                MaxSuicideLivesThreshold = 3,
            };
            Engine.SetWireSender(Wire.Add);
            Handler = new SuicideHandler(Engine, Router, Profile, Protector, PromptScanner, Log);
            Handler.SetWireSender(Wire.Add);
        }

        public void Dispose()
        {
            Handler.Dispose();
            Engine.Dispose();
            Directory.Delete(TmpDir, recursive: true);
        }
    }

    private static void DispatchTelepath(MessageRouter router, string from, string body)
    {
        string line = $"{from} telepaths: {body}";
        router.Dispatch(new LineExtractor.EmittedLine(
            line, new CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
    }

    private static void DispatchLine(MessageRouter router, string text) =>
        router.Dispatch(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(), DateTimeOffset.UnixEpoch, IsPromptLine: false));

    private static void GrantElevated(PlayerDatabase db, string name)
    {
        db.RecordObservation(name, null, null, null, null, null, null, DateTime.UtcNow);
        db.EditCustomization(name, new PlayerCustomization(
            RemoteControls: PlayerRemoteControls.SysopCommands));
    }

    [Fact]
    public void SuicideWithStoredPassword_SendsCommandAndPassword()
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("hunter2");

        DispatchTelepath(h.Router, "Trusted", "@suicide");

        // Two sends from the handler: "suicide\r" then "hunter2\r".
        // The engine itself may also send a denial reply if mis-routed,
        // so check the handler-driven bytes explicitly.
        string joined = string.Join("|", h.Wire.Select(b => Encoding.Latin1.GetString(b)));
        Assert.Contains("suicide\r", joined);
        Assert.Contains("hunter2\r", joined);
    }

    [Fact]
    public void NoStoredPassword_SendsProFirst_NotSuicideImmediately()
    {
        // Updated flow: with no stored password, we don't know
        // whether the realm has a password set. Type `pro` first
        // and disambiguate from the response. If we sent bare
        // `suicide` straight away and the realm DID have a password,
        // we'd hang at the "Enter your suicide password:" prompt
        // with no answer.
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");

        DispatchTelepath(h.Router, "Trusted", "@suicide");

        // First wire-send from the handler is `pro\r`; suicide must
        // NOT be on the wire yet (waiting on `pro` response).
        string joined = string.Join("|", h.Wire.Select(b => Encoding.Latin1.GetString(b)));
        Assert.Contains("pro\r", joined);
        Assert.DoesNotContain("suicide\r", joined);
    }

    [Fact]
    public void NoStoredPw_RealmConfirmsNotSet_SendsSuicideAtNextStatline()
    {
        // pro pre-check: server response includes "You do not have a
        // suicide password set." before the next statline arrives.
        // Decision point is the NEXT prompt observation (which
        // brackets pro output per the Playpen wire trace) — at that
        // point _seenNotSetInProWindow is true, fire suicide.
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();   // drop the `pro` send

        // Server response: "not set" line during the window...
        DispatchLine(h.Router, "You do not have a suicide password set.");
        // ...then the next statline closes the window.
        h.Handler.FireNextPromptForTests();

        byte[] sent = Assert.Single(h.Wire);
        Assert.Equal("suicide\r", Encoding.Latin1.GetString(sent));
    }

    [Fact]
    public void NoStoredPw_NextStatlineWithoutNotSet_RepliesMismatch()
    {
        // pro output doesn't include "not set" → realm has a password
        // we don't have stored. Mismatch — telepath sender with the
        // user-specified reply text.
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        // Pro output arrives + next statline closes the window
        // without "not set" ever appearing.
        h.Handler.FireNextPromptForTests();

        byte[] sent = Assert.Single(h.Wire);
        string text = Encoding.Latin1.GetString(sent);
        Assert.Contains("/Trusted", text);
        Assert.Contains("Suicide failed, password set in game but not stored.", text);
    }

    [Fact]
    public void NoStoredPw_NextStatlineWithWarnOnDenialOff_StaysSilent()
    {
        using Harness h = new();
        h.Engine.WarnOnDenial = false;
        GrantElevated(h.Players, "Trusted");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        h.Handler.FireNextPromptForTests();

        Assert.Empty(h.Wire);
    }

    [Fact]
    public void NoStoredPw_SecondAttemptDuringProCheck_Refused()
    {
        // Second @suicide arrives while the pro window is open from
        // the first one — refuse the second defensively so we don't
        // double-send `pro` or end up in tangled state.
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        GrantElevated(h.Players, "AlsoTrusted");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        DispatchTelepath(h.Router, "AlsoTrusted", "@suicide");

        // Second invocation gets a denial reply (single send),
        // no extra `pro` on the wire.
        byte[] sent = Assert.Single(h.Wire);
        string text = Encoding.Latin1.GetString(sent);
        Assert.Contains("/AlsoTrusted", text);
        Assert.Contains("already in-flight", text);
    }

    [Fact]
    public void NoStoredPw_NotSetLineOutsidePending_NotAReply()
    {
        // User typed `pro` manually (no @suicide pending). The
        // "not set" line fires but we shouldn't send suicide on the
        // wire — only @suicide-initiated checks should drive that.
        using Harness h = new();
        // No DispatchTelepath @suicide call — handler has no pending state.

        DispatchLine(h.Router, "You do not have a suicide password set.");

        // No `suicide\r` from the handler.
        Assert.DoesNotContain(h.Wire,
            b => Encoding.Latin1.GetString(b).Contains("suicide\r"));
    }

    [Fact]
    public void InvalidResponse_TelepathsBackToSender()
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("wrongpw");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        // Server responds with the invalid line.
        DispatchLine(h.Router, "Invalid password specified.");

        // Handler should have telepathed the sender with the canonical
        // failure body wrapped in braces.
        byte[] sent = Assert.Single(h.Wire);
        string text = Encoding.Latin1.GetString(sent);
        Assert.Equal("/Trusted {invalid suicide password is stored, unable}\r", text);
    }

    [Fact]
    public void InvalidResponse_WhenWarnOnDenialOff_StaysSilent()
    {
        // Per the engine-wide reply policy: WarnOnDenial gates ALL
        // invalid / denial replies including specific-reason ones
        // emitted by handlers. With the flag off, the
        // invalid-password telepath is suppressed.
        using Harness h = new();
        h.Engine.WarnOnDenial = false;
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("wrongpw");

        DispatchTelepath(h.Router, "Trusted", "@suicide");
        h.Wire.Clear();

        DispatchLine(h.Router, "Invalid password specified.");

        Assert.DoesNotContain(h.Wire,
            b => Encoding.Latin1.GetString(b).Contains("invalid suicide password is stored"));
    }

    [Fact]
    public void InvalidResponseWithNoPendingInvocation_IsIgnored()
    {
        // A "Invalid password specified." line from the user's own
        // manual `suicide` attempt mustn't trigger a reply (no @suicide
        // was issued, no _pendingReply captured).
        using Harness h = new();
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("foo");

        DispatchLine(h.Router, "Invalid password specified.");

        // No telepath was queued because nothing was pending.
        Assert.DoesNotContain(h.Wire,
            b => Encoding.Latin1.GetString(b).Contains("invalid suicide password is stored"));
    }

    [Fact]
    public void UnauthorisedSender_IsDeniedByEngine_NoCommandSent()
    {
        // Sender has no permissions on the Players-tab row → engine
        // refuses to invoke the handler. We should observe NO
        // "suicide" command on the wire from the handler itself.
        using Harness h = new();
        // Don't grant Elevated to this player.
        h.Players.RecordObservation("Stranger", null, null, null, null, null, null, DateTime.UtcNow);

        DispatchTelepath(h.Router, "Stranger", "@suicide");

        string joined = string.Join("|", h.Wire.Select(b => Encoding.Latin1.GetString(b)));
        Assert.DoesNotContain("suicide\r", joined);
    }

    [Fact]
    public void HardBlockAtLowLives_PreventsInvocation()
    {
        // Lives ≤ MaxSuicideLivesThreshold = engine refuses the
        // invocation even with Elevated permission.
        using Harness h = new(currentLives: 2);   // threshold 3 default
        GrantElevated(h.Players, "Trusted");
        h.Profile.Current!.EncryptedSuicidePassword = h.Protector.Protect("foo");

        DispatchTelepath(h.Router, "Trusted", "@suicide");

        string joined = string.Join("|", h.Wire.Select(b => Encoding.Latin1.GetString(b)));
        Assert.DoesNotContain("suicide\r", joined);
    }

    [Fact]
    public void Catalog_SuicideMappedToSysopCommands()
    {
        // Pin the catalog mapping so a future refactor that
        // accidentally drops @suicide or moves it to a different
        // category gets caught.
        Assert.True(RemoteCommandCatalog.TryGetCategory("@suicide", out PlayerRemoteControls cat));
        Assert.Equal(PlayerRemoteControls.SysopCommands, cat);
    }
}
