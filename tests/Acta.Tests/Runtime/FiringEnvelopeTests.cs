using System.Text;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class FiringEnvelopeTests
{
    private static readonly DateTime Occurrence = new(2026, 1, 2, 3, 0, 0, DateTimeKind.Utc);

    [Fact(DisplayName = "A firing envelope is stored as exactly its members, in order")]
    public void Stored_json_is_pinned()
    {
        var envelope = new FiringEnvelope(
            Guid.Parse("01890a5d-ac96-774b-bcce-b302099a8057"),
            FiringEnvelope.ScheduledKind,
            Occurrence.AddSeconds(4),
            [new TriggeringSchedule("nightly", Occurrence, "Europe/Ljubljana", Occurrence.AddHours(1))]
        );

        Assert.Equal(
            """{"firingId":"01890a5d-ac96-774b-bcce-b302099a8057","kind":"scheduled","admittedAtUtc":"2026-01-02T03:00:04Z","contributors":[{"scheduleName":"nightly","occurrenceAtUtc":"2026-01-02T03:00:00Z","timeZoneId":"Europe/Ljubljana","coveredThroughUtc":"2026-01-02T04:00:00Z"}]}""",
            envelope.ToJson()
        );
    }

    [Fact(DisplayName = "A firing envelope with a member this build does not know reads its known members")]
    public void Unknown_member_is_ignored()
    {
        // A newer build serving the same namespace may store a member this one lacks; the firing must still read.
        var stored =
            """{"firingId":"01890a5d-ac96-774b-bcce-b302099a8057","kind":"scheduled","later":1,"admittedAtUtc":"2026-01-02T03:00:04Z","contributors":[{"scheduleName":"nightly","later":"x","occurrenceAtUtc":"2026-01-02T03:00:00Z","timeZoneId":"Europe/Ljubljana","coveredThroughUtc":"2026-01-02T04:00:00Z"}]}""";

        var envelope = FiringEnvelope.FromJson(Encoding.UTF8.GetBytes(stored));

        Assert.NotNull(envelope);
        Assert.Equal([new TriggeringSchedule("nightly", Occurrence, "Europe/Ljubljana", Occurrence.AddHours(1))], envelope.Contributors);
    }
}
