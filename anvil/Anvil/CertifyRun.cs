using Acta;
using Anvil;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Anvil;

/// <summary>
/// Drives one certification end to end in-process: seed, chaos, quiesce, verdict.
/// </summary>
/// <remarks>
/// <para>
/// In-process on purpose. Orchestrating this from a shell meant driving Anvil over HTTP on its fixed
/// port, and two runs then share one control plane: an earlier run reached the end of its chaos
/// window and stopped a newer run's chaos, which then finished with no kills at all and every check
/// green. A clean-looking pass over a run that was never chaosed. There is no control plane to
/// collide on here.
/// </para>
/// <para>
/// The other reason is that the timings stop being copied. The warm-up window is
/// <c>LeaseTtlSeconds</c> plus the recovery cadence, both read from the running configuration rather
/// than pasted into a script that goes stale the moment either is tuned.
/// </para>
/// </remarks>
internal static class CertifyRun
{
    // Fallback only, used when sys.recovery's schedule cannot be read. Never the source of truth:
    // a hardcoded cadence here would be the same copied constant this class exists to remove, one
    // level down, and it would silently mis-size the warm-up the moment the schedule changed.
    private static readonly TimeSpan RecoveryCadenceFallback = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The real cadence of <c>sys.recovery</c>, taken from its registered schedule and measured as
    /// the gap between its next two firings rather than assumed from the cron text.
    /// </summary>
    private static async Task<TimeSpan> RecoveryCadenceAsync(IServiceScopeFactory scopes, string jobNamespace, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var operations = scope.ServiceProvider.GetRequiredService<IActaOperations>();
            var page = await operations.Schedules.ListAsync(
                new ListSchedulesQuery(JobNamespace: jobNamespace, JobName: "sys.recovery", PageSize: 1),
                ct
            );
            if (page.Items is [{ Expression: { Length: > 0 } expression }, ..])
            {
                var cron = Cronos.CronExpression.Parse(
                    expression,
                    expression.Split(' ').Length > 5 ? Cronos.CronFormat.IncludeSeconds : Cronos.CronFormat.Standard
                );
                var first = cron.GetNextOccurrence(DateTime.UtcNow);
                var second = first is { } f ? cron.GetNextOccurrence(f) : null;
                if (first is { } a && second is { } b)
                {
                    return b - a;
                }
            }
        }
        catch
        {
            // A lab tool must not fail a run because it could not read a schedule; the fallback is
            // the shipped default and the header prints whatever was used.
        }

