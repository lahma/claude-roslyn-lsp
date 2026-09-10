using System.Reflection;
using System.Text.Json;

using ClaudeRoslynLsp.Mcp;
using ClaudeRoslynLsp.Mcp.Models;

using Xunit;

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
    [Fact]
    public void ProductionSerializerOptionsPutOurContextFirstAndAreReadOnly()
    {
        var options = McpServerSetup.CreateToolSerializerOptions();

        Assert.True(options.IsReadOnly);
        Assert.Equal(2, options.TypeInfoResolverChain.Count);
        Assert.IsType<RoslynToolJsonContext>(options.TypeInfoResolverChain[0]);
    }

    /// <summary>
    /// The frozen schema table. The property list is ordered exactly as the parameters are declared,
    /// because that order is what a model reads top to bottom; the required list is what it must
    /// supply.
    /// </summary>
    [Theory]
    [InlineData("getWorkspaceStatus", "", "")]
    [InlineData("resolveSymbol", "symbol,kind,maxResults", "symbol")]
    [InlineData("getTypeMembers", "type,maxResults", "type")]
    [InlineData("findReferences", "symbol,includeDeclaration,maxResults,offset", "symbol")]
    [InlineData(
        "getDiagnostics",
        "scope,path,project,minSeverity,includeAnalyzers,ids,maxResults",
        "")]
    [InlineData("getCodeActions", "path,line,col,endLine,endCol,kind", "path,line,col")]
    [InlineData(
        "applyCodeAction",
        "path,line,col,id,title,endLine,endCol,fixAllScope,preview",
        "path,line,col")]
    [InlineData("renameSymbol", "symbol,newName,preview", "symbol,newName")]
    [InlineData("fixDiagnostics", "diagnosticId,scope,path,project,preview", "diagnosticId")]
    [InlineData("formatCode", "paths,project,organizeUsings,preview", "")]
    public void InputSchemaExposesExactlyTheModelSuppliedArguments(string name, string properties, string required)
    {
        var schema = ToolTestHost.Find(name).ProtocolTool.InputSchema;

        Assert.Equal("object", schema.GetProperty("type").GetString());

        var actualProperties = schema.TryGetProperty("properties", out var propertiesElement)
            ? propertiesElement.EnumerateObject().Select(property => property.Name).ToArray()
            : [];

        Assert.Equal(Split(properties), actualProperties);

        var actualRequired = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [];

        Assert.Equal(Split(required), actualRequired);
    }

    /// <summary>
    /// The table above must cover every tool. Without this a tool added without a row would have no
    /// frozen schema at all, which is the one thing the table exists to prevent.
    /// </summary>
    [Fact]
    public void EveryToolHasARowInTheFrozenSchemaTable()
    {
        // Read through CustomAttributeData rather than by instantiating the attribute: xunit's
        // InlineDataAttribute.GetData signature is not a stable thing to depend on, and the tool name
        // is simply the first constructor argument.
        var covered = typeof(ToolSchemaTests)
            .GetMethod(nameof(InputSchemaExposesExactlyTheModelSuppliedArguments))!
            .GetCustomAttributesData()
            .Where(data => data.AttributeType == typeof(InlineDataAttribute))
            .Select(data => (string) ((IReadOnlyList<CustomAttributeTypedArgument>) data.ConstructorArguments[0].Value!)[0].Value!)
            .ToArray();

        Assert.Equal(
            ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal),
            covered.Order(StringComparer.Ordinal));
    }

    [Fact]
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
                Assert.DoesNotContain(property.Name, NeverInSchema, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
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
                Assert.True(IsCamelCase(property.Name), $"{toolName}.{property.Name} is not camelCase.");

                var described = property.Value.TryGetProperty("description", out var description)
                    && !string.IsNullOrWhiteSpace(description.GetString());

                Assert.True(described, $"{toolName}.{property.Name} has no schema description.");
            }
        }
    }

    /// <summary>
    /// Output properties carry no description, and that is not an oversight: the exporter reads
    /// <c>[Description]</c>, which the result records deliberately do not have — their meaning is
    /// documented in the tool description, where a model reads it once instead of per property. So the
    /// assertion here is the naming policy alone.
    /// </summary>
    [Fact]
    public void EveryToolHasAnObjectOutputSchemaInCamelCase()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var outputSchema = tool.ProtocolTool.OutputSchema;

            Assert.NotNull(outputSchema);
            Assert.Equal("object", outputSchema.Value.GetProperty("type").GetString());

            AssertCamelCaseProperties(outputSchema.Value, tool.ProtocolTool.Name);
        }
    }

    /// <summary>
    /// Every result a caller has to branch on carries <c>status</c>, in the same place, so "is this a
    /// real answer" is one property lookup rather than ten shapes (D66).
    /// </summary>
    [Fact]
    public void EveryOutputSchemaCarriesAStatus()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var properties = tool.ProtocolTool.OutputSchema!.Value.GetProperty("properties");

            Assert.True(
                properties.TryGetProperty("status", out _),
                $"{tool.ProtocolTool.Name}'s result has no status; a caller cannot tell 'loading' from an answer.");
        }
    }

    /// <summary>
    /// Every mutating tool answers with the same shape, so a caller reads <c>applied</c> and
    /// <c>files</c> once and not four times.
    /// </summary>
    [Theory]
    [InlineData("applyCodeAction")]
    [InlineData("renameSymbol")]
    [InlineData("fixDiagnostics")]
    [InlineData("formatCode")]
    public void EveryMutatingToolReturnsTheSameEditResultShape(string name)
    {
        Assert.Equal(typeof(EditResult), ToolTestHost.FindMethod(name).ReturnType.GetGenericArguments()[0]);

        var properties = ToolTestHost.Find(name).ProtocolTool.OutputSchema!.Value.GetProperty("properties");

        foreach (var expected in new[] { "applied", "filesChanged", "edits", "files", "diff", "note" })
        {
            Assert.True(properties.TryGetProperty(expected, out _), $"{name}'s result has no '{expected}'.");
        }
    }

    /// <summary>
    /// The documentation of the 1-based convention has to reach the model, and the only channel it
    /// has is the schema description of the parameter it applies to.
    /// </summary>
    [Theory]
    [InlineData("getCodeActions", "line")]
    [InlineData("getCodeActions", "col")]
    [InlineData("getCodeActions", "endLine")]
    [InlineData("getCodeActions", "endCol")]
    [InlineData("applyCodeAction", "line")]
    [InlineData("applyCodeAction", "col")]
    [InlineData("resolveSymbol", "symbol")]
    [InlineData("renameSymbol", "symbol")]
    [InlineData("findReferences", "symbol")]
    [InlineData("getTypeMembers", "type")]
    public void EveryPositionArgumentSaysItIsOneBased(string tool, string property)
    {
        var description = ToolTestHost.Find(tool)
            .ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty(property)
            .GetProperty("description")
            .GetString();

        Assert.Contains("1-based", description, StringComparison.Ordinal);
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
                Assert.True(
                    IsCamelCase(property.Name),
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
