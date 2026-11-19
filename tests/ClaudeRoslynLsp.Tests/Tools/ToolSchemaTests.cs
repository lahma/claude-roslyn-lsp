using System.Reflection;
using System.Text.Json;

using ClaudeRoslynLsp.Mcp;
using ClaudeRoslynLsp.Mcp.Models;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Tools;

/// <summary>
/// The JSON schemas an MCP client receives from <c>tools/list</c>, generated with the server's own
/// serializer options.
/// </summary>
/// <remarks>
/// A schema is the only description of a tool the model ever sees, so the failures worth catching
/// here are the silent ones: the injected context leaking in as a required argument the model then
/// has to invent, a parameter losing its description, or the naming policy slipping so that the
/// schema says <c>MaxResults</c> while the tool binds <c>maxResults</c>.
/// </remarks>
public class ToolSchemaTests
{
    /// <summary>Parameter names the SDK must never expose: they are bound from DI or the protocol.</summary>
    private static readonly string[] NeverInSchema = ["context", "cancellationToken"];

    /// <summary>
    /// The schemas above are only the shipped ones if the options that produced them are the shipped
    /// ones — our context first, the SDK's resolver second, and the whole thing frozen.
    /// </summary>
    [Test]
    public void ProductionSerializerOptionsPutOurContextFirstAndAreReadOnly()
    {
        var options = McpServerSetup.CreateToolSerializerOptions();

        Assert.That(options.IsReadOnly, Is.True);
        Assert.That(options.TypeInfoResolverChain.Count, Is.EqualTo(2));
        Assert.That(options.TypeInfoResolverChain[0], Is.TypeOf<RoslynToolJsonContext>());
    }

    /// <summary>
    /// The frozen schema table. The property list is ordered exactly as the parameters are declared,
    /// because that order is what a model reads top to bottom; the required list is what it must
    /// supply.
    /// </summary>
    [TestCase("getWorkspaceStatus", "", "")]
    [TestCase("resolveSymbol", "symbol,kind,maxResults", "symbol")]
    [TestCase("getTypeMembers", "type,maxResults", "type")]
    [TestCase("findReferences", "symbol,includeDeclaration,maxResults,offset", "symbol")]
    [TestCase(
        "getDiagnostics",
        "scope,path,project,minSeverity,includeAnalyzers,ids,maxResults",
        "")]
    [TestCase("getCodeActions", "path,line,col,endLine,endCol,kind", "path,line,col")]
    [TestCase(
        "applyCodeAction",
        "path,line,col,id,title,endLine,endCol,fixAllScope,preview",
        "path,line,col")]
    [TestCase("renameSymbol", "symbol,newName,preview", "symbol,newName")]
    [TestCase("fixDiagnostics", "diagnosticId,scope,path,project,preview", "diagnosticId")]
    [TestCase("formatCode", "paths,project,organizeUsings,preview", "")]
    public void InputSchemaExposesExactlyTheModelSuppliedArguments(string name, string properties, string required)
    {
        var schema = ToolTestHost.Find(name).ProtocolTool.InputSchema;

        Assert.That(schema.GetProperty("type").GetString(), Is.EqualTo("object"));

        var actualProperties = schema.TryGetProperty("properties", out var propertiesElement)
            ? propertiesElement.EnumerateObject().Select(property => property.Name).ToArray()
            : [];

        Assert.That(actualProperties, Is.EqualTo(Split(properties)));

        var actualRequired = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [];

        Assert.That(actualRequired, Is.EqualTo(Split(required)));
    }

