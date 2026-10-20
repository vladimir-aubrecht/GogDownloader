using GogArchiver.Core.Library;
using GameArchiver.Core;
namespace GogArchiver.Core.Download;

/// <summary>Compatibility facade for the shared GameVault archive naming rules.</summary>
public static class GameVaultName
{
    public static string For(LibraryEntry game, GamePlatform platform, string? versionName) =>
        ArchiveName.For(new GameInfo(new("gog", game.Id.ToString()), game.Title, game.Slug)
            { EarlyAccess = game.EarlyAccess }, (GameOs)platform, versionName);
    internal static string Title(string title) => ArchiveName.Title(title);
    internal static string? Version(string? versionName) => ArchiveName.Version(versionName);
    internal static string? GameType(GamePlatform platform) => ArchiveName.GameType((GameOs)platform);
}
