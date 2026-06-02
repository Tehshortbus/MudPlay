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
            Handler = new SuicideHandler(Engine, Router, Profile, Protector);
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
    public void SuicideWithoutStoredPassword_SendsCommandOnly()
    {
        using Harness h = new();
        GrantElevated(h.Players, "Trusted");
        // No password stored.

        DispatchTelepath(h.Router, "Trusted", "@suicide");

        string joined = string.Join("|", h.Wire.Select(b => Encoding.Latin1.GetString(b)));
        Assert.Contains("suicide\r", joined);
        // No password follow-up because nothing's stored.
        Assert.DoesNotContain("hunter2", joined);
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
