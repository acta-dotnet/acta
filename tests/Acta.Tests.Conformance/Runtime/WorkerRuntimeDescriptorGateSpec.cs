using System.Collections.Immutable;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The descriptor shape gate runs before the catalog is written. A hand-authored manifest bypasses
/// the generator's compile-time checks, so an invalid descriptor first becomes visible at startup,
/// and validating it after the worker row was written would leave a <c>workers</c> row and a
/// <c>worker.started</c> event behind for a runtime that never started.
/// </summary>
[ConformanceSpec(
    "worker-runtime.descriptor-gate",
    "An invalid descriptor fails startup before the catalog is written",
    Area = "Catalog",
    Contract = "InitializeAsync validates every descriptor's shape before writing the worker row, so an invalid descriptor leaves no workers row and no WorkerStarted event.",
    Arrange = "A hand-authored manifest declares one descriptor whose Backoff expression does not parse.",
    Act = "InitializeAsync runs against the namespace.",
    Assert = "It throws naming the job and the expression, and the namespace holds no worker row and no WorkerStarted event."
)]
public abstract class WorkerRuntimeDescriptorGateSpec<TFixture> : ActaRuntimeTestBase<TFixture, InvalidBackoffManifest>
    where TFixture : IConformanceFixture, new()
{
    // Every other runtime spec initializes in the fixture. Here the failure is the subject, so it has
    // to happen inside the test body, where the throw and the untouched catalog are both assertable.
    protected override ValueTask AfterInitializeAsync() => ValueTask.CompletedTask;

    [Fact(DisplayName = "A manifest carrying an invalid descriptor writes no worker row and no WorkerStarted event")]
    public async Task Invalid_descriptor_fails_before_the_catalog_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = Services.GetServices<WorkerRuntime>().Single();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => runtime.InitializeAsync(ct));
        Assert.Contains("gated-job", error.Message, StringComparison.Ordinal);
        Assert.Contains("not-a-backoff", error.Message, StringComparison.Ordinal);

        // The namespace row may or may not have been written before the gate; the worker row and its
        // event are what must not exist, because a start that threw did not start anything.
        var nsId = (await Db.From<JobNamespace>().Where(n => n.Name == TestNamespace).SingleOrDefaultAsync(ct))?.Id;
        Assert.Equal(0, nsId is null ? 0 : await Db.From<JobWorker>().Where(w => w.NamespaceId == nsId.Value).CountAsync(ct));
        Assert.Equal(
            0,
            nsId is null
                ? 0
                : await Db.From<JobEvent>().Where(e => e.NamespaceId == nsId.Value && e.EventCode == EventCode.WorkerStarted).CountAsync(ct)
        );
    }
}

/// <summary>
/// Hand-authored manifest standing in for a consumer that writes <see cref="IJobManifest"/> by hand
/// instead of having the generator emit it. Its one descriptor carries a Backoff expression that
/// does not parse, which is the shape the generator would have rejected at compile time.
/// </summary>
public sealed class InvalidBackoffManifest : IJobManifest
{
    public static JobDescriptorManifest Descriptors { get; } =
        new(
            ImmutableArray.Create(
                new JobDescriptor(
                    JobName: "gated-job",
                    HandlerType: typeof(InvalidBackoffManifest),
                    MethodName: "Run",
                    InputType: typeof(object),
                    OutputType: null,
                    InputPayloadFormat: JobPayloadFormat.None,
                    OutputPayloadFormat: null,
                    InvocationKind: JobInvocationKind.Task,
                    RequiresJobContextParameter: false,
                    RequiresCancellationToken: false,
                    Priority: JobPriorityCode.Normal,
                    MaxAttempts: 1,
                    AuditLevel: JobAuditLevelCode.Audit,
                    AlertProfile: AlertProfileCode.None,
                    Invoker: static async (_, _, _, _) =>
                    {
                        await Task.CompletedTask;
                        return new JobHandlerInvocationResult(false, null);
                    },
                    DeserializeInput: static (_, _) => new object(),
                    SerializeOutput: null
                )
                {
                    Backoff = "not-a-backoff",
                }
            )
        );
}
