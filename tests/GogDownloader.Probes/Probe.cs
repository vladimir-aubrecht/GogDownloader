namespace GogDownloader.Probes;

/// <summary>
/// The whole test harness. Checks print one line each and the process exits non-zero when any
/// of them fails, which is everything `scripts/check.sh` needs.
/// </summary>
public static class Probe
{
    private static int _failures;
    private static string _group = "";

    public static void Group(string name)
    {
        _group = name;
        Console.WriteLine();
        Console.WriteLine($"── {name}");
    }

    public static void Check(string what, bool passed, string? actual = null)
    {
        if (passed)
        {
            Console.WriteLine($"  ok   {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL {what}" + (actual is null ? "" : $"  → {actual}"));
    }

    public static void Note(string line) => Console.WriteLine($"       {line}");

    public static int Report()
    {
        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "All probes passed." : $"{_failures} probe(s) FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>A scratch directory on a real disk — temp is a small tmpfs on Linux.</summary>
    public static string ScratchDirectory()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gogdownloader", "probes", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}
