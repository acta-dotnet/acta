import { writable } from 'svelte/store';
import { advanceNow } from './time.ts';
import { stringifyBody, type RawJson } from './lib/rawJson.ts';
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
  const serializedBody = options.body === undefined ? undefined : stringifyBody(options.body);
  if (serializedBody !== undefined) headers['Content-Type'] = 'application/json';

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
  totalCount: number | null;
}

// One ledger event, as every events read returns it (the /events feed, a job's timeline, a definition's
// trail). The subject is the kebab `entityKind` plus its numeric `entityId`, the one id the wire carries;
// `jobRef` is set while a job subject's row exists. `message` is human text only, and `detailsText` is the
// details payload decoded to text, usually a small JSON object of text facts: read it with eventFacts.
export interface EventItem {
  eventId: number;
  eventCode: string;
  createdAtUtc: string;
  jobNamespace: string;
  entityKind: string;
  entityId: number;
  jobName: string | null;
  jobRef: string | null;
  lineageRootJobRef: string | null;
  tenantName: string | null;
  workerRef: string | null;
  executionNumber: number | null;
  durationMs: number | null;
  message: string | null;
  detailsText: string | null;
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
// string 'true'; see GetCapabilitiesResponse.ConfirmationHeader). Defaults to the backend's own default
// so a control request fired before capabilities loads still matches an unconfigured server;
// fetchCapabilities() below keeps this in sync with the live value, the same "module state kept in
// sync by a fetch" pattern the `online` store above uses.
let confirmationHeaderName = 'X-Acta-Control';

function controlHeaders(): Record<string, string> {
  return { [confirmationHeaderName]: 'true' };
}

export interface Capabilities {
  controlsEnabled: boolean;
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

  // request() has already thrown on every status but a success, 404, and 409.
  if (parsed && typeof parsed === 'object' && 'action' in (parsed as object)) {
    return parsed as TResult;
  }
  if (response.status === 404) return notFound;
  if (response.status === 409 && versionConflict) return versionConflict;

