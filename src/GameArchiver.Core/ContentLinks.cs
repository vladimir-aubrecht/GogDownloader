namespace GameArchiver.Core;

public sealed record ContentEntry(string RelativePath, long Size);
public sealed record ContentLink(string Path, string Target);
public sealed record ContentLayout(List<ContentEntry> Files, List<string> Directories, List<ContentLink> Links)
{
    public long LinkBytes { get; init; }
}


/// <summary>Resolve links against the manifest tree before copying or downloading any content.</summary>
public static class ContentLinks
{
    private sealed class Node(string path, StringComparer comparer)
    {
        public string Path { get; } = path;
        public ContentEntry? File { get; set; }
        public string? Target { get; set; }
        public Dictionary<string, Node> Children { get; } = new(comparer);
    }

    public static ContentLayout Resolve(ContentLayout plan, bool ignoreCase)
    {
        if (plan.Links.Count == 0) return plan;
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var root = new Node("", comparer);
        Node Add(string path)
        {
            var node = root;
            foreach (var segment in Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node.File is not null)
                    throw new InvalidOperationException($"A manifest file is also a directory: {node.Path}");
                if (!node.Children.TryGetValue(segment, out var child))
                    node.Children[segment] = child = new Node(node.Path.Length == 0 ? segment : $"{node.Path}/{segment}", comparer);
                node = child;
            }
            return node;
        }

        foreach (var directory in plan.Directories) Add(directory);
        foreach (var file in plan.Files) Add(file.RelativePath).File = file;
        foreach (var link in plan.Links)
        {
            var node = Add(link.Path);
            node.File = null;
            node.Target = Normalize(link.Target);
        }

        Node Follow(Node node, HashSet<Node> visiting)
        {
            if (node.Target is null) return node;
            if (!visiting.Add(node))
                throw new InvalidOperationException($"Cyclic manifest link: {node.Path}");
            var resolved = Find(node.Target, visiting);
            visiting.Remove(node);
            return resolved;
        }

        Node Find(string path, HashSet<Node> visiting)
        {
            var node = root;
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                node = Follow(node, visiting);
                if (!node.Children.TryGetValue(segment, out var child))
                    throw new InvalidOperationException($"Missing manifest link target: {path}");
                node = child;
            }
            return Follow(node, visiting);
        }

        var copies = new Dictionary<string, (ContentLink Link, long Size)>(comparer);
        var directories = plan.Directories.ToHashSet(comparer);
        void Expand(Node node, string destination, HashSet<Node> visiting)
        {
            node = Follow(node, []);
            if (node.File is { } file)
            {
                copies[destination] = (new ContentLink(destination, file.RelativePath), file.Size);
                return;
            }
            if (!visiting.Add(node))
                throw new InvalidOperationException($"Cyclic manifest directory link at {destination}");
            directories.Add(destination);
            foreach (var (name, child) in node.Children)
                Expand(child, $"{destination}/{name}", visiting);
            visiting.Remove(node);
        }

        foreach (var link in plan.Links)
            Expand(Find(Normalize(link.Path), []), Normalize(link.Path), []);
        return plan with
        {
            Links = copies.Values.Select(c => c.Link).ToList(),
            Directories = directories.ToList(),
            LinkBytes = copies.Values.Sum(c => c.Size),
        };
    }

    private static string Normalize(string path)
    {
        var relative = path.Replace('\\', '/').Trim('/');
        if (relative.Split('/').Any(segment => segment is ".." or "."))
            throw new InvalidOperationException($"Suspicious path in manifest link: {path}");
        return relative;
    }
}
