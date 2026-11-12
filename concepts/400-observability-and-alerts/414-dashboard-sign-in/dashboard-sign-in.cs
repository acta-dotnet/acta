// Concept: the host's own cookie sign-in in front of the dashboard and the API, its roles behind Acta's four permissions.
using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Acta;
using Acta.AspNetCore;
using Acta.Concepts.DashboardSignIn;
using Acta.Dashboard;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

const string jobNamespace = "dashboard-sign-in";
const string tokenField = "__RequestVerificationToken";

var builder = WebApplication.CreateBuilder(args);

// The post with no token below is refused on purpose: keep the developer exception page's trace out of the output.
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics", LogLevel.None);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DashboardSignInJobs>(jobNamespace);
});

// The host owns sign-in: Acta ships none. This is ASP.NET Core's cookie handler with no Identity behind it. The ticket is
// short and never renewed, so a session ends while the dashboard is open; a real host chooses its own lifetime.
builder
    .Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.LoginPath = "/sign-in";
        cookie.ExpireTimeSpan = TimeSpan.FromMinutes(2);
        cookie.SlidingExpiration = false;
    });

// The roles are the host's own. Acta asks for one policy per permission, and mapping throws when one of the four is missing.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ActaPermissions.Read, policy => policy.RequireRole("Reader", "Operator", "Admin"));
    options.AddPolicy(ActaPermissions.Enqueue, policy => policy.RequireRole("Operator", "Admin"));
    options.AddPolicy(ActaPermissions.Control, policy => policy.RequireRole("Operator", "Admin"));
    options.AddPolicy(ActaPermissions.Admin, policy => policy.RequireRole("Admin"));
});
builder.Services.AddAntiforgery();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Three development-only users, each one's password its name. A real host checks its own store. The role comes from
// here, never from anything the caller sends.
var roles = new Dictionary<string, string>
{
    ["reader"] = "Reader",
    ["operator"] = "Operator",
    ["admin"] = "Admin",
};

// One page signs in and out. Both forms carry an antiforgery token: a cookie alone does not show the person meant the post.
app.MapGet(
    "/sign-in",
    (HttpContext http, IAntiforgery antiforgery, string? returnUrl) =>
    {
        var token = antiforgery.GetAndStoreTokens(http).RequestToken;
        var who = http.User.Identity?.Name is { } name ? $"Signed in as {name}." : "Nobody is signed in.";
        return Results.Content(
            $"""
            <!doctype html><title>Sign in</title>
            <p>{who} Sign in as reader, operator or admin; the password is the name. <a href="/acta/">Dashboard</a></p>
            <form method="post" action="/sign-in">
              <input type="hidden" name="{tokenField}" value="{token}">
              <input type="hidden" name="returnUrl" value="{WebUtility.HtmlEncode(returnUrl)}">
              <input name="name" placeholder="name" autofocus> <input name="password" type="password" placeholder="password">
              <button>Sign in</button>
            </form>
            <form method="post" action="/sign-out">
              <input type="hidden" name="{tokenField}" value="{token}"><button>Sign out</button>
            </form>
            """,
            "text/html"
        );
    }
);

// A route that binds a form is refused with 400 before it runs when the token is missing or wrong.
app.MapPost(
    "/sign-in",
    async (HttpContext http, [FromForm] string name, [FromForm] string password, [FromForm] string? returnUrl) =>
    {
        if (!roles.TryGetValue(name, out var role) || password != name)
        {
            return Results.Redirect("/sign-in");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)],
            CookieAuthenticationDefaults.AuthenticationScheme
        );
        await http.SignInAsync(new ClaimsPrincipal(identity));

        // Back to where the browser was headed, when that is a path on this site: a link must not send a signed-in
        // browser away. LocalRedirect refuses whatever else is not local.
        return Results.LocalRedirect(returnUrl is ['/', not ('/' or '\\'), ..] ? returnUrl : "/acta/");
    }
);

// Signing out clears this browser's cookie. A copy of the cookie still answers until its ticket ends, which is why
// the ticket is short.
app.MapPost(
    "/sign-out",
    async (HttpContext http, IAntiforgery antiforgery) =>
    {
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            return Results.BadRequest();
        }

        await http.SignOutAsync();
        return Results.Redirect("/sign-in");
    }
);

// RequirePermissions has Acta check every route, the dashboard's pages included, against the policy named for its
// permission. With no session a page is sent to the sign-in above, and an API call answers 401 with no redirect,
// which the dashboard shows as an ended session. LocalOnly and the control confirmation stay on.
app.MapActa(
    "/acta",
    options =>
    {
        options.EnableControls = true;
        options.RequirePermissions = true;
    }
);

await app.StartAsync();

var jobs = app.Services.GetRequiredService<IJobs>();
var namespaces = app.Services.GetRequiredService<INamespaces>();
var site = new Uri(app.Urls.First(url => url.StartsWith("http://", StringComparison.Ordinal)));

