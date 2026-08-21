using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Acta.Relational.Connections;

/// <summary>
/// Compares the ADO driver assembly actually loaded into the process against the major version the
/// provider package was built and certified against, at provider bootstrap and before any SQL runs.
/// </summary>
internal static class DriverVersionPreflight
{
    // Each provider package owns its certified major as a constant next to its package reference;
    // this class owns only the comparison, the message, and the policy branch, so all three providers
    // say the same thing in the same words. A mismatch in either direction is reported: a driver
    // behind the certified major can be missing behavior Acta relies on, and a driver ahead of it is
    // a new major precisely because its authors reserved the right to change something.

    /// <summary>
    /// Throws (or warns, under <see cref="DriverVersionPolicy.Warn"/>) when
    /// <paramref name="driverAssembly"/>'s major version is not <paramref name="certifiedMajor"/>.
    /// </summary>
    public static void Run(Assembly driverAssembly, int certifiedMajor, DriverVersionPolicy policy, ILogger? log)
    {
        var name = driverAssembly.GetName();
        var loadedMajor = name.Version?.Major ?? 0;
        if (loadedMajor == certifiedMajor)
        {
            return;
        }

        var driverName = name.Name ?? driverAssembly.ToString();
        // Everything that is not Warn fails, rather than only the Fail member. Fail-closed is the point:
        // configuration binding can land an undefined value here (options validation rejects one, and
        // this is the belt to that pair of braces), and the enum deliberately offers no "skip", so an
        // uncertified driver must never continue on a value nobody chose.
        if (policy != DriverVersionPolicy.Warn)
        {
            throw new InvalidOperationException(Message(driverName, loadedMajor, certifiedMajor));
        }

        // A host that registered no logging gets nothing here, and that is the accepted answer rather
        // than the Skip the policy refuses to offer: it has chosen silence for every warning in the
        // process, not for this one. Warn is still a decision to keep running, and the option name is
        // in the host's own configuration where the operator wrote it.
        //
        // One line, not one per connection: this runs once per bootstrap. The mismatch itself is the
        // Detail, so the operator can read what differs without an index lookup for a second field.
        log?.LogWarning(
            "Acta: ({Operation}) continued on an uncertified database driver ({Detail}); reason ({Reason}).",
            "driver-version-preflight",
            Detail(driverName, loadedMajor, certifiedMajor),
            "driver-major-mismatch"
        );
    }

    /// <summary>
    /// The failure text: what is loaded, what was certified, and the one option that lets a host
    /// proceed anyway. Shared with the warning so both surfaces name the same three things.
    /// </summary>
    internal static string Message(string driverName, int loadedMajor, int certifiedMajor) =>
        $"{Detail(driverName, loadedMajor, certifiedMajor)}. Acta's dependency on {driverName} is an unbounded floor, so "
        + "the host application decides the driver version and this check is what makes that decision visible. Align the "
        + $"{driverName} version with the certified major, or set DriverVersionPolicy = DriverVersionPolicy.Warn on the "
        + "provider options to run on it anyway and take the logged warning instead.";

    private static string Detail(string driverName, int loadedMajor, int certifiedMajor) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{driverName} major {loadedMajor} is loaded, but this Acta provider package was built and certified against major {certifiedMajor}"
        );
}
