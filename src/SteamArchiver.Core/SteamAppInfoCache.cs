using System.Collections.Concurrent;

namespace SteamArchiver.Core;

internal enum SteamAppTokenStatus { Denied, Omitted, Public, Granted }

/// <summary>Records token state, never token values. Only this PICS failure is retried.</summary>
internal sealed class SteamAppInfoAccessException(uint appId, SteamAppTokenStatus tokenStatus)
    : IOException($"Steam appinfo for {appId} requires an access token (PICS missing_token; token request: {Describe(tokenStatus)}).")
{
    public uint AppId { get; } = appId;
    public SteamAppTokenStatus TokenStatus { get; } = tokenStatus;

    internal static SteamAppTokenStatus Status(uint appId, IReadOnlyDictionary<uint, ulong> tokens, IEnumerable<uint> denied) =>
        denied.Contains(appId) ? SteamAppTokenStatus.Denied : !tokens.TryGetValue(appId, out var token)
            ? SteamAppTokenStatus.Omitted : token == 0 ? SteamAppTokenStatus.Public : SteamAppTokenStatus.Granted;

    internal static string Describe(SteamAppTokenStatus status) => status switch
    {
        SteamAppTokenStatus.Denied => "explicitly denied",
        SteamAppTokenStatus.Omitted => "not returned",
        SteamAppTokenStatus.Public => "public (zero token)",
        _ => "nonzero token returned",
    };
}

internal sealed class SteamAppInfoRecheckException(uint appId, SteamAppTokenStatus initialStatus,
    uint[] packageIds, Exception retryError) : IOException(
        $"Appinfo recheck for {appId} failed. Initial token request: {SteamAppInfoAccessException.Describe(initialStatus)}. "
        + $"Licensed packages: {(packageIds.Length == 0 ? "none in the current license list" : string.Join(", ", packageIds.Distinct().Order()))}. "
        + $"Individual retry: {retryError.Message}", retryError)
{
    public uint AppId { get; } = appId;
    public bool ExplicitlyDenied { get; } = initialStatus == SteamAppTokenStatus.Denied
        && retryError is SteamAppInfoAccessException retry && retry.AppId == appId && retry.TokenStatus == SteamAppTokenStatus.Denied;
}

internal sealed record SteamAppInfoResult(SteamApp? App = null, Exception? Error = null);

/// <summary>Cache one sync's PICS results; recheck missing-token responses once by AppID.</summary>
internal sealed class SteamAppInfoCache(
    Func<IReadOnlyCollection<uint>, CancellationToken, Task<IReadOnlyDictionary<uint, SteamAppInfoResult>>> fetch)
{
    private readonly ConcurrentDictionary<uint, SteamAppInfoResult> _entries = new();
    private IReadOnlyDictionary<uint, uint[]> _packages = new Dictionary<uint, uint[]>();

    public void Reset(IReadOnlyDictionary<uint, uint[]> packages)
    { _entries.Clear(); _packages = packages; }

    public async Task PreloadAsync(IReadOnlySet<uint> appIds, CancellationToken ct)
    {
        foreach (var batch in appIds.Where(id => !_entries.ContainsKey(id)).Chunk(100))
            await LoadAsync(batch, ct);
    }

    public async Task<SteamApp> GetAsync(uint appId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_entries.ContainsKey(appId)) await LoadAsync([appId], ct);
        var result = _entries[appId];
        return result.App ?? throw new IOException($"Cannot read Steam app {appId}: {result.Error!.Message}", result.Error);
    }

    private static SteamAppInfoResult Result(IReadOnlyDictionary<uint, SteamAppInfoResult> results, uint id)
    {
        if (!results.TryGetValue(id, out var result)) return new(Error: new IOException($"Steam did not return appinfo for {id}."));
        if (result.Error is OperationCanceledException cancelled) throw cancelled;
        if (result.App?.AppId == id && result.Error is null || result.App is null && result.Error is not null) return result;
        return new(Error: new InvalidDataException($"Invalid Steam appinfo result for {id}."));
    }

    private async Task LoadAsync(uint[] ids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = await fetch(ids, ct);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var result = Result(results, id);
            if (result.Error is SteamAppInfoAccessException first)
            {
                try { result = Result(await fetch([id], ct), id); }
                catch (Exception e) when (e is not OperationCanceledException) { result = new(Error: e); }
                if (result.Error is not null)
                    result = new(Error: new SteamAppInfoRecheckException(id, first.TokenStatus, _packages.GetValueOrDefault(id, []), result.Error));
            }
            ct.ThrowIfCancellationRequested();
            _entries[id] = result;
        }
    }
}
