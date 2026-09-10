using System.Text;

using ClaudeRoslynLsp.Edits;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Edits;

/// <summary>
/// Byte order marks and line endings, which a naive read/write round trip destroys silently (D61).
/// </summary>
public class TextFileCodecTests
{
    [Fact]
    public void Utf8WithoutAMarkRoundTripsUnchanged()
    {
        var bytes = Encoding.UTF8.GetBytes("class C\n{\n}\n");

        var (text, format) = TextFileCodec.Decode(bytes);

        Assert.Equal("class C\n{\n}\n", text);
        Assert.Equal(TextFileEncoding.Utf8, format.Encoding);
        Assert.False(format.HasByteOrderMark);
        Assert.Equal("\n", format.NewLine);
        Assert.Equal(bytes, TextFileCodec.Encode(text, format));
    }

    /// <summary>
    /// The mark is stripped from the text and put back on the bytes. Leaving it in the text would put
    /// a U+FEFF at offset zero, which every LSP range in the file is then off by one against.
    /// </summary>
    [Fact]
    public void AUtf8MarkIsPreservedWithoutLeakingIntoTheText()
    {
        var bytes = (byte[]) [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class C\r\n")];

        var (text, format) = TextFileCodec.Decode(bytes);

        Assert.Equal("class C\r\n", text);
        Assert.True(format.HasByteOrderMark);
        Assert.Equal("\r\n", format.NewLine);
        Assert.Equal(bytes, TextFileCodec.Encode(text, format));
    }

    [Fact]
    public void Utf16LittleEndianRoundTripsAsUtf16()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("class C\r\n")).ToArray();

        var (text, format) = TextFileCodec.Decode(bytes);

        Assert.Equal("class C\r\n", text);
        Assert.Equal(TextFileEncoding.Utf16LittleEndian, format.Encoding);
        Assert.True(format.HasByteOrderMark);
        Assert.Equal(bytes, TextFileCodec.Encode(text, format));
    }

    [Fact]
    public void Utf16BigEndianRoundTripsAsUtf16()
    {
        var encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("class C\n")).ToArray();

        var (text, format) = TextFileCodec.Decode(bytes);

        Assert.Equal("class C\n", text);
        Assert.Equal(TextFileEncoding.Utf16BigEndian, format.Encoding);
        Assert.Equal(bytes, TextFileCodec.Encode(text, format));
    }

    /// <summary>
    /// Dominant, not first: a file edited on two platforms should keep the convention most of it
    /// uses rather than whichever one is at the top.
    /// </summary>
    [Theory]
    [InlineData("a\r\nb\r\nc\r\n", "\r\n")]
    [InlineData("a\nb\nc\n", "\n")]
    [InlineData("a\nb\r\nc\r\nd\r\n", "\r\n")]
    [InlineData("a\r\nb\nc\nd\n", "\n")]
    [InlineData("no line endings at all", "\n")]
    public void TheDominantLineEndingWins(string text, string expected) =>
        Assert.Equal(expected, TextFileCodec.DetectNewLine(text));

    /// <summary>
    /// C22: a single <c>newText</c> from Roslyn really does mix both, so normalising the file alone
    /// leaves a mixed-ending file behind.
    /// </summary>
    [Theory]
    [InlineData("one\r\ntwo\nthree", "\r\n", "one\r\ntwo\r\nthree")]
    [InlineData("one\r\ntwo\nthree", "\n", "one\ntwo\nthree")]
    [InlineData("one\rtwo", "\n", "one\ntwo")]
    [InlineData("no endings", "\r\n", "no endings")]
    [InlineData("", "\n", "")]
    public void IncomingTextIsNormalisedToTheFilesOwnEnding(string text, string newLine, string expected) =>
        Assert.Equal(expected, TextFileCodec.NormalizeNewLines(text, newLine));

    [Fact]
    public void AFileCreatedFromNothingIsUtf8WithoutAMark()
    {
        var format = TextFileFormat.CreatedFileDefault;

        Assert.Equal(TextFileEncoding.Utf8, format.Encoding);
        Assert.False(format.HasByteOrderMark);
        Assert.Empty(TextFileCodec.Encode(string.Empty, format));
    }
}
