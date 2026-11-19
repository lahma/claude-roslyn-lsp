using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The answers Roslyn's eighty configuration sections get, and the order of precedence behind them.
/// </summary>
/// <remarks>
/// The whole set is exercised against the real section list the fake sends, because the failure this
/// guards against is a default that stops matching after a Roslyn bump renames a section: the section
/// would then fall through to <c>null</c>, Roslyn would use its own default, and the only symptom
/// would be a machine that got slower.
/// </remarks>
public class ConfigurationResponderTests
{
    /// <summary>The four choices that are not merely "leave it alone".</summary>
    [TestCase("csharp|background_analysis.dotnet_analyzer_diagnostics_scope", "\"openFiles\"")]
    [TestCase("csharp|background_analysis.dotnet_compiler_diagnostics_scope", "\"openFiles\"")]
    [TestCase("projects.dotnet_enable_automatic_restore", "true")]
    [TestCase("csharp|symbol_search.dotnet_search_reference_assemblies", "false")]
    [TestCase("navigation.dotnet_navigate_to_decompiled_sources", "true")]
    [TestCase("csharp|auto_insert.dotnet_enable_auto_insert", "false")]
    public void TheAgentTunedDefaultsAreWhatAnUnconfiguredSessionAnswers(string section, string expected)
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);

        Assert.That(Answer(responder, section), Is.EqualTo(expected));
    }

    /// <summary>
    /// Inlay hints and code lens are answered <c>false</c> by prefix, not by enumeration: Roslyn adds
    /// hint kinds between versions, and a list that was complete on the pinned build would quietly
    /// stop being complete on the next — turning back on a feature this adapter does not carry (C26).
    /// </summary>
    [TestCase("csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters")]
    [TestCase("csharp|inlay_hints.csharp_enable_inlay_hints_for_collection_expressions")]
    [TestCase("csharp|inlay_hints.a_kind_that_does_not_exist_yet")]
    [TestCase("csharp|code_lens.dotnet_enable_references_code_lens")]
    [TestCase("csharp|code_lens.dotnet_enable_tests_code_lens")]
    public void EveryInlayHintAndCodeLensSectionIsSwitchedOff(string section)
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);

        Assert.That(Answer(responder, section), Is.EqualTo("false"));
    }

    /// <summary>
    /// A section nobody has an opinion about is answered <c>null</c>, which is how Roslyn is told to
    /// use its own default. Answering something invented would be worse than answering nothing.
    /// </summary>
    [TestCase("csharp|completion.dotnet_provide_regex_completions")]
    [TestCase("razor.format.attribute_indent_style")]
    [TestCase("something.entirely.new")]
    [TestCase("")]
    public void AnUnknownSectionFallsThroughToNull(string section)
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);

        Assert.That(Answer(responder, section), Is.EqualTo("null"));
    }

    /// <summary>
    /// The whole real request, answered: every section the pinned server asks for produces valid JSON,
    /// the intended values land on the six sections that have them, the two families are off, and
    /// everything else is <c>null</c>.
    /// </summary>
    [Test]
    public void AllEightyRealSectionsProduceTheIntendedAnswers()
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);
        var sections = FakeRoslynScript.AllConfigurationSections;

        Assert.That(sections.Count, Is.EqualTo(80));

        var answers = sections.ToDictionary(x => x, x => Answer(responder, x), StringComparer.Ordinal);

        Assert.That(answers["csharp|background_analysis.dotnet_compiler_diagnostics_scope"], Is.EqualTo("\"openFiles\""));
        Assert.That(answers["csharp|background_analysis.dotnet_analyzer_diagnostics_scope"], Is.EqualTo("\"openFiles\""));
        Assert.That(answers["projects.dotnet_enable_automatic_restore"], Is.EqualTo("true"));
        Assert.That(answers["navigation.dotnet_navigate_to_decompiled_sources"], Is.EqualTo("true"));
        Assert.That(answers["csharp|symbol_search.dotnet_search_reference_assemblies"], Is.EqualTo("false"));
        Assert.That(answers["csharp|auto_insert.dotnet_enable_auto_insert"], Is.EqualTo("false"));

        foreach (var section in sections.Where(x => x.StartsWith("csharp|inlay_hints.", StringComparison.Ordinal)
                                                    || x.StartsWith("csharp|code_lens.", StringComparison.Ordinal)))
        {
            Assert.That(answers[section], Is.EqualTo("false"), section);
        }

        // The Visual Basic twins are deliberately left alone: the adapter serves C# and answering
        // for a language it does not carry would be an opinion it has no basis for.
        Assert.That(answers["visual_basic|inlay_hints.dotnet_enable_inlay_hints_for_parameters"], Is.EqualTo("null"));
        Assert.That(answers["visual_basic|background_analysis.dotnet_compiler_diagnostics_scope"], Is.EqualTo("null"));

        // The six named values plus the two off-by-prefix families; everything else is null.
        var nonNull = answers.Count(x => x.Value != "null");
        var families = sections.Count(x => x.StartsWith("csharp|inlay_hints.", StringComparison.Ordinal)
                                           || x.StartsWith("csharp|code_lens.", StringComparison.Ordinal));

        Assert.That(nonNull, Is.EqualTo(families + 6));
    }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_OPTIONS</c> beats the adapter's defaults, because it exists for the client
    /// that has no settings channel at all.
    /// </summary>
    [Test]
    public void TheEnvironmentOverrideBeatsTheDefaults()
    {
        var responder = new ConfigurationResponder(
            """{"csharp|background_analysis.dotnet_compiler_diagnostics_scope":"fullSolution","projects.dotnet_enable_automatic_restore":false}""",
            NullLogger.Instance);

        Assert.That(responder.EnvironmentSectionCount, Is.EqualTo(2));
        Assert.That(Answer(responder, "csharp|background_analysis.dotnet_compiler_diagnostics_scope"), Is.EqualTo("\"fullSolution\""));
        Assert.That(Answer(responder, "projects.dotnet_enable_automatic_restore"), Is.EqualTo("false"));

        // And the defaults still apply to everything it did not name.
        Assert.That(Answer(responder, "csharp|background_analysis.dotnet_analyzer_diagnostics_scope"), Is.EqualTo("\"openFiles\""));
    }

    /// <summary>The client's own settings beat both: a value a user wrote down was meant.</summary>
    [Test]
    public void TheClientsSettingsBeatTheEnvironmentAndTheDefaults()
    {
        var responder = new ConfigurationResponder(
            """{"navigation.dotnet_navigate_to_decompiled_sources":false}""",
            NullLogger.Instance);

        responder.UpdateClientSettings(Element("""
            {"settings":{"roslyn":{"navigation.dotnet_navigate_to_decompiled_sources":"from-client","csharp|code_lens.dotnet_enable_tests_code_lens":true}}}
            """));

        Assert.That(Answer(responder, "navigation.dotnet_navigate_to_decompiled_sources"), Is.EqualTo("\"from-client\""));
        Assert.That(Answer(responder, "csharp|code_lens.dotnet_enable_tests_code_lens"), Is.EqualTo("true"));
    }

    /// <summary>
    /// Both spellings are accepted, because both are in circulation and a configuration block that is
    /// silently ignored is the one bug a user cannot diagnose.
    /// </summary>
    [TestCase("""{"settings":{"roslyn":{"projects.dotnet_binary_log_path":"/tmp/msbuild.binlog"}}}""")]
    [TestCase("""{"roslyn":{"projects.dotnet_binary_log_path":"/tmp/msbuild.binlog"}}""")]
    [TestCase("""{"projects.dotnet_binary_log_path":"/tmp/msbuild.binlog"}""")]
    public void ASettingsBlockIsFoundAtAnyOfItsThreeSpellings(string json)
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);
        responder.UpdateClientSettings(Element(json));

        Assert.That(Answer(responder, "projects.dotnet_binary_log_path"), Is.EqualTo("\"/tmp/msbuild.binlog\""));
    }

    /// <summary>
    /// A malformed override is a logged warning, never a startup failure (D10): stdout is the protocol
    /// channel, so a server that refuses to start leaves its client with a dead process and nowhere
    /// to read the reason.
    /// </summary>
    [TestCase("{not json")]
    [TestCase("[1,2,3]")]
    [TestCase("\"a string\"")]
    public void AMalformedOverrideIsIgnoredRatherThanFatal(string optionsJson)
    {
        var responder = new ConfigurationResponder(optionsJson, NullLogger.Instance);

        Assert.That(responder.EnvironmentSectionCount, Is.EqualTo(0));
        Assert.That(Answer(responder, "projects.dotnet_enable_automatic_restore"), Is.EqualTo("true"));
    }

    /// <summary>
    /// The answer array has to be the same length as the request's item list and in the same order —
    /// the protocol correlates them by position and nothing else.
    /// </summary>
    [Test]
    public void TheResponseIsOneValuePerRequestedSectionInOrder()
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);

        var parameters = JsonSerializer.Deserialize(
            Encoding.UTF8.GetBytes("""
                {"items":[{"section":"projects.dotnet_enable_automatic_restore"},{"section":"unknown.section"},{"section":"csharp|code_lens.dotnet_enable_tests_code_lens"}]}
                """),
            LspJsonContext.Default.ConfigurationParams);

        using var document = JsonDocument.Parse(responder.BuildResponse("7"u8, parameters));
        var root = document.RootElement;

        Assert.That(root.GetProperty("id").GetInt32(), Is.EqualTo(7));

        var result = root.GetProperty("result");

        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(result.GetArrayLength(), Is.EqualTo(3));
        Assert.That(result[0].ValueKind, Is.EqualTo(JsonValueKind.True));
        Assert.That(result[1].ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(result[2].ValueKind, Is.EqualTo(JsonValueKind.False));
    }

    [Test]
    public void ARequestWithNoItemsAnswersAnEmptyArray()
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);

        using var document = JsonDocument.Parse(responder.BuildResponse("1"u8, parameters: null));

        Assert.That(document.RootElement.GetProperty("result").GetArrayLength(), Is.EqualTo(0));
    }

    private static string Answer(ConfigurationResponder responder, string section) =>
        Encoding.UTF8.GetString(responder.Answer(section).Span);

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
