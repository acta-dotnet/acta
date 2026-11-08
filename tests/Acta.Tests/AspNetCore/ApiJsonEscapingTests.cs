using System.Net;
using Acta.Tests.Architecture;
using Acta.Tests.AspNetCore.Fakes;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The API escapes only what JSON requires, as the CLI and the event details do: an apostrophe and a plus sign go out as
/// they are, where the serializer's default encoder writes them as unicode escapes.
/// </summary>
public sealed class ApiJsonEscapingTests
{
    [Fact]
    public async Task A_response_writes_apostrophes_and_plus_signs_as_they_are()
    {
        var fakes = new FakeOperations();
        var explain = fakes.Jobs.Explain;
        fakes.Jobs.Explain = async (job, ct) =>
            await explain(job, ct) is { } explanation
                ? explanation with
                {
                    Headline = "Ready on 1.6.1+b81d04e; run 'jobs events' for the timeline.",
                }
                : null;
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var body = await client.GetStringAsync($"/acta/api/v1/jobs/{FakeJobs.FoundJobRef}/detail", TestContext.Current.CancellationToken);

        Assert.Contains("Ready on 1.6.1+b81d04e; run 'jobs events' for the timeline.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0027", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A problem document is written by the framework through the host's JSON options, the context lending it the type,
    /// and their default encoder is the relaxed one too.
    /// </summary>
    [Fact]
    public async Task A_problem_writes_apostrophes_as_they_are()
    {
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true);
        await using var _ = app;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{FakeJobs.FoundJobRef}/pause")
        {
            Content = new StringContent("""{"actor":"spoofed"}""", System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not valid at '$.actor'", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The generated <c>Default</c> context keeps the default encoder, so every response the API writes, and every type it
    /// lends the host's resolver chain, goes through <c>ActaApiJsonContext.Relaxed</c>.
    /// </summary>
    [Fact]
    public void No_shipped_code_writes_through_the_default_context()
    {
        var offenders = ShippedCode
            .SourceFiles("*.cs")
            .Where(file => File.ReadAllText(file.Path).Contains("ActaApiJsonContext.Default", StringComparison.Ordinal))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(offenders.Count == 0, "Write through ActaApiJsonContext.Relaxed:\n" + string.Join("\n", offenders));
    }
}
