using System.Diagnostics;
using System.Text.RegularExpressions;

namespace xProtoView.Services;

public sealed class ProtocDecoder
{
    public sealed record ProtoMessageScope(string TypeName, string ProtoFile, string IncludeDir);

    public sealed record ProtoInputPreparation(string ProtoText, bool ConvertedFromJson);

    private static readonly Regex PackageRegex = new(@"^\s*package\s+([A-Za-z_][\w.]*)\s*;", RegexOptions.Compiled);
    private static readonly Regex MessageRegex = new(@"^\s*message\s+([A-Za-z_]\w*)\s*\{?", RegexOptions.Compiled);

    // 解析 message 对应的 proto 文件与 include 目录。
    public ProtoMessageScope ResolveMessageScope(string typeName, IReadOnlyList<string> protoFiles)
    {
        var normalizedTypeName = (typeName ?? string.Empty).Trim();
        if (normalizedTypeName.Length == 0)
        {
            throw new InvalidOperationException("Message 类型不能为空。");
        }

        var messageFileMap = BuildMessageTypeFileMap(protoFiles);
        if (!messageFileMap.TryGetValue(normalizedTypeName, out var protoFile))
        {
            throw new InvalidOperationException($"无法定位 Message 类型对应的 proto 文件：{normalizedTypeName}");
        }

        var includeDir = ResolveIncludeDir(protoFile, normalizedTypeName);

        return new ProtoMessageScope(normalizedTypeName, protoFile, includeDir);
    }

