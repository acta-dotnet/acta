namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="ITenants"/> over one tenant, "cust-001" (id 1). A name that is no tenant name, such as "bad key", throws the
/// name validation error production throws, and so does "sys" on the registration and the admin verbs; "missing" reports
/// not found, and a patch with expectedVersion 999 conflicts. Every other verb applies and answers the version after one
/// change: cust-001's 0, or the expected version, plus one.
/// </summary>
internal sealed class FakeTenants : ITenants
{
    private static readonly DateTime CreatedAtUtc = new(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc);

    public FakeTenants()
    {
        Register = (tenantName, displayName, description, _) =>
        {
            NameSyntax.Canonicalize(tenantName, nameof(tenantName), NameSyntax.ExtendedMaxLength, reserveSystem: true);
            RegisterCalls.Add((tenantName, displayName, description));
            return ValueTask.FromResult(new RegisterTenantResponse(7, tenantName));
        };
        Get = static (tenantName, _) =>
        {
            NameSyntax.Canonicalize(tenantName, nameof(tenantName), NameSyntax.ExtendedMaxLength);
            return ValueTask.FromResult<GetTenantResponse?>(
                tenantName == "cust-001"
                    ? new GetTenantResponse(1, "cust-001", "Acme", "Acme Corp", TenantStatusCode.Active, CreatedAtUtc, CreatedAtUtc, 0)
                    : null
            );
        };
        List = static (_, _) =>
            ValueTask.FromResult(
                new ListTenantsResponse(
                    [new ListTenantsItem(1, "cust-001", "Acme", "Acme Corp", TenantStatusCode.Active, CreatedAtUtc, CreatedAtUtc, 0)],
                    null,
                    false,
                    50,
                    null
                )
            );
        Suspend = (tenantName, reason, actor, _) =>
            Apply(tenantName, reason, actor, static (action, version) => new SuspendTenantResponse(action, version));
        Resume = (tenantName, reason, actor, _) =>
            Apply(tenantName, reason, actor, static (action, version) => new ResumeTenantResponse(action, version));
        Update = (tenantName, expectedVersion, _, _, reason, actor, _) =>
            Apply(tenantName, reason, actor, static (action, version) => new UpdateTenantResponse(action, version), expectedVersion);
    }

    public List<(string TenantName, string? DisplayName, string? Description)> RegisterCalls { get; } = [];

    /// <summary>Applied suspend, resume, and patch calls.</summary>
    public List<(string TenantName, string? ReasonMessage, string? Actor)> SuspendResumeCalls { get; } = [];

    public Func<string, string?, string?, CancellationToken, ValueTask<RegisterTenantResponse>> Register { get; set; }

    public Func<string, CancellationToken, ValueTask<GetTenantResponse?>> Get { get; set; }

    public Func<ListTenantsQuery, CancellationToken, ValueTask<ListTenantsResponse>> List { get; set; }

    public Func<string, string?, string?, CancellationToken, ValueTask<SuspendTenantResponse>> Suspend { get; set; }

    public Func<string, string?, string?, CancellationToken, ValueTask<ResumeTenantResponse>> Resume { get; set; }

    public Func<string, int, string?, string?, string?, string?, CancellationToken, ValueTask<UpdateTenantResponse>> Update { get; set; }

    ValueTask<RegisterTenantResponse> ITenants.RegisterAsync(
        string tenantName,
        string? displayName,
        string? description,
        CancellationToken ct
    ) => Register(tenantName, displayName, description, ct);

    ValueTask<GetTenantResponse?> ITenants.GetAsync(string tenantName, CancellationToken ct) => Get(tenantName, ct);

    ValueTask<ListTenantsResponse> ITenants.ListAsync(ListTenantsQuery query, CancellationToken ct) => List(query, ct);

    ValueTask<SuspendTenantResponse> ITenants.SuspendAsync(string tenantName, string? reasonMessage, string? actor, CancellationToken ct) =>
        Suspend(tenantName, reasonMessage, actor, ct);

    ValueTask<ResumeTenantResponse> ITenants.ResumeAsync(string tenantName, string? reasonMessage, string? actor, CancellationToken ct) =>
        Resume(tenantName, reasonMessage, actor, ct);

    ValueTask<UpdateTenantResponse> ITenants.UpdateAsync(
        string tenantName,
        int expectedVersion,
        string? displayName,
        string? description,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Update(tenantName, expectedVersion, displayName, description, reasonMessage, actor, ct);

    private ValueTask<T> Apply<T>(
        string tenantName,
        string? reasonMessage,
        string? actor,
        Func<ControlAction, int?, T> response,
        int? expectedVersion = null
    )
    {
        _ = NameSyntax.Canonicalize(tenantName, nameof(tenantName), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        if (tenantName == "missing")
        {
            return ValueTask.FromResult(response(ControlAction.NotFound, null));
        }

        if (expectedVersion == 999)
        {
            return ValueTask.FromResult(response(ControlAction.VersionConflict, 5));
        }

        SuspendResumeCalls.Add((tenantName, reasonMessage, actor));
        return ValueTask.FromResult(response(ControlAction.Applied, (expectedVersion ?? 0) + 1));
    }
}
