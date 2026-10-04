using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tenants;

/// <summary>
/// Conformance for <c>ListTenants</c>: a keyset page of tenants ordered tenant_key ascending, with an
/// opt-in filter-wide total, all in one round trip.
/// </summary>
[ConformanceSpec(
    "list-tenants.keyset-page",
    "ListTenants pages tenants key-ascending with an opt-in total",
    Area = "Reads",
    Contract = "ListTenants pages tenants by key ascending without duplicates and reads the page plus an opt-in filter-wide count in one command."
)]
public sealed class ListTenantsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ITenants Tenants => Services.GetRequiredService<ITenants>();

    [Fact(DisplayName = "Walking the cursor visits a registered tenant once with no duplicates")]
    public async Task Contains_registered_tenant_with_no_duplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-list");
        await Tenants.RegisterAsync(key, null, "Acme Corp", ct);

        // Keyset paging is self-consistent under the database's own collation (the cursor predicate and
        // ORDER BY share it), so we assert coverage and uniqueness rather than a client-side string order.
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Tenants.ListAsync(new ListTenantsQuery(PageSize: 50, Cursor: cursor), ct);
            seen.AddRange(page.Items.Select(r => r.TenantKey));
            cursor = page.NextCursor;
            Assert.True(++pages < 100_000, "pagination did not terminate");
        } while (cursor is not null);

        Assert.Contains(key, seen);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(DisplayName = "The list row carries the tenant's optimistic-concurrency version")]
    public async Task Row_carries_row_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-version");
        await Tenants.RegisterAsync(key, null, "Acme Corp", ct);

        // Bump the row version off its default so a broken projection cannot pass by reading a stray 0.
        var suspended = await Tenants.SuspendAsync(key, null, null, ct);
        Assert.Equal(ControlAction.Applied, suspended.Action);
        Assert.NotNull(suspended.Version);

        var row = await FindAsync(key, ct);
        Assert.Equal(suspended.Version, row.Version);
    }

    [Fact(DisplayName = "The list row carries the tenant's display name and description")]
    public async Task Row_carries_display_name_and_description()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tenant-metadata");
        await Tenants.RegisterAsync(key, "Acme Display", "Acme Corp", ct);

        var row = await FindAsync(key, ct);
        Assert.Equal("Acme Display", row.DisplayName);
        Assert.Equal("Acme Corp", row.Description);
    }

    [Fact(DisplayName = "Search treats provider pattern characters as literal text")]
    public async Task Search_treats_pattern_characters_literally()
    {
        var ct = TestContext.Current.CancellationToken;

        foreach (var (name, special, replacement) in new[] { ("percent", "%", "x"), ("underscore", "_", "x"), ("bracket", "[", "x") })
        {
            var token = TestKey($"tenant-search-{name}");
            var matchingKey = TestKey($"tenant-search-{name}-matching");
            var controlKey = TestKey($"tenant-search-{name}-control");
            await Tenants.RegisterAsync(matchingKey, $"{token}{special}suffix", null, ct);
            await Tenants.RegisterAsync(controlKey, $"{token}{replacement}suffix", null, ct);

            var page = await Tenants.ListAsync(new ListTenantsQuery(NameContains: token + special, PageSize: 100), ct);

            Assert.Contains(page.Items, row => row.TenantKey == matchingKey);
            Assert.DoesNotContain(page.Items, row => row.TenantKey == controlKey);
        }
    }

    [Fact(DisplayName = "IncludeTotal returns the filter-wide count and is opt-in")]
    public async Task Total_is_opt_in()
    {
        var ct = TestContext.Current.CancellationToken;
        await Tenants.RegisterAsync(TestKey("tenant-total"), null, null, ct);

        Assert.Null((await Tenants.ListAsync(new ListTenantsQuery(PageSize: 1), ct)).TotalCount);

        var total = (await Tenants.ListAsync(new ListTenantsQuery(PageSize: 50, IncludeTotal: true), ct)).TotalCount;
        Assert.NotNull(total);
        Assert.True(total >= 1);
    }

    // The key is this run's own, so filtering on it reaches the row in a database holding earlier runs.
    private async Task<TenantListItem> FindAsync(string key, CancellationToken ct) =>
        (await Tenants.ListAsync(new ListTenantsQuery(NameContains: key), ct)).Items.Single(r => r.TenantKey == key);
}
