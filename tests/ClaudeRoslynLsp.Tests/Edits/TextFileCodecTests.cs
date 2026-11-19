using System.Text;

using ClaudeRoslynLsp.Edits;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Edits;

/// <summary>
/// Byte order marks and line endings, which a naive read/write round trip destroys silently (D61).
/// </summary>
public class TextFileCodecTests
{
    [Test]
    public void Utf8WithoutAMarkRoundTripsUnchanged()
    {
        var bytes = Encoding.UTF8.GetBytes("class C\n{\n}\n");

        var (text, format) = TextFileCodec.Decode(bytes);

        Assert.That(text, Is.EqualTo("class C\n{\n}\n"));
        Assert.That(format.Encoding, Is.EqualTo(TextFileEncoding.Utf8));
        Assert.That(format.HasByteOrderMark, Is.False);
        Assert.That(format.NewLine, Is.EqualTo("\n"));
        Assert.That(TextFileCodec.Encode(text, format), Is.EqualTo(bytes));
    }

    /// <summary>
    /// The mark is stripped from the text and put back on the bytes. Leaving it in the text would put
    /// a U+FEFF at offset zero, which every LSP range in the file is then off by one against.
    /// </summary>
    [Test]
    public void AUtf8MarkIsPreservedWithoutLeakingIntoTheText()
    {
        var bytes = (byte[]) [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class C\r\n")];

        var (text, format) = TextFileCodec.Decode(bytes);

        Assert.That(text, Is.EqualTo("class C\r\n"));
        Assert.That(format.HasByteOrderMark, Is.True);
        Assert.That(format.NewLine, Is.EqualTo("\r\n"));
        Assert.That(TextFileCodec.Encode(text, format), Is.EqualTo(bytes));
    }

    [Test]
    public void Utf16LittleEndianRoundTripsAsUtf16()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("class C\r\n")).ToArray();

        var (text, format) = TextFileCodec.Decode(bytes);

        Assert.That(text, Is.EqualTo("class C\r\n"));
        Assert.That(format.Encoding, Is.EqualTo(TextFileEncoding.Utf16LittleEndian));
        Assert.That(format.HasByteOrderMark, Is.True);
        Assert.That(TextFileCodec.Encode(text, format), Is.EqualTo(bytes));
    }

    [Test]
    public void Utf16BigEndianRoundTripsAsUtf16()
    {
        var encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("class C\n")).ToArray();

        var (text, format) = TextFileCodec.Decode(bytes);

        Assert.That(text, Is.EqualTo("class C\n"));
        Assert.That(format.Encoding, Is.EqualTo(TextFileEncoding.Utf16BigEndian));
        Assert.That(TextFileCodec.Encode(text, format), Is.EqualTo(bytes));
    }

    /// <summary>
    /// Dominant, not first: a file edited on two platforms should keep the convention most of it
    /// uses rather than whichever one is at the top.
    /// </summary>
    [TestCase("a\r\nb\r\nc\r\n", "\r\n")]
    [TestCase("a\nb\nc\n", "\n")]
    [TestCase("a\nb\r\nc\r\nd\r\n", "\r\n")]
    [TestCase("a\r\nb\nc\nd\n", "\n")]
    [TestCase("no line endings at all", "\n")]
    public void TheDominantLineEndingWins(string text, string expected) =>
        Assert.That(TextFileCodec.DetectNewLine(text), Is.EqualTo(expected));

    /// <summary>
    /// C22: a single <c>newText</c> from Roslyn really does mix both, so normalising the file alone
    /// leaves a mixed-ending file behind.
    /// </summary>
    [TestCase("one\r\ntwo\nthree", "\r\n", "one\r\ntwo\r\nthree")]
    [TestCase("one\r\ntwo\nthree", "\n", "one\ntwo\nthree")]
    [TestCase("one\rtwo", "\n", "one\ntwo")]
    [TestCase("no endings", "\r\n", "no endings")]
    [TestCase("", "\n", "")]
    public void IncomingTextIsNormalisedToTheFilesOwnEnding(string text, string newLine, string expected) =>
        Assert.That(TextFileCodec.NormalizeNewLines(text, newLine), Is.EqualTo(expected));

    [Test]
    public void AFileCreatedFromNothingIsUtf8WithoutAMark()
    {
        var format = TextFileFormat.CreatedFileDefault;

        Assert.That(format.Encoding, Is.EqualTo(TextFileEncoding.Utf8));
        Assert.That(format.HasByteOrderMark, Is.False);
        Assert.That(TextFileCodec.Encode(string.Empty, format), Is.Empty);
    }
}
