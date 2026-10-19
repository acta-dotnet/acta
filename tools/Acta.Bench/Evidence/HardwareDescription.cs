using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Acta.Bench;

internal static partial class HardwareDescription
{
    public static string CpuModel()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var model =
                    Microsoft.Win32.Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                        "ProcessorNameString",
                        null
                    ) as string;
                if (!string.IsNullOrWhiteSpace(model))
                {
                    return model.Trim();
                }
            }
            catch (System.Security.SecurityException) { }
        }
        const string cpuInfo = "/proc/cpuinfo";
        if (File.Exists(cpuInfo))
        {
            var line = File.ReadLines(cpuInfo).FirstOrDefault(l => l.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
    }

    public static int? PhysicalCores() => OperatingSystem.IsWindows() ? WindowsPhysicalCores() : null;

    [SupportedOSPlatform("windows")]
    private static int? WindowsPhysicalCores()
    {
        uint size = 0;
        _ = GetLogicalProcessorInformationEx(0, 0, ref size);
        if (size == 0)
        {
            return null;
        }
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (GetLogicalProcessorInformationEx(0, buffer, ref size) == 0)
            {
                return null;
            }
            var count = 0;
            for (var offset = 0; offset < size; )
            {
                var bytes = Marshal.ReadInt32(buffer + offset, 4);
                if (bytes < 8 || offset + bytes > size)
                {
                    return null;
                }
                count++;
                offset += bytes;
            }
            return count;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint length);
}
