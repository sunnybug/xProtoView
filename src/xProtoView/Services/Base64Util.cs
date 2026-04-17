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

        // 兼容二进制输入：先在内存中转为 base64，再复用 base64 解码流程。
        if (TryDecodeBinary(raw, out var binaryBytes, out var binaryError))
        {
            if (!string.IsNullOrEmpty(binaryError))
            {
                throw new InvalidOperationException(binaryError);
            }

            var binaryAsBase64 = Convert.ToBase64String(binaryBytes, Base64FormattingOptions.None);
            return Convert.FromBase64String(binaryAsBase64);
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

    // 检测并解码二进制文本，返回 true 表示按二进制路径处理。
    private static bool TryDecodeBinary(string input, out byte[] bytes, out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;
        var bits = new List<char>(input.Length);
        var sawBinaryMarker = false;
        var sawSeparator = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c))
            {
                sawSeparator = true;
                continue;
            }

            if (c == ',' || c == '-' || c == ':' || c == '_')
            {
                sawSeparator = true;
                continue;
            }

            if (c == '0' && i + 1 < input.Length && (input[i + 1] == 'b' || input[i + 1] == 'B'))
            {
                sawBinaryMarker = true;
                i++;
                continue;
            }

            if (c == '0' || c == '1')
            {
                bits.Add(c);
                continue;
            }

            if (sawBinaryMarker)
            {
                error = $"检测到二进制输入，但包含非法字符 '{c}'（位置 {i + 1}）。";
                return true;
            }

            return false;
        }

        if (bits.Count == 0)
        {
            if (!sawBinaryMarker)
            {
                return false;
            }

            error = "检测到二进制输入，但未找到有效二进制字符。";
            return true;
        }

        // 无显式 0b 标记时，仅在明显是二进制字节流（有分隔符或恰好按字节对齐）才按二进制处理，避免与十六进制/普通输入冲突。
        if (!sawBinaryMarker && !sawSeparator && bits.Count % 8 != 0)
        {
            return false;
        }

        if (bits.Count % 8 != 0)
        {
            error = $"检测到二进制输入，但位数为 {bits.Count}，每个字节需要 8 位。";
            return true;
        }

        var output = new byte[bits.Count / 8];
        for (var byteIndex = 0; byteIndex < output.Length; byteIndex++)
        {
            byte value = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                value <<= 1;
                if (bits[byteIndex * 8 + bit] == '1')
                {
                    value |= 0x01;
                }
            }
            output[byteIndex] = value;
        }

        bytes = output;
        return true;
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
