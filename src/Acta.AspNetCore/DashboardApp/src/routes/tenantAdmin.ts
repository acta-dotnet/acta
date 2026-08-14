// Pure helpers for the tenant admin controls (suspend/resume/details edit): CAS payload building
// and the reload-on-conflict decision, mirroring scheduleOverrides.ts. Split out so node --test can
// exercise them without the Svelte compiler.
import type { AdminControlAction } from '../api.ts';

export interface TenantDetailsInput {
  displayName: string; // '' clears display_name. Both fields prefill from the tenant list row
  // (display_name + description ship on TenantListItem), so a blank field is a deliberate clear by
  // the operator, not an always-clobber of an unseen value.
  description: string; // '' clears description
}

// Full-set PATCH semantics: a blank field clears that column, matching the backend's
// TenantPatchRequest contract (null DisplayName/Description clears).
export function buildTenantDetailsPayload(input: TenantDetailsInput): { displayName: string | null; description: string | null } {
  return {
    displayName: input.displayName.trim() || null,
    description: input.description.trim() || null
  };
}

// AdminControlAction has no message field and a different action set to
// ControlAction: 'applied' and 'alreadyInState' both mean the row now matches the caller's
// request (alreadyInState is an idempotent no-op, still a success); 'notFound' and
// 'versionConflict' both mean the caller's local copy is stale - never silently resend the same
// expectedVersion, only warn and reload. Shared by suspend/resume and the details editor.
export function tenantAdminNeedsReload(action: AdminControlAction): boolean {
  return action === 'notFound' || action === 'versionConflict';
}
