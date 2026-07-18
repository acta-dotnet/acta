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
  if (category === 'signal') return code.startsWith('job.signal.');
  if (category === 'schedule') return code.startsWith('schedule.') || code === 'job.recurring.rolled-over';
  return true;
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
