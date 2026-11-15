// Operator-facing text for a control verb's outcome. The API answers with the outcome alone (action,
// status, version); the dashboard words it here, one function per control family.

const title = (verb: string) => verb.charAt(0).toUpperCase() + verb.slice(1);

// Job verbs at jobs/{jobRef}/{verb}: pause, resume, restart, cancel, reschedule, reprioritize, purge,
// signal, and clear-signal (DELETE jobs/{jobRef}/signals/{name}). The dashboard sends no expectedVersion, so a job
// verb never answers version-conflict. Only the two signal verbs answer already-in-state, and they name the reason
// for a refusal.
export function jobControlMessage(verb: string, action: string, reason?: string | null): string {
  switch (action) {
    case 'applied':
      return verb === 'clear-signal' ? 'Signal cleared.' : `${title(verb)} applied.`;
    case 'already-in-state':
      return verb === 'clear-signal' ? 'The job holds no signal by that name.' : 'The signal already holds this value.';
    case 'rejected':
      if (verb === 'clear-signal')
        return 'Clear rejected: the job has not finished. A signal clears only from a finished job or a permanent job between firings.';
      if (verb === 'signal' && reason === 'job-finished')
        return 'Signal rejected: the job has finished. Clear the signal if it holds a value, run the job again, then raise it.';
      if (verb === 'signal' && reason === 'wait-expired') return 'Signal rejected: the wait for it has expired.';
      if (verb === 'signal' && reason === 'value-differs')
        return 'Signal rejected: it already holds another value, and a signal keeps its first.';
      if (verb === 'restart')
        return 'Restart rejected: the job is executing, a firing is still outstanding, an unfinished ancestor or descendant sits in its lane, or its schedule is full, orphaned or retired.';
      if (verb === 'purge')
        return "Purge rejected: the job's status, its parent or child jobs, a live schedule or an outstanding firing keeps it, or sys.alerts has not processed its latest events yet.";
      if (verb === 'reschedule')
        return "Reschedule rejected: the job's status does not allow it, its definition is retired, or its namespace is suspended.";
      return `${title(verb)} rejected: the job's current status does not allow it.`;
    default:
      return 'Job not found.';
  }
}

// Schedule verbs at schedules/{ns}/{job}/{schedule}/{verb}: pause, resume, trigger. None is version-guarded,
// a resume is never rejected, and a pause is rejected only for its end.
export function scheduleControlMessage(verb: string, action: string): string {
  switch (action) {
    case 'applied':
      return `Schedule ${verb} applied.`;
    case 'rejected':
      if (verb === 'pause') return 'Schedule pause rejected: the time to pause until has already passed.';
      return 'Schedule trigger rejected: its namespace is suspended, the schedule is paused or at its capacity, or its job is not idle or has a firing outstanding.';
    default:
      return 'Schedule not found.';
  }
}

// Outbox verbs at outbox/{ns}/{verb}: requeue, discard. Accepted is the success: the command is parked
// and the next relay pass applies it.
export function outboxControlMessage(verb: string, action: string): string {
  switch (action) {
    case 'accepted':
      return `Outbox ${verb} accepted; the next relay pass applies it.`;
    case 'rejected':
      return `Outbox ${verb} rejected: a pending ${verb} command is already parked for this source.`;
    default:
      return 'No outbox relay job exists for that namespace.';
  }
}

// Definition verbs: the overrides PUT and retire. Both are version-guarded, so a version conflict means
// the definition changed since it was loaded.
export function definitionControlMessage(verb: 'overrides' | 'retire', action: string): string {
  switch (action) {
    case 'applied':
      return verb === 'retire' ? 'Definition retired.' : 'Definition overrides applied.';
    case 'version-conflict':
      return verb === 'retire'
        ? 'Retire rejected: the definition changed since you loaded it (version conflict). Reload and retry.'
        : 'Definition override rejected: the definition changed since you loaded it (version conflict). Reload and retry.';
    default:
      return 'Definition not found.';
  }
}
