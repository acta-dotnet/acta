using Acta.Features.Namespaces;
using Acta.Features.Shared;
using Acta.Features.Tenants;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Tenants;

/// <summary>
/// Conformance for the tenant catalog upsert: <c>register_tenant</c> inserts a new tenant by key and
/// returns its DB-assigned id, returns the same id on re-registration (idempotent), updates description /
/// status on change, and stores a suspended status that the enqueue path later honors.
/// </summary>
[ConformanceSpec(
    "register-tenant.upsert",
    "Tenant registration is an idempotent upsert by key that returns a stable id",
    Area = "Catalog",
    Contract = "Registering a tenant returns a new id, re-registering returns the same id and updates its metadata, and suspended registration stores status Suspended.",
    Arrange = "A fresh tenant key exists only in the caller's hands.",
    Act = "The key is registered, re-registered with new metadata, and registered suspended, reading the stored row after each call.",
    Assert = "The first registration returns a new id, repeats return the same id with updated metadata, and the suspended registration stores Suspended."
)]
[CoversStoreMethod(typeof(ITenantStore), nameof(ITenantStore.RegisterTenantAsync))]
public abstract class RegisterTenantSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private IDbSession Store() => Db;

    private async Task<Tenant?> ReadAsync(string key, CancellationToken ct)
    {
        return await Db.From<Tenant>().Where(t => t.TenantKey == key).SingleOrDefaultAsync(ct);
    }

    [Fact(DisplayName = "A new tenant key inserts and returns a positive id with status Active")]
    public async Task New_tenant_inserts()
    {
        var ct = TestContext.Current.CancellationToken;
        // A normalized tenant key with uppercase: Acta keys fold to lowercase at write.
        var key = $"550E8400-{TestKey("tenant-new")}";
        var canonical = key.ToLowerInvariant();

        var id = await Services
            .GetRequiredService<TenantsService>()
            .RegisterAsync(key, null, "Acme Corp", status: TenantStatusCode.Active, ct);

        Assert.True(id > 0);
        var row = await ReadAsync(canonical, ct); // stored key is the folded form
        Assert.NotNull(row);
        Assert.Equal(id, row!.Id);
        Assert.Equal(canonical, row.TenantKey); // key is lowercased at write
        Assert.Equal("Acme Corp", row.Description);
        Assert.Equal(TenantStatusCode.Active, row.Status);
    }

    [Fact(DisplayName = "Re-registering the same key returns the same id (idempotent)")]
    public async Task Duplicate_key_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-dup");

        var first = await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, null, status: TenantStatusCode.Active, ct);
        var second = await Services
            .GetRequiredService<TenantsService>()
            .RegisterAsync(key, null, null, status: TenantStatusCode.Active, ct);

        Assert.Equal(first, second);
    }

    [Fact(DisplayName = "Re-registering with new metadata updates description and status but keeps the id")]
    public async Task Reregister_updates_metadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-update");

        var id = await Services
            .GetRequiredService<TenantsService>()
            .RegisterAsync(key, null, "before", status: TenantStatusCode.Active, ct);
        var again = await Services
            .GetRequiredService<TenantsService>()
            .RegisterAsync(key, null, "after", status: TenantStatusCode.Suspended, ct);

        Assert.Equal(id, again);
        var row = await ReadAsync(key, ct);
        Assert.NotNull(row);
        Assert.Equal("after", row!.Description);
        Assert.Equal(TenantStatusCode.Suspended, row.Status);
    }

    [Fact(DisplayName = "Registering suspended stores status Suspended")]
    public async Task Suspended_registration_stores_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-suspended");

        await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, null, status: TenantStatusCode.Suspended, ct);

        var row = await ReadAsync(key, ct);
        Assert.NotNull(row);
        Assert.Equal(TenantStatusCode.Suspended, row!.Status);
    }

    [Fact(DisplayName = "Registering with a display name reads it back")]
    public async Task New_tenant_with_display_name_inserts()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-display-name");

        await Services.GetRequiredService<TenantsService>().RegisterAsync(key, "Acme Corp", null, status: TenantStatusCode.Active, ct);

        var row = await ReadAsync(key, ct);
        Assert.NotNull(row);
        Assert.Equal("Acme Corp", row!.DisplayName);
    }

    [Fact(DisplayName = "Re-registering with a changed display name updates it and bumps the version")]
    public async Task Reregister_updates_display_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-display-name-update");

        await Services.GetRequiredService<TenantsService>().RegisterAsync(key, "Acme Corp", null, status: TenantStatusCode.Active, ct);
        var before = await ReadAsync(key, ct);

        await Services
            .GetRequiredService<TenantsService>()
            .RegisterAsync(key, "Acme Corporation", null, status: TenantStatusCode.Active, ct);
        var after = await ReadAsync(key, ct);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal("Acme Corporation", after!.DisplayName);
        Assert.True(after.Version > before!.Version);
    }

    [Fact(DisplayName = "Re-registering with a null display name overwrites it to null")]
    public async Task Reregister_null_display_name_clears()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-display-name-clear");

        await Services.GetRequiredService<TenantsService>().RegisterAsync(key, "Acme Corp", null, status: TenantStatusCode.Active, ct);
        await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, null, status: TenantStatusCode.Active, ct);

        var row = await ReadAsync(key, ct);
        Assert.NotNull(row);
        Assert.Null(row!.DisplayName);
    }
}
