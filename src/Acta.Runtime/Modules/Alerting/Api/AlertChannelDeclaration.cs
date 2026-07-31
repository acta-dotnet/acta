using Acta.Runtime.Hosting;

namespace Acta.Runtime.Modules.Alerting.Api;

/// <summary>
/// One <c>IWorkerBuilder.AddAlertChannel</c> declaration, carried on <see cref="WorkerRegistration"/> and
/// resolved from the worker's in-memory alert channel registry at delivery time.
/// </summary>
internal sealed record AlertChannelDeclaration(
    string Name,
    string TransportKind,
    string Endpoint,
    AlertChannelStatusCode Status,
    AlertSeverityCode MinSeverity
);
