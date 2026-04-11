using xProtoView.Services;
using Xunit;

namespace xProtoView.Tests;

public class Base64UtilTests
{
    [Fact]
    public void Decode_ShouldThrow_WhenInputIsEmpty()
    {
        Assert.Throws<InvalidOperationException>(() => Base64Util.Decode("  "));
    }

    [Fact]
    public void Decode_ShouldReturnBytes_WhenInputIsValid()
    {
        var bytes = Base64Util.Decode("SGVsbG8=");
        Assert.Equal("Hello", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Decode_ShouldReturnBytes_WhenInputIsHex()
    {
        // 纯十六进制输入应被识别并正常解码。
        var bytes = Base64Util.Decode("48656c6c6f");
        Assert.Equal("Hello", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Decode_ShouldReturnBytes_WhenInputIsHexWithPrefixAndSeparators()
    {
        // 支持 0x 前缀与常见分隔符。
        var bytes = Base64Util.Decode("0x48,0x65,0x6c-0x6c:0x6f");
        Assert.Equal("Hello", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Decode_ShouldThrow_WhenHexLengthIsOdd()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Base64Util.Decode("ABC"));
        Assert.Contains("字符数为奇数", ex.Message);
    }

    [Fact]
    public void Encode_ShouldReturnBase64_WhenInputIsValid()
    {
        var text = Base64Util.Encode(System.Text.Encoding.UTF8.GetBytes("Hello"));
        Assert.Equal("SGVsbG8=", text);
    }

    [Fact]
    public void Encode_ShouldInsertLineBreaks_WhenInputIsLong()
    {
        // 长文本编码后应自动分行。
        var bytes = Enumerable.Repeat((byte)'A', 200).ToArray();
        var text = Base64Util.Encode(bytes);
        Assert.Contains(Environment.NewLine, text);

        // 分行后的 Base64 仍应可被解码还原。
        var decoded = Base64Util.Decode(text);
        Assert.Equal(bytes, decoded);
    }
}
