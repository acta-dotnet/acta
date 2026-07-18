namespace Acta.Demos.ApiWorkerSplit.Contracts;

// The durable route the API enqueues against: the (namespace, job-name) pair, shared across processes.
// The handler lives in the worker.
public static class WelcomeEmailRoute
{
    public const string Namespace = "welcome-emails";
    public const string JobName = "send-welcome-email";
}

public sealed record SendWelcomeEmail(string UserId, string Email, string Name);

public sealed record WelcomeEmailAccepted(JobRef JobRef, string Action, string StatusUrl, string CorrelationKey);

public sealed record WelcomeEmailStatus(
    JobRef JobRef,
    string Status,
    string? DeduplicationKey,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
