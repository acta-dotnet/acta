// Pure helpers for the schedule overrides editor: payload building and the reload-on-conflict
// decision. Split out so node --test can exercise them without the Svelte compiler - same reason
// jobControlState.ts / scheduleControlState.ts exist.
export interface ScheduleOverridesInput {
  expression: string; // '' clears the override
  timeZoneId: string; // '' clears the override
}

// Full-set PATCH semantics: a blank field clears that override (sent as null), matching the
// backend's SetScheduleOverridesRequest contract (null Expression/TimeZoneId clears).
export function buildOverridesPayload(input: ScheduleOverridesInput): { expression: string | null; timeZoneId: string | null } {
  return {
    expression: input.expression.trim() || null,
    timeZoneId: input.timeZoneId.trim() || null
  };
}

// Only 'applied' means the row is now in the state the caller expects. 'rejected' - which covers
// both a stale expectedVersion (CAS conflict) and any other state that no longer accepts the
// override, since ScheduleControlResponse carries no separate VersionConflict tag - and 'notFound'
// both mean the caller's local copy is stale: the caller must reload rather than silently resend
// the same expectedVersion (that would just conflict again, or worse, race a different change).
export function overridesNeedsReload(action: string): boolean {
  return action !== 'applied';
}