  throw new ApiError(
    response.status,
    problemValue(parsed, 'title'),
    problemValue(parsed, 'detail'),
    problemValue(parsed, 'traceId')
  );
}

export type ControlAction = 'applied' | 'notFound' | 'rejected' | 'versionConflict';

// All seven job-control verbs (pause/resume/restart/cancel/reschedule/reprioritize/purge) return this
// shape at `jobs/{jobRef}/{action}`; JobControls.svelte drives them all through useControlMutation
// (api.ts's controlRequest, via useControlMutation.ts) rather than a per-verb fetch function. The
// dashboard sends no expectedVersion, so 'versionConflict' only reaches other API callers.
export interface JobControlResponse {
  action: ControlAction;
  status: string | null;
}

// Alert-control POST response (acknowledge/resolve, at alerts/{alertRef}/{action}). AlertsList and
// AlertDetail drive both through useControlMutation. Unlike JobControlResponse's action, this action
// is only ever 'applied' or 'notFound' - acknowledge/resolve are idempotent, so the backend never rejects.
export interface AlertControlResponse {
  action: ControlAction;
}

// GET /alerts/{alertRef}: the single-alert read behind the alert detail screen. Same fields the list
// row carries; the numeric alert id never reaches the wire.
export interface AlertDetailView {
  alertRef: string;
  jobNamespace: string;
  jobRef: string | null;
  origin: string;
  severity: string;
  kind: string;
  title: string;
  message: string;
  channelName: string;
  occurrenceCount: number;
  resolvedAtUtc: string | null;
  deliveryStatus: string;
  retryCount: number;
  retryAfterUtc: string | null;
  createdAtUtc: string;
  modifiedAtUtc: string;
  acknowledgedAtUtc: string | null;
}

export interface TenantListItem {
  tenantName: string;
  displayName: string | null;
  description: string | null;
  status: string;
  createdAtUtc: string;
  modifiedAtUtc: string;
  version: number;
}

// Admin controls use a distinct action set from job controls and can return optimistic conflicts.
// No message field (the frontend synthesizes its
// own operator-facing text from `action`); `alreadyInState` is a successful idempotent no-op, not
// a rejection. All four come back from the server as this body: applied and alreadyInState at 200,
// notFound at 404, versionConflict at 409 carrying the row's current `version`, so a retry needs no
// re-read. useControlMutation's notFound/versionConflict fallbacks only cover a body that arrives
// without an `action` at all.
export type AdminControlAction = 'applied' | 'notFound' | 'alreadyInState' | 'versionConflict';

export interface AdminControlResult {
  action: AdminControlAction;
  version: number | null;
}

// A tag write answers applied at 200 or notFound at 404, with no version: a tag set is not a versioned record.
export interface TagMutationResponse {
  action: 'applied' | 'notFound';
}

export interface NamespaceListItem {
  jobNamespace: string;
  status: string;
  ownerTeam: string | null;
  description: string | null;
  version: number;
}

export interface RegisterTenantResponse {
  tenantName: string;
}

// Tenant register POST. Insert-or-return-existing: a new tenant is created active, an existing one
// is returned untouched (suspend/resume own status changes). Returns the canonical tenant name; a
// 400/404 (bad name, or controls disabled) throws with the problem detail.
export async function registerTenant(
  tenantName: string,
  displayName?: string | null,
  description?: string | null
): Promise<RegisterTenantResponse> {
  const { response, body } = await request<RegisterTenantResponse>({
    path: 'tenants',
    method: 'POST',
    headers: controlHeaders(),
    body: {
      tenantName: tenantName.trim(),
      displayName: displayName?.trim() || null,
      description: description?.trim() || null
    }
  });

  if (response.ok && body && typeof body === 'object' && 'tenantName' in (body as object)) {
    return body;
  }

  throw new ApiError(response.status, 'Invalid response.', null, null);
}

export interface DefinitionControlResponse {
  action: ControlAction;
}

// PATCH a definition's operator overrides, addressed by its natural key. The body carries the
// expectedVersion CAS token, the full override set (null/absent field = clear), and a note. Applied
// (200), versionConflict (409), and not-found (404) all return a DefinitionControlResponse;
// anything else throws.
export async function setDefinitionOverrides(
  jobNamespace: string,
  jobName: string,
  expectedVersion: number,
  overrides: Record<string, unknown>,
  note?: string
): Promise<DefinitionControlResponse> {
  return controlRequest<DefinitionControlResponse>(
    `definitions/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(jobName)}`,
    { expectedVersion, overrides, reasonMessage: note?.trim() || null },
    { action: 'notFound' },
    'PATCH'
  );
}

// POST a definition retire, addressed by its natural key. The body carries the expectedVersion CAS
// token and a note; the verb cancels the definition's parked jobs server-side. Applied (200),
// versionConflict (409), and not-found (404) all return a DefinitionControlResponse.
export async function retireDefinition(
  jobNamespace: string,
  jobName: string,
  expectedVersion: number,
  note?: string
): Promise<DefinitionControlResponse> {
  return controlRequest<DefinitionControlResponse>(
    `definitions/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(jobName)}/retire`,
    { expectedVersion, reasonMessage: note?.trim() || null },
    { action: 'notFound' }
  );
}

export interface ScheduleControlResponse {
  action: ControlAction;
}

// Schedule pause/resume/trigger/overrides live at schedules/{ns}/{job}/{schedule}/{action} and return
// ScheduleControlResponse; ScheduleControls.svelte drives all four through useControlMutation
// (controlRequest, above) rather than a per-verb fetch function.

// Read-only forecast of a schedule's upcoming fire instants (effective expression/timezone plus the
// next N occurrence instants); not a control endpoint, so no confirmation header and always available.
export interface SchedulePreview {
  expression: string;
  timeZoneId: string;
  nextOccurrencesUtc: string[];
}

export async function previewSchedule(
  jobNamespace: string,
  jobName: string,
  scheduleName: string
): Promise<SchedulePreview> {
  return api<SchedulePreview>(`schedules/${jobNamespace}/${jobName}/${scheduleName}/preview`, { limit: 10 });
}

// Format-dispatched payload projection served by the input/result/checkpoint reads (part of the
// always-on read surface). The consumer reads whichever body field the `format` names: json -> the stored
// JSON text (never parsed, see lib/rawJson.ts), none -> no body field, any other format (bytes or a
// consumer-defined id) -> base64. A stored json row that is not strict JSON ships as base64 with
// `malformed` set. A payload past the server's size cap ships no body field: `truncated` is true and
// `byteLength` carries its stored size. PayloadView.svelte dispatches on `formatName` and renders the match.
export interface JobPayloadView {
  formatName: string;
  formatId: number;
  json?: string;
  base64?: string;
  byteLength?: number;
  truncated?: boolean;
  malformed?: boolean;
}

// One schedule bound to a job's recurring slot, as it appears inside the aggregate detail (the same
// shape the /schedules list returns; JobSchedulesPanel reads this subset).
export interface JobScheduleView {
  jobNamespace: string;
  jobName: string;
  scheduleName: string;
  expressionKind: string;
  expression: string;
  timeZoneId: string;
  nextOccurrenceAtUtc: string | null;
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
  status?: string;
  dueAtUtc?: string;
  value?: JobPayloadView;
}

// GET /jobs/input-template: the compile-time shape of a job's input. `templateJson` is the skeleton as
// JSON text, null when the input is not json-formatted. A job this host has no descriptor for is a
// 404, which inputTemplate() below answers as null.
export interface JobInputTemplate {
  inputTypeName: string;
  inputFormatName: string;
  templateJson: string | null;
}

// The input template, or null when this host does not know the job: a dashboard can point at a shared
// ledger it has no job assembly for, and the form then keeps its empty editor.
export async function inputTemplate(
  jobNamespace: string,
  jobName: string,
  options: { signal?: AbortSignal } = {}
): Promise<JobInputTemplate | null> {
  try {
    return await api<JobInputTemplate>('jobs/input-template', { jobNamespace, jobName }, options);
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
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
  input?: RawJson;
  deduplicationKey?: string | null;
  correlationKey?: string | null;
  tenantName?: string | null;
  priority?: string | null;
  delaySeconds?: number | null;
}

// Enqueue a job through POST /jobs. A 201 returns the ref + action; validation (400), an enqueue
// rejection (409), and an over-size input (413) all throw an ApiError carrying the problem detail so
// the form can surface it inline. `input` is the operator's JSON text, sent verbatim; an absent input
// stays absent.
export async function enqueueJob(spec: EnqueueJobRequest): Promise<JobEnqueueResult> {
  const { response, body } = await request<JobEnqueueResult>({
    path: 'jobs',
    method: 'POST',
    headers: controlHeaders(),
    body: {
      jobNamespace: spec.jobNamespace.trim(),
      jobName: spec.jobName.trim(),
      input: spec.input ?? undefined,
      deduplicationKey: spec.deduplicationKey?.trim() || null,
      correlationKey: spec.correlationKey?.trim() || null,
      tenantName: spec.tenantName?.trim() || null,
      priority: spec.priority || null,
      delaySeconds: spec.delaySeconds ?? null
    }
  });

  if (response.ok && body && typeof body === 'object' && 'jobRef' in (body as object)) {
    return body;
  }

  throw new ApiError(response.status, 'Invalid response.', null, null);
}
