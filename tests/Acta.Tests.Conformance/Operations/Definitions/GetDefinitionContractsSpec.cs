using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>Conformance for the catalog read a worker makes at startup and on the policy reload: every definition of one namespace, retired ones included, with its effective policy.</summary>
[ConformanceSpec(
    "get-definition-contracts.catalog",
    "The catalog read returns every definition of one namespace",
    Area = "Catalog",
    Contract = "GetDefinitionContracts returns every definition of one namespace, retired ones included, with its contract, hash, status, and override-aware effective policy."
)]
public sealed class GetDefinitionContractsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<IReadOnlyList<StoredDefinitionContract>> ReadAsync(int namespaceId, CancellationToken ct) =>
        (
            await Services.GetRequiredService<GetDefinitionContractsHandler>().HandleAsync(new GetDefinitionContractsQuery(namespaceId), ct)
        ).Contracts;

    private async Task<int> RegisterAsync(int namespaceId, string name, int maxAttempts, CancellationToken ct) =>
        (await DefinitionTestOps.RegisterAsync(Services, namespaceId, Generation, [Def(name, maxAttempts)], ct))[name];

    private async Task<int> VersionAsync(int definitionId, CancellationToken ct) =>
        (await Db.From<DefinitionEntity>().Where(d => d.Id == definitionId).SingleOrDefaultAsync(ct))!.Version;

    [Fact(DisplayName = "A namespace without definitions reads an empty catalog")]
    public async Task Empty_namespace_reads_empty()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Empty(await ReadAsync(TestNamespaceId, ct));
    }

    [Fact(DisplayName = "Each definition reads back its identity, generation, contract, hash, status, and policy")]
    public async Task Definition_reads_back_whole()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Unique("contract-a");
        var second = Unique("contract-b");
        var firstId = await RegisterAsync(TestNamespaceId, first, 3, ct);
        var secondId = await RegisterAsync(TestNamespaceId, second, 5, ct);

        var catalog = (await ReadAsync(TestNamespaceId, ct)).ToDictionary(c => c.Name);

        Assert.Equal([first, second], catalog.Keys.Order(StringComparer.Ordinal));
        var stored = catalog[first];
        Assert.Equal(firstId, stored.Id);
        Assert.Equal(Generation, stored.ManifestGenerationAtUtc);
        Assert.Equal(typeof(int).FullName, stored.Contract.InputTypeName);
        Assert.Equal(JobPayloadFormat.Json.Id, stored.Contract.InputFormatId);
        Assert.False(string.IsNullOrEmpty(stored.DefinitionHash));
        Assert.Equal(DefinitionStatusCode.Active, stored.Status);
        Assert.Equal(3, stored.Effective.MaxAttempts);
        Assert.Equal(secondId, catalog[second].Id);
        Assert.Equal(5, catalog[second].Effective.MaxAttempts);
    }

    [Fact(DisplayName = "An operator override reads back as the effective policy")]
    public async Task Override_is_the_effective_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = Unique("contract-ovr");
        var id = await RegisterAsync(TestNamespaceId, name, 3, ct);

        await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            TestNamespace,
            name,
            await VersionAsync(id, ct),
            new DefinitionPolicyOverrides(MaxAttempts: 9),
            new JobControlActor(ActorCode.Operator, "tester"),
            null,
            ct
        );

        Assert.Equal(9, Assert.Single(await ReadAsync(TestNamespaceId, ct)).Effective.MaxAttempts);
    }

    [Fact(DisplayName = "A retired definition stays in the catalog with its Retired status")]
    public async Task Retired_definition_is_listed()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = Unique("contract-retired");
        var id = await RegisterAsync(TestNamespaceId, name, 3, ct);

        var retired = await Services
            .GetRequiredService<IDefinitions>()
            .RetireAsync(TestNamespace, name, await VersionAsync(id, ct), "left the build", "tester", ct);
        Assert.Equal(ControlAction.Applied, retired.Action);

        Assert.Equal(DefinitionStatusCode.Retired, Assert.Single(await ReadAsync(TestNamespaceId, ct)).Status);
    }

    [Fact(DisplayName = "The catalog holds only the asked namespace's definitions")]
    public async Task Catalog_is_scoped_to_its_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherNamespaceId = await Seeder.SeedJobNamespaceAsync(Unique("contracts-other"), ct: ct);
        var mine = Unique("contract-mine");
        await RegisterAsync(TestNamespaceId, mine, 3, ct);
        await RegisterAsync(otherNamespaceId, Unique("contract-theirs"), 3, ct);

        Assert.Equal(mine, Assert.Single(await ReadAsync(TestNamespaceId, ct)).Name);
    }

    private static JobDescriptor Def(string name, int maxAttempts) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            InvocationKind: default,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: default,
            MaxAttempts: maxAttempts,
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );
}
