# How to hold a sign-up until the email is confirmed

The job is the pending sign-up: Acta stores its input, a signal confirms it, and a deadline ends it with no account to clean up.

- Section: Wait for people, events, and time, step 2
- Program: `learn/200-durable-execution/221-pending-signup` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/221-pending-signup
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Wait for the confirmation, with a deadline

The wait is a row with its deadline stored in the database, so no process has to stay alive for it. When nobody confirms in time, the job cancels itself: no user was ever written, so there is nothing to undo. `CancelAsync` ends the handler there, so the lines after it run only for a confirmed sign-up.

`pending-signup.cs` (excerpt):

```csharp
var confirmed = await ctx.WaitSignalAsync<EmailConfirmed>("email-confirmed", input.ConfirmWithin, ct);
if (confirmed.TimedOut)
{
    Console.WriteLine($"[{input.Email}] not confirmed in time");
    await ctx.CancelAsync("not confirmed in time", ct);
}
```

## 2. Create the user only after the confirmation

Only now does the sign-up become a real user. The insert is keyed by the email, so a repeat of the step adds nothing.

`pending-signup.cs` (excerpt):

```csharp
await ctx.RunStepAsync(
    "create-user",
    _ =>
    {
        users.CreateIfAbsent(input.Email, input.DisplayName);
        Console.WriteLine($"[{input.Email}] user created");
        return Task.CompletedTask;
    },
    ct
);
```

## 3. Start the job when the form is submitted

The input is the pending sign-up, so there is no table of pending users. The one-time code also goes into the link of the email. Ada gets a day to confirm; Sam gets two seconds, so the program can show a sign-up that expires.

The lines that matter:

```csharp
var ada = await jobs.EnqueueAsync(new RegisterUser("ada@example.com", "Ada", "c0ffee", TimeSpan.FromHours(24)));
```

In `pending-signup.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var users = host.Services.GetRequiredService<UserStore>();

var ada = await jobs.EnqueueAsync(new RegisterUser("ada@example.com", "Ada", "c0ffee", TimeSpan.FromHours(24)));
var sam = await jobs.EnqueueAsync(new RegisterUser("sam@example.com", "Sam", "decade", TimeSpan.FromSeconds(2)));
```

## 4. Confirm it from the link in the email

A raise proves nothing about who sent it, so the code from the link is compared with the one stored in the job before the signal is raised. The raise answers what it did.

The lines that matter:

```csharp
var pending = await jobs.GetInputAsync<RegisterUser>(ada);
if (pending.Code == "c0ffee")
{
    var raised = await jobs.RaiseSignalAsync(ada, "email-confirmed", new EmailConfirmed("c0ffee"));
    Console.WriteLine($"ada's click: {raised.Action}");
}
```

In `pending-signup.cs` (excerpt):

```csharp
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while ((await jobs.GetAsync(ada, timeout.Token))?.Status != JobStatusCode.Suspended)
{
    await Task.Delay(50, timeout.Token);
}

var pending = await jobs.GetInputAsync<RegisterUser>(ada);
if (pending.Code == "c0ffee")
{
    var raised = await jobs.RaiseSignalAsync(ada, "email-confirmed", new EmailConfirmed("c0ffee"));
    Console.WriteLine($"ada's click: {raised.Action}");
}

await ada.WaitAsync();
await sam.WaitAsync();
Console.WriteLine($"ada@example.com: {(await jobs.GetAsync(ada))!.Status}");
Console.WriteLine($"sam@example.com: {(await jobs.GetAsync(sam))!.Status}");
Console.WriteLine($"users: {string.Join(", ", users.Emails)}");
```

## 5. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/221-pending-signup
```

Its output must contain:

```text
ada's click: Applied
[ada@example.com] user created
[sam@example.com] not confirmed in time
sam@example.com: Cancelled
users: ada@example.com
sam's late click: Rejected
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- Nothing real exists until `create-user` runs. An unconfirmed sign-up ends as a cancelled job and leaves no half-made account behind.
- The deadline is stored once, as database time plus the timeout. Restarts and downtime never make it longer.
- The handler runs from the top after the confirmation. The email is not sent again, because the step that sends it already has a recorded outcome.
- Make `create-user` safe to repeat: insert only when the address is absent. A crash right after the insert runs the step again, and two submits of the form end as one account.
- A finished job is kept with its input for its retention period: a year by default, seven days here, through `[Job("register-user", JobRetention = "7d")]`. Keep the input to what the work needs when it holds personal data.
- In a web app the confirm link opens a page with a Confirm button that posts: mail scanners fetch the links they see, and a fetch must not confirm anything.
- `"send-confirmation"` and `"create-user"` are the keys to each step's stored result. Rename `"send-confirmation"` and a sign-up that already sent its email sends it again.
- `"email-confirmed"` names the signal a sign-up waits on. A renamed signal is a different signal, so let the waiting sign-ups finish before you rename it.
- A job that waits for a signal or a timer can stay stored for days. The name in `[Job("register-user")]` and the namespace in `Run<PendingSignupJobs>("pending-signup")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `RegisterUser` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `register-user-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Check yourself

1. The app is redeployed twelve hours after the sign-up. What happens to the pending registration?
   - a) It is lost and the person has to sign up again
   - b) Nothing: it is a suspended job, and the link still works
   - c) The confirmation email is sent a second time
   - Answer: b. The wait is a row with its deadline stored in the database. No process had to stay alive for it, and the email step is not repeated.
2. Nobody opens the link. What is left when the deadline has passed?
   - a) A user row that has to be deleted
   - b) A cancelled job with its timeline, and no user
   - c) A job that waits forever
   - Answer: b. The wait times out, the handler cancels the job, and `create-user` never runs. The job row stays for its retention period as the record of what happened.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to wait for time to pass without a running process](/learn/205-durable-sleep.md)

## More

- [The whole program](/learn/221-pending-signup.program.md): every file as compiled, 123 lines.
