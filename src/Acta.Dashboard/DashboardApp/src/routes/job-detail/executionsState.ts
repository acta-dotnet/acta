import type { EventItem, JobStepItem } from '../../api.ts';
import { eventFacts } from '../../lib/eventFacts.ts';
import type { JobDetail } from './types.ts';
import type { ExecutionStep, TimelineTone } from '../../components/jobTimelineState.ts';

// One row per execution whose outcome the ledger holds: the event that closed the attempt, which carries
// the worker and the handler's duration. The claim writes no event, so the execution in flight takes its
// row from the job snapshot instead. The ledger can hold fewer executions than the claim counter: at
// event recording Failures a success that answers no failure and every yield write nothing, and event
// retention runs on its own clock.

export type ExecutionOutcome =
  | 'executing'
  | 'succeeded'
  | 'failed'
  | 'rescheduled'
  | 'suspended'
  | 'sleeping'
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

// A yield's details.outcome, by the outcome the page shows for it.
const YIELD_OUTCOMES: ReadonlyMap<string, ExecutionOutcome> = new Map([
  ['rescheduled', 'rescheduled'],
  ['slept', 'sleeping'],
  ['suspended', 'suspended'],
  ['paused', 'paused'],
  ['cancelled', 'cancelled']
]);

function outcomeOf(eventCode: string, yielded: string | undefined): ExecutionOutcome {
  const recorded = CLOSING_CODES.get(eventCode);
  if (recorded !== 'yielded') return recorded ?? 'unknown';
  return (yielded !== undefined && YIELD_OUTCOMES.get(yielded)) || 'unknown';
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
    const outcome = outcomeOf(end.eventCode, facts.outcome);
    return {
      executionNumber,
      // The start is the finish less the handler's duration; a reclaim or a cancel records no duration, so its start is unknown.
      startedAtUtc: end.durationMs != null ? new Date(Date.parse(end.createdAtUtc) - end.durationMs).toISOString() : null,
      finishedAtUtc: end.createdAtUtc,
      durationMs: end.durationMs,
      workerRef: end.workerRef,
      outcome,
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

// Places each step in the shown executions, by execution number. An execution holds the instants after the previous
// one's end up to its own end, inclusive; the running one has no end. Step writes are fenced to the executing attempt,
// so a shown previous execution's end is the exact bound, and execution 1, with none before it, has no lower bound.
// Past a gap the derived start bounds it instead, which runs late: the closing event is written after the handler
// returns. With neither bound it takes everything up to its end.
// A succeeded step goes to the execution holding its last write, and is started in the one holding its first start
// when that is another; a step that has not succeeded is started there. A step no shown execution holds is left off.
// Each line runs by first start, then name.
export function placeSteps(
  executions: ExecutionSummary[],
  steps: Pick<JobStepItem, 'name' | 'status' | 'createdAtUtc' | 'modifiedAtUtc'>[]
): Map<number, ExecutionStep[]> {
  const ordered = [...executions].sort((a, b) => a.executionNumber - b.executionNumber);
  const spans = ordered.map((execution, index) => {
    const previous = ordered[index - 1];
    const previousEnd = previous?.finishedAtUtc != null ? Date.parse(previous.finishedAtUtc) : null;
    const exact =
      execution.executionNumber === 1 || (previousEnd !== null && previous.executionNumber === execution.executionNumber - 1);
    const start = !exact && execution.startedAtUtc != null ? Date.parse(execution.startedAtUtc) : null;
    const end = execution.finishedAtUtc != null ? Date.parse(execution.finishedAtUtc) : Infinity;
    return {
      executionNumber: execution.executionNumber,
      holds: (at: number) => (start !== null ? at >= start : previousEnd === null || at > previousEnd) && at <= end
    };
  });
  const holder = (instant: string) => {
    const at = Date.parse(instant);
    return spans.find((span) => span.holds(at))?.executionNumber;
  };

  const lines = new Map<number, ExecutionStep[]>();
  const add = (executionNumber: number, step: ExecutionStep) => {
    const line = lines.get(executionNumber);
    if (line) line.push(step);
    else lines.set(executionNumber, [step]);
  };
  const byStart = [...steps].sort(
    (a, b) => Date.parse(a.createdAtUtc) - Date.parse(b.createdAtUtc) || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0)
  );
  for (const step of byStart) {
    const began = holder(step.createdAtUtc);
    const finished = step.status === 'succeeded' ? holder(step.modifiedAtUtc) : undefined;
    if (finished !== undefined) add(finished, { name: step.name, state: 'succeeded' });
    if (began !== undefined && began !== finished) add(began, { name: step.name, state: 'started' });
  }
  return lines;
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
    case 'suspended': return { tone: 'held', icon: 'clock', label: 'Waiting' };
    case 'sleeping': return { tone: 'held', icon: 'clock', label: 'Sleeping' };
    case 'paused': return { tone: 'warn', icon: 'pause', label: 'Paused' };
    case 'cancelled': return { tone: 'bad', icon: 'x', label: 'Cancelled' };
    case 'orphaned': return { tone: 'warn', icon: 'warn', label: 'Orphaned' };
    default: return { tone: 'neutral', icon: 'minus-circle', label: 'Unknown' };
  }
}
