namespace xProtoView.Services;

public static class Base64Util
{
    // 解码 base64 文本为字节数组。
    public static byte[] Decode(string text)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            throw new InvalidOperationException("Base64 为空。");
        }

        var compact = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray());
        try
        {
            return Convert.FromBase64String(compact);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Base64 格式错误：{ex.Message}");
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
