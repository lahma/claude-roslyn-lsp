using System.Text.Json;

using ClaudeRoslynLsp.Mcp.Engine;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Mcp;

/// <summary>
/// Code action payloads copied from the WP0 spike logs, with only the file URI changed.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these was captured off the wire from
/// <c>roslyn-language-server 5.12.0-1.26426.8</c> against the two-project fixture on 2026-09-10
/// (spikes S4 and S4b, which produced C18-C20). They are transcribed rather than invented because
/// the shapes are the entire difficulty: <c>data</c> is PascalCase, a fix-all is a separate action
/// with a <c>command</c> instead of an <c>edit</c>, the nested actions hide three levels down inside
/// <c>command.arguments[0].NestedCodeActions</c>, and a plain action's <c>data</c> carries
/// <c>NestedCodeActions: []</c> where a fix-all entry's carries <c>FixAllFlavors</c>. A hand-written
/// approximation of any of that would test the approximation.
/// </para>
/// </remarks>
internal static class RoslynPayloads
{
    /// <summary>The fixture document every payload addresses.</summary>
    internal const string DocumentUri = "file:///C:/fixture/Core/Calculator.cs";

    /// <summary>
    /// The plain IDE0005 fix. Note the empty <c>diagnostics</c> array: Roslyn offers the fix without
    /// attaching the diagnostic it fixes, which is why <c>fixDiagnostics</c> cannot rely on
    /// <c>diagnosticIds</c> alone.
    /// </summary>
    internal const string RemoveUnnecessaryUsings = """
        {
          "title": "Remove unnecessary usings",
          "kind": "quickfix",
          "diagnostics": [],
          "data": {
            "UniqueIdentifier": "Remove unnecessary usings",
            "CustomTags": [ "RemoveUnnecessaryImports" ],
            "Range": {
              "start": { "line": 1, "character": 0 },
              "end": { "line": 1, "character": 18 }
            },
            "TextDocument": { "uri": "file:///C:/fixture/Core/Calculator.cs" },
            "CodeActionPath": [ "Remove unnecessary usings" ],
            "NestedCodeActions": []
          }
        }
        """;

    /// <summary>
    /// The separate <c>Fix All:</c> entry for the same fix, carrying <c>FixAllFlavors</c> and the
    /// client-side marker command (C18, C19).
    /// </summary>
    internal const string FixAllRemoveUnnecessaryUsings = """
        {
          "title": "Fix All: Remove unnecessary usings",
          "kind": "quickfix",
          "diagnostics": [],
          "command": {
            "title": "Fix All: Remove unnecessary usings",
            "command": "roslyn.client.fixAllCodeAction",
            "arguments": [
              {
                "UniqueIdentifier": "Fix All: Remove unnecessary usings",
                "CustomTags": [ "RemoveUnnecessaryImports" ],
                "Range": {
                  "start": { "line": 1, "character": 0 },
                  "end": { "line": 1, "character": 18 }
                },
                "TextDocument": { "uri": "file:///C:/fixture/Core/Calculator.cs" },
                "CodeActionPath": [ "Remove unnecessary usings" ],
                "FixAllFlavors": [ "Document", "Project", "Solution" ]
              }
            ]
          },
          "data": {
            "UniqueIdentifier": "Fix All: Remove unnecessary usings",
            "CustomTags": [ "RemoveUnnecessaryImports" ],
            "Range": {
              "start": { "line": 1, "character": 0 },
              "end": { "line": 1, "character": 18 }
            },
            "TextDocument": { "uri": "file:///C:/fixture/Core/Calculator.cs" },
            "CodeActionPath": [ "Remove unnecessary usings" ],
            "FixAllFlavors": [ "Document", "Project", "Solution" ]
          }
        }
        """;

