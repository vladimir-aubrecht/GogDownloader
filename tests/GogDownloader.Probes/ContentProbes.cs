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
