using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Every door that routes by input type answers an ambiguous type with the registry's one message: the typed enqueue,
/// its transaction form, the typed call, a handler's child enqueue, and a map. The graph is the one <c>UseActa</c> builds,
/// over a scripted session, so each door runs its own code up to the throw and the throw comes before any write.
/// </summary>
public sealed class TypedEnqueueAmbiguityTests
{
    [Fact]
    public async Task The_typed_enqueue_answers_the_registry_message()
    {
        using var services = Services();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IJobs>().EnqueueAsync(new AddNumbers(1, 2), ct: TestContext.Current.CancellationToken)
        );

        Assert.Equal(RegistryMessage(services), ex.Message);
    }

    [Fact]
    public async Task The_transaction_form_answers_the_registry_message()
    {
        using var services = Services();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IJobs>().EnqueueAsync(transaction, new AddNumbers(1, 2), ct: TestContext.Current.CancellationToken)
        );

        Assert.Equal(RegistryMessage(services), ex.Message);
    }

    [Fact]
    public async Task The_typed_call_answers_the_registry_message()
    {
        using var services = Services();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IJobs>().CallAsync(new AddNumbers(1, 2), ct: TestContext.Current.CancellationToken)
        );

        Assert.Equal(RegistryMessage(services), ex.Message);
    }

    [Fact]
    public async Task A_child_enqueue_answers_the_registry_message()
    {
        using var services = Services();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Context(services).EnqueueChildAsync("add", new AddNumbers(1, 2), TestContext.Current.CancellationToken)
        );

        Assert.Equal(RegistryMessage(services), ex.Message);
    }

    [Fact]
    public async Task A_map_answers_the_registry_message()
    {
        using var services = Services();
        IEnumerable<int> items = [1];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Context(services)
                .MapAsync("adds", items, item => item, item => new AddNumbers(item, item), TestContext.Current.CancellationToken)
        );

        Assert.Equal(RegistryMessage(services), ex.Message);
    }

    // The test manifest referenced in two namespaces gives each of its input types two jobs. A map first reads the
    // children it already started, and finds none; any other call would be a write the throw should have prevented.
    private static ServiceProvider Services() =>
        new ScriptedSession(call =>
            call.Stem == "ListChildJobsByName" ? Array.Empty<ListChildJobsByNameItem>() : throw new NotSupportedException(call.Stem)
        ).Services(acta => acta.Reference<TestJobsManifest>("billing").Reference<TestJobsManifest>("payments"));

    private static string RegistryMessage(IServiceProvider services) =>
        Assert
            .Throws<InvalidOperationException>(() => services.GetRequiredService<JobTypeRegistry>().Resolve(typeof(AddNumbers), null))
            .Message;

    private static RuntimeJobContext Context(IServiceProvider services) =>
        new(
            new ClaimedJob(
                JobId: 1000,
                JobRef: Guid.CreateVersion7(),
                DefinitionId: 1,
                TenantId: null,
                ExecutionNumber: 1,
                CorrelationKey: null,
                ConcurrencyKey: null,
                InputFormatId: 0,
                Input: ReadOnlyMemory<byte>.Empty,
                NextRunAtUtc: null,
                CreatedAtUtc: DateTime.UtcNow,
                FailureCount: 0
            ),
            jobName: "parent",
            namespaceName: "billing",
            namespaceId: 1,
            leaseTtlSeconds: 180,
            jobOperations: services.GetRequiredService<JobsOperations>(),
            signals: null!,
            alerts: null!,
            executionStore: services.GetRequiredService<ExecutionOperations>(),
            services.GetRequiredService<IJobPayloadSerializerRegistry>(),
            services.GetRequiredService<LocksOperations>(),
            cancellationToken: CancellationToken.None,
            firing: null,
            deadlineAtUtc: null
        );
}
