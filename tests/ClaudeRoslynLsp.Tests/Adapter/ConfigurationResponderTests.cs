using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

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
    [Theory]
    [InlineData("csharp|background_analysis.dotnet_analyzer_diagnostics_scope", "\"openFiles\"")]
    [InlineData("csharp|background_analysis.dotnet_compiler_diagnostics_scope", "\"openFiles\"")]
    [InlineData("projects.dotnet_enable_automatic_restore", "true")]
    [InlineData("csharp|symbol_search.dotnet_search_reference_assemblies", "false")]
    [InlineData("navigation.dotnet_navigate_to_decompiled_sources", "true")]
    [InlineData("csharp|auto_insert.dotnet_enable_auto_insert", "false")]
    public void TheAgentTunedDefaultsAreWhatAnUnconfiguredSessionAnswers(string section, string expected)
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);

        Assert.Equal(expected, Answer(responder, section));
    }

    /// <summary>
    /// Inlay hints and code lens are answered <c>false</c> by prefix, not by enumeration: Roslyn adds
    /// hint kinds between versions, and a list that was complete on the pinned build would quietly
    /// stop being complete on the next — turning back on a feature this adapter does not carry (C26).
    /// </summary>
    [Theory]
    [InlineData("csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters")]
    [InlineData("csharp|inlay_hints.csharp_enable_inlay_hints_for_collection_expressions")]
    [InlineData("csharp|inlay_hints.a_kind_that_does_not_exist_yet")]
    [InlineData("csharp|code_lens.dotnet_enable_references_code_lens")]
    [InlineData("csharp|code_lens.dotnet_enable_tests_code_lens")]
    public void EveryInlayHintAndCodeLensSectionIsSwitchedOff(string section)
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);

        Assert.Equal("false", Answer(responder, section));
    }

    /// <summary>
    /// A section nobody has an opinion about is answered <c>null</c>, which is how Roslyn is told to
    /// use its own default. Answering something invented would be worse than answering nothing.
    /// </summary>
    [Theory]
    [InlineData("csharp|completion.dotnet_provide_regex_completions")]
    [InlineData("razor.format.attribute_indent_style")]
    [InlineData("something.entirely.new")]
    [InlineData("")]
    public void AnUnknownSectionFallsThroughToNull(string section)
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);

        Assert.Equal("null", Answer(responder, section));
    }

    /// <summary>
    /// The whole real request, answered: every section the pinned server asks for produces valid JSON,
    /// the intended values land on the six sections that have them, the two families are off, and
    /// everything else is <c>null</c>.
    /// </summary>
    [Fact]
    public void AllEightyRealSectionsProduceTheIntendedAnswers()
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);
        var sections = FakeRoslynScript.AllConfigurationSections;

        Assert.Equal(80, sections.Count);

        var answers = sections.ToDictionary(x => x, x => Answer(responder, x), StringComparer.Ordinal);

        Assert.Equal("\"openFiles\"", answers["csharp|background_analysis.dotnet_compiler_diagnostics_scope"]);
        Assert.Equal("\"openFiles\"", answers["csharp|background_analysis.dotnet_analyzer_diagnostics_scope"]);
        Assert.Equal("true", answers["projects.dotnet_enable_automatic_restore"]);
        Assert.Equal("true", answers["navigation.dotnet_navigate_to_decompiled_sources"]);
        Assert.Equal("false", answers["csharp|symbol_search.dotnet_search_reference_assemblies"]);
        Assert.Equal("false", answers["csharp|auto_insert.dotnet_enable_auto_insert"]);

        Assert.All(
            sections.Where(x => x.StartsWith("csharp|inlay_hints.", StringComparison.Ordinal)
                                || x.StartsWith("csharp|code_lens.", StringComparison.Ordinal)),
            section => Assert.Equal("false", answers[section]));

        // The Visual Basic twins are deliberately left alone: the adapter serves C# and answering
        // for a language it does not carry would be an opinion it has no basis for.
        Assert.Equal("null", answers["visual_basic|inlay_hints.dotnet_enable_inlay_hints_for_parameters"]);
        Assert.Equal("null", answers["visual_basic|background_analysis.dotnet_compiler_diagnostics_scope"]);

        // The six named values plus the two off-by-prefix families; everything else is null.
        var nonNull = answers.Count(x => x.Value != "null");
        var families = sections.Count(x => x.StartsWith("csharp|inlay_hints.", StringComparison.Ordinal)
                                           || x.StartsWith("csharp|code_lens.", StringComparison.Ordinal));

        Assert.Equal(families + 6, nonNull);
    }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_OPTIONS</c> beats the adapter's defaults, because it exists for the client
    /// that has no settings channel at all.
    /// </summary>
    [Fact]
    public void TheEnvironmentOverrideBeatsTheDefaults()
    {
        var responder = new ConfigurationResponder(
            """{"csharp|background_analysis.dotnet_compiler_diagnostics_scope":"fullSolution","projects.dotnet_enable_automatic_restore":false}""",
            NullLogger.Instance);

        Assert.Equal(2, responder.EnvironmentSectionCount);
        Assert.Equal("\"fullSolution\"", Answer(responder, "csharp|background_analysis.dotnet_compiler_diagnostics_scope"));
        Assert.Equal("false", Answer(responder, "projects.dotnet_enable_automatic_restore"));

        // And the defaults still apply to everything it did not name.
        Assert.Equal("\"openFiles\"", Answer(responder, "csharp|background_analysis.dotnet_analyzer_diagnostics_scope"));
    }

    /// <summary>The client's own settings beat both: a value a user wrote down was meant.</summary>
    [Fact]
    public void TheClientsSettingsBeatTheEnvironmentAndTheDefaults()
    {
        var responder = new ConfigurationResponder(
            """{"navigation.dotnet_navigate_to_decompiled_sources":false}""",
            NullLogger.Instance);

        responder.UpdateClientSettings(Element("""
            {"settings":{"roslyn":{"navigation.dotnet_navigate_to_decompiled_sources":"from-client","csharp|code_lens.dotnet_enable_tests_code_lens":true}}}
            """));

        Assert.Equal("\"from-client\"", Answer(responder, "navigation.dotnet_navigate_to_decompiled_sources"));
        Assert.Equal("true", Answer(responder, "csharp|code_lens.dotnet_enable_tests_code_lens"));
    }

    /// <summary>
    /// Both spellings are accepted, because both are in circulation and a configuration block that is
    /// silently ignored is the one bug a user cannot diagnose.
    /// </summary>
    [Theory]
    [InlineData("""{"settings":{"roslyn":{"projects.dotnet_binary_log_path":"/tmp/msbuild.binlog"}}}""")]
    [InlineData("""{"roslyn":{"projects.dotnet_binary_log_path":"/tmp/msbuild.binlog"}}""")]
    [InlineData("""{"projects.dotnet_binary_log_path":"/tmp/msbuild.binlog"}""")]
    public void ASettingsBlockIsFoundAtAnyOfItsThreeSpellings(string json)
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);
        responder.UpdateClientSettings(Element(json));

        Assert.Equal("\"/tmp/msbuild.binlog\"", Answer(responder, "projects.dotnet_binary_log_path"));
    }

    /// <summary>
    /// A malformed override is a logged warning, never a startup failure (D10): stdout is the protocol
    /// channel, so a server that refuses to start leaves its client with a dead process and nowhere
    /// to read the reason.
    /// </summary>
    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    public void AMalformedOverrideIsIgnoredRatherThanFatal(string optionsJson)
    {
        var responder = new ConfigurationResponder(optionsJson, NullLogger.Instance);

        Assert.Equal(0, responder.EnvironmentSectionCount);
        Assert.Equal("true", Answer(responder, "projects.dotnet_enable_automatic_restore"));
    }

    /// <summary>
    /// The answer array has to be the same length as the request's item list and in the same order —
    /// the protocol correlates them by position and nothing else.
    /// </summary>
    [Fact]
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

        Assert.Equal(7, root.GetProperty("id").GetInt32());

        var result = root.GetProperty("result");

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(3, result.GetArrayLength());
        Assert.Equal(JsonValueKind.True, result[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, result[1].ValueKind);
        Assert.Equal(JsonValueKind.False, result[2].ValueKind);
    }

    [Fact]
    public void ARequestWithNoItemsAnswersAnEmptyArray()
    {
        var responder = new ConfigurationResponder(optionsJson: null, NullLogger.Instance);

        using var document = JsonDocument.Parse(responder.BuildResponse("1"u8, parameters: null));

        Assert.Equal(0, document.RootElement.GetProperty("result").GetArrayLength());
    }

    private static string Answer(ConfigurationResponder responder, string section) =>
        Encoding.UTF8.GetString(responder.Answer(section).Span);

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
