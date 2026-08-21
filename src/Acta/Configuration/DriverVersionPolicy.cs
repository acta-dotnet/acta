namespace Acta;

/// <summary>
/// What a provider bootstrap does when the ADO driver assembly loaded into the process is not the
/// major version this Acta provider package was built and certified against.
/// </summary>
/// <remarks>
/// The package dependency stays a floor with no upper bound, because a nuspec upper bound is a fake
/// lock: NuGet reports a violated range as the NU1608 warning, not an error, so a host that manages
/// its own driver version silently wins anyway. The lock that actually holds is this one - a runtime
/// comparison at bootstrap, before any SQL runs. There is deliberately no "skip" member: an
/// uncertified driver either stops the process or says so in the log.
/// </remarks>
public enum DriverVersionPolicy : byte
{
    /// <summary>Throw at provider bootstrap, before any SQL. The default.</summary>
    Fail = 0,

    /// <summary>Log one structured warning naming both majors and continue.</summary>
    Warn = 1,
}
