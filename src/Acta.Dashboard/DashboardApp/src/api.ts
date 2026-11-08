import { writable } from 'svelte/store';
import { advanceNow } from './time.ts';
import { quotePayloadJson, RawJson, stringifyBody } from './lib/rawJson.ts';
import type { JobDetail, JobExplanation, JobLineage, JobWorker } from './routes/job-detail/types.ts';

export const online = writable(true);

// Counts the 403s of calls other than the capabilities read, so the caller's permissions are read again after one; the
// capabilities read's own 403 never counts, so a revoked read cannot loop.
export const permissionRefused = writable(0);

// Set by the first 401: the host's sign-in no longer covers this page. From then every call fails here, without a
// request, until the page reloads and the host's scheme challenges it.
export const sessionExpired = writable(false);
let expired = false;

export function resetSessionForTests(): void {
  expired = false;
  sessionExpired.set(false);
}

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
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
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

// A payload envelope's json member arrives as its exact text (lib/rawJson.ts), never as a rebuilt value.
async function parseJson(response: Response): Promise<ParsedJson> {
  const text = await response.text();
  if (!text) return { body: null, valid: false };
  try {
    return { body: JSON.parse(quotePayloadJson(text)) as unknown, valid: true };
  } catch {
    return { body: null, valid: false };
  }
}

function apiUrl(path: string, query: Record<string, unknown> = {}): URL {
  // The version segment is Acta's, fixed against the mount: MapActa serves {mount}/api/v1/...
  const url = new URL('api/v1/' + path.replace(/^\/+/, ''), document.baseURI);
  for (const [key, value] of Object.entries(query)) {
    if (Array.isArray(value)) {
      // Repeated query params (e.g. tag filters): one entry per non-blank member.
      for (const member of value) {
        if (member !== undefined && member !== null && member !== '') url.searchParams.append(key, String(member));
      }
    } else if (value !== undefined && value !== null && value !== '') {
      url.searchParams.set(key, String(value));
    }
  }
  return url;
}

function sessionError(): ApiError {
  return new ApiError(401, 'Your session has ended.', 'Reload to sign in again.', null);
}

// A network failure marks the dashboard offline; any answer marks it online again. A 401 ends the session.
async function send(url: URL, init: RequestInit): Promise<Response> {
  if (expired) throw sessionError();
  let response: Response;
  try {
    response = await fetch(url, init);
  } catch (error) {
    if (!isAbortError(error)) online.set(false);
    throw error;
  }

  online.set(true);
  advanceNow();
  if (response.status === 401) {
    expired = true;
    sessionExpired.set(true);
  }
  return response;
}

