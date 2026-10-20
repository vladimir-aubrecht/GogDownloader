using System.Net;
using GogArchiver.Core.Content;

namespace SteamArchiver.Core;

internal sealed record ContentHttpContext(CancellationToken Cancellation, RateLimiter Limiter)
{
    public AsyncLocal<Action<int>?> CountBytes { get; } = new();
    public AsyncLocal<CancellationToken> RequestCancellation { get; } = new();
}

/// <summary>SteamKit2's CDN API has no caller cancellation parameter. Own the HTTP body
/// lifetime so cancellation aborts reads before the caller can reuse a chunk buffer.</summary>
internal sealed class SteamContentHttpHandler(ContentHttpContext? context) : DelegatingHandler(new SocketsHttpHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context?.Cancellation ?? default,
            context?.RequestCancellation.Value ?? default);
        try
        {
            var response = await base.SendAsync(request, linked.Token);
            response.Content = new Content(response.Content, linked, context?.Limiter, context?.CountBytes.Value);
            return response;
        }
        catch { linked.Dispose(); throw; }
    }

    private sealed class Content : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationTokenRegistration _abort;
        private readonly RateLimiter? _limiter;
        private readonly Action<int>? _countBytes;
        public Content(HttpContent inner, CancellationTokenSource cancellation, RateLimiter? limiter, Action<int>? countBytes)
        {
            _inner = inner; _cancellation = cancellation; _limiter = limiter; _countBytes = countBytes;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
            _abort = cancellation.Token.Register(inner.Dispose);
        }
        protected override bool TryComputeLength(out long length)
        { length = _inner.Headers.ContentLength ?? -1; return length >= 0; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellation.Token);
            // SteamKit2 starts a separate body timeout after receiving the headers.
            // HttpClient's send token alone no longer carries that timeout at this point.
            using var abort = linked.Token.Register(_inner.Dispose);
            await using var input = new ReadStream(await _inner.ReadAsStreamAsync(linked.Token), linked.Token, _limiter, _countBytes);
            await input.CopyToAsync(stream, linked.Token);
        }
        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new ReadStream(await _inner.ReadAsStreamAsync(_cancellation.Token), _cancellation.Token, _limiter, _countBytes);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _abort.Dispose(); _inner.Dispose(); _cancellation.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class ReadStream(Stream inner, CancellationToken operation, RateLimiter? limiter, Action<int>? countBytes) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operation, ct);
            try
            {
                var read = await inner.ReadAsync(buffer, linked.Token);
                countBytes?.Invoke(read);
                if (limiter is not null) await limiter.ConsumeAsync(read, linked.Token);
                return read;
            }
            catch (Exception) when (operation.IsCancellationRequested) { operation.ThrowIfCancellationRequested(); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
