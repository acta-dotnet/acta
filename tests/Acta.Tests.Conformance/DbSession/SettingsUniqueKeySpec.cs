using System.Data.Common;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.DbSession;

/// <summary>
/// Conformance for the central <c>settings</c> table's natural identity: the filtered unique pair
/// (<c>ux_settings_scope_name</c> for non-NULL <c>scope_id</c>, <c>ux_settings_global_name</c> for
/// the NULL-scope global form) admits exactly one row per <c>(scope_code, scope_id, name)</c> on
/// every provider, closing the NULL-comparison divergence between the three databases.
/// </summary>
[ConformanceSpec(
    "settings.unique-key",
    "Settings rows are unique per (scope_code, scope_id, name)",
    Area = "Settings",
    Contract = "The settings table admits one row per (scope_code, scope_id, name), including the NULL-scope global form, on every provider.",
    Arrange = "A live provider schema exposes the settings table with its filtered unique pair over (scope_code, scope_id, name).",
    Act = "Scoped and NULL-scope global settings rows are inserted twice each through the IDbSession seam, along with distinct names and scope ids.",
    Assert = "Each duplicate insert is rejected while distinct names and scope ids insert cleanly on every provider."
)]
public abstract class SettingsUniqueKeySpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static Setting NewSetting(SettingScopeCode scope, int? scopeId, string name) =>
        new()
        {
            ScopeCode = scope,
            ScopeId = scopeId,
            Name = name,
            ValueFormatCode = JobPayloadFormat.Text.Id,
            Value = "on"u8.ToArray(),
            ModifiedAtUtc = DateTime.UtcNow,
        };

    [Fact(DisplayName = "A duplicate scoped setting is rejected while a different name or scope id inserts cleanly")]
    public async Task Scoped_settings_are_unique_per_scope_and_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"sys.test.{TestId}.scoped";

        var id = await Db.From<Setting>().InsertAsync<int>(NewSetting(SettingScopeCode.Namespace, TestNamespaceId, name), ct);
        Assert.NotEqual(0, id);

        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.From<Setting>().InsertAsync<int>(NewSetting(SettingScopeCode.Namespace, TestNamespaceId, name), ct)
        );

        // A different name under the same scope, and the same name under a different scope id, both insert.
        var other = await Db.From<Setting>().InsertAsync<int>(NewSetting(SettingScopeCode.Namespace, TestNamespaceId, $"{name}-b"), ct);
        Assert.NotEqual(0, other);
        var otherScope = await Db.From<Setting>().InsertAsync<int>(NewSetting(SettingScopeCode.Namespace, TestNamespaceId + 1, name), ct);
        Assert.NotEqual(0, otherScope);
    }

    [Fact(DisplayName = "A duplicate global (NULL scope_id) setting is rejected on every provider")]
    public async Task Global_settings_are_unique_per_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"sys.test.{TestId}.global";

        var id = await Db.From<Setting>().InsertAsync<int>(NewSetting(SettingScopeCode.Global, null, name), ct);
        Assert.NotEqual(0, id);

        // Providers disagree on NULL equality in plain unique indexes; the filtered global index
        // must reject the duplicate on all three.
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.From<Setting>().InsertAsync<int>(NewSetting(SettingScopeCode.Global, null, name), ct)
        );
    }
}
