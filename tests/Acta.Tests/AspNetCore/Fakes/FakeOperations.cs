namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// The whole fake world behind the dashboard host: one fake per public facade, plus <see cref="Jobs"/>, which the host
/// registers as <see cref="IJobs"/>. Every facade member the HTTP API calls forwards to a settable delegate named after
/// it without Async, whose default answers from the fake's canned state; a test bends one behaviour by replacing that
/// delegate. Members the API never calls throw <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class FakeOperations : IActaOperations
{
    public FakeJobs Jobs { get; } = new();

    public FakeLedger Ledger { get; } = new();

    public FakeSchedules Schedules { get; } = new();

    public FakeDefinitions Definitions { get; } = new();

    public FakeWorkers Workers { get; } = new();

    public FakeAlerts Alerts { get; } = new();

    public FakeTenants Tenants { get; } = new();

    public FakeNamespaces Namespaces { get; } = new();

    public FakeTags Tags { get; } = new();

    public FakeOutbox Outbox { get; } = new();

    public DbProvider Provider => DbProvider.Sqlite;

    ILedger IActaOperations.Ledger => Ledger;

    ISchedules IActaOperations.Schedules => Schedules;

    IDefinitions IActaOperations.Definitions => Definitions;

    IWorkers IActaOperations.Workers => Workers;

    IAlerts IActaOperations.Alerts => Alerts;

    ITenants IActaOperations.Tenants => Tenants;

    INamespaces IActaOperations.Namespaces => Namespaces;

    ITags IActaOperations.Tags => Tags;

    IOutbox IActaOperations.Outbox => Outbox;

    ISettings IActaOperations.Settings => throw new NotSupportedException();
}
