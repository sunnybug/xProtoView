using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf.Reflection;

namespace xProtoView.Services;

public sealed class ProtocDecoder
{
    public sealed record ProtoMessageScope(string TypeName, string ProtoFile, string IncludeDir);

    public sealed record ProtoInputPreparation(string ProtoText, bool ConvertedFromJson);

    private const int MaxNestedDecodeDepth = 16;
    private static readonly Regex PackageRegex = new(@"^\s*package\s+([A-Za-z_][\w.]*)\s*;", RegexOptions.Compiled);
    private static readonly Regex MessageRegex = new(@"^\s*message\s+([A-Za-z_]\w*)\s*\{?", RegexOptions.Compiled);
    private static readonly Regex CommentTypeHintRegex = new(@"([A-Za-z_]\w*(?:(?:::|:|\.)[A-Za-z_]\w+)+)", RegexOptions.Compiled);

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
    public string DecodeToProtoText(byte[] payload, ProtoMessageScope scope, IReadOnlyList<string>? allProtoFiles = null)
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

        // 基于注释映射对 bytes 字段做二次 message 解码并回写。
        var schema = BuildDecodeSchema(scope, protoc, includeDir, allProtoFiles);
        if (!schema.HasAnnotatedBytesField)
        {
            return output;
        }

        return ExpandAnnotatedBytesFields(output, NormalizeTypeName(scope.TypeName), schema, protoc, includeDir, "$", 0);
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

    // 加载 descriptor 并构建 bytes 注释解码规则。
    private static DecodeSchema BuildDecodeSchema(ProtoMessageScope scope, string protoc, string includeDir, IReadOnlyList<string>? allProtoFiles)
    {
        // 汇总已加载 proto 中的 message->文件映射，用于补充未 import 的注释提示类型。
        var externalTypeFileMap = BuildExternalTypeFileMap(allProtoFiles);

        var descriptorPath = Path.Combine(Path.GetTempPath(), $"xProtoView.decode.{Guid.NewGuid():N}.desc");
        try
        {
            var args = $"-I\"{includeDir}\" --include_imports --include_source_info --descriptor_set_out=\"{descriptorPath}\" \"{scope.ProtoFile}\"";
            ExecuteProtoc(protoc, args, null, out _);
            if (!File.Exists(descriptorPath))
            {
                throw new InvalidOperationException("protoc 未生成 descriptor_set 文件。");
            }

            var bytes = File.ReadAllBytes(descriptorPath);
            if (bytes.Length == 0)
            {
                throw new InvalidOperationException("descriptor_set 文件为空。");
            }

            FileDescriptorSet set;
            try
            {
                set = FileDescriptorSet.Parser.ParseFrom(bytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"descriptor_set 解析失败：{ex.Message}");
            }

            return BuildDecodeSchemaFromDescriptor(set, includeDir, scope.ProtoFile, externalTypeFileMap);
        }
        finally
        {
            try
            {
                if (File.Exists(descriptorPath))
                {
                    File.Delete(descriptorPath);
                }
            }
            catch
            {
            }
        }
    }

