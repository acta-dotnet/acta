namespace Acta;

/// <summary>
/// One manifest captured by the builder. <see cref="GetDescriptors"/> is a closed-over,
/// non-reflective accessor to the generated manifest descriptors.
/// </summary>
internal sealed record ManifestRegistration(Type ManifestType, Func<JobDescriptorManifest> GetDescriptors);
