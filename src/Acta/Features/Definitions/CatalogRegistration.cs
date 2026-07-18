namespace Acta.Features.Definitions;

/// <summary>
/// One catalog contribution to typed-enqueue routing: a namespace and the modules whose descriptors
/// are visible there. Produced by <c>IJobsBuilder.Reference</c> and by every worker registration.
/// </summary>
internal sealed record CatalogRegistration(string NamespaceName, IReadOnlyList<ModuleRegistration> Modules);

/// <summary>
/// One manifest module captured by the builder. <see cref="GetDescriptors"/> is a closed-over,
/// non-reflective accessor to the generated manifest descriptors.
/// </summary>
internal sealed record ModuleRegistration(Type ManifestType, Func<JobDescriptorManifest> GetDescriptors);
