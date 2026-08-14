// Concept: reading a JobOutcome from RunAndWaitAsync -- timeout, failure, and success paths.
using Acta;
using Acta.Concepts.ExecuteOutcomeTimeout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ExecuteOutcomeTimeoutJobs>("execute-outcome-timeout");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// (a) Slow handler with a short WaitTimeout -> outcome.IsTimedOut is true.
// The job keeps running on the worker; the caller just stops waiting.
Console.WriteLine("--- (a) timeout ---");
var opts = new JobExecutionOptions { WaitTimeout = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromMilliseconds(50) };
var timedOut = await jobs.RunAndWaitAsync<SlowReport, ReportResult>(new SlowReport(), opts);
Console.WriteLine($"IsTimedOut: {timedOut.IsTimedOut}");
Console.WriteLine($"IsSuccess: {timedOut.IsSuccess}");

// (b) Failing handler -> IsFailed; ThrowIfFailed throws JobFailedException; TryGetValue is false.
Console.WriteLine("--- (b) failure ---");
var failed = await jobs.RunAndWaitAsync<FailingReport, ReportResult>(new FailingReport());
Console.WriteLine($"IsFailed: {failed.IsFailed}");
try
{
    failed.ThrowIfFailed();
}
catch (JobFailedException ex)
{
    Console.WriteLine($"ThrowIfFailed threw: {ex.GetType().Name} status={ex.TerminalStatus}");
}
Console.WriteLine($"TryGetValue: {failed.TryGetValue(out _)}");

// (c) Success -> ValueOrThrow returns the handler's result.
Console.WriteLine("--- (c) success ---");
var success = await jobs.RunAndWaitAsync<SuccessReport, ReportResult>(new SuccessReport());
Console.WriteLine($"IsSuccess: {success.IsSuccess}");
var result = success.ValueOrThrow();
Console.WriteLine($"ValueOrThrow: {result.Summary}");

await host.StopAsync();

namespace Acta.Concepts.ExecuteOutcomeTimeout
{
    public readonly record struct SlowReport;

    public readonly record struct FailingReport;

    public readonly record struct SuccessReport;

    public sealed record ReportResult(string Summary);

    public static class SlowReportJob
    {
        [Job("slow-report")]
        public static async Task<ReportResult> Handle(SlowReport input, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new ReportResult("done");
        }
    }

    public static class FailingReportJob
    {
        [Job("failing-report", MaxAttempts = 1)]
        public static Task<ReportResult> Handle(FailingReport input)
        {
            throw new InvalidOperationException("report source unavailable");
        }
    }

    public static class SuccessReportJob
    {
        [Job("success-report")]
        public static Task<ReportResult> Handle(SuccessReport input)
        {
            return Task.FromResult(new ReportResult("all clear"));
        }
    }
}
