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
        Assert.All(snapshot.CodeFamilies, f => Assert.NotEmpty(f.ValueIds));
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
        Assert.Contains(snapshot.CodeFamilies, f => f.CodeKind == "job-status" && f.ValueIds.Count > 0);

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

    // The same rule one layer down: the extensibility test above decides WHICH families contribute, this
    // decides WHICH FIELDS of one. A closed family's entire DDL footprint is the numeric IN-list of its
    // synthetic check (`SqlSchemaEmitter.cs:133-143` renders `CHECK (col IN (<ids>))`), so only Id is
    // physical: rewording a [Code] description — or renaming its code, or retiring it — must leave the
    // snapshot byte-identical, while renumbering must still warn. Both directions in one test on purpose;
    // the negative alone would pass if the code-family section were deleted outright.
    // Equality is asserted over canonical JSON as well as over IsEmpty because that string comparison is
    // literally what `Acta.Emit check` calls drift (`CheckCommand.cs:59`).
    [Fact]
    public void Code_value_text_edits_leave_the_snapshot_identical_but_id_set_changes_still_warn()
    {
        var model = SchemaModel.Discover();
        var family = (byte[] ids, string code, string description, string lifecycle) =>
            (IReadOnlyList<CodeFamilyModel>)
                [
                    new CodeFamilyModel(
                        Name: "FakeFamily",
                        CodeKind: "test-fake-family",
                        Storage: "byte",
                        Values: ids.Select(id => new CodeEntryModel(id, $"M{id}", $"{code}-{id}", $"{description} {id}.", lifecycle))
                            .ToList(),
                        ReservedCodes: [],
                        ReservedRanges: [],
                        IsExtensible: false
                    ),
                ];

        var baseline = SchemaSnapshot.Capture(model, family([10, 20], "code", "Description", "Active"));
        var reworded = SchemaSnapshot.Capture(model, family([10, 20], "code", "Reworded description", "Active"));
        var retexted = SchemaSnapshot.Capture(model, family([10, 20], "renamed", "Reworded description", "Deprecated"));
        var renumbered = SchemaSnapshot.Capture(model, family([10, 30], "code", "Description", "Active"));

        // Description-only edit — a [Code] doc comment moved and nothing else. This is the case that was
        // minting empty migrations.
        Assert.Equal(Canon(baseline), Canon(reworded));
        Assert.True(SchemaDiff.Compute(baseline, reworded).IsEmpty);

        // Code text and lifecycle move too: still no DDL, so still no drift.
        Assert.Equal(Canon(baseline), Canon(retexted));
        Assert.True(SchemaDiff.Compute(baseline, retexted).IsEmpty);

        // Id set moves: a real change to the emitted IN-list, so it must still be reported.
        var renumberedDiff = SchemaDiff.Compute(baseline, renumbered);
        Assert.NotEqual(Canon(baseline), Canon(renumbered));
        Assert.False(renumberedDiff.IsEmpty);
        Assert.Contains(renumberedDiff.Warnings, w => w.Contains("test-fake-family value ids changed ('10,20' -> '10,30')"));
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
