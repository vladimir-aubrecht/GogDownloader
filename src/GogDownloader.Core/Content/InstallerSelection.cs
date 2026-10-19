namespace GogDownloader.Core.Content;

/// <summary>The same language choice must drive download selection and backup identity.</summary>
internal static class InstallerSelection
{
    public static T? Select<T>(IReadOnlyList<T> installers, string language, Func<T, string> languageOf) where T : class
    {
        if (installers.Count <= 1) return installers.FirstOrDefault();
        var requested = GogLanguage.Parse(language);
        if (requested is not null && installers.FirstOrDefault(i => requested.Matches(languageOf(i))) is { } exact)
            return exact;
        return installers.FirstOrDefault(i => languageOf(i).Equals("en", StringComparison.OrdinalIgnoreCase))
            ?? installers.FirstOrDefault(i => GogLanguage.Parse("en")!.Matches(languageOf(i)));
    }

    public static string BuildId(string language, string? version, long size) =>
        $"installer-{(GogLanguage.Parse(language)?.Code ?? language).ToLowerInvariant()}-{version ?? "0"}-{size}";
}
