namespace TinyProxy.Tests.Protocol;

public class HttpForwarderChunkedParsingTests
{
    private static readonly MethodInfo s_parseChunkSizeMethod =
        typeof(HttpForwarder).GetMethod(
            "ParseChunkSize",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    [Theory]
    [InlineData("A\r\n", 10)]
    [InlineData("A\n", 10)]
    [InlineData("1f\r\n", 31)]
    [InlineData("10;foo=bar\r\n", 16)]
    [InlineData(" 2B ; ext=value \r\n", 43)]
    public void ParseChunkSize_ParsesValidLines(string line, long expected)
    {
        var actual = InvokeParseChunkSize(line);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("XYZ\r\n")]
    [InlineData("10\r")]
    public void ParseChunkSize_RejectsInvalidLines(string line)
    {
        var ex = Assert.Throws<TargetInvocationException>(() => InvokeParseChunkSize(line));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    private static long InvokeParseChunkSize(string line)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(line);
        return (long)s_parseChunkSizeMethod.Invoke(null, new object[] { new ReadOnlyMemory<byte>(bytes) })!;
    }
}
