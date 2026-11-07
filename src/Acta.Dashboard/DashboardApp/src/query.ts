// Central TanStack Query policy: one client, one key convention, one options factory for keyset
// list pages. Imports only @tanstack/query-core so node --test can load it without the Svelte
// compiler; components get createQuery/createInfiniteQuery from @tanstack/svelte-query.
import { QueryClient, keepPreviousData } from '@tanstack/query-core';
import { api, fetchCapabilities, type Capabilities, type Paged } from './api.ts';

// One refresh cadence for every polled list. TanStack's default refetchIntervalInBackground=false
// skips ticks while the tab is unfocused.
export const LIST_REFETCH_MS = 10_000;

// retry 1: the API maps backend faults to 503 ProblemDetails, so a single retry rides out a
// transient hiccup without hammering a database that is actually down.
export function createAppQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: { queries: { staleTime: 5_000, retry: 1 } }
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

// App-wide capabilities gate: whether controls are enabled on this host, the confirmation header
// name, and provider/version info. staleTime: Infinity - fixed for the process lifetime of the
// dashboard host, so this fetches once (via createQuery in App.svelte) and is reused (same cache
// key) by every later feature's control UI instead of each one re-fetching it.
export function capabilitiesQuery() {
  return {
    queryKey: keys.capabilities,
    queryFn: ({ signal }: { signal: AbortSignal }) => fetchCapabilities({ signal }),
    staleTime: Infinity
  };
}

// Read-only gate. Fails closed (false) while capabilities hasn't loaded yet or the fetch is still
// pending, so control UI never renders as enabled before the host's real answer is known.
export function canControl(capabilities: Capabilities | undefined): boolean {
  return capabilities?.controlsEnabled === true;
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
