using System.Text.Json;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>
/// Refuses a <c>tools/call</c> whose argument names are not the ones the tool advertised, before the
/// tool runs (D109).
/// </summary>
/// <remarks>
/// <para>
/// The SDK binds arguments to parameters by exact name and ignores any it cannot place. A mis-cased
/// required argument (<c>Symbol</c>, <c>Path</c>) therefore ends as the SDK's bare "An error occurred
/// invoking 'resolveSymbol'." — nothing a model can correct from — and a mis-cased optional one is
/// dropped without a word. Here that is worse than a wrong answer: the four mutating tools take
/// <c>preview</c>, so <c>"Preview": true</c> would be dropped, and the call would <b>write to disk</b>
/// while the model believed it was looking at a diff (D62). Verified over stdio on ModelContextProtocol
/// 2.2.0 and 2.4.0 alike (sonarqube-mcp, 2026-11-18).
/// </para>
/// <para>
/// The names are checked against the input schema the client was given in <c>tools/list</c>, so the
/// rule is simply "send what was advertised". A case-only mismatch is refused rather than bound:
/// the boundary keeps one spelling, and the answer names it. The refusal is a tool error
/// (<c>isError</c>), not a protocol error, because it is something the model can fix and retry.
/// </para>
/// </remarks>
internal static class ToolArgumentNames
{
    /// <summary>The furthest edit distance at which a misspelled name still earns a suggestion.</summary>
    private const int MaxSuggestionDistance = 2;

    /// <summary>The call-tool filter that applies <see cref="Check"/> to every call of a known tool.</summary>
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            // A call to an unknown tool has no match, and the SDK's own answer to that is left alone.
            if (context.MatchedPrimitive is McpServerTool tool)
            {
                var problem = Check(tool.ProtocolTool, context.Params?.Arguments?.Keys ?? []);

                if (problem is not null)
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = problem }],
                    };
                }
            }

            return await next(context, cancellationToken).ConfigureAwait(false);
        };

    /// <summary>
    /// What is wrong with these argument names for this tool, as a message for the model, or
    /// <see langword="null"/> when nothing is.
    /// </summary>
    internal static string? Check(Tool tool, IEnumerable<string> given)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(given);

        var (declared, required) = Declared(tool.InputSchema);
        var names = given.ToList();
        var problems = new List<string>();
        var suggested = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (declared.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var meant = Closest(name, declared);

            if (meant is null)
            {
                problems.Add($"'{name}' is not one of its arguments.");
                continue;
            }

            suggested.Add(meant);
            problems.Add(string.Equals(name, meant, StringComparison.OrdinalIgnoreCase)
                ? $"'{name}' is spelled '{meant}': argument names are case-sensitive."
                : $"'{name}' is not one of its arguments; did you mean '{meant}'?");
        }

        // A required argument that was sent under a wrong name is already named above.
        foreach (var name in required)
        {
            if (!names.Contains(name, StringComparer.Ordinal) && !suggested.Contains(name))
            {
                problems.Add($"'{name}' is required and was not passed.");
            }
        }

        if (problems.Count == 0)
        {
            return null;
        }

        var accepted = declared.Count == 0
            ? $"{tool.Name} takes no arguments."
            : $"Its arguments are {string.Join(", ", declared)}.";

        return $"{tool.Name} was not run: {string.Join(" ", problems)} {accepted}";
    }

    /// <summary>The property names an input schema declares, in order, and the required ones.</summary>
    private static (List<string> Declared, List<string> Required) Declared(JsonElement schema)
    {
        var declared = new List<string>();
        var required = new List<string>();

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return (declared, required);
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                declared.Add(property.Name);
            }
        }

        if (schema.TryGetProperty("required", out var names) && names.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in names.EnumerateArray())
            {
                if (name.GetString() is { } text)
                {
                    required.Add(text);
                }
            }
        }

        return (declared, required);
    }

    /// <summary>
    /// The declared name a wrong one most likely meant: the same name in another case first, then the
    /// nearest within <see cref="MaxSuggestionDistance"/> edits, then the one name it is a prefix of.
    /// </summary>
    private static string? Closest(string name, List<string> declared)
    {
        var sameIgnoringCase = declared.Find(candidate =>
            string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));

        if (sameIgnoringCase is not null)
        {
            return sameIgnoringCase;
        }

        var lower = name.ToLowerInvariant();
        string? nearest = null;
        var nearestDistance = MaxSuggestionDistance + 1;

        foreach (var candidate in declared)
        {
            var distance = EditDistance(lower, candidate.ToLowerInvariant());

            if (distance < nearestDistance)
            {
                nearest = candidate;
                nearestDistance = distance;
            }
        }

        if (nearest is not null)
        {
            return nearest;
        }

        // "include" for includeDeclaration, "organize" for organizeUsings - but only when the prefix
        // is unambiguous.
        var extended = declared
            .Where(candidate => candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return name.Length >= 3 && extended.Count == 1 ? extended[0] : null;
    }

    /// <summary>The Levenshtein distance between two short strings.</summary>
    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;

            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(substitution, Math.Min(previous[column], current[column - 1]) + 1);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
