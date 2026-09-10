using System.Text.Json.Serialization;

namespace ClaudeRoslynLsp.Mcp.Engine;

/// <summary>
/// The source-generated serializer contract for the LSP shapes the MCP half exchanges with Roslyn.
/// </summary>
/// <remarks>
/// <para>
/// A third context, and deliberately so (D60). D7's rule is that the tool vocabulary and an LSP
/// vocabulary must never share a resolver chain, because the leak would look like a working
/// feature — it is a rule about <em>chaining</em>, not about counting. This context is chained with
/// nothing: <c>Mcp/Models/RoslynToolJsonContext</c> answers for what a model reads, and this one
/// answers for what Roslyn is told, and no code path reaches both through one
/// <c>JsonSerializerOptions</c>.
/// </para>
/// <para>
/// It is separate from <c>Protocol/LspJsonContext</c> for a different reason: that context serves the
/// <em>mediation</em>, which forwards bytes and models only the handful of messages it has to
/// intercept (D47). The MCP half is a real LSP client and has to understand every field of every
/// answer it gets. Merging them would make one file the union of two jobs and give WP4 and WP5 the
/// same file to edit.
/// </para>
/// <para>
/// No naming policy is set: every member carries an explicit <c>[JsonPropertyName]</c>, because
/// these are somebody else's names and a policy that guessed them right today could guess them
/// wrong after a pin bump.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LspPosition))]
[JsonSerializable(typeof(LspRange))]
[JsonSerializable(typeof(LspLocation))]
[JsonSerializable(typeof(LspLocation[]))]
[JsonSerializable(typeof(LspTextEdit))]
[JsonSerializable(typeof(LspTextEdit[]))]
[JsonSerializable(typeof(WorkspaceEdit))]
[JsonSerializable(typeof(DocumentChange))]
[JsonSerializable(typeof(RawDiagnostic))]
[JsonSerializable(typeof(RawDiagnostic[]))]
[JsonSerializable(typeof(DocumentDiagnosticReport))]
[JsonSerializable(typeof(WorkspaceDiagnosticReport))]
[JsonSerializable(typeof(RawCodeAction))]
[JsonSerializable(typeof(RawCodeAction[]))]
[JsonSerializable(typeof(SymbolInformation))]
[JsonSerializable(typeof(SymbolInformation[]))]
[JsonSerializable(typeof(DocumentSymbolNode))]
[JsonSerializable(typeof(DocumentSymbolNode[]))]
internal sealed partial class RoslynEngineJsonContext : JsonSerializerContext;
