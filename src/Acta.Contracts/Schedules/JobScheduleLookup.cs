namespace Acta;

/// <summary>
/// Addresses one named schedule for a control verb: the recurring slot <see cref="Job"/> it belongs to
/// plus the schedule's <see cref="ScheduleName"/>. The slot job is the recurring slot for a definition;
/// reach it by its public ref (dashboard), by its deduplication key which equals the job name
/// (programmatic), or by internal id. There is no public numeric schedule id.
/// </summary>
public sealed record JobScheduleLookup(JobLookup Job, string ScheduleName);
