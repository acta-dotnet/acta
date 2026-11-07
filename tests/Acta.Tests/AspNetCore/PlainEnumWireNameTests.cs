using System.Text.Json;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The two plain enums an SDK response carries, <see cref="ControlAction"/> and <see cref="EnqueueJobAction"/>, write their
/// kebab names under any serializer, not only under the API's: a host that returns a control response from its own endpoint
/// writes the same text the API writes beside the code families, which carry their converters on the type.
/// </summary>
public sealed class PlainEnumWireNameTests
{
    [Theory]
    [InlineData(ControlAction.Applied, "applied")]
    [InlineData(ControlAction.NotFound, "not-found")]
    [InlineData(ControlAction.Rejected, "rejected")]
    [InlineData(ControlAction.Accepted, "accepted")]
    [InlineData(ControlAction.VersionConflict, "version-conflict")]
    [InlineData(ControlAction.AlreadyInState, "already-in-state")]
    public void A_control_action_round_trips_through_default_options_by_its_kebab_name(ControlAction action, string wire)
    {
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(action));
        Assert.Equal(action, JsonSerializer.Deserialize<ControlAction>($"\"{wire}\""));
    }

    [Theory]
    [InlineData(EnqueueJobAction.Inserted, "inserted")]
    [InlineData(EnqueueJobAction.Deduplicated, "deduplicated")]
    public void An_enqueue_action_round_trips_through_default_options_by_its_kebab_name(EnqueueJobAction action, string wire)
    {
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(action));
        Assert.Equal(action, JsonSerializer.Deserialize<EnqueueJobAction>($"\"{wire}\""));
    }

    // Property casing is the host's; the value is Acta's.
    [Fact]
    public void A_control_response_under_default_options_writes_the_action_as_the_api_does()
    {
        var body = JsonSerializer.Serialize(new CancelJobResponse(42, ControlAction.Rejected, JobStatusCode.Succeeded, 1));

        Assert.Contains("\"Action\":\"rejected\"", body);
        Assert.Contains("\"Status\":\"succeeded\"", body);
        Assert.DoesNotContain("Rejected", body);
    }

    /// <summary>
    /// The first-job sample's shape: the host maps the API and answers a cancel from its own endpoint with the host's
    /// serializer, which the mapping gave the API's metadata first.
    /// </summary>
    [Fact]
    public async Task A_hosts_own_endpoint_writes_the_action_as_the_api_does()
    {
        var (app, client) = await TestDashboardHost.StartAsync(configureApp: app =>
            app.MapPost(
                "/orders/{jobRef}/cancel",
                async (string jobRef, IJobs jobs) =>
                {
                    var cancelled = await jobs.CancelAsync(JobRef.Parse(jobRef), reasonMessage: "because");
                    return Results.Ok(new { cancelled.Action, cancelled.Status });
                }
            )
        );
        await using var _ = app;

        var response = await client.PostAsync($"/orders/{FakeJobs.RejectedJobRef}/cancel", null, TestContext.Current.CancellationToken);

        Assert.Equal(
            """{"action":"rejected","status":"succeeded"}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }
}
