namespace Acta;

/// <summary>
/// Generator-emitted per-assembly manifest carrying every <c>[Job]</c>-decorated handler's
/// descriptor. Namespace binding happens at registration time via
/// <c>IJobsBuilder.AddModule&lt;TManifest&gt;</c>; the manifest is namespace-neutral and carries
/// no runtime/worker identity.
/// </summary>
public interface IActaManifest
{
    /// <summary>
    /// Generator-emitted manifest of every <see cref="JobDescriptor"/> in this assembly.
    /// </summary>
    static abstract JobDescriptorManifest Descriptors { get; }
}
