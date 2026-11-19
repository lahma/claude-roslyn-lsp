using System.Reflection;

using ClaudeRoslynLsp.Mcp.Tools;

using ModelContextProtocol.Server;

using NUnit.Framework;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace ClaudeRoslynLsp.Tests.Tools;

/// <summary>
/// The tool surface as a contract: which tools exist, what they are called, and what an MCP client
/// is told about each one before it decides whether to ask the user first.
/// </summary>
/// <remarks>
/// <para>
/// The annotation table is the load-bearing part. <c>Destructive</c> defaults to
/// <see langword="true"/> in the SDK, so a mutating tool that forgets to say otherwise makes clients
/// prompt before every format, and a read tool that says anything at all adds noise to a decision
/// <c>readOnlyHint</c> has already made.
/// </para>
/// <para>
/// Everything is asserted against the values an MCP client actually receives — the
/// <see cref="McpServerTool.ProtocolTool"/> built through the SDK with the production serializer
/// options — rather than against the attribute, so an SDK change in how attributes become
/// annotations cannot pass unnoticed.
/// </para>
/// </remarks>
public class ToolInventoryTests
{
    /// <summary>
    /// The design's tool list (the AGENTS.md tool table), in full and in ordinal order. Adding a tool
    /// means editing this array, the AGENTS.md tool table, <c>Build.cs</c>'s <c>ExpectedToolNames</c>,
    /// the README table and the skill.
    /// </summary>
    private static readonly string[] ExpectedToolNames =
    [
        "applyCodeAction",
        "findReferences",
        "fixDiagnostics",
        "formatCode",
        "getCodeActions",
        "getDiagnostics",
        "getTypeMembers",
        "getWorkspaceStatus",
        "renameSymbol",
        "resolveSymbol",
    ];

    /// <summary>
    /// The parameter types the SDK binds from DI or from the protocol, and therefore leaves out of
    /// the generated schema. Everything else is a model-supplied argument.
    /// </summary>
    private static readonly Type[] InjectedParameterTypes =
    [
        typeof(RoslynToolContext),
        typeof(CancellationToken),
    ];

    [Test]
    public void ExactlyTenToolsAreDeclaredAcrossTheFiveToolClasses()
    {
        Assert.That(ToolTestHost.ToolTypes.Count, Is.EqualTo(5));
        Assert.That(ToolTestHost.ToolMethods.Count, Is.EqualTo(ExpectedToolNames.Length));
        Assert.That(ToolTestHost.Tools.Count, Is.EqualTo(ExpectedToolNames.Length));
        Assert.That(ExpectedToolNames.Length, Is.EqualTo(10));
    }

