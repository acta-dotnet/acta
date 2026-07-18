namespace Acme.Shop.Api;

// Lifecycle-only projection returned by the status endpoints: job run state, not order business state
// (the App's order store owns that).
public sealed record JobLifecycleStatus(string JobRef, string Status, DateTime CreatedAtUtc, DateTime ModifiedAtUtc);
