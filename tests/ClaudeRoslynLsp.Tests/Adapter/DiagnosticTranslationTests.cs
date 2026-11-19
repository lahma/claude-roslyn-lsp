using System.Text.Json;

using ClaudeRoslynLsp.Adapter;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The filter that decides what an agent actually sees.
/// </summary>
/// <remarks>
/// Every rule here removes something that would otherwise crowd out something more important, and
/// each one is derived from a wire observation rather than from taste — which is why the cases are
/// written as the real shapes (C16's severities, positions and private tags) rather than as minimal
/// JSON.
/// </remarks>
public class DiagnosticTranslationTests
{
    /// <summary>The IDE0005 entry exactly as Roslyn sends it (C16).</summary>
    private const string Ide0005 = """
        {"range":{"start":{"line":0,"character":0},"end":{"line":1,"character":18}},"severity":4,
         "code":"IDE0005","source":"csharp","message":"Using directive is unnecessary.",
         "tags":[1,2147483645]}
        """;

    /// <summary>The CA1822 entry, at Information (C16).</summary>
    private const string Ca1822 = """
        {"range":{"start":{"line":20,"character":15},"end":{"line":20,"character":23}},"severity":3,
         "code":"CA1822","source":"csharp","message":"Member 'Describe' does not access instance data."}
        """;

    /// <summary>The CS0029 the fixture carries.</summary>
    private const string Cs0029 = """
        {"range":{"start":{"line":21,"character":16},"end":{"line":21,"character":19}},"severity":1,
         "code":"CS0029","source":"csharp","message":"Cannot implicitly convert type 'string' to 'int'"}
        """;

    /// <summary>A warning, for the floor cases.</summary>
    private const string Warning = """
        {"range":{"start":{"line":5,"character":0},"end":{"line":5,"character":4}},"severity":2,
         "code":"CS0168","source":"csharp","message":"The variable is declared but never used"}
        """;

    /// <summary>
    /// The default floor keeps errors and warnings and drops the rest. IDE0005 and CA1822 are
    /// exactly the entries an agent does not want and an IDE does.
    /// </summary>
    [Test]
    public void TheDefaultFloorKeepsErrorsAndWarningsOnly()
    {
        var kept = DiagnosticTranslation.Select(
            Items(Cs0029, Warning, Ca1822, Ide0005),
            DiagnosticTranslation.SeverityWarning);

        Assert.That(kept.Count, Is.EqualTo(2));
        Assert.That(CodeOf(kept[0]), Is.EqualTo("CS0029"));
        Assert.That(CodeOf(kept[1]), Is.EqualTo("CS0168"));
    }

    /// <summary>
    /// C16: an <c>Unnecessary</c>-tagged non-error goes even when the floor would have kept it,
    /// because it is reported at 0:0 for the whole using block and reads as "something is wrong at
    /// the top of this file".
    /// </summary>
    [Test]
    public void AnUnnecessaryTaggedNonErrorGoesEvenBelowTheFloor()
    {
        var kept = DiagnosticTranslation.Select(Items(Ide0005), DiagnosticTranslation.SeverityHint);

        Assert.That(kept, Is.Empty);
    }

    /// <summary>An <c>Unnecessary</c>-tagged <em>error</em> stays: that is a real one with a fade hint.</summary>
    [Test]
    public void AnUnnecessaryTaggedErrorStays()
    {
        const string unusedButFatal = """
            {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":5}},"severity":1,
             "code":"CS8602","message":"Dereference of a possibly null reference.","tags":[1]}
            """;

        var kept = DiagnosticTranslation.Select(Items(unusedButFatal), DiagnosticTranslation.SeverityWarning);

        Assert.That(kept, Has.Exactly(1).Items);
    }

    /// <summary>
    /// C16: tags at or above 2147483640 are Visual Studio's private values and must not be
    /// forwarded — several clients treat an unknown tag as a reason to drop the whole diagnostic.
    /// </summary>
    [Test]
    public void PrivateTagsAreStripped()
    {
        const string errorWithPrivateTags = """
            {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"severity":1,
             "code":"CS0029","message":"boom","tags":[1,2147483640,2147483645]}
            """;

        var published = Publish(Items(errorWithPrivateTags), DiagnosticTranslation.SeverityWarning);
        var tags = published[0].GetProperty("tags");

        Assert.That(tags.GetArrayLength(), Is.EqualTo(1));
        Assert.That(tags[0].GetInt32(), Is.EqualTo(1));
    }

    /// <summary>A diagnostic whose every tag was private loses the member rather than carrying an empty array.</summary>
    [Test]
    public void ATagsMemberThatEmptiesOutIsDroppedEntirely()
    {
        const string onlyPrivateTags = """
            {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"severity":1,
             "code":"CS0029","message":"boom","tags":[2147483641]}
            """;

        var published = Publish(Items(onlyPrivateTags), DiagnosticTranslation.SeverityWarning);

        Assert.That(published[0].TryGetProperty("tags", out _), Is.False);
    }

    /// <summary>Severity first, then line: the order somebody reading only the first few wants.</summary>
    [Test]
    public void SortingIsSeverityThenPosition()
    {
        const string lateError = """
            {"range":{"start":{"line":90,"character":0},"end":{"line":90,"character":1}},"severity":1,"code":"E2"}
            """;

        const string earlyWarning = """
            {"range":{"start":{"line":2,"character":0},"end":{"line":2,"character":1}},"severity":2,"code":"W1"}
            """;

        const string earlyError = """
            {"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":1}},"severity":1,"code":"E1"}
            """;

        var kept = DiagnosticTranslation.Select(
            Items(earlyWarning, lateError, earlyError),
            DiagnosticTranslation.SeverityWarning);

        Assert.That(kept.Select(CodeOf).ToArray(), Is.EqualTo(["E1", "E2", "W1"]));
    }

