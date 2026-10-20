using System.Diagnostics;
using System.Text.Json;

namespace GameArchiver.Core;

/// <summary>Serializes directory allocation across providers and processes sharing a library.</summary>
public static class ArchiveReservation
{
    public static async Task<FileStream> AcquireAsync(string root, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var path = ArchivePaths.Within(root, ".gamearchive.reserve.lock");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
            { await Task.Delay(20, ct); }
        }
    }

    /// <summary>Exclusively creates ownership metadata; never replaces an existing owner.</summary>
    public static async Task<bool> CreateOwnerAsync<T>(string path, T owner, JsonSerializerOptions options,
        CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(owner, options);
        ct.ThrowIfCancellationRequested();
        FileStream stream;
        try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException) when (File.Exists(path)) { return false; }
        await using (stream)
        {
            // The root reservation prevents other allocators from reading a partial claim.
            // Finish this small write even if cancellation arrives after the exclusive create.
            // An I/O failure leaves an invalid claim that fails closed on the next attempt.
            await stream.WriteAsync(bytes, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
        }
        return true;
    }
}
