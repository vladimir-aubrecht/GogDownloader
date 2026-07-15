using System.Diagnostics;
using System.Text.Json;
using GogDownloader.Core.Content;

namespace GogDownloader.Probes;

/// <summary>
/// The Galaxy content system: the parts where a wrong guess produces a plausible-looking URL
/// that silently downloads nothing, or a limiter that never lets go.
/// </summary>
public static class ContentProbes
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A stream whose reads never complete until cancelled, like a silent connection.</summary>
    private sealed class StallingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>Sends a Content-Length it does not honour, then closes — a truncated response.</summary>
    private sealed class TruncatingServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        public string Url { get; }

        public TruncatingServer(int promised, int sent)
        {
            var port = Random.Shared.Next(20000, 60000);
            Url = $"http://127.0.0.1:{port}/chunk";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    ctx.Response.ContentLength64 = promised;      // promise more than we send
                    await ctx.Response.OutputStream.WriteAsync(new byte[sent]);
                    ctx.Response.Abort();                          // close early
                }
                catch { }
            });
        }

        public void Dispose() { _listener.Stop(); _listener.Close(); }
    }

    public static async Task RunAsync()
    {
        Probe.Group("galaxy_path");
        var path = GalaxyPath.For("d9b5e0dbb0a5b39b1c2c3f0e1a2b3c4d");
        Probe.Check("inserts two directory levels", path == "d9/b5/d9b5e0dbb0a5b39b1c2c3f0e1a2b3c4d", path);
        Probe.Check("leaves a path that already has slashes", GalaxyPath.For("ab/cd/abcd") == "ab/cd/abcd");

        Probe.Group("Chunk URLs from a secure link");
        const string secureLink = """
            {"urls":[{"priority":0,"url_format":"https://gog-cdn-fastly.gog.com{path}?token={token}",
            "parameters":{"path":"/content-system/v2/store/1207658930","token":"abc123","expires_at":1799999999}}]}
            """;
        var endpoint = JsonSerializer.Deserialize<SecureLinkResponse>(secureLink, Json)!.Urls[0];

        var url = endpoint.ChunkUrl("d9b5e0dbb0a5b39b1c2c3f0e1a2b3c4d");
        Probe.Check("appends galaxy_path into {path}, then substitutes",
            url == "https://gog-cdn-fastly.gog.com/content-system/v2/store/1207658930/d9/b5/d9b5e0dbb0a5b39b1c2c3f0e1a2b3c4d?token=abc123",
            url);

        // gogdl mutates its parameters dict in place; ours must not, or the second chunk of a
        // file would get a path with the first chunk's hash still glued on.
        var second = endpoint.ChunkUrl("aabbccddeeff00112233445566778899");
        Probe.Check("is idempotent — parameters are not mutated",
            second.EndsWith("/aa/bb/aabbccddeeff00112233445566778899?token=abc123"), second);

        Probe.Group("Depot manifest");
        const string depot = """
            {"depot":{"items":[
             {"type":"DepotFile","path":"game\\bin\\x.exe","chunks":[
               {"compressedMd5":"c1","md5":"m1","size":10,"compressedSize":5},
               {"compressedMd5":"c2","md5":"m2","size":7,"compressedSize":4}],"md5":"whole","flags":["executable"]},
             {"type":"DepotFile","path":"scripts\\goggame.script","chunks":[
               {"compressedMd5":"c3","md5":"m3","size":3,"compressedSize":2}],"flags":["support"]},
             {"type":"DepotDirectory","path":"game\\bin"},
             {"type":"DepotLink","path":"a/link","target":"game/bin/x.exe"}]}}
            """;
        var manifest = JsonSerializer.Deserialize<DepotManifest>(depot, Json)!;
        Probe.Check("parses every item", manifest.Depot.Items.Count == 4);

        var exe = manifest.Depot.Items[0];
        Probe.Check("file size is the sum of chunk sizes", exe.Size == 17, exe.Size.ToString());
        Probe.Check("identity prefers md5", exe.Identity == "whole", exe.Identity);
        Probe.Check("identity of a single-chunk file falls back to the chunk md5",
            manifest.Depot.Items[1].Identity == "m3", manifest.Depot.Items[1].Identity);
        Probe.Check("support flag is recognised", manifest.Depot.Items[1].IsSupport);
        Probe.Check("link carries its target", manifest.Depot.Items[3].Target == "game/bin/x.exe");

        Probe.Group("Depot language filter");
        Probe.Check("'*' matches any language", new DepotRef { Languages = ["*"] }.MatchesLanguage("en"));
        Probe.Check("'de' does not match 'en'", !new DepotRef { Languages = ["de"] }.MatchesLanguage("en"));
        Probe.Check("matching ignores case", new DepotRef { Languages = ["de"] }.MatchesLanguage("DE"));

        // Depots carry full codes. Comparing the raw request would match nothing but the
        // neutral depots, and the game would download as a near-empty archive.
        Probe.Check("'en' matches a depot tagged en-US",
            new DepotRef { Languages = ["en-US"] }.MatchesLanguage("en"));
        Probe.Check("'en' does not match British English",
            !new DepotRef { Languages = ["en-GB"] }.MatchesLanguage("en"));
        Probe.Check("'de' matches a depot tagged de-DE",
            new DepotRef { Languages = ["de-DE"] }.MatchesLanguage("de"));
        Probe.Check("'cz' matches a depot tagged cs-CZ",
            new DepotRef { Languages = ["cs-CZ"] }.MatchesLanguage("cz"));
        Probe.Check("'en' does not match a German depot",
            !new DepotRef { Languages = ["de-DE"] }.MatchesLanguage("en"));
        Probe.Check("a depot listing several languages matches any of them",
            new DepotRef { Languages = ["de-DE", "en-US"] }.MatchesLanguage("en"));
        Probe.Check("an unknown request matches nothing",
            !new DepotRef { Languages = ["en-US"] }.MatchesLanguage("klingon"));

        Probe.Group("Language table");
        Probe.Check("'en' resolves to en-US", GogLanguage.Parse("en")?.Code == "en-US", GogLanguage.Parse("en")?.Code);
        Probe.Check("the full code resolves to itself", GogLanguage.Parse("en-US")?.Code == "en-US");
        Probe.Check("the English name resolves", GogLanguage.Parse("English")?.Code == "en-US");
        Probe.Check("'cz' resolves to cs-CZ", GogLanguage.Parse("cz")?.Code == "cs-CZ");
        Probe.Check("an unknown language resolves to null", GogLanguage.Parse("klingon") is null);
        Probe.Check("the table came from gogdl in full", GogLanguage.All.Length == 84, GogLanguage.All.Length.ToString());

        Probe.Group("Repository manifest");
        const string repository = """
            {"version":2,"baseProductId":"1207658930","buildId":"55","installDirectory":"The Game",
             "dependencies":["MSVC2015"],
             "products":[{"productId":"1207658930","name":"Base"},{"productId":"99","name":"DLC"}],
             "depots":[{"manifest":"abcd","productId":"99","languages":["*"],"size":1,"compressedSize":1}]}
            """;
        var repo = JsonSerializer.Deserialize<RepositoryManifest>(repository, Json)!;
        Probe.Check("reads baseProductId and products", repo.BaseProductId == "1207658930" && repo.Products.Count == 2);
        Probe.Check("reads dependencies", repo.Dependencies is ["MSVC2015"]);
        Probe.Check("reads depot productId", repo.Depots[0].ProductId == "99");

        Probe.Group("Generation 1 manifests");
        const string repositoryV1 = """
            {"product":{"rootGameID":"1207658930","timestamp":1445945558,"installDirectory":"Quake",
             "depots":[
               {"manifest":"aaa","gameIDs":["1207658930"],"languages":["Neutral"],"size":"12345"},
               {"manifest":"bbb","gameIDs":["1207658930"],"languages":["de-DE"],"size":"999"},
               {"manifest":"ccc","gameIDs":["99"],"languages":["Neutral"],"size":"50"},
               {"redist":"MSVC2015","size":"1","targetDir":"__redist"}]}}
            """;
        var v1 = JsonSerializer.Deserialize<RepositoryManifestV1>(repositoryV1, Json)!;
        Probe.Check("reads rootGameID", v1.Product.RootGameId == "1207658930");
        Probe.Check("a numeric timestamp becomes the string used in URLs",
            v1.Product.TimestampText == "1445945558", v1.Product.TimestampText);
        Probe.Check("size is a string in the JSON but a number to us",
            v1.Product.Depots[0].Size == 12345, v1.Product.Depots[0].Size.ToString());
        Probe.Check("a redist depot is not game content", v1.Product.Depots[3].IsRedistributable);
        Probe.Check("a content depot is not a redist", !v1.Product.Depots[0].IsRedistributable);

        // v1 spells the neutral tag differently; treating `*` as universal would drop everything.
        Probe.Check("'Neutral' matches any language", v1.Product.Depots[0].MatchesLanguage("en"));
        Probe.Check("a German v1 depot does not match English", !v1.Product.Depots[1].MatchesLanguage("en"));
        Probe.Check("a full code still matches", new DepotRefV1 { Languages = ["en-US"] }.MatchesLanguage("en"));

        const string depotV1 = """
            {"depot":{"files":[
              {"path":"quake/id1/pak0.pak","size":18689235,"offset":0,"hash":"abc"},
              {"path":"quake","directory":true},
              {"path":"goggame.script","size":10,"offset":18689235,"hash":"def","support":true},
              {"path":"empty.txt","size":0,"offset":0,"hash":"000"}]}}
            """;
        var depotFiles = JsonSerializer.Deserialize<DepotManifestV1>(depotV1, Json)!.Depot.Files;
        Probe.Check("reads every record", depotFiles.Count == 4);
        Probe.Check("a directory record is marked", depotFiles[1].Directory);
        Probe.Check("a file carries offset and hash",
            depotFiles[0] is { Offset: 0, Size: 18689235, Hash: "abc" });
        Probe.Check("support files are flagged", depotFiles[2].Support);

        Probe.Group("Generation 1 blob URLs");
        var v1Endpoint = JsonSerializer.Deserialize<SecureLinkResponse>("""
            {"urls":[{"priority":0,"url_format":"https://cdn.invalid{path}?t={token}",
            "parameters":{"path":"/windows/1445945558","token":"abc"}}]}
            """, Json)!.Urls[0];

        Probe.Check("every v1 file comes from one main.bin",
            v1Endpoint.BlobUrl() == "https://cdn.invalid/windows/1445945558/main.bin?t=abc", v1Endpoint.BlobUrl());
        Probe.Check("asking twice does not accumulate the suffix",
            v1Endpoint.BlobUrl() == v1Endpoint.BlobUrl());

        Probe.Group("A stalled connection times out instead of hanging");
        // HttpClient.Timeout does not cover the body under ResponseHeadersRead, so without this a
        // CDN that sends headers then goes silent would hang the read forever.
        var stalling = new StallingStream();
        var buffer = new byte[16];
        var stallClock = System.Diagnostics.Stopwatch.StartNew();
        var timedOut = false;
        try { await stalling.WithStallTimeoutAsync(buffer, TimeSpan.FromMilliseconds(200), default); }
        catch (IOException) { timedOut = true; }
        stallClock.Stop();
        Probe.Check("a silent stream is abandoned", timedOut);
        Probe.Check($"it gives up near the timeout, not on HttpClient's ({stallClock.ElapsedMilliseconds} ms)",
            stallClock.ElapsedMilliseconds is > 150 and < 2000, stallClock.ElapsedMilliseconds.ToString());

        var live = new MemoryStream([1, 2, 3, 4, 5]);
        Probe.Check("a stream with data reads normally",
            await live.WithStallTimeoutAsync(new byte[16], TimeSpan.FromMilliseconds(200), default) == 5);

        using (var userCancel = new CancellationTokenSource())
        {
            await userCancel.CancelAsync();
            var wasUserCancel = false;
            try { await new StallingStream().WithStallTimeoutAsync(buffer, TimeSpan.FromSeconds(30), userCancel.Token); }
            catch (OperationCanceledException) { wasUserCancel = true; }
            catch (IOException) { }
            Probe.Check("the user's Ctrl+C is not mistaken for a stall", wasUserCancel);
        }

        Probe.Group("A truncated chunk reports the HTTP details");
        // A CDN that promises a Content-Length then delivers less must surface what happened,
        // not a bare "response ended prematurely" — that hid the cause for days.
        using (var truncating = new TruncatingServer(promised: 5000, sent: 1177))
        {
            using var http = new HttpClient();
            var content = new ContentClient(http, http, new RateLimiter(0));
            var linkJson = "{\"urls\":[{\"priority\":0,\"url_format\":\"" + truncating.Url + "\",\"parameters\":{}}]}";
            var truncEndpoint = JsonSerializer.Deserialize<SecureLinkResponse>(linkJson, Json)!.Urls[0];
            var chunk = new Chunk { CompressedMd5 = "abcdef0123456789", Size = 5000, CompressedSize = 5000 };

            var message = "";
            try { await content.DownloadChunkAsync(truncEndpoint, chunk, null, false, default); }
            catch (ChunkTransferException e) { message = e.Message; }
            Probe.Note(message);
            Probe.Check("the enriched error names the promised length", message.Contains("content-length=5000"), message);
            Probe.Check("and the bytes that actually arrived", message.Contains("got=1177"), message);
            Probe.Check("and the HTTP status", message.Contains("HTTP 200"), message);
        }

        Probe.Group("Rate limiter");
        // The bucket starts full: 1 MiB is free, the remaining 0.5 MiB costs half a second.
        var limiter = new RateLimiter(1024 * 1024);
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 24; i++) await limiter.ConsumeAsync(64 * 1024, default);
        stopwatch.Stop();
        Probe.Check($"1.5 MiB at 1 MiB/s costs about 0.5 s (took {stopwatch.Elapsed.TotalSeconds:F2} s)",
            stopwatch.Elapsed.TotalSeconds is > 0.3 and < 0.9, stopwatch.Elapsed.TotalSeconds.ToString("F2"));

        // A request larger than the per-second budget must not wait forever.
        var big = new RateLimiter(10_000_000);
        var completed = big.ConsumeAsync(20_000_000, default).AsTask().Wait(TimeSpan.FromSeconds(5));
        Probe.Check("a request bigger than the budget still completes", completed);

        var disabled = new RateLimiter(0);
        var fast = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) await disabled.ConsumeAsync(1 << 20, default);
        Probe.Check("a limit of 0 is a no-op", fast.Elapsed.TotalSeconds < 0.2);
    }
}
