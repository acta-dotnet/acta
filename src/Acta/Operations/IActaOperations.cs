namespace Acta;

/// <summary>
/// The operator and administration facade: resource-oriented domains (schedules, definitions,
/// workers, alerts, tenants, namespaces, tags), the ledger reads, and runtime capabilities.
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

    /// <summary>Namespaces domain (list/suspend/resume/update). See <see cref="INamespaces"/>.</summary>
    INamespaces Namespaces { get; }

    /// <summary>Exact searchable metadata attachments. See <see cref="ITags"/>.</summary>
    ITags Tags { get; }

    /// <summary>Durable settings domain (get/set). See <see cref="ISettings"/>.</summary>
    ISettings Settings { get; }

    /// <summary>Cross-resource ledger reads (job list, audit trail, overview). See <see cref="ILedger"/>.</summary>
    ILedger Ledger { get; }

    /// <summary>The durable provider backing this runtime (surfaced by the capabilities read).</summary>
    DbProvider Provider { get; }
}
