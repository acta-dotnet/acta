using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for reading a job's variable from outside the job. The value its handler stored reads back as its type,
/// default stands for no variable and for no job alike, and the name is held to the rules the handler's own read holds
/// it to.
/// </summary>
[ConformanceSpec(
    "get-job-variable-or-default.reads",
    "GetJobVariableOrDefault reads a job's variable from outside the job",
    Area = "Variables",
    Contract = "A job's variable reads back outside the job as the type its handler stored, default when it is unset or no job matches, under a name the handler could set."
)]
[Deploys("add-numbers", "job-variable-persistence", "job-variable-versioning", "jobref-probe")]
public sealed class GetJobVariableOrDefaultSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetJobVariableOrDefaultSpec>>
{
    [Fact(DisplayName = "A variable the handler set reads back as its type, decoded by its stored format, under any casing of its name")]
    public async Task A_variable_the_handler_set_reads_back_as_its_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-variable-persistence", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal("done", await Jobs.GetVariableOrDefaultAsync<string>(enqueued.JobRef, "fetch.status", ct));
        // Stored as Bytes: zero bytes decode as an empty array, where a JSON decode would fail.
        Assert.Equal(Array.Empty<byte>(), await Jobs.GetVariableOrDefaultAsync<byte[]>(enqueued.JobRef, "payload.empty-bytes", ct));
        // The name is trimmed and lowercased as the handler's own read canonicalizes it.
        Assert.Equal("done", await Jobs.GetVariableOrDefaultAsync<string>(enqueued.JobRef, " Fetch.Status ", ct));
    }

    [Fact(DisplayName = "A value-type variable reads back as stored")]
    public async Task A_value_type_variable_reads_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-variable-versioning", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(7, await Jobs.GetVariableOrDefaultAsync<int>(enqueued.JobRef, "retry-count", ct));
        Assert.Equal(7, await Jobs.GetVariableOrDefaultAsync<int?>(enqueued.JobRef, "retry-count", ct));
    }

    [Fact(
        DisplayName = "An unset variable and a lookup that matches no job both answer default, null for a reference type and 0 for an int"
    )]
    public async Task An_unset_variable_and_a_missing_job_answer_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        var missing = JobRef.New();

        Assert.Null(await Jobs.GetVariableOrDefaultAsync<string>(enqueued.JobRef, "stage", ct));
        Assert.Equal(0, await Jobs.GetVariableOrDefaultAsync<int>(enqueued.JobRef, "stage", ct));
        Assert.Null(await Jobs.GetVariableOrDefaultAsync<int?>(enqueued.JobRef, "stage", ct));
        Assert.Null(await Jobs.GetVariableOrDefaultAsync<string>(missing, "stage", ct));
        Assert.Equal(0, await Jobs.GetVariableOrDefaultAsync<int>(missing, "stage", ct));
    }

    [Fact(DisplayName = "Each job answers its own variable, never another job's variable of the same name")]
    public async Task Each_job_answers_its_own_variable()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "jobref-probe", JobPayload.None), ct);
        var second = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "jobref-probe", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(first, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(second, ct));

        // Each handler stored the ref it saw under the one name both use.
        Assert.Equal(first.JobRef.ToString(), await Jobs.GetVariableOrDefaultAsync<string>(first.JobRef, "seen-ref", ct));
        Assert.Equal(second.JobRef.ToString(), await Jobs.GetVariableOrDefaultAsync<string>(second.JobRef, "seen-ref", ct));
    }

    [Fact(
        DisplayName = "A name under sys. or not dotted kebab is refused as the handler's own read refuses it, whether or not a job matches"
    )]
    public async Task A_reserved_or_malformed_name_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );

        foreach (var name in new[] { "sys.progress", "sys", "", "cursor_id", ".cursor", "cursor.", "cursor..id", new string('a', 129) })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await Jobs.GetVariableOrDefaultAsync<string>(enqueued.JobRef, name, ct)
            );
        }
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Jobs.GetVariableOrDefaultAsync<string>(JobRef.New(), "sys.progress", ct)
        );
    }
}
