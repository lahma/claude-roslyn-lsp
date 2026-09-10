using System.Text.Json.Serialization;

namespace ClaudeRoslynLsp.Protocol;

/// <summary>
/// The source-generated serializer contract for every LSP message this adapter reads or writes
/// itself.
/// </summary>
/// <remarks>
/// <para>
/// Source-generated rather than reflective, and the product is published with
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c>, so a type that is not declared here fails
/// at the first serialisation attempt under <c>dotnet test</c> — not only after a Native AOT publish,
/// where the same mistake would surface as a released binary that answers nothing.
/// </para>
/// <para>
/// There is no naming policy: every property carries an explicit <see cref="JsonPropertyNameAttribute"/>.
/// These are wire shapes defined by somebody else's specification, and a policy that happened to
/// produce the right names would still be a policy — one rename away from producing wrong ones
/// silently.
/// </para>
/// <para>
/// Deliberately small next to the protocol it carries, and it stays that way. The mediation design
/// (D1) forwards everything that is not a handshake, registration, configuration, progress or
/// diagnostics message as raw bytes with only the JSON-RPC id token rewritten
/// (<see cref="LspMessageScanner"/>), so the set of shapes this adapter has to <em>understand</em>
/// is a fraction of the set it has to <em>carry</em>. Anything whose payload is somebody else's —
/// a configuration answer, a registration's options, a progress token — is a
/// <see cref="System.Text.Json.JsonElement"/> here and a raw span on the wire.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(InitializeResponse))]
[JsonSerializable(typeof(ClientInitializeParams))]
[JsonSerializable(typeof(RawParamsNotification))]
[JsonSerializable(typeof(RawParamsRequest))]
[JsonSerializable(typeof(DidOpenNotification))]
[JsonSerializable(typeof(DidOpenParams))]
[JsonSerializable(typeof(DidChangeParams))]
[JsonSerializable(typeof(TextDocumentParams))]
[JsonSerializable(typeof(RegistrationParams))]
[JsonSerializable(typeof(UnregistrationParams))]
[JsonSerializable(typeof(ConfigurationParams))]
[JsonSerializable(typeof(WorkDoneProgressCreateParams))]
[JsonSerializable(typeof(ProgressParams))]
[JsonSerializable(typeof(LogMessageParams))]
[JsonSerializable(typeof(LogMessageNotification))]
[JsonSerializable(typeof(ApplyWorkspaceEditResult))]

[JsonSerializable(typeof(SolutionOpenNotification))]
[JsonSerializable(typeof(ProjectOpenNotification))]
[JsonSerializable(typeof(RoslynInitializeParams))]
[JsonSerializable(typeof(WorkspaceFolder))]
internal sealed partial class LspJsonContext : JsonSerializerContext;
