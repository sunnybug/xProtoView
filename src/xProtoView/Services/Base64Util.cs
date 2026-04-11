namespace xProtoView.Services;

public static class Base64Util
{
    // 解码 base64 文本为字节数组。
    public static byte[] Decode(string text)
    {
        var raw = text ?? string.Empty;
        if (raw.Length == 0)
        {
            throw new InvalidOperationException("Base64 为空。");
        }

        // 兼容十六进制输入：先在内存中转为 base64，再复用 base64 解码流程。
        if (TryDecodeHex(raw, out var hexBytes, out var hexError))
        {
            if (!string.IsNullOrEmpty(hexError))
            {
                throw new InvalidOperationException(hexError);
            }

            var hexAsBase64 = Convert.ToBase64String(hexBytes, Base64FormattingOptions.None);
            return Convert.FromBase64String(hexAsBase64);
        }

        var compact = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length == 0)
        {
            throw new InvalidOperationException("Base64 为空。");
        }
        try
        {
            return Convert.FromBase64String(compact);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Base64 格式错误：{ex.Message}");
        }
    }

    // 检测并解码十六进制文本，返回 true 表示按十六进制路径处理。
    private static bool TryDecodeHex(string input, out byte[] bytes, out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;
        var hexChars = new List<char>(input.Length);
        var sawHexMarker = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c == ':' || c == '-' || c == ',')
            {
                sawHexMarker = true;
                continue;
            }

            if (c == '0' && i + 1 < input.Length && (input[i + 1] == 'x' || input[i + 1] == 'X'))
            {
                sawHexMarker = true;
                i++;
                continue;
            }

            if (Uri.IsHexDigit(c))
            {
                hexChars.Add(c);
                continue;
            }

            if (sawHexMarker)
            {
                error = $"检测到十六进制输入，但包含非法字符 '{c}'（位置 {i + 1}）。";
                return true;
            }

            return false;
        }

        if (!sawHexMarker && hexChars.Count == 0)
        {
            return false;
        }

        if (!sawHexMarker && hexChars.Count != input.Count(c => !char.IsWhiteSpace(c)))
        {
            return false;
        }

        if (hexChars.Count == 0)
        {
            error = "检测到十六进制输入，但未找到有效十六进制字符。";
            return true;
        }

        if (hexChars.Count % 2 != 0)
        {
            error = $"检测到十六进制输入，但字符数为奇数（{hexChars.Count}），每个字节需要 2 个十六进制字符。";
            return true;
        }

        try
        {
            bytes = Convert.FromHexString(new string(hexChars.ToArray()));
            return true;
        }
        catch (FormatException ex)
        {
            error = $"十六进制格式错误：{ex.Message}";
            return true;
        }
    }

    // 编码字节数组为 base64 文本。
    public static string Encode(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            throw new InvalidOperationException("待编码字节为空。");
        }
        // 编码时按标准 76 列自动换行，便于多行查看与复制。
        return Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks);
    }
}
