export type RouteName =
  | 'overview'
  | 'jobs'
  | 'job-detail'
  | 'events'
  | 'definitions'
  | 'definition-detail'
  | 'schedules'
  | 'schedule-detail'
  | 'workers'
  | 'worker-detail'
  | 'alerts'
  | 'namespaces'
  | 'namespace-detail'
  | 'tenants'
  | 'tenant-detail'
  | 'tenant-new'
  | 'not-found';

export type NavigationSection = 'Operate' | 'Configure' | 'Admin';

export interface RouteMetadata {
  name: RouteName;
  label: string;
  section: NavigationSection | null;
  detail: boolean;
  fullHeight: boolean;
  activeNav: RouteName | null;
  navPath?: string;
  navOrder?: number;
}

export const routeRegistry: RouteMetadata[] = [
  { name: 'overview', label: 'Overview', section: 'Operate', detail: false, fullHeight: false, activeNav: 'overview', navPath: '', navOrder: 0 },
  { name: 'jobs', label: 'Jobs', section: 'Operate', detail: false, fullHeight: true, activeNav: 'jobs', navPath: 'jobs', navOrder: 1 },
  { name: 'job-detail', label: 'Job', section: 'Operate', detail: true, fullHeight: false, activeNav: 'jobs' },
  { name: 'alerts', label: 'Alerts', section: 'Operate', detail: false, fullHeight: true, activeNav: 'alerts', navPath: 'alerts', navOrder: 2 },
  { name: 'workers', label: 'Workers', section: 'Operate', detail: false, fullHeight: true, activeNav: 'workers', navPath: 'workers', navOrder: 3 },
  { name: 'worker-detail', label: 'Worker', section: 'Operate', detail: true, fullHeight: false, activeNav: 'workers' },
  { name: 'events', label: 'Events', section: 'Operate', detail: false, fullHeight: true, activeNav: 'events', navPath: 'events', navOrder: 4 },
  { name: 'schedules', label: 'Schedules', section: 'Configure', detail: false, fullHeight: true, activeNav: 'schedules', navPath: 'schedules', navOrder: 0 },
  { name: 'schedule-detail', label: 'Schedule', section: 'Configure', detail: true, fullHeight: false, activeNav: 'schedules' },
  { name: 'definitions', label: 'Definitions', section: 'Configure', detail: false, fullHeight: true, activeNav: 'definitions', navPath: 'definitions', navOrder: 1 },
  { name: 'definition-detail', label: 'Definition', section: 'Configure', detail: true, fullHeight: false, activeNav: 'definitions' },
  { name: 'namespaces', label: 'Namespaces', section: 'Admin', detail: false, fullHeight: true, activeNav: 'namespaces', navPath: 'namespaces', navOrder: 0 },
  { name: 'namespace-detail', label: 'Namespace', section: 'Admin', detail: true, fullHeight: false, activeNav: 'namespaces' },
  { name: 'tenants', label: 'Tenants', section: 'Admin', detail: false, fullHeight: true, activeNav: 'tenants', navPath: 'tenants', navOrder: 1 },
  { name: 'tenant-detail', label: 'Tenant', section: 'Admin', detail: true, fullHeight: false, activeNav: 'tenants' },
  { name: 'tenant-new', label: 'Register tenant', section: 'Admin', detail: true, fullHeight: false, activeNav: 'tenants' },
  { name: 'not-found', label: 'Not found', section: null, detail: false, fullHeight: false, activeNav: null }
];

export const routeMetadata = Object.fromEntries(routeRegistry.map((route) => [route.name, route])) as Record<RouteName, RouteMetadata>;

export const navigationGroups = (['Operate', 'Configure', 'Admin'] as const).map((label) => ({
  label,
  routes: routeRegistry
    .filter((route) => route.section === label && route.navPath !== undefined)
    .sort((a, b) => (a.navOrder ?? 0) - (b.navOrder ?? 0))
}));

export function navigationHref(route: RouteMetadata, namespace?: string | null): string {
  return href(route.navPath ?? '', { ns: ns(namespace) });
}

type QueryValue = string | number | boolean | null | undefined;

function href(path: string, query: Record<string, QueryValue> = {}): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== '') params.set(key, String(value));
  }
  const encoded = params.toString();
  return '#/' + path + (encoded ? `?${encoded}` : '');
}

const ns = (namespace?: string | null) => namespace || undefined;

export const routes = {
  overview: (options: { namespace?: string | null } = {}) => href('', { ns: ns(options.namespace) }),
  jobs: (options: { namespace?: string | null; status?: string | null; jobName?: string | null; correlationKey?: string | null; tenantId?: number | string | null; pageSize?: number | string | null } = {}) =>
    href('jobs', { ns: ns(options.namespace), status: options.status, jobName: options.jobName, correlationKey: options.correlationKey, tenantId: options.tenantId, pageSize: options.pageSize }),
  job: (jobRef: string, options: { namespace?: string | null } = {}) => href(`jobs/${encodeURIComponent(jobRef)}`, { ns: ns(options.namespace) }),
  events: (options: { namespace?: string | null } = {}) => href('events', { ns: ns(options.namespace) }),
  workers: (options: { namespace?: string | null; status?: string | null } = {}) => href('workers', { ns: ns(options.namespace), status: options.status }),
  worker: (workerId: number, options: { namespace?: string | null } = {}) => href(`workers/${workerId}`, { ns: ns(options.namespace) }),
  alerts: (options: { namespace?: string | null } = {}) => href('alerts', { ns: ns(options.namespace) }),
  definitions: (options: { namespace?: string | null } = {}) => href('definitions', { ns: ns(options.namespace) }),
  definition: (definitionId: number | string, options: { namespace?: string | null } = {}) => href(`definitions/${encodeURIComponent(String(definitionId))}`, { ns: ns(options.namespace) }),
  schedules: (options: { namespace?: string | null } = {}) => href('schedules', { ns: ns(options.namespace) }),
  schedule: (namespace: string, jobName: string, scheduleName: string) => href(`schedules/${encodeURIComponent(namespace)}/${encodeURIComponent(jobName)}/${encodeURIComponent(scheduleName)}`, { ns: namespace }),
  namespaces: (options: { namespace?: string | null } = {}) => href('namespaces', { ns: ns(options.namespace) }),
  namespace: (name: string, options: { namespace?: string | null } = {}) => href(`namespaces/${encodeURIComponent(name)}`, { ns: ns(options.namespace) }),
  tenants: (options: { namespace?: string | null } = {}) => href('tenants', { ns: ns(options.namespace) }),
  tenant: (tenantKey: string, options: { namespace?: string | null } = {}) => href(`tenants/${encodeURIComponent(tenantKey)}`, { ns: ns(options.namespace) }),
  newTenant: (options: { namespace?: string | null } = {}) => href('tenants/new', { ns: ns(options.namespace) })
};
