import { writable } from 'svelte/store';
import { advanceNow } from './time.ts';

export const online = writable(true);

export class ApiError extends Error {
  readonly status: number;
  readonly title: string | null;
  readonly detail: string | null;
  readonly traceId: string | null;

  constructor(
    status: number,
    title: string | null,
    detail: string | null,
    traceId: string | null
  ) {
    super(detail ?? title ?? `HTTP ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.traceId = traceId;
  }
}

interface ProblemDetails {
  title?: unknown;
  detail?: unknown;
  traceId?: unknown;
}

interface RequestOptions {
  path: string;
  method?: 'GET' | 'POST' | 'PATCH' | 'DELETE';
  query?: Record<string, unknown>;
  body?: unknown;
  headers?: Record<string, string>;
  signal?: AbortSignal;
  acceptedStatuses?: readonly number[];
}

function isAbortError(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'name' in error && error.name === 'AbortError';
}

function problemValue(value: unknown, key: keyof ProblemDetails): string | null {
  if (!value || typeof value !== 'object') return null;
  const field = (value as ProblemDetails)[key];
  return typeof field === 'string' ? field : null;
}

interface ParsedJson {
  body: unknown | null;
  valid: boolean;
}

async function parseJson(response: Response): Promise<ParsedJson> {
  const text = await response.text();
  if (!text) return { body: null, valid: false };
  try {
    return { body: JSON.parse(text) as unknown, valid: true };
  } catch {
    return { body: null, valid: false };
  }
}

async function request<T>(options: RequestOptions): Promise<{ response: Response; body: T | null }> {
  const url = new URL('api/' + options.path.replace(/^\/+/, ''), document.baseURI);
  for (const [key, value] of Object.entries(options.query ?? {})) {
    if (Array.isArray(value)) {
      // Repeated query params (e.g. tag filters): one entry per non-blank member.
      for (const member of value) {
        if (member !== undefined && member !== null && member !== '') url.searchParams.append(key, String(member));
      }
    } else if (value !== undefined && value !== null && value !== '') {
      url.searchParams.set(key, String(value));
    }
  }

  const headers: Record<string, string> = { Accept: 'application/json', ...options.headers };
  const serializedBody = options.body === undefined ? undefined : JSON.stringify(options.body);
  if (serializedBody !== undefined && !Object.keys(headers).some((key) => key.toLowerCase() === 'content-type')) {
    headers['Content-Type'] = 'application/json';
  }

  let response: Response;
  try {
    response = await fetch(url, {
      method: options.method ?? 'GET',
      headers,
      body: serializedBody,
      signal: options.signal
    });
  } catch (error) {
    if (!isAbortError(error)) online.set(false);
    throw error;
  }

  online.set(true);
  advanceNow();
  const parsed = await parseJson(response);
  const accepted = response.ok || options.acceptedStatuses?.includes(response.status) === true;
  if (!accepted) {
    throw new ApiError(
      response.status,
      problemValue(parsed.body, 'title'),
      problemValue(parsed.body, 'detail'),
      problemValue(parsed.body, 'traceId')
    );
  }

  // Every successful dashboard endpoint has a JSON response contract. Accepted error statuses may
  // still use an empty or non-JSON proxy response so their caller can provide its typed fallback.
  if (response.ok && (!parsed.valid || parsed.body === null)) {
    throw new ApiError(response.status, 'Invalid response.', 'Expected a JSON response body.', null);
  }

  return { response, body: parsed.body as T | null };
}

export interface Paged<T> {
  items: T[];
  nextCursor: string | null;
  hasMore: boolean;
  pageSize: number;
  totalCount: number | null;
}

export async function api<T>(
  path: string,
  query: Record<string, unknown> = {},
  options: { signal?: AbortSignal } = {}
): Promise<T> {
  const { body } = await request<T>({ path, query, signal: options.signal });
  return body as T;
}

// Confirmation header the backend requires on every control POST/PATCH (value is always the literal
// string 'true'; see CapabilitiesResponse.ConfirmationHeader). Defaults to the backend's own default
// so a control request fired before capabilities loads still matches an unconfigured server;
// fetchCapabilities() below keeps this in sync with the live value, the same "module state kept in
// sync by a fetch" pattern the `online` store above uses.
let confirmationHeaderName = 'X-Acta-Control';

function controlHeaders(): Record<string, string> {
  return { [confirmationHeaderName]: 'true' };
}

export interface Capabilities {
  controlsEnabled: boolean;
  version: string;
  provider: string;
  confirmationHeader: string;
}

export async function fetchCapabilities(options: { signal?: AbortSignal } = {}): Promise<Capabilities> {
  const result = await api<Capabilities>('capabilities', {}, options);
  confirmationHeaderName = result.confirmationHeader;
  return result;
}

export async function controlRequest<TResult extends { action: string }>(
  path: string,
  body: unknown,
  notFound: TResult,
  method: 'POST' | 'PATCH' | 'DELETE' = 'POST',
  versionConflict?: TResult
): Promise<TResult> {
  const { response, body: parsed } = await request<unknown>({
    path,
    method,
    body,
    headers: controlHeaders(),
    acceptedStatuses: [404, 409]
  });

  if (response.ok || response.status === 404 || response.status === 409) {
    if (parsed && typeof parsed === 'object' && 'action' in (parsed as object)) {
      return parsed as TResult;
    }
    if (response.status === 404) return notFound;
    if (response.status === 409 && versionConflict) return versionConflict;
  }

  throw new ApiError(
    response.status,
    problemValue(parsed, 'title'),
    problemValue(parsed, 'detail'),
    problemValue(parsed, 'traceId')
  );
}

export type JobControlAction = 'applied' | 'notFound' | 'rejected';

// All seven job-control verbs (pause/resume/restart/cancel/reschedule/reprioritize/purge) return this
// shape at `jobs/{jobRef}/{action}`; JobControls.svelte drives them all through useControlMutation
// (api.ts's controlRequest, via useControlMutation.ts) rather than a per-verb fetch function.
export interface JobControlResponse {
  jobRef: string;
  action: JobControlAction;
  status: string | null;
  message: string;
}

// Alert-control POST response (acknowledge/resolve, at alerts/{alertId}/{action}). AlertsList
// drives both through useControlMutation. Unlike JobControlResponse's action, this action is only
// ever 'applied' or 'notFound' - acknowledge/resolve are idempotent, so the backend never rejects.
export interface AlertControlResponse {
  alertId: number;
  action: JobControlAction;
  acknowledgedAtUtc: string | null;
  resolvedAtUtc: string | null;
}

export interface TenantListItem {
  tenantId: number;
  tenantKey: string;
  displayName: string | null;
  description: string | null;
  status: string;
  createdAtUtc: string;
  modifiedAtUtc: string;
  version: number;
}

// Admin controls use a distinct action set from job controls and can return optimistic conflicts.
// #6: materially different from JobControlAction. No message field (the frontend synthesizes its
// own operator-facing text from `action`); `alreadyInState` is a successful idempotent no-op, not
// a rejection. NotFound/VersionConflict come back from the server as a bare Problem body with no
// `action` field at all - useControlMutation's notFound/versionConflict fallbacks supply those two
// cases (see controlRequest above), never a parsed server value.
export type AdminControlAction = 'applied' | 'notFound' | 'alreadyInState' | 'versionConflict';

export interface AdminControlResult {
  action: AdminControlAction;
  version: number | null;
}

export interface NamespaceListItem {
  id: number;
  name: string;
  status: string;
  ownerTeam: string | null;
  description: string | null;
  version: number;
}

export interface TenantRegistrationResponse {
  tenantId: number;
  tenantKey: string;
  status: string;
}

// Tenant register/suspend POST. Register and suspend are the same idempotent upsert; pass
// status 'suspended' to suspend. Returns the assigned tenant id; a 400/404 (bad key, or controls
// disabled) throws with the problem detail.
export async function registerTenant(
  tenantKey: string,
  displayName?: string | null,
  description?: string | null,
  status?: 'active' | 'suspended'
): Promise<TenantRegistrationResponse> {
  const { response, body } = await request<TenantRegistrationResponse>({
    path: 'tenants',
    method: 'POST',
    headers: controlHeaders(),
    body: {
      tenantKey: tenantKey.trim(),
      displayName: displayName?.trim() || null,
      description: description?.trim() || null,
      status: status ?? null
    }
  });

  if (response.ok && body && typeof body === 'object' && 'tenantId' in (body as object)) {
    return body;
  }

  throw new ApiError(response.status, 'Invalid response.', null, null);
}

export interface DefinitionOverrideResponse {
  jobDefinitionId: number;
  action: JobControlAction;
  message: string;
}

// PATCH a definition's operator overrides. The body carries the version (optimistic concurrency), the
// full override set (null/absent field = clear), and a note. Applied (200), rejected/version-conflict
// (409), and not-found (404) all return a DefinitionOverrideResponse; anything else throws.
export async function setDefinitionOverrides(
  id: number,
  version: number,
  overrides: Record<string, unknown>,
  note?: string
): Promise<DefinitionOverrideResponse> {
  const { response, body } = await request<unknown>({
    path: `definitions/${id}`,
    method: 'PATCH',
    headers: controlHeaders(),
    body: { version, overrides, note: note?.trim() || null },
    acceptedStatuses: [404, 409]
  });

  if (response.ok || response.status === 404 || response.status === 409) {
    if (body && typeof body === 'object' && 'action' in (body as object)) {
      return body as DefinitionOverrideResponse;
    }
    if (response.status === 404) {
      return {
        jobDefinitionId: id,
        action: 'notFound',
        message: problemValue(body, 'detail') ?? problemValue(body, 'title') ?? 'Definition not found.'
      };
    }
  }

  throw new ApiError(
    response.status,
    problemValue(body, 'title'),
    problemValue(body, 'detail'),
    problemValue(body, 'traceId')
  );
}

export interface ScheduleControlResponse {
  action: JobControlAction;
  status: string | null;
  pausedUntilUtc: string | null;
  nextRunAtUtc: string | null;
  version: number | null;
  message: string;
}

// Schedule pause/resume/trigger/overrides all live at schedules/{action}, addressed by natural key
// in the body, and return ScheduleControlResponse - ScheduleControls.svelte drives all four through
// useControlMutation (controlRequest, above) rather than a per-verb fetch function.

// Read-only forecast of a schedule's upcoming fire instants (effective expression/timezone plus the
// next N run instants); not a control endpoint, so no confirmation header and always available.
export interface SchedulePreview {
  expression: string;
  timeZoneId: string;
  nextRunsUtc: string[];
}

export async function previewSchedule(
  jobNamespace: string,
  jobName: string,
  scheduleName: string,
  count = 10
): Promise<SchedulePreview> {
  return api<SchedulePreview>('schedules/preview', { jobNamespace, jobName, scheduleName, count });
}
