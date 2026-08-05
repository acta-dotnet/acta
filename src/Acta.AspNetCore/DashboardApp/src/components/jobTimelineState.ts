export type TimelineCategory = 'all' | 'failure' | 'control' | 'signal' | 'schedule';

interface TimelineEvent {
  eventCode?: string | null;
  executionNumber?: number | null;
  executionStatus?: string | null;
  toStatus?: string | null;
}

export function matchesTimelineCategory(evt: TimelineEvent, category: TimelineCategory): boolean {
  const code = String(evt.eventCode ?? '').toLowerCase();
  if (category === 'all') return true;
  if (category === 'failure') {
    return (
      String(evt.executionStatus).toLowerCase() === 'failed' ||
      String(evt.toStatus).toLowerCase() === 'failed' ||
      code === 'job.cancelled'
    );
  }
  if (category === 'control') {
    return /job\.(paused|resumed|restarted|cancelled|rescheduled|reprioritized|purged)/.test(code);
  }
  if (category === 'signal') return code.startsWith('job.signal-');
  if (category === 'schedule') return code.startsWith('schedule.') || code === 'job.recurring-rolled-over';
  return true;
}

export type TimelineTone = 'ok' | 'warn' | 'bad' | 'held' | 'run' | 'neutral';

export interface TimelinePresentation {
  icon: string;
  tone: TimelineTone;
  title: string;
}

/** Rail node presentation for an event: icon, tone, and a plain-verb title. The exact eventCode stays visible as a sub-line. */
export function timelinePresentation(evt: TimelineEvent): TimelinePresentation {
  const code = String(evt.eventCode ?? '').toLowerCase();
  const outcome = String(evt.executionStatus ?? evt.toStatus ?? '').toLowerCase();
  switch (code) {
    case 'job.execution-started':
      return { icon: 'lightning-bolt', tone: 'run', title: 'Execution started' };
    case 'job.execution-finished':
      // outcome carries the ExecutionStatusCode name: succeeded/failed/rescheduled/suspended/paused/cancelled/orphaned.
      if (outcome === 'succeeded') return { icon: 'check', tone: 'ok', title: 'Completed' };
      if (outcome === 'failed') return { icon: 'x', tone: 'bad', title: 'Execution failed' };
      if (outcome === 'rescheduled') return { icon: 'counter-clockwise-clock', tone: 'warn', title: 'Attempt rescheduled' };
      if (outcome === 'suspended') return { icon: 'clock', tone: 'held', title: 'Sleeping' };
      if (outcome === 'paused') return { icon: 'pause', tone: 'warn', title: 'Paused' };
      if (outcome === 'cancelled') return { icon: 'x', tone: 'bad', title: 'Cancelled' };
      if (outcome === 'orphaned') return { icon: 'warn', tone: 'warn', title: 'Orphaned' };
      return { icon: 'minus-circle', tone: 'neutral', title: 'Execution finished' };
    case 'job.rescheduled':
      return { icon: 'counter-clockwise-clock', tone: 'warn', title: 'Rescheduled' };
    case 'job.recurring-rolled-over':
      return { icon: 'calendar', tone: 'held', title: 'Recurrence advanced' };
    case 'job.suspended':
      return { icon: 'clock', tone: 'held', title: 'Sleeping' };
    case 'job.cancelled':
      return { icon: 'x', tone: 'bad', title: 'Cancelled' };
    case 'job.paused':
      return { icon: 'pause', tone: 'warn', title: 'Paused' };
    case 'job.resumed':
      return { icon: 'play', tone: 'ok', title: 'Resumed' };
    case 'job.restarted':
      return { icon: 'reload', tone: 'run', title: 'Restarted' };
    case 'job.reprioritized':
      return { icon: 'person', tone: 'held', title: 'Priority changed' };
    case 'job.purged':
      return { icon: 'x-circle', tone: 'bad', title: 'Purged' };
    case 'job.input-amended':
      return { icon: 'person', tone: 'held', title: 'Input amended' };
    case 'job.state-reset':
      return { icon: 'counter-clockwise-clock', tone: 'warn', title: 'State reset' };
    case 'job.signal-raised':
      return { icon: 'target', tone: 'run', title: 'Signal raised' };
  }
  if (code.startsWith('schedule.')) {
    return { icon: 'calendar', tone: 'held', title: humanize(code) };
  }
  return { icon: 'minus-circle', tone: 'neutral', title: humanize(code) };
}

/** A job status folded onto the shared tone classes, for the hero and lineage dots. */
export function statusTonePresentation(statusClass: string): { tone: TimelineTone; icon: string } {
  switch (statusClass) {
    case 'ok':
      return { tone: 'ok', icon: 'check' };
    case 'bad':
      return { tone: 'bad', icon: 'x' };
    case 'run':
      return { tone: 'run', icon: 'lightning-bolt' };
    case 'warn':
      return { tone: 'warn', icon: 'pause' };
    default:
      return { tone: 'neutral', icon: 'clock' };
  }
}

function humanize(code: string): string {
  const stem = code.split('.').pop() ?? code;
  const words = stem.replace(/-/g, ' ');
  return (code.startsWith('schedule.') ? 'Schedule ' + words : words.charAt(0).toUpperCase() + words.slice(1)).trim();
}

export function timelineAttemptNumbers(events: TimelineEvent[]): number[] {
  return [...new Set(events.map((evt) => evt.executionNumber ?? 0))].sort((a, b) => b - a);
}

export function failedTimelineAttempts(events: TimelineEvent[]): number[] {
  return timelineAttemptNumbers(events).filter((attempt) =>
    events.some(
      (evt) =>
        (evt.executionNumber ?? 0) === attempt &&
        (String(evt.executionStatus).toLowerCase() === 'failed' || String(evt.toStatus).toLowerCase() === 'failed')
    )
  );
}
