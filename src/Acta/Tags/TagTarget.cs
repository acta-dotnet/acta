using System.Numerics;

namespace Acta;

/// <summary>
/// Opaque address for one taggable target. Construct targets only through the named factory matching
/// the target type; the discriminator and lookup payload are intentionally not public contract data.
/// </summary>
public sealed class TagTarget
{
    private TagTarget(TagScopeCode scopeCode, object lookup)
    {
        ScopeCode = scopeCode;
        Lookup = lookup;
    }

    internal TagScopeCode ScopeCode { get; }
    internal object Lookup { get; }

    public static TagTarget ForTenant(string tenantKey) =>
        new(TagScopeCode.Tenant, IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey)));

    public static TagTarget ForNamespace(string namespaceName) =>
        new(TagScopeCode.Namespace, IdentifierSyntax.CanonicalizeKebab(namespaceName, nameof(namespaceName)));

    public static TagTarget ForDefinition(string jobNamespace, string jobName) =>
        new(
            TagScopeCode.Definition,
            (
                IdentifierSyntax.CanonicalizeKebab(jobNamespace, nameof(jobNamespace)),
                IdentifierSyntax.NormalizeName(jobName, nameof(jobName), IdentifierSyntax.ExtendedMaxLength)
            )
        );

    public static TagTarget ForJob(JobLookup job) => new(TagScopeCode.Job, job);

    public static TagTarget ForSchedule(ScheduleLookup schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return new TagTarget(TagScopeCode.Schedule, schedule);
    }

    public static TagTarget ForWorker(WorkerRef workerRef) => new(TagScopeCode.Worker, workerRef);

    public static TagTarget ForAlert(AlertRef alertRef) => new(TagScopeCode.Alert, alertRef);

    public static TagTarget ForEvent(long eventId) => new(TagScopeCode.Event, Positive(eventId, nameof(eventId)));

    private static T Positive<T>(T value, string paramName)
        where T : INumber<T>
    {
        return value <= T.Zero ? throw new ArgumentOutOfRangeException(paramName, value, "Target id must be positive.") : value;
    }
}
