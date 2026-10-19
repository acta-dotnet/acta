// Operator-facing text for a control verb's outcome. The API answers with the outcome alone (action,
// status, version); the dashboard words it here, one function per control family.

const title = (verb: string) => verb.charAt(0).toUpperCase() + verb.slice(1);

// Job verbs at jobs/{jobRef}/{verb}: pause, resume, restart, cancel, reschedule, reprioritize, purge,
// signal. The dashboard sends no expectedVersion, so a job verb never answers versionConflict.
export function jobControlMessage(verb: string, action: string): string {
  switch (action) {
    case 'applied':
      return `${title(verb)} applied.`;
    case 'rejected':
      if (verb === 'restart') return "Restart rejected: the job's status or its lane does not allow it.";
      return `${title(verb)} rejected: the job's current status does not allow it.`;
    default:
      return 'Job not found.';
  }
}

// Schedule verbs at schedules/{ns}/{job}/{schedule}/{verb}: pause, resume, trigger, overrides. Only the
// overrides save is version-guarded, so only it answers versionConflict.
export function scheduleControlMessage(verb: string, action: string): string {
  switch (action) {
    case 'applied':
      return `Schedule ${verb} applied.`;
    case 'rejected':
      return `Schedule ${verb} rejected: the schedule's current state does not allow it.`;
    case 'versionConflict':
      return `Schedule ${verb} rejected: the schedule changed since you loaded it (version conflict).`;
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
      return 'No outbox relay slot exists for that namespace.';
  }
}

// Definition verbs: the overrides PATCH and retire. Both are version-guarded, so a version conflict means
// the definition changed since it was loaded.
export function definitionControlMessage(verb: 'overrides' | 'retire', action: string): string {
  switch (action) {
    case 'applied':
      return verb === 'retire' ? 'Definition retired.' : 'Definition overrides applied.';
    case 'versionConflict':
      return verb === 'retire'
        ? 'Retire rejected: the definition changed since you loaded it (version conflict). Reload and retry.'
        : 'Definition override rejected: the definition changed since you loaded it (version conflict). Reload and retry.';
    default:
      return 'Definition not found.';
  }
}
