using Acta.Demos.ApiWorkerSplit.Contracts;

namespace Acta.Demos.ApiWorkerSplit;

public static class SendWelcomeEmailJob
{
    [Job(WelcomeEmailRoute.JobName)]
    public static async Task Handle(SendWelcomeEmail input, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}> user={input.UserId}");
    }
}
