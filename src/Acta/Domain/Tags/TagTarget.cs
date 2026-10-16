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

    public static TagTarget ForTenant(string tenantName) =>
        new(TagScopeCode.Tenant, NameSyntax.Canonicalize(tenantName, nameof(tenantName), NameSyntax.ExtendedMaxLength));

    public static TagTarget ForNamespace(string namespaceName) =>
        new(TagScopeCode.Namespace, NameSyntax.Canonicalize(namespaceName, nameof(namespaceName)));

    public static TagTarget ForDefinition(string jobNamespace, string jobName) =>
        new(
            TagScopeCode.Definition,
            (
                NameSyntax.Canonicalize(jobNamespace, nameof(jobNamespace)),
                NameSyntax.Canonicalize(jobName, nameof(jobName), NameSyntax.ExtendedMaxLength)
            )
        );

    public static TagTarget ForJob(JobLookup job) => new(TagScopeCode.Job, job);
}
