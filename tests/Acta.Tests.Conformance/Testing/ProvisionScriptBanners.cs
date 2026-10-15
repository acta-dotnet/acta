using System.Text.RegularExpressions;

namespace Acta.Tests.Conformance.Testing;

/// <summary>The section banner the emitter writes above every migration in a published provisioning script.</summary>
internal static partial class ProvisionScriptBanners
{
    /// <summary>How many migrations the script carries, which is how many history rows a run of it leaves.</summary>
    public static int MigrationCount(string script) => BeginBanner().Count(script);

    [GeneratedRegex(@"^-- ===== BEGIN M[0-9]{14}_", RegexOptions.Multiline)]
    private static partial Regex BeginBanner();
}
