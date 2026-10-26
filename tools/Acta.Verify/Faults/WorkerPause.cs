using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Acta.Verify;

internal static partial class WorkerPause
{
    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtSuspendProcess(SafeProcessHandle handle);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtResumeProcess(SafeProcessHandle handle);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SendSignal(int pid, int signal);

    public static void Set(System.Diagnostics.Process process, bool paused)
    {
        var result = OperatingSystem.IsWindows()
            ? paused
                ? NtSuspendProcess(process.SafeHandle)
                : NtResumeProcess(process.SafeHandle)
            : SendSignal(
                process.Id,
                paused
                    ? OperatingSystem.IsMacOS()
                        ? 17
                        : 19
                    : OperatingSystem.IsMacOS()
                        ? 19
                        : 18
            );
        if (result != 0)
        {
            throw new InvalidOperationException("The OS refused to pause/resume this owned worker.");
        }
    }
}
