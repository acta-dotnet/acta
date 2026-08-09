import type { JobEvent } from './types.ts';
import type { TimelineTone } from '../../components/jobTimelineState.ts';

// One row per execution, derived from the event ledger. Pairing is NOT guaranteed: at audit level
// Failures a successful execution leaves zero events, an orphan reclaim writes an end event with no
// worker and no duration, a claim-only crash has no start, and an in-flight execution has no end.
// Everything here is outer semantics over whatever events survive.

export type ExecutionOutcome =
  | 'executing'
  | 'succeeded'
  | 'failed'
  | 'rescheduled'
  | 'suspended'
  | 'paused'
  | 'cancelled'
  | 'orphaned'
  | 'unknown';

export interface ExecutionSummary {
  executionNumber: number;
  startedAtUtc: string | null;
  finishedAtUtc: string | null;
  durationMs: number | null;
  workerId: number | null;
  outcome: ExecutionOutcome;
  reasonCode: string | null;
  reasonMessage: string | null;
  eventCount: number;
  missingStart: boolean;
  missingEnd: boolean;
}

const OUTCOMES: ReadonlySet<string> = new Set([
  'executing', 'succeeded', 'failed', 'rescheduled', 'suspended', 'paused', 'cancelled', 'orphaned'
]);

export function deriveExecutions(events: JobEvent[]): ExecutionSummary[] {
  const groups = new Map<number, JobEvent[]>();
  for (const event of events) {
    if (event.executionNumber == null) continue; // lifecycle events (enqueue, controls) have no execution
    const group = groups.get(event.executionNumber);
    if (group) group.push(event);
    else groups.set(event.executionNumber, [event]);
  }

  const summaries: ExecutionSummary[] = [];
  for (const [executionNumber, group] of groups) {
    // Defensive against replays: earliest start, latest end by durable event id.
    let start: JobEvent | null = null;
    let end: JobEvent | null = null;
    for (const event of group) {
      if (event.eventCode === 'job.execution-started' && (!start || event.jobEventId < start.jobEventId)) start = event;
      if (event.eventCode === 'job.execution-finished' && (!end || event.jobEventId > end.jobEventId)) end = event;
    }

    const rawOutcome = String(end?.executionStatus ?? '').toLowerCase();
    const outcome: ExecutionOutcome = end
      ? (OUTCOMES.has(rawOutcome) ? (rawOutcome as ExecutionOutcome) : 'unknown')
      : start
        ? 'executing'
        : 'unknown';

    summaries.push({
      executionNumber,
      startedAtUtc: start?.createdAtUtc ?? null,
      finishedAtUtc: end?.createdAtUtc ?? null,
      durationMs: end?.durationMs ?? null,
      // Orphan reclaim writes the end event with a NULL worker; the start event still knows.
      workerId: end?.workerId ?? start?.workerId ?? null,
      outcome,
      reasonCode: end?.reasonCode ?? null,
      reasonMessage: end?.reasonMessage ?? null,
      eventCount: group.length,
      missingStart: !start,
      missingEnd: !end,
    });
  }

  return summaries.sort((a, b) => b.executionNumber - a.executionNumber);
}

export interface ExecutionGap {
  shown: number;
  total: number;
  missing: number;
}

// snapshot.executionNumber (the runtimes claim counter) is the only complete count of executions;
// events can be thinner (audit level) or already purged (event retention runs on its own clock).
// The event feed can also briefly run AHEAD of the snapshot poll, so the ledger's own highest
// execution number participates in the total.
export function executionGapSummary(snapshotExecutionNumber: number, derived: ExecutionSummary[]): ExecutionGap {
  const total = Math.max(snapshotExecutionNumber, derived[0]?.executionNumber ?? 0);
  return { shown: derived.length, total, missing: total - derived.length };
}

export interface ExecutionPresentation {
  tone: TimelineTone;
  icon: string;
  label: string;
}

export function executionPresentation(outcome: ExecutionOutcome): ExecutionPresentation {
  switch (outcome) {
    case 'succeeded': return { tone: 'ok', icon: 'check', label: 'Succeeded' };
    case 'failed': return { tone: 'bad', icon: 'x', label: 'Failed' };
    case 'executing': return { tone: 'run', icon: 'lightning-bolt', label: 'Executing' };
    case 'rescheduled': return { tone: 'warn', icon: 'counter-clockwise-clock', label: 'Rescheduled' };
    case 'suspended': return { tone: 'held', icon: 'clock', label: 'Sleeping' };
    case 'paused': return { tone: 'warn', icon: 'pause', label: 'Paused' };
    case 'cancelled': return { tone: 'bad', icon: 'x', label: 'Cancelled' };
    case 'orphaned': return { tone: 'warn', icon: 'warn', label: 'Orphaned' };
    default: return { tone: 'neutral', icon: 'minus-circle', label: 'Unknown' };
  }
}
