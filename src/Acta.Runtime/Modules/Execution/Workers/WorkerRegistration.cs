using Acta.Modules.Alerting;
using Acta.Modules.Execution.Definitions;
using Acta.Modules.Outbox;

namespace Acta.Modules.Execution.Workers;

/// <summary>
/// One worker declared via <c>IActaBuilder.Run(...)</c>. Carries the runtime's namespace identity and
/// the manifests it hosts, read by <see cref="WorkerRuntime"/> at <c>InitializeAsync</c> to upsert the
/// <c>namespaces</c> row, the per-namespace <c>definitions</c> rows, and the <c>workers</c> row,
/// and by <see cref="WorkerRuntime.RunLoopAsync"/> to decide whether to enter the claim-poll loop.
/// </summary>
/// <remarks>
/// <see cref="ActaServiceCollectionExtensions.UseActa"/> registers one <see cref="WorkerRuntime"/>
/// per declared worker, so a process running several <c>Run(...)</c> calls fans out one claim/dispatch/
/// heartbeat trio per namespace. Enqueue-only runtimes (HTTP frontends, dashboards) omit <c>Run(...)</c>
/// and may <c>Reference(...)</c> manifests for typed enqueue: neither creates a
/// <see cref="WorkerRegistration"/>, so the process registers no worker and never writes catalog rows;
/// enqueue resolves <c>(namespace, jobName)</c> to ids via SQL JOIN at INSERT time.
/// </remarks>
internal sealed record WorkerRegistration(
    string NamespaceName,
    string? OwnerTeam,
    string? Description,
    IReadOnlyList<ManifestRegistration> Manifests,
    IReadOnlyList<AlertChannelDeclaration> AlertChannels,
    OutboxRelayRegistration? Relay = null
);
