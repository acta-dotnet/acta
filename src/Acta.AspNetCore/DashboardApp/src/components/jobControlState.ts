// Pure derivation of which JobControls buttons apply to a given status. Split out from
// JobControls.svelte so node --test can exercise it directly (no Svelte compiler needed) - same
// reason query.ts and lib/controlMutation.ts stay plain .ts. The server stays authoritative on
// legality; this only hides obviously inapplicable buttons.
import { TERMINAL_STATUSES } from '../format.ts';

export interface JobControlState {
  terminal: boolean;
  canPause: boolean;
  canResume: boolean;
  canRestart: boolean;
  canCancel: boolean;
  canReschedule: boolean;
  canReprioritize: boolean;
  canPurge: boolean;
}

export function jobControlState(status: string): JobControlState {
  const terminal = TERMINAL_STATUSES.includes(status);
  return {
    terminal,
    canPause: !terminal && status !== 'paused',
    canResume: status === 'paused',
    canRestart: status !== 'executing',
    canCancel: !terminal,
    // RescheduleAsync: applies to Paused/Suspended/Ready; an in-flight or terminal row is rejected.
    canReschedule: !terminal && status !== 'executing',
    // ReprioritizeAsync: any non-terminal row (including in-flight) accepts a new priority.
    canReprioritize: !terminal,
    // PurgeAsync: only a terminal job (done/failed/cancelled) may be purged.
    canPurge: terminal
  };
}