    // 将二进制 proto 解码为 proto 文本。
    public string DecodeToProtoText(byte[] payload, ProtoMessageScope scope)
    {
        var protoc = ResolveProtocPath();
        if (!File.Exists(protoc))
        {
            throw new InvalidOperationException($"未找到 protoc：{protoc}");
        }
        var includeDir = ResolveIncludeDir(scope.ProtoFile, scope.TypeName);

        var args = new List<string>
        {
            $"--decode={scope.TypeName}",
            $"-I\"{includeDir}\"",
            $"\"{scope.ProtoFile}\""
        };
        var output = ExecuteProtoc(protoc, string.Join(" ", args), payload, out var stderr);
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "protoc 未输出结果。" : stderr);
        }
        return output;
    }

    // 编码前按需将 JSON 转为 proto 文本。
    public ProtoInputPreparation PrepareProtoTextForEncode(string protoOrJsonText, ProtoMessageScope scope)
    {
        var raw = (protoOrJsonText ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            throw new InvalidOperationException("Proto 文本为空。");
        }

        if (!JsonProtoTextConverter.LooksLikeJson(raw))
        {
            return new ProtoInputPreparation(raw, false);
        }

        var convertedProtoText = JsonProtoTextConverter.Convert(raw, scope, ResolveProtocPath);
        return new ProtoInputPreparation(convertedProtoText, true);
    }

    // 将 proto 文本编码为二进制 proto。
    public byte[] EncodeFromProtoText(string protoText, ProtoMessageScope scope)
    {
        var raw = (protoText ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            throw new InvalidOperationException("Proto 文本为空。");
        }

        var protoc = ResolveProtocPath();
        if (!File.Exists(protoc))
        {
            throw new InvalidOperationException($"未找到 protoc：{protoc}");
        }
        var includeDir = ResolveIncludeDir(scope.ProtoFile, scope.TypeName);

        var args = new List<string>
        {
            $"--encode={scope.TypeName}",
            $"-I\"{includeDir}\"",
            $"\"{scope.ProtoFile}\""
        };
        var output = ExecuteProtocBinary(protoc, string.Join(" ", args), raw, out var stderr);
        if (output.Length == 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "protoc 未输出二进制结果。" : stderr);
        }
        return output;
    }

    public static List<string> ExtractMessageTypes(IEnumerable<string> protoFiles)
    {
        return BuildMessageTypeFileMap(protoFiles)
            .Keys
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    // 建立 message 到 proto 文件路径的映射。
    private static Dictionary<string, string> BuildMessageTypeFileMap(IEnumerable<string> protoFiles)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawPath in protoFiles)
        {
            var file = Path.GetFullPath(rawPath);
            if (!File.Exists(file))
            {
                throw new InvalidOperationException($"proto 文件不存在：{file}");
            }

            var packageName = string.Empty;
            var stack = new Stack<string>();
            var lines = File.ReadAllLines(file);

            foreach (var raw in lines)
            {
                var line = raw.Split("//")[0];
                if (line.TrimStart().StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                var packageMatch = PackageRegex.Match(line);
                if (packageMatch.Success)
                {
                    packageName = packageMatch.Groups[1].Value.Trim();
                }

                var msgMatch = MessageRegex.Match(line);
                if (msgMatch.Success)
                {
                    var name = msgMatch.Groups[1].Value.Trim();
                    var fullName = string.Join(".", stack.Reverse().Append(name));
                    if (!string.IsNullOrWhiteSpace(packageName))
                    {
                        fullName = $"{packageName}.{fullName}";
                    }

                    if (map.TryGetValue(fullName, out var existing) &&
                        !string.Equals(existing, file, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Message 类型重复定义：{fullName}（{existing} / {file}）");
                    }

                    map[fullName] = file;
                    stack.Push(name);
                }

                var openCount = line.Count(c => c == '{');
                var closeCount = line.Count(c => c == '}');
                var diff = closeCount - Math.Max(0, openCount - (msgMatch.Success ? 1 : 0));
                for (var i = 0; i < diff && stack.Count > 0; i++)
                {
                    stack.Pop();
                }
            }
        }

        return map;
    }

    private static string ExecuteProtoc(string protocPath, string arguments, byte[]? input, out string stderr)
    {
        const int TimeoutMs = 180000;
        using var process = new Process();
        process.StartInfo.FileName = protocPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardInput = input is not null;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        process.Start();
        if (input is not null)
        {
            process.StandardInput.BaseStream.Write(input, 0, input.Length);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            throw new TimeoutException($"protoc 执行超时（{TimeoutMs / 1000}s），参数：{arguments}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"protoc 退出码: {process.ExitCode}" : stderr.Trim());
        }
        return stdout;
    }

    // 执行 protoc 并读取二进制 stdout。
    private static byte[] ExecuteProtocBinary(string protocPath, string arguments, string input, out string stderr)
    {
        const int TimeoutMs = 180000;
        using var process = new Process();
        process.StartInfo.FileName = protocPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        process.Start();
        process.StandardInput.Write(input);
        process.StandardInput.Close();

        using var ms = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(ms);
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            throw new TimeoutException($"protoc 执行超时（{TimeoutMs / 1000}s），参数：{arguments}");
        }

        copyTask.GetAwaiter().GetResult();
        stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"protoc 退出码: {process.ExitCode}" : stderr.Trim());
        }
        return ms.ToArray();
    }

    private static string ResolveProtocPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var baseDir = Path.Combine(home, ".nuget", "packages", "grpc.tools");
        if (!Directory.Exists(baseDir))
        {
            return "protoc";
        }

        var versions = Directory.GetDirectories(baseDir)
            .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var version in versions)
        {
            var x64 = Path.Combine(version, "tools", "windows_x64", "protoc.exe");
            if (File.Exists(x64)) return x64;
            var x86 = Path.Combine(version, "tools", "windows_x86", "protoc.exe");
            if (File.Exists(x86)) return x86;
        }

        return "protoc";
    }

    private static string ResolveIncludeDir(string protoFile, string typeName)
    {
        var includeDir = Path.GetDirectoryName(protoFile);
        if (string.IsNullOrWhiteSpace(includeDir))
        {
            throw new InvalidOperationException($"无法解析 Message 所在目录：{typeName}（{protoFile}）");
        }
        return includeDir;
    }
}
