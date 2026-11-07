namespace Acta.AspNetCore;

/// <summary>The permissions an Acta operation can need, one per operation.</summary>
public static class ActaPermissions
{
    /// <summary>Every read, and the dashboard's pages.</summary>
    public const string Read = "acta.read";

    /// <summary>Enqueueing a job.</summary>
    public const string Enqueue = "acta.enqueue";

    /// <summary>Acting on one job, schedule, or alert, or requeueing quarantined outbox rows.</summary>
    public const string Control = "acta.control";

    /// <summary>
    /// Deleting data, changing configuration, and stopping a namespace or tenant; tag writes on namespaces, tenants, and
    /// definitions included.
    /// </summary>
    public const string Admin = "acta.admin";
}
