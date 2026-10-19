namespace Acta;

/// <summary>
/// Resolved enqueue route for a job input type: the namespace it was registered under, its job name,
/// and the wire format its input serializes to.
/// </summary>
internal readonly record struct JobRoute(string Namespace, string JobName, JobPayloadFormat InputFormat);
