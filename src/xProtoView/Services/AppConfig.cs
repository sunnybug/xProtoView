namespace xProtoView.Services;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public ProtoConfig Proto { get; set; } = new();

    public static AppConfig Default() => new();
}

public sealed class ProtoConfig
{
    // 兼容旧版配置，保留 paths 字段
    public List<string> Paths { get; set; } = [];
    public List<ProtoPathEntry> PathEntries { get; set; } = [];
}

public sealed class ProtoPathEntry
{
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public bool IncludeSubDirectories { get; set; } = true;

    public string DisplayText =>
        IsDirectory
            ? $"{Path} [目录，包含子目录：{(IncludeSubDirectories ? "是" : "否")}]"
            : $"{Path} [.proto 文件]";

    public static ProtoPathEntry FromLegacyPath(string path)
    {
        var trimmed = path.Trim();
        var isProtoFile = string.Equals(
            System.IO.Path.GetExtension(trimmed),
            ".proto",
            StringComparison.OrdinalIgnoreCase);
        return new ProtoPathEntry
        {
            Path = trimmed,
            IsDirectory = !isProtoFile,
            IncludeSubDirectories = !isProtoFile
        };
    }
}
