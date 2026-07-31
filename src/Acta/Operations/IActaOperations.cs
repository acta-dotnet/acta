namespace Acta;

/// <summary>
/// The operator and administration facade: resource-oriented domains (schedules, definitions,
/// workers, alerts, tenants, namespaces, tags), the overview read, and runtime capabilities.
/// Application code injects <see cref="IJobs"/> alone; dashboards, CLIs, and operator hosts inject
/// this. The two facades never overlap.
/// </summary>
public interface IActaOperations
{
    /// <summary>Schedules domain (pause/resume/list). See <see cref="ISchedules"/>.</summary>
    ISchedules Schedules { get; }

    /// <summary>Job definitions domain (overrides/detail/list). See <see cref="IDefinitions"/>.</summary>
    IDefinitions Definitions { get; }

    /// <summary>Workers domain (list). See <see cref="IWorkers"/>.</summary>
    IWorkers Workers { get; }

    /// <summary>Alerts domain (list). See <see cref="IAlerts"/>.</summary>
    IAlerts Alerts { get; }

    /// <summary>Tenants domain (register/list). See <see cref="ITenants"/>.</summary>
    ITenants Tenants { get; }

    /// <summary>Namespaces domain (list/suspend/resume/metadata). See <see cref="INamespaces"/>.</summary>
    INamespaces Namespaces { get; }

    /// <summary>Exact searchable metadata attachments. See <see cref="ITags"/>.</summary>
    ITags Tags { get; }

    /// <summary>List jobs newest first, optionally filtered by namespace, status, definition, tenant, correlation id, or tags.</summary>
    ValueTask<PagedResult<JobListItem>> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default);

    /// <summary>List audit events newest first, optionally scoped to a job, lineage, namespace, or event code.</summary>
    ValueTask<PagedResult<JobEventListItem>> ListJobEventsAsync(ListJobEventsQuery query, CancellationToken ct = default);

    /// <summary>One-shot dashboard health counters, optionally scoped to a namespace.</summary>
    ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct = default);

    /// <summary>The durable provider backing this runtime (surfaced by the capabilities read).</summary>
    DbProvider Provider { get; }
}
