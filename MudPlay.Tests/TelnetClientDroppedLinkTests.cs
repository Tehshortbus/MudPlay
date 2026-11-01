using System.Net;
using System.Net.Sockets;
using MudPlay.Net;
using Xunit;

namespace MudPlay.Tests;

// A window size sent to a link that has already dropped (a profile load re-applying
// the terminal size, a resize) faulted on the finalizer thread as a crash: its
// caller doesn't wait on it (Crash-20261030-095446 / -095447). The failed write is
// logged, and the drop is left to the read loop to report.
public sealed class TelnetClientDroppedLinkTests
{
    [Fact]
    public async Task AWindowSizeSentAfterTheLinkDropped_IsLogged_NotThrown()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using TelnetClient client = new();
        List<string> log = new();
        client.Log += line => { lock (log) log.Add(line); };
        TaskCompletionSource dropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => dropped.TrySetResult();

        Task<Socket> accepted = listener.AcceptSocketAsync();
        await client.ConnectAsync("127.0.0.1", port);
        using Socket server = await accepted;
        // The board asks for the window size, and the client agrees and sends it.
        await server.SendAsync(new byte[] { TelnetProtocol.IAC, TelnetProtocol.DO, TelnetProtocol.OptNaws });
        byte[] reply = new byte[64];
        int read = 0;
        while (read < 3 + 9) read += await server.ReceiveAsync(reply.AsMemory(read));

        // The link drops: the board resets it.
        server.LingerState = new LingerOption(true, 0);
        server.Close();
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await client.SendWindowSizeAsync(80, 50);
        await client.SendWindowSizeAsync(80, 50);   // a later write fails for sure once the reset is in

        lock (log) Assert.Contains(log, l => l.StartsWith("NAWS not sent: ", StringComparison.Ordinal));
    }
}
