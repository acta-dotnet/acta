import { writable } from 'svelte/store';
import { advanceNow } from './time.ts';
import type { JobDetail, JobExplanation, JobLineage, JobWorker } from './routes/job-detail/types.ts';

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
  // The version segment is Acta's, fixed against the mount: MapActa serves {mount}/api/v1/...
  const url = new URL('api/v1/' + options.path.replace(/^\/+/, ''), document.baseURI);
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
  schema: string;
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

export type ControlAction = 'applied' | 'notFound' | 'rejected';

// All seven job-control verbs (pause/resume/restart/cancel/reschedule/reprioritize/purge) return this
// shape at `jobs/{jobRef}/{action}`; JobControls.svelte drives them all through useControlMutation
// (api.ts's controlRequest, via useControlMutation.ts) rather than a per-verb fetch function.
export interface JobControlResponse {
  jobRef: string;
  action: ControlAction;
  status: string | null;
  message: string;
}

// Alert-control POST response (acknowledge/resolve, at alerts/{alertId}/{action}). AlertsList
// drives both through useControlMutation. Unlike JobControlResponse's action, this action is only
// ever 'applied' or 'notFound' - acknowledge/resolve are idempotent, so the backend never rejects.
export interface AlertControlResponse {
  alertId: number;
  action: ControlAction;
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
// #6: materially different from ControlAction. No message field (the frontend synthesizes its
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
  namespaceId: number;
  jobNamespace: string;
  status: string;
  ownerTeam: string | null;
  description: string | null;
  version: number;
}

export interface TenantRegistrationResponse {
  tenantId: number;
  tenantKey: string;
}

// Tenant register POST. Insert-or-return-existing: a new tenant is created active, an existing one
// is returned untouched (suspend/resume own status changes). Returns the assigned tenant id; a
// 400/404 (bad key, or controls disabled) throws with the problem detail.
export async function registerTenant(
  tenantKey: string,
  displayName?: string | null,
  description?: string | null
): Promise<TenantRegistrationResponse> {
  const { response, body } = await request<TenantRegistrationResponse>({
    path: 'tenants',
    method: 'POST',
    headers: controlHeaders(),
    body: {
      tenantKey: tenantKey.trim(),
      displayName: displayName?.trim() || null,
      description: description?.trim() || null
    }
  });

  if (response.ok && body && typeof body === 'object' && 'tenantId' in (body as object)) {
    return body;
  }

  throw new ApiError(response.status, 'Invalid response.', null, null);
}

export interface DefinitionControlResponse {
  definitionId: number;
  action: ControlAction;
  message: string;
}

