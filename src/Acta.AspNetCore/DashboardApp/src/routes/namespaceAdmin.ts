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

// The reserved system namespace name. Mirrors IdentifierSyntax.ReservedSystemName, which the
// dashboard cannot import; it is a frozen wire constant, so one named copy is the whole coupling.
export const RESERVED_SYSTEM_NAMESPACE = 'sys';

// The seeded sys namespace is protected by the backend (control verbs 400 on it); the list page
// mirrors that guardrail by rendering no write controls for this row at all. Recognized by name: the
// row's DB id is an engine internal and no longer reaches the wire. The reservation is the bare name
// OR the `sys.` prefix, matching IdentifierSyntax.IsReservedSystemName - a kebab namespace name
// cannot contain a dot today, so the prefix arm is defense against the rule widening, not a live case.
export function isSysNamespace(jobNamespace: string | null | undefined): boolean {
  return jobNamespace === RESERVED_SYSTEM_NAMESPACE || (jobNamespace?.startsWith(RESERVED_SYSTEM_NAMESPACE + '.') ?? false);
}

// Same AdminControlAction semantics as tenantAdmin.ts's tenantAdminNeedsReload:
// 'applied'/'alreadyInState' are successes, while 'notFound'/'versionConflict' mean the caller's
// local copy is stale - never silently resend the same expectedVersion, only warn and reload.
export function namespaceAdminNeedsReload(action: AdminControlAction): boolean {
  return action === 'notFound' || action === 'versionConflict';
}
