using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace xProtoView.Services;

public sealed class ConfigService
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public string GetConfigDir()
    {
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var exeDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        return Path.Combine(exeDir, "config");
    }

    public string GetConfigPath() => Path.Combine(GetConfigDir(), "config.yaml");

    public AppConfig ReadConfig()
    {
        var path = GetConfigPath();
        if (!File.Exists(path))
        {
            return AppConfig.Default();
        }

        var text = File.ReadAllText(path);
        var cfg = _deserializer.Deserialize<AppConfig>(text) ?? AppConfig.Default();
        cfg.Proto ??= new ProtoConfig();
        cfg.Ui ??= new UiConfig();
        cfg.Ui.MainWindow ??= new WindowLayoutConfig();
        cfg.Ui.SettingsDialog ??= new WindowLayoutConfig();
        cfg.Ui.YamlViewer ??= new WindowLayoutConfig();
        if (cfg.Ui.YamlViewerSplitterDistance <= 0)
        {
            cfg.Ui.YamlViewerSplitterDistance = null;
        }
        cfg.Proto.PathEntries = NormalizePathEntries(cfg.Proto.PathEntries, cfg.Proto.Paths);
        cfg.Proto.Paths = cfg.Proto.PathEntries
            .Select(x => x.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cfg.Version <= 0) cfg.Version = 1;
        return cfg;
    }

    public void WriteConfig(AppConfig config)
    {
        config.Proto ??= new ProtoConfig();
        config.Ui ??= new UiConfig();
        config.Ui.MainWindow ??= new WindowLayoutConfig();
        config.Ui.SettingsDialog ??= new WindowLayoutConfig();
        config.Ui.YamlViewer ??= new WindowLayoutConfig();
        if (config.Ui.YamlViewerSplitterDistance <= 0)
        {
            config.Ui.YamlViewerSplitterDistance = null;
        }
        config.Proto.PathEntries = NormalizePathEntries(config.Proto.PathEntries, config.Proto.Paths);
        config.Proto.Paths = config.Proto.PathEntries
            .Select(x => x.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Directory.CreateDirectory(GetConfigDir());
        var path = GetConfigPath();
        var text = _serializer.Serialize(config);
        File.WriteAllText(path, text);
    }

    private static List<ProtoPathEntry> NormalizePathEntries(
        IEnumerable<ProtoPathEntry>? entries,
        IEnumerable<string>? legacyPaths)
    {
        var source = (entries ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => new ProtoPathEntry
            {
                Path = x.Path.Trim(),
                IsDirectory = x.IsDirectory,
                IncludeSubDirectories = x.IsDirectory && x.IncludeSubDirectories
            })
            .ToList();

        if (source.Count == 0)
        {
            source = (legacyPaths ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(ProtoPathEntry.FromLegacyPath)
                .ToList();
        }

        return source
            .DistinctBy(x => $"{x.Path}|{x.IsDirectory}|{x.IncludeSubDirectories}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
