using System.Diagnostics;
using System.Text.RegularExpressions;

namespace xProtoView.Services;

public sealed class ProtocDecoder
{
    // 将二进制 proto 解码为 proto 文本。
    public string DecodeToProtoText(
        byte[] payload,
        string typeName,
        IReadOnlyList<string> includeDirs,
        IReadOnlyList<string> protoFiles)
    {
        var protoc = ResolveProtocPath();
        if (!File.Exists(protoc))
        {
            throw new InvalidOperationException($"未找到 protoc：{protoc}");
        }

        var args = new List<string> { $"--decode={typeName}" };
        args.AddRange(includeDirs.Select(x => $"-I\"{x}\""));
        args.AddRange(protoFiles.Select(x => $"\"{x}\""));
        var output = ExecuteProtoc(protoc, string.Join(" ", args), payload, out var stderr);
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "protoc 未输出结果。" : stderr);
        }
        return output;
    }

    // 将 proto 文本编码为二进制 proto。
    public byte[] EncodeFromProtoText(
        string protoText,
        string typeName,
        IReadOnlyList<string> includeDirs,
        IReadOnlyList<string> protoFiles)
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

        var args = new List<string> { $"--encode={typeName}" };
        args.AddRange(includeDirs.Select(x => $"-I\"{x}\""));
        args.AddRange(protoFiles.Select(x => $"\"{x}\""));
        var output = ExecuteProtocBinary(protoc, string.Join(" ", args), raw, out var stderr);
        if (output.Length == 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "protoc 未输出二进制结果。" : stderr);
        }
        return output;
    }

    public static List<string> ExtractMessageTypes(IEnumerable<string> protoFiles)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var packageRegex = new Regex(@"^\s*package\s+([A-Za-z_][\w.]*)\s*;", RegexOptions.Compiled);
        var messageRegex = new Regex(@"^\s*message\s+([A-Za-z_]\w*)\s*\{?", RegexOptions.Compiled);

        foreach (var file in protoFiles)
        {
            var packageName = string.Empty;
            var lines = File.ReadAllLines(file);
            var stack = new Stack<string>();

            foreach (var raw in lines)
            {
                var line = raw.Split("//")[0];
                if (line.TrimStart().StartsWith("/*", StringComparison.Ordinal)) continue;

                var packageMatch = packageRegex.Match(line);
                if (packageMatch.Success)
                {
                    packageName = packageMatch.Groups[1].Value.Trim();
                }

                var msgMatch = messageRegex.Match(line);
                if (msgMatch.Success)
                {
                    var name = msgMatch.Groups[1].Value.Trim();
                    var full = string.Join(".", stack.Reverse().Append(name));
                    if (!string.IsNullOrWhiteSpace(packageName))
                    {
                        full = $"{packageName}.{full}";
                    }
                    result.Add(full);
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

        return result.OrderBy(x => x, StringComparer.Ordinal).ToList();
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
}
