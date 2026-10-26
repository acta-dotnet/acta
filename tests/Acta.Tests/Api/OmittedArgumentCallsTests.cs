using System.Data.Common;
using TestJobs;
using Xunit;

namespace Acta.Tests.Api;

/// <summary>
/// The calls a consumer writes first, with every optional argument left out, resolve to one overload. The body never
/// runs: the test is that this file compiles.
/// </summary>
public sealed class OmittedArgumentCallsTests
{
    [Fact]
    public void Ordinary_calls_with_omitted_optional_arguments_compile() => Assert.NotNull((Func<IJobs, DbTransaction, Task>)CallsAsync);

    private static async Task CallsAsync(IJobs jobs, DbTransaction transaction)
    {
        await jobs.EnqueueAsync(new EnqueueJobRequest("probe", "noop"));
        await jobs.EnqueueAsync(transaction, new EnqueueJobRequest("probe", "noop"));
        await jobs.EnqueueAsync(new AddNumbers(1, 2));
        await jobs.EnqueueAsync(new AddNumbers(1, 2), o => o.DeduplicationKey("k"));
        await jobs.EnqueueAsync(transaction, new AddNumbers(1, 2));
        await jobs.EnqueueAsync(TestJobsManifest.AddNumbers(new AddNumbers(1, 2)));
        await jobs.EnqueueAsync(transaction, TestJobsManifest.AddNumbers(new AddNumbers(1, 2)));
        await jobs.ScheduleAsync(TestJobsManifest.AddNumbers(new AddNumbers(1, 2)), TimeSpan.FromMinutes(1));
        await jobs.ScheduleAsync(new AddNumbers(1, 2), TimeSpan.FromMinutes(1));
        await jobs.RunAndWaitAsync(TestJobsManifest.AddNumbers(new AddNumbers(1, 2)));
        await jobs.RunAndWaitAsync<AddNumbers, AddNumbersResult>(new AddNumbers(1, 2));
        await jobs.WaitAsync(JobLookup.ById(1));
        await jobs.WaitAsync<AddNumbersResult>(JobLookup.ById(1));
        await jobs.EnqueueBatchAsync([new EnqueueJobRequest("probe", "noop")]);
    }
}
