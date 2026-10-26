using GameArchiver.Core;
namespace GogArchiver.Core.Download;

internal static class LinkResolver
{
    public static DepotPlan Resolve(DepotPlan plan, GamePlatform platform)
    {
        var layout = ContentLinks.Resolve(new(plan.Files.Select(f => new ContentEntry(f.RelativePath, f.Size)).ToList(),
            plan.Directories, plan.Links.Select(l => new ContentLink(l.Path, l.Target)).ToList()), platform == GamePlatform.Windows);
        var directoryModes = new Dictionary<string, UnixFileMode>(plan.DirectoryModes);
        foreach (var (destination, target) in layout.DirectoryTargets)
            directoryModes[destination] = plan.DirectoryModes.GetValueOrDefault(target, UnixArchivePermissions.Executable);
        return plan with { Links = layout.Links.Select(l => new PlannedLink(l.Path, l.Target)).ToList(),
            Directories = layout.Directories, DirectoryModes = directoryModes, LinkBytes = layout.LinkBytes };
    }
}
