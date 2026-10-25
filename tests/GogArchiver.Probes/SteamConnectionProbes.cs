using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using SteamArchiver.Core;
using SteamKit2;
using SteamKit2.Discovery;

namespace GogArchiver.Probes;

/// <summary>Exercise the real WebSocket handshake and SteamKit callback pump without a Steam account or network.</summary>
public static class SteamConnectionProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Steam connection and WebSocket transport");
        await TransportAsync();
        await RetryAsync();
        await FailureAsync();
    }

    private static async Task TransportAsync()
    {
        var broken = new UpgradeHandler();
        using (var http = new HttpClient(new SteamContentHttpHandler(null, broken)))
        using (var socket = new ClientWebSocket())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var rejected = false;
            try { await socket.ConnectAsync(new("wss://test.invalid/cmsocket/"), http, timeout.Token); }
            catch (WebSocketException) { rejected = true; }
            Probe.Check("the CDN body wrapper reproduces the pre-authentication WebSocket failure", rejected && broken.Attempts == 1);
        }

        var upgrade = new UpgradeHandler();
        using (var http = SteamKitSession.CreateHttpClient(HttpClientPurpose.CMWebSocket, TimeSpan.FromSeconds(5), transport: upgrade))
        using (var socket = new ClientWebSocket())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await socket.ConnectAsync(new("wss://test.invalid/cmsocket/"), http, timeout.Token);
            await socket.SendAsync(new byte[] { 1, 2, 3 }.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            Probe.Check("CM WebSockets keep a duplex stream and can send after the HTTP upgrade", socket.State == WebSocketState.Open
                && upgrade.Stream is { BytesWritten: > 0 });
        }
        Probe.Check("disposing the WebSocket client releases its transport", upgrade.Disposed && upgrade.Stream!.Disposed);

        foreach (var purpose in new[] { HttpClientPurpose.CDN, HttpClientPurpose.WebAPI })
        {
            var transport = new PlainHandler();
            using var http = SteamKitSession.CreateHttpClient(purpose, TimeSpan.FromSeconds(5), transport: transport);
            Probe.Check($"{purpose} responses still support normal body reads", await http.GetStringAsync("https://test.invalid/") == "content");
        }
    }

    private static SteamKitSession Session(SteamClientOptions options, UpgradeHandler handler) => new(options, b => b
        .WithDirectoryFetch(false)
        .WithProtocolTypes(ProtocolTypes.WebSocket)
        .WithServerListProvider(new Servers())
        .WithHttpClientFactory(p => SteamKitSession.CreateHttpClient(p, options.OperationTimeout, transport: handler)));

    private static async Task RetryAsync()
    {
        var handler = new UpgradeHandler { Failures = 2 };
        var guards = 0;
        await using var session = Session(new() { OperationTimeout = TimeSpan.FromSeconds(5),
            Authenticate = (_, _) => { guards++; throw new Exception("Connection must precede Steam Guard"); } }, handler);
        await session.ConnectAsync(default);
        Probe.Check("SteamKit retries failed endpoints and establishes the third connection", handler.Attempts == 3 && !session.Disconnected
            && handler.Hosts.Distinct().Count() == 3);
        Probe.Check("connection retries do not invoke Steam Guard or send credentials", guards == 0);
        await session.ConnectAsync(default);
        Probe.Check("a connected session is reused without reconnecting", handler.Attempts == 3);

        handler.Stream!.EndInput.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!session.Disconnected) await Task.Delay(10, timeout.Token);
        string? message = null;
        try { await session.ConnectAsync(timeout.Token); }
        catch (SteamAuthenticationException e) { message = e.Message; }
        Probe.Check("disconnects after connection report their phase and do not restart authentication", message?.Contains("starting authentication") == true
            && handler.Attempts == 3);
    }

    private static async Task FailureAsync()
    {
        var failed = new UpgradeHandler { Failures = int.MaxValue };
        await using (var session = Session(new() { OperationTimeout = TimeSpan.FromSeconds(5) }, failed))
        {
            string? message = null;
            try { await session.ConnectAsync(default); }
            catch (SteamAuthenticationException e) { message = e.Message; }
            Probe.Check("failed connections stop after three attempts with a pre-authentication diagnosis", failed.Attempts == 3 && session.Disconnected
                && message?.Contains("after 3 attempts, before authentication") == true && !message.Contains("Recreate the client"));
        }

        var cancelled = new UpgradeHandler { Hang = true };
        await using (var session = Session(new() { OperationTimeout = TimeSpan.FromSeconds(5) }, cancelled))
        using (var cancel = new CancellationTokenSource())
        {
            var connecting = session.ConnectAsync(cancel.Token);
            await cancelled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            var interrupted = false;
            try { await connecting; } catch (OperationCanceledException) { interrupted = true; }
            Probe.Check("user cancellation stops the connection without retries", interrupted && session.Disconnected && cancelled.Attempts == 1);
        }

        var stalled = new UpgradeHandler { Hang = true };
        await using (var session = Session(new() { OperationTimeout = TimeSpan.FromMilliseconds(150) }, stalled))
        {
            string? message = null;
            try { await session.ConnectAsync(default); }
            catch (SteamAuthenticationException e) { message = e.Message; }
            Probe.Check("connection timeout is reported separately from user cancellation", session.Disconnected
                && message?.Contains("Timed out connecting to Steam, before authentication") == true && stalled.Attempts == 1);
        }

        var backoff = new UpgradeHandler { Failures = int.MaxValue };
        await using (var session = Session(new() { OperationTimeout = TimeSpan.FromMilliseconds(150) }, backoff))
        {
            string? message = null;
            try { await session.ConnectAsync(default); }
            catch (SteamAuthenticationException e) { message = e.Message; }
            Probe.Check("all connection attempts and retry delays share one timeout budget", message?.Contains("Timed out connecting") == true
                && backoff.Attempts == 1);
        }
    }

    private sealed class Servers : IServerListProvider
    {
        public DateTime LastServerListRefresh => DateTime.UtcNow;
        public Task<IEnumerable<ServerRecord>> FetchServerListAsync() => Task.FromResult(Enumerable.Range(0, 3)
            .Select(i => ServerRecord.CreateWebSocketServer($"cm-{i}.test.invalid:443")));
        public Task UpdateServerListAsync(IEnumerable<ServerRecord> endpoints) => Task.CompletedTask;
    }

    private sealed class UpgradeHandler : HttpMessageHandler
    {
        public int Failures;
        public bool Hang;
        public int Attempts;
        public bool Disposed;
        public readonly List<string> Hosts = [];
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DuplexStream? Stream;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++; Hosts.Add(request.RequestUri!.Host); Started.TrySetResult();
            if (Hang) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (Attempts <= Failures) throw new HttpRequestException("Simulated unavailable CM endpoint");
            var key = request.Headers.GetValues("Sec-WebSocket-Key").Single();
            Stream = new DuplexStream();
            var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols)
            { RequestMessage = request, Content = new UpgradeContent(Stream) };
            response.Headers.Connection.Add("Upgrade");
            response.Headers.Upgrade.ParseAdd("websocket");
            response.Headers.Add("Sec-WebSocket-Accept", Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))));
            return response;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class UpgradeContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Stream CreateContentReadStream(CancellationToken ct) => stream;
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }

    private sealed class DuplexStream : Stream
    {
        public readonly TaskCompletionSource EndInput = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long BytesWritten;
        public bool Disposed;
        public override bool CanRead => !Disposed;
        public override bool CanWrite => !Disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await EndInput.Task.WaitAsync(ct); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => Interlocked.Add(ref BytesWritten, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Interlocked.Add(ref BytesWritten, buffer.Length); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing) { Disposed = true; EndInput.TrySetResult(); base.Dispose(disposing); }
    }

    private sealed class PlainHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("content") });
    }
}
