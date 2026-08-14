// Concept: misfire decision - Skip advances past missed occurrences; CatchUpOnce keeps one
// so it fires once on recovery. Proven by comparing the reconciled next-run cursors after resume.
using Acta;
using Acta.Concepts.ScheduleMisfire;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ScheduleMisfireJobs>("schedule-misfire");
});

using var host = builder.Build();
await host.StartAsync();

var schedules = host.Services.GetRequiredService<IActaOperations>().Schedules;

// The recurring slots register on StartAsync; wait for the durable rows instead of guessing a delay.
using var registrationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (
    (
        await schedules.ListAsync(
            new ListSchedulesQuery(JobNamespace: "schedule-misfire", PageSize: 2, IncludeTotal: true),
            registrationTimeout.Token
        )
    ).TotalCount < 2
)
{
    await Task.Delay(50, registrationTimeout.Token);
}

var skipLookup = new ScheduleLookup(JobLookup.ByDeduplicationKey("schedule-misfire", "skip-report"), "every-2s-skip");
var catchUpLookup = new ScheduleLookup(JobLookup.ByDeduplicationKey("schedule-misfire", "catch-up-report"), "every-2s-catchup");

// Pause both schedules indefinitely so no occurrences fire while we wait.
Console.WriteLine("Pausing both schedules indefinitely...");
var ps1 = await schedules.PauseAsync(skipLookup, untilUtc: null, reasonMessage: "misfire demo");
var ps2 = await schedules.PauseAsync(catchUpLookup, untilUtc: null, reasonMessage: "misfire demo");
Console.WriteLine($"skip-report paused: status={ps1.Status}");
Console.WriteLine($"catch-up-report paused: status={ps2.Status}");
await ShowSchedulesAsync(lab, "Paused before an occurrence is missed");

// This reproduces the same reconciliation a worker does after downtime: we wait one interval
// while the schedules are paused so a scheduled instant passes without being claimed.
Console.WriteLine("Waiting one interval so a scheduled instant passes while paused...");
await Task.Delay(2500);
await ShowSchedulesAsync(lab, "Both cursors are overdue while the schedules remain paused");
var overdue = await schedules.ListAsync(new ListSchedulesQuery(JobNamespace: "schedule-misfire", PageSize: 10, IncludeTotal: true));
var overdueSkip = overdue.Items.Single(item => item.JobName == "skip-report").NextRunAtUtc;
var overdueCatchUp = overdue.Items.Single(item => item.JobName == "catch-up-report").NextRunAtUtc;

// Resume reconciles each schedule's cursor by its misfire policy.
// Skip: first occurrence strictly after now - the missed instant is dropped.
// CatchUpOnce: the missed past instant is kept so it fires once on recovery.
Console.WriteLine("Resuming both schedules - each cursor is reconciled by its misfire policy...");
var rs1 = await schedules.ResumeAsync(skipLookup, reasonMessage: "misfire demo");
CatchUpReportJob.HoldNextExecution();
var rs2 = await schedules.ResumeAsync(catchUpLookup, reasonMessage: "misfire demo");

Console.WriteLine($"skip-report   overdue cursor: {overdueSkip?.ToString("O") ?? "(none)"}");
Console.WriteLine($"skip-report   next-run (UTC): {rs1.NextRunAtUtc?.ToString("O") ?? "(none)"}");
Console.WriteLine($"catch-up-report overdue cursor: {overdueCatchUp?.ToString("O") ?? "(none)"}");
Console.WriteLine($"catch-up-report next-run (UTC): {rs2.NextRunAtUtc?.ToString("O") ?? "(none)"}");

var skipAdvancedPastMiss = overdueSkip is not null && rs1.NextRunAtUtc > overdueSkip;
var catchUpPreservedMiss = overdueCatchUp is not null && rs2.NextRunAtUtc == overdueCatchUp;

Console.WriteLine($"skip-report advanced past missed cursor: {skipAdvancedPastMiss}  (missed instant dropped)");
Console.WriteLine($"catch-up-report preserved missed cursor: {catchUpPreservedMiss}  (fires once on recovery)");
try
{
    // If a worker claims the catch-up immediately, the lab gate holds completion so the overdue
    // schedule cursor cannot advance before the learner sees it.
    await ShowSchedulesAsync(lab, "Resume applies each schedule's explicit misfire policy");
}
finally
{
    CatchUpReportJob.ReleaseExecution();
}
using var catchUpTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await CatchUpReportJob.WaitForExecutionAsync(catchUpTimeout.Token);

await host.StopAsync();

static async Task ShowSchedulesAsync(ConceptLab lab, string title)
{
    await lab.ShowAllAsync(
        $"Explore the complete schedule records: {title}",
        """
        SELECT *
        FROM schedules_view
        WHERE namespace = @jobNamespace
          AND job_name IN ('skip-report', 'catch-up-report')
        ORDER BY job_name
        """,
        new { jobNamespace = "schedule-misfire" }
    );
    await lab.ShowAsync(
        title,
        """
        SELECT job_name, schedule_name, status, misfire_strategy, next_run_at_utc, paused_until_utc
        FROM schedules_view
        WHERE namespace = @jobNamespace
          AND job_name IN ('skip-report', 'catch-up-report')
        ORDER BY job_name
        """,
        new { jobNamespace = "schedule-misfire" }
    );
}

namespace Acta.Concepts.ScheduleMisfire
{
    public readonly record struct SkipReportInput;

    public sealed class SkipReportJob
    {
        [Job("skip-report", AuditLevel = JobAuditLevelCode.Off)]
        [JobSchedule("every-2s-skip", "PT2S", MisfireStrategy = MisfireStrategyCode.Skip)]
        public async Task Handle(SkipReportInput input, CancellationToken ct)
        {
            await Task.Delay(100, ct);
        }
    }

    public readonly record struct CatchUpReportInput;

    public sealed class CatchUpReportJob
    {
        private static int _holdNextExecution;
        private static TaskCompletionSource _release = NewRelease();
        private static TaskCompletionSource _completed = NewRelease();

        public static void HoldNextExecution()
        {
            _release = NewRelease();
            _completed = NewRelease();
            Interlocked.Exchange(ref _holdNextExecution, 1);
        }

        public static void ReleaseExecution() => _release.TrySetResult();

        public static Task WaitForExecutionAsync(CancellationToken ct) => _completed.Task.WaitAsync(ct);

        [Job("catch-up-report", AuditLevel = JobAuditLevelCode.Off)]
        [JobSchedule("every-2s-catchup", "PT2S", MisfireStrategy = MisfireStrategyCode.CatchUpOnce)]
        public async Task Handle(CatchUpReportInput input, CancellationToken ct)
        {
            var controlledRecoveryExecution = false;
            try
            {
                if (Interlocked.Exchange(ref _holdNextExecution, 0) == 1)
                {
                    controlledRecoveryExecution = true;
                    await _release.Task.WaitAsync(ct);
                }
                await Task.Delay(100, ct);
                if (controlledRecoveryExecution)
                {
                    Console.WriteLine("catch-up-report fired once from the preserved missed cursor");
                }
            }
            finally
            {
                if (controlledRecoveryExecution)
                {
                    _completed.TrySetResult();
                }
            }
        }

        private static TaskCompletionSource NewRelease() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