    /// <summary>Fifty per file, because the first fifty say what the next four hundred would.</summary>
    [Test]
    public void TheCapIsFiftyPerFile()
    {
        var many = Enumerable.Range(0, 200).Select(line => $$$"""
            {"range":{"start":{"line":{{{line}}},"character":0},"end":{"line":{{{line}}},"character":1}},
             "severity":1,"code":"CS0029"}
            """);

        var kept = DiagnosticTranslation.Select(
            Items([.. many]),
            DiagnosticTranslation.SeverityWarning);

        Assert.That(kept.Count, Is.EqualTo(DiagnosticTranslation.MaxPerFile));
    }

    /// <summary>
    /// The mirror version rides along, which is how a client discards a set an edit has overtaken.
    /// </summary>
    [Test]
    public void ThePublishCarriesTheVersionWhenThereIsOne()
    {
        var withVersion = Notification(DiagnosticTranslation.BuildPublish(
            "file:///w/A.cs", 7, Items(Cs0029), DiagnosticTranslation.SeverityWarning).Body);

        Assert.That(withVersion.GetProperty("method").GetString(), Is.EqualTo("textDocument/publishDiagnostics"));
        Assert.That(withVersion.GetProperty("params").GetProperty("uri").GetString(), Is.EqualTo("file:///w/A.cs"));
        Assert.That(withVersion.GetProperty("params").GetProperty("version").GetInt32(), Is.EqualTo(7));
    }

    /// <summary>
    /// And is omitted when there is none — a closed file has no mirror version, and a fabricated one
    /// would make the client discard the set.
    /// </summary>
    [Test]
    public void ThePublishOmitsTheVersionWhenThereIsNone()
    {
        var withoutVersion = Notification(DiagnosticTranslation.BuildPublish(
            "file:///w/A.cs", null, Items(Cs0029), DiagnosticTranslation.SeverityWarning).Body);

        Assert.That(withoutVersion.GetProperty("params").TryGetProperty("version", out _), Is.False);
    }

    /// <summary>A report with nothing in it still publishes, which is how a fixed file is cleared.</summary>
    [Test]
    public void AnEmptyReportStillPublishesAnEmptyArray()
    {
        var (body, count) = DiagnosticTranslation.BuildPublish(
            "file:///w/A.cs", null, default, DiagnosticTranslation.SeverityWarning);

        Assert.That(count, Is.EqualTo(0));
        Assert.That(Notification(body).GetProperty("params").GetProperty("diagnostics").GetArrayLength(), Is.EqualTo(0));
    }

    /// <summary>A diagnostic with no severity is kept, because hiding an unclassified one is worse.</summary>
    [Test]
    public void ADiagnosticWithNoSeverityIsTreatedAsAnError()
    {
        const string noSeverity = """
            {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"?"}
            """;

        Assert.That(DiagnosticTranslation.Select(Items(noSeverity), DiagnosticTranslation.SeverityError), Has.Exactly(1).Items);
    }

    /// <summary>The floor is spelled with names, and an unrecognised value falls back rather than failing.</summary>
    [TestCase(null, DiagnosticTranslation.SeverityWarning)]
    [TestCase("error", DiagnosticTranslation.SeverityError)]
    [TestCase("ERRORS", DiagnosticTranslation.SeverityError)]
    [TestCase("warning", DiagnosticTranslation.SeverityWarning)]
    [TestCase("information", DiagnosticTranslation.SeverityInformation)]
    [TestCase("hint", DiagnosticTranslation.SeverityHint)]
    [TestCase("all", DiagnosticTranslation.SeverityHint)]
    [TestCase("3", DiagnosticTranslation.SeverityInformation)]
    [TestCase("banana", DiagnosticTranslation.SeverityWarning)]
    public void TheSeverityFloorIsParsedByName(string? value, int expected) =>
        Assert.That(DiagnosticTranslation.ParseSeverityFloor(value, NullLogger.Instance), Is.EqualTo(expected));

    /// <summary>Everything a diagnostic carried that this class does not understand crosses verbatim.</summary>
    [Test]
    public void OpaqueMembersCrossUnchanged()
    {
        const string withData = """
            {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"severity":1,
             "code":"CS0029","data":{"UniqueIdentifier":"x","Nested":[1,2,{"a":null}]},
             "codeDescription":{"href":"https://example/CS0029"}}
            """;

        var published = Publish(Items(withData), DiagnosticTranslation.SeverityWarning);

        Assert.That(published[0].GetProperty("data").GetProperty("UniqueIdentifier").GetString(), Is.EqualTo("x"));
        Assert.That(published[0].GetProperty("data").GetProperty("Nested").GetArrayLength(), Is.EqualTo(3));
        Assert.That(
            published[0].GetProperty("codeDescription").GetProperty("href").GetString(),
            Is.EqualTo("https://example/CS0029"));
    }

    private static JsonElement Items(params string[] diagnostics) =>
        JsonDocument.Parse("[" + string.Join(",", diagnostics) + "]").RootElement.Clone();

    private static JsonElement Notification(byte[] body) => JsonDocument.Parse(body).RootElement.Clone();

    private static JsonElement Publish(JsonElement items, int floor)
    {
        var body = DiagnosticTranslation.BuildPublish("file:///w/A.cs", null, items, floor).Body;
        return Notification(body).GetProperty("params").GetProperty("diagnostics");
    }

    private static string CodeOf(JsonElement diagnostic) => diagnostic.GetProperty("code").GetString()!;
}
