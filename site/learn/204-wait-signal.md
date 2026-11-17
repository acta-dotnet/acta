# How to wait for an approval without holding a worker

A job parks on a named signal, holds no worker, and carries on when the signal is raised.

- Section: Wait for people, events, and time, step 1
- Program: `concepts/200-durable-execution/204-wait-signal` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/204-wait-signal
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Wait for a named signal in the handler

`WaitSignalAsync` stores the wait as a row and ends this execution. The job reads `suspended`. Suspends (no executor held) until the named signal is raised, then re-enters with the latched payload.

The lines that matter:

```csharp
var decision = await context.WaitSignalAsync<Decision>("approval", ct);
```

In `wait-signal.cs` (excerpt):

```csharp
public sealed record ApproveExpense(string Id, decimal Amount);

public sealed record Decision(bool Approved, string By);

public sealed class ApproveExpenseJob
{
    [Job("approve-expense")]
    public async Task Handle(ApproveExpense expense, JobContext context, CancellationToken ct)
    {
        var decision = await context.WaitSignalAsync<Decision>("approval", ct);
        var amount = expense.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        Console.WriteLine($"[{expense.Id}] expense for {amount} EUR {(decision!.Approved ? "APPROVED" : "rejected")} by {decision.By}");
    }
}
```

## 2. Raise the signal when the answer arrives

Whoever learns the answer raises the signal by name on that job: an endpoint, another job, an operator. The value is stored, the job becomes ready, and the handler runs again from its first line. This time the wait returns the stored value.

The lines that matter:

```csharp
await jobs.RaiseSignalAsync(job, "approval", new Decision(true, "alice"));
```

In `wait-signal.cs` (excerpt):

```csharp
}
else
{
    Console.WriteLine("Brief/non-interactive run: raising the approval signal automatically.");
}

await jobs.RaiseSignalAsync(job, "approval", new Decision(true, "alice"));

Console.WriteLine("approved - the job resumes.");
var terminal = await WaitForAsync(jobs, job.JobId, static snapshot => snapshot.Status.IsTerminal, "signal-waiting job to finish");
EnsureDone(terminal);
await ShowSignalStateAsync(lab, job.JobId);
await host.StopAsync();
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/200-durable-execution/204-wait-signal
```

Its output must contain:

```text
Brief/non-interactive run: raising the approval signal automatically.
approved - the job resumes.
[exp-1] expense for 4200.00 EUR APPROVED by alice
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A `suspended` job is healthy. It is waiting, not stuck.
- The handler starts from the top when the signal arrives. Put work that must not repeat in a step.
- Decide what happens if nobody answers: `WaitSignalAsync<Decision>("approval", TimeSpan.FromDays(14), ct)` returns an outcome whose `TimedOut` is true when the deadline passed.
- A raise proves nothing about who sent it. Check the caller in your endpoint before you raise.
- A raise that comes before the handler reaches its wait is stored, and the wait reads it without parking.
- The typed value is null when the signal was raised without one. A value binds leniently, so give an enum a first value that grants nothing.
- A signal keeps the value of its first raise. Keep timestamps and per-attempt ids out of it, so a repeated raise answers `AlreadyInState`.

## Check yourself

1. While the job waits for the signal, which worker holds it?
   - a) The worker that started it
   - b) Whichever worker is idle
   - c) No worker
   - Answer: c. The wait is a row. The job reads `suspended` with no worker and no lease, so the process can stop and the wait is still there.
2. The signal is raised. On which execution does the job finish?
   - a) Execution 1 carries on where it stopped
   - b) Execution 2, from the first line of the handler
   - c) A new job is created
   - Answer: b. Acta does not resume in the middle of a method. It runs the handler again, and the wait answers from its stored value.

## Not in the packages

`UseLocalDatabase` and `ConceptLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to hold a sign-up until the email is confirmed](/learn/221-pending-signup.md)

## More

- [The whole program and its notes](/learn/204-wait-signal.program.md): every file as compiled, 206 lines, and the notes on the design, the rows to inspect, and how to break it.
