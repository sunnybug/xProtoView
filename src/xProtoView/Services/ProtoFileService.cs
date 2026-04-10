namespace xProtoView.Services;

public sealed class ProtoFileService
{
    public List<string> CollectProtoFiles(IEnumerable<ProtoPathEntry> entries)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries.Where(x => !string.IsNullOrWhiteSpace(x.Path)))
        {
            var raw = entry.Path.Trim();
            if (Directory.Exists(raw))
            {
                var dir = Path.GetFullPath(raw);
                var searchOption = entry.IncludeSubDirectories
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;
                foreach (var path in Directory.EnumerateFiles(dir, "*.proto", searchOption))
                {
                    var full = Path.GetFullPath(path);
                    if (seen.Add(full)) output.Add(full);
                }
                continue;
            }

            if (File.Exists(raw))
            {
                if (!string.Equals(Path.GetExtension(raw), ".proto", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"不是 .proto 文件：{raw}");
                }
                var full = Path.GetFullPath(raw);
                if (seen.Add(full)) output.Add(full);
                continue;
            }

            throw new InvalidOperationException($"路径不存在：{raw}");
        }

        return output;
    }

    public List<string> GetIncludeDirs(IEnumerable<ProtoPathEntry> configuredEntries, IEnumerable<string> protoFiles)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in configuredEntries.Select(x => x.Path.Trim()).Where(x => x.Length > 0))
        {
            if (Directory.Exists(raw))
            {
                dirs.Add(Path.GetFullPath(raw));
            }
            else if (File.Exists(raw))
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(raw));
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    dirs.Add(parent);
                }
            }
        }

        foreach (var file in protoFiles)
        {
            var parent = Path.GetDirectoryName(file);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                dirs.Add(parent);
            }
        }

        return dirs.ToList();
    }
}
