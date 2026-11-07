using System.Net;
using System.Text;
using System.Text.Json;
using Acta.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// A route reads a name as the rest of the API does, through NameSyntax: trimmed, folded to lowercase, and held to its
/// kind's cap, so a padded or mixed-case name reads its target and a non-name or an over-long one is the 400. These run
/// on the real operations over a throwaway SQLite file, since the fakes take any name.
/// </summary>
public sealed class RouteNameEndpointTests
{
    private const string Namespace = "route-names";

    /// <summary>The definition read, its tags, and its events, each below the definition's key.</summary>
    private static readonly string[] DefinitionRoutes = ["", "/tags", "/events"];

    /// <summary>A definition's routes read its key as the jobs list reads a job name, the controls included.</summary>
    [Fact]
    public async Task A_definition_route_reads_a_padded_mixed_case_key_and_refuses_a_non_name()
    {
        await WithSqliteHostAsync(async client =>
        {
            var key = $"{Padded("Route-Names")}/{Padded("Echo")}";
            foreach (var route in DefinitionRoutes)
            {
                Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, $"definitions/{key}{route}"));
                Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(client, HttpMethod.Get, $"definitions/{Namespace}/a..b{route}"));
            }

            // A stale version is the conflict only a definition the key names can answer.
            Assert.Equal(
                HttpStatusCode.Conflict,
                await SendAsync(client, HttpMethod.Post, $"definitions/{key}/retire", """{"expectedVersion":999}""")
            );
        });
    }

    [Fact]
    public async Task The_input_template_route_reads_a_padded_mixed_case_key_and_refuses_a_non_name()
    {
        await WithSqliteHostAsync(async client =>
        {
            var ct = TestContext.Current.CancellationToken;
            using var template = await client.GetAsync(Url($"definitions/{Padded("Route-Names")}/{Padded("Echo")}/input-template"), ct);
            var malformed = await SendAsync(client, HttpMethod.Get, $"definitions/{Namespace}/a..b/input-template");
            var unknown = await SendAsync(client, HttpMethod.Get, $"definitions/{Namespace}/no-such-job/input-template");

            Assert.Equal(HttpStatusCode.OK, template.StatusCode);
            using var body = JsonDocument.Parse(await template.Content.ReadAsStringAsync(ct));
            Assert.Equal(Namespace, body.RootElement.GetProperty("jobNamespace").GetString());
            Assert.Equal("echo", body.RootElement.GetProperty("jobName").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, malformed);
            Assert.Equal(HttpStatusCode.NotFound, unknown);
        });
    }

    /// <summary>A job name takes 128 characters, so a schedule of a job with a 100-character name is addressable.</summary>
    [Fact]
    public async Task A_schedule_route_takes_a_job_name_up_to_the_job_name_cap()
    {
        await WithSqliteHostAsync(async client =>
        {
            var schedule = $"schedules/{Namespace}/{new string('a', 100)}/every-5-minutes";
            var overLong = $"schedules/{Namespace}/{new string('a', 129)}/every-5-minutes";

            Assert.Equal(
                HttpStatusCode.OK,
                await SendAsync(client, HttpMethod.Get, $"schedules/{Namespace}/{Padded("Recurring-Ping")}/every-5-minutes")
            );
            foreach (var (method, route) in Routes(schedule))
            {
                Assert.Equal(HttpStatusCode.NotFound, await SendAsync(client, method, route));
            }
            foreach (var (method, route) in Routes(overLong))
            {
                Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(client, method, route));
            }
        });

        static (HttpMethod, string)[] Routes(string schedule) =>
            [
                (HttpMethod.Get, schedule),
                (HttpMethod.Get, schedule + "/preview"),
                (HttpMethod.Post, schedule + "/pause"),
                (HttpMethod.Post, schedule + "/resume"),
                (HttpMethod.Post, schedule + "/trigger"),
            ];
    }

    /// <summary>A namespace takes 64 characters on every route, the controls and the list's name filter included.</summary>
    [Fact]
    public async Task A_namespace_route_refuses_a_name_over_the_namespace_cap()
    {
        await WithSqliteHostAsync(async client =>
        {
            var longest = new string('a', 64);
            var overLong = new string('a', 65);

            Assert.Equal(HttpStatusCode.NotFound, await SendAsync(client, HttpMethod.Post, $"namespaces/{longest}/suspend"));
            Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, $"namespaces?nameContains={longest}"));
            Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(client, HttpMethod.Get, $"namespaces/{overLong}"));
            Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(client, HttpMethod.Post, $"namespaces/{overLong}/suspend"));
            Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(client, HttpMethod.Post, $"namespaces/{overLong}/resume"));
            Assert.Equal(
                HttpStatusCode.BadRequest,
                await SendAsync(client, HttpMethod.Put, $"namespaces/{overLong}", """{"expectedVersion":1}""")
            );
            Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(client, HttpMethod.Get, $"namespaces?nameContains={overLong}"));
        });
    }

    private static string Padded(string name) => Uri.EscapeDataString($" {name} ");

    private static string Url(string path) => "/acta/api/v1/" + path;

    // A confirmed call; a GET sends no body.
    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string path, string body = "{}")
    {
        using var request = new HttpRequestMessage(method, Url(path))
        {
            Content = method == HttpMethod.Get ? null : new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Acta-Control", "true");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task WithSqliteHostAsync(Func<HttpClient, Task> act)
    {
        var path = Path.Combine(Path.GetTempPath(), $"acta-route-names-{Guid.NewGuid():N}.db");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.UseActa(j =>
        {
            j.UseSqlite(o =>
            {
                o.ConnectionString = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
                o.MigrateOnStartup = true;
            });
            j.Run<TestJobs.TestJobsManifest>(Namespace, ownerTeam: "test");
        });
        builder.Services.Configure<JobsOptions>(o => o.RunSystemJobs = false);
        var app = builder.Build();
        app.MapActa("/acta", o => o.EnableControls = true);
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            await act(app.GetTestClient());
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }
}
