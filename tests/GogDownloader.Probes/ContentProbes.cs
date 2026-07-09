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
