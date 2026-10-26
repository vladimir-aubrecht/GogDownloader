namespace GameArchiver.Core;

/// <summary>ZIP Unix modes independent of the OS and filesystem used for staging.</summary>
public static class UnixArchivePermissions
{
    public const ushort VersionMadeBy = (3 << 8) | 45;
    public const UnixFileMode Regular = (UnixFileMode)0x1a4; // 0644
    public const UnixFileMode Executable = (UnixFileMode)0x1ed; // 0755; also directories
    public static uint Attributes(UnixFileMode mode, bool directory = false) =>
        ((uint)((int)mode & 0x1ff) | (directory ? 0x4000u : 0x8000u)) << 16 | (directory ? 0x10u : 0);
}