    // 从 descriptor 建立 message 字段与注释提示映射。
    private static DecodeSchema BuildDecodeSchemaFromDescriptor(
        FileDescriptorSet set,
        string includeDir,
        string rootProtoFile,
        IReadOnlyDictionary<string, string> externalTypeFileMap)
    {
        var messages = new Dictionary<string, MessageDecodeRule>(StringComparer.Ordinal);
        var pendingHints = new List<PendingBytesHint>();

        foreach (var file in set.File)
        {
            var packageName = (file.Package ?? string.Empty).Trim();
            var protoFilePath = ResolveProtoFilePath(file.Name, includeDir, rootProtoFile);
            var locationMap = BuildLocationMap(file.SourceCodeInfo);
            for (var i = 0; i < file.MessageType.Count; i++)
            {
                var path = new List<int> { 4, i };
                CollectMessageDecodeRule(
                    messages,
                    pendingHints,
                    file.MessageType[i],
                    packageName,
                    parentName: null,
                    protoFilePath,
                    locationMap,
                    path);
            }
        }

        // 将注释中的提示类型解析为 descriptor 中的真实 message 名称。
        var knownTypes = messages.Keys
            .Concat(externalTypeFileMap.Keys)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pending in pendingHints)
        {
            string? resolved = null;
            string? resolvedHint = null;
            foreach (var rawHint in pending.RawHints)
            {
                var candidate = ResolveCommentTypeHint(rawHint, pending.OwnerMessageType, messages, knownTypes);
                if (candidate is null)
                {
                    continue;
                }

                resolved = candidate;
                resolvedHint = rawHint;
                break;
            }

            // 注释提示仅作为增强能力，不阻断主解码流程。
            if (resolved is null || resolvedHint is null)
            {
                continue;
            }

            pending.FieldRule.BytesDecodeTypeName = resolved;
            pending.FieldRule.CommentHint = resolvedHint;

            // 对 descriptor 外部类型补齐最小规则，确保后续可定位到目标 proto 文件。
            if (!messages.ContainsKey(resolved) &&
                externalTypeFileMap.TryGetValue(resolved, out var externalProtoFile))
            {
                messages[resolved] = new MessageDecodeRule
                {
                    FullName = resolved,
                    PackageName = string.Empty,
                    ProtoFilePath = externalProtoFile
                };
            }
        }

