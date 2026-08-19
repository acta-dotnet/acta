using System.Text.Json;
using Acta.Emit.Features.Migrations;
using Acta.Emit.Shared.Model;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class SchemaSnapshotTests
{
    // Records with IReadOnlyList members lack structural equality, so compare via canonical
    // (newline-normalized) JSON: also immune to on-disk line-ending differences.
    private static string Canon(SchemaSnapshot s) =>
        JsonSerializer.Serialize(s, SchemaSnapshotJsonContext.Default.SchemaSnapshot).ReplaceLineEndings("\n");

    [Fact]
    public void Capture_includes_every_entity_and_is_table_ordered()
    {
        var model = SchemaModel.Discover();
        var families = CodeFamilyDiscovery.DiscoverAll(model);

        var snapshot = SchemaSnapshot.Capture(model, families);

        Assert.Equal(model.Entities.Count, snapshot.Entities.Count);
        var tables = snapshot.Entities.Select(e => e.Table).ToList();
        Assert.Equal(tables.OrderBy(t => t, StringComparer.Ordinal), tables);
        Assert.Contains(snapshot.Entities, e => e.Table == "jobs");
    }

    [Fact]
    public void Capture_records_code_family_membership()
    {
        var model = SchemaModel.Discover();
        var families = CodeFamilyDiscovery.DiscoverAll(model);

        var snapshot = SchemaSnapshot.Capture(model, families);

        Assert.NotEmpty(snapshot.CodeFamilies);
        Assert.All(snapshot.CodeFamilies, f => Assert.NotEmpty(f.Values));
    }

    // Extensible families (AlertKindCode, EventCode, JobEventReasonCode: real [CodeKind(Extensible = true)]
    // enums) emit no CHECK-list DDL at all (SqlSchemaEmitter's own !IsExtensible guard), so nothing about
    // their membership is physical schema: the snapshot must not carry them at all, and the coded columns
    // that use them must carry no value list either. A non-extensible family (job-status) is the control:
    // it still IS a physical CHECK constraint, so it must still be captured with values, and its coded
    // column must still carry its value list.
    [Fact]
    public void Capture_omits_extensible_families_and_their_column_value_lists_but_keeps_closed_ones()
    {
        var model = SchemaModel.Discover();
        var families = CodeFamilyDiscovery.DiscoverAll(model);

        var snapshot = SchemaSnapshot.Capture(model, families);

        Assert.DoesNotContain(snapshot.CodeFamilies, f => f.CodeKind is "alert-kind" or "event" or "job-event-reason");
        Assert.Contains(snapshot.CodeFamilies, f => f.CodeKind == "job-status" && f.Values.Count > 0);

        var alerts = Assert.Single(snapshot.Entities, e => e.Table == "alerts");
        var kindCode = Assert.Single(alerts.Columns, c => c.Name == "kind_code");
        Assert.Contains("coded=True", kindCode.Signature);
        Assert.Contains("codeKind=alert-kind", kindCode.Signature);
        Assert.EndsWith("values=", kindCode.Signature);

        var events = Assert.Single(snapshot.Entities, e => e.Table == "events");
        var reasonCode = Assert.Single(events.Columns, c => c.Name == "reason_code");
        Assert.Contains("coded=True", reasonCode.Signature);
        Assert.Contains("codeKind=job-event-reason", reasonCode.Signature);
        Assert.EndsWith("values=", reasonCode.Signature);
        var eventCode = Assert.Single(events.Columns, c => c.Name == "event_code");
        Assert.Contains("coded=True", eventCode.Signature);
        Assert.Contains("codeKind=event", eventCode.Signature);
        Assert.EndsWith("values=", eventCode.Signature);

        var runtimes = Assert.Single(snapshot.Entities, e => e.Table == "runtimes");
        var statusCode = Assert.Single(runtimes.Columns, c => c.Name == "status_code");
        Assert.Contains("codeKind=job-status", statusCode.Signature);
        Assert.Matches(@"values=\d+(,\d+)*$", statusCode.Signature);
    }

    // Both directions, both extensibility kinds, exercised directly through Capture(model, families) — the
    // exact seam SchemaAddCommand and CheckCommand drive. A synthetic family stands in for a real one so a
    // member can be added and removed at test time (real [Code] enums are fixed at compile time).
    [Theory]
    [InlineData(true)] // extensible: gaining/losing a member must be invisible to the diff
    [InlineData(false)] // closed: gaining/losing a member must still be reported
    public void Extensible_membership_changes_are_invisible_to_the_diff_closed_ones_are_not(bool isExtensible)
    {
        var model = SchemaModel.Discover();
        var family = (byte[] ids) =>
            (IReadOnlyList<CodeFamilyModel>)
                [
                    new CodeFamilyModel(
                        Name: "FakeFamily",
                        CodeKind: "test-fake-family",
                        Storage: "byte",
                        Values: ids.Select(id => new CodeEntryModel(id, $"M{id}", $"code-{id}", $"Description {id}.", "Active")).ToList(),
                        ReservedCodes: [],
                        ReservedRanges: [],
                        IsExtensible: isExtensible
                    ),
                ];

        var baseline = SchemaSnapshot.Capture(model, family([0, 10]));
        var memberAdded = SchemaSnapshot.Capture(model, family([0, 10, 20]));
        var memberRemoved = SchemaSnapshot.Capture(model, family([0]));

        var addedDiff = SchemaDiff.Compute(baseline, memberAdded);
        var removedDiff = SchemaDiff.Compute(baseline, memberRemoved);

        if (isExtensible)
        {
            Assert.Empty(baseline.CodeFamilies); // never recorded in the first place
            Assert.True(addedDiff.IsEmpty, string.Join("; ", addedDiff.Warnings));
            Assert.True(removedDiff.IsEmpty, string.Join("; ", removedDiff.Warnings));
        }
        else
        {
            Assert.Single(baseline.CodeFamilies);
            Assert.False(addedDiff.IsEmpty);
            Assert.False(removedDiff.IsEmpty);
        }
    }

    [Fact]
    public void Save_then_Load_round_trips_identically()
    {
        var model = SchemaModel.Discover();
        var families = CodeFamilyDiscovery.DiscoverAll(model);
        var snapshot = SchemaSnapshot.Capture(model, families);
        var path = Path.Combine(Path.GetTempPath(), $"acta-snap-{Guid.NewGuid():N}.json");

        try
        {
            SchemaSnapshot.Save(snapshot, path);
            var reloaded = SchemaSnapshot.Load(path);
            Assert.Equal(Canon(snapshot), Canon(reloaded));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
