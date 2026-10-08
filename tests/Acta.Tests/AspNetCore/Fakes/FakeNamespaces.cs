namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="INamespaces"/> listing two rows, sys and billing. On the admin verbs a name of "sys" or "bad key" throws
/// like production, "missing" reports not found, and a patch with expectedVersion 999 conflicts.
/// </summary>
internal sealed class FakeNamespaces : INamespaces
{
    public FakeNamespaces()
    {
        List = static (_, _) =>
            ValueTask.FromResult(
                new PagedResult<NamespaceListItem>(
                    [
                        new NamespaceListItem(1, "sys", NamespaceStatusCode.Active, null, "system", 0),
                        new NamespaceListItem(2, "billing", NamespaceStatusCode.Active, "payments", "billing jobs", 3),
                    ],
                    null,
                    false,
                    50,
                    null
                )
            );
        Suspend = (name, reason, actor, _) =>
            Apply("suspend", name, reason, actor, static (action, version) => new SuspendNamespaceResponse(action, version));
        Resume = (name, reason, actor, _) =>
            Apply("resume", name, reason, actor, static (action, version) => new ResumeNamespaceResponse(action, version));
        Update = (name, expectedVersion, _, _, reason, actor, _) =>
            Apply("patch", name, reason, actor, static (action, version) => new UpdateNamespaceResponse(action, version), expectedVersion);
    }

    /// <summary>Applied admin verbs, named suspend, resume, or patch.</summary>
    public List<(string Verb, string Name, string? ReasonMessage, string? ActorKey)> AdminCalls { get; } = [];

    public Func<ListNamespaceItemsQuery, CancellationToken, ValueTask<PagedResult<NamespaceListItem>>> List { get; set; }

    public Func<string, string?, string?, CancellationToken, ValueTask<SuspendNamespaceResponse>> Suspend { get; set; }

    public Func<string, string?, string?, CancellationToken, ValueTask<ResumeNamespaceResponse>> Resume { get; set; }

    public Func<string, int, string?, string?, string?, string?, CancellationToken, ValueTask<UpdateNamespaceResponse>> Update { get; set; }

    ValueTask<PagedResult<NamespaceListItem>> INamespaces.ListAsync(ListNamespaceItemsQuery query, CancellationToken ct) => List(query, ct);

    ValueTask<SuspendNamespaceResponse> INamespaces.SuspendAsync(
        string name,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    ) => Suspend(name, reasonMessage, actorKey, ct);

    ValueTask<ResumeNamespaceResponse> INamespaces.ResumeAsync(
        string name,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    ) => Resume(name, reasonMessage, actorKey, ct);

    ValueTask<UpdateNamespaceResponse> INamespaces.UpdateAsync(
        string name,
        int expectedVersion,
        string? ownerTeam,
        string? description,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    ) => Update(name, expectedVersion, ownerTeam, description, reasonMessage, actorKey, ct);

    ValueTask<PagedResult<string>> INamespaces.ListNamesAsync(ListNamespacesQuery query, CancellationToken ct) =>
        throw new NotSupportedException();

    private ValueTask<T> Apply<T>(
        string verb,
        string name,
        string? reasonMessage,
        string? actorKey,
        Func<ControlAction, int?, T> response,
        int? expectedVersion = null
    )
    {
        if (name is "sys" or "bad key")
        {
            throw new ArgumentException("The system namespace sys cannot be suspended or edited.", nameof(name));
        }

        if (name == "missing")
        {
            return ValueTask.FromResult(response(ControlAction.NotFound, null));
        }

        if (expectedVersion == 999)
        {
            return ValueTask.FromResult(response(ControlAction.VersionConflict, 5));
        }

        AdminCalls.Add((verb, name, reasonMessage, actorKey));
        return ValueTask.FromResult(response(ControlAction.Applied, 2));
    }
}
