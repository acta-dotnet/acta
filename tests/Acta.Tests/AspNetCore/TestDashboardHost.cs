using System.Security.Claims;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// In-process dashboard host over a <see cref="FakeOperations"/> world; no database, no network listener.
/// </summary>
internal static class TestDashboardHost
{
    public static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        Action<ActaDashboardOptions>? configureDashboard = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        FakeOperations? fakes = null,
        Action<WebApplication>? configureApp = null
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var world = fakes ?? new FakeOperations();
        builder.Services.AddSingleton<IJobs>(world.Jobs);
        builder.Services.AddSingleton<IActaOperations>(world);
        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        configureApp?.Invoke(app);
        app.MapActa("/acta", configureDashboard);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, app.GetTestClient());
    }

    /// <summary>Same as <see cref="StartAsync"/>, but every request carries an authenticated
    /// <see cref="ClaimsPrincipal"/> named <paramref name="principalName"/> (set unconditionally by a
    /// middleware, no auth scheme involved) so control endpoints can read <c>http.User.Identity.Name</c>.</summary>
    public static Task<(WebApplication App, HttpClient Client)> StartAuthenticatedAsync(
        string principalName = "test-operator",
        Action<ActaDashboardOptions>? configureDashboard = null,
        FakeOperations? fakes = null
    ) =>
        StartAsync(
            configureDashboard,
            fakes: fakes,
            configureApp: app =>
                app.Use(
                    (ctx, next) =>
                    {
                        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, principalName)], "Test"));
                        return next(ctx);
                    }
                )
        );

    /// <summary>
    /// Request content declaring neither a length nor a content type: the shape a <c>StreamContent</c>
    /// over a non-seekable stream takes on the wire (chunked, no <c>Content-Type</c>), which is the one
    /// case the optional-body readers cannot answer from headers alone.
    /// </summary>
    public static HttpContent UndeclaredContent(string body) =>
        new StreamContent(new UnseekableStream(System.Text.Encoding.UTF8.GetBytes(body)));

    // Refuses CanSeek so StreamContent.TryComputeLength fails and no Content-Length is sent.
    private sealed class UnseekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
