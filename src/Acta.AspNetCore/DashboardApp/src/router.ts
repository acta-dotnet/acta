import { readable } from 'svelte/store';
import type { RouteName } from './routes.ts';

export interface Route {
  name: RouteName;
  jobRef?: string;
  defId?: string;
  workerId?: number;
  namespaceName?: string;
  tenantKey?: string;
  scheduleNamespace?: string;
  scheduleJobName?: string;
  scheduleName?: string;
}

function splitHash(hash: string): { path: string; query: string } {
  const value = hash.replace(/^#\/?/, '');
  const queryIndex = value.indexOf('?');
  return queryIndex < 0
    ? { path: value, query: '' }
    : { path: value.slice(0, queryIndex), query: value.slice(queryIndex + 1) };
}

function decoded(parts: string[], index: number): string | undefined {
  const value = parts[index];
  if (!value) return undefined;
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

export function parseRouteHash(hash: string): Route {
  const parts = splitHash(hash).path.split('/').filter(Boolean);
  if (parts.length === 0) return { name: 'overview' };

  switch (parts[0]) {
    case 'jobs':
      if (parts.length === 1) return { name: 'jobs' };
      return parts.length === 2 ? { name: 'job-detail', jobRef: decoded(parts, 1) } : { name: 'not-found' };
    case 'enqueue':
      return parts.length === 1 ? { name: 'enqueue' } : { name: 'not-found' };
    case 'events':
      return parts.length === 1 ? { name: 'events' } : { name: 'not-found' };
    case 'definitions':
      if (parts.length === 1) return { name: 'definitions' };
      return parts.length === 2 ? { name: 'definition-detail', defId: decoded(parts, 1) } : { name: 'not-found' };
    case 'schedules':
      if (parts.length === 1) return { name: 'schedules' };
      return parts.length === 4
        ? { name: 'schedule-detail', scheduleNamespace: decoded(parts, 1), scheduleJobName: decoded(parts, 2), scheduleName: decoded(parts, 3) }
        : { name: 'not-found' };
    case 'workers': {
      if (parts.length === 1) return { name: 'workers' };
      const workerId = Number(decoded(parts, 1));
      return parts.length === 2 && Number.isInteger(workerId) && workerId > 0
        ? { name: 'worker-detail', workerId }
        : { name: 'not-found' };
    }
    case 'alerts':
      return parts.length === 1 ? { name: 'alerts' } : { name: 'not-found' };
    case 'namespaces':
      if (parts.length === 1) return { name: 'namespaces' };
      return parts.length === 2 ? { name: 'namespace-detail', namespaceName: decoded(parts, 1) } : { name: 'not-found' };
    case 'tenants':
      if (parts.length === 1) return { name: 'tenants' };
      if (parts.length !== 2) return { name: 'not-found' };
      return parts[1] === 'new' ? { name: 'tenant-new' } : { name: 'tenant-detail', tenantKey: decoded(parts, 1) };
    default:
      return { name: 'not-found' };
  }
}

const currentRoute = () => parseRouteHash(typeof location === 'undefined' ? '#/' : location.hash);

export const route = readable<Route>(currentRoute(), (set) => {
  const onChange = () => set(currentRoute());
  addEventListener('hashchange', onChange);
  return () => removeEventListener('hashchange', onChange);
});

export function hashParams(): URLSearchParams {
  return new URLSearchParams(splitHash(location.hash).query);
}

export function updateHashParams(patch: Record<string, string | null>, mode: 'replace' | 'push' = 'replace'): void {
  const { path } = splitHash(location.hash);
  const params = hashParams();
  for (const [key, value] of Object.entries(patch)) {
    if (value === null || value === '') params.delete(key);
    else params.set(key, value);
  }
  const query = params.toString();
  const next = '#/' + path + (query ? '?' + query : '');
  if (mode === 'push') location.hash = next.slice(1);
  else history.replaceState(null, '', next);
}
