// Operator-facing text for a control verb's outcome. The API answers with the outcome alone (action,
// status, version); the dashboard words it here, one function per control family.

const title = (verb: string) => verb.charAt(0).toUpperCase() + verb.slice(1);

// Job verbs at jobs/{jobRef}/{verb}: pause, resume, restart, cancel, reschedule, reprioritize, purge,
// signal. The dashboard sends no expectedVersion, so a job verb never answers version-conflict.
export function jobControlMessage(verb: string, action: string): string {
  switch (action) {
    case 'applied':
      return `${title(verb)} applied.`;
    case 'rejected':
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