    /// <summary>
    /// The CA1822 fix, which does attach its diagnostic — including the VS-private tags C16 says must
    /// never be forwarded.
    /// </summary>
    internal const string MakeStatic = """
        {
          "title": "Make static",
          "kind": "quickfix",
          "diagnostics": [
            {
              "range": {
                "start": { "line": 7, "character": 15 },
                "end": { "line": 7, "character": 22 }
              },
              "severity": 3,
              "code": "CA1822",
              "codeDescription": {
                "href": "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1822"
              },
              "message": "Member 'Compute' does not access instance data and can be marked as static",
              "tags": [ 2147483642, 2147483645 ]
            }
          ],
          "data": {
            "UniqueIdentifier": "Make static",
            "CustomTags": [ "CSharpMarkMembersAsStaticFixer" ],
            "Range": {
              "start": { "line": 12, "character": 15 },
              "end": { "line": 12, "character": 23 }
            },
            "TextDocument": { "uri": "file:///C:/fixture/Core/Calculator.cs" },
            "CodeActionPath": [ "Make static" ],
            "NestedCodeActions": []
          }
        }
        """;

    /// <summary>
    /// The nested group. Its real choices live inside the marker command's argument zero, not inside
    /// <c>data</c>, and each child's <c>CodeActionPath</c> is three segments deep (S4b).
    /// </summary>
    internal const string SuppressOrConfigureIssues = """
        {
          "title": "Suppress or configure issues",
          "kind": "quickfix",
          "command": {
            "title": "Suppress or configure issues",
            "command": "roslyn.client.nestedCodeAction",
            "arguments": [
              {
                "UniqueIdentifier": "Suppress or configure issues",
                "CustomTags": [],
                "Range": {
                  "start": { "line": 1, "character": 0 },
                  "end": { "line": 1, "character": 18 }
                },
                "TextDocument": { "uri": "file:///C:/fixture/Core/Calculator.cs" },
                "CodeActionPath": [ "Suppress or configure issues" ],
                "NestedCodeActions": [
                  {
                    "title": "None",
                    "kind": "quickfix",
                    "data": {
                      "UniqueIdentifier": "None",
                      "CustomTags": [],
                      "Range": {
                        "start": { "line": 1, "character": 0 },
                        "end": { "line": 1, "character": 18 }
                      },
                      "TextDocument": { "uri": "file:///C:/fixture/Core/Calculator.cs" },
                      "CodeActionPath": [ "Suppress or configure issues", "Configure IDE0005 severity", "None" ]
                    }
                  },
                  {
                    "title": "Silent",
                    "kind": "quickfix",
                    "data": {
                      "UniqueIdentifier": "Silent",
                      "CustomTags": [],
                      "Range": {
                        "start": { "line": 1, "character": 0 },
                        "end": { "line": 1, "character": 18 }
                      },
                      "TextDocument": { "uri": "file:///C:/fixture/Core/Calculator.cs" },
                      "CodeActionPath": [ "Suppress or configure issues", "Configure IDE0005 severity", "Silent" ]
                    }
                  }
                ]
              }
            ]
          }
        }
        """;

    /// <summary>
    /// The resolved edit for the IDE0005 fix: <c>documentChanges</c>, <c>version: null</c>, and a
    /// range that deletes lines 0 to 3 (C21).
    /// </summary>
    internal const string ResolvedRemoveUsingsEdit = """
        {
          "documentChanges": [
            {
              "textDocument": {
                "version": null,
                "uri": "file:///C:/fixture/Core/Calculator.cs"
              },
              "edits": [
                {
                  "range": {
                    "start": { "line": 0, "character": 0 },
                    "end": { "line": 3, "character": 0 }
                  },
                  "newText": ""
                }
              ]
            }
          ]
        }
        """;

    /// <summary>Deserialises one code action.</summary>
    /// <param name="json">The payload.</param>
    internal static RawCodeAction Action(string json)
    {
        var action = JsonSerializer.Deserialize(json, RoslynEngineJsonContext.Default.RawCodeAction);
        Assert.NotNull(action);
        return action;
    }

    /// <summary>Deserialises one workspace edit.</summary>
    /// <param name="json">The payload.</param>
    internal static WorkspaceEdit Edit(string json)
    {
        var edit = JsonSerializer.Deserialize(json, RoslynEngineJsonContext.Default.WorkspaceEdit);
        Assert.NotNull(edit);
        return edit;
    }
}
