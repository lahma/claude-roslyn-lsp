using System.Text.Json.Serialization;

namespace ClaudeRoslynLsp.Mcp.Models;

/// <summary>
/// The source-generated serializer contract for everything the tool layer hands back to an MCP
/// client — result records, and the primitive parameter types the SDK builds tool schemas from.
/// </summary>
/// <remarks>
/// <para>
/// This is the context that goes <b>first</b> in the MCP server's <c>TypeInfoResolverChain</c>, with
/// the SDK's own resolver second. First match wins, so ours answers for our types and falls through
/// for MCP protocol types — which is what makes JIT and Native AOT resolve identically instead of one
/// of them quietly reaching for reflection.
/// </para>
/// <para>
/// Deliberately separate from <see cref="ClaudeRoslynLsp.Protocol.LspJsonContext"/>, and the two are
/// never chained together. LSP shapes are somebody else's wire contract, spelled exactly as the
/// specification spells them; these are ours, in the vocabulary a model reads. A shared resolver
/// would let one leak into the other, and the leak would look like a working feature.
/// </para>
/// <para>
/// The primitive registrations are not decorative. With
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> the schema exporter can only describe a
/// parameter whose type some resolver in the chain knows, so every type used as a tool parameter has
/// to be resolvable — including the nullable value types, which no other context in the chain
/// declares.
/// </para>
/// <para>
/// The engine's LSP shapes are in a third context, <c>Mcp/Engine/RoslynEngineJsonContext</c>, which
/// is chained with neither of the other two (D60). Nothing from that vocabulary appears here: what a
/// model reads is this repository's own words, not Roslyn's.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]

// Tool parameter types, for schema generation.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(bool?))]

// Tool result types, for structured content and output schemas.
[JsonSerializable(typeof(WorkspaceStatusResult))]
[JsonSerializable(typeof(SymbolResolveResult))]
[JsonSerializable(typeof(TypeMembersResult))]
[JsonSerializable(typeof(ReferencesResult))]
[JsonSerializable(typeof(DiagnosticsResult))]
[JsonSerializable(typeof(CodeActionsResult))]
[JsonSerializable(typeof(EditResult))]
internal sealed partial class RoslynToolJsonContext : JsonSerializerContext;
