using Acta.Emit.Features.Migrations;
using Acta.Emit.Shared;
using Acta.Emit.Shared.Sql;
using Acta.Relational.Schema;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// The baseline stamp is a content hash of a provider's baseline migration, written into that
/// migration by the emitter and required at bootstrap from the generated constants. These prove the
/// two sides still name the same bodies; the emitter's `check` command makes the same comparison
/// outside the test run.
/// </summary>
public sealed class BaselineStampParityTests
{
    public static TheoryData<string> ProviderTokens => [.. ProviderCatalog.All.Select(p => p.Token)];

    private static string BaselineFile(string token)
    {
        var provider = ProviderCatalog.Resolve(token)!;
        return MigrationFiles.BaselineFile(IntegrationConfig.FindRepoRoot(), provider.Suffix)
            ?? throw new InvalidOperationException($"Provider '{token}' has no baseline migration recording a stamp row.");
    }

    [Theory]
    [MemberData(nameof(ProviderTokens))]
    public void Provider_baseline_migration_carries_its_generated_constant(string token)
    {
        var expected = BaselineStamps.ForDialect(token);

        Assert.Contains($"'{expected}'", File.ReadAllText(BaselineFile(token)), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ProviderTokens))]
    public void Provider_baseline_migration_still_hashes_to_its_generated_constant(string token)
    {
        var body = File.ReadAllText(BaselineFile(token));

        Assert.Equal(BaselineStamps.ForDialect(token), BaselineStamp.Of(body));
    }

    [Theory]
    [MemberData(nameof(ProviderTokens))]
    public void One_changed_character_outside_the_stamp_changes_the_hash(string token)
    {
        var body = File.ReadAllText(BaselineFile(token));
        Assert.Contains("NOT NULL", body, StringComparison.Ordinal);
        // One character of DDL, nowhere near the stamp row: a hand-edit anywhere in the file has to
        // move the hash, or the stamp would not be naming the schema the file actually creates.
        var mutated = ReplaceFirst(body, "NOT NULL", "NOT NULl");

        Assert.NotEqual(BaselineStamps.ForDialect(token), BaselineStamp.Of(mutated));
    }

    private static string ReplaceFirst(string text, string find, string replacement)
    {
        var at = text.IndexOf(find, StringComparison.Ordinal);
        return text[..at] + replacement + text[(at + find.Length)..];
    }
}

/// <summary>
/// What the stamp's canonical form does and does not absorb. A checkout can change line endings and
/// prepend a byte-order mark without changing the schema, so neither may change the stamp.
/// </summary>
public sealed class BaselineStampCanonicalizationTests
{
    private const string Body = "-- M001_init\nCREATE TABLE t (id integer NOT NULL);\nVALUES (0, '{{baseline-stamp}}');\n";

    [Fact]
    public void Line_endings_do_not_change_the_stamp()
    {
        Assert.Equal(BaselineStamp.Of(Body), BaselineStamp.Of(Body.ReplaceLineEndings("\r\n")));
    }

    [Fact]
    public void A_byte_order_mark_does_not_change_the_stamp()
    {
        Assert.Equal(BaselineStamp.Of(Body), BaselineStamp.Of("﻿" + Body));
    }

    [Fact]
    public void Substituting_leaves_no_token_and_records_the_bodys_own_hash()
    {
        var written = BaselineStamp.Substitute(Body);

        Assert.DoesNotContain(BaselineStamp.Token, written, StringComparison.Ordinal);
        Assert.Equal(BaselineStamp.Of(Body), BaselineStamp.Recorded(written));
        // Re-hashing the written file is the drift gate's move, and it has to land on the value the
        // file records rather than on a hash of that value.
        Assert.Equal(BaselineStamp.Recorded(written), BaselineStamp.Of(written));
    }
}
