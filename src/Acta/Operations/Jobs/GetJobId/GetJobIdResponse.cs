namespace Acta;

/// <summary>The internal id a <see cref="JobLookup"/> resolves to, returned by <see cref="IJobs.GetIdAsync"/>.</summary>
public sealed record GetJobIdResponse(long JobId);
