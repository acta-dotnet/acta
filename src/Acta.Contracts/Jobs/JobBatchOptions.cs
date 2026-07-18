namespace Acta;

/// <summary>
/// Optional per-batch inputs for <see cref="IJobs.ControlBatchAsync"/>. <see cref="NextRunAtUtc"/> is
/// mandatory for <see cref="JobBatchAction.Reschedule"/>; <see cref="Priority"/> is mandatory for
/// <see cref="JobBatchAction.Reprioritize"/>. <see cref="ReasonMessage"/> is forwarded to every verb
/// except <see cref="JobBatchAction.Purge"/>, which (like <see cref="IJobs.PurgeAsync"/>) carries no
/// caller reason.
/// </summary>
public sealed record JobBatchOptions(DateTime? NextRunAtUtc = null, JobPriorityCode? Priority = null, string? ReasonMessage = null);

/// <summary>
/// The single control verb a <see cref="IJobs.ControlBatchAsync"/> call applies to every target.
/// </summary>
public enum JobBatchAction : byte
{
    /// <summary>See <see cref="IJobs.CancelAsync"/>.</summary>
    Cancel = 1,

    /// <summary>See <see cref="IJobs.PauseAsync"/>.</summary>
    Pause = 2,

    /// <summary>See <see cref="IJobs.ResumeAsync"/>.</summary>
    Resume = 3,

    /// <summary>See <see cref="IJobs.RestartAsync"/>.</summary>
    Restart = 4,

    /// <summary>See <see cref="IJobs.RescheduleAsync"/>. Requires <see cref="JobBatchOptions.NextRunAtUtc"/>.</summary>
    Reschedule = 5,

    /// <summary>See <see cref="IJobs.ReprioritizeAsync"/>. Requires <see cref="JobBatchOptions.Priority"/>.</summary>
    Reprioritize = 6,

    /// <summary>See <see cref="IJobs.PurgeAsync"/>. Ignores <see cref="JobBatchOptions.ReasonMessage"/>.</summary>
    Purge = 7,
}
