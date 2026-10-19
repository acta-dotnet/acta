using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Acta.Bench;

internal static class BuildMode
{
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000",
        Justification = "The empty location is deliberately inspected, together with explicit publish metadata, to avoid labelling an AOT-constrained managed development build as a native executable."
    )]
    public static string Current()
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            return "managed_jit";
        var assembly = typeof(BenchCommand).Assembly;
        if (assembly.Location.Length > 0)
            return "managed_aot_constraints";
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        var native = metadata.Any(a =>
            a.Key == "Acta.Bench.PublishAot" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase)
        );
        var bundle = metadata.Any(a =>
            a.Key == "Acta.Bench.PublishSingleFile" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase)
        );
        return native && !bundle ? "native_aot" : "aot_constraints_unknown_binary_kind";
    }
}