    /// <summary>
    /// The table above must cover every tool. Without this a tool added without a row would have no
    /// frozen schema at all, which is the one thing the table exists to prevent.
    /// </summary>
    [Test]
    public void EveryToolHasARowInTheFrozenSchemaTable()
    {
        // The tool name is each row's first argument, which NUnit exposes as TestCaseAttribute.Arguments
        // whichever of its constructors the row bound to.
        var covered = typeof(ToolSchemaTests)
            .GetMethod(nameof(InputSchemaExposesExactlyTheModelSuppliedArguments))!
            .GetCustomAttributes<TestCaseAttribute>()
            .Select(row => (string) row.Arguments[0]!)
            .ToArray();

        Assert.That(
            covered.Order(StringComparer.Ordinal),
            Is.EqualTo(ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The recycle is visible in the answer a model reads (D96): without these a call after a quiet
    /// spell is slow for no reason anybody can see.
    /// </summary>
    [Test]
    public void TheWorkspaceStatusCarriesTheIdlePolicysFields()
    {
        var properties = ToolTestHost.Find("getWorkspaceStatus").ProtocolTool.OutputSchema!.Value.GetProperty("properties");

        foreach (var name in new[] { "memoryMegabytes", "privateMegabytes", "recycles", "baselineMegabytes", "lastRecycleAt" })
        {
            Assert.That(properties.TryGetProperty(name, out _), Is.True, $"getWorkspaceStatus has no '{name}'.");
        }
    }

    [Test]
    public void NoInputSchemaMentionsAnInjectedParameter()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            if (!tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                Assert.That(
                    NeverInSchema,
                    Has.None.EqualTo(property.Name).Using(StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    [Test]
    public void EveryInputPropertyIsCamelCaseAndDescribed()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var toolName = tool.ProtocolTool.Name;

            if (!tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                Assert.That(IsCamelCase(property.Name), Is.True, $"{toolName}.{property.Name} is not camelCase.");

                var described = property.Value.TryGetProperty("description", out var description)
                    && !string.IsNullOrWhiteSpace(description.GetString());

                Assert.That(described, Is.True, $"{toolName}.{property.Name} has no schema description.");
            }
        }
    }

    /// <summary>
    /// Output properties carry no description, and that is not an oversight: the exporter reads
    /// <c>[Description]</c>, which the result records deliberately do not have — their meaning is
    /// documented in the tool description, where a model reads it once instead of per property. So the
    /// assertion here is the naming policy alone.
    /// </summary>
    [Test]
    public void EveryToolHasAnObjectOutputSchemaInCamelCase()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var outputSchema = tool.ProtocolTool.OutputSchema;

            Assert.That(outputSchema, Is.Not.Null);
            Assert.That(outputSchema!.Value.GetProperty("type").GetString(), Is.EqualTo("object"));

            AssertCamelCaseProperties(outputSchema.Value, tool.ProtocolTool.Name);
        }
    }

    /// <summary>
    /// Every result a caller has to branch on carries <c>status</c>, in the same place, so "is this a
    /// real answer" is one property lookup rather than ten shapes (D66).
    /// </summary>
    [Test]
    public void EveryOutputSchemaCarriesAStatus()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var properties = tool.ProtocolTool.OutputSchema!.Value.GetProperty("properties");

            Assert.That(
                properties.TryGetProperty("status", out _), Is.True,
                $"{tool.ProtocolTool.Name}'s result has no status; a caller cannot tell 'loading' from an answer.");
        }
    }

    /// <summary>
    /// Every mutating tool answers with the same shape, so a caller reads <c>applied</c> and
    /// <c>files</c> once and not four times.
    /// </summary>
    [TestCase("applyCodeAction")]
    [TestCase("renameSymbol")]
    [TestCase("fixDiagnostics")]
    [TestCase("formatCode")]
    public void EveryMutatingToolReturnsTheSameEditResultShape(string name)
    {
        Assert.That(ToolTestHost.FindMethod(name).ReturnType.GetGenericArguments()[0], Is.EqualTo(typeof(EditResult)));

        var properties = ToolTestHost.Find(name).ProtocolTool.OutputSchema!.Value.GetProperty("properties");

        foreach (var expected in new[] { "applied", "filesChanged", "edits", "files", "diff", "note" })
        {
            Assert.That(properties.TryGetProperty(expected, out _), Is.True, $"{name}'s result has no '{expected}'.");
        }
    }

    /// <summary>
    /// The documentation of the 1-based convention has to reach the model, and the only channel it
    /// has is the schema description of the parameter it applies to.
    /// </summary>
    [TestCase("getCodeActions", "line")]
    [TestCase("getCodeActions", "col")]
    [TestCase("getCodeActions", "endLine")]
    [TestCase("getCodeActions", "endCol")]
    [TestCase("applyCodeAction", "line")]
    [TestCase("applyCodeAction", "col")]
    [TestCase("resolveSymbol", "symbol")]
    [TestCase("renameSymbol", "symbol")]
    [TestCase("findReferences", "symbol")]
    [TestCase("getTypeMembers", "type")]
    public void EveryPositionArgumentSaysItIsOneBased(string tool, string property)
    {
        var description = ToolTestHost.Find(tool)
            .ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty(property)
            .GetProperty("description")
            .GetString();

        Assert.That(description, Contains.Substring("1-based").Using(StringComparison.Ordinal));
    }

    private static string[] Split(string value) => value.Length == 0 ? [] : value.Split(',');

    private static void AssertCamelCaseProperties(JsonElement schema, string toolName)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                Assert.That(
                    IsCamelCase(property.Name), Is.True,
                    $"{toolName} output property '{property.Name}' is not camelCase.");

                AssertCamelCaseProperties(property.Value, toolName);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            AssertCamelCaseProperties(items, toolName);
        }
    }

    /// <summary>
    /// Hand-rolled rather than a regex: the rule is small, and the failure message matters more than
    /// the pattern.
    /// </summary>
    private static bool IsCamelCase(string name)
    {
        if (name.Length == 0 || !char.IsLower(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
