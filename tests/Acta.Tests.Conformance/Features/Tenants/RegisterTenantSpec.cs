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
/// Conformance for tenant registration: <c>register_tenant</c> inserts a new Active tenant by key and
/// returns its DB-assigned id, and on an existing key returns the same id while leaving the stored
/// status, metadata, and version untouched. Status changes belong to suspend/resume, metadata to the
/// version-guarded metadata update.
/// </summary>
[ConformanceSpec(
    "register-tenant.insert-or-get",
    "Tenant registration inserts a new Active tenant or returns the existing row",
    Area = "Catalog",
    Contract = "Registering a new tenant inserts it Active and returns a new id, and re-registering returns the same id without changing status, metadata, or version.",
    Arrange = "A fresh tenant key exists only in the caller's hands, optionally suspended after its first registration.",
    Act = "The key is registered, then registered again with different metadata, reading the stored row after each call.",
    Assert = "The first registration returns a new Active row and repeats return the same id with status, metadata, and version unchanged."
)]
[CoversStoreMethod(typeof(ITenantStore), nameof(ITenantStore.RegisterTenantAsync))]
public abstract class RegisterTenantSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private TenantsService Tenants() => Services.GetRequiredService<TenantsService>();

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

        var id = await Tenants().RegisterAsync(key, null, "Acme Corp", ct);

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

        var first = await Tenants().RegisterAsync(key, null, null, ct);
        var second = await Tenants().RegisterAsync(key, null, null, ct);

        Assert.Equal(first, second);
    }

    [Fact(DisplayName = "Re-registering an existing key leaves metadata and version untouched")]
    public async Task Reregister_leaves_existing_row_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-keep");

        var id = await Tenants().RegisterAsync(key, "Acme Corp", "before", ct);
        var before = await ReadAsync(key, ct);

        var again = await Tenants().RegisterAsync(key, "Other Name", "after", ct);
        var after = await ReadAsync(key, ct);

        Assert.Equal(id, again);
        Assert.NotNull(after);
        Assert.Equal("Acme Corp", after!.DisplayName);
        Assert.Equal("before", after.Description);
        Assert.Equal(before!.Version, after.Version);
    }

    [Fact(DisplayName = "Re-registering a suspended tenant does not resume it")]
    public async Task Reregister_does_not_resume_suspended_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-stays-suspended");

        var id = await Tenants().RegisterAsync(key, null, null, ct);
        await Tenants().SuspendAsync(key, "hold", null, ct);
        var suspended = await ReadAsync(key, ct);

        var again = await Tenants().RegisterAsync(key, "New Name", "new-desc", ct);
        var after = await ReadAsync(key, ct);

        Assert.Equal(id, again);
        Assert.NotNull(after);
        Assert.Equal(TenantStatusCode.Suspended, after!.Status);
        Assert.Null(after.DisplayName);
        Assert.Null(after.Description);
        Assert.Equal(suspended!.Version, after.Version);
    }

    [Fact(DisplayName = "Concurrent same-key registrations all return the same id")]
    public async Task Concurrent_registration_converges()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-race");

        // Overlapping insert-or-get calls must all converge on the winner's row; a losing arm
        // returning no row (or a different id) is the failure mode this pins per provider.
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Tenants().RegisterAsync(key, null, null, ct).AsTask()));

        Assert.True(ids[0] > 0);
        Assert.All(ids, id => Assert.Equal(ids[0], id));
    }

    [Fact(DisplayName = "Registering with a display name reads it back")]
    public async Task New_tenant_with_display_name_inserts()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-display-name");

        await Tenants().RegisterAsync(key, "Acme Corp", null, ct);

        var row = await ReadAsync(key, ct);
        Assert.NotNull(row);
        Assert.Equal("Acme Corp", row!.DisplayName);
    }

    [Fact(DisplayName = "The bare reserved tenant key 'sys' is rejected")]
    public async Task Bare_sys_tenant_key_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(async () => await Tenants().RegisterAsync("sys", null, null, ct));
    }
}
