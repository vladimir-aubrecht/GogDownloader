using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using GogArchiver.Core.Content;
using GogArchiver.Core.Download;

namespace GogArchiver.Probes;

public static class StreamingProbes
{
    private const long LargeSize = (1L << 31) + 17;
    // CRC-32 of LargeSize zero bytes, checked independently with Python zlib.crc32.
    private const uint LargeCrc = 0xc7f0dfd3;

    public static async Task RunAsync()
    {
        Probe.Group("Large v1 and Linux files stream with bounded buffers");
        long from = 0, to = 0;
        using var largeHttp = new HttpClient(new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            from = range.From!.Value;
            to = range.To!.Value;
            return Reply(new ZeroStream(LargeSize), LargeSize);
        }));
        var client = new ContentClient(largeHttp, largeHttp, new(0));
        var endpoint = new SecureLinkEndpoint { UrlFormat = "https://fixture.invalid/blob" };
        using var sink = new CountingStream();
        long progress = 0;
        await client.CopyRangeAsync(endpoint, 5_000_000_000, LargeSize, null, sink,
            new SyncProgress<long>(n => progress += n), false, default);
        Probe.Check("a v1 range beyond 2 GiB streams fully without large allocations",
            sink.Position == LargeSize && sink.LargestWrite <= 64 * 1024 && progress == LargeSize
            && from == 5_000_000_000 && to == from + LargeSize - 1);

        sink.Reset(); progress = 0;
        var reader = new MojoSetupReader(largeHttp, "https://fixture.invalid/setup.sh");
        await reader.CopyEntryAsync(new("large", 5_000_000_000, LargeSize, LargeSize, LargeCrc, false, false, false),
            sink, new SyncProgress<long>(n => progress += n), default);
        Probe.Check("a stored Linux entry streams more than 2 GiB and verifies its CRC",
            sink.Position == LargeSize && sink.LargestWrite <= 64 * 1024 && progress == LargeSize
            && from == 5_000_000_000 && to == from + LargeSize - 1);

        using var compressed = new MemoryStream();
        await using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, true))
        {
            var zeros = new byte[64 * 1024];
            for (long left = LargeSize; left > 0; left -= Math.Min(left, zeros.Length))
                await deflate.WriteAsync(zeros.AsMemory(0, (int)Math.Min(left, zeros.Length)));
        }
        var packed = compressed.ToArray();
        using var deflatedHttp = new HttpClient(new Handler(_ => Reply(new MemoryStream(packed), packed.Length)));
        reader = new MojoSetupReader(deflatedHttp, "https://fixture.invalid/setup.sh");
        sink.Reset(); progress = 0;
        await reader.CopyEntryAsync(new("deflated", 123, packed.Length, LargeSize, LargeCrc, true, false, false),
            sink, new SyncProgress<long>(n => progress += n), default);
        Probe.Check("a deflated Linux entry expands beyond 2 GiB with bounded memory and compressed-byte progress",
            sink.Position == LargeSize && sink.LargestWrite <= 64 * 1024 && progress == packed.Length);

        var requests = 0;
        using var noHttp = new HttpClient(new Handler(_ => { requests++; throw new InvalidOperationException("Unexpected empty range"); }));
        var bufferedRangeRejected = false;
        try
        {
            await new ContentClient(noHttp, noHttp, new(0))
                .DownloadRangeAsync(endpoint, 0, LargeSize, null, null, false, default);
        }
        catch (ArgumentOutOfRangeException) { bufferedRangeRejected = true; }
        var bufferedEntryRejected = false;
        try
        {
            await new MojoSetupReader(noHttp, "https://fixture.invalid/setup.sh")
                .ReadEntryAsync(new("large", 0, LargeSize, LargeSize, LargeCrc, false, false, false), null, default);
        }
        catch (ArgumentOutOfRangeException) { bufferedEntryRejected = true; }
        Probe.Check("buffered convenience APIs reject oversize requests before transferring data",
            bufferedRangeRejected && bufferedEntryRejected && requests == 0);
        sink.Reset();
        await new MojoSetupReader(noHttp, "https://fixture.invalid/setup.sh")
            .CopyEntryAsync(new("empty", 100, 0, 0, 0, false, false, false), sink, null, default);
        Probe.Check("an empty stored Linux file needs no invalid HTTP range", requests == 0 && sink.Position == 0);

        await RetryAndCancelAsync();
        await PartOffsetsAsync();
    }

    private static async Task RetryAndCancelAsync()
    {
        Probe.Group("Streaming retains verification, retries and cancellation");
        var attempts = 0;
        var bypassedCache = false;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/secure_link"))
                return new(HttpStatusCode.OK) { Content = new StringContent(
                    "{\"urls\":[{\"url_format\":\"https://fixture.invalid{path}\",\"parameters\":{\"path\":\"/blob\"}}]}",
                    Encoding.UTF8, "application/json") };
            bypassedCache |= request.Headers.CacheControl?.NoCache == true;
            attempts++;
            return Reply(new MemoryStream(Encoding.UTF8.GetBytes(attempts == 1 ? "BAD!" : "GOOD")), 4);
        }));
        var source = new DepotSourceV1(new(http, http, new(0)), new(http), "unused", "windows", "en");
        var hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("GOOD")));
        var part = new PlannedPart(0, 0, 4, hash, 4);
        var file = new PlannedFile("file", "1", 4, hash, [part]);
        using var output = new MemoryStream();
        long progress = 0;
        await source.CopyToAsync(file, part, output, new SyncProgress<long>(n => progress += n), default);
        Probe.Check("a bad streamed MD5 retries from the beginning and overwrites the failed bytes",
            Encoding.UTF8.GetString(output.ToArray()) == "GOOD" && attempts == 2 && bypassedCache && progress == 4);

        if (OperatingSystem.IsLinux())
        {
            var before = attempts;
            using var full = File.OpenHandle("/dev/full", FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                FileOptions.Asynchronous);
            using var destination = new PartWriteStream(full, 0);
            var failed = false;
            try { await source.CopyToAsync(file, part, destination, null, default); }
            catch (StagingWriteException) { failed = true; }
            Probe.Check("a full staging volume is reported without retrying the CDN", failed && attempts == before + 1);
        }

        using var badCrc = new HttpClient(new Handler(_ => Reply(new MemoryStream([1, 2, 3]), 3)));
        var rejected = false;
        try
        {
            await new MojoSetupReader(badCrc, "https://fixture.invalid/setup.sh")
                .CopyEntryAsync(new("bad", 0, 3, 3, 0, false, false, false), Stream.Null, null, default);
        }
        catch (ChunkChecksumException) { rejected = true; }
        Probe.Check("streaming rejects an incorrect Linux CRC", rejected);

        foreach (var linux in new[] { false, true })
        {
            using var cancel = new CancellationTokenSource();
            using var httpCancel = new HttpClient(new Handler(_ => Reply(new ZeroStream(LargeSize), LargeSize)));
            var counter = new SyncProgress<long>(_ => cancel.Cancel());
            var cancelled = false;
            try
            {
                if (linux)
                    await new MojoSetupReader(httpCancel, "https://fixture.invalid/setup.sh").CopyEntryAsync(
                        new("cancel", 0, LargeSize, LargeSize, LargeCrc, false, false, false), Stream.Null, counter, cancel.Token);
                else
                    await new ContentClient(httpCancel, httpCancel, new(0)).CopyRangeAsync(
                        new() { UrlFormat = "https://fixture.invalid/blob" }, 0, LargeSize, null, Stream.Null, counter, false, cancel.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Probe.Check($"{(linux ? "Linux" : "v1")}: cancellation stops a streamed large file after one buffer", cancelled);
        }
    }

    private static async Task PartOffsetsAsync()
    {
        // Unix creates a sparse gap here; Windows may allocate and clear the full 2 GiB.
        if (OperatingSystem.IsWindows()) return;
        var root = Probe.ScratchDirectory();
        try
        {
            var path = Path.Combine(root, "sparse.bin");
            using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite,
                FileOptions.Asynchronous);
            using var first = new PartWriteStream(handle, (1L << 31) - 2);
            using var second = new PartWriteStream(handle, (1L << 31) + 4);
            await Task.WhenAll(first.WriteAsync(Encoding.UTF8.GetBytes("ABCD")).AsTask(),
                second.WriteAsync(Encoding.UTF8.GetBytes("EFGH")).AsTask());
            var bytes = new byte[10];
            await RandomAccess.ReadAsync(handle, bytes, (1L << 31) - 2);
            Probe.Check("independent part writes retain 64-bit offsets across the 2 GiB boundary",
                bytes.SequenceEqual(new byte[] { 65, 66, 67, 68, 0, 0, 69, 70, 71, 72 }));
        }
        finally { Directory.Delete(root, true); }
    }

    private static HttpResponseMessage Reply(Stream body, long length)
    {
        var content = new StreamContent(body);
        content.Headers.ContentLength = length;
        return new(HttpStatusCode.PartialContent) { Content = content };
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count > 1024 * 1024) throw new InvalidOperationException("The reader requested an unbounded buffer.");
            var read = (int)Math.Min(count, length - _position);
            buffer.AsSpan(offset, read).Clear(); _position += read; return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (buffer.Length > 1024 * 1024) throw new InvalidOperationException("The reader requested an unbounded buffer.");
            var read = (int)Math.Min(buffer.Length, length - _position);
            buffer.Span[..read].Clear(); _position += read; return ValueTask.FromResult(read);
        }
    }
    private sealed class CountingStream : Stream
    {
        public int LargestWrite;
        public void Reset() { Position = 0; LargestWrite = 0; }
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => Position;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { Position += count; LargestWrite = Math.Max(LargestWrite, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Position += buffer.Length; LargestWrite = Math.Max(LargestWrite, buffer.Length); return ValueTask.CompletedTask; }
    }
}
