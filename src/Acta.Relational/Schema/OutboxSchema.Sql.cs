namespace Acta.Relational.Schema;

/// <summary>
/// Hand-authored parameter metadata for the external-outbox relay commands. Kept separate from the
/// ledger's <see cref="ActaSchema"/> because the source table is not part of the Acta entity model;
/// the relay binds these through <c>DbParams.For</c> so name, kind, and width flow from one place. The
/// executable claim/finalize SQL is provider-owned and lands with each provider package.
/// </summary>
internal static class OutboxSchema
{
    internal static class Sql
    {
        public static readonly DbValueSpec<Guid> ClaimToken = new(
            ParameterName: "p_claim_token",
            Kind: DbKind.Guid,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> ClaimBatchSize = new(
            ParameterName: "p_batch_size",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        public static readonly DbValueSpec<int> LeaseTtlSeconds = new(
            ParameterName: "p_lease_ttl_seconds",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // A JSON array of claimed outbox-id strings (["<guid>", ...]) so the set-based delete/release
        // finalize the whole claimed group in one round trip; each provider parses it natively
        // (PG jsonb_array_elements_text, SQLite json_each, SQL Server OPENJSON). Size -1 is the provider
        // max text width so a full 256-row batch of ids never truncates.
        public static readonly DbValueSpec<string> OutboxIds = new(
            ParameterName: "p_outbox_ids",
            Kind: DbKind.UnicodeString,
            Size: -1,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // The operator verbs' optional id filter: the same JSON id-array shape as OutboxIds, but NULL
        // means "every quarantined row" so a bulk requeue-after-fix needs no listing round trip.
        public static readonly DbValueSpec<string?> OutboxIdsOptional = new(
            ParameterName: "p_outbox_ids",
            Kind: DbKind.UnicodeString,
            Size: -1,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        public static readonly DbValueSpec<int> PageSize = new(
            ParameterName: "p_page_size",
            Kind: DbKind.Int32,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: false
        );

        // Keyset cursor for the quarantined listing: the last outbox_id of the previous page, NULL for
        // the first page. The id is the order key - see ListQuarantinedRows.sql for why not a timestamp.
        public static readonly DbValueSpec<Guid?> AfterOutboxId = new(
            ParameterName: "p_after_outbox_id",
            Kind: DbKind.Guid,
            Size: null,
            Precision: null,
            Scale: null,
            IsNullable: true
        );

        // The backlog count is the one relay command whose SQL is identical across the three source
        // providers, so it is composed here as shared inline text over the validated table reference
        // instead of landing as three per-provider resources.
        public static string CountBacklog(string tableRef) =>
            $"SELECT COUNT(*) FROM {tableRef} WHERE status_code = 10 /* OutboxStatusCode.Pending */;";

        // Same shared-inline reasoning as CountBacklog: the quarantine total the tick summary carries
        // cross-peer so operator surfaces see it without reaching the source database.
        public static string CountQuarantined(string tableRef) =>
            $"SELECT COUNT(*) FROM {tableRef} WHERE status_code = 90 /* OutboxStatusCode.Quarantined */;";

        // A JSON array of per-row reschedule/quarantine records
        // ([{"outbox_id","failure_count","backoff_seconds"?,"last_error"}, ...]) so each of those finalizes
        // the whole claimed group in one set-based round trip; every provider unnests it server-side
        // (PG jsonb_to_recordset, SQL Server OPENJSON ... WITH, SQLite json_each + json_extract). Each row
        // carries its own failure count, per-row backoff (added to the SOURCE clock in-SQL), and error, so
        // per-row semantics are preserved. Size -1 is the provider max text width.
        public static readonly DbValueSpec<string> RowRecords = new(
            ParameterName: "p_rows",
            Kind: DbKind.UnicodeString,
            Size: -1,
            Precision: null,
            Scale: null,
            IsNullable: false
        );
    }
}
