using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Acta;
using Acta.AspNetCore;
using Acta.Sqlite;
using DeploymentSmoke;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

var output =
    Environment.GetEnvironmentVariable("ACTA_SMOKE_OUTPUT") ?? throw new InvalidOperationException("ACTA_SMOKE_OUTPUT is required.");
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:0");
builder.Services.AddAuthentication("Smoke").AddScheme<AuthenticationSchemeOptions, SmokeAuthentication>("Smoke", _ => { });
builder.Services.AddAuthorization();
builder.Services.UseActa(j =>
{
    j.UseSqlite(o =>
    {
        o.ConnectionString = $"Data Source={Path.Combine(output, "deployment.db")}";
        o.ApplyMigrationsOnStartup = true;
    });
    j.Run<DeploymentSmokeJobs>("deployment-smoke");
    j.ConfigureOptions(o => o.MaxConcurrentExecutors = 2);
});
var app = builder.Build();
app.UsePathBase("/operations");
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapActa(
    "/acta",
    o =>
    {
        o.LocalOnly = false;
        o.EnableControls = false;
        o.ConfigureEndpoints = group => group.RequireAuthorization();
    }
);
await app.StartAsync();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var jobs = app.Services.GetRequiredService<IJobs>();
var job = await jobs.EnqueueAsync(
    new JobEnqueueRequest("deployment-smoke", "binary-probe", JobPayload.CopyBytes(JobPayloadFormat.Bytes, [1, 2, 3, 4])),
    deadline.Token
);
while (await jobs.GetStatusAsync(job, deadline.Token) != JobStatusCode.Succeeded)
{
    await Task.Delay(25, deadline.Token);
}
await File.WriteAllTextAsync(
    Path.Combine(output, "ready.json"),
    JsonSerializer.Serialize(new { Port = new Uri(app.Urls.Single()).Port, JobRef = job.JobRef.ToString() })
);
await app.WaitForShutdownAsync();

namespace DeploymentSmoke
{
    public static class BinaryProbe
    {
        [Job("binary-probe", Format = "bytes")]
        public static async Task<byte[]> Run(byte[] input, JobContext ctx, CancellationToken ct)
        {
            if (!input.SequenceEqual(new byte[] { 1, 2, 3, 4 }))
            {
                throw new InvalidOperationException("Input payload did not survive package deployment.");
            }
            await ctx.SetVariableAsync("binary-checkpoint", JobPayload.CopyBytes(JobPayloadFormat.Bytes, [9, 10, 11, 12]), ct);
            return [5, 6, 7, 8];
        }
    }

    // Deliberately test-only. Production identity providers and TLS are outside this smoke.
    public sealed class SmokeAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var expected = Environment.GetEnvironmentVariable("ACTA_SMOKE_KEY");
            if (string.IsNullOrEmpty(expected) || Request.Headers["X-Smoke-Key"] != expected)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "smoke-operator")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
