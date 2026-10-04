using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Settings;

/// <summary>Conformance for the setting point read: exact scope, no fallback, and null rather than NotFound for anything absent.</summary>
[ConformanceSpec(
    "get-setting.exact-scope",
    "Get reads one setting at exactly the scope its targets name",
    Area = "Admin",
    Contract = "Get returns the setting stored at exactly the inferred scope, with no fallback to a wider one, and null when the setting or its scope target is absent."
)]
public sealed class GetSettingSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ISettings Settings => Services.GetRequiredService<ISettings>();

    [Fact(DisplayName = "A read returns the stored value, description, timestamps, and version")]
    public async Task Read_returns_the_stored_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("read");
        await Settings.SetAsync(name, "{\"limit\":5}", null, "batch size", TestNamespace, jobName: null, null, "op-1", ct);
        await Settings.SetAsync(name, "{\"limit\":7}", null, "batch size", TestNamespace, jobName: null, null, "op-1", ct);

        var setting = await Settings.GetAsync(name, TestNamespace, jobName: null, ct);

        Assert.NotNull(setting);
        Assert.Equal(name, setting.Name);
        Assert.Equal("{\"limit\":7}", setting.Value);
        Assert.Equal("batch size", setting.Description);
        Assert.Equal(1, setting.Version);
        Assert.True(setting.ModifiedAtUtc >= setting.CreatedAtUtc);
    }

    [Fact(DisplayName = "A global value does not answer a namespace or definition read")]
    public async Task Reads_never_fall_back_to_a_wider_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("no-fallback");
        var jobName = TestKey("no-fallback-job");
        await Seeder.SeedJobDefinitionAsync(TestNamespaceId, name: jobName, ct: ct);
        await Settings.SetAsync(name, "global", null, null, namespaceName: null, jobName: null, null, "op-1", ct);

        Assert.Equal("global", (await Settings.GetAsync(name, namespaceName: null, jobName: null, ct))!.Value);
        Assert.Null(await Settings.GetAsync(name, TestNamespace, jobName: null, ct));
        Assert.Null(await Settings.GetAsync(name, TestNamespace, jobName, ct));
    }

    [Fact(DisplayName = "An unwritten name and an unregistered scope target both read null")]
    public async Task Absent_setting_or_target_reads_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = TestKey("absent");

        Assert.Null(await Settings.GetAsync(name, namespaceName: null, jobName: null, ct));
        Assert.Null(await Settings.GetAsync(name, TestKey("no-such-ns"), jobName: null, ct));
        Assert.Null(await Settings.GetAsync(name, TestNamespace, TestKey("no-such-job"), ct));
    }

    [Fact(DisplayName = "A read accepts the reserved sys prefix but rejects a malformed name or a job without its namespace")]
    public async Task Read_validates_shape_only()
    {
        var ct = TestContext.Current.CancellationToken;

        // Writes reserve the sys prefix; reads only check the shape, so a system setting stays readable.
        Assert.Null(await Settings.GetAsync("sys." + TestKey("probe"), namespaceName: null, jobName: null, ct));

        await Assert.ThrowsAsync<ArgumentException>(async () => await Settings.GetAsync("Not Kebab", null, null, ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Settings.GetAsync(TestKey("orphan"), null, "some-job", ct));
    }
}
