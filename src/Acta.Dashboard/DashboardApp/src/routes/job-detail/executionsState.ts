import type { EventItem } from '../../api.ts';
import { eventFacts } from '../../lib/eventFacts.ts';
import type { JobDetail } from './types.ts';
import type { TimelineTone } from '../../components/jobTimelineState.ts';

// One row per execution whose outcome the ledger holds: the event that closed the attempt, which carries
// the worker and the handler's duration. The claim writes no event, so the execution in flight takes its
// row from the job snapshot instead. The ledger can hold fewer executions than the claim counter: at
// event recording Failures a success that answers no failure and every yield write nothing, Off writes
// nothing, and event retention runs on its own clock.

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
  workerRef: string | null;
  outcome: ExecutionOutcome;
  reason: string | null;
  message: string | null;
}

// The codes that close an attempt, by the outcome each records. A failure that runs again and the job's
// final failure both read as failed; a yield names its outcome in details.outcome.
const CLOSING_CODES: ReadonlyMap<string, ExecutionOutcome | 'yielded'> = new Map([
  ['job.execution-succeeded', 'succeeded'],
  ['job.execution-failed', 'failed'],
  ['job.execution-orphaned', 'orphaned'],
  ['job.execution-yielded', 'yielded'],
  ['job.failed', 'failed']
]);

const YIELD_OUTCOMES: ReadonlySet<string> = new Set(['rescheduled', 'suspended', 'paused', 'cancelled']);

function outcomeOf(eventCode: string, yielded: string | undefined): ExecutionOutcome {
  const recorded = CLOSING_CODES.get(eventCode);
  if (recorded !== 'yielded') return recorded ?? 'unknown';
  return yielded !== undefined && YIELD_OUTCOMES.has(yielded) ? (yielded as ExecutionOutcome) : 'unknown';
}

export function deriveExecutions(events: EventItem[]): ExecutionSummary[] {
  // Defensive against replays: the latest closing event of an execution wins, by durable event id.
  const closing = new Map<number, EventItem>();
  for (const event of events) {
    if (!CLOSING_CODES.has(event.eventCode) || event.executionNumber == null) continue;
    const seen = closing.get(event.executionNumber);
    if (!seen || event.eventId > seen.eventId) closing.set(event.executionNumber, event);
  }

  const summaries = [...closing].map(([executionNumber, end]): ExecutionSummary => {
    const facts = eventFacts(end.detailsText);
    return {
      executionNumber,
      // The start is the finish less the handler's duration; a reclaim or a cancel records no duration, so its start is unknown.
      startedAtUtc: end.durationMs != null ? new Date(Date.parse(end.createdAtUtc) - end.durationMs).toISOString() : null,
      finishedAtUtc: end.createdAtUtc,
      durationMs: end.durationMs,
      workerRef: end.workerRef,
      outcome: outcomeOf(end.eventCode, facts.outcome),
      reason: facts.reason ?? null,
      message: end.message,
    };
  });

  return summaries.sort((a, b) => b.executionNumber - a.executionNumber);
}

// An executing job's current attempt, newest-first ahead of the finished ones: its number is the claim
// counter and its worker the lease holder. It has no start: the claim stamps modified_at_utc, but a
// reprioritize or a consumed sleep timer moves it while the attempt runs. A closing event at or past
// the counter means the snapshot trails the event feed, and that event's row stands alone.
export function withRunningExecution(
  executions: ExecutionSummary[],
  job: Pick<JobDetail, 'status' | 'executionNumber' | 'leasedByWorkerRef'>
): ExecutionSummary[] {
  if (job.status !== 'executing' || (executions[0]?.executionNumber ?? 0) >= job.executionNumber) return executions;
  const running: ExecutionSummary = {
    executionNumber: job.executionNumber,
    startedAtUtc: null,
    finishedAtUtc: null,
    durationMs: null,
    workerRef: job.leasedByWorkerRef,
    outcome: 'executing',
    reason: null,
    message: null,
  };
  return [running, ...executions];
}

export interface ExecutionGap {
  shown: number;
  total: number;
  missing: number;
}

// snapshot.executionNumber (the jobs_state claim counter) is the only complete count of executions;
// events can be thinner (event recording) or already purged (event retention runs on its own clock).
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
