using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace xProtoView.Services;

public sealed record UpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTag,
    string AssetName,
    string DownloadUrl,
    string ReleaseUrl);

public sealed class UpdateService
{
    public const string ProjectName = "xProtoView";
    public const string RepositoryUrl = "https://github.com/sunnybug/xProtoView";
    public const string Author = "sunnybug";

    private const string LatestReleaseApi = "https://api.github.com/repos/sunnybug/xProtoView/releases/latest";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;

    public UpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub API 要求携带 User-Agent。
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(ProjectName, "1.0"));
        }
        if (_httpClient.DefaultRequestHeaders.Accept.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
    }

    public static string GetCurrentVersion()
    {
        var asm = typeof(UpdateService).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Trim();
        }

        var nameVersion = asm.GetName().Version;
        if (nameVersion is not null)
        {
            return FormatVersion(nameVersion);
        }

        return "0.0.0";
    }

    // 启动时检查是否存在可自动安装的新版本。
    public async Task<UpdateInfo?> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var current = ParseVersion(currentVersion)
            ?? throw new InvalidOperationException($"当前版本号格式不受支持：{currentVersion}");

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // 仓库暂无 Release 时视为无更新。
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorDetailAsync(response, cancellationToken);
            throw new InvalidOperationException($"检查更新失败：GitHub 返回 {(int)response.StatusCode} {response.ReasonPhrase}。{detail}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var release = JsonSerializer.Deserialize<GithubReleaseDto>(json, JsonOptions)
            ?? throw new InvalidOperationException("检查更新失败：GitHub 返回了空发布数据。");
        if (string.IsNullOrWhiteSpace(release.TagName))
        {
            throw new InvalidOperationException("检查更新失败：发布数据缺少 tag_name。");
        }

        var latest = ParseVersion(release.TagName)
            ?? throw new InvalidOperationException($"检查更新失败：发布标签格式不受支持：{release.TagName}");
        if (latest <= current || release.Draft || release.Prerelease)
        {
            return null;
        }

        // 仅接受 zip 包，确保可自动替换安装目录。
        var asset = (release.Assets ?? [])
            .FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.BrowserDownloadUrl) &&
                x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.State, "uploaded", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            return null;
        }

        var releaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? RepositoryUrl : release.HtmlUrl.Trim();
        return new UpdateInfo(
            FormatVersion(current),
            FormatVersion(latest),
            release.TagName.Trim(),
            asset.Name.Trim(),
            asset.BrowserDownloadUrl.Trim(),
            releaseUrl);
    }

    // 用户确认后下载并拉起临时更新脚本。
    public async Task StartUpdateAsync(UpdateInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            throw new InvalidOperationException($"启动更新失败：无法定位当前可执行文件：{exePath}");
        }

        var targetDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            throw new InvalidOperationException($"启动更新失败：无法定位安装目录：{targetDir}");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"{ProjectName}.update.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var zipName = SanitizeAssetName(info.AssetName);
        var zipPath = Path.Combine(tempRoot, zipName);
        await DownloadUpdatePackageAsync(info.DownloadUrl, zipPath, cancellationToken);

        var extractDir = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(extractDir);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, extractDir);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"启动更新失败：解压更新包失败：{ex.Message}", ex);
        }

        var payloadDir = FindPayloadDirectory(extractDir);
        if (payloadDir is null)
        {
            throw new InvalidOperationException("启动更新失败：更新包内未找到 xProtoView.exe。");
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"{ProjectName}.updater.{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, BuildUpdaterScriptContent(), Encoding.UTF8);

        StartUpdaterProcess(scriptPath, payloadDir, targetDir, exePath, tempRoot, Environment.ProcessId);
    }

    private async Task DownloadUpdatePackageAsync(string downloadUrl, string zipPath, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"启动更新失败：下载地址无效：{downloadUrl}");
        }

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorDetailAsync(response, cancellationToken);
            throw new InvalidOperationException($"启动更新失败：下载更新包失败，HTTP {(int)response.StatusCode} {response.ReasonPhrase}。{detail}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file, cancellationToken);
    }

    // 兼容 zip 根目录嵌套场景，自动定位包含主程序的目录。
    private static string? FindPayloadDirectory(string extractDir)
    {
        var exeCandidates = Directory
            .GetFiles(extractDir, "xProtoView.exe", SearchOption.AllDirectories)
            .OrderBy(x => x.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .ToList();
        if (exeCandidates.Count == 0)
        {
            return null;
        }
        return Path.GetDirectoryName(exeCandidates[0]);
    }

    private static void StartUpdaterProcess(
        string scriptPath,
        string sourceDir,
        string targetDir,
        string exePath,
        string tempRoot,
        int waitPid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments =
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -SourceDir \"{sourceDir}\" -TargetDir \"{targetDir}\" -ExePath \"{exePath}\" -TempRoot \"{tempRoot}\" -WaitPid {waitPid}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException("启动更新失败：无法启动更新脚本进程。");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启动更新失败：拉起更新脚本失败：{ex.Message}", ex);
        }
    }

    private static string BuildUpdaterScriptContent()
    {
        return """
param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][string]$TargetDir,
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$TempRoot,
    [Parameter(Mandatory = $true)][int]$WaitPid
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms

try {
    # 等待旧进程退出，避免覆盖运行中的文件。
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue)) {
            break
        }
        Start-Sleep -Milliseconds 400
    }
    if (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) {
        throw "等待旧进程退出超时：PID=$WaitPid"
    }

    # 校验路径合法性，避免误操作目录。
    if (-not (Test-Path -LiteralPath $SourceDir -PathType Container)) {
        throw "更新源目录不存在：$SourceDir"
    }
    if (-not (Test-Path -LiteralPath $TargetDir -PathType Container)) {
        throw "更新目标目录不存在：$TargetDir"
    }
    $resolvedTarget = (Resolve-Path -LiteralPath $TargetDir).Path
    if ($resolvedTarget.Length -lt 4) {
        throw "更新目标目录异常：$resolvedTarget"
    }

    $sourceExe = Join-Path $SourceDir "xProtoView.exe"
    if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
        throw "更新包缺少 xProtoView.exe：$SourceDir"
    }

    # 保留 config 目录，其余文件先清理再覆盖。
    Get-ChildItem -LiteralPath $resolvedTarget -Force | Where-Object { $_.Name -ne "config" } | Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $SourceDir -Force | Where-Object { $_.Name -ne "config" } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $resolvedTarget $_.Name) -Recurse -Force
    }

    # 临时目录清理失败不影响主流程。
    try {
        if (Test-Path -LiteralPath $TempRoot -PathType Container) {
            Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction Stop
        }
    }
    catch {
    }

    Start-Process -FilePath $ExePath -WorkingDirectory $resolvedTarget | Out-Null
}
catch {
    [System.Windows.Forms.MessageBox]::Show(
        "更新失败：$($_.Exception.Message)",
        "xProtoView 更新失败",
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    ) | Out-Null
    exit 1
}
""";
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "响应体为空。";
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("message", out var messageElement))
            {
                var message = messageElement.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return $"详情：{message.Trim()}";
                }
            }
        }
        catch
        {
        }

        var text = raw.Trim();
        if (text.Length > 120)
        {
            text = text[..120] + "...";
        }
        return $"详情：{text}";
    }

    private static string SanitizeAssetName(string? assetName)
    {
        var raw = string.IsNullOrWhiteSpace(assetName) ? "update.zip" : assetName.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            raw = raw.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(raw) ? "update.zip" : raw;
    }

    private static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        var cutIndex = text.IndexOfAny(['-', '+', ' ']);
        if (cutIndex >= 0)
        {
            text = text[..cutIndex];
        }

        var segments = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 2 or > 4)
        {
            return null;
        }

        var numbers = new List<int>(4);
        foreach (var segment in segments)
        {
            if (!int.TryParse(segment, out var value) || value < 0)
            {
                return null;
            }
            numbers.Add(value);
        }
        while (numbers.Count < 4)
        {
            numbers.Add(0);
        }

        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    private static string FormatVersion(Version version)
    {
        var build = version.Build < 0 ? 0 : version.Build;
        var revision = version.Revision < 0 ? 0 : version.Revision;
        if (revision > 0)
        {
            return $"{version.Major}.{version.Minor}.{build}.{revision}";
        }
        return $"{version.Major}.{version.Minor}.{build}";
    }

    private sealed class GithubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GithubAssetDto> Assets { get; set; } = [];
    }

    private sealed class GithubAssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
