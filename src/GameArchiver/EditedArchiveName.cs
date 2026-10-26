using GameArchiver.Core;

namespace GameArchiver;

internal static class EditedArchiveName
{
    public static string Create(string original) => UniqueArchiveName.Create(original, "local");
}
