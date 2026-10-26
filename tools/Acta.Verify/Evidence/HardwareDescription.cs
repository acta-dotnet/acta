using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Acta.Verify;

public static partial class HardwareDescription
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

    public static int? PhysicalCores() => OperatingSystem.IsWindows() ? WindowsCores()?.Count : null;

    /// <summary>
    /// This machine's performance cores as an affinity mask over its logical processors; null when every core shares one
    /// efficiency class, or when the topology is unknown or spans more than the one processor group a mask covers.
    /// </summary>
    public static ulong? PerformanceMask() =>
        OperatingSystem.IsWindows() && WindowsCores() is { } cores && cores.All(c => c.Group == 0)
            ? PerformanceMask([.. cores.Select(c => (c.EfficiencyClass, c.Mask))])
            : null;

    /// <summary>
    /// The logical processors of the cores in the highest efficiency class; null when every core shares one class, so the
    /// CPU is not hybrid.
    /// </summary>
    public static ulong? PerformanceMask(IReadOnlyCollection<(byte EfficiencyClass, ulong Mask)> cores)
    {
        ArgumentNullException.ThrowIfNull(cores);
        if (cores.Select(c => c.EfficiencyClass).Distinct().Count() < 2)
        {
            return null;
        }
        var performance = cores.Max(c => c.EfficiencyClass);
        return cores.Where(c => c.EfficiencyClass == performance).Aggregate(0UL, (mask, c) => mask | c.Mask);
    }

    /// <summary>Each physical core's efficiency class, processor group and logical-processor mask; null when unreadable.</summary>
    [SupportedOSPlatform("windows")]
    private static List<(byte EfficiencyClass, ushort Group, ulong Mask)>? WindowsCores()
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
            List<(byte, ushort, ulong)> cores = [];
            for (var offset = 0; offset < size; )
            {
                var record = buffer + offset;
                var bytes = Marshal.ReadInt32(record, 4);
                if (bytes < 48 || offset + bytes > size)
                {
                    return null;
                }
                // A RelationProcessorCore record: EfficiencyClass at byte 9, then its one GROUP_AFFINITY at 32, the
                // mask first and the group after it.
                cores.Add((Marshal.ReadByte(record, 9), (ushort)Marshal.ReadInt16(record, 40), (ulong)Marshal.ReadInt64(record, 32)));
                offset += bytes;
            }
            return cores;
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
