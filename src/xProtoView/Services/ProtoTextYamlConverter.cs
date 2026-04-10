using System.Globalization;
using YamlDotNet.Serialization;

namespace xProtoView.Services;

public sealed class ProtoTextYamlConverter
{
    // 将 protoc 文本格式转换为 YAML。
    public string ConvertToYaml(string protoText)
    {
        var root = ParseProtoText(protoText);
        var serializer = new SerializerBuilder()
            .DisableAliases()
            .WithIndentedSequences()
            .Build();
        var yaml = serializer.Serialize(root).Trim();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidOperationException("YAML 序列化结果为空。");
        }
        return yaml;
    }

    // 解析 proto 文本为层级字典结构。
    private static Dictionary<string, object?> ParseProtoText(string protoText)
    {
        if (string.IsNullOrWhiteSpace(protoText))
        {
            throw new InvalidOperationException("Proto 文本为空。");
        }

        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        var stack = new Stack<Dictionary<string, object?>>();
        stack.Push(root);

        var lines = protoText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var raw = lines[i];
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // 处理块开始：field_name {
            if (line.EndsWith("{", StringComparison.Ordinal))
            {
                var field = line[..^1].Trim();
                if (!IsValidFieldName(field))
                {
                    throw new InvalidOperationException($"第 {lineNo} 行字段名非法：{field}");
                }

                var child = new Dictionary<string, object?>(StringComparer.Ordinal);
                AddFieldValue(stack.Peek(), field, child);
                stack.Push(child);
                continue;
            }

            // 处理块结束：}
            if (line == "}")
            {
                if (stack.Count <= 1)
                {
                    throw new InvalidOperationException($"第 {lineNo} 行出现多余的右花括号。");
                }
                stack.Pop();
                continue;
            }

            // 处理标量行：field_name: value
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= line.Length - 1)
            {
                throw new InvalidOperationException($"第 {lineNo} 行格式无效：{line}");
            }

            var fieldName = line[..colonIndex].Trim();
            var rawValue = line[(colonIndex + 1)..].Trim();
            if (!IsValidFieldName(fieldName))
            {
                throw new InvalidOperationException($"第 {lineNo} 行字段名非法：{fieldName}");
            }

            if (rawValue.Length == 0)
            {
                throw new InvalidOperationException($"第 {lineNo} 行字段值为空：{fieldName}");
            }

            var value = ParseScalar(rawValue);
            AddFieldValue(stack.Peek(), fieldName, value);
        }

        if (stack.Count != 1)
        {
            throw new InvalidOperationException("Proto 文本括号未闭合，存在未结束的消息块。");
        }
        return root;
    }

    // 将同名字段合并为数组，保持 protobuf 重复字段语义。
    private static void AddFieldValue(Dictionary<string, object?> target, string fieldName, object? value)
    {
        if (!target.TryGetValue(fieldName, out var existing))
        {
            target[fieldName] = value;
            return;
        }

        if (existing is List<object?> list)
        {
            list.Add(value);
            return;
        }

        target[fieldName] = new List<object?> { existing, value };
    }

    // 解析标量值（字符串、布尔、数字或标识符）。
    private static object ParseScalar(string value)
    {
        if (value.Length >= 2 && value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
        {
            var inner = value[1..^1];
            return RegexUnescape(inner);
        }

        if (value.Equals("true", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Equals("false", StringComparison.Ordinal))
        {
            return false;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return longValue;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        return value;
    }

    private static bool IsValidFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        for (var i = 0; i < fieldName.Length; i++)
        {
            var c = fieldName[i];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
            {
                continue;
            }
            return false;
        }
        return true;
    }

    // 仅反转义常见控制字符，保留其他内容原样。
    private static string RegexUnescape(string text)
    {
        return text
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
    }
}
