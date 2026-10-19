using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using GogDownloader.Core;
using GogDownloader.Core.Content;
using GogDownloader.Core.Download;
using GogDownloader.Core.Download.Zip;
using GogDownloader.Core.Library;

namespace GogDownloader.Probes;

public static class InstallerLimitProbes
{
    public static async Task RunAsync()
    {
        Probe.Group("Installer transfers share the configured bandwidth limit");
        var data = new byte[64 * 1024];
        var crc = new Crc32();
        crc.Append(data);
        foreach (var deflated in new[] { false, true })
        {
            var packed = data;
            if (deflated)
            {
                using var output = new MemoryStream();
                using (var stream = new DeflateStream(output, CompressionLevel.Fastest, true)) stream.Write(data);
                packed = output.ToArray();
            }
            using var http = new HttpClient(new BodyHandler(packed));
            var entry = new InstallerEntry("game.bin", 0, packed.Length, data.Length, crc.Value, deflated, false, false);
            using var sink = new MemoryStream();
            var limited = await CancelWhileLimitedAsync(ct =>
                new MojoSetupReader(http, "https://fixture.invalid/setup.sh", new(1)).CopyEntryAsync(entry, sink, null, ct));
            Probe.Check($"Linux {(deflated ? "deflated" : "stored")} content waits for bandwidth and remains cancellable",
                limited && sink.Length == 0);
            await new MojoSetupReader(http, "https://fixture.invalid/setup.sh", new(0)).CopyEntryAsync(entry, sink, null, default);
            Probe.Check($"Linux {(deflated ? "deflated" : "stored")} content transfers normally with the limit disabled", sink.Length == data.Length);
        }

        var root = Probe.ScratchDirectory();
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // Detection is all this fake tool is needed for: the limited download must be
            // cancelled before extraction starts, on every host platform.
            var bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "innoextract.exe" : "innoextract"), "");
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + oldPath);
            foreach (var platform in new[] { GamePlatform.Windows, GamePlatform.Linux })
            {
                using var fixture = new InstallerFixture(platform, data);
                using var http = new HttpClient(fixture);
                var shared = new RateLimiter(1);
                await shared.ConsumeAsync(1, default);
                var downloader = new GameDownloader(new(http), new(http, http, shared), http);
                var game = new LibraryEntry
                {
                    Id = 1, Slug = "limited", Title = "Limited",
                    Platforms = new() { [platform] = new() { Installers = [new("en", "1", fixture.Payload.Length)] } },
                };
                var options = new DownloadOptions
                {
                    OutputRoot = Path.Combine(root, "out"), StagingRoot = Path.Combine(root, "staging"),
                    Platform = platform, WriteLauncher = false,
                };
                Probe.Check($"{platform}: the full downloader passes its shared limiter to the installer path",
                    await CancelWhileLimitedAsync(ct => downloader.RunAsync(game, options, ct: ct)) && fixture.BodyRequests > 0);
                Probe.Check($"{platform}: cancellation while throttled never publishes a backup",
                    await BackupManifest.LoadAsync(Path.Combine(options.OutputRoot, game.DirectoryName)) is null);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> CancelWhileLimitedAsync(Func<CancellationToken, Task> action)
    {
        // One byte/second cannot refill a single transfer block before this cancellation.
        // A missing limiter completes the transfer (or reaches extraction) instead.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try { await action(cancel.Token); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return true; }
        catch (DownloadInterruptedException) when (cancel.IsCancellationRequested) { return true; }
        catch (Exception e) { Probe.Note($"Transfer reached {e.GetType().Name} instead of waiting for bandwidth."); }
        return false;
    }

    private sealed class BodyHandler(byte[] data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data) });
    }

    private sealed class InstallerFixture : HttpMessageHandler
    {
        private readonly GamePlatform _platform;
        public byte[] Payload { get; }
        public int BodyRequests;
        public InstallerFixture(GamePlatform platform, byte[] data)
        {
            _platform = platform;
            if (platform == GamePlatform.Windows) Payload = data;
            else
            {
                using var output = new MemoryStream();
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                using (var entry = zip.CreateEntry("data/noarch/game.bin", CompressionLevel.NoCompression).Open()) entry.Write(data);
                Payload = [.. Encoding.UTF8.GetBytes("#!/bin/sh\n"), .. output.ToArray()];
            }
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpContent body;
            var status = HttpStatusCode.OK;
            if (path.StartsWith("/products/"))
                body = Json(new { downloads = new { installers = new[] { new { id = "1", os = _platform.ApiName(), language = "en",
                    version = "1", total_size = Payload.Length, files = new[] { new { id = "1", size = Payload.Length, downlink = "https://fixture.invalid/link" } } } } } });
            else if (path == "/link") body = Json(new { downlink = "https://fixture.invalid/setup.exe" });
            else if (path == "/setup.exe")
            {
                BodyRequests++;
                if (request.Headers.Range?.Ranges.Single() is { } range)
                {
                    var from = (int)range.From!.Value;
                    var to = (int)Math.Min(range.To!.Value, Payload.Length - 1);
                    body = new ByteArrayContent(Payload[from..(to + 1)]);
                    body.Headers.ContentRange = new(from, to, Payload.Length);
                    status = HttpStatusCode.PartialContent;
                }
                else body = new ByteArrayContent(Payload);
            }
            else throw new InvalidOperationException("Unexpected fixture request: " + path);
            return Task.FromResult(new HttpResponseMessage(status) { Content = body });
        }
        private static HttpContent Json(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }
}
