namespace Acta.Features.Definitions;

/// <summary>
/// One catalog contribution to typed-enqueue routing: a namespace and the manifests whose descriptors
/// are visible there. Produced by <c>IActaBuilder.Reference</c> and by every worker registration.
/// </summary>
internal sealed record JobCatalogRegistration(string NamespaceName, IReadOnlyList<ManifestRegistration> Manifests);

/// <summary>
/// One manifest captured by the builder. <see cref="GetDescriptors"/> is a closed-over,
/// non-reflective accessor to the generated manifest descriptors.
/// </summary>
internal sealed record ManifestRegistration(Type ManifestType, Func<JobDescriptorManifest> GetDescriptors);