// PATCH a definition's operator overrides. The body carries the version (optimistic concurrency), the
// full override set (null/absent field = clear), and a note. Applied (200), rejected/version-conflict
// (409), and not-found (404) all return a DefinitionControlResponse; anything else throws.
export async function setDefinitionOverrides(
  id: number,
  version: number,
  overrides: Record<string, unknown>,
  note?: string
): Promise<DefinitionControlResponse> {
  const { response, body } = await request<unknown>({
    path: `definitions/${id}`,
    method: 'PATCH',
    headers: controlHeaders(),
    body: { version, overrides, reasonMessage: note?.trim() || null },
    acceptedStatuses: [404, 409]
  });

  if (response.ok || response.status === 404 || response.status === 409) {
    if (body && typeof body === 'object' && 'action' in (body as object)) {
      return body as DefinitionControlResponse;
    }
    if (response.status === 404) {
      return {
        definitionId: id,
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
  action: ControlAction;
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
  return api<SchedulePreview>('schedules/preview', { jobNamespace, jobName, scheduleName, limit: count });
}

// Format-dispatched payload projection served by the input/result/checkpoint reads (part of the
// always-on read surface). The consumer reads whichever body field the `format` names: json -> parsed
// JSON, text -> decoded string, none -> no body field, any other format (bytes or a consumer-defined
// id) -> base64. A payload past the server's size cap ships no body field: `truncated` is true and
// `byteLength` carries its stored size. PayloadView.svelte dispatches on `formatName` and renders the match.
export interface JobPayloadView {
  formatName: string;
  formatId: number;
  json?: unknown;
  text?: string;
  base64?: string;
  byteLength?: number;
  truncated?: boolean;
}

// One schedule bound to a job's recurring slot, as it appears inside the aggregate detail (the same
// shape the /schedules list returns; JobSchedulesPanel reads this subset).
export interface JobScheduleView {
  jobScheduleId: number;
  jobNamespace: string;
  jobName: string;
  scheduleName: string;
  expressionKind: string;
  expression: string;
  timeZoneId: string;
  nextRunAtUtc: string | null;
  status: string;
  pausedUntilUtc: string | null;
  version: number;
}

// GET /jobs/{jobRef}/detail: the whole job screen in one aggregate so a lightweight job renders from a
// single request. Composed server-side after one job-id resolution; the input/result/checkpoint
// payloads are size-capped exactly like the standalone reads were. An absent result or empty
// schedule/worker set is a null/empty field. The unbounded event history keeps its own paged
// endpoint (JobEventsPanel), so it is not part of this shape.
export interface JobDetailView {
  job: JobDetail;
  input: JobPayloadView;
  result: JobPayloadView | null;
  checkpoints: JobCheckpoint[];
  explain: JobExplanation | null;
  lineage: JobLineage | null;
  schedules: JobScheduleView[];
  // Filter-wide counts: above the array length means this is the first page, not the whole set.
  schedulesTotal: number | null;
  tenantKey?: string;
  // Effective retry budget from the definition; absent when the definition row is gone.
  maxAttemptsEffective?: number;
  workers: JobWorker[] | null;
  workersTotal?: number;
}

// One checkpoint row (variable/signal/timer/progress/child-latch); kind and state are kebab code
// strings. `value` carries the format-dispatched payload shape when the checkpoint holds one.
export interface JobCheckpoint {
  kind: string;
  name: string;
  state?: string;
  dueAtUtc?: string;
  value?: JobPayloadView;
  createdAtUtc: string;
  modifiedAtUtc: string;
}

// GET /jobs/input-template: the compile-time shape of a job's input. `template` is raw JSON (the
// skeleton) and is null when the input is not json-formatted or this host has no descriptor for the
// job, in which case `inputTypeName` is null and `format` is 'none'.
export interface JobInputTemplate {
  jobNamespace: string;
  jobName: string;
  inputTypeName: string | null;
  inputFormatName: string;
  template: unknown;
}

// POST /jobs enqueue outcome: the assigned public ref and the coarse action ('inserted' for a fresh
// row, 'deduplicated' when a deduplicationKey matched an existing job).
export interface JobEnqueueResult {
  jobRef: string;
  action: 'inserted' | 'deduplicated';
}

export interface EnqueueJobRequest {
  jobNamespace: string;
  jobName: string;
  input?: unknown;
  text?: string;
  deduplicationKey?: string | null;
  correlationKey?: string | null;
  tenantKey?: string | null;
  priority?: string | null;
  delaySeconds?: number | null;
  nextRunAtUtc?: string | null;
}

// Enqueue a job through POST /jobs. A 201 returns the ref + action; validation (400), an enqueue
// rejection (409), and an over-size input (413) all throw an ApiError carrying the problem detail so
// the form can surface it inline. The input is format-faithful: `input` (raw JSON) or `text` (a text
// job, e.g. a text clone); only the field the caller sets travels, so an absent input stays absent.
export async function enqueueJob(spec: EnqueueJobRequest): Promise<JobEnqueueResult> {
  const { response, body } = await request<JobEnqueueResult>({
    path: 'jobs',
    method: 'POST',
    headers: controlHeaders(),
    body: {
      jobNamespace: spec.jobNamespace.trim(),
      jobName: spec.jobName.trim(),
      input: spec.input ?? undefined,
      text: spec.text ?? undefined,
      deduplicationKey: spec.deduplicationKey?.trim() || null,
      correlationKey: spec.correlationKey?.trim() || null,
      tenantKey: spec.tenantKey?.trim() || null,
      priority: spec.priority || null,
      delaySeconds: spec.delaySeconds ?? null,
      nextRunAtUtc: spec.nextRunAtUtc || null
    }
  });

  if (response.ok && body && typeof body === 'object' && 'jobRef' in (body as object)) {
    return body;
  }

  throw new ApiError(response.status, 'Invalid response.', null, null);
}
