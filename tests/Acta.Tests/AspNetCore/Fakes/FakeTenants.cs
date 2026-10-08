namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="ITenants"/> over one tenant, "cust-001" (id 1). A key of "bad key" throws the opaque-key validation error
/// like production, and so does "sys" on the admin verbs; "missing" reports not found, and a patch with expectedVersion
/// 999 conflicts.
/// </summary>
internal sealed class FakeTenants : ITenants
{
    public FakeTenants()
    {
        Register = (tenantKey, displayName, description, _) =>
        {
            // Mirror the production guard: an opaque-key validation failure surfaces as ArgumentException.
            if (tenantKey == "bad key")
            {
                throw new ArgumentException("Tenant key must not contain whitespace.", nameof(tenantKey));
            }

            RegisterCalls.Add((tenantKey, displayName, description));
            return ValueTask.FromResult(new RegisterTenantResponse(7, tenantKey));
        };
        Get = static (tenantKey, _) =>
            ValueTask.FromResult<GetTenantResponse?>(
                tenantKey == "cust-001"
                    ? new GetTenantResponse(1, "cust-001", "Acme", "Acme Corp", TenantStatusCode.Active, default, default, 0)
                    : null
            );
        List = static (_, _) =>
            ValueTask.FromResult(
                new ListTenantsResponse(
                    [new ListTenantsItem(1, "cust-001", "Acme", "Acme Corp", TenantStatusCode.Active, default, default, 0)],
                    null,
                    false,
                    50,
                    null
                )
            );
        Suspend = (tenantKey, reason, actor, _) =>
            Apply(tenantKey, reason, actor, static (action, version) => new SuspendTenantResponse(action, version));
        Resume = (tenantKey, reason, actor, _) =>
            Apply(tenantKey, reason, actor, static (action, version) => new ResumeTenantResponse(action, version));
        Update = (tenantKey, expectedVersion, _, _, reason, actor, _) =>
            Apply(tenantKey, reason, actor, static (action, version) => new UpdateTenantResponse(action, version), expectedVersion);
    }

    public List<(string TenantKey, string? DisplayName, string? Description)> RegisterCalls { get; } = [];

    /// <summary>Applied suspend, resume, and patch calls.</summary>
    public List<(string TenantKey, string? ReasonMessage, string? ActorKey)> SuspendResumeCalls { get; } = [];

    public Func<string, string?, string?, CancellationToken, ValueTask<RegisterTenantResponse>> Register { get; set; }

    public Func<string, CancellationToken, ValueTask<GetTenantResponse?>> Get { get; set; }

    public Func<ListTenantsQuery, CancellationToken, ValueTask<ListTenantsResponse>> List { get; set; }

    public Func<string, string?, string?, CancellationToken, ValueTask<SuspendTenantResponse>> Suspend { get; set; }

    public Func<string, string?, string?, CancellationToken, ValueTask<ResumeTenantResponse>> Resume { get; set; }

    public Func<string, int, string?, string?, string?, string?, CancellationToken, ValueTask<UpdateTenantResponse>> Update { get; set; }

    ValueTask<RegisterTenantResponse> ITenants.RegisterAsync(
        string tenantKey,
        string? displayName,
        string? description,
        CancellationToken ct
    ) => Register(tenantKey, displayName, description, ct);

    ValueTask<GetTenantResponse?> ITenants.GetAsync(string tenantKey, CancellationToken ct) => Get(tenantKey, ct);

    ValueTask<ListTenantsResponse> ITenants.ListAsync(ListTenantsQuery query, CancellationToken ct) => List(query, ct);

    ValueTask<SuspendTenantResponse> ITenants.SuspendAsync(
        string tenantKey,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    ) => Suspend(tenantKey, reasonMessage, actorKey, ct);

    ValueTask<ResumeTenantResponse> ITenants.ResumeAsync(string tenantKey, string? reasonMessage, string? actorKey, CancellationToken ct) =>
        Resume(tenantKey, reasonMessage, actorKey, ct);

    ValueTask<UpdateTenantResponse> ITenants.UpdateAsync(
        string tenantKey,
        int expectedVersion,
        string? displayName,
        string? description,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    ) => Update(tenantKey, expectedVersion, displayName, description, reasonMessage, actorKey, ct);

    private ValueTask<T> Apply<T>(
        string tenantKey,
        string? reasonMessage,
        string? actorKey,
        Func<ControlAction, int?, T> response,
        int? expectedVersion = null
    )
    {
        if (tenantKey is "bad key" or "sys")
        {
            throw new ArgumentException("Tenant key must not contain whitespace.", nameof(tenantKey));
        }

        if (tenantKey == "missing")
        {
            return ValueTask.FromResult(response(ControlAction.NotFound, null));
        }

        if (expectedVersion == 999)
        {
            return ValueTask.FromResult(response(ControlAction.VersionConflict, 5));
        }

        SuspendResumeCalls.Add((tenantKey, reasonMessage, actorKey));
        return ValueTask.FromResult(response(ControlAction.Applied, 2));
    }
}
