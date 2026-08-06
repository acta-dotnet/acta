// Frontend-only "Copy SQL" builder for the jobs, events, and schedules lists. It renders the filters
// currently applied on a list route as a SELECT against the curated operator views (jobs_view /
// events_view / schedules_view, qualified by the configured schema). Those views are for ad-hoc
// inspection and learning the schema only, never a stable integration API, so the generated statement
// is a starting point for a human at a SQL prompt, not a contract. Interpolated string values are
// single-quote escaped; the code filters go through the decoded text columns (event / actor / reason
// / status), which is what the kebab-case filter values the dashboard carries actually match (the raw
// *_code columns are the undecoded numeric codes).

const ROW_LIMIT = 100;

// The SQL flavor the emitted statement targets. Provider and schema come from the /capabilities read;
// both default to a pg-shaped statement in the `acta` schema so a Copy fired before capabilities loads
// still yields a runnable starting point. Every provider installs its operator views schema-qualified
// (on SQLite `schema` is the attached database, normally `main`), so one `{schema}.{view}` form is
// correct across all three; only the row-cap syntax differs (SQL Server has no LIMIT).
export interface SqlDialect {
  provider?: string;
  schema?: string;
}

// The button is icon-only, so this hint carries the whole feature: what it copies, and the caveat.
export const COPY_SQL_TITLE =
  'Copy these filters as SQL against the operator views. For ad-hoc inspection; the views are not a stable integration API.';

type Predicate = { column: string; op: string; value: string | number } | { raw: string };

// Single-quote escape a string literal; numbers pass through as bare literals (no quoting) so numeric
// columns (tenant_id, worker_id) are not compared against a quoted string.
function sqlLiteral(value: string | number): string {
  return typeof value === 'number' ? String(value) : `'${value.replace(/'/g, "''")}'`;
}

function select(dialect: SqlDialect, view: string, predicates: Predicate[], orderBy: string): string {
  const where =
    predicates.length === 0
      ? ''
      : ' WHERE ' +
        predicates.map((p) => ('raw' in p ? p.raw : `${p.column} ${p.op} ${sqlLiteral(p.value)}`)).join(' AND ');
  const from = `${dialect.schema ?? 'acta'}.${view}`;
  // SQL Server has no trailing LIMIT; the row cap rides in the projection as TOP. pg / sqlite / unknown
  // all take LIMIT.
  return dialect.provider === 'mssql'
    ? `SELECT TOP ${ROW_LIMIT} * FROM ${from}${where} ORDER BY ${orderBy};`
    : `SELECT * FROM ${from}${where} ORDER BY ${orderBy} LIMIT ${ROW_LIMIT};`;
}

function text(value: string | null | undefined): string {
  return (value ?? '').trim();
}

// A numeric filter contributes only when it is a whole-number literal; a blank or partial entry is
// dropped rather than emitted as a type-mismatched predicate.
function integer(value: string | number | null | undefined): number | null {
  if (value === null || value === undefined || value === '') return null;
  const parsed = Number(value);
  return Number.isInteger(parsed) ? parsed : null;
}

export interface JobsSqlFilters {
  namespace?: string;
  status?: string;
  jobName?: string;
  correlationKey?: string;
  tenantKey?: string;
}

export function jobsListSql(filters: JobsSqlFilters, dialect: SqlDialect = {}): string {
  const predicates: Predicate[] = [];
  if (text(filters.namespace)) predicates.push({ column: 'namespace', op: '=', value: text(filters.namespace) });
  // The view's status column is the decoded kebab code (lowercase); the filter carries the display casing.
  if (text(filters.status)) predicates.push({ column: 'status', op: '=', value: text(filters.status).toLowerCase() });
  if (text(filters.jobName)) predicates.push({ column: 'job_name', op: '=', value: text(filters.jobName) });
  if (text(filters.correlationKey))
    predicates.push({ column: 'correlation_key', op: '=', value: text(filters.correlationKey) });
  if (text(filters.tenantKey)) predicates.push({ column: 'tenant_key', op: '=', value: text(filters.tenantKey) });
  return select(dialect, 'jobs_view', predicates, 'created_at_utc DESC');
}

export interface EventsSqlFilters {
  namespace?: string;
  eventCode?: string;
  actorCode?: string;
  reasonCode?: string;
  workerId?: string | number;
  tenantId?: string | number;
  createdFromUtc?: string;
  createdToUtc?: string;
}

export function eventsListSql(filters: EventsSqlFilters, dialect: SqlDialect = {}): string {
  const predicates: Predicate[] = [];
  if (text(filters.namespace)) predicates.push({ column: 'namespace', op: '=', value: text(filters.namespace) });
  if (text(filters.eventCode)) predicates.push({ column: 'event', op: '=', value: text(filters.eventCode) });
  if (text(filters.actorCode)) predicates.push({ column: 'actor', op: '=', value: text(filters.actorCode) });
  if (text(filters.reasonCode)) predicates.push({ column: 'reason', op: '=', value: text(filters.reasonCode) });
  const worker = integer(filters.workerId);
  if (worker !== null) predicates.push({ column: 'worker_id', op: '=', value: worker });
  const tenant = integer(filters.tenantId);
  if (tenant !== null) predicates.push({ column: 'tenant_id', op: '=', value: tenant });
  if (text(filters.createdFromUtc))
    predicates.push({ column: 'created_at_utc', op: '>=', value: text(filters.createdFromUtc) });
  // Exclusive upper bound to match every provider's ListJobEvents.sql (`created_at_utc < to`); a `<=`
  // here would copy back rows the grid excluded.
  if (text(filters.createdToUtc))
    predicates.push({ column: 'created_at_utc', op: '<', value: text(filters.createdToUtc) });
  return select(dialect, 'events_view', predicates, 'created_at_utc DESC');
}

export interface SchedulesSqlFilters {
  namespace?: string;
  jobName?: string;
  liveOnly?: boolean;
}

export function schedulesListSql(filters: SchedulesSqlFilters, dialect: SqlDialect = {}): string {
  const predicates: Predicate[] = [];
  if (text(filters.namespace)) predicates.push({ column: 'namespace', op: '=', value: text(filters.namespace) });
  if (text(filters.jobName)) predicates.push({ column: 'job_name', op: '=', value: text(filters.jobName) });
  // "Live only" is the not-orphaned view the list shows by default; a schedule keeps its origin link.
  if (filters.liveOnly) predicates.push({ raw: 'orphaned_at_utc IS NULL' });
  return select(dialect, 'schedules_view', predicates, 'next_run_at_utc');
}
