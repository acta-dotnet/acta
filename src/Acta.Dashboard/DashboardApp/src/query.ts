// Central TanStack Query policy: one client, one key convention, one options factory for keyset
// list pages. Imports only @tanstack/query-core so node --test can load it without the Svelte
// compiler; components get createQuery/createInfiniteQuery from @tanstack/svelte-query.
import { QueryClient, keepPreviousData } from '@tanstack/query-core';
import { api, ApiError, fetchCapabilities, type Capabilities, type NamespaceListItem, type Paged } from './api.ts';
import { readAllPages } from './lib/pagedWalk.ts';
import { isSysNamespace } from './routes/namespaceAdmin.ts';

// One refresh cadence for every polled list. TanStack's default refetchIntervalInBackground=false
// skips ticks while the tab is unfocused.
export const LIST_REFETCH_MS = 10_000;

// retry 1: the API maps backend faults to 503 ProblemDetails, so a single retry rides out a
// transient hiccup without hammering a database that is actually down. A 401 is never retried: the
// session has ended, and only a reload signs in again.
export function retryOnce(failureCount: number, error: unknown): boolean {
  return failureCount < 1 && !(error instanceof ApiError && error.status === 401);
}

export function createAppQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: { queries: { staleTime: 5_000, retry: retryOnce } }
  });
}

// Key conventions - lists: [entity, filters]; feeds: [entity, 'feed', filters]; details:
// [entity, 'detail', ref]. Blank filter values are dropped so '' and undefined share a cache entry.
export const keys = {
  list: (entity: string, filters: Record<string, unknown>) => [entity, compact(filters)] as const,
  feed: (entity: string, filters: Record<string, unknown>) => [entity, 'feed', compact(filters)] as const,
  detail: (entity: string, ref: string | number) => [entity, 'detail', String(ref)] as const,
  capabilities: ['capabilities'] as const
};

// App-wide capabilities gate: whether controls are enabled on this host, the permissions this caller
// holds, the confirmation header name, and provider/version info. One cache entry every control
// surface shares; the caller's grants can change while the page is open, so it is read again on focus
// and, through App.svelte, after a 403.
export function capabilitiesQuery() {
  return {
    queryKey: keys.capabilities,
    queryFn: ({ signal }: { signal: AbortSignal }) => fetchCapabilities({ signal }),
    staleTime: 60_000,
    refetchOnWindowFocus: 'always' as const
  };
}

// The namespaces a scope can name, read whole: every one but the reserved sys namespace, which holds no jobs.
export function namespaceNamesQuery() {
  return {
    queryKey: keys.list('namespace-names', {}),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      const all = await readAllPages((cursor) => api<Paged<NamespaceListItem>>('namespaces', { pageSize: 100, cursor }, { signal }));
      return all.filter((item) => !isSysNamespace(item.jobNamespace)).map((item) => item.jobNamespace);
    },
    staleTime: 60_000
  };
}

// The namespace a name lookup falls back on when no scope is picked: the only one there is, or none.
export function onlyNamespace(names: string[] | undefined): string {
  return names?.length === 1 ? names[0] : '';
}

export const Permission = {
  read: 'acta.read',
  enqueue: 'acta.enqueue',
  control: 'acta.control',
  admin: 'acta.admin'
} as const;
export type PermissionName = (typeof Permission)[keyof typeof Permission];

// A control renders when the host maps the controls and this caller holds the permission its route
// needs. Fails closed (false) until capabilities has loaded, so no control renders before the answer.
export function can(capabilities: Capabilities | undefined, permission: PermissionName): boolean {
  return capabilities?.controlsEnabled === true && (capabilities.permissions ?? []).includes(permission);
}

// The option that maps the controls, named wherever the dashboard says they are off on this host.
export const ENABLE_CONTROLS_HINT = 'Set EnableControls = true in the MapActa options to enable controls.';

// Whether any control this dashboard offers is open to this caller: the read-only chip shows when none is.
export function canWriteAny(capabilities: Capabilities | undefined): boolean {
  return [Permission.enqueue, Permission.control, Permission.admin].some((p) => can(capabilities, p));
}

function compact(filters: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(filters)) {
    if (value !== undefined && value !== null && value !== '') out[key] = value;
  }
  return out;
}

// Options for one keyset page. Shared by the grid and by pages reading the same first page (the
// job timeline), so identical parameters resolve to a single cache entry and a single fetch.
// keepPreviousData holds the old rows visible while a filter or page change fetches, so paging
// never flashes the loading state.
export function listPage<T>(
  entity: string,
  filters: Record<string, unknown>,
  page: { pageSize: number; cursor: string | null; includeTotal?: boolean }
) {
  const params = {
    ...filters,
    pageSize: page.pageSize,
    cursor: page.cursor,
    ...(page.includeTotal ? { includeTotal: true } : {})
  };
  return {
    queryKey: keys.list(entity, params),
    queryFn: ({ signal }: { signal: AbortSignal }) => api<Paged<T>>(entity, params, { signal }),
    placeholderData: keepPreviousData,
    refetchInterval: LIST_REFETCH_MS
  };
}