    [Test]
    public void ToolNamesAreExactlyThePlannedOnes() =>
        Assert.That(ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name).ToArray(), Is.EqualTo(ExpectedToolNames));

    [Test]
    public void TheToolClassesAreTheFiveTheDesignNames()
    {
        var names = ToolTestHost.ToolTypes.Select(type => type.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.That(
            names,
            Is.EqualTo(["CodeActionTools", "DiagnosticTools", "EditTools", "SymbolTools", "WorkspaceTools"]));
    }

    [TestCase("getWorkspaceStatus", "Get workspace status")]
    [TestCase("resolveSymbol", "Resolve symbol")]
    [TestCase("getTypeMembers", "Get type members")]
    [TestCase("findReferences", "Find references")]
    [TestCase("getDiagnostics", "Get diagnostics")]
    [TestCase("getCodeActions", "Get code actions")]
    [TestCase("applyCodeAction", "Apply code action")]
    [TestCase("renameSymbol", "Rename symbol")]
    [TestCase("fixDiagnostics", "Fix diagnostics")]
    [TestCase("formatCode", "Format code")]
    public void TitleIsTheOneTheDesignSpecifies(string name, string expectedTitle)
    {
        var tool = ToolTestHost.Find(name).ProtocolTool;

        Assert.That(tool.Title, Is.EqualTo(expectedTitle));
        Assert.That(tool.Annotations?.Title, Is.EqualTo(expectedTitle));
    }

    /// <summary>
    /// The six read tools. <c>destructiveHint</c> must be <b>absent</b>: the SDK omits it unless the
    /// attribute sets it, and a destructive hint on a read-only tool is noise in front of a decision
    /// <c>readOnlyHint</c> has already made.
    /// <para>
    /// <c>getWorkspaceStatus</c> is idempotent in the sense the annotation means — calling it changes
    /// nothing — even though its answer changes while the solution loads, which is the entire reason
    /// it exists. The hint is about effects, not about a stable response.
    /// </para>
    /// </summary>
    [TestCase("getWorkspaceStatus")]
    [TestCase("resolveSymbol")]
    [TestCase("getTypeMembers")]
    [TestCase("findReferences")]
    [TestCase("getDiagnostics")]
    [TestCase("getCodeActions")]
    public void ReadToolsAreReadOnlyIdempotentAndSayNothingAboutDestruction(string name)
    {
        var annotations = ToolTestHost.Find(name).ProtocolTool.Annotations;

        Assert.That(annotations, Is.Not.Null);
        Assert.That(annotations!.ReadOnlyHint, Is.True);
        Assert.That(annotations.IdempotentHint, Is.True);
        Assert.That(annotations.DestructiveHint, Is.Null);
    }

    /// <summary>
    /// The four mutating tools, and the two judgement calls in the table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>applyCodeAction</c> and <c>fixDiagnostics</c> are <b>destructive</b>: a code action can
    /// delete a file outright (<c>Move type to X.cs</c> empties the original and creates another),
    /// and a fix-all rewrites every occurrence across a solution. Neither is undoable from inside this
    /// server, so a client that wants to confirm first should be told to.
    /// </para>
    /// <para>
    /// <c>renameSymbol</c> and <c>formatCode</c> are <b>not</b>: both rewrite source files, but
    /// neither removes a declaration, a file or a piece of information — a rename is the same code
    /// under another name and a format is the same code in another shape. Marking them destructive
    /// would put a confirmation in front of the two operations this product most wants a model to
    /// reach for, which is exactly how a user learns to click through the prompts that matter.
    /// </para>
    /// <para>
    /// Idempotence splits on whether a second identical call does anything. Renaming to the same name
    /// twice, fixing the same diagnostic id twice and formatting twice all find nothing left to do;
    /// <c>applyCodeAction</c> does not, because "use expression body" and "convert to positional
    /// record" applied twice are two different transformations of two different sources.
    /// </para>
    /// </remarks>
    [TestCase("applyCodeAction", true, false)]
    [TestCase("renameSymbol", false, true)]
    [TestCase("fixDiagnostics", true, true)]
    [TestCase("formatCode", false, true)]
    public void MutatingToolsDeclareDestructionAndIdempotenceExplicitly(string name, bool destructive, bool idempotent)
    {
        var annotations = ToolTestHost.Find(name).ProtocolTool.Annotations;

        Assert.That(annotations, Is.Not.Null);
        Assert.That(annotations!.ReadOnlyHint, Is.False);
        Assert.That(annotations.DestructiveHint, Is.EqualTo(destructive));
        Assert.That(annotations.IdempotentHint, Is.EqualTo(idempotent));
    }

    /// <summary>
    /// Nothing here reaches outside the machine, or even outside the workspace: the backend is a
    /// child process and the domain is one solution's files. That is what <c>openWorldHint: false</c>
    /// says, and saying <c>true</c> — as a server talking to a web API would — would misdescribe every
    /// tool in the list.
    /// </summary>
    [Test]
    public void NoToolClaimsAnOpenWorld()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            Assert.That(tool.ProtocolTool.Annotations?.OpenWorldHint, Is.EqualTo(false));
        }
    }

    [Test]
    public void EveryToolAdvertisesStructuredOutput()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            Assert.That(
                tool.ProtocolTool.OutputSchema is not null, Is.True,
                $"{tool.ProtocolTool.Name} has no output schema; UseStructuredContent must be true.");
        }
    }

    [Test]
    public void EveryToolHasATitle()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            Assert.That(
                string.IsNullOrWhiteSpace(tool.ProtocolTool.Title), Is.False,
                $"{tool.ProtocolTool.Name} has no title; it is what a client shows a human.");
        }
    }

    /// <summary>
    /// Sealed, not <c>static</c>: C# forbids a static class as the type argument of
    /// <c>WithTools&lt;T&gt;</c> (CS0718). The private constructor is what keeps it uninstantiable
    /// anyway, and the methods themselves must be static so no instance is activated per call.
    /// </summary>
    [Test]
    public void ToolClassesAreSealedAttributedAndUninstantiable()
    {
        foreach (var type in ToolTestHost.ToolTypes)
        {
            Assert.That(type.IsSealed, Is.True, $"{type.Name} must be sealed.");
            Assert.That(type.GetCustomAttribute<McpServerToolTypeAttribute>(), Is.Not.Null);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        }
    }

    [Test]
    public void EveryToolMethodIsPublicStaticAndReturnsATaskOfAShapedResult()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            Assert.That(method.IsPublic, Is.True, $"{method.Name} must be public.");
            Assert.That(method.IsStatic, Is.True, $"{method.Name} must be static.");

            Assert.That(
                method.ReturnType.IsGenericType
                && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>), Is.True,
                $"{method.Name} must return Task<T>, not {method.ReturnType.Name}.");

            Assert.That(method.ReturnType.GetGenericArguments()[0].Namespace, Is.EqualTo("ClaudeRoslynLsp.Mcp.Models"));
        }
    }

    [Test]
    public void EveryToolMethodHasADescription()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;

            Assert.That(
                string.IsNullOrWhiteSpace(description), Is.False,
                $"{method.Name} needs a [Description]; it is what the model reads to choose the tool.");
        }
    }

    [Test]
    public void EveryModelSuppliedParameterHasADescription()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            foreach (var parameter in method.GetParameters())
            {
                if (InjectedParameterTypes.Contains(parameter.ParameterType))
                {
                    continue;
                }

                Assert.That(
                    string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description), Is.False,
                    $"{method.Name}({parameter.Name}) needs a [Description]; it becomes the schema description.");
            }
        }
    }

    /// <summary>
    /// The token is the SDK's cancellation wiring, and a defaulted trailing parameter is the only
    /// shape that keeps every other argument callable by name and position from a test.
    /// </summary>
    [Test]
    public void CancellationTokenIsTheLastParameterOfEveryTool()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var parameters = method.GetParameters();
            var last = parameters[^1];

            Assert.That(last.ParameterType, Is.EqualTo(typeof(CancellationToken)));
            Assert.That(last.HasDefaultValue, Is.True, $"{method.Name}'s cancellationToken must be optional.");

            Assert.That(
                parameters[..^1],
                Has.None.Matches<ParameterInfo>(parameter => parameter!.ParameterType == typeof(CancellationToken)));
        }
    }

    /// <summary>
    /// One injected collaborator, always first, so the model-facing arguments are the tail of the
    /// signature and every schema exclusion has one name to watch for.
    /// </summary>
    [Test]
    public void EveryToolTakesItsContextAsTheFirstParameter()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            Assert.That(method.GetParameters()[0].ParameterType, Is.EqualTo(typeof(RoslynToolContext)));
        }
    }

    /// <summary>
    /// Every tool method's name is its MCP name plus <c>Async</c>, so a stack trace, a log line and a
    /// <c>tools/list</c> entry all name the same thing.
    /// </summary>
    [Test]
    public void EveryToolMethodIsNamedAfterTheToolItDeclares()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name;

            Assert.That(method.Name, Is.EqualTo(char.ToUpperInvariant(name![0]) + name[1..] + "Async"));
        }
    }

    /// <summary>
    /// Every mutating tool takes <c>preview</c>, and no read tool does. The flag is the product's
    /// safety story — "look before you write" is only available if it is available everywhere — and a
    /// preview flag on a tool that writes nothing would be a promise about an operation that does not
    /// exist.
    /// </summary>
    [TestCase("applyCodeAction", true)]
    [TestCase("renameSymbol", true)]
    [TestCase("fixDiagnostics", true)]
    [TestCase("formatCode", true)]
    [TestCase("getWorkspaceStatus", false)]
    [TestCase("resolveSymbol", false)]
    [TestCase("getTypeMembers", false)]
    [TestCase("findReferences", false)]
    [TestCase("getDiagnostics", false)]
    [TestCase("getCodeActions", false)]
    public void PreviewExistsOnEveryMutatingToolAndOnNoOther(string name, bool expected)
    {
        var parameter = ToolTestHost.FindMethod(name)
            .GetParameters()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, "preview", StringComparison.Ordinal));

        Assert.That(parameter is not null, Is.EqualTo(expected));

        if (parameter is not null)
        {
            Assert.That(parameter.ParameterType, Is.EqualTo(typeof(bool)));
            Assert.That(parameter.DefaultValue, Is.EqualTo(false));
        }
    }
}
