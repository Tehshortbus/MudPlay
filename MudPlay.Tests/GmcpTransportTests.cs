using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MudPlay.Net;
using Xunit;

namespace MudPlay.Tests;

public sealed class GmcpTransportTests
{
    [Fact]
    public void EveryFragmentBoundary_PreservesUtf8PayloadAndDisplayOrder()
    {
        byte[] frame = new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes("Anomaly.Test {\"name\":\"Ithelric’s rift — 火\"}")).Concat(new byte[] { 255, 240 }).ToArray();
        for (int split = 0; split <= frame.Length; split++)
        {
            TelnetClient client = new();
            List<string> observed = new();
            client.DataReceived += bytes => observed.Add(Encoding.ASCII.GetString(bytes.Span));
            client.GmcpReceived += message => observed.Add(message.Payload.GetProperty("name").GetString()!);
            byte[] output = new byte[16384];
            client.Interpret(new byte[] { 255, 251, 201 }, output);
            byte[] first = Encoding.ASCII.GetBytes("before").Concat(frame[..split]).ToArray();
            int count = client.Interpret(first, output);
            if (count > 0) observed.Add(Encoding.ASCII.GetString(output, 0, count));
            byte[] second = frame[split..].Concat(Encoding.ASCII.GetBytes("after")).ToArray();
            count = client.Interpret(second, output);
            if (count > 0) observed.Add(Encoding.ASCII.GetString(output, 0, count));
            Assert.Equal(new[] { "before", "Ithelric’s rift — 火", "after" }, observed);
        }
    }

    [Fact]
    public void OversizeMalformedAndUnterminatedFrames_DoNotPoisonNextMessage()
    {
        TelnetClient client = new();
        List<string> errors = new();
        List<GmcpMessage> messages = new();
        client.Log += errors.Add;
        client.GmcpReceived += messages.Add;
        byte[] output = new byte[20000];
        client.Interpret(new byte[] { 255, 251, 201 }, output);
        byte[] Raw(string body) => new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes(body)).Concat(new byte[] { 255, 240 }).ToArray();
        client.Interpret(Raw("Anomaly.Test {"), output);
        client.Interpret(Raw("Anomaly.Test " + new string('x', GmcpProtocol.MaxIncomingBodyBytes + 100)), output);
        client.Interpret(new byte[] { 255, 250, 201, 65, 255, 255, 66, 255, 240 }, output);
        client.Interpret(new byte[] { 255, 250, 201, 65 }, output);
        client.Interpret(GmcpProtocol.Frame("Anomaly.Test", new { hp = 12 }), output);
        Assert.Single(messages);
        Assert.Equal(12, messages[0].Payload.GetProperty("hp").GetInt32());
        Assert.Contains(errors, error => error.Contains("Oversize", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Malformed", StringComparison.Ordinal));
    }

    [Fact]
    public void LargeServerSnapshot_IsAcceptedBeyondTheHostsOutgoingRequestLimit()
    {
        TelnetClient client = new();
        List<GmcpMessage> messages = new();
        client.GmcpReceived += messages.Add;
        byte[] output = new byte[20000];
        client.Interpret(new byte[] { 255, 251, 201 }, output);
        string body = "Anomaly.Test " + JsonSerializer.Serialize(new { description = new string('x', 16000) });
        byte[] frame = new byte[] { 255, 250, 201 }.Concat(Encoding.UTF8.GetBytes(body)).Concat(new byte[] { 255, 240 }).ToArray();
        client.Interpret(frame, output);
        Assert.Equal(16000, Assert.Single(messages).Payload.GetProperty("description").GetString()!.Length);
        Assert.Throws<ArgumentException>(() => GmcpProtocol.Frame("Anomaly.Request", new { package = new string('x', 16000) }));
    }

    [Fact]
    public void MalformedSubnegotiation_RemainderNeverBecomesTerminalText()
    {
        TelnetClient client = new();
        byte[] output = new byte[100];
        byte[] malformed = new byte[] { 255, 250, 201, 65, 255, 42 }.Concat(Encoding.ASCII.GetBytes("not terminal text")).Concat(new byte[] { 255, 240 }).ToArray();
        Assert.Equal(0, client.Interpret(malformed, output));
        Assert.Equal(2, client.Interpret(Encoding.ASCII.GetBytes("ok"), output));
    }

    [Fact]
    public void Wont_RevokesNegotiationAndWillEnablesFreshHandshake()
    {
        TelnetClient client = new();
        List<bool> transitions = new();
        client.GmcpNegotiated += transitions.Add;
        byte[] output = new byte[32];
        client.Interpret(new byte[] { 255, 251, 201, 255, 251, 201, 255, 252, 201, 255, 251, 201 }, output);
        Assert.Equal(new[] { true, false, true }, transitions);
    }

    [Fact]
    public async Task RealSocket_NegotiatesHelloAndSupportsAndSendsRecoveryRequest()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        await using TelnetClient client = new() { ClientVersion = "test" };
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient peer = await accept;
        NetworkStream stream = peer.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 201 });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        List<byte> received = new();
        byte[] buffer = new byte[4096];
        while (!Encoding.UTF8.GetString(received.ToArray()).Contains("Anomaly.Quests 1", StringComparison.Ordinal))
        {
            int count = await stream.ReadAsync(buffer, timeout.Token);
            Assert.True(count > 0);
            received.AddRange(buffer[..count]);
        }
        string text = Encoding.UTF8.GetString(received.ToArray());
        Assert.Equal(new byte[] { 255, 253, 201 }, received.Take(3));
        Assert.Contains("Core.Hello", text);
        Assert.Contains("\"contract\":\"1.2.0\"", text);
        Assert.Contains("Core.Supports.Set", text);
        await client.SendGmcpAsync("Anomaly.Request", new { package = "Anomaly.Mob.Info", uid = 102 });
        int requestLength = await stream.ReadAsync(buffer, timeout.Token);
        Assert.Contains("Anomaly.Request", Encoding.UTF8.GetString(buffer, 0, requestLength));
    }
}
