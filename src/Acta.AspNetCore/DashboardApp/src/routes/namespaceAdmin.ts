// Pure helpers for the namespace admin controls (suspend/resume/details edit): CAS payload
// building, the sys-namespace guardrail, and the reload-on-conflict decision - mirroring
// tenantAdmin.ts. Split out so node --test can exercise them without the Svelte compiler.
import type { AdminControlAction } from '../api.ts';

export interface NamespaceDetailsInput {
  ownerTeam: string; // '' clears owner_team. Both fields prefill from the namespace list row, so a
  // blank field is a deliberate clear by the operator, not an always-clobber of an unseen value.
  description: string; // '' clears description
}

// Full-set PATCH semantics: a blank field clears that column, matching the backend's
// NamespacePatchRequest contract (null OwnerTeam/Description clears).
export function buildNamespaceDetailsPayload(input: NamespaceDetailsInput): { ownerTeam: string | null; description: string | null } {
  return {
    ownerTeam: input.ownerTeam.trim() || null,
    description: input.description.trim() || null
  };
}

// The seeded sys namespace (Id 1) is protected by the backend (control verbs 400 on it); the list
// page mirrors that guardrail by rendering no write controls for this row at all.
export function isSysNamespace(id: number): boolean {
  return id === 1;
}

// Same AdminControlAction semantics as tenantAdmin.ts's tenantAdminNeedsReload:
// 'applied'/'alreadyInState' are successes, while 'notFound'/'versionConflict' mean the caller's
// local copy is stale - never silently resend the same expectedVersion, only warn and reload.
export function namespaceAdminNeedsReload(action: AdminControlAction): boolean {
  return action === 'notFound' || action === 'versionConflict';
}
