using Acta.Features.Namespaces;
using Acta.Features.Shared;
using Acta.Features.Tenants;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Tenants;

/// <summary>
/// Conformance for <c>ListTenants</c>: a keyset page of tenants ordered tenant_key ascending, with an
/// opt-in filter-wide total, all in one round trip.
/// </summary>
[ConformanceSpec(
    "list-tenants.keyset-page",
    "ListTenants pages tenants key-ascending with an opt-in total",
    Area = "Reads",
    Contract = "ListTenants pages tenants by key ascending without duplicates and reads the page plus an opt-in filter-wide count in one command.",
    Arrange = "One tenant is registered with an Active status.",
    Act = "Tenants are paged by cursor and read with and without IncludeTotal.",
    Assert = "The walk visits the registered tenant once with no duplicates and the opt-in total arrives with the page."
)]
[CoversStoreMethod(typeof(ITenantStore), nameof(ITenantStore.ListTenantsAsync))]
public abstract class ListTenantsSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private IDbSession Store() => Db;

    [Fact(DisplayName = "Walking the cursor visits a registered tenant once with no duplicates")]
    public async Task Contains_registered_tenant_with_no_duplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = Store();
        var key = TestKey("tenant-list");
        await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, "Acme Corp", ct);

        // Keyset paging is self-consistent under the database's own collation (the cursor predicate and
        // ORDER BY share it), so we assert coverage and uniqueness rather than a client-side string order.
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page__ = await Services
                .GetRequiredService<ITenantStore>()
                .ListTenantsAsync(new TenantPageRequest(null, null, cursor, 50, false), ct);
            var (rows, _) = (page__.Rows, page__.Total);
            seen.AddRange(rows.Select(r => r.TenantKey));
            cursor = rows.Count == 50 ? rows[^1].TenantKey : null;
            Assert.True(++pages < 100_000, "pagination did not terminate");
        } while (cursor is not null);

        Assert.Contains(key, seen);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(DisplayName = "The list row carries the tenant's optimistic-concurrency version")]
    public async Task Row_carries_row_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenants = Services.GetRequiredService<TenantsService>();
        var key = TestKey("tenant-version");
        await tenants.RegisterAsync(key, null, "Acme Corp", ct);

        // Bump the row version off its default so a broken projection cannot pass by reading a stray 0.
        var suspended = await tenants.SuspendAsync(key, null, null, ct);
        Assert.Equal(AdminControlAction.Applied, suspended.Action);
        Assert.NotNull(suspended.Version);

        // The shared provider DB accumulates tenants across runs, so page by cursor to reach our row.
        var store = Services.GetRequiredService<ITenantStore>();
        TenantListItem? row = null;
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await store.ListTenantsAsync(new TenantPageRequest(null, null, cursor, 50, false), ct);
            row = page.Rows.FirstOrDefault(r => r.TenantKey == key);
            cursor = row is null && page.Rows.Count == 50 ? page.Rows[^1].TenantKey : null;
            Assert.True(++pages < 100_000, "pagination did not terminate");
        } while (row is null && cursor is not null);

        Assert.NotNull(row);
        Assert.Equal(suspended.Version, row.Version);
    }

    [Fact(DisplayName = "The list row carries the tenant's display name and description")]
    public async Task Row_carries_display_name_and_description()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenants = Services.GetRequiredService<TenantsService>();
        var key = TestKey("tenant-metadata");
        await tenants.RegisterAsync(key, "Acme Display", "Acme Corp", ct);

        // The shared provider DB accumulates tenants across runs, so page by cursor to reach our row.
        var store = Services.GetRequiredService<ITenantStore>();
        TenantListItem? row = null;
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await store.ListTenantsAsync(new TenantPageRequest(null, null, cursor, 50, false), ct);
            row = page.Rows.FirstOrDefault(r => r.TenantKey == key);
            cursor = row is null && page.Rows.Count == 50 ? page.Rows[^1].TenantKey : null;
            Assert.True(++pages < 100_000, "pagination did not terminate");
        } while (row is null && cursor is not null);

        Assert.NotNull(row);
        Assert.Equal("Acme Display", row.DisplayName);
        Assert.Equal("Acme Corp", row.Description);
    }

    [Fact(DisplayName = "Search treats provider pattern characters as literal text")]
    public async Task Search_treats_pattern_characters_literally()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenants = Services.GetRequiredService<TenantsService>();

        foreach (var (name, special, replacement) in new[] { ("percent", "%", "x"), ("underscore", "_", "x"), ("bracket", "[", "x") })
        {
            var token = TestKey($"tenant-search-{name}");
            var matchingKey = TestKey($"tenant-search-{name}-matching");
            var controlKey = TestKey($"tenant-search-{name}-control");
            await tenants.RegisterAsync(matchingKey, $"{token}{special}suffix", null, ct);
            await tenants.RegisterAsync(controlKey, $"{token}{replacement}suffix", null, ct);

            var page = await tenants.ListAsync(new ListTenantsQuery(Search: token + special, PageSize: 100), ct);

            Assert.Contains(page.Items, row => row.TenantKey == matchingKey);
            Assert.DoesNotContain(page.Items, row => row.TenantKey == controlKey);
        }
    }

    [Fact(DisplayName = "IncludeTotal returns the filter-wide count and is opt-in")]
    public async Task Total_is_opt_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = Store();
        await Services.GetRequiredService<TenantsService>().RegisterAsync(TestKey("tenant-total"), null, null, ct);

        var page_noTotal = await Services
            .GetRequiredService<ITenantStore>()
            .ListTenantsAsync(new TenantPageRequest(null, null, null, 1, false), ct);
        var (_, noTotal) = (page_noTotal.Rows, page_noTotal.Total);
        Assert.Null(noTotal);

        var page_total = await Services
            .GetRequiredService<ITenantStore>()
            .ListTenantsAsync(new TenantPageRequest(null, null, null, 50, true), ct);
        var (_, total) = (page_total.Rows, page_total.Total);
        Assert.NotNull(total);
        Assert.True(total >= 1);
    }
}
