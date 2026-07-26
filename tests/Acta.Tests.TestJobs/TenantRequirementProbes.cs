using Acta;

namespace TestJobs;

public sealed record TenantScopedWork(string Note);

public sealed record TenantNeutralWork(string Note);

/// <summary>
/// Definitions pinning the enqueue-boundary tenant-requirement policy: one that must carry a tenant
/// (explicit key or parent inheritance) and one that never does (explicit key rejected, inheritance
/// suppressed).
/// </summary>
public static class TenantRequirementProbes
{
    [Job("tenant-required-probe", TenantRequirement = JobTenantRequirementCode.Required)]
    public static Task RequiredRun(TenantScopedWork input) => Task.CompletedTask;

    [Job("tenant-forbidden-probe", TenantRequirement = JobTenantRequirementCode.Forbidden)]
    public static Task ForbiddenRun(TenantNeutralWork input) => Task.CompletedTask;
}
