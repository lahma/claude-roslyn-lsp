using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>
/// Makes sure the Roslyn child cannot outlive this process, on the one platform where that is not
/// already guaranteed.
/// </summary>
/// <remarks>
/// <para>
/// <b>D35 — a Windows Job Object with <c>KILL_ON_JOB_CLOSE</c>, and nothing at all elsewhere.</b>
/// Roslyn already watches the process id it is given (<c>initializeParams.processId</c>, and
/// <c>--clientProcessId</c> where the build has it) and exits when that process dies, which covers an
/// orderly failure everywhere. What it does not cover is an adapter killed hard — <c>TerminateProcess</c>,
/// a debugger detaching, the OOM killer — in the window before <c>initialize</c> has been sent. On
/// Unix that window ends by itself: the child is in this process's group and a session teardown
/// reaches it. On Windows there is no process group with that meaning, and the orphan is a 250 MB
/// server holding a solution open with nobody to talk to. A job object closes the window: the handle
/// is owned by this process, and the kernel kills the job's members when the last handle to it goes,
/// however it goes.
/// </para>
/// <para>
/// Everything here is <c>[LibraryImport]</c> rather than <c>[DllImport]</c>, because the product is
/// published Native AOT with <c>IL2026</c> and <c>IL3050</c> promoted to errors: the source generator
/// emits the marshalling ahead of time instead of asking the runtime to generate it. There is no
/// dependency here beyond <c>kernel32</c>, which is the point — the package budget has no room for a
/// process-supervision library and this is fifty lines.
/// </para>
/// </remarks>
internal sealed partial class ChildProcessGuard : IDisposable
{
    /// <summary><c>JobObjectExtendedLimitInformation</c>.</summary>
    private const int JobObjectExtendedLimitInformation = 9;

    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> — the whole reason this file exists.</summary>
    private const uint LimitKillOnJobClose = 0x2000;

    private readonly JobObjectHandle? _job;

    private ChildProcessGuard(JobObjectHandle? job) => _job = job;

    /// <summary>Whether the guard is doing anything — true only on Windows, and only if setup worked.</summary>
    internal bool IsActive => _job is { IsInvalid: false };

    /// <summary>
    /// Creates a guard for this process. Never throws: a machine that refuses to create a job object
    /// (an already-jobbed container, an unusual policy) still gets a working adapter, just without the
    /// belt to go with Roslyn's braces.
    /// </summary>
    /// <param name="logger">Where a failure to arm the guard is reported.</param>
    internal static ChildProcessGuard Create(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!OperatingSystem.IsWindows())
        {
            return new ChildProcessGuard(job: null);
        }

        var job = CreateJobObject(IntPtr.Zero, name: null);

        if (job.IsInvalid)
        {
            // Captured into a local before anything else runs: the last error is thread-global and
            // the next call - including the logger's own - would overwrite it. (It also satisfies
            // CA1873, which refuses a method call in a log argument.)
            var error = Marshal.GetLastWin32Error();
            Log.GuardUnavailable(logger, error);
            job.Dispose();
            return new ChildProcessGuard(job: null);
        }

        var information = default(JobObjectExtendedLimit);
        information.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;

        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                ref information,
                Marshal.SizeOf<JobObjectExtendedLimit>()))
        {
            var error = Marshal.GetLastWin32Error();
            Log.GuardUnavailable(logger, error);
            job.Dispose();
            return new ChildProcessGuard(job: null);
        }

        return new ChildProcessGuard(job);
    }

    /// <summary>
    /// Puts a started process under the guard. A failure is logged and otherwise ignored, for the
    /// reason <see cref="Create"/> gives.
    /// </summary>
    /// <param name="process">A process that has been started and has not exited.</param>
    /// <param name="logger">Where a failure is reported.</param>
    internal void Add(Process process, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(logger);

        if (_job is null || _job.IsInvalid)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(_job, process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                Log.GuardAssignmentFailed(logger, process.Id, error);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between Start and here. Nothing to guard, and nothing to say.
        }
    }

    /// <summary>
    /// Closes the job handle, which is what kills anything still in it. Idempotent.
    /// </summary>
    public void Dispose() => _job?.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial JobObjectHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        JobObjectHandle job,
        int informationClass,
        ref JobObjectExtendedLimit information,
        int informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(JobObjectHandle job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>. Only <c>LimitFlags</c> is set.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary><c>IO_COUNTERS</c>. Present only so the extended structure has its real size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.</summary>
    /// <remarks>
    /// The basic structure is not enough: <c>SetInformationJobObject</c> validates the length against
    /// the information class, and <c>JobObjectExtendedLimitInformation</c> with a basic-sized buffer
    /// fails with <c>ERROR_BAD_LENGTH</c> — a guard that silently does nothing, which is worse than no
    /// guard, because nobody would look for it.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public JobObjectBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary>A job object handle, closed through <c>CloseHandle</c>.</summary>
    private sealed class JobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobObjectHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    /// <summary>Source-generated log records; see the note in <c>LspStubServer</c> for why (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 120,
            Level = LogLevel.Debug,
            Message = "Could not create a job object (Win32 error {Error}); the Roslyn child is supervised by " +
                      "its own processId watchdog only.")]
        internal static partial void GuardUnavailable(ILogger logger, int error);

        [LoggerMessage(
            EventId = 121,
            Level = LogLevel.Debug,
            Message = "Could not add process {ProcessId} to the job object (Win32 error {Error}).")]
        internal static partial void GuardAssignmentFailed(ILogger logger, int processId, int error);
    }
}