        return RecoveryCadenceFallback;
    }

    public static async Task<int> ExecuteAsync(
        IServiceProvider services,
        RunIdentity id,
        string provider,
        int jobs,
        int workers,
        TimeSpan chaos,
        TimeSpan quiesceTimeout,
        int stepDelayMs,
        bool isSeeder,
        string participant,
        CancellationToken ct
    )
    {
        var options = services.GetRequiredService<IOptions<JobsOptions>>().Value;
        var launcher = services.GetRequiredService<WorkerProcessLauncher>();
        var faults = services.GetRequiredService<FaultInjectors>();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var session = services.GetRequiredService<AnvilSession>();
        var progress = services.GetRequiredService<SeedProgress>();
        var outboxDb = services.GetRequiredService<AnvilOutboxDatabase>();

        // The relay is certified under the same chaos as the ledger: the seeder commits business rows
        // plus staged outbox records into the producer file for the whole window, and sys.outbox
        // drains them while workers are being killed. Sized to the run but bounded, so the staging
        // tail never dominates the quiesce.
        var outboxRows = isSeeder ? Math.Clamp(jobs / 2, 200, 5_000) : 0;

        void Phase(string phase, string detail)
        {
            session.Certification = new CertificationStatus(phase, detail, jobs, workers, (int)chaos.TotalMinutes);
            Console.WriteLine($"  {DateTime.Now:HH:mm:ss}  {phase, -9} {detail}");
        }

        Phase(isSeeder ? "START" : "JOINING", isSeeder ? "starting" : $"joining run {id.RunId}");
        Console.WriteLine();
        Console.WriteLine($"  Acta certification | {provider} | schema {id.Schema} | run {id.RunId}");
        Console.WriteLine(
            isSeeder
                ? $"  {participant} seeds and owns the verdict | {jobs} jobs, {workers} workers, 5 steps x {stepDelayMs}ms, chaos for {chaos.TotalMinutes:0} min"
                    + $" | {outboxRows} outbox rows staged under chaos"
                : $"  {participant} brings {workers} workers and chaos for {chaos.TotalMinutes:0} min | the seeder owns the verdict"
        );
        Console.WriteLine();

        Phase("START", "waiting for the first worker to register");
        var registered = await WaitAsync(
            async () => (await ReadAsync(scopes, ct)).Ready,
            TimeSpan.FromMinutes(3),
            TimeSpan.FromSeconds(2),
            ct
        );
        if (!registered)
        {
            Phase("ABORT", "no worker registered within 3 minutes");
            return 1;
        }

        // Read AFTER registration, not before: the worker registers sys.recovery's schedule at
        // startup, so reading earlier finds nothing and silently falls back - which is exactly what
        // happened the first time, printing the default while the schedule said otherwise.
        var cadence = await RecoveryCadenceAsync(scopes, id.Namespace, ct);
        var warmUp = TimeSpan.FromSeconds(options.LeaseTtlSeconds) + cadence;
        Console.WriteLine(
            $"  Reclaim is impossible for the first {warmUp.TotalSeconds:0}s (lease {options.LeaseTtlSeconds}s + recovery cadence {cadence.TotalSeconds:0}s)"
        );

        if (chaos <= warmUp)
        {
            Phase(
                "ABORT",
                $"chaos window {chaos.TotalMinutes:0} min is inside the {warmUp.TotalSeconds:0}s warm-up: the run could not observe a single reclaim"
            );
            return 2;
        }

        launcher.SetTargetCount(workers);
        // Exactly one seeder, named by a flag rather than elected. At this scale - one operator, a few
        // machines - a leader-election protocol would be more moving parts than the thing it coordinates,
        // and a second seeder is a mistake the run reports rather than one it has to prevent.
        if (isSeeder)
        {
            // The interruptible shapes are timed from the same two numbers this run already derived
            // rather than from constants pasted here: due after the warm-up, because nothing can be
            // reclaimed before a lease can lapse, and spread over what remains of the chaos window.
            var spec = new AnvilRunSpec(
                AnvilWorkloadCode.CrashRecovery,
                jobs,
                workers,
                stepDelayMs,
                EffectDelaySeconds: (int)warmUp.TotalSeconds,
                EffectSpreadSeconds: (int)(chaos - warmUp).TotalSeconds
            );
            var batch = session.NextBatch();
            _ = SeedAsync(scopes, batch, spec, progress);
            _ = StageOutboxAsync(outboxDb, session, outboxRows, chaos);
        }

        faults.StartContinuousCrashes();
        Phase("RUNNING", isSeeder ? "seeded; killing a worker every 5s" : "claiming the run's work; killing a worker every 5s");

        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < chaos)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            var counts = (await ReadAsync(scopes, ct)).Counts;
            var elapsed = DateTime.UtcNow - started;
            if (elapsed < warmUp)
            {
                // Said out loud: zero reclaims here is the timer, not a stall.
                Phase("RUNNING", $"done {counts?.Done}  |  warm-up: no reclaim possible for another {(warmUp - elapsed).TotalSeconds:0}s");
            }
            else
            {
                Phase(
                    "RUNNING",
                    $"done {counts?.Done}  ready {counts?.Ready}  executing {counts?.Executing}  |  {(chaos - elapsed).TotalMinutes:0} min of chaos left"
                );
            }
        }

        faults.StopContinuousCrashes();
        Phase("QUIESCE", "chaos stopped; draining in-flight work");

        // A joining participant keeps its workers alive through the drain and then leaves without a
        // verdict. Exiting at the end of chaos would withdraw its executors from the very phase whose
        // length it just extended, and two participants printing two verdicts is the failure this
        // role split exists to remove: one run, one seal.
        if (!isSeeder)
        {
            var drained = await WaitAsync(
                async () => (await SeededAsync(scopes, id, ct)) is var (total, terminal) && total > 0 && terminal == total,
                quiesceTimeout,
                TimeSpan.FromSeconds(20),
                ct
            );
            Phase(
                drained ? "DONE" : "DONE",
                drained ? "the run's work is terminal; the seeder owns the verdict" : "quiesce timed out here; the seeder owns the verdict"
            );
            return 0;
        }

        // The staging file drains first: rows still Pending or Claimed there become ledger jobs only
        // when a relay tick moves them, so waiting on the ledger alone would declare quiesce while
        // the relay is still creating work. Quarantined rows do not block the drain - they are the
        // delivered check's finding, not a backlog.
        Phase("QUIESCE", "waiting for the outbox staging backlog to drain");
        var stagingDrained = await WaitAsync(
            async () => (await outboxDb.CountsAsync(ct)).Pending == 0,
            quiesceTimeout,
            TimeSpan.FromSeconds(10),
            ct
        );

        // Quiesce does not end when the chaos stops: work held by already-killed workers stays
        // Executing until its lease lapses and recovery sweeps it, one batch per tick.
        var quiesced = await WaitAsync(
            async () =>
            {
                var (total, terminal) = await SeededAsync(scopes, id, ct);
                Phase("QUIESCE", $"terminal {terminal} of {total} seeded");
                return total > 0 && terminal == total;
            },
            quiesceTimeout,
            TimeSpan.FromSeconds(20),
            ct
        );

        if (!quiesced)
        {
            Phase("ABORT", $"did not quiesce within {quiesceTimeout.TotalMinutes:0} minutes");
            return 1;
        }

        Phase("SEALING", "checking the outbox handoff, then running certify.sql");
        Console.WriteLine();

        // The outbox certification is split across the ownership seam: the staging table lives in
        // the producer's database, which certify.sql (bound to the Acta schema) cannot reach.
        // These two lines are the producer-side half; certify.sql carries the ledger-reachable half
        // (outbox-relayed-outcome / outbox-relayed).
        var (pendingLeft, quarantined) = await outboxDb.CountsAsync(ct);
        var stagedOperations = await outboxDb.CountOperationsAsync(ct);
        var deliveredJobs = await OutboxDeliveredAsync(scopes, id, ct);
        var drainedOk = stagingDrained && pendingLeft == 0;
        // Delivered is count equality, and that is sufficient: dedup keys are unique per staged row
        // and per (namespace, key) in the ledger, so equal counts is a bijection. A deduplicated
        // handoff resolves to the same job and counts as delivered.
        var deliveredOk = deliveredJobs == stagedOperations;
        Console.WriteLine(
            $"  [{(drainedOk ? "ok  " : "FAIL")}] {"outbox-drained", -28} pending+claimed={pendingLeft} quarantined={quarantined}"
        );
        Console.WriteLine(
            $"  [{(deliveredOk ? "ok  " : "FAIL")}] {"outbox-delivered", -28} staged={stagedOperations} ledger_jobs={deliveredJobs}"
        );

        var exit = await CertifyVerdict.RunAsync(provider, id.Schema, ct);
        if (!drainedOk || !deliveredOk)
        {
            // An outbox handoff failure is a hard FAIL even when the SQL half passed or was merely
            // inconclusive; the seal must not read green over undelivered producer rows.
            exit = 1;
        }
        Phase(exit == 0 ? "PASS" : "FAIL", exit == 0 ? "every asserted property held" : "see the verdict on the console");
        return exit;
    }

    // The producer-to-ledger bridge for the delivered check: every staged operation's job under this
    // run's correlation key, recognized by the ingress=outbox tag the staging path stamps.
    private static async Task<long> OutboxDeliveredAsync(IServiceScopeFactory scopes, RunIdentity id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IActaOperations>().Ledger;
        var page = await ledger.ListJobsAsync(
            new ListJobsQuery(
                JobNamespace: id.Namespace,
                CorrelationKey: id.RunId,
                PageSize: 1,
                IncludeTotal: true,
                Tags: [new TagFilter("ingress", "outbox")]
            ),
            ct
        );
        return page.TotalCount ?? 0;
    }

    // Staging runs beside the chaos rather than ahead of it: a fixed budget in small committed
    // batches spread across the window, so relay claims race worker kills the whole time. Best
    // effort by design - the delivered check compares against what actually committed, so a staging
    // error under-fills the run instead of corrupting the verdict.
    private static async Task StageOutboxAsync(AnvilOutboxDatabase outboxDb, AnvilSession session, int total, TimeSpan chaos)
    {
        const int Chunk = 200;
        var batch = session.NextBatch();
        var delay = TimeSpan.FromTicks(chaos.Ticks / (Math.Max(1, total / Chunk) + 1));
        var staged = 0;
        try
        {
            while (staged < total)
            {
                var count = Math.Min(Chunk, total - staged);
                await outboxDb.StageAsync(count, batch, firstOrdinal: staged, CancellationToken.None);
                staged += count;
                await Task.Delay(delay);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  outbox staging stopped after {staged} of {total} rows: {ex.Message}");
        }
    }

    // AnvilStateReader is scoped (it holds a store connection), so every read opens its own scope.
    // Resolving it from the root provider worked only outside Development, where scope validation is
    // off - in Development the certification aborted at its first read.
    // Quiesce asks about THIS run's seeded work, not the namespace: the namespace always holds the
    // recurring jobs (sys.recovery, sys.alerts, sys.retention, sys.outbox, and Anvil's own pulse),
    // which sit Ready between firings forever. Waiting for a namespace-wide ready count of zero
    // therefore never completed - it timed out at 10000 of 10000 jobs done with five schedules
    // parked. The seeder stamps every job it enqueues with the run id as its correlation key, so
    // that key is the exact boundary of what this certification is responsible for, children of
    // fanned-out parents included.
    private static async Task<(long Total, long Terminal)> SeededAsync(IServiceScopeFactory scopes, RunIdentity id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IActaOperations>().Ledger;
        var all = await ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: id.Namespace, CorrelationKey: id.RunId, PageSize: 1, IncludeTotal: true),
            ct
        );
        var terminal = await ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: id.Namespace, CorrelationKey: id.RunId, PageSize: 1, IncludeTotal: true, TerminalOnly: true),
            ct
        );
        return (all.TotalCount ?? 0, terminal.TotalCount ?? 0);
    }

    private static async Task<AnvilState> ReadAsync(IServiceScopeFactory scopes, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AnvilStateReader>().ReadAsync(ct);
    }

    private static async Task SeedAsync(IServiceScopeFactory scopes, int batch, AnvilRunSpec spec, SeedProgress progress)
    {
        using var scope = scopes.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<AnvilSeeder>();
        try
        {
            await seeder.SeedAsync(batch, spec, progress, CancellationToken.None);
        }
        catch
        {
            // SeedAsync records its own one-line error in SeedProgress.
        }
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan timeout, TimeSpan poll, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(poll, ct);
            if (await condition())
            {
                return true;
            }
        }
        return false;
    }
}
