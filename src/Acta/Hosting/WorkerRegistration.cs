namespace Acta;

/// <summary>
/// One worker declared via <c>IActaBuilder.Run(...)</c>. Carries the runtime's namespace identity and
/// the manifests it hosts, read by a deploy to write the <c>namespaces</c> row and the per-namespace
/// <c>definitions</c> rows, by <see cref="Acta.WorkerRuntime"/> at <c>InitializeAsync</c> to write the <c>workers</c> row,
/// and by <see cref="Acta.WorkerRuntime.RunLoopAsync"/> to decide whether to enter the claim-poll loop.
/// The one-runtime-per-Run fan-out and the enqueue-only topology are documented in
/// docs/architecture-decisions.md (Boundaries).
/// </summary>
internal sealed record WorkerRegistration(
    string NamespaceName,
    string? OwnerTeam,
    string? Description,
    IReadOnlyList<ManifestRegistration> Manifests,
    IReadOnlyList<AlertChannelDeclaration> AlertChannels,
    OutboxRelayRegistration? Relay = null
);
