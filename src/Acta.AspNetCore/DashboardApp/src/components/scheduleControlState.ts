// Pure derivation of which ScheduleControls buttons apply to a given status. Split out so
// node --test can exercise it directly - same reason jobControlState.ts exists for JobControls.
// The server stays authoritative on legality; this only hides obviously inapplicable buttons.
export interface ScheduleControlState {
  paused: boolean;
  canTrigger: boolean;
}

export function scheduleControlState(status: string): ScheduleControlState {
  return {
    paused: status === 'paused',
    // Orphaned means the origin declaration is gone, so trigger is meaningless; every other status
    // (including paused - trigger-now is an operator override of the schedule's own cadence) stays
    // offered. The real legality (e.g. a terminal owning-job slot) is enforced server-side and comes
    // back as a Rejected result, rendered as a warning rather than hidden here.
    canTrigger: status !== 'orphaned'
  };
}
