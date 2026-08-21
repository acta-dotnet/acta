// Pure control-mutation building blocks: request-body assembly and success-invalidation. Imports
// only @tanstack/query-core (no @tanstack/svelte-query, which pulls in Svelte-compiled .svelte
// files) so node --test can load this directly - same reason query.ts stays query-core-only.
// useControlMutation.ts wraps these with createMutation for components.
import type { QueryClient } from '@tanstack/query-core';

export interface ControlMutationOptions<TVars extends object, TResult extends { action: string }> {
  /** Builds the request path (relative to `api/`), e.g. `(vars) => 'jobs/' + vars.jobRef + '/pause'`. */
  path: (vars: TVars) => string;
  method?: 'POST' | 'PATCH';
  /** Builds the request body; `reasonMessage` is merged in automatically from `vars.reason`. */
  body?: (vars: TVars) => Record<string, unknown>;
  /** Escape hatch for endpoints where the request body IS the caller's own JSON, verbatim - not a
   *  reason-merged object (e.g. signal-raise, where a request with zero body bytes has its own
   *  meaning: a presence-only signal). When set, this replaces `body`/the reason merge entirely;
   *  return undefined to send no request body at all. */
  rawBody?: (vars: TVars) => unknown;
  /** Result to return when the server responds 404 with no typed body. Every control family types
   *  its 404, so this is the guard for a proxy or a gateway answering in the server's place. */
  notFound: (vars: TVars) => TResult;
  /** Result to return when the server responds 409 with no typed body. Same guard as `notFound`,
   *  for the one outcome only the version-guarded admin patches can reach. */
  versionConflict?: (vars: TVars) => TResult;
  /** Query keys to invalidate once the request resolves without throwing (applied, rejected, and
   *  not-found all count - only a genuine fetch/parse failure skips invalidation). */
  invalidateKeys: (vars: TVars, result: TResult) => readonly (readonly unknown[])[];
}

// Merges the caller's reason into the request body.
export function buildBody<TVars extends object>(
  vars: TVars,
  body: ((vars: TVars) => Record<string, unknown>) | undefined
): Record<string, unknown> {
  const reason = 'reason' in vars && typeof vars.reason === 'string' ? vars.reason : undefined;
  return { ...(body?.(vars) ?? {}), reasonMessage: reason?.trim() || null };
}

// Invalidates every caller-named key.
export function invalidateAll(queryClient: QueryClient, keys: readonly (readonly unknown[])[]): void {
  for (const key of keys) queryClient.invalidateQueries({ queryKey: key as unknown[] });
}