        var includeDirs = CollectIncludeDirs(messages.Values.Select(x => x.ProtoFilePath), includeDir);
        return new DecodeSchema(messages, includeDirs);
    }

    // 递归收集消息字段规则，并记录待解析的 bytes 注释提示。
    private static void CollectMessageDecodeRule(
        Dictionary<string, MessageDecodeRule> messages,
        List<PendingBytesHint> pendingHints,
        DescriptorProto message,
        string packageName,
        string? parentName,
        string protoFilePath,
        Dictionary<string, SourceCodeInfo.Types.Location> locationMap,
        IReadOnlyList<int> messagePath)
    {
        var fullName = parentName is null ? JoinName(packageName, message.Name) : $"{parentName}.{message.Name}";
        if (messages.TryGetValue(fullName, out var existing) &&
            !string.Equals(existing.ProtoFilePath, protoFilePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Message 类型重复定义：{fullName}（{existing.ProtoFilePath} / {protoFilePath}）");
        }

        if (!messages.TryGetValue(fullName, out var rule))
        {
            rule = new MessageDecodeRule
            {
                FullName = fullName,
                PackageName = packageName,
                ProtoFilePath = protoFilePath
            };
            messages[fullName] = rule;
        }

        for (var i = 0; i < message.Field.Count; i++)
        {
            var field = message.Field[i];
            var fieldRule = new FieldDecodeRule
            {
                Name = field.Name,
                Type = field.Type,
                MessageTypeName = field.Type == FieldDescriptorProto.Types.Type.Message ? NormalizeTypeName(field.TypeName) : null
            };
            rule.Fields[field.Name] = fieldRule;

            if (field.Type != FieldDescriptorProto.Types.Type.Bytes)
            {
                continue;
            }

            var comment = ReadFieldComment(locationMap, messagePath, i);
            var hints = ExtractCommentTypeHints(comment);
            if (hints.Count == 0)
            {
                continue;
            }

            // 每个 bytes 字段按注释顺序尝试提示，命中后用于二次解码。
            pendingHints.Add(new PendingBytesHint
            {
                OwnerMessageType = fullName,
                FieldName = field.Name,
                RawHints = hints,
                FieldRule = fieldRule
            });
        }

        for (var i = 0; i < message.NestedType.Count; i++)
        {
            var childPath = AppendPathSegment(messagePath, 3, i);
            CollectMessageDecodeRule(
                messages,
                pendingHints,
                message.NestedType[i],
                packageName,
                fullName,
                protoFilePath,
                locationMap,
                childPath);
        }
    }

    // 将解码后的 proto 文本中命中的 bytes 字段替换为嵌套 message 块。
    private static string ExpandAnnotatedBytesFields(
        string protoText,
        string currentTypeName,
        DecodeSchema schema,
        string protocPath,
        string includeDir,
        string path,
        int depth)
    {
        if (depth > MaxNestedDecodeDepth)
        {
            throw new InvalidOperationException($"{path} 嵌套 bytes 解码层级超过上限：{MaxNestedDecodeDepth}");
        }

        var lines = SplitLines(protoText);
        var index = 0;
        var output = RewriteMessageLines(
            lines,
            ref index,
            NormalizeTypeName(currentTypeName),
            schema,
            protocPath,
            includeDir,
            path,
            depth,
            stopOnBrace: false);

        if (index < lines.Length)
        {
            throw new InvalidOperationException($"{path} 解析 proto 文本失败：存在未消费内容。");
        }

        return string.Join(Environment.NewLine, output).TrimEnd();
    }

    // 递归改写当前 message 的字段行。
    private static List<string> RewriteMessageLines(
        string[] lines,
        ref int index,
        string? currentTypeName,
        DecodeSchema schema,
        string protocPath,
        string includeDir,
        string path,
        int depth,
        bool stopOnBrace)
    {
        var output = new List<string>();
        schema.Messages.TryGetValue(NormalizeTypeName(currentTypeName), out var currentRule);
        var fieldCounters = new Dictionary<string, int>(StringComparer.Ordinal);

        while (index < lines.Length)
        {
            var raw = lines[index];
            var line = raw.Trim();
            if (line.Length == 0)
            {
                output.Add(raw);
                index++;
                continue;
            }

            if (line == "}")
            {
                if (!stopOnBrace)
                {
                    throw new InvalidOperationException($"{path} 解析 proto 文本失败：出现多余右花括号。");
                }
                break;
            }

            if (TryParseBlockStart(line, out var blockFieldName))
            {
                var blockFieldIndex = NextFieldIndex(fieldCounters, blockFieldName);
                var blockPath = $"{path}.{blockFieldName}[{blockFieldIndex}]";
                output.Add(raw);
                index++;

                string? childTypeName = null;
                if (currentRule is not null &&
                    currentRule.Fields.TryGetValue(blockFieldName, out var fieldRule) &&
                    fieldRule.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    childTypeName = fieldRule.MessageTypeName;
                }

                var childLines = RewriteMessageLines(
                    lines,
                    ref index,
                    childTypeName,
                    schema,
                    protocPath,
                    includeDir,
                    blockPath,
                    depth,
                    stopOnBrace: true);
                output.AddRange(childLines);

                if (index >= lines.Length || lines[index].Trim() != "}")
                {
                    throw new InvalidOperationException($"{blockPath} 解析 proto 文本失败：缺少右花括号。");
                }

                output.Add(lines[index]);
                index++;
                continue;
            }

            if (TryParseScalarLine(line, out var scalarFieldName, out var scalarValue))
            {
                var scalarFieldIndex = NextFieldIndex(fieldCounters, scalarFieldName);
                var scalarPath = $"{path}.{scalarFieldName}[{scalarFieldIndex}]";

                if (currentRule is not null &&
                    currentRule.Fields.TryGetValue(scalarFieldName, out var scalarRule) &&
                    scalarRule.Type == FieldDescriptorProto.Types.Type.Bytes &&
                    !string.IsNullOrWhiteSpace(scalarRule.BytesDecodeTypeName))
                {
                    var targetType = scalarRule.BytesDecodeTypeName!;
                    var hint = scalarRule.CommentHint ?? targetType;
                    try
                    {
                        var nestedBytes = ParseProtoBytesLiteral(scalarValue, scalarPath);
                        var nestedProtoFile = ResolveProtoFileForType(schema, targetType);
                        var nestedDecoded = DecodeWithProtoc(protocPath, nestedBytes, targetType, schema.IncludeDirs, nestedProtoFile, scalarPath);
                        var nestedExpanded = ExpandAnnotatedBytesFields(
                            nestedDecoded,
                            targetType,
                            schema,
                            protocPath,
                            includeDir,
                            scalarPath,
                            depth + 1);

                        var indent = GetIndent(raw);
                        output.Add($"{indent}{scalarFieldName} {{");
                        foreach (var child in SplitLines(nestedExpanded))
                        {
                            output.Add(child.Length == 0 ? string.Empty : $"{indent}  {child}");
                        }
                        output.Add($"{indent}}}");
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"{scalarPath} bytes 二次解码失败：注释提示 {hint} -> {targetType}，原因：{ex.Message}");
                    }

                    index++;
                    continue;
                }

                output.Add(raw);
                index++;
                continue;
            }

            output.Add(raw);
            index++;
        }

        if (stopOnBrace && index >= lines.Length)
        {
            throw new InvalidOperationException($"{path} 解析 proto 文本失败：缺少右花括号。");
        }

        return output;
    }

    // 执行一次指定类型的 protoc 解码。
    private static string DecodeWithProtoc(string protocPath, byte[] payload, string typeName, IReadOnlyList<string> includeDirs, string protoFile, string path)
    {
        if (!File.Exists(protoFile))
        {
            throw new InvalidOperationException($"{path} 二次解码目标 proto 文件不存在：{protoFile}");
        }

        var decodeIncludeDirs = BuildDecodeIncludeDirs(protoFile, includeDirs);
        if (decodeIncludeDirs.Count == 0)
        {
            throw new InvalidOperationException($"{path} 二次解码失败：未找到可用的 proto include 目录。");
        }

        var args = new List<string>
        {
            $"--decode={typeName}"
        };
        args.AddRange(decodeIncludeDirs.Select(x => $"-I\"{x}\""));
        args.Add($"\"{protoFile}\"");

        string output;
        string stderr;
        try
        {
            output = ExecuteProtoc(protocPath, string.Join(" ", args), payload, out stderr);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{path} 执行 protoc 二次解码失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? "protoc 未输出结果。" : stderr.Trim();
            throw new InvalidOperationException($"{path} 二次解码无输出：{reason}");
        }

        return output;
    }

    // 解析 proto bytes 字面量为原始字节。
    private static byte[] ParseProtoBytesLiteral(string value, string path)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length < 2 || !trimmed.StartsWith("\"", StringComparison.Ordinal) || !trimmed.EndsWith("\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{path} 不是合法 bytes 字面量：{trimmed}");
        }

        var inner = trimmed[1..^1];
        var bytes = new List<byte>(inner.Length);
        var plainText = new StringBuilder();

        void FlushPlainText()
        {
            if (plainText.Length == 0)
            {
                return;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(plainText.ToString()));
            plainText.Clear();
        }

        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c != '\\')
            {
                plainText.Append(c);
                continue;
            }

            FlushPlainText();
            if (i == inner.Length - 1)
            {
                throw new InvalidOperationException($"{path} bytes 字面量转义不完整：结尾反斜杠。");
            }

            i++;
            var esc = inner[i];
            switch (esc)
            {
                case 'a': bytes.Add(0x07); break;
                case 'b': bytes.Add(0x08); break;
                case 'f': bytes.Add(0x0C); break;
                case 'n': bytes.Add(0x0A); break;
                case 'r': bytes.Add(0x0D); break;
                case 't': bytes.Add(0x09); break;
                case 'v': bytes.Add(0x0B); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case '\'': bytes.Add((byte)'\''); break;
                case '"': bytes.Add((byte)'"'); break;
                case 'x':
                case 'X':
                {
                    if (i == inner.Length - 1 || !IsHex(inner[i + 1]))
                    {
                        throw new InvalidOperationException($"{path} bytes 十六进制转义无效：\\x 后缺少十六进制数字。");
                    }

                    var hexBuilder = new StringBuilder(2);
                    i++;
                    hexBuilder.Append(inner[i]);
                    if (i + 1 < inner.Length && IsHex(inner[i + 1]))
                    {
                        i++;
                        hexBuilder.Append(inner[i]);
                    }

                    var valueByte = byte.Parse(hexBuilder.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    bytes.Add(valueByte);
                    break;
                }
                default:
                {
                    if (esc is >= '0' and <= '7')
                    {
                        var octBuilder = new StringBuilder(3);
                        octBuilder.Append(esc);
                        for (var j = 0; j < 2; j++)
                        {
                            if (i + 1 >= inner.Length || inner[i + 1] < '0' || inner[i + 1] > '7')
                            {
                                break;
                            }
                            i++;
                            octBuilder.Append(inner[i]);
                        }

                        var octValue = Convert.ToInt32(octBuilder.ToString(), 8);
                        if (octValue < 0 || octValue > 255)
                        {
                            throw new InvalidOperationException($"{path} bytes 八进制转义超出范围：\\{octBuilder}");
                        }
                        bytes.Add((byte)octValue);
                        break;
                    }

                    // 兼容未知转义：按字面字符处理，避免吞掉可见数据。
                    bytes.AddRange(Encoding.UTF8.GetBytes(new[] { esc }));
                    break;
                }
            }
        }

        FlushPlainText();
        return bytes.ToArray();
    }

    // 读取字段注释（前置/后置/分离注释）。
    private static string ReadFieldComment(Dictionary<string, SourceCodeInfo.Types.Location> locationMap, IReadOnlyList<int> messagePath, int fieldIndex)
    {
        var fieldPath = AppendPathSegment(messagePath, 2, fieldIndex);
        if (!locationMap.TryGetValue(BuildPathKey(fieldPath), out var location))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(location.LeadingComments))
        {
            parts.Add(location.LeadingComments);
        }
        if (!string.IsNullOrWhiteSpace(location.TrailingComments))
        {
            parts.Add(location.TrailingComments);
        }
        foreach (var detached in location.LeadingDetachedComments)
        {
            if (!string.IsNullOrWhiteSpace(detached))
            {
                parts.Add(detached);
            }
        }

        return string.Join(Environment.NewLine, parts).Trim();
    }

    // 从注释文本提取可能的 message 类型提示。
    private static List<string> ExtractCommentTypeHints(string comment)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(comment))
        {
            return results;
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in CommentTypeHintRegex.Matches(comment))
        {
            var raw = match.Groups[1].Value.Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            var normalized = NormalizeCommentHint(raw);
            if (normalized.Length == 0)
            {
                continue;
            }

            // 仅接受末段首字母大写的提示，降低普通“键:值”注释误判。
            var tail = normalized.Split('.').LastOrDefault() ?? string.Empty;
            if (tail.Length == 0 || !char.IsUpper(tail[0]))
            {
                continue;
            }

            if (unique.Add(raw))
            {
                results.Add(raw);
            }
        }

        return results;
    }

    // 将注释提示解析为已知 message 全名。
    private static string? ResolveCommentTypeHint(
        string rawHint,
        string ownerMessageType,
        Dictionary<string, MessageDecodeRule> messages,
        HashSet<string> knownTypes)
    {
        if (!messages.TryGetValue(ownerMessageType, out var ownerRule))
        {
            return null;
        }

        var normalized = NormalizeCommentHint(rawHint);
        if (normalized.Length == 0)
        {
            return null;
        }

        var candidates = new List<string>();
        AddCandidate(candidates, normalized);

        if (!string.IsNullOrWhiteSpace(ownerRule.PackageName) &&
            !normalized.StartsWith($"{ownerRule.PackageName}.", StringComparison.Ordinal))
        {
            AddCandidate(candidates, $"{ownerRule.PackageName}.{normalized}");
        }

        foreach (var scope in EnumerateTypeScopes(ownerRule.FullName))
        {
            if (normalized.StartsWith($"{scope}.", StringComparison.Ordinal))
            {
                continue;
            }
            AddCandidate(candidates, $"{scope}.{normalized}");
        }

        foreach (var candidate in candidates)
        {
            if (knownTypes.Contains(candidate))
            {
                return candidate;
            }
        }

        var suffix = $".{normalized}";
        var suffixMatched = knownTypes
            .Where(x => x.EndsWith(suffix, StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        return suffixMatched.Count == 1 ? suffixMatched[0] : null;
    }

    // 根据类型名查找其定义所在 proto 文件。
    private static string ResolveProtoFileForType(DecodeSchema schema, string messageType)
    {
        var normalized = NormalizeTypeName(messageType);
        if (!schema.Messages.TryGetValue(normalized, out var rule))
        {
            throw new InvalidOperationException($"无法定位 bytes 注释目标类型：{normalized}");
        }

        if (!File.Exists(rule.ProtoFilePath))
        {
            throw new InvalidOperationException($"目标类型 proto 文件不存在：{normalized}（{rule.ProtoFilePath}）");
        }

        return rule.ProtoFilePath;
    }

    // 将 descriptor 相对文件名解析为本地可访问路径。
    private static string ResolveProtoFilePath(string descriptorFileName, string includeDir, string rootProtoFile)
    {
        if (string.IsNullOrWhiteSpace(descriptorFileName))
        {
            return rootProtoFile;
        }

        if (Path.IsPathRooted(descriptorFileName))
        {
            return Path.GetFullPath(descriptorFileName);
        }

        var byInclude = Path.GetFullPath(Path.Combine(includeDir, descriptorFileName));
        if (File.Exists(byInclude))
        {
            return byInclude;
        }

        var rootDir = Path.GetDirectoryName(rootProtoFile);
        if (!string.IsNullOrWhiteSpace(rootDir))
        {
            var byRoot = Path.GetFullPath(Path.Combine(rootDir, descriptorFileName));
            if (File.Exists(byRoot))
            {
                return byRoot;
            }
        }

        return byInclude;
    }

    // 生成二次解码时可用的 include 目录列表（优先目标 proto 所在目录）。
    private static List<string> BuildDecodeIncludeDirs(string protoFile, IReadOnlyList<string> includeDirs)
    {
        var result = new List<string>();
        var targetDir = Path.GetDirectoryName(protoFile);
        AddIncludeDir(result, targetDir);
        foreach (var dir in includeDirs)
        {
            AddIncludeDir(result, dir);
        }
        return result;
    }

    // 收集并去重 include 目录，保证后续 protoc 参数稳定。
    private static List<string> CollectIncludeDirs(IEnumerable<string> protoFiles, string fallbackIncludeDir)
    {
        var result = new List<string>();
        AddIncludeDir(result, fallbackIncludeDir);
        foreach (var file in protoFiles)
        {
            AddIncludeDir(result, Path.GetDirectoryName(file));
        }
        return result;
    }

    // 构建外部 message 类型索引，补充 descriptor 不包含的注释目标类型。
    private static IReadOnlyDictionary<string, string> BuildExternalTypeFileMap(IReadOnlyList<string>? allProtoFiles)
    {
        if (allProtoFiles is null || allProtoFiles.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var existing = allProtoFiles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (existing.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return BuildMessageTypeFileMap(existing);
    }

    // 构建 source path 到 location 的索引。
    private static Dictionary<string, SourceCodeInfo.Types.Location> BuildLocationMap(SourceCodeInfo sourceCodeInfo)
    {
        var map = new Dictionary<string, SourceCodeInfo.Types.Location>(StringComparer.Ordinal);
        foreach (var location in sourceCodeInfo.Location)
        {
            map[BuildPathKey(location.Path)] = location;
        }
        return map;
    }

    // 生成 path key 用于 source location 查找。
    private static string BuildPathKey(IReadOnlyList<int> path)
    {
        return string.Join('.', path);
    }

    // 给 descriptor path 追加一个“字段号 + 索引”段。
    private static List<int> AppendPathSegment(IReadOnlyList<int> path, int fieldNumber, int index)
    {
        var result = new List<int>(path.Count + 2);
        for (var i = 0; i < path.Count; i++)
        {
            result.Add(path[i]);
        }
        result.Add(fieldNumber);
        result.Add(index);
        return result;
    }

    // 规范化注释中的类型写法（::、:、. 统一为 .）。
    private static string NormalizeCommentHint(string rawHint)
    {
        var normalized = (rawHint ?? string.Empty).Trim().TrimStart('.');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        normalized = normalized.Replace("::", ".", StringComparison.Ordinal);
        normalized = normalized.Replace(':', '.');
        while (normalized.Contains("..", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("..", ".", StringComparison.Ordinal);
        }

        return normalized.Trim('.');
    }

    // 解析块开头行：field_name {
    private static bool TryParseBlockStart(string line, out string fieldName)
    {
        fieldName = string.Empty;
        if (!line.EndsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        var name = line[..^1].Trim();
        if (name.Length == 0)
        {
            return false;
        }

        fieldName = name;
        return true;
    }

    // 解析标量行：field_name: value
    private static bool TryParseScalarLine(string line, out string fieldName, out string value)
    {
        fieldName = string.Empty;
        value = string.Empty;
        var idx = line.IndexOf(':');
        if (idx <= 0 || idx >= line.Length - 1)
        {
            return false;
        }

        var name = line[..idx].Trim();
        var rawValue = line[(idx + 1)..].Trim();
        if (name.Length == 0 || rawValue.Length == 0)
        {
            return false;
        }

        fieldName = name;
        value = rawValue;
        return true;
    }

    // 获取原行前导缩进。
    private static string GetIndent(string rawLine)
    {
        var trimmed = rawLine.TrimStart();
        var len = rawLine.Length - trimmed.Length;
        return len <= 0 ? string.Empty : rawLine[..len];
    }

    // 切分 proto 文本为逻辑行。
    private static string[] SplitLines(string text)
    {
        return (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    // 记录同名字段出现序号，便于错误路径定位。
    private static int NextFieldIndex(Dictionary<string, int> counters, string fieldName)
    {
        counters.TryGetValue(fieldName, out var current);
        counters[fieldName] = current + 1;
        return current;
    }

    // 判断字符是否十六进制数字。
    private static bool IsHex(char c)
    {
        return (c is >= '0' and <= '9') ||
               (c is >= 'a' and <= 'f') ||
               (c is >= 'A' and <= 'F');
    }

    // 组合包名和类型名为完整名字。
    private static string JoinName(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return right;
        if (string.IsNullOrWhiteSpace(right)) return left;
        return $"{left}.{right}";
    }

    // 枚举当前类型从内到外的作用域。
    private static IEnumerable<string> EnumerateTypeScopes(string fullName)
    {
        var parts = NormalizeTypeName(fullName).Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length; i > 0; i--)
        {
            yield return string.Join(".", parts.Take(i));
        }
    }

    // 向候选列表添加不重复项。
    private static void AddCandidate(List<string> candidates, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!candidates.Contains(value, StringComparer.Ordinal))
        {
            candidates.Add(value);
        }
    }

    // 追加 include 目录并做规范化去重。
    private static void AddIncludeDir(List<string> includeDirs, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        var full = Path.GetFullPath(dir);
        if (!Directory.Exists(full))
        {
            return;
        }

        if (!includeDirs.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            includeDirs.Add(full);
        }
    }

    // 规范化类型名，移除前导点。
    private static string NormalizeTypeName(string? typeName)
    {
        return (typeName ?? string.Empty).Trim().TrimStart('.');
    }

    // bytes 注释待解析项。
    private sealed class PendingBytesHint
    {
        public required string OwnerMessageType { get; init; }
        public required string FieldName { get; init; }
        public required IReadOnlyList<string> RawHints { get; init; }
        public required FieldDecodeRule FieldRule { get; init; }
    }

    // 整体解码规则容器。
    private sealed class DecodeSchema
    {
        public DecodeSchema(Dictionary<string, MessageDecodeRule> messages, IReadOnlyList<string> includeDirs)
        {
            Messages = messages;
            IncludeDirs = includeDirs;
        }

        public Dictionary<string, MessageDecodeRule> Messages { get; }
        public IReadOnlyList<string> IncludeDirs { get; }

        public bool HasAnnotatedBytesField =>
            Messages.Values.Any(x => x.Fields.Values.Any(f => !string.IsNullOrWhiteSpace(f.BytesDecodeTypeName)));
    }

    // 单个 message 的字段解码规则。
    private sealed class MessageDecodeRule
    {
        public required string FullName { get; init; }
        public required string PackageName { get; init; }
        public required string ProtoFilePath { get; init; }
        public Dictionary<string, FieldDecodeRule> Fields { get; } = new(StringComparer.Ordinal);
    }

    // 单个字段的解码规则。
    private sealed class FieldDecodeRule
    {
        public required string Name { get; init; }
        public required FieldDescriptorProto.Types.Type Type { get; init; }
        public string? MessageTypeName { get; init; }
        public string? BytesDecodeTypeName { get; set; }
        public string? CommentHint { get; set; }
    }
}
