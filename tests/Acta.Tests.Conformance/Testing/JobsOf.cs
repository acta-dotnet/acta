using System.Reflection;
using TestJobs;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// The manifest of a spec on the test jobs: the jobs <typeparamref name="TSpec"/> names in its
/// <see cref="DeploysAttribute"/>, and no others. Every test deploys its manifest into a namespace of its own, so a
/// spec that took every test job would write them all for each fact.
/// </summary>
public sealed class JobsOf<TSpec> : IJobManifest
{
    private static readonly JobDescriptorManifest s_descriptors = Select();

    // Explicit: a generic type declares no public static member.
    static JobDescriptorManifest IJobManifest.Descriptors => s_descriptors;

    private static JobDescriptorManifest Select()
    {
        var spec = typeof(TSpec);
        var deploys =
            spec.GetCustomAttribute<DeploysAttribute>()
            ?? throw new InvalidOperationException(
                $"{spec.Name} names no jobs to deploy. Put [Deploys(...)] on it, with no name for none."
            );
        var byName = TestJobsManifest.Descriptors.Descriptors.ToDictionary(d => d.JobName, StringComparer.Ordinal);
        return new([
            .. deploys.JobNames.Select(name =>
                byName.TryGetValue(name, out var descriptor)
                    ? descriptor
                    : throw new InvalidOperationException($"{spec.Name} deploys '{name}', which is no test job.")
            ),
        ]);
    }
}
