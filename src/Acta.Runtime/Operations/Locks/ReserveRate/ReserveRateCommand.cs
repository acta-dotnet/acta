namespace Acta.Runtime;

/// <summary>One turn to spend from a rate meter for a job.</summary>
internal sealed record ReserveRateCommand(string BucketKey, long JobId, int IntervalMilliseconds, int Burst, int GraceSeconds);
