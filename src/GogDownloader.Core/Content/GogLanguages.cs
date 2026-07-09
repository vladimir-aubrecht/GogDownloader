namespace GogDownloader.Core.Content;

/// <summary>
/// A GOG language. Depots are tagged with codes like `en-US`, never `en`, so a plain string
/// comparison against what the user typed silently matches nothing but the language-neutral
/// depots — a game would download as a near-empty archive.
///
/// Matching mirrors heroic-gogdl `languages.py`: a depot's tag matches when it equals the
/// code, the English name, or one of the deprecated aliases, ignoring case.
/// </summary>
public sealed record GogLanguage(string Code, string Name, string[] Aliases)
{
    public bool Matches(string tag) =>
        string.Equals(tag, Code, StringComparison.OrdinalIgnoreCase)
        || string.Equals(tag, Name, StringComparison.OrdinalIgnoreCase)
        || Aliases.Any(a => string.Equals(tag, a, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves what the user asked for, e.g. `en` to `en-US`. Null when unknown.</summary>
    public static GogLanguage? Parse(string requested) =>
        All.FirstOrDefault(language => language.Matches(requested));

    /// <summary>
    /// Verified against heroic-gogdl `gogdl/languages.py`. Regenerate with
    /// `scripts/vendor-ref.sh gog`; do not hand-edit.
    /// </summary>
    public static readonly GogLanguage[] All =
    [
        new("af-ZA", "Afrikaans", []),
        new("ar", "Arabic", []),
        new("az-AZ", "Azeri", []),
        new("be-BY", "Belarusian", ["be"]),
        new("bn-BD", "Bengali", ["bn_BD"]),
        new("bg-BG", "Bulgarian", ["bg", "bl"]),
        new("bs-BA", "Bosnian", []),
        new("ca-ES", "Catalan", ["ca"]),
        new("cs-CZ", "Czech", ["cz"]),
        new("cy-GB", "Welsh", []),
        new("da-DK", "Danish", ["da"]),
        new("de-DE", "German", ["de"]),
        new("dv-MV", "Divehi", []),
        new("el-GR", "Greek", ["gk", "el-GK"]),
        new("en-GB", "British English", ["en_GB"]),
        new("en-US", "English", ["en"]),
        new("es-ES", "Spanish", ["es"]),
        new("es-MX", "Latin American Spanish", ["es_mx"]),
        new("et-EE", "Estonian", ["et"]),
        new("eu-ES", "Basque", []),
        new("fa-IR", "Persian", ["fa"]),
        new("fi-FI", "Finnish", ["fi"]),
        new("fo-FO", "Faroese", []),
        new("fr-FR", "French", ["fr"]),
        new("gl-ES", "Galician", []),
        new("gu-IN", "Gujarati", ["gu"]),
        new("he-IL", "Hebrew", ["he"]),
        new("hi-IN", "Hindi", ["hi"]),
        new("hr-HR", "Croatian", []),
        new("hu-HU", "Hungarian", ["hu"]),
        new("hy-AM", "Armenian", []),
        new("id-ID", "Indonesian", []),
        new("is-IS", "Icelandic", ["is"]),
        new("it-IT", "Italian", ["it"]),
        new("ja-JP", "Japanese", ["jp"]),
        new("jv-ID", "Javanese", ["jv"]),
        new("ka-GE", "Georgian", []),
        new("kk-KZ", "Kazakh", []),
        new("kn-IN", "Kannada", []),
        new("ko-KR", "Korean", ["ko"]),
        new("kok-IN", "Konkani", []),
        new("ky-KG", "Kyrgyz", []),
        new("la", "Latin", []),
        new("lt-LT", "Lithuanian", []),
        new("lv-LV", "Latvian", []),
        new("ml-IN", "Malayalam", ["ml"]),
        new("mi-NZ", "Maori", []),
        new("mk-MK", "Macedonian", []),
        new("mn-MN", "Mongolian", []),
        new("mr-IN", "Marathi", ["mr"]),
        new("ms-MY", "Malay", []),
        new("mt-MT", "Maltese", []),
        new("nb-NO", "Norwegian", ["no"]),
        new("nl-NL", "Dutch", ["nl"]),
        new("ns-ZA", "Northern Sotho", []),
        new("pa-IN", "Punjabi", []),
        new("pl-PL", "Polish", ["pl"]),
        new("ps-AR", "Pashto", []),
        new("pt-BR", "Portuguese (Brazilian)", ["br"]),
        new("pt-PT", "Portuguese", ["pt"]),
        new("ro-RO", "Romanian", ["ro"]),
        new("ru-RU", "Russian", ["ru"]),
        new("sa-IN", "Sanskrit", []),
        new("sk-SK", "Slovak", ["sk"]),
        new("sl-SI", "Slovenian", []),
        new("sq-AL", "Albanian", []),
        new("sr-SP", "Serbian", ["sb"]),
        new("sv-SE", "Swedish", ["sv"]),
        new("sw-KE", "Kiswahili", []),
        new("ta-IN", "Tamil", ["ta_IN"]),
        new("te-IN", "Telugu", ["te"]),
        new("th-TH", "Thai", ["th"]),
        new("tl-PH", "Tagalog", []),
        new("tn-ZA", "Setswana", []),
        new("tr-TR", "Turkish", ["tr"]),
        new("tt-RU", "Tatar", []),
        new("uk-UA", "Ukrainian", ["uk"]),
        new("ur-PK", "Urdu", ["ur_PK"]),
        new("uz-UZ", "Uzbek", []),
        new("vi-VN", "Vietnamese", ["vi"]),
        new("xh-ZA", "isiXhosa", []),
        new("zh-Hans", "Chinese (Simplified)", ["zh_Hans", "zh", "cn"]),
        new("zh-Hant", "Chinese (Traditional)", ["zh_Hant"]),
        new("zu-ZA", "isiZulu", []),
    ];
}
