// Pure alert state-filter logic split out from AlertsList.svelte so node --test can exercise it
// directly (no Svelte compiler needed) - same reason jobControlState.ts and signalDrawerState.ts
// stay plain .ts.
export type AlertStateBucket = 'unacknowledged' | 'acknowledged' | 'resolved';

export interface AlertStateRow {
  acknowledgedAtUtc: string | null;
  resolvedAtUtc: string | null;
}

// Exhaustive partition: every alert lands in exactly one bucket. Resolved wins over acknowledged -
// once resolved, an alert is never shown as still "open" regardless of whether it was acknowledged
// on the way there.
export function alertStateBucket(row: AlertStateRow): AlertStateBucket {
  if (row.resolvedAtUtc) return 'resolved';
  return row.acknowledgedAtUtc ? 'acknowledged' : 'unacknowledged';
}

// Predicate for ActaGrid's rowFilter: keeps only rows in the selected bucket.
export function alertStateMatches(bucket: AlertStateBucket, row: AlertStateRow): boolean {
  return alertStateBucket(row) === bucket;
}

// Server-side query params for the selected bucket (ListAlertsQuery.UnresolvedOnly/Acknowledged).
// unacknowledged/acknowledged narrow the fetch server-side; resolved has no "resolved only" server
// param (UnresolvedOnly only ever restricts to unresolved, never to resolved), so it fetches
// unfiltered and relies on alertStateMatches (via ActaGrid's rowFilter) as the client-side backstop.
export function alertStateQuery(bucket: AlertStateBucket): { unresolvedOnly: boolean | ''; acknowledgedOnly: boolean | '' } {
  if (bucket === 'resolved') return { unresolvedOnly: '', acknowledgedOnly: '' };
  return { unresolvedOnly: true, acknowledgedOnly: bucket === 'acknowledged' };
}
