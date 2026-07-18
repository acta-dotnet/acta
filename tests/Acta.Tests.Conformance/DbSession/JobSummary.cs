namespace Acta.Tests.Conformance.DbSession;

/// <summary>
/// Column-pruned projection over <see cref="Job"/>. Constructor parameter names match the
/// <c>[DbColumn]</c> CLR property names on <c>JobRuntime</c> - the reflection-based reader
/// (<c>session.From&lt;JobRuntime, JobSummary&gt;()</c>) maps each parameter to its source column by name.
/// </summary>
public sealed record JobSummary(long Id, JobStatusCode Status);
