using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// The few shapes the tests themselves serialise.
/// </summary>
/// <remarks>
/// The test project sets <c>JsonSerializerIsReflectionEnabledByDefault=false</c> alongside the
/// product (D6), so even a test helper that quotes a string for a JSON literal needs a contract. That
/// is the point of the setting: a missing <c>[JsonSerializable]</c> fails here, in a second, rather
/// than after an AOT publish.
/// </remarks>
[JsonSerializable(typeof(string))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

/// <summary>Shorthand for the contracts above.</summary>
internal static class TestJson
{
    /// <summary>The contract for a bare JSON string, used to quote text into a message literal.</summary>
    internal static JsonTypeInfo<string> String => TestJsonContext.Default.String;
}
