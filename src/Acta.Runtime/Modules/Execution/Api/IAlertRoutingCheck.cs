using System.Collections.Immutable;

namespace Acta.Modules.Execution.Api;

/// <summary>
/// Execution's startup alert-routing seam, mirroring <see cref="IAlertSink"/>: worker init hands
/// each namespace's effective descriptors here after catalog registration, and Alerting - which
/// owns channel declarations and the implicit default channel - validates that every alerting
/// definition resolves to a configured channel. Composition registers the implementation; the
/// direct-constructor test seam may pass null to skip the check.
/// </summary>
internal interface IAlertRoutingCheck
{
    void ValidateRouting(string namespaceName, ImmutableArray<JobDescriptor> effectiveDescriptors);
}
