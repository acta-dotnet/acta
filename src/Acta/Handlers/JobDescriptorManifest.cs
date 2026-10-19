using System.Collections.Immutable;

namespace Acta;

/// <summary>
/// Generator-emitted manifest of every <see cref="JobDescriptor"/> declared in an
/// <see cref="IJobManifest"/>-bearing assembly. Source-time facts only.
/// </summary>
public sealed record JobDescriptorManifest(ImmutableArray<JobDescriptor> Descriptors);