// An earlier run may have left the namespace suspended, from the dashboard or by stopping midway; it would refuse the job.
await namespaces.ResumeAsync(jobNamespace);

// A job delayed five minutes, so the worker leaves it alone while the calls below pause it.
var job = await jobs.ScheduleAsync(new SampleTask("seeded"), TimeSpan.FromMinutes(5));
var pause = $"/acta/api/v1/jobs/{job.JobRef}/pause";
var suspend = $"/acta/api/v1/namespaces/{jobNamespace}/suspend";

Console.WriteLine("Nobody signed in:");
using var nobody = Browser();
await CallAsync(nobody, HttpMethod.Get, "/acta/");
await CallAsync(nobody, HttpMethod.Get, "/acta/api/v1/jobs");
await CallAsync(nobody, HttpMethod.Post, "/sign-in", new() { ["name"] = "admin", ["password"] = "admin" });

Console.WriteLine("Reader:");
using var reader = await SignedInAsync("reader");
await CallAsync(reader, HttpMethod.Get, "/acta/api/v1/jobs");
var held = await CallAsync(reader, HttpMethod.Get, "/acta/api/v1/capabilities");
Console.WriteLine($"  holds {string.Join(", ", held!["permissions"]!.AsArray().Select(p => p!.GetValue<string>()))}");
await CallAsync(reader, HttpMethod.Post, pause);
Console.WriteLine($"  the job is {(await jobs.GetAsync(job))!.Status}");

Console.WriteLine("Operator:");
using var @operator = await SignedInAsync("operator");
await CallAsync(@operator, HttpMethod.Post, pause);
Console.WriteLine($"  the job is {(await jobs.GetAsync(job))!.Status}");
await CallAsync(@operator, HttpMethod.Post, suspend);

Console.WriteLine("Admin:");
using var admin = await SignedInAsync("admin");
await CallAsync(admin, HttpMethod.Post, suspend);
Console.WriteLine($"  the namespace is {(await namespaces.GetAsync(jobNamespace))!.Status}");
await CallAsync(admin, HttpMethod.Post, $"/acta/api/v1/namespaces/{jobNamespace}/resume");
Console.WriteLine($"  the namespace is {(await namespaces.GetAsync(jobNamespace))!.Status}");
await CallAsync(admin, HttpMethod.Post, "/sign-out", new() { [tokenField] = await TokenAsync(admin) });
await CallAsync(admin, HttpMethod.Get, "/acta/api/v1/capabilities");

Console.WriteLine();
Console.WriteLine($"Open {app.Urls.First()}/acta/ and sign in as reader, operator or admin; the password is the name.");
Console.WriteLine("A session lasts two minutes: leave the dashboard open and it says the session has ended.");
Console.WriteLine("Sign out, or change user, at /sign-in. Press Ctrl+C to stop.");

await app.WaitForShutdownAsync();

// A browser of its own: its cookies, and no redirect followed, so each answer prints as the server gave it.
HttpClient Browser() => new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = site };

// The token the sign-in page hands this browser, with the cookie that pairs with it.
async Task<string> TokenAsync(HttpClient browser) =>
    Regex.Match(await browser.GetStringAsync("/sign-in"), $"name=\"{tokenField}\" value=\"([^\"]+)\"").Groups[1].Value;

// Signs a new browser in through the form, as a person would.
async Task<HttpClient> SignedInAsync(string name)
{
    var browser = Browser();
    await CallAsync(
        browser,
        HttpMethod.Post,
        "/sign-in",
        new()
        {
            [tokenField] = await TokenAsync(browser),
            ["name"] = name,
            ["password"] = name,
        }
    );
    return browser;
}

// One call. It prints the status and what the answer carried: where a redirect leads, the permission a 403 names,
// the action a control answered.
async Task<JsonNode?> CallAsync(HttpClient browser, HttpMethod method, string path, Dictionary<string, string>? form = null)
{
    using var request = new HttpRequestMessage(method, path);
    if (form is not null)
    {
        request.Content = new FormUrlEncodedContent(form);
    }
    else if (method == HttpMethod.Post)
    {
        // A control is confirmed by this header or by a JSON body.
        request.Headers.Add("X-Acta-Control", "true");
    }

    using var response = await browser.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    var json =
        response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.Ordinal) == true ? JsonNode.Parse(text) : null;
    var carried = response.Headers.Location?.OriginalString ?? (json?["detail"] ?? json?["action"])?.GetValue<string>();
    Console.WriteLine($"  {method} {path} -> {(int)response.StatusCode} {carried}".TrimEnd());
    return json;
}

namespace Acta.Concepts.DashboardSignIn
{
    public sealed record SampleTask(string Id);

    public static class SampleTaskJob
    {
        [Job("sample-task")]
        public static void Handle(SampleTask input) { }
    }
}
