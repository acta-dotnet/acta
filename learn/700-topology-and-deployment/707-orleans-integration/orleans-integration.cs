// How to integrate Acta with Orleans
// You run an app whose users each have a wallet in EUR. The wallet is a grain: the live object, with an address, its
// state in memory, answering one call at a time. Topping it up is a job: recorded step by step in the database,
// retried when it fails, and able to wait for days. The grain enqueues the job and returns, and the job's handler
// calls the grain back as its last step.
using Acta;
using Acta.Labs;
using Acta.Learn.Wallets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime;

var builder = Host.CreateApplicationBuilder(args);
var lab = new LearnLab(builder.Configuration, args);
builder.Logging.AddFilter("Acta", LogLevel.Error);
builder.Logging.AddFilter("Orleans", LogLevel.Error);

// learn: 1. Run the silo and the worker in one application
// The silo hosts the grains and the Acta worker runs the jobs. In one application they share its services, so a
// grain can take IJobs and a handler can take IGrainFactory.
builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorageAsDefault();
});
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<WalletsJobs>("wallets");
});

// learn: end

using var host = builder.Build();
await host.StartAsync();

// A user tops up their wallet with 100 EUR, and taps the button twice. The wallet answers each tap with the ref of
// the job that does it, the same ref both times. The top-up has an id of its own for each run, so a second run
// does not find the first run's job.
var wallet = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IWalletGrain>(1001);
var topUpId = $"top-{Guid.CreateVersion7():N}";
var topUp = await wallet.TopUpAsync(topUpId, 100m);
await wallet.TopUpAsync(topUpId, 100m);

// The top-up is not finished, and the grain is free: it answers from the state it holds in memory.
Console.WriteLine($"balance before the payment was confirmed: {await wallet.GetBalanceAsync()} EUR");

// learn: 5. Raise the job's signal when the payment provider confirms
// A job's ref names it everywhere: one word, unique across namespaces, so any code that has IJobs reaches the job
// with it. The app gave the ref to the payment provider as its reference, and here the program stands in for the
// provider's confirmation, which carries it back.
var jobs = host.Services.GetRequiredService<IJobs>();
await jobs.RaiseSignalAsync(topUp, "payment-confirmed");

// learn: end

var done = await jobs.WaitAsync(topUp);
done.ThrowIfFailed();
Console.WriteLine($"balance after the payment was confirmed: {await wallet.GetBalanceAsync()} EUR");

// learn: 7. Read what the job recorded with a plain query
// The grain holds the balance in memory. What the job did is rows in the database the application already has:
// one job for the top-up, though it was asked for twice, each step recorded once, and each attempt with its error.
await lab.ShowAsync(
    "One job for the top-up, though it was asked for twice",
    """
    SELECT definition_name, job_status, execution_number
    FROM jobs_view
    WHERE idempotency_key = @topUpId
    """,
    new { topUpId }
);
await lab.ShowAsync(
    "Each step recorded once, though the job ran twice",
    """
    SELECT step_name, step_status
    FROM steps_view
    WHERE job_id = @jobId
    ORDER BY created_at_utc
    """,
    new { jobId = done.JobId }
);
await lab.ShowAsync(
    "Both attempts, with the first one's error",
    """
    SELECT event, execution_number, event_message
    FROM events_view
    WHERE entity = 'job' AND entity_id = @jobId
    ORDER BY event_id
    """,
    new { jobId = done.JobId }
);

// learn: end

await host.StopAsync();

namespace Acta.Learn.Wallets
{
    public interface IWalletGrain : IGrainWithIntegerKey
    {
        Task<Guid> TopUpAsync(string topUpId, decimal amount);
        Task AddToBalanceAsync(string topUpId, decimal amount);
        Task<decimal> GetBalanceAsync();
    }

    [GenerateSerializer]
    public sealed class WalletState
    {
        [Id(0)]
        public decimal Balance { get; set; }

        [Id(1)]
        public HashSet<string> TopUps { get; set; } = [];
    }

    public sealed class WalletGrain([PersistentState("wallet")] IPersistentState<WalletState> wallet, IJobs jobs) : Grain, IWalletGrain
    {
        // learn: 2. Enqueue from the grain and return
        // The grain hands the top-up to Acta and stays free for its next call, the job's own call back included.
        // The idempotency key makes a repeated TopUpAsync find the same job: the grain keeps no record that it
        // already asked. It answers with the job's ref as a Guid, which a grain call carries and IJobs takes as it is.
        public async Task<Guid> TopUpAsync(string topUpId, decimal amount)
        {
            var job = await jobs.EnqueueAsync(new TopUpWallet(this.GetPrimaryKeyLong(), topUpId, amount), o => o.IdempotencyKey(topUpId));
            return job.JobRef;
        }

        // learn: end

        // learn: 6. Make the call a job sends safe to receive twice
        // The grain owns its state and saves it when it chooses. The call can arrive again if the job's step was
        // not yet recorded, so the wallet adds a top-up once, by its id.
        public async Task AddToBalanceAsync(string topUpId, decimal amount)
        {
            if (wallet.State.TopUps.Add(topUpId))
            {
                wallet.State.Balance += amount;
                await wallet.WriteStateAsync();
            }
        }

        // learn: end

        public Task<decimal> GetBalanceAsync() => Task.FromResult(wallet.State.Balance);
    }

    public sealed record TopUpWallet(long WalletId, string TopUpId, decimal Amount);

    public sealed class TopUpWalletJob(IGrainFactory grains)
    {
        // learn: 3. Let the job retry by itself
        // The receipt's mail server is down on the first attempt. Nobody has to call again: the job runs once more
        // after its backoff, and the attempt and its error are on the job's record.
        [Job("top-up-wallet", MaxAttempts = 3, Backoff = "1s")]
        // learn: end
        public async Task Handle(TopUpWallet input, JobContext context, CancellationToken ct)
        {
            // learn: 4. Wait for the payment and call the grain back
            // The wait is a row in the database, not a call held open, so it can last days. Once a step is
            // recorded, a later run reuses it. When the receipt fails and the job runs again, the payment is not
            // requested again, the job does not wait again, and the grain is not told again.
            await context.RunStepAsync("request-payment", _ => PaymentProvider.RequestAsync(input.Amount), ct);
            await context.WaitSignalAsync("payment-confirmed", ct);
            var wallet = grains.GetGrain<IWalletGrain>(input.WalletId);
            await context.RunStepAsync("add-to-balance", _ => wallet.AddToBalanceAsync(input.TopUpId, input.Amount), ct);
            await context.RunStepAsync("send-receipt", _ => Receipts.SendAsync(input.Amount, context.ExecutionNumber), ct);
            // learn: end
        }
    }

    // Stands in for the payment provider.
    public static class PaymentProvider
    {
        public static Task RequestAsync(decimal amount)
        {
            Console.WriteLine($"asking the payment provider for {amount} EUR");
            return Task.CompletedTask;
        }
    }

    // Stands in for the mail server, which is down the first time a receipt is sent.
    public static class Receipts
    {
        public static Task SendAsync(decimal amount, long attempt)
        {
            Console.WriteLine($"sending the receipt for {amount} EUR (attempt {attempt})");
            return attempt == 1 ? throw new InvalidOperationException("the mail server timed out") : Task.CompletedTask;
        }
    }
}
