namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="INamespaces"/> listing two rows, sys and billing, and reading billing alone by name; a name that is no
/// namespace name, such as "bad key", throws the name validation error production throws, and so does "sys" on the admin
/// verbs. On those "missing" reports not found, and a patch with expectedVersion 999 conflicts. Every other verb applies
/// and answers the version after one change: billing's 3, or the expected version, plus one.
/// </summary>
internal sealed class FakeNamespaces : INamespaces
{
    public FakeNamespaces()
    {
        Get = static (jobNamespace, _) =>
        {
            NameSyntax.Canonicalize(jobNamespace, nameof(jobNamespace));
            return ValueTask.FromResult<GetNamespaceResponse?>(
                jobNamespace == "billing"
                    ? new GetNamespaceResponse(2, "billing", NamespaceStatusCode.Active, "payments", "billing jobs", "1.6.2+5c0ffee", 3)
                    : null
            );
        };
        List = static (_, _) =>
            ValueTask.FromResult(
                new ListNamespacesResponse(
                    [
                        new ListNamespacesItem(1, "sys", NamespaceStatusCode.Active, null, "system", null, 0),
                        new ListNamespacesItem(2, "billing", NamespaceStatusCode.Active, "payments", "billing jobs", "1.6.2+5c0ffee", 3),
                    ],
                    null,
                    false,
                    50,
                    null
                )
            );
        Suspend = (jobNamespace, reason, actor, _) =>
            Apply("suspend", jobNamespace, reason, actor, static (action, version) => new SuspendNamespaceResponse(action, version));
        Resume = (jobNamespace, reason, actor, _) =>
            Apply("resume", jobNamespace, reason, actor, static (action, version) => new ResumeNamespaceResponse(action, version));
        Update = (jobNamespace, expectedVersion, _, _, reason, actor, _) =>
            Apply(
                "patch",
                jobNamespace,
                reason,
                actor,
                static (action, version) => new UpdateNamespaceResponse(action, version),
                expectedVersion
            );
    }

    /// <summary>Applied admin verbs, named suspend, resume, or patch.</summary>
    public List<(string Verb, string NamespaceName, string? ReasonMessage, string? Actor)> AdminCalls { get; } = [];

    public Func<string, CancellationToken, ValueTask<GetNamespaceResponse?>> Get { get; set; }

    public Func<ListNamespacesQuery, CancellationToken, ValueTask<ListNamespacesResponse>> List { get; set; }

    public Func<string, string?, string?, CancellationToken, ValueTask<SuspendNamespaceResponse>> Suspend { get; set; }

    public Func<string, string?, string?, CancellationToken, ValueTask<ResumeNamespaceResponse>> Resume { get; set; }

    public Func<
        string,
        long,
        string?,
        string?,
        string?,
        string?,
        CancellationToken,
        ValueTask<UpdateNamespaceResponse>
    > Update { get; set; }

    ValueTask<GetNamespaceResponse?> INamespaces.GetAsync(string jobNamespace, CancellationToken ct) => Get(jobNamespace, ct);

    ValueTask<ListNamespacesResponse> INamespaces.ListAsync(ListNamespacesQuery query, CancellationToken ct) => List(query, ct);

    ValueTask<SuspendNamespaceResponse> INamespaces.SuspendAsync(
        string jobNamespace,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Suspend(jobNamespace, reasonMessage, actor, ct);

    ValueTask<ResumeNamespaceResponse> INamespaces.ResumeAsync(
        string jobNamespace,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Resume(jobNamespace, reasonMessage, actor, ct);

    ValueTask<UpdateNamespaceResponse> INamespaces.UpdateAsync(
        string jobNamespace,
        long expectedVersion,
        string? ownerTeam,
        string? description,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Update(jobNamespace, expectedVersion, ownerTeam, description, reasonMessage, actor, ct);

    private ValueTask<T> Apply<T>(
        string verb,
        string jobNamespace,
        string? reasonMessage,
        string? actor,
        Func<ControlAction, long?, T> response,
        long? expectedVersion = null
    )
    {
        _ = NameSyntax.Canonicalize(jobNamespace, nameof(jobNamespace), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        if (jobNamespace == "missing")
        {
            return ValueTask.FromResult(response(ControlAction.NotFound, null));
        }

        if (expectedVersion == 999)
        {
            return ValueTask.FromResult(response(ControlAction.VersionConflict, 5));
        }

        AdminCalls.Add((verb, jobNamespace, reasonMessage, actor));
        return ValueTask.FromResult(response(ControlAction.Applied, (expectedVersion ?? 3) + 1));
    }
}
