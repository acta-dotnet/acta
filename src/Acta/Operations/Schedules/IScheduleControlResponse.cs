namespace Acta;

/// <summary>
/// What a schedule control verb did, and on an attempt that reached the schedule its resulting state: the
/// status, the timed-pause expiry, the per-schedule cursor, and the row version for optimistic concurrency.
/// </summary>
public interface IScheduleControlResponse : IControlResponse
{
    /// <summary>The schedule's status after the attempt.</summary>
    ScheduleStatusCode? Status { get; }

    /// <summary>When a timed pause expires; null for an indefinite pause or after resume.</summary>
    DateTime? PausedUntilUtc { get; }

    /// <summary>The schedule's cursor after the attempt.</summary>
    DateTime? NextRunAtUtc { get; }
}
