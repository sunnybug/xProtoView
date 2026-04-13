using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf.Reflection;

namespace xProtoView.Services;

internal static class JsonProtoTextConverter
{
    // 仅在明显 JSON 结构时走 JSON 转换分支。
    public static bool LooksLikeJson(string text)
    {
        var trimmed = (text ?? string.Empty).TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal);
    }

    // 基于 descriptor 将 JSON 转为 proto text。
    public static string Convert(string json, ProtocDecoder.ProtoMessageScope scope, Func<string> resolveProtocPath)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"JSON 格式错误：{ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("JSON 根节点必须是对象。");
            }

            var index = LoadDescriptorIndex(scope, resolveProtocPath);
            var typeName = NormalizeTypeName(scope.TypeName);
            if (!index.Messages.TryGetValue(typeName, out var message))
            {
                throw new InvalidOperationException($"descriptor 中未找到消息类型：{scope.TypeName}");
            }

            var lines = new List<string>();
            AppendMessage(lines, doc.RootElement, message, index, 0, "$");
            var protoText = string.Join(Environment.NewLine, lines).Trim();
            if (protoText.Length == 0)
            {
                throw new InvalidOperationException("JSON 转换后的 Proto 文本为空，无法编码。");
            }
            return protoText;
        }
    }

    // 递归转换消息对象。
    private static void AppendMessage(List<string> lines, JsonElement obj, DescriptorProto msg, DescriptorIndex index, int indent, string path)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{path} 必须是对象。");
        }

        var fieldMap = new Dictionary<string, FieldDescriptorProto>(StringComparer.Ordinal);
        foreach (var field in msg.Field)
        {
            fieldMap.TryAdd(field.Name, field);
            if (!string.IsNullOrWhiteSpace(field.JsonName))
            {
                fieldMap.TryAdd(field.JsonName, field);
            }
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (!fieldMap.TryGetValue(prop.Name, out var field))
            {
                throw new InvalidOperationException($"JSON 字段不存在：{path}.{prop.Name}");
            }
            if (prop.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            AppendField(lines, field, prop.Value, index, indent, $"{path}.{prop.Name}");
        }
    }

    // 处理 repeated / map / 普通字段。
    private static void AppendField(List<string> lines, FieldDescriptorProto field, JsonElement value, DescriptorIndex index, int indent, string path)
    {
        if (TryGetMapEntry(field, index, out var mapEntry))
        {
            AppendMapField(lines, field, value, mapEntry!, index, indent, path);
            return;
        }

        if (field.Label == FieldDescriptorProto.Types.Label.Repeated && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                AppendValue(lines, field.Name, field, item, index, indent, path);
            }
            return;
        }

        AppendValue(lines, field.Name, field, value, index, indent, path);
    }

    // map 字段按 key/value entry 输出。
    private static void AppendMapField(List<string> lines, FieldDescriptorProto field, JsonElement value, DescriptorProto mapEntry, DescriptorIndex index, int indent, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{path} 必须是对象（map 字段）。");
        }

        var keyField = mapEntry.Field.Single(x => x.Name == "key");
        var valueField = mapEntry.Field.Single(x => x.Name == "value");
        var pad = new string(' ', indent);
        var pad2 = new string(' ', indent + 2);

        foreach (var pair in value.EnumerateObject())
        {
            lines.Add($"{pad}{field.Name} {{");
            lines.Add($"{pad2}key: {ConvertMapKeyLiteral(keyField, pair.Name, $"{path}[{pair.Name}]<key>")}");
            AppendValue(lines, "value", valueField, pair.Value, index, indent + 2, $"{path}[{pair.Name}]");
            lines.Add($"{pad}}}");
        }
    }

    // 输出单个值，支持标量/枚举/消息。
    private static void AppendValue(List<string> lines, string outName, FieldDescriptorProto field, JsonElement value, DescriptorIndex index, int indent, string path)
    {
        var pad = new string(' ', indent);
        if (field.Type == FieldDescriptorProto.Types.Type.Message)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"{path} 必须是对象。");
            }
            var typeName = NormalizeTypeName(field.TypeName);
            if (!index.Messages.TryGetValue(typeName, out var nested))
            {
                throw new InvalidOperationException($"{path} 无法解析消息类型：{field.TypeName}");
            }
            lines.Add($"{pad}{outName} {{");
            AppendMessage(lines, value, nested, index, indent + 2, path);
            lines.Add($"{pad}}}");
            return;
        }

        if (field.Type == FieldDescriptorProto.Types.Type.Enum)
        {
            lines.Add($"{pad}{outName}: {ConvertEnumLiteral(field, value, index, path)}");
            return;
        }

        lines.Add($"{pad}{outName}: {ConvertScalarLiteral(field.Type, value, path)}");
    }

    // 判断字段是否 map。
    private static bool TryGetMapEntry(FieldDescriptorProto field, DescriptorIndex index, out DescriptorProto? mapEntry)
    {
        mapEntry = null;
        if (field.Label != FieldDescriptorProto.Types.Label.Repeated || field.Type != FieldDescriptorProto.Types.Type.Message)
        {
            return false;
        }

        if (!index.Messages.TryGetValue(NormalizeTypeName(field.TypeName), out var msg))
        {
            return false;
        }

        if (!msg.Options.MapEntry)
        {
            return false;
        }

        mapEntry = msg;
        return true;
    }

    // 标量字段按 protobuf 类型转换。
    private static string ConvertScalarLiteral(FieldDescriptorProto.Types.Type type, JsonElement value, string path)
    {
        return type switch
        {
            FieldDescriptorProto.Types.Type.String => QuoteProtoString(ReadAsString(value, path)),
            FieldDescriptorProto.Types.Type.Bytes => QuoteProtoBytes(ReadBytes(value, path)),
            FieldDescriptorProto.Types.Type.Bool => ReadBool(value, path) ? "true" : "false",
            FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 or FieldDescriptorProto.Types.Type.Sfixed32 => ReadInt(value, path, int.MinValue, int.MaxValue).ToString(CultureInfo.InvariantCulture),
            FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 or FieldDescriptorProto.Types.Type.Sfixed64 => ReadInt(value, path, long.MinValue, long.MaxValue).ToString(CultureInfo.InvariantCulture),
            FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Fixed32 => ReadUInt(value, path, uint.MaxValue).ToString(CultureInfo.InvariantCulture),
            FieldDescriptorProto.Types.Type.Uint64 or FieldDescriptorProto.Types.Type.Fixed64 => ReadUInt(value, path, ulong.MaxValue).ToString(CultureInfo.InvariantCulture),
            FieldDescriptorProto.Types.Type.Float => FormatFloat(ReadDouble(value, path)),
            FieldDescriptorProto.Types.Type.Double => FormatDouble(ReadDouble(value, path)),
            _ => throw new InvalidOperationException($"{path} 不支持的标量类型：{type}")
        };
    }

    // 枚举支持名称或数字。
    private static string ConvertEnumLiteral(FieldDescriptorProto field, JsonElement value, DescriptorIndex index, string path)
    {
        var typeName = NormalizeTypeName(field.TypeName);
        if (!index.Enums.TryGetValue(typeName, out var enumDesc))
        {
            throw new InvalidOperationException($"{path} 无法解析枚举类型：{field.TypeName}");
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = (value.GetString() ?? string.Empty).Trim();
            if (enumDesc.Value.Any(x => x.Name == raw))
            {
                return raw;
            }
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
            {
                return ResolveEnumByNumber(enumDesc, intVal);
            }
            throw new InvalidOperationException($"{path} 枚举值无效：{raw}");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return ResolveEnumByNumber(enumDesc, number);
        }

        throw new InvalidOperationException($"{path} 枚举值类型错误：{value.ValueKind}");
    }

    // map key 按目标类型转换。
    private static string ConvertMapKeyLiteral(FieldDescriptorProto keyField, string keyText, string path)
    {
        return keyField.Type switch
        {
            FieldDescriptorProto.Types.Type.String => QuoteProtoString(keyText),
            FieldDescriptorProto.Types.Type.Bool => ParseBoolText(keyText, path) ? "true" : "false",
            FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 or FieldDescriptorProto.Types.Type.Sfixed32 => ParseIntText(keyText, path, int.MinValue, int.MaxValue).ToString(CultureInfo.InvariantCulture),
            FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 or FieldDescriptorProto.Types.Type.Sfixed64 => ParseIntText(keyText, path, long.MinValue, long.MaxValue).ToString(CultureInfo.InvariantCulture),
            FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Fixed32 => ParseUIntText(keyText, path, uint.MaxValue).ToString(CultureInfo.InvariantCulture),
            FieldDescriptorProto.Types.Type.Uint64 or FieldDescriptorProto.Types.Type.Fixed64 => ParseUIntText(keyText, path, ulong.MaxValue).ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"{path} map key 类型不支持：{keyField.Type}")
        };
    }

    // 解析 descriptor_set 并建立类型索引。
    private static DescriptorIndex LoadDescriptorIndex(ProtocDecoder.ProtoMessageScope scope, Func<string> resolveProtocPath)
    {
        var protoc = resolveProtocPath();
        if (!File.Exists(protoc))
        {
            throw new InvalidOperationException($"未找到 protoc：{protoc}");
        }

        var descriptorPath = Path.Combine(Path.GetTempPath(), $"xProtoView.{Guid.NewGuid():N}.desc");
        try
        {
            var args = $"-I\"{scope.IncludeDir}\" --include_imports --descriptor_set_out=\"{descriptorPath}\" \"{scope.ProtoFile}\"";
            ExecuteProcess(protoc, args, null);
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

            return BuildDescriptorIndex(set);
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

    // 从 descriptor 构建消息与枚举索引。
    private static DescriptorIndex BuildDescriptorIndex(FileDescriptorSet set)
    {
        var messages = new Dictionary<string, DescriptorProto>(StringComparer.Ordinal);
        var enums = new Dictionary<string, EnumDescriptorProto>(StringComparer.Ordinal);
        foreach (var file in set.File)
        {
            var packageName = (file.Package ?? string.Empty).Trim();
            foreach (var msg in file.MessageType)
            {
                CollectMessage(messages, enums, msg, packageName, null);
            }
            foreach (var enumType in file.EnumType)
            {
                enums[JoinName(packageName, enumType.Name)] = enumType;
            }
        }
        return new DescriptorIndex(messages, enums);
    }

    // 递归收集嵌套消息与枚举。
    private static void CollectMessage(Dictionary<string, DescriptorProto> messages, Dictionary<string, EnumDescriptorProto> enums, DescriptorProto msg, string packageName, string? parent)
    {
        var fullName = parent is null ? JoinName(packageName, msg.Name) : $"{parent}.{msg.Name}";
        messages[fullName] = msg;
        foreach (var enumType in msg.EnumType)
        {
            enums[$"{fullName}.{enumType.Name}"] = enumType;
        }
        foreach (var nested in msg.NestedType)
        {
            CollectMessage(messages, enums, nested, packageName, fullName);
        }
    }

    private sealed record DescriptorIndex(
        Dictionary<string, DescriptorProto> Messages,
        Dictionary<string, EnumDescriptorProto> Enums);

    // 执行 protoc 并在失败时抛出精确错误。
    private static void ExecuteProcess(string fileName, string args, string? stdin)
    {
        const int TimeoutMs = 180000;
        using var p = new Process();
        p.StartInfo.FileName = fileName;
        p.StartInfo.Arguments = args;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardInput = stdin is not null;
        p.StartInfo.RedirectStandardOutput = false;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.CreateNoWindow = true;
        p.Start();
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }

        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"protoc 执行超时（{TimeoutMs / 1000}s），参数：{args}");
        }

        var stderr = errTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"protoc 退出码: {p.ExitCode}" : stderr.Trim());
        }
    }

    // 读取可字符串化的 JSON 值。
    private static string ReadAsString(JsonElement value, string path)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new InvalidOperationException($"{path} 类型错误：{value.ValueKind}（期望字符串/数字/布尔）")
        };
    }

    // 读取 bytes（base64）并转义为 proto bytes 字面量。
    private static byte[] ReadBytes(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{path} 类型错误：{value.ValueKind}（bytes 期望 base64 字符串）");
        }

        try
        {
            return System.Convert.FromBase64String(value.GetString() ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{path} 不是有效 base64：{ex.Message}");
        }
    }

    // 读取 bool，兼容 true/false、0/1、\"true\"/\"false\"。
    private static bool ReadBool(JsonElement value, string path)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var n) && (n == 0 || n == 1) => n == 1,
            JsonValueKind.String => ParseBoolText(value.GetString() ?? string.Empty, path),
            JsonValueKind.Number => throw new InvalidOperationException($"{path} 布尔数字仅支持 0/1。"),
            _ => throw new InvalidOperationException($"{path} 类型错误：{value.ValueKind}（期望布尔/字符串/数字）")
        };
    }

    // 读取有符号整数并校验范围。
    private static long ReadInt(JsonElement value, string path, long min, long max)
    {
        long n = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var i) => i,
            JsonValueKind.String => ParseIntText(value.GetString() ?? string.Empty, path, min, max),
            JsonValueKind.Number => throw new InvalidOperationException($"{path} 不是有效整数。"),
            _ => throw new InvalidOperationException($"{path} 类型错误：{value.ValueKind}（期望整数或数字字符串）")
        };

        if (n < min || n > max)
        {
            throw new InvalidOperationException($"{path} 超出范围：{n}（{min}..{max}）");
        }

        return n;
    }

    // 读取无符号整数并校验范围。
    private static ulong ReadUInt(JsonElement value, string path, ulong max)
    {
        ulong n = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt64(out var i) => i,
            JsonValueKind.String => ParseUIntText(value.GetString() ?? string.Empty, path, max),
            JsonValueKind.Number => throw new InvalidOperationException($"{path} 不是有效无符号整数。"),
            _ => throw new InvalidOperationException($"{path} 类型错误：{value.ValueKind}（期望无符号整数或数字字符串）")
        };

        if (n > max)
        {
            throw new InvalidOperationException($"{path} 超出范围：{n}（最大 {max}）");
        }

        return n;
    }

    // 读取浮点（支持字符串数字和 NaN/Infinity）。
    private static double ReadDouble(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n))
        {
            return n;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{path} 类型错误：{value.ValueKind}（期望数字或数字字符串）");
        }

        var raw = (value.GetString() ?? string.Empty).Trim();
        if (raw.Equals("nan", StringComparison.OrdinalIgnoreCase)) return double.NaN;
        if (raw.Equals("inf", StringComparison.OrdinalIgnoreCase) || raw.Equals("infinity", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
        if (raw.Equals("-inf", StringComparison.OrdinalIgnoreCase) || raw.Equals("-infinity", StringComparison.OrdinalIgnoreCase)) return double.NegativeInfinity;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw new InvalidOperationException($"{path} 不是有效浮点数：{raw}");
    }

    private static bool ParseBoolText(string text, string path)
    {
        return text.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            "1" => true,
            "0" => false,
            _ => throw new InvalidOperationException($"{path} 不是有效布尔值：{text}")
        };
    }

    private static long ParseIntText(string text, string path, long min, long max)
    {
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            throw new InvalidOperationException($"{path} 不是有效整数：{text}");
        }
        if (n < min || n > max)
        {
            throw new InvalidOperationException($"{path} 超出范围：{n}（{min}..{max}）");
        }
        return n;
    }

    private static ulong ParseUIntText(string text, string path, ulong max)
    {
        if (!ulong.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            throw new InvalidOperationException($"{path} 不是有效无符号整数：{text}");
        }
        if (n > max)
        {
            throw new InvalidOperationException($"{path} 超出范围：{n}（最大 {max}）");
        }
        return n;
    }

    private static string ResolveEnumByNumber(EnumDescriptorProto enumDesc, int number)
    {
        var value = enumDesc.Value.FirstOrDefault(x => x.Number == number);
        return value is null ? number.ToString(CultureInfo.InvariantCulture) : value.Name;
    }

    private static string QuoteProtoString(string text)
    {
        var escaped = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string QuoteProtoBytes(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 4);
        foreach (var b in bytes)
        {
            sb.Append("\\x");
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return $"\"{sb}\"";
    }

    private static string FormatFloat(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        return ((float)value).ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string JoinName(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return right;
        if (string.IsNullOrWhiteSpace(right)) return left;
        return $"{left}.{right}";
    }

    private static string NormalizeTypeName(string typeName)
    {
        return (typeName ?? string.Empty).Trim().TrimStart('.');
    }
}
