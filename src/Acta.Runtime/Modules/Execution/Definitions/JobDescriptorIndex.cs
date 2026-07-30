namespace Acta.Modules.Execution.Definitions;

/// <summary>
/// Maps a (namespace, job name) pair to its generated <see cref="JobDescriptor"/>, backing
/// <see cref="IJobs.GetInputTemplate"/>. Built once at
/// <see cref="ActaServiceCollectionExtensions.UseActa"/> from the declared catalogs (Reference and
/// Run alike), so an enqueue-only dashboard host resolves the same descriptors a worker host does.
/// A host that never registered the job's manifest has no entry and reports null: the descriptor is
/// in-process compile-time data, never a database read.
/// </summary>
internal sealed class JobDescriptorIndex
{
    private readonly Dictionary<(string Namespace, string JobName), JobDescriptor> _descriptors;

    private JobDescriptorIndex(Dictionary<(string, string), JobDescriptor> descriptors) => _descriptors = descriptors;

    public static JobDescriptorIndex Build(IEnumerable<JobCatalogRegistration> catalogs)
    {
        var descriptors = new Dictionary<(string, string), JobDescriptor>();

        foreach (var catalog in catalogs)
        {
            foreach (var manifest in catalog.Manifests)
            {
                foreach (var descriptor in manifest.GetDescriptors().Descriptors)
                {
                    // The same job can surface via several catalogs (Reference + Run); first wins, and
                    // every route carries the identical generated descriptor.
                    descriptors[(catalog.NamespaceName, descriptor.JobName)] = descriptor;
                }
            }
        }

        return new JobDescriptorIndex(descriptors);
    }

    public JobDescriptor? Find(string jobNamespace, string jobName) =>
        _descriptors.TryGetValue((jobNamespace, jobName), out var descriptor) ? descriptor : null;
}
