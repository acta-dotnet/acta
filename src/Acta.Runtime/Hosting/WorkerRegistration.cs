using Acta.Runtime.Modules.Alerting.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Outbox;

namespace Acta.Runtime.Hosting;

/// <summary>
/// One worker declared via <c>IActaBuilder.Run(...)</c>. Carries the runtime's namespace identity and
/// the manifests it hosts, read by <see cref="Acta.Runtime.Modules.Execution.Workers.WorkerRuntime"/> at <c>InitializeAsync</c> to upsert the
/// <c>namespaces</c> row, the per-namespace <c>definitions</c> rows, and the <c>workers</c> row,
/// and by <see cref="Acta.Runtime.Modules.Execution.Workers.WorkerRuntime.RunLoopAsync"/> to decide whether to enter the claim-poll loop.
/// The one-runtime-per-Run fan-out and the enqueue-only topology are documented in
/// docs/internals/design.md (Boundaries).
/// </summary>
internal sealed record WorkerRegistration(
    string NamespaceName,
    string? OwnerTeam,
    string? Description,
    IReadOnlyList<ManifestRegistration> Manifests,
    IReadOnlyList<AlertChannelDeclaration> AlertChannels,
    OutboxRelayRegistration? Relay = null
);
