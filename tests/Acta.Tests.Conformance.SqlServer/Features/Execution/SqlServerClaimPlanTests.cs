using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Acta.Tests.Conformance.Runtime;
using Acta.Tests.Conformance.SqlServer.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Execution;

/// <summary>
/// The actual plans claim_batch runs, returned by STATISTICS XML for every statement inside the procedure:
/// the claim seeks ix_runtimes_claim_ready one priority band at a time and reads only the due rows, and the
/// empty claim's horizon reads the head of each band once. Parked waits, which SQL Server sorts first in
/// their band, and a band of delayed High rows crowd the index around the due rows.
/// </summary>
public sealed class SqlServerClaimPlanTests : ClaimSkewTestBase<SqlServerConformanceFixture>
{
    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    [Fact(
        DisplayName = "claim_batch seeks ix_runtimes_claim_ready per priority band and reads only the due rows, and its horizon reads each band's head"
    )]
    public async Task Claim_seeks_each_band_and_reads_only_due_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ns, workerId) = await ClaimantAsync(ct);
        await SeedSkewAsync(ct);

        await using var conn = new SqlConnection(Schema.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (var statistics = new SqlCommand("SET STATISTICS XML ON", conn, tx))
        {
            await statistics.ExecuteNonQueryAsync(ct);
        }

        // The first claim takes every due row; the second finds none left and reports the horizon.
        var (claimed, claimPlans) = await ClaimAsync(conn, tx, ns, workerId, ct);
        var (none, horizonPlans) = await ClaimAsync(conn, tx, ns, workerId, ct);
        await tx.RollbackAsync(ct);
        Assert.Equal(DueRows, claimed);
        Assert.Equal(0, none);

        var seeks = ClaimIndexOperators(claimPlans, "READPAST");
        Assert.NotEmpty(seeks);
        // Each band's seek ends at its first row not yet due, so the rows read are the due rows, not the
        // parked or delayed rows ahead of them.
        Assert.InRange(seeks.Sum(s => Counter(s, "ActualRowsRead")), DueRows, DueRows + Enum.GetValues<JobPriorityCode>().Length);
        Assert.All(
            seeks,
            s =>
                Assert.Contains(
                    s.Descendants(Showplan + "SeekPredicates").Descendants(Showplan + "ColumnReference"),
                    c => (string?)c.Attribute("Column") is "priority_code" or "[priority_code]"
                )
        );

        var heads = ClaimIndexOperators(horizonPlans, "next_ready_at_utc");
        Assert.NotEmpty(heads);
        Assert.Equal(Enum.GetValues<JobPriorityCode>().Length, heads.Sum(s => Counter(s, "ActualExecutions")));
    }

    private async Task<(int Claimed, List<XDocument> Plans)> ClaimAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int ns,
        int workerId,
        CancellationToken ct
    )
    {
        await using var cmd = new SqlCommand($"{Schema.SchemaName}.claim_batch", conn, tx) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@p_namespace_id", ns);
        cmd.Parameters.AddWithValue("@p_leased_by_worker_id", workerId);
        cmd.Parameters.AddWithValue("@p_claim_limit", 16);
        cmd.Parameters.AddWithValue("@p_lease_ttl_seconds", 30);
        cmd.Parameters.AddWithValue("@p_start_executing", false);

        var claimed = 0;
        var plans = new List<XDocument>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        do
        {
            var showplan = reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.Ordinal);
            while (await reader.ReadAsync(ct))
            {
                if (showplan)
                {
                    plans.Add(XDocument.Parse(reader.GetString(0)));
                }
                else if (!reader.IsDBNull(0))
                {
                    claimed++;
                }
            }
        } while (await reader.NextResultAsync(ct));
        return (claimed, plans);
    }

    /// <summary>Every operator reading ix_runtimes_claim_ready inside the statements whose text contains <paramref name="marker"/>.</summary>
    private static List<XElement> ClaimIndexOperators(IEnumerable<XDocument> plans, string marker) =>
        [
            .. plans
                .SelectMany(p => p.Descendants(Showplan + "StmtSimple"))
                .Where(s => ((string?)s.Attribute("StatementText") ?? "").Contains(marker, StringComparison.Ordinal))
                .SelectMany(s => s.Descendants(Showplan + "RelOp"))
                .Where(op =>
                    op.Elements(Showplan + "IndexScan")
                        .Elements(Showplan + "Object")
                        .Any(o => (string?)o.Attribute("Index") == "[ix_runtimes_claim_ready]")
                ),
        ];

    private static int Counter(XElement op, string counter) =>
        op.Elements(Showplan + "RunTimeInformation")
            .Elements(Showplan + "RunTimeCountersPerThread")
            .Sum(t => int.Parse((string?)t.Attribute(counter) ?? "0", CultureInfo.InvariantCulture));
}
