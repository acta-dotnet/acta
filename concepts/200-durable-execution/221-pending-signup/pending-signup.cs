// How to hold a sign-up until the email is confirmed
// The job is the pending sign-up: Acta stores its input, a signal confirms it, and a deadline ends it with no account
// to clean up.
using System.Collections.Concurrent;
using Acta;
using Acta.Concepts.PendingSignup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<UserStore>();

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<PendingSignupJobs>("pending-signup");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var users = host.Services.GetRequiredService<UserStore>();

// learn: 3. Start the job when the form is submitted
// The input is the pending sign-up, so there is no table of pending users. The one-time code also goes into the
// link of the email. Ada gets a day to confirm; Sam gets two seconds, so the program can show a sign-up that expires.
var ada = await jobs.EnqueueAsync(new RegisterUser("ada@example.com", "Ada", "c0ffee", TimeSpan.FromHours(24)));

// learn: end
var sam = await jobs.EnqueueAsync(new RegisterUser("sam@example.com", "Sam", "decade", TimeSpan.FromSeconds(2)));

while ((await jobs.GetAsync(ada))?.Status != JobStatusCode.Suspended)
{
    await Task.Delay(50);
}

// learn: 4. Confirm it from the link in the email
// A raise proves nothing about who sent it, so the code from the link is compared with the one stored in the job
// before the signal is raised. The raise answers what it did.
var pending = await jobs.GetInputAsync<RegisterUser>(ada);
if (pending.Code == "c0ffee")
{
    var raised = await jobs.RaiseSignalAsync(ada, "email-confirmed", new EmailConfirmed("c0ffee"));
    Console.WriteLine($"ada's click: {raised.Action}");
}

// learn: end

await ada.WaitAsync();
await sam.WaitAsync();
Console.WriteLine($"ada@example.com: {(await jobs.GetAsync(ada))!.Status}");
Console.WriteLine($"sam@example.com: {(await jobs.GetAsync(sam))!.Status}");
Console.WriteLine($"users: {string.Join(", ", users.Emails)}");

// A click on Sam's link after his sign-up has ended: the answer is not Applied, so the page must not say "confirmed".
var late = await jobs.RaiseSignalAsync(sam, "email-confirmed", new EmailConfirmed("decade"));
Console.WriteLine($"sam's late click: {late.Action}");

await host.StopAsync();

namespace Acta.Concepts.PendingSignup
{
    public sealed record RegisterUser(string Email, string DisplayName, string Code, TimeSpan ConfirmWithin);

    public sealed record EmailConfirmed(string Code);

    /// <summary>The application's own users. Nothing reaches it before a sign-up is confirmed.</summary>
    public sealed class UserStore
    {
        private readonly ConcurrentDictionary<string, string> _users = new();

        public IEnumerable<string> Emails => _users.Keys.Order();

        public bool CreateIfAbsent(string email, string displayName) => _users.TryAdd(email, displayName);
    }

    public sealed class RegistrationJobs(UserStore users)
    {
        // The finished job is kept with its input for seven days, not the default ninety.
        [Job("register-user", JobRetention = "7d")]
        public async Task Handle(RegisterUser input, JobContext ctx, CancellationToken ct)
        {
            await ctx.RunStepAsync(
                "send-confirmation",
                _ =>
                {
                    Console.WriteLine($"[{input.Email}] confirmation email sent");
                    return Task.CompletedTask;
                },
                ct
            );

            // learn: 1. Wait for the confirmation, with a deadline
            // The wait is a row with its deadline stored in the database, so no process has to stay alive for it. When
            // nobody confirms in time, the job cancels itself: no user was ever written, so there is nothing to undo.
            var confirmed = await ctx.WaitSignalAsync<EmailConfirmed>("email-confirmed", input.ConfirmWithin, ct);
            if (confirmed.TimedOut)
            {
                Console.WriteLine($"[{input.Email}] not confirmed in time");
                await ctx.CancelAsync("not confirmed in time", ct);
            }
            // learn: end

            // learn: 2. Create the user only after the confirmation
            // Only now does the sign-up become a real user. The insert is keyed by the email, so a repeat of the step
            // adds nothing.
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
            // learn: end
        }
    }
}
