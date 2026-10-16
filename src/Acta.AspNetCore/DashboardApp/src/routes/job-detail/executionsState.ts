import type { JobEvent } from './types.ts';
import type { TimelineTone } from '../../components/jobTimelineState.ts';

// One row per execution whose outcome the ledger holds: its job.execution-finished event, which carries
// the worker and the handler's duration. The claim writes no event, so an in-flight execution has no row
// until it finishes; the job snapshot shows it meanwhile. The ledger can hold fewer executions than the
// claim counter: at audit level Failures a success that answers no failure writes nothing, Off writes
// nothing, and event retention runs on its own clock.

export type ExecutionOutcome =
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
  finishedAtUtc: string;
  durationMs: number | null;
  workerRef: string | null;
  outcome: ExecutionOutcome;
  reasonCode: string | null;
  reasonMessage: string | null;
}

const OUTCOMES: ReadonlySet<string> = new Set([
  'succeeded', 'failed', 'rescheduled', 'suspended', 'paused', 'cancelled', 'orphaned'
]);

export function deriveExecutions(events: JobEvent[]): ExecutionSummary[] {
  // Defensive against replays: the latest finished event of an execution wins, by durable event id.
  const finished = new Map<number, JobEvent>();
  for (const event of events) {
    if (event.eventCode !== 'job.execution-finished' || event.executionNumber == null) continue;
    const seen = finished.get(event.executionNumber);
    if (!seen || event.jobEventId > seen.jobEventId) finished.set(event.executionNumber, event);
  }

  const summaries = [...finished].map(([executionNumber, end]): ExecutionSummary => {
    const rawOutcome = String(end.executionStatus ?? '').toLowerCase();
    return {
      executionNumber,
      // The start is the finish less the handler's duration; a reclaim or a cancel records no duration, so its start is unknown.
      startedAtUtc: end.durationMs != null ? new Date(Date.parse(end.createdAtUtc) - end.durationMs).toISOString() : null,
      finishedAtUtc: end.createdAtUtc,
      durationMs: end.durationMs,
      workerRef: end.workerRef,
      outcome: OUTCOMES.has(rawOutcome) ? (rawOutcome as ExecutionOutcome) : 'unknown',
      reasonCode: end.reasonCode,
      reasonMessage: end.reasonMessage,
    };
  });

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
    case 'rescheduled': return { tone: 'warn', icon: 'counter-clockwise-clock', label: 'Rescheduled' };
    case 'suspended': return { tone: 'held', icon: 'clock', label: 'Sleeping' };
    case 'paused': return { tone: 'warn', icon: 'pause', label: 'Paused' };
    case 'cancelled': return { tone: 'bad', icon: 'x', label: 'Cancelled' };
    case 'orphaned': return { tone: 'warn', icon: 'warn', label: 'Orphaned' };
    default: return { tone: 'neutral', icon: 'minus-circle', label: 'Unknown' };
  }
}