async function request<T>(options: RequestOptions): Promise<{ response: Response; body: T | null }> {
  const headers: Record<string, string> = { Accept: 'application/json', ...options.headers };
  const serializedBody = options.body === undefined ? undefined : stringifyBody(options.body);
  if (serializedBody !== undefined) headers['Content-Type'] = 'application/json';

  const response = await send(apiUrl(options.path, options.query), {
    method: options.method ?? 'GET',
    headers,
    body: serializedBody,
    signal: options.signal
  });
  const parsed = await parseJson(response);
  const accepted = response.ok || options.acceptedStatuses?.includes(response.status) === true;
  if (response.status === 401) throw sessionError();
  if (response.status === 403 && options.path !== 'capabilities') permissionRefused.update((n) => n + 1);
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

// Confirmation header the dashboard sends on every control, which confirms it (value is always the literal
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
  // The permissions this caller holds: acta.read, acta.enqueue, acta.control, acta.admin.
  permissions: string[];
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
  method: 'POST' | 'PUT' | 'DELETE' = 'POST',
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

export type ControlAction = 'applied' | 'not-found' | 'rejected' | 'accepted' | 'version-conflict';

// All seven job-control verbs (pause/resume/restart/cancel/reschedule/reprioritize/purge) return this
// shape at `jobs/{jobRef}/{action}`; JobControls.svelte drives them all through useControlMutation
// (api.ts's controlRequest, via useControlMutation.ts) rather than a per-verb fetch function. The
// dashboard sends no expectedVersion, so 'version-conflict' only reaches other API callers.
export interface JobControlResponse {
  action: ControlAction;
  status: string | null;
}

// Alert-control POST response (acknowledge/resolve, at alerts/{alertRef}/{action}). AlertsList and
// AlertDetail drive both through useControlMutation. Unlike JobControlResponse's action, this action
// is only ever 'applied' or 'not-found' - acknowledge/resolve are idempotent, so the backend never rejects.
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
// own operator-facing text from `action`); `already-in-state` is a successful idempotent no-op, not
// a rejection. All four come back from the server as this body: applied and already-in-state at 200,
// not-found at 404, version-conflict at 409 carrying the row's current `version`, so a retry needs no
// re-read. useControlMutation's notFound/versionConflict fallbacks only cover a body that arrives
// without an `action` at all.
export type AdminControlAction = 'applied' | 'not-found' | 'already-in-state' | 'version-conflict';

export interface AdminControlResult {
  action: AdminControlAction;
  version: number | null;
}

// A tag write answers applied at 200 or not-found at 404, with no version: a tag set is not a versioned record.
export interface TagMutationResponse {
  action: 'applied' | 'not-found';
}

export interface NamespaceListItem {
  jobNamespace: string;
  status: string;
  ownerTeam: string | null;
  description: string | null;
  // The app version a deploy step last recorded in the namespace; null while only a development start has written it.
  deploymentVersion: string | null;
  version: number;
}

// GET /namespaces/{jobNamespace}: one namespace, the fields its list row carries, or null when the
// namespace does not exist (the route's 404).
export async function getNamespace(
  jobNamespace: string,
  options: { signal?: AbortSignal } = {}
): Promise<NamespaceListItem | null> {
  try {
    return await api<NamespaceListItem>(`namespaces/${encodeURIComponent(jobNamespace)}`, {}, options);
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
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

// A definition's operator overrides, as GET /definitions/{ns}/{name} answers them in `overrides` and the
// PUT below takes them: every member present, null where there is no override.
export interface DefinitionPolicyOverrides {
  priority: string | null;
  maxAttempts: number | null;
  concurrencyLimit: number | null;
  rateLimit: string | null;
  backoff: string | null;
  executionTimeoutSeconds: number | null;
  deadlineSeconds: number | null;
  deadlineBehavior: string | null;
  jobRetentionSeconds: number | null;
  eventRecording: string | null;
  alertProfile: string | null;
  alertChannelName: string | null;
  runbookUrl: string | null;
  displayName: string | null;
  description: string | null;
}

// PUT a definition's operator overrides, addressed by its natural key. The body carries the
// expectedVersion CAS token, the full override set that replaces the current one (null = clear; the
// server refuses a member left out with a 400), and a note. Applied (200), version-conflict (409), and
// not-found (404) all return a DefinitionControlResponse; anything else throws.
export async function updateDefinitionOverrides(
  jobNamespace: string,
  jobName: string,
  expectedVersion: number,
  overrides: DefinitionPolicyOverrides,
  note?: string
): Promise<DefinitionControlResponse> {
  return controlRequest<DefinitionControlResponse>(
    `definitions/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(jobName)}/overrides`,
    { expectedVersion, overrides, reasonMessage: note?.trim() || null },
    { action: 'not-found' },
    'PUT'
  );
}

// POST a definition retire, addressed by its natural key. The body carries the expectedVersion CAS
// token and a note; the verb cancels the definition's parked jobs server-side. Applied (200),
// version-conflict (409), and not-found (404) all return a DefinitionControlResponse.
export async function retireDefinition(
  jobNamespace: string,
  jobName: string,
  expectedVersion: number,
  note?: string
): Promise<DefinitionControlResponse> {
  return controlRequest<DefinitionControlResponse>(
    `definitions/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(jobName)}/retire`,
    { expectedVersion, reasonMessage: note?.trim() || null },
    { action: 'not-found' }
  );
}

export interface ScheduleControlResponse {
  action: ControlAction;
}

// Schedule pause/resume/trigger live at schedules/{ns}/{job}/{schedule}/{action} and return
// ScheduleControlResponse; ScheduleControls.svelte drives all three through useControlMutation
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

// One calendar family in the GET /calendars page: the Published version (current) and the highest one
// (latest), the shared families of `sys` listed beside a namespace's own. `dependentCount` counts the
// live schedules of active definitions that bind the family, the ones that refuse an archive.
export interface CalendarListItem {
  jobNamespace: string;
  calendarName: string;
  role: string;
  currentVersion: number | null;
  latestVersion: number;
  latestStatus: string;
  publishedAtUtc: string | null;
  modifiedAtUtc: string;
  dependentCount: number;
}

export interface CalendarVersionView {
  version: number;
  status: string;
  contentHash: string;
  createdAtUtc: string;
  publishedAtUtc: string | null;
  modifiedAtUtc: string;
}

// A schedule bound to the family by name, as GET /calendars/{ns}/{name}/dependents lists it: how it binds the
// family (occurrence source or availability filter), its status, and its delivery. A binding resolves
// afresh at every evaluation, so there is no pinned version to show.
export interface CalendarDependent {
  jobNamespace: string;
  jobName: string;
  scheduleName: string;
  binding: string;
  status: string;
  scheduledDelivery: string;
}

// GET /calendars/{ns}/{name}?version=: the family's head: its current and latest versions, and one version's
// status and definition, the stored JSON object (the requested one, else the current, else the latest).
export interface CalendarHeadView {
  jobNamespace: string;
  calendarName: string;
  role: string;
  currentVersion: number | null;
  latestVersion: number;
  shownVersion: number;
  shownStatus: string;
  definition: Record<string, unknown>;
}

// GET /calendars/{ns}/{name}/detail?version=: the head with the first page of its versions (newest first) and
// of the schedules naming the family; each page's totalCount says whether it is the whole set.
export interface CalendarDetailView {
  calendar: CalendarHeadView;
  versions: Paged<CalendarVersionView>;
  dependents: Paged<CalendarDependent>;
}

export interface CalendarWindowView {
  startUtc: string;
  endUtc: string | null;
}

export interface CalendarPreviewSchedule {
  jobNamespace: string;
  jobName: string;
  scheduleName: string;
  binding: string;
  status: string;
  overdue: boolean;
}

// GET /calendars/{ns}/{name}/preview?version=: one version's forecast beside the current version's over
// the same horizon (instants for an occurrence calendar, windows for an availability one), and the bound
// schedules with the overdue flag a Refuse publication rejects on. A read, so always available.
export interface CalendarPreview {
  role: string;
  version: number;
  currentVersion: number | null;
  occurrencesUtc: string[];
  currentOccurrencesUtc: string[];
  windows: CalendarWindowView[];
  currentWindows: CalendarWindowView[];
  // Overdue ones first, capped; the totals count them all.
  schedules: CalendarPreviewSchedule[];
  schedulesTotal: number;
  overdueTotal: number;
}

export async function previewCalendar(
  jobNamespace: string,
  calendarName: string,
  version: number,
  options: { limit?: number; timeZoneId?: string; signal?: AbortSignal } = {}
): Promise<CalendarPreview> {
  return api<CalendarPreview>(
    `calendars/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(calendarName)}/preview`,
    { version, limit: options.limit ?? 10, timeZoneId: options.timeZoneId },
    { signal: options.signal }
  );
}

// POST calendars/{ns}/{name}/drafts: applied (200) with the new version and its content hash, already-in-state
// (200) with the latest version when it holds the same definition, rejected (409) with the compiler's problems,
// version-conflict (409) when the latest version moved. The definition is the operator's JSON text, spliced
// into the body verbatim as the `definition` object.
export interface CalendarDraftResponse {
  action: ControlAction | 'already-in-state';
  version: number | null;
  contentHash: string | null;
  problems: string[];
}

export async function saveCalendarDraft(
  jobNamespace: string,
  calendarName: string,
  role: string,
  definition: string,
  expectedLatestVersion: number,
  note?: string
): Promise<CalendarDraftResponse> {
  return controlRequest<CalendarDraftResponse>(
    `calendars/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(calendarName)}/drafts`,
    { role, definition: new RawJson(definition), expectedLatestVersion, reasonMessage: note?.trim() || null },
    { action: 'not-found', version: null, contentHash: null, problems: [] },
    'POST',
    { action: 'version-conflict', version: null, contentHash: null, problems: [] }
  );
}

// POST calendars/{ns}/{name}/publish: applied with the schedules retimed, already-in-state when the candidate
// is the current version, not-found when the candidate is no version of the family, version-conflict when the
// current version moved, rejected with the reason and the schedules holding overdue occurrences (under the
// refuse decision).
export interface CalendarPublishResponse {
  action: ControlAction | 'already-in-state';
  version: number | null;
  retimedSchedules: string[];
  overdueSchedules: string[];
  reason: string | null;
}

export async function publishCalendar(
  jobNamespace: string,
  calendarName: string,
  expectedCurrentVersion: number,
  candidateVersion: number,
  overdueDecision: 'refuse' | 'discard',
  note?: string
): Promise<CalendarPublishResponse> {
  return controlRequest<CalendarPublishResponse>(
    `calendars/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(calendarName)}/publish`,
    { expectedCurrentVersion, candidateVersion, overdueDecision, reasonMessage: note?.trim() || null },
    { action: 'not-found', version: null, retimedSchedules: [], overdueSchedules: [], reason: null },
    'POST',
    { action: 'version-conflict', version: null, retimedSchedules: [], overdueSchedules: [], reason: null }
  );
}

// POST calendars/{ns}/{name}/archive: applied with the version archived, rejected with how many live
// schedules still bind the family, not-found when it has no current version.
export interface CalendarArchiveResponse {
  action: ControlAction;
  version: number | null;
  dependentCount: number;
}

export async function archiveCalendar(
  jobNamespace: string,
  calendarName: string,
  expectedCurrentVersion: number,
  note?: string
): Promise<CalendarArchiveResponse> {
  return controlRequest<CalendarArchiveResponse>(
    `calendars/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(calendarName)}/archive`,
    { expectedCurrentVersion, reasonMessage: note?.trim() || null },
    { action: 'not-found', version: null, dependentCount: 0 },
    'POST',
    { action: 'version-conflict', version: null, dependentCount: 0 }
  );
}

// The payload envelope the detail and checkpoint reads carry; no envelope is no payload. A json payload's
// `json` is the embedded value's exact text, as parseJson keeps it (lib/rawJson.ts); any other format (bytes
// or a consumer-defined id) is base64. A stored json row that is not strict JSON ships as base64 with
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
// payloads are size-capped. An absent input or result, or an empty schedule/worker set, is a missing,
// null, or empty field. The unbounded event history keeps its own paged endpoint (JobEventsPanel), so
// it is not part of this shape.
export interface JobDetailView {
  job: JobDetail;
  input?: JobPayloadView | null;
  result?: JobPayloadView | null;
  // The first page; GET /jobs/{ref}/checkpoints continues it from nextCursor.
  checkpoints: Paged<JobCheckpoint>;
  explain: JobExplanation | null;
  lineage: JobLineage | null;
  schedules: JobScheduleView[];
  // Filter-wide counts: above the array length means this is the first page, not the whole set.
  schedulesTotal: number | null;
  // Effective retry budget from the definition; absent when the definition row is gone.
  maxAttemptsEffective?: number;
  // The type the definition declares the job returns; null when it declares none.
  resultTypeName: string | null;
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

// GET /definitions/{ns}/{name}/input-template: the compile-time shape of a job's input. `templateJson`
// is the skeleton as JSON text, null when the input is not json-formatted. A job this host has no
// descriptor for is a 404, which getDefinitionInputTemplate() below answers as null.
export interface DefinitionInputTemplate {
  inputTypeName: string;
  inputFormatName: string;
  templateJson: string | null;
}

// The input template, or null when this host does not know the job: a dashboard can point at a shared
// ledger it has no job assembly for, and the form then keeps its empty editor.
export async function getDefinitionInputTemplate(
  jobNamespace: string,
  jobName: string,
  options: { signal?: AbortSignal } = {}
): Promise<DefinitionInputTemplate | null> {
  try {
    return await api<DefinitionInputTemplate>(
      `definitions/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(jobName)}/input-template`,
      {},
      options
    );
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}

// GET /jobs/{jobRef}/input answers the stored input itself: the JSON text of a json input, read as text so every
// digit is kept. Null when the job has no input, is gone, or stores another format, which travels as raw bytes.
export async function getJobInputJson(jobRef: string, options: { signal?: AbortSignal } = {}): Promise<string | null> {
  const response = await send(apiUrl(`jobs/${encodeURIComponent(jobRef)}/input`), { signal: options.signal });
  if (response.status === 204 || response.status === 404) return null;
  if (response.status === 401) throw sessionError();
  if (!response.ok) {
    const problem = (await parseJson(response)).body;
    throw new ApiError(
      response.status,
      problemValue(problem, 'title'),
      problemValue(problem, 'detail'),
      problemValue(problem, 'traceId')
    );
  }
  return response.headers.get('Content-Type')?.startsWith('application/json') ? await response.text() : null;
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
  lane?: string | null;
  concurrencyKey?: string | null;
  // Left out of the request when empty, so a job enqueued without tags carries none, as before.
  tags?: EnqueueTag[];
}

export interface EnqueueTag {
  name: string;
  value: string | null;
}

// Enqueue a job through POST /jobs. A 201 returns the ref + action; validation (400), an enqueue
// rejection (409), and an over-size input (413) all throw an ApiError carrying the problem detail so
// the form can surface it inline. `input` is the payload envelope around the operator's JSON text, sent
// verbatim (jsonPayload in lib/rawJson.ts); an absent input stays absent.
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
      delaySeconds: spec.delaySeconds ?? null,
      lane: spec.lane?.trim() || null,
      concurrencyKey: spec.concurrencyKey?.trim() || null,
      tags: spec.tags && spec.tags.length > 0 ? spec.tags : undefined
    }
  });

  if (response.ok && body && typeof body === 'object' && 'jobRef' in (body as object)) {
    return body;
  }

  throw new ApiError(response.status, 'Invalid response.', null, null);
}
