using System.Runtime.InteropServices;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>
/// The eight runtime identifiers Microsoft publishes <c>roslyn-language-server.&lt;rid&gt;</c> for,
/// and the rule that picks this host's one.
/// </summary>
/// <remarks>
/// <para>
/// <b>D24.</b> The payload package is RID-specific — the id <c>roslyn-language-server</c> itself is a
/// 33 KB shim whose only job is to depend on the right one — so acquisition (D2) starts by naming a
/// RID, and the pin's hash table (<see cref="RoslynServerManifest"/>) is keyed by it. There is no
/// portable fallback: a wrong RID downloads 70 MB that will not run.
/// </para>
/// <para>
/// The list is <em>Microsoft's</em>, taken from the shim's <c>DotnetToolSettings.xml</c> (C5), not
/// this repository's release matrix, and the two deliberately differ: this adapter ships five Native
/// AOT binaries but can front Roslyn on eight platforms, because <c>osx-x64</c> and the two musl RIDs
/// are reachable through the framework-dependent NuGet tool (D22) even though no archive is built for
/// them.
/// </para>
/// <para>
/// <see cref="Current"/> may return a RID that is <em>not</em> in <see cref="All"/> — <c>win-x86</c>
/// and <c>linux-arm</c> are real hosts that Microsoft simply does not publish for. That is reported
/// as an unsupported host by <c>doctor</c> rather than corrected to a near neighbour, because
/// silently downloading the 64-bit payload onto a 32-bit host produces a child process that dies
/// with a message about an image format, which is nobody's idea of a diagnosis.
/// </para>
/// </remarks>
internal static class RuntimeIdentifier
{
    /// <summary>Windows on x64.</summary>
    internal const string WindowsX64 = "win-x64";

    /// <summary>Windows on ARM64.</summary>
    internal const string WindowsArm64 = "win-arm64";

    /// <summary>Linux with glibc on x64.</summary>
    internal const string LinuxX64 = "linux-x64";

    /// <summary>Linux with glibc on ARM64.</summary>
    internal const string LinuxArm64 = "linux-arm64";

    /// <summary>Linux with musl libc on x64 — Alpine, and the distroless images built on it.</summary>
    internal const string LinuxMuslX64 = "linux-musl-x64";

    /// <summary>Linux with musl libc on ARM64.</summary>
    internal const string LinuxMuslArm64 = "linux-musl-arm64";

    /// <summary>macOS on Intel.</summary>
    internal const string OsxX64 = "osx-x64";

    /// <summary>macOS on Apple silicon.</summary>
    internal const string OsxArm64 = "osx-arm64";

    /// <summary>
    /// Every RID the pinned server is published for, in the order the manifest lists them.
    /// </summary>
    /// <remarks>
    /// Eight, not the seven the design sketch assumed: <c>linux-musl-arm64</c> is published as well
    /// (C5). <c>RoslynServerManifestTests</c> asserts that this list and the manifest's hash table
    /// have exactly the same members, so a RID added here without a hash — or a hash without a RID —
    /// fails the build rather than a user's first run.
    /// </remarks>
    internal static IReadOnlyList<string> All { get; } =
    [
        WindowsX64,
        WindowsArm64,
        LinuxX64,
        LinuxArm64,
        LinuxMuslX64,
        LinuxMuslArm64,
        OsxX64,
        OsxArm64,
    ];

    /// <summary>
    /// This host's RID, computed once. Not necessarily one of <see cref="All"/> — see the type
    /// remarks.
    /// </summary>
    internal static string Current { get; } = ResolveCurrent();

    /// <summary>Whether the pinned server is published for <paramref name="runtimeIdentifier"/>.</summary>
    /// <param name="runtimeIdentifier">A RID, typically <see cref="Current"/>.</param>
    internal static bool IsSupported(string? runtimeIdentifier) =>
        runtimeIdentifier is not null && All.Contains(runtimeIdentifier, StringComparer.Ordinal);

    /// <summary>
    /// Builds the RID for an explicitly named platform. Split out from <see cref="ResolveCurrent"/>
    /// so the mapping can be exercised for all eight platforms from any one of them.
    /// </summary>
    /// <param name="isWindows">Whether the host is Windows.</param>
    /// <param name="isMacOs">Whether the host is macOS.</param>
    /// <param name="isMusl">Whether the host's libc is musl rather than glibc. Ignored off Linux.</param>
    /// <param name="architecture">The process architecture.</param>
    /// <returns>
    /// The RID, which is <c>&lt;os&gt;-&lt;arch&gt;</c> in .NET's own spelling — so an architecture
    /// Microsoft does not publish for produces a RID that <see cref="IsSupported"/> rejects rather
    /// than an exception.
    /// </returns>
    internal static string Compose(bool isWindows, bool isMacOs, bool isMusl, Architecture architecture)
    {
        var os = isWindows ? "win" : isMacOs ? "osx" : isMusl ? "linux-musl" : "linux";

        var cpu = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => architecture.ToString().ToLowerInvariant(),
        };

        return os + "-" + cpu;
    }

    /// <summary>
    /// Decides whether this Linux host's libc is musl.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two independent signals, because each one alone has a hole. The runtime's own
    /// <see cref="RuntimeInformation.RuntimeIdentifier"/> already carries <c>musl</c> when the app
    /// was built or published for it — but a portable, framework-dependent build (which is exactly
    /// how the NuGet tool of D22 runs) reports the RID it was <em>built</em> for, which may be plain
    /// <c>linux-x64</c>. So the dynamic loader is probed as well: musl installs its loader as
    /// <c>/lib/ld-musl-&lt;arch&gt;.so.1</c>, a file glibc systems do not have and musl systems
    /// cannot boot without.
    /// </para>
    /// <para>
    /// Getting this wrong is not subtle at the failure point but is very subtle at the cause: the
    /// glibc payload on Alpine dies with <c>no such file or directory</c> for a file that plainly
    /// exists, which is what a missing <c>ld-linux</c> looks like through a shell.
    /// </para>
    /// </remarks>
    /// <param name="hostRuntimeIdentifier">The RID the runtime reports for itself.</param>
    /// <param name="enumerateLoaders">
    /// Returns the musl loader files present in <c>/lib</c>. Injected so the branch is testable on a
    /// machine that has no <c>/lib</c> at all.
    /// </param>
    internal static bool DetectMusl(string? hostRuntimeIdentifier, Func<IEnumerable<string>> enumerateLoaders)
    {
        ArgumentNullException.ThrowIfNull(enumerateLoaders);

        if (hostRuntimeIdentifier?.Contains("musl", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return enumerateLoaders().Any();
    }

    /// <summary>Enumerates musl's dynamic loaders, tolerating a host with no <c>/lib</c>.</summary>
    private static IEnumerable<string> EnumerateMuslLoaders()
    {
        // Directory.EnumerateFiles throws for a missing directory rather than yielding nothing, and
        // on Windows /lib is missing by definition. The whole point of this probe is to answer
        // "musl?" with false on every host that is not Alpine, so absence is the common case.
        if (!Directory.Exists("/lib"))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles("/lib", "ld-musl-*");
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ResolveCurrent() =>
        Compose(
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS(),
            OperatingSystem.IsLinux() && DetectMusl(RuntimeInformation.RuntimeIdentifier, EnumerateMuslLoaders),
            RuntimeInformation.ProcessArchitecture);
}
