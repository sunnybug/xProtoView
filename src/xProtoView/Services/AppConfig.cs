namespace xProtoView.Services;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public ProtoConfig Proto { get; set; } = new();
    public UiConfig Ui { get; set; } = new();

    public static AppConfig Default() => new();
}

public sealed class ProtoConfig
{
    // 兼容旧版配置，保留 paths 字段
    public List<string> Paths { get; set; } = [];
    public List<ProtoPathEntry> PathEntries { get; set; } = [];
}

public sealed class UiConfig
{
    // 主窗口布局。
    public WindowLayoutConfig MainWindow { get; set; } = new();
    // 设置窗口布局。
    public WindowLayoutConfig SettingsDialog { get; set; } = new();
    // YAML 窗口布局。
    public WindowLayoutConfig YamlViewer { get; set; } = new();
    // YAML 分栏位置。
    public int? YamlViewerSplitterDistance { get; set; }
}

public sealed class WindowLayoutConfig
{
    public int? Left { get; set; }
    public int? Top { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool IsMaximized { get; set; }
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
