using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Time;

/// <summary>
/// Conformance for <c>GetUtcNow</c>: a single scalar round-trip that returns the DB server's UTC
/// clock. The returned value must be UTC-kinded and within a two-minute window of the calling
/// process clock - confirming the provider wires a UTC-returning scalar and the C# binder reads
/// it correctly.
/// </summary>
[ConformanceSpec(
    "get-utc-now.scalar-clock",
    "GetUtcNow returns the DB server UTC instant within a two-minute window",
    Area = "Clock",
    Contract = "GetUtcNow executes a scalar round-trip and returns the DB server's UTC clock aligned with the C# UTC clock within a two-minute window.",
    Arrange = "A live provider connection is open.",
    Act = "GetUtcNow reads the DB server clock via a single scalar round-trip.",
    Assert = "The returned instant is UTC-kinded and within a two-minute window of the calling process clock."
)]
public abstract class GetUtcNowSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "The returned DateTime is UTC-kinded and within two minutes of the calling process clock")]
    public async Task Returns_utc_instant_within_two_minutes_of_process_clock()
    {
        var ct = TestContext.Current.CancellationToken;

        var before = DateTime.UtcNow;
        var result = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        var after = DateTime.UtcNow;

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.True(result >= before.AddMinutes(-2), $"DB clock {result:O} is more than 2 min behind process clock {before:O}.");
        Assert.True(result <= after.AddMinutes(2), $"DB clock {result:O} is more than 2 min ahead of process clock {after:O}.");
    }
}
